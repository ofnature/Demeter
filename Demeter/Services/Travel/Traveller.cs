using System;
using System.Numerics;

namespace Demeter.Services.Travel;

public enum TravelStatus
{
    /// <summary>Nothing begun, or cancelled.</summary>
    Idle,
    Running,

    /// <summary>Standing at the point — on the ground, when the leg asked to land.</summary>
    Done,

    /// <summary>Cannot get there; <see cref="ITravel.FailReason"/> says why.</summary>
    Failed,
}

/// <summary>Getting the character to a point, anywhere in the open world.</summary>
public interface ITravel
{
    TravelStatus Status { get; }
    string FailReason { get; }

    /// <summary>
    /// Go to <paramref name="to"/> in <paramref name="territoryId"/>. <paramref name="fly"/> is
    /// "fly if the zone allows it"; <paramref name="land"/> ends the leg on the ground. A stop
    /// distance of <see cref="Traveller.PointStop"/> means land on the point itself.
    /// </summary>
    void Begin(uint territoryId, Vector3 to, bool fly, bool land, float stopDistance = Traveller.PointStop);

    TravelStatus Tick();
    void Cancel();
}

/// <summary>
/// Demeter's own travel: open-world only, ticked every frame.
///
/// <para>
/// The gatherer used to borrow Odysseus's quest step executor for this, which does far more —
/// doors, lifts, gates, the city aethernet, quest mounts, dives, duty handoffs — none of which a
/// node in a field ever needs. What is here is the part of it gathering exercised, ported with the
/// lessons that went into it (same author, both AGPL): settle before choosing a route, warm the
/// aetheryte list before teleporting, ask again when a teleport is swallowed by the action lock,
/// keep asking for the mount after a loading screen, fly wherever the currents are done, judge a
/// leg by progress rather than wall time, step between air and ground when the pathfollower wedges,
/// ride a descent all the way down and reroute one that never lands, and hold still through a fight
/// Daedalus is having.
/// </para>
///
/// <para>
/// Cross-zone travel is ours, not the pathing plugin's: Ariadne and vnavmesh move within a zone,
/// and getting to the zone is a Lifestream teleport to the attuned aetheryte nearest the point.
/// </para>
/// </summary>
public sealed class Traveller : ITravel
{
    private enum Phase { None, Settle, Teleport, TeleportWait, Mount, Move, Land }

    /// <summary>Land on the point itself (vnavmesh's own arrival, not a radius around it).</summary>
    public const float PointStop = 0.5f;

    /// <summary>How close "arrived" is for a stop that is not the point itself.</summary>
    public const float DefaultStopDistance = 3f;

    /// <summary>
    /// The pathing plugin declares arrival by its own tolerance and can stop a hair outside ours;
    /// without slack the leg would re-path three times over half a yalm and then fail.
    /// </summary>
    public const float ArrivalSlack = 1.5f;

    /// <summary>
    /// Close enough for a keypress to land on a node. Measured in three dimensions, because the
    /// way this fails is vertical — the walk finishes on a lip above it.
    /// </summary>
    public const float InteractReach = 3.5f;

    /// <summary>Same zone, but this far out with an aetheryte by the point: the crystal beats the ride.</summary>
    public const float TeleportWorthDistance = 250f;

    /// <summary>And it has to win by this much — a teleport is a cast and a loading screen.</summary>
    private const float TeleportMargin = 200f;

    /// <summary>Distances past this are worth a mount.</summary>
    public const float MountWorthDistance = 30f;

    private const float SprintWorthDistance = 25f;

    /// <summary>A leg given up on this close to its point is taken as arrived rather than failed.</summary>
    private const float NearEnough = 5f;

    /// <summary>The most that is walked blind from the mesh's nearest point to the destination.</summary>
    private const float OffMeshDirectMax = 15f;

    /// <summary>How far around an off-mesh destination to look for a point the mesh does reach.</summary>
    private const float OffMeshSnapRange = 10f;

    /// <summary>Closing this much counts as progress; anything less is standing still.</summary>
    private const float StallProgress = 0.5f;

    /// <summary>Moving this far in the stall window is not a stall, whatever the straight-line distance does.</summary>
    private const float StallMoveProgress = 2.5f;
    private const int MaxMoveRetries = 3;
    private const int MaxFrozenStops = 6;
    private const int MaxTeleportAsks = 2;

    private static readonly TimeSpan SettleGrace = TimeSpan.FromSeconds(1);

    /// <summary>A conversation left hanging is not worth waiting on forever; cutscenes hold this clock.</summary>
    private static readonly TimeSpan SettleMax = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan ReadyWait = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan AetheryteWarmup = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan TeleportRetryEvery = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan TeleportGiveUp = TimeSpan.FromSeconds(12);

    /// <summary>A teleport that took goes travel-busy within a second or two; past this, it did not.</summary>
    private static readonly TimeSpan TeleportStartGrace = TimeSpan.FromSeconds(4);
    private static readonly TimeSpan TravelStart = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan TravelMax = TimeSpan.FromSeconds(90);

    /// <summary>
    /// How long to keep asking for a mount before walking instead. Longer than it looks like it
    /// needs to be: the seconds after a teleport are a lock, and a mount asked for inside it is
    /// dropped without a word.
    /// </summary>
    private static readonly TimeSpan MountWait = TimeSpan.FromSeconds(12);
    private static readonly TimeSpan MountRetry = TimeSpan.FromSeconds(2);

    /// <summary>A leg that stops closing for this long fails; every ten yalms gained buys it back.</summary>
    private static readonly TimeSpan MoveTotal = TimeSpan.FromSeconds(180);
    private static readonly TimeSpan MoveStall = TimeSpan.FromSeconds(8);

    /// <summary>How long a pathfind is given to answer before the attempt is judged.</summary>
    private static readonly TimeSpan PathSettle = TimeSpan.FromSeconds(2);

    /// <summary>"Moving" with the position frozen this long is a wedge, not a walk.</summary>
    private static readonly TimeSpan FrozenStallLimit = TimeSpan.FromSeconds(12);
    private static readonly TimeSpan StallJumpAfter = TimeSpan.FromSeconds(4);
    private static readonly TimeSpan StallJumpGap = TimeSpan.FromSeconds(8);
    private static readonly TimeSpan SprintRetry = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan DismountRetry = TimeSpan.FromSeconds(2);

    /// <summary>The mounted flag clears before the animation ends, and a press in that gap is eaten.</summary>
    private static readonly TimeSpan DismountSettle = TimeSpan.FromSeconds(0.8);

    /// <summary>A three-yalm descent touches down in a second or two; five is a descent that will not.</summary>
    private static readonly TimeSpan BlockedDescentAfter = TimeSpan.FromSeconds(5);

    private readonly ITravelWorld _world;

    private Phase _phase;
    private DateTime _phaseStart;
    private uint _territory;
    private Vector3 _to;
    private bool _fly;
    private bool _land;
    private float _stop;

    // Teleport
    private uint _teleportTarget;
    private bool _listWarmed;
    private DateTime _warmedAt;
    private DateTime _lastTeleportTry;
    private int _teleportAsks;
    private bool _sawTravelBusy;
    private DateTime _settledSince;

    // Mount / land
    private DateTime _lastMountTry;
    private DateTime _lastDismountTry;
    private DateTime _dismountedAt;
    private bool _descentRerouted;

    /// <summary>A detour inside the move — the reroute over a descent with no floor.</summary>
    private Vector3? _detourTo;

    // Move
    private DateTime _lastMoveIssue;
    private int _moveRetries;
    private float _bestDistance;
    private DateTime _lastProgress;
    private Vector3 _frozenAt;
    private DateTime _frozenSince;
    private int _frozenStops;
    private bool _groundFallback;
    private bool _airFallback;
    private bool _lastIssuedFly;
    private bool _landedToFinish;
    private float _closestSeen;
    private DateTime _stalledSince;
    private Vector3 _stallAnchor;
    private DateTime _lastStallJump;
    private int _stallJumps;
    private DateTime _lastSprintTry;
    private bool _offMeshNudged;
    private Vector3? _offMeshSnap;

    // Fight
    private bool _heldForFight;
    private bool _fightDismounted;
    private DateTime _lastFightDismount;

    public Traveller(ITravelWorld world) => _world = world;

    public TravelStatus Status { get; private set; } = TravelStatus.Idle;
    public string FailReason { get; private set; } = string.Empty;

    /// <summary>What it is doing, for a status line.</summary>
    public string PhaseName => _heldForFight ? "Waiting out a fight" : _phase.ToString();

    public void Begin(uint territoryId, Vector3 to, bool fly, bool land, float stopDistance = PointStop)
    {
        _territory = territoryId;
        _to = to;
        _fly = fly;
        _land = land;
        _stop = stopDistance;
        _teleportAsks = 0;
        _listWarmed = false;
        _settledSince = default;
        _descentRerouted = false;
        _detourTo = null;
        _heldForFight = false;
        _fightDismounted = false;
        _groundFallback = false;
        _airFallback = false;
        _landedToFinish = false;
        _frozenStops = 0;
        FailReason = string.Empty;
        Status = TravelStatus.Running;
        Enter(Phase.Settle);
    }

    public void Cancel()
    {
        if (Status == TravelStatus.Running)
        {
            _world.StopMoving();
            _world.AbortTravel();
        }
        _phase = Phase.None;
        Status = TravelStatus.Idle;
    }

    public TravelStatus Tick()
    {
        if (Status != TravelStatus.Running)
            return Status;

        var now = _world.UtcNow;
        if (_world.IsDead)
            return Fail("the character is dead");

        if (_phase is Phase.Mount or Phase.Move && HoldForFight(now))
            return Status;

        switch (_phase)
        {
            case Phase.Settle: TickSettle(now); break;
            case Phase.Teleport: TickTeleport(now); break;
            case Phase.TeleportWait: TickTeleportWait(now); break;
            case Phase.Mount: TickMount(now); break;
            case Phase.Move: TickMove(now); break;
            case Phase.Land: TickLand(now); break;
        }
        return Status;
    }

    /// <summary>
    /// Where the character is, is only worth choosing a route from once the game has stopped moving
    /// it — a zone load still finishing, or a conversation still open, decides the wrong one.
    /// </summary>
    private void TickSettle(DateTime now)
    {
        if (_world.IsOccupied || _world.IsTravelBusy)
        {
            _settledSince = default;
            if (now - _phaseStart > SettleMax)
            {
                _world.Log($"The game has not let go of the character in {SettleMax.TotalSeconds:F0}s — choosing the route anyway.");
                Enter(Route());
            }
            return;
        }
        if (_settledSince == default)
            _settledSince = now;
        if (now - _settledSince >= SettleGrace)
            Enter(Route());
    }

    private Phase Route()
    {
        var here = _world.PlayerPosition;
        if (_world.TerritoryId != _territory)
        {
            if (_world.NearestAttunedAetheryte(_territory, _to) is not { } aetheryte)
            {
                Fail($"territory {_territory} has no aetheryte this character has attuned — attune one, or go there yourself");
                return Phase.None;
            }
            _teleportTarget = aetheryte;
            _world.Log($"Going to territory {_territory}: teleporting to aetheryte {aetheryte}.");
            return Phase.Teleport;
        }

        // Same zone and a long way off, with an attuned aetheryte standing near the point: the
        // crystal beats even the flight — when it wins by a clear margin.
        var away = Vector3.Distance(here, _to);
        if (away > TeleportWorthDistance
            && _world.NearestAttunedAetheryte(_territory, _to) is { } near
            && _world.AetherytePosition(near) is { } nearAt
            && Vector3.Distance(nearAt, _to) + TeleportMargin < away)
        {
            _teleportTarget = near;
            _world.Log($"The point is {away:F0}y away and aetheryte {near} stands {Vector3.Distance(nearAt, _to):F0}y from it — teleporting there.");
            return Phase.Teleport;
        }

        return AfterTeleport();
    }

    private Phase AfterTeleport()
    {
        var distance = Vector3.Distance(_world.PlayerPosition, _to);
        if (distance <= _stop + ArrivalSlack)
            return Arrive();
        if (distance > MountWorthDistance && _world.CanMountHere && !_world.IsMounted)
        {
            _lastMountTry = default;   // the phase does the asking, and keeps asking
            return Phase.Mount;
        }
        return Phase.Move;
    }

    private void TickTeleport(DateTime now)
    {
        if (!_world.IsReady || _world.InCombat)
        {
            if (_world.IsOccupied)
                _phaseStart = now; // a cutscene is not the teleport failing
            if (now - _phaseStart > ReadyWait)
                Fail("never became ready to teleport");
            return;
        }
        // The game's aetheryte list fills a frame or two after it is asked to: a request made in
        // the same breath is accepted and casts nothing. Warm it, then ask.
        if (!_listWarmed)
        {
            _listWarmed = true;
            _warmedAt = now;
            _world.RefreshAetheryteList();
            return;
        }
        if (now - _warmedAt < AetheryteWarmup)
            return;
        // A refusal right after a gearset change or a dismount is the action lock settling, not a
        // missing attunement — the gather runner switches job and then teleports, every time.
        if (now - _lastTeleportTry < TeleportRetryEvery)
            return;
        if (!_world.Teleport(_teleportTarget))
        {
            _lastTeleportTry = now;
            if (now - _phaseStart > TeleportGiveUp)
                Fail($"the teleport to aetheryte {_teleportTarget} kept being refused for {(now - _phaseStart).TotalSeconds:F0}s — Lifestream loaded?");
            return;
        }
        _sawTravelBusy = false;
        Enter(Phase.TeleportWait);
    }

    private void TickTeleportWait(DateTime now)
    {
        if (_world.IsTravelBusy)
            _sawTravelBusy = true;
        var arrived = _world.TerritoryId == _territory && !_world.IsTravelBusy && _world.IsReady;

        // Lifestream said yes and nothing happened: the cast fell into the action lock a gearset
        // change leaves behind. Ask again before believing it — the gather lists faulted every
        // item this way in Odysseus before this was there.
        if (!_sawTravelBusy && !_world.IsTravelBusy && _world.TerritoryId != _territory
            && now - _phaseStart > TeleportStartGrace && _teleportAsks < MaxTeleportAsks)
        {
            _teleportAsks++;
            _world.Log($"The teleport to aetheryte {_teleportTarget} never started — asking again ({_teleportAsks}/{MaxTeleportAsks}).");
            _listWarmed = false;
            Enter(Phase.Teleport);
            return;
        }

        if (arrived && (_sawTravelBusy || now - _phaseStart > TravelStart))
        {
            // Wherever it put us is where the next decision starts — settle, then route again.
            _settledSince = default;
            Enter(Phase.Settle);
            return;
        }
        if (!_sawTravelBusy && !arrived && now - _phaseStart > TravelStart)
        {
            Fail($"the teleport to aetheryte {_teleportTarget} never started");
            return;
        }
        if (now - _phaseStart > TravelMax)
            Fail($"the teleport to aetheryte {_teleportTarget} did not finish in {TravelMax.TotalSeconds:F0}s");
    }

    private void TickMount(DateTime now)
    {
        if (_world.IsMounted || now - _phaseStart > MountWait)
        {
            if (!_world.IsMounted)
                _world.Log("The mount would not come — this leg stays on foot.");
            Enter(Phase.Move); // mounted, or long enough — walking is always an option
            return;
        }
        // Asked here rather than once on the way in: the request only lands when the character can
        // act, and after a teleport that is not straight away.
        if (_world.IsReady && !_world.InCombat && now - _lastMountTry >= MountRetry)
        {
            _lastMountTry = now;
            _world.Mount();
        }
    }

    /// <summary>
    /// A fight on the way — a mob that took a swing at the node circuit, a FATE spilling over.
    /// Daedalus finishes it (Demeter's busy gate is what tells it to) and that moves the character,
    /// so every walk issued meanwhile would be cut off and counted as a failure. Hold every clock,
    /// get off the mount (nobody fights from the saddle), and carry on when it is over — remounting
    /// if the fight took us off. Daedalus switched off means nobody fights: then moving on is the
    /// better answer, and nothing holds.
    /// </summary>
    private bool HoldForFight(DateTime now)
    {
        if (_world.InCombat && !_world.DaedalusDisabledByUser)
        {
            if (!_heldForFight)
            {
                _heldForFight = true;
                if (_world.IsMoving)
                    _world.StopMoving();
                _world.Log("In combat on the way — waiting for it to end, then going on.");
            }
            if (_world.IsMounted && now - _lastFightDismount > DismountRetry)
            {
                _lastFightDismount = now;
                _fightDismounted = true;
                _world.StopMoving();
                _world.Dismount();
            }
            _phaseStart = now;
            _lastMoveIssue = default;
            _lastProgress = now;
            _stalledSince = default;
            return true;
        }
        if (!_heldForFight)
            return false;

        _heldForFight = false;
        var left = Vector3.Distance(_world.PlayerPosition, _to);
        var remount = _phase == Phase.Move && (_fightDismounted || left > MountWorthDistance)
                      && _world.CanMountHere && !_world.IsMounted;
        _fightDismounted = false;
        _world.Log(remount ? "Out of combat — mounting up and going on." : "Out of combat — going on.");
        if (!remount)
            return false;
        _lastMountTry = default;
        Enter(Phase.Mount);
        return true;
    }

    private void TickMove(DateTime now)
    {
        // A cutscene or conversation owns the character: no clock runs through it.
        if (_world.IsOccupied)
        {
            _phaseStart = now;
            _lastMoveIssue = now;
            _lastProgress = now;
            return;
        }

        var detour = _detourTo;
        var target = detour ?? _to;
        var tolerance = detour is null ? _stop : InteractReach - ArrivalSlack;
        var distance = Vector3.Distance(_world.PlayerPosition, target);

        if (_stalledSince == default)
        {
            _stalledSince = now;
            _closestSeen = distance;
            _stallAnchor = _world.PlayerPosition;
        }

        // A flight that ends hanging over its point never arrives: the point's own Y is the ground.
        // Horizontally there and above it — come down, then judge from the ground.
        if (detour is null && _world.IsInFlight && _world.PlayerPosition.Y > target.Y
            && Vector3.Distance(target with { Y = _world.PlayerPosition.Y }, _world.PlayerPosition) <= tolerance + ArrivalSlack
            && distance > tolerance + ArrivalSlack)
        {
            _world.StopMoving();
            _world.Log($"The flight ended {_world.PlayerPosition.Y - target.Y:F0}y above {Fmt(target)} — landing.");
            BeginLand();
            return;
        }

        if (distance <= tolerance + ArrivalSlack)
        {
            _world.StopMoving();
            if (detour is not null)
            {
                // The reroute over a floorless descent: on to the landing again from here.
                _detourTo = null;
                BeginLand();
                return;
            }
            Enter(Arrive());
            return;
        }

        // The budget measures progress, not wall time: a long crossing still closing is not stuck.
        if (distance < _bestDistance - 10f)
        {
            _bestDistance = distance;
            _lastProgress = now;
        }
        if (now - _lastProgress > MoveTotal)
        {
            Fail($"no progress toward {Fmt(target)} for {MoveTotal.TotalSeconds:F0}s ({distance:F1}y left)");
            return;
        }

        if (!_world.NavmeshReady)
        {
            // A mesh still building is a wait, not a fault: a fresh zone takes far longer than the
            // stall clock, which is there for a mesh that is not coming at all.
            if (_world.NavmeshBuildProgress >= 0f)
            {
                _phaseStart = now;
                _lastProgress = now;
                return;
            }
            if (now - _phaseStart > MoveStall)
                Fail("the navmesh is not ready — is vnavmesh (or Ariadne) loaded?");
            return;
        }

        if (_world.IsMoving)
        {
            TickWhileMoving(now, target, distance);
            return;
        }

        // Not moving and not there: either we have not asked yet, or the path ended short.
        if (_lastMoveIssue != default && now - _lastMoveIssue < PathSettle)
            return;

        var fly = (_fly || _airFallback) && !_groundFallback && _world.CanFlyHere && _world.IsMounted
                  || (detour is not null && _world.IsMounted);
        _lastIssuedFly = fly;

        // The mesh answered nothing. A point simply off the mesh — a node on a rock the mesh
        // disowns — is reached by walking the last stretch blind, or by going to the nearest
        // point the mesh reaches and walking the rest.
        if (_moveRetries > 0 && _world.PathWaypointCount == 0)
        {
            if (!_offMeshNudged && distance <= OffMeshDirectMax)
            {
                _offMeshNudged = true;
                _lastMoveIssue = now;
                _world.Log($"Walking the last {distance:F1}y to {Fmt(target)} directly — the mesh gave no path.");
                _world.MoveDirectTo(target, false);
                return;
            }
            if (_offMeshSnap is null
                && _world.NearestReachablePoint(target, OffMeshSnapRange) is { } snap
                && Vector3.Distance(snap, target) > ArrivalSlack
                && Vector3.Distance(snap, _world.PlayerPosition) > ArrivalSlack)
            {
                _offMeshSnap = snap;
                _lastMoveIssue = now;
                _world.Log($"{Fmt(target)} is off the mesh; going to the nearest point it reaches ({Vector3.Distance(snap, target):F1}y short).");
                _world.MoveTo(snap, fly);
                return;
            }
        }

        if (_moveRetries >= MaxMoveRetries)
        {
            // Giving up while still in the air is premature: a hover is fat and snags on lips a
            // walker slips past. Land once, within the last stretch, and try again on foot.
            if (_world.IsInFlight && !_landedToFinish && detour is null && distance <= OffMeshDirectMax)
            {
                _landedToFinish = true;
                _groundFallback = true;
                _world.Log($"Still in flight {distance:F1}y from {Fmt(target)} — landing to finish on foot.");
                BeginLand(thenMove: true);
                return;
            }
            if (detour is null && distance <= NearEnough)
            {
                _world.Log($"Ended {distance:F1}y short of {Fmt(target)} and can get no closer — near enough.");
                _world.StopMoving();
                Enter(Arrive());
                return;
            }
            // The ground has no route: the air may. One leg, not the run.
            if (!_airFallback && _world.CanFlyHere)
            {
                _airFallback = true;
                _groundFallback = false;
                _moveRetries = 0;
                _world.Log($"The ground has no route to {Fmt(target)} — flying this leg.");
                Enter(_world.IsMounted ? Phase.Move : Phase.Mount);
                return;
            }
            Fail(_world.PathWaypointCount == 0
                ? $"no path to {Fmt(target)} after {_moveRetries} attempts{MeshDiagnosis(target)}"
                : $"stalled {_moveRetries} times short of {Fmt(target)} ({distance:F1}y left)");
            return;
        }

        var ok = tolerance > PointStop
            ? _world.MoveCloseTo(target, tolerance, fly)
            : _world.MoveTo(target, fly);
        _lastMoveIssue = now;
        _moveRetries++;
        if (!ok)
            _world.Log($"Move to {Fmt(target)} refused (attempt {_moveRetries}).");
    }

    private void TickWhileMoving(DateTime now, Vector3 target, float distance)
    {
        // Moving in name only: the pathfollower can wedge against geometry with the position frozen
        // to the yalm. Hand the leg to the other medium — the volume path snags on rooflines and
        // branches the walk beneath slips under, and the reverse. The epsilon beats a jump's bob.
        if (Vector3.Distance(_world.PlayerPosition, _frozenAt) > 2.5f
            || _world.IsPathfinding || _world.IsCasting || _world.IsTravelBusy)
        {
            _frozenAt = _world.PlayerPosition;
            _frozenSince = now;
        }
        else if (_frozenSince != default && now - _frozenSince > FrozenStallLimit)
        {
            if (++_frozenStops > MaxFrozenStops)
            {
                Fail($"the leg keeps wedging near {Fmt(_world.PlayerPosition)} — {_frozenStops} re-paths went nowhere toward {Fmt(target)}");
                return;
            }
            if (_lastIssuedFly && !_groundFallback)
            {
                _groundFallback = true;
                _world.Log($"The flight is wedged at {Fmt(_world.PlayerPosition)} — trying this leg on the ground.");
            }
            else if (!_lastIssuedFly && _world.CanFlyHere)
            {
                _groundFallback = false;
                _airFallback = true;
                _world.Log($"The walk is wedged at {Fmt(_world.PlayerPosition)} — trying this leg in the air.");
            }
            else
                _world.Log($"Moving without moving at {Fmt(_world.PlayerPosition)} — stopping and re-pathing.");
            _world.StopMoving();
            _frozenSince = now;
            _lastMoveIssue = default;
            if (_airFallback && !_groundFallback && !_world.IsMounted && _world.CanMountHere)
                Enter(Phase.Mount);
            return;
        }
        _lastMoveIssue = now;

        // No mount allowed here: Sprint is the fastest thing going.
        if (!_world.IsMounted && !_world.CanMountHere && distance > SprintWorthDistance && now - _lastSprintTry > SprintRetry)
        {
            _lastSprintTry = now;
            _world.Sprint();
        }

        // Moving but not getting anywhere — caught on the lip of something. A jump clears most of
        // it. Never while the pathing plugin plans or a cast runs (that is busy, not stuck), never
        // from the saddle in the air, and three hops that gain nothing hand it to the re-path.
        if (_world.IsCasting || _world.IsTravelBusy || _world.IsPathfinding)
        {
            _stalledSince = now;
            _stallAnchor = _world.PlayerPosition;
            return;
        }
        var moved = Vector3.Distance(_world.PlayerPosition, _stallAnchor) > StallMoveProgress;
        if (distance < _closestSeen - StallProgress || moved)
        {
            if (distance < _closestSeen) _closestSeen = distance;
            _stallAnchor = _world.PlayerPosition;
            _stalledSince = now;
            _stallJumps = 0;
        }
        else if (now - _stalledSince > StallJumpAfter && now - _lastStallJump > StallJumpGap
                 && distance > 10f && !_world.IsInFlight && _world.CanMountHere)
        {
            _lastStallJump = now;
            _stalledSince = now;
            if (++_stallJumps >= 3)
            {
                _stallJumps = 0;
                _moveRetries++;
                _world.Log($"Three hops gained nothing toward {Fmt(target)} ({distance:F1}y) — stopping and re-pathing.");
                _world.StopMoving();
                _lastMoveIssue = default;
                return;
            }
            _world.Log($"Not getting any closer to {Fmt(target)} ({distance:F1}y) — jumping.");
            _world.Jump();
        }
    }

    private Phase Arrive()
    {
        if (_land && _world.IsMounted)
        {
            BeginLand();
            return _phase;
        }
        Status = TravelStatus.Done;
        return Phase.None;
    }

    private bool _landThenMove;

    private void BeginLand(bool thenMove = false)
    {
        _landThenMove = thenMove;
        _lastDismountTry = default;
        _dismountedAt = default;
        Enter(Phase.Land);
    }

    /// <summary>
    /// Both feet on the ground. A dismount in the air is a descent and the character stays mounted
    /// the whole way down, so this keeps asking rather than assuming it took — and a descent with
    /// no floor beneath it (hovering against a ledge) is aborted and rerouted to come down on a
    /// floor the mesh knows, beside the point.
    /// </summary>
    private void TickLand(DateTime now)
    {
        if (_world.IsMounted && _world.IsInFlight && !_descentRerouted && _world.CanFlyHere
            && now - _phaseStart > BlockedDescentAfter && Vector3.Distance(_to, _world.PlayerPosition) > 0.5f)
        {
            var floor = _world.NearestReachablePoint(_to, 8f) ?? _to;
            _descentRerouted = true;
            _world.Log($"The descent is not landing (at {Fmt(_world.PlayerPosition)}) — flying over {Fmt(floor)} to come down on its floor.");
            _detourTo = floor;
            Enter(Phase.Move);
            return;
        }

        if (!_world.IsMounted)
        {
            if (_dismountedAt == default)
            {
                _dismountedAt = now;
                return;
            }
            if (now - _dismountedAt < DismountSettle)
                return;
            if (_landThenMove)
            {
                _landThenMove = false;
                Enter(Phase.Move);
                return;
            }
            Status = TravelStatus.Done;
            _phase = Phase.None;
            return;
        }
        if (now - _lastDismountTry > DismountRetry)
        {
            _lastDismountTry = now;
            _world.Dismount();
        }
        if (now - _phaseStart > ReadyWait)
            Fail($"could not get off the mount (at {Fmt(_world.PlayerPosition)}, {(_world.IsInFlight ? "in flight" : "on the ground")})");
    }

    /// <summary>
    /// Why the mesh gave nothing. A mesh that does not cover where we stand is stale (a rebuild
    /// fixes it); a destination nothing reaches is the data's.
    /// </summary>
    private string MeshDiagnosis(Vector3 target)
    {
        if (_world.NearestReachablePoint(_world.PlayerPosition, 3f) is null)
            return " — the loaded navmesh does not cover where you stand, so it is probably stale: /vnav rebuild";
        if (_world.NearestReachablePoint(target, 3f) is null)
            return " — the point is off the mesh";
        return string.Empty;
    }

    private void Enter(Phase phase)
    {
        if (phase == Phase.None)
            return; // a helper already finished or failed the leg
        _phase = phase;
        _phaseStart = _world.UtcNow;
        if (phase == Phase.Move)
        {
            _lastMoveIssue = default;
            _moveRetries = 0;
            _bestDistance = float.MaxValue;
            _lastProgress = _world.UtcNow;
            _stalledSince = default;
            _stallJumps = 0;
            _offMeshSnap = null;
            _offMeshNudged = false;
            _frozenSince = default;
        }
    }

    private TravelStatus Fail(string reason)
    {
        _world.StopMoving();
        _phase = Phase.None;
        FailReason = reason;
        Status = TravelStatus.Failed;
        return Status;
    }

    private static string Fmt(Vector3 v) => $"({v.X:F0},{v.Y:F0},{v.Z:F0})";
}
