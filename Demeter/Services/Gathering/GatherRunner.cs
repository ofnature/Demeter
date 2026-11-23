using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Demeter.Services.Travel;

namespace Demeter.Services.Gathering;

/// <summary>What the runner needs of the game beyond travel, which is <see cref="ITravel"/>'s.</summary>
public interface IGatherWorld
{
    DateTime UtcNow { get; }
    uint TerritoryId { get; }
    Vector3 PlayerPosition { get; }
    bool IsOccupied { get; }

    /// <summary>The class being played. A node is worked as a Miner or a Botanist and no one else.</summary>
    uint CurrentClassJob { get; }

    /// <summary>Equip a gearset for this class. False when the character has none, with a reason.</summary>
    bool EquipGearsetFor(uint classJobId, out string reason);

    /// <summary>
    /// With no gearset for the class, one is being made: the game's Recommended Gear put on and
    /// saved. The switch is not over until it is, though the class may already read right. Reading
    /// it drives it.
    /// </summary>
    bool GearingUp => false;

    /// <summary>Why making that gearset stopped short; empty when it did not.</summary>
    string GearUpFailure => string.Empty;

    bool IsMounted { get; }

    /// <summary>Get off. A node cannot be worked from the saddle.</summary>
    void Dismount();

    /// <summary>Mounted and airborne — a descent in progress still counts.</summary>
    bool IsInFlight { get; }

    /// <summary>How many of the item are held at or above a collectability.</summary>
    int CollectableCount(uint itemId, int minimumCollectability);

    /// <summary>The node object, if it has spawned at the spot we walked to.</summary>
    bool IsDataIdSpawned(uint dataId);

    /// <summary>How far the node actually is, or null when it is not loaded.</summary>
    float? DistanceToDataId(uint dataId);

    /// <summary>Where the node actually is, which beats where it was recorded.</summary>
    Vector3? PositionOfDataId(uint dataId);

    /// <summary>
    /// The nearest node among these that is <i>up</i> — loaded and targetable — or null when none
    /// is. Targetable is what tells a live node from one that has been worked out and is waiting to
    /// move, which is the difference between walking to something and walking past it. An empty set
    /// means any node at all, which is what a diagnostic wants.
    /// </summary>
    (uint NodeId, Vector3 Position, float Distance)? NearestLiveNode(IReadOnlyCollection<uint> nodeIds);
    void FaceDataId(uint dataId);
    bool TryInteractWithDataId(uint dataId);

    /// <summary>Gathering has begun — the game says so before either window appears.</summary>
    bool NodeOpen { get; }

    /// <summary>The item list is actually on screen, so a slot can be chosen.</summary>
    bool ItemListOpen { get; }

    /// <summary>
    /// A gathering action — or the reveal animation when a node first opens — is still playing.
    /// Anything fired into the window while this is set is swallowed without a word.
    /// </summary>
    bool ExecutingAction { get; }

    /// <summary>The window's own numbers, or null when it is not open or not a collectable node.</summary>
    CollectableState? Collectable { get; }

    /// <summary>Choose the slot yielding this item, so the node's other offerings are left alone.</summary>
    bool SelectSlotFor(uint itemId);

    /// <summary>The items in the open node's rows; empty when no list is up.</summary>
    IReadOnlyList<uint> NodeItems => Array.Empty<uint>();

    bool UseAction(uint actionId);

    /// <summary>The character has this buff on.</summary>
    bool HasStatus(uint statusId);

    /// <summary>Gathering points in hand.</summary>
    int Gp { get; }

    /// <summary>The action's recast is over.</summary>
    bool OffCooldown(uint actionId);

    /// <summary>Stop dead. Interacting while still being moved is refused by the game.</summary>
    void StopMoving();

    /// <summary>The condition flags, for the log — what the game thought was true at the time.</summary>
    string Conditions { get; }

    /// <summary>Write what the open window contains to the log, without touching it.</summary>
    void DescribeOpenWindow();

    /// <summary>Starting on a fresh node: whatever was tried on the last one does not carry over.</summary>
    void ForgetSlotAttempts();

    void CloseNode();

    /// <summary>
    /// Leaving a node with gathers still on it — a timed one, say — the game asks first, and its
    /// Yes stays greyed until the tick box beside it is ticked. Tick it and say yes, if that
    /// prompt is up over an open node; true when it was answered.
    /// </summary>
    bool ConfirmLeavingNode() => false;

    void Log(string message);

    /// <summary>
    /// Why this character cannot use the action — "Collect needs quest "…"" — or null when it can.
    /// Answered from <see cref="GatheringAbilities"/>; a world without one knows every action.
    /// </summary>
    string? WhyCannotUse(uint actionId) => null;
}

/// <summary>Where a run has got to.</summary>
public enum GatherRunState
{
    Idle, SwitchingJob, Travelling, Walking, Opening, Working,
    /// <summary>Finished or told to stop, and seeing the gathering window shut before saying so.</summary>
    Closing,
    Done, Faulted,
}

/// <summary>
/// Gathers a number of one collectable.
///
/// <para>
/// The shape is: go to the zone, walk to a spot the node spawns at, open it, and work it with
/// <see cref="CollectableRotation"/> until the bag holds enough at the collectability wanted. A node
/// is spent after a few attempts and re-appears at one of its <i>other</i> fixed spots, so running
/// out is the normal case rather than a failure: the runner walks to the next spot on the list and
/// carries on, cycling round.
/// </para>
///
/// <para>
/// Travel is <see cref="ITravel"/>'s, not a second implementation of it: aetherytes, mounts, the
/// navmesh and the fixes that went into them live there. (In Odysseus this borrowed the quest step
/// executor; Demeter has its own <see cref="Traveller"/>, ported from it.)
/// </para>
/// </summary>
public sealed class GatherRunner
{
    /// <summary>
    /// How far a loaded node may be from a recorded spot and still count as being at it. The spots
    /// are distinct places a node moves between, so this only has to cover the difference between
    /// where it was measured and where it stands, not the distance between spots.
    /// </summary>
    private const float SpotRadius = 12f;

    private static readonly TimeSpan JobWait = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan DismountRetry = TimeSpan.FromSeconds(2);

    /// <summary>
    /// Truth of Forests and Truth of Mountains: the buff that shows a Botanist or Miner the
    /// unspoiled and other hidden nodes. Changing class takes it off, and it is a toggle — pressed
    /// while on, it goes off — so it is pressed only while the game says it is off, and not again
    /// until the game has had time to say otherwise.
    /// </summary>
    private const uint TruthOfForests = 221, TruthOfForestsStatus = 221;
    private const uint TruthOfMountains = 238, TruthOfMountainsStatus = 222;
    private static readonly TimeSpan TruthRetry = TimeSpan.FromSeconds(2.5);
    private static readonly TimeSpan TruthWait = TimeSpan.FromSeconds(12);
    private DateTime _truthSince, _truthPressed;

    /// <summary>
    /// Blessed Harvest and King's Yield: every gather on the node gives one more (two more for the
    /// II). A timed node is the one node of its window, so the GP is spent there, once a node,
    /// before its first gather — on the II when it is learned and affordable, else the plain one.
    /// </summary>
    private const uint BlessedHarvest = 222, BlessedHarvestII = 224, KingsYield = 239, KingsYieldII = 241;
    private const int YieldCost = 400, YieldIICost = 500;
    private const uint YieldUpStatus = 219, YieldUpIIStatus = 1286;
    private readonly HashSet<uint> _timedNodes = new();
    private bool _yieldAsked;

    /// <summary>
    /// The Twelve's Bounty and The Giving Land: more shards, crystals or clusters from the next
    /// gather. They stack. The Giving Land is on a three-minute recast, so it goes on whenever it
    /// is up; the Twelve's Bounty on every gather the GP pays for. Each is pressed at most once a
    /// gather, so one the game takes without effect holds nothing up.
    /// </summary>
    private const uint TwelvesBountyMiner = 280, TwelvesBountyBotanist = 282, TwelvesBountyStatus = 825;
    private const uint GivingLandMiner = 4589, GivingLandBotanist = 4590, GivingLandStatus = 1802;
    private const int TwelvesBountyCost = 150, GivingLandCost = 200;
    private readonly HashSet<uint> _crystalBuffsPressed = new();

    /// <summary>The elemental shards, crystals and clusters: items 2 to 19.</summary>
    private static bool IsCrystal(uint itemId) => itemId is >= 2 and <= 19;
    private static readonly TimeSpan OpenWait = TimeSpan.FromSeconds(12);

    /// <summary>
    /// How long to leave an interaction alone before trying again, and how few times to try.
    ///
    /// <para>
    /// Opening a node is not something to repeat while one is already in flight: it takes up to a
    /// second for the game to report gathering has begun, and firing again into that gap wedged a
    /// client. GatherBuddy allows one interaction and then keeps its hands off for five seconds.
    /// </para>
    /// </summary>
    private static readonly TimeSpan OpenRetry = TimeSpan.FromSeconds(4);

    /// <summary>How long the game gets to put a window up once gathering has begun.</summary>
    private static readonly TimeSpan WindowWait = TimeSpan.FromSeconds(8);

    /// <summary>And how long it gets to take one down before we stop holding everyone up.</summary>
    private static readonly TimeSpan CloseWait = TimeSpan.FromSeconds(15);
    /// <summary>One ask per visit, as GatherBuddy does. Repeats are what wedge a client.</summary>
    private const int MaxOpens = 1;

    /// <summary>How level with a node you have to be. GatherBuddy uses three.</summary>
    private const float VerticalReach = 3f;

    /// <summary>How long to let a stop land before interacting. A frame would do; this is safe.</summary>
    private static readonly TimeSpan SettleAfterStopping = TimeSpan.FromMilliseconds(400);
    private static readonly TimeSpan MoveWait = TimeSpan.FromMinutes(3);
    private static readonly TimeSpan ActionGap = TimeSpan.FromSeconds(1);

    /// <summary>How long a plain-row press gets to become a gathering action before the row is judged dead.</summary>
    private static readonly TimeSpan PlainPressGrace = TimeSpan.FromSeconds(3);

    /// <summary>How long a shut window waits for the node to come back (Revisit) before the next spot.</summary>
    private static readonly TimeSpan RevisitGrace = TimeSpan.FromSeconds(2.5);

    /// <summary>A Revisit reopen that does not take is judged quickly; the node may only have looked live.</summary>
    private static readonly TimeSpan RevisitOpenWait = TimeSpan.FromSeconds(4);
    private DateTime _spentAt;
    private bool _revisit;

    /// <summary>
    /// A node the game opens and shuts again before its list shows is not spent, and is not back
    /// from a Revisit: the game began the gather and then refused it as out of reach, though within
    /// <see cref="Traveller.InteractReach"/>. Walked right up to and asked once more; refused again,
    /// it is left a while.
    /// </summary>
    private const float CloseReach = Traveller.PointStop + Traveller.ArrivalSlack;
    private static readonly TimeSpan LeaveRefusingNode = TimeSpan.FromMinutes(2);
    private bool _windowSeen;
    private uint _closeInNode;
    private readonly Dictionary<uint, DateTime> _leftUntil = new();
    private bool _plainPressed;
    private bool _plainPressTook;

    /// <summary>Leave this much GP so the next node is not started empty.</summary>
    public int GpReserve { get; set; }

    /// <summary>
    /// Walk the whole run and touch nothing. Everything happens except the interaction itself: the
    /// class switch, the travel, finding what is up, getting level with it — and then a line in the
    /// log saying what it would have done. For when the thing being tested has been costing a
    /// client per attempt and the movement is what needs proving.
    /// </summary>
    public bool DryRun { get; set; }

    /// <summary>
    /// Open one node, write down everything the window says, close it, and stop. The step between
    /// "walk to it" and "work it": it proves the interaction on its own, without firing the slot
    /// callback — which is the one part still built on a guess about the window's layout, and the
    /// likeliest thing to leave a window that never populates.
    /// </summary>
    public bool ProbeOnly { get; set; }

    private readonly IGatherWorld _world;
    private readonly ITravel _travel;

    /// <summary>One place to stand, and the node that stands there.</summary>
    private readonly record struct Stop(uint NodeId, Vector3 Position);

    private readonly List<Stop> _stops = [];
    private GatheringTarget? _target;
    private uint _itemId;
    private int _wanted;
    private int _baseline;
    private int _collectability;
    private int _spawn;
    private int _visited;

    /// <summary>The bag at the last spot left: a visit that added to it is progress, not a lap.</summary>
    private int _heldAtLastSpot;

    /// <summary>Spots in a row Ariadne found no way to, and what it last said.</summary>
    private int _unreachableRun;
    private string _lastUnreachable = string.Empty;
    private DateTime _phaseStart;
    private DateTime _lastAction;
    private bool _scrutinyUsed;
    private int _opens;
    private uint _currentNode;
    private Vector3 _travelTo;
    private readonly List<uint> _nodeIds = [];

    /// <summary>Nodes tried from where Ariadne gave out that would not open: left alone for the rest of the run.</summary>
    private readonly HashSet<uint> _setAside = [];

    /// <summary>The opening under way is from short of reach, because the mesh goes no closer.</summary>
    private bool _fromShort;
    private bool _lastOpenAccepted;
    private DateTime _stoppedAt;
    private DateTime _slotChosenAt;
    private GatherRunState _afterClose = GatherRunState.Done;

    public GatherRunner(IGatherWorld world, ITravel travel)
    {
        _world = world;
        _travel = travel;
    }

    public GatherRunState State { get; private set; } = GatherRunState.Idle;
    public string Status { get; private set; } = string.Empty;
    public string FailReason { get; private set; } = string.Empty;

    /// <summary>The fault is a zone Ariadne has no way around, not the nodes: another zone may do.</summary>
    public bool NoWayInZone { get; private set; }

    /// <summary>How many are held at the collectability asked for.</summary>
    public int Held => _target is null ? 0 : _world.CollectableCount(_itemId, _collectability);

    /// <summary>
    /// How many this run has added. The ask is a <i>shortfall</i> — "get N more" — and judging it
    /// against the whole bag ends a run before it moves: three already held against an ask of one
    /// read as finished, while the delivery still counted itself short.
    /// </summary>
    public int Gathered => Held - _baseline;

    /// <summary>
    /// What a node's remaining attempts go on once the item wanted is gone from it, or once enough
    /// of it is in the bag: Dark Matter Cluster first, then clusters, crystals and shards, then
    /// whatever else the node has. A node is gathered out, not left: an unspoiled node gives up its
    /// one item and keeps its integrity, walking away left a 100% cluster behind every window, and
    /// a timed node left with gathers on it asks before letting go.
    /// </summary>
    private const uint DarkMatterCluster = 10335;

    /// <summary>The node's rows, best spare first; <paramref name="crystalsOnly"/> stops before the rest.</summary>
    private IEnumerable<uint> Spares(bool crystalsOnly)
    {
        var rows = _world.NodeItems.Where(i => i != 0 && i != _itemId && i != _fallback).ToList();
        var best = rows.Where(i => i == DarkMatterCluster)
            .Concat(rows.Where(IsCrystal).OrderByDescending(i => i));   // clusters, then crystals, then shards
        return crystalsOnly ? best : best.Concat(rows.Where(i => i != DarkMatterCluster && !IsCrystal(i)));
    }

    /// <summary>The fallback being gathered on this node, once the item wanted ran out on it.</summary>
    private uint? _fallback;

    /// <summary>Enough is in the bag, and the node in hand is being gathered out before it is left.</summary>
    private bool _finishing;

    /// <summary>Only timed nodes: one node, somewhere among its spots, and gone once worked.</summary>
    private bool _timedOnly;

    private bool TryFallback()
    {
        foreach (var item in Spares(crystalsOnly: false))
        {
            if (!_world.SelectSlotFor(item)) continue;
            _fallback = item;
            var name = item == DarkMatterCluster ? " (Dark Matter Cluster)" : string.Empty;
            _world.Log(_finishing
                ? $"Node {_currentNode}: finishing it on item {item}{name}."
                : $"Item {_itemId} is gone from node {_currentNode} — spending the rest of it on item {item}{name}.");
            return true;
        }
        return false;
    }

    /// <summary>
    /// Enough is in the bag with the node still open: gather it out. A timed node goes on giving
    /// the item it is for; an ordinary one's attempts go on its shards, crystals or clusters where
    /// it has them, and on the item where it has none.
    /// </summary>
    private void FinishNode()
    {
        _finishing = true;
        _world.Log($"Node {_currentNode}: {Gathered} of {_wanted} gathered — gathering the node out before leaving.");
        Status = $"Node {_currentNode}: {Gathered} of {_wanted} gathered — finishing the node.";
    }

    /// <summary>
    /// An ordinary node being finished goes on its best crystal row. Chosen at the press, not when
    /// finishing begins: the list hides while a gather plays, and its rows cannot be read then.
    /// </summary>
    private void FinishOnCrystals()
    {
        if (!_finishing || _fallback is not null || _timedNodes.Contains(_currentNode))
            return;
        if (Spares(crystalsOnly: true).FirstOrDefault() is var spare and not 0)
        {
            _fallback = spare;
            _world.Log($"Node {_currentNode}: finishing it on item {spare}.");
        }
    }

    /// <summary>Work one node's spots. Kept for a caller that has already chosen.</summary>
    public void Begin(GatheringTarget target, int count, int minimumCollectability)
        => Begin([target], count, minimumCollectability);

    /// <summary>
    /// Work every node that yields the item, not just the best one.
    ///
    /// <para>
    /// An item is usually offered by several nodes in the same zone — Glass Eye by eleven of them,
    /// nineteen spots between them — and only some are up at any moment. Working one node's spots
    /// and giving up means walking a three-stop circuit past eight other nodes that have what you
    /// came for.
    /// </para>
    /// </summary>
    public void Begin(IReadOnlyList<GatheringTarget> targets, int count, int minimumCollectability)
    {
        var target = targets[0];
        _stops.Clear();
        _nodeIds.Clear();
        _setAside.Clear();
        _leftUntil.Clear();
        _closeInNode = 0;
        _timedNodes.Clear();
        foreach (var t in targets)
        {
            if (t.TerritoryId != target.TerritoryId || t.ClassJobId != target.ClassJobId)
                continue; // one trip, one class
            _nodeIds.Add(t.NodeId);
            if (t.Timed)
                _timedNodes.Add(t.NodeId);
            foreach (var spawn in t.Spawns)
                _stops.Add(new Stop(t.NodeId, spawn));
        }

        _target = target;
        _itemId = target.ItemId;
        _wanted = count;
        _fallback = null;
        _finishing = false;
        _timedOnly = System.Linq.Enumerable.All(targets, t => t.Timed);
        _collectability = minimumCollectability;
        _spawn = 0;
        _visited = 0;
        _baseline = _world.CollectableCount(target.ItemId, minimumCollectability);
        _heldAtLastSpot = _baseline;
        _unreachableRun = 0;
        _scrutinyUsed = false;
        _currentNode = target.NodeId;
        _travelTo = default;
        FailReason = string.Empty;
        NoWayInZone = false;
        _stopAfterNode = false;
        _truthSince = _truthPressed = default;
        Enter(GatherRunState.SwitchingJob);
    }

    /// <summary>Set by <see cref="StopAfterNode"/>: the node in hand is the last. Begin clears it.</summary>
    private bool _stopAfterNode;

    /// <summary>
    /// Stop once the node in hand is finished: a node being opened or worked is seen through until
    /// its window shuts, with no Revisit and no next spot. Switching class or on the way to a node,
    /// the run stops now. Either way it ends through Closing, so Busy holds until the character is free.
    /// </summary>
    public void StopAfterNode()
    {
        if (State is GatherRunState.Opening or GatherRunState.Working)
            _stopAfterNode = true;
        else if (State is GatherRunState.SwitchingJob or GatherRunState.Travelling or GatherRunState.Walking)
            StopAsAsked();
    }

    private void StopAsAsked()
    {
        _travel.Cancel();
        _target = null;
        _afterClose = GatherRunState.Idle;
        Enter(GatherRunState.Closing);
        Status = "Stopped after the node, as asked.";
    }

    public void Cancel()
    {
        _travel.Cancel();
        _target = null;

        // A window still open — or an action still playing — outlives the wish to stop, and the
        // caller's next move is usually a teleport the game refuses while it is up. So stopping
        // with a node open is a state, not an instant: Busy holds until the window is shut.
        if (_world.NodeOpen || _world.ExecutingAction)
        {
            _afterClose = GatherRunState.Idle;
            Enter(GatherRunState.Closing);
            return;
        }

        State = GatherRunState.Idle;
        Status = string.Empty;
    }

    public GatherRunState Tick()
    {
        // A node left with gathers on it asks first, and nothing else moves until that is answered.
        if (_leftAt != default && _world.NodeOpen && _world.UtcNow - _leftAt < LeavePromptWait
            && _world.UtcNow - _confirmedAt > ActionGap && _world.ConfirmLeavingNode())
        {
            _confirmedAt = _world.UtcNow;
            _world.Log($"Node {_currentNode}: the game asked to confirm leaving it with gathers left — ticked and answered yes.");
            return State;
        }

        // Closing runs even with no target: a cancelled run still owns its open window.
        if (State == GatherRunState.Closing)
        {
            TickClosing();
            return State;
        }

        if (_target is null || State is GatherRunState.Idle or GatherRunState.Done or GatherRunState.Faulted)
            return State;

        // What this run has gathered is the only thing that decides it is finished — but finished
        // still means seeing the window shut, or the teleport that follows is refused into it.
        // A collectable window at no integrity is spent, though it may not have shut yet.
        if (State == GatherRunState.Working && Gathered >= _wanted && _world.NodeOpen && _windowSeen && !ProbeOnly
            && _world.Collectable is not { IntegrityLeft: <= 0 })
        {
            if (!_finishing)
                FinishNode();
        }
        else if (State != GatherRunState.Closing && Gathered >= _wanted)
        {
            _travel.Cancel();
            Status = $"Gathered {_wanted} more of item {_itemId} at {_collectability}+ — closing up.";
            _afterClose = GatherRunState.Done;
            Enter(GatherRunState.Closing);
        }

        if (State == GatherRunState.Closing)
            return State;

        switch (State)
        {
            case GatherRunState.SwitchingJob: TickJob(); break;
            case GatherRunState.Travelling: TickTravel(); break;
            case GatherRunState.Walking: TickWalk(); break;
            case GatherRunState.Opening: TickOpen(); break;
            case GatherRunState.Working: TickWork(); break;
        }
        return State;
    }

    /// <summary>
    /// A node is worked as a Miner or a Botanist. Travelling first and noticing afterwards wastes
    /// the trip, so the class is settled before anything moves.
    /// </summary>
    private void TickJob()
    {
        var target = _target!;
        if (_world.GearingUp)
        {
            Status = "Saving a gearset: putting on the game's Recommended Gear";
            _phaseStart = _world.UtcNow;   // its own steps have their own waits
            return;
        }
        if (_lastAction != default && _world.GearUpFailure is { Length: > 0 } noGearset)
        {
            Fault(noGearset);
            return;
        }
        if (_world.CurrentClassJob == target.ClassJobId)
        {
            // A collectable needs Collect, which a quest teaches, not a level. Finding that out at the
            // node is a trip wasted — say what is missing before going anywhere.
            if (_collectability > 0
                && _world.WhyCannotUse(CollectableRotation.ActionId(GatherMove.Collect, target.ClassJobId)) is { } noCollect)
            {
                Fault($"item {_itemId} is wanted as a collectable, and {noCollect}");
                return;
            }
            if (!TruthIsOn(target.ClassJobId))
                return;
            Enter(GatherRunState.Travelling);
            return;
        }

        if (_world.UtcNow - _phaseStart > JobWait)
        {
            Fault($"could not switch to class {target.ClassJobId} to work node {target.NodeId}");
            return;
        }

        // Asked once; the rest of the wait is for the game to catch up.
        if (_lastAction != default)
            return;

        _lastAction = _world.UtcNow;
        Status = $"Switching to class {target.ClassJobId}";
        if (!_world.EquipGearsetFor(target.ClassJobId, out var reason))
            Fault(reason.Length > 0 ? reason : $"no gearset for class {target.ClassJobId}");
    }

    /// <summary>
    /// See that Truth is on before anything moves: without it a timed node is never there to walk
    /// to, and the run goes round its spots finding nothing. True when it is on, when the character
    /// has not learned it, or when it would not go on — which is said, and not a reason to stop.
    /// </summary>
    private bool TruthIsOn(uint classJobId)
    {
        var (action, status, name) = classJobId == GatheringAbilities.Botanist
            ? (TruthOfForests, TruthOfForestsStatus, "Truth of Forests")
            : (TruthOfMountains, TruthOfMountainsStatus, "Truth of Mountains");
        if (_world.HasStatus(status) || _world.WhyCannotUse(action) is not null)
            return true;

        var now = _world.UtcNow;
        if (_truthSince == default)
        {
            _truthSince = now;
            _world.Log($"{name} is off — putting it on, so timed and hidden nodes show.");
        }
        else if (now - _truthSince > TruthWait)
        {
            _world.Log($"{name} would not go on ({_world.Conditions}) — going on without it; timed and hidden nodes will not show.");
            return true;
        }

        Status = $"Putting {name} on";
        if (_world.IsMounted)
        {
            if (now - _lastAction > DismountRetry)
            {
                _lastAction = now;
                _world.Dismount();
            }
            return false;
        }
        if (_world.IsOccupied || now - _truthPressed < TruthRetry)
            return false;
        if (_world.UseAction(action))
            _truthPressed = now;
        return false;
    }

    private void TickTravel()
    {
        var target = _target!;
        if (_world.TerritoryId == target.TerritoryId)
        {
            _travel.Cancel();
            Enter(GatherRunState.Walking);
            return;
        }

        Status = $"Travelling to {Zones.Describe(target.TerritoryId)}";
        if (_travel.Status != TravelStatus.Running)
            Walk(target.Spawns[0], target.TerritoryId);

        if (_travel.Tick() == TravelStatus.Failed)
            Fault($"could not reach {Zones.Describe(target.TerritoryId)}: {_travel.FailReason}");
    }

    private void TickWalk()
    {
        var target = _target!;

        // What is actually up, of everything that yields this item. Any live node beats every
        // recorded coordinate: the coordinates only say where nodes *can* be, and most of them are
        // not there at any given moment.
        // An empty list asks for any node at all, so with every node left out there is no asking.
        var looking = _setAside.Count == 0 && _leftUntil.Count == 0
            ? _nodeIds
            : System.Linq.Enumerable.ToList(System.Linq.Enumerable.Where(_nodeIds, id => !_setAside.Contains(id)
                && !(_leftUntil.TryGetValue(id, out var until) && _world.UtcNow < until)));
        if (looking.Count > 0 && _world.NearestLiveNode(looking) is { } live)
        {
            _currentNode = live.NodeId;
            Status = $"Node {live.NodeId}, {live.Distance:F0}y away";

            if (live.Distance <= (live.NodeId == _closeInNode ? CloseReach : Traveller.InteractReach))
            {
                // Reached in the air: the executor is landing — with its blocked-descent reroute —
                // and cancelling it here left a bare dismount press against a descent that never
                // finished, twelve seconds a node, five nodes running.
                if (_world.IsInFlight && _travel.Status == TravelStatus.Running)
                {
                    Status = $"Node {live.NodeId}: landing";
                    _travel.Tick();
                    return;
                }
                BeginOpening(fromShort: false);
                return;
            }

            // Ariadne has finished, short of it: the mesh ends before the node does — a node on a
            // rock or above a lip. Asking again only gets the same answer, and that went on for the
            // whole three-minute walk budget. Try from here once and let the game judge the reach;
            // a node that will not open from here is set aside and the next one gone for.
            if (_travel.Status == TravelStatus.Done && _travelTo == live.Position)
            {
                _world.Log($"Node {live.NodeId}: Ariadne stopped {live.Distance:F1}y from it and goes no closer — trying it from here.");
                BeginOpening(fromShort: true);
                return;
            }

            if (_travel.Status != TravelStatus.Running || _travelTo != live.Position)
            {
                _travelTo = live.Position;
                Walk(live.Position, target.TerritoryId);
            }

            if (_travel.Tick() == TravelStatus.Failed)
                NextSpot($"node {live.NodeId} could not be reached ({_travel.FailReason})", unreachable: _travel.FailReason);
            else if (_world.UtcNow - _phaseStart > MoveWait)
                NextSpot($"node {live.NodeId} took too long to reach");
            return;
        }

        // Nothing up within sight. Walk the circuit of recorded spots to bring more into view.
        var stop = _stops[_spawn % _stops.Count];
        Status = $"Nothing up nearby — stop {_spawn % _stops.Count + 1} of {_stops.Count}";

        // There, or as near as Ariadne goes: the stop is only for looking around.
        if (Vector3.Distance(_world.PlayerPosition, stop.Position) <= Traveller.DefaultStopDistance + Traveller.ArrivalSlack
            || (_travel.Status == TravelStatus.Done && _travelTo == stop.Position))
        {
            _travel.Cancel();
            NextSpot("nothing up here");
            return;
        }

        if (_travel.Status != TravelStatus.Running || _travelTo != stop.Position)
        {
            _travelTo = stop.Position;
            Walk(stop.Position, target.TerritoryId);
        }

        if (_travel.Tick() == TravelStatus.Failed)
        {
            NextSpot($"could not be reached ({_travel.FailReason})", unreachable: _travel.FailReason);
            return;
        }

        if (_world.UtcNow - _phaseStart > MoveWait)
            NextSpot("took too long to reach");
    }

    private void TickOpen()
    {
        if (_world.NodeOpen)
        {
            Enter(GatherRunState.Working);
            return;
        }

        if (_world.UtcNow - _phaseStart > (_revisit ? RevisitOpenWait : OpenWait))
        {
            _revisit = false;
            var reach = _world.DistanceToDataId(_currentNode);
            if (_fromShort)
                _setAside.Add(_currentNode);
            NextSpot($"would not open after {_opens} attempt(s){(_fromShort ? " from as near as Ariadne goes — set aside for this run" : string.Empty)} — " +
                     $"{(reach is { } d ? $"{d:F1}y away" : "not in the object table")}, " +
                     $"mounted={_world.IsMounted}, last interact returned {_lastOpenAccepted}");
            return;
        }

        // A node cannot be worked from the saddle, and the walk here will have mounted for anything
        // over thirty yalms. Ask, and keep asking while it comes down — a dismount in the air is a
        // descent and you stay mounted the whole way.
        if (_world.IsMounted)
        {
            if (_world.UtcNow - _lastAction > DismountRetry)
            {
                _lastAction = _world.UtcNow;
                _world.Dismount();
            }
            return;
        }

        // Level with it. GatherBuddy requires under three yalms of vertical separation before it
        // will interact, and a node reached from above or below is one the game refuses — which
        // looks from here like an interaction that did nothing.
        if (!_fromShort && _world.PositionOfDataId(_currentNode) is { } where
            && Math.Abs(where.Y - _world.PlayerPosition.Y) >= VerticalReach)
        {
            if (_travel.Status != TravelStatus.Running)
                Walk(where, _target!.TerritoryId);
            _travel.Tick();
            return;
        }

        if (_world.IsOccupied || _world.UtcNow - _lastAction < OpenRetry)
            return;

        if (_opens >= MaxOpens)
            return; // out of tries; the phase clock ends it rather than another keypress

        // Stop, and let the stop land before asking. GatherBuddy is explicit about this — it
        // enqueues the interaction a frame after stopping navigation, "to avoid the 'Unable to
        // execute command while in flight' error" — and stopping and interacting in the same frame
        // is what we were doing instead.
        if (_stoppedAt == default)
        {
            _stoppedAt = _world.UtcNow;
            _world.StopMoving();
            _travel.Cancel();
            return;
        }

        if (_world.UtcNow - _stoppedAt < SettleAfterStopping)
            return;

        _lastAction = _world.UtcNow;
        _opens++;
        _world.FaceDataId(_currentNode);

        if (DryRun)
        {
            // Report and stop. Cycling on would burn the whole circuit in a tenth of a second and
            // end in a fault, which says nothing about the one thing being checked.
            var away = _world.DistanceToDataId(_currentNode);
            var level = _world.PositionOfDataId(_currentNode) is { } at
                ? $"{Math.Abs(at.Y - _world.PlayerPosition.Y):F1}y vertically"
                : "height unknown";
            Status = $"Dry run: node {_currentNode} is reachable and would be opened now.";
            _world.Log($"Dry run: would open node {_currentNode} — " +
                       $"{(away is { } a ? $"{a:F1}y away" : "distance unknown")}, {level}, " +
                       $"mounted={_world.IsMounted}, conditions: {_world.Conditions}");
            _travel.Cancel();
            State = GatherRunState.Done;
            return;
        }

        _world.Log($"Opening node {_currentNode}: {_world.Conditions}");
        _leftAt = default;
        _lastOpenAccepted = _world.TryInteractWithDataId(_currentNode);
    }

    private void TickWork()
    {
        // Opened, which is all this was asked to prove. Write down what the window says and leave.
        if (ProbeOnly && _world.NodeOpen)
        {
            if (!_world.ItemListOpen && _world.Collectable is null)
            {
                if (_world.UtcNow - _phaseStart > WindowWait)
                {
                    _world.Log($"Probe: node {_currentNode} began gathering but no window appeared in " +
                               $"{WindowWait.TotalSeconds:F0}s — {_world.Conditions}");
                    Leave();
                    State = GatherRunState.Done;
                }
                return;
            }

            _world.Log($"Probe: node {_currentNode} opened — {_world.Conditions}");
            _world.DescribeOpenWindow();
            Leave();
            Status = $"Probe: node {_currentNode} opened and was left alone.";
            State = GatherRunState.Done;
            return;
        }

        if (!_world.NodeOpen)
        {
            // The node asked to be the last is finished: no waiting for a Revisit (NextSpot stops).
            if (_stopAfterNode)
            {
                NextSpot("spent");
                return;
            }

            // Opened and shut with no list ever showing: the game refused it. Taking that for a
            // Revisit re-asked it every second from the same spot, for eight minutes on one node.
            if (!_windowSeen)
            {
                ShutBeforeItsWindow();
                return;
            }

            // Revisit — the trait that refreshes a depleted node in place. The window shuts
            // either way, so the node gets a moment to come back before the walk to the next
            // spot: live and within reach again means work it again.
            var now = _world.UtcNow;
            if (_spentAt == default)
            {
                _spentAt = now;
                return;
            }
            if (_world.NearestLiveNode(new[] { _currentNode }) is { } back && back.Distance <= Traveller.InteractReach)
            {
                _world.Log($"Node {_currentNode} came back (Revisit) — working it again.");
                _spentAt = default;
                _revisit = true;
                _fromShort = false;
                _windowSeen = false;
                _opens = 0;
                _stoppedAt = default;
                _slotChosenAt = default;
                _plainPressed = false;
                _plainPressTook = false;
                _yieldAsked = false;
                _world.ForgetSlotAttempts();
                _fallback = null;
                Enter(GatherRunState.Opening);
                return;
            }
            if (now - _spentAt < RevisitGrace)
                return;
            _spentAt = default;
            NextSpot("spent");
            return;
        }

        if (!_windowSeen && (_world.ItemListOpen || _world.ExecutingAction || _world.Collectable is not null))
        {
            _windowSeen = true;
            _closeInNode = 0;
        }

        // An action playing is the node's business, whatever the list is doing meanwhile — on a
        // plain node it is also the proof the last row press took.
        if (_world.ExecutingAction)
        {
            if (_collectability <= 0)
                _plainPressTook = true;
            return;
        }

        // The collectable window is up: work it.
        if (_world.Collectable is { } reported)
        {
            if (_world.ExecutingAction || _world.UtcNow - _lastAction < ActionGap)
                return;

            // Target and floor are both the band asked for — GatherBuddy's own delivery rule:
            // never bank below it, never spend integrity chasing past it.
            // Only what the character has learned: Meticulous and Scrutiny come with levels and quests,
            // and a press of an unlearned action does nothing but cost the frame.
            var job = _target!.ClassJobId;
            var state = reported with
            {
                Target = _collectability, Minimum = _collectability, GpReserve = GpReserve, ScrutinyUsed = _scrutinyUsed,
                MeticulousYield = _world.WhyCannotUse(CollectableRotation.ActionId(GatherMove.Meticulous, job)) is null ? reported.MeticulousYield : 0,
                ScrutinyKnown = _world.WhyCannotUse(CollectableRotation.ActionId(GatherMove.Scrutiny, job)) is null,
            };
            var move = CollectableRotation.Next(state);
            Status = $"Node {_currentNode}: {state.Collectability} of {_collectability}, {state.IntegrityLeft} left, {move}";

            _lastAction = _world.UtcNow;
            // The window does not say whether Scrutiny is up, so remember it. It improves the *next*
            // raise and is spent by it, so only Scrutiny itself leaves it standing.
            _scrutinyUsed = move == GatherMove.Scrutiny;

            var action = CollectableRotation.ActionId(move, _target!.ClassJobId);
            if (action != 0 && !_world.UseAction(action))
                _world.Log($"Gathering action {action} ({move}) was refused.");
            return;
        }

        // Gathering has begun and no window is up yet. The game takes a moment over this, and the
        // one thing that must not happen is walking away in the middle of it: the character is left
        // in a gathering that never finishes, which needs the client killed to escape.
        if (!_world.ItemListOpen)
        {
            // On a plain node the list hides while each gather plays, so the wait is measured
            // from the last press, not from the node opening — and nothing is closed while an
            // action is still running: that is the walk-away that locks the client.
            var since = _plainPressed ? _lastAction : _phaseStart;
            if (_world.UtcNow - since > WindowWait)
            {
                if (_world.ExecutingAction)
                    return;
                Leave();
                NextSpot($"gathering began but no window appeared in {WindowWait.TotalSeconds:F0}s");
            }
            return;
        }

        // A plain item: each press of its row gathers one, and no collectable window will
        // ever open. Keep pressing while the list stands and the count is short; the node
        // closes itself when its attempts are spent, and the outer check closes up when the
        // bag holds enough.
        if (_collectability <= 0 || _fallback is not null)
        {
            // The gathering action playing is the proof a press took — a successful or a
            // failed roll both play it. The world's once-per-item guard on the row exists for
            // collectables, where one press opens a window; here it is lifted between presses.
            if (_world.UtcNow - _lastAction < ActionGap)
                return;
            if (_plainPressed && !_plainPressTook)
            {
                if (_world.UtcNow - _lastAction < PlainPressGrace)
                    return;
                Leave();
                NextSpot($"the row for item {_itemId} did not respond");
                return;
            }
            FinishOnCrystals();
            if (IsCrystal(_fallback ?? _itemId) && CrystalBuff() is var (crystalBuff, crystalName))
            {
                _lastAction = _world.UtcNow;
                _crystalBuffsPressed.Add(crystalBuff);
                if (!_world.UseAction(crystalBuff))
                    _world.Log($"{crystalName} was refused — gathering without it.");
                return;
            }
            if (!_plainPressed && !_yieldAsked && _fallback is null && _timedNodes.Contains(_currentNode))
            {
                _yieldAsked = true;
                if (YieldBuff() is var (buff, name, cost))
                {
                    _lastAction = _world.UtcNow;
                    var gp = _world.Gp;
                    if (_world.UseAction(buff))
                        _world.Log($"Timed node {_currentNode}: {name} ({cost} of {gp} GP).");
                    else
                        _world.Log($"{name} was refused — gathering without it.");
                    return;
                }
            }
            if (_plainPressed)
                _world.ForgetSlotAttempts();
            _lastAction = _world.UtcNow;
            _plainPressTook = false;
            _crystalBuffsPressed.Clear();
            if (_world.SelectSlotFor(_fallback ?? _itemId))
            {
                _plainPressed = true;
                Status = _finishing
                    ? $"Node {_currentNode}: {Gathered} of {_wanted} gathered — finishing the node on item {_fallback ?? _itemId}."
                    : _fallback is { } spare
                        ? $"Node {_currentNode}: item {_itemId} is gone — gathering item {spare} with what is left."
                        : $"Node {_currentNode}: {Gathered} of {_wanted} gathered.";
            }
            else if (TryFallback())
            {
                _plainPressed = true;
                Status = _finishing
                    ? $"Node {_currentNode}: {Gathered} of {_wanted} gathered — finishing the node on item {_fallback}."
                    : $"Node {_currentNode}: item {_itemId} is gone — gathering item {_fallback} with what is left.";
            }
            else
            {
                Leave();
                NextSpot($"has no item {_fallback ?? _itemId} to gather");
            }
            return;
        }

        // Already asked for the row: the collectable window is what says whether it took.
        if (_slotChosenAt != default)
        {
            if (_world.UtcNow - _slotChosenAt > WindowWait)
            {
                Leave();
                NextSpot($"chose the row for item {_itemId} but the collectable window did not open");
            }
            return;
        }

        // The reveal animation when a node opens sets ExecutingGatheringAction, and a callback
        // fired into it is swallowed silently — which is exactly what the probe logged every time:
        // "opened — Gathering ExecutingGatheringAction". The one build that got through fired so
        // often that a late attempt happened to land after the animation. Wait it out instead.
        if (_world.ExecutingAction || _world.UtcNow - _lastAction < ActionGap)
            return;

        _lastAction = _world.UtcNow;
        if (_world.SelectSlotFor(_itemId))
            _slotChosenAt = _world.UtcNow;
        else if (TryFallback())
        {
            _plainPressed = true;
            _plainPressTook = false;
        }
        else
        {
            Leave();
            NextSpot($"has no item {_itemId} to collect");
        }
    }

    /// <summary>The crystal buff to press before the next gather, or null: both on, pressed, not learned, or not the GP.</summary>
    private (uint Action, string Name)? CrystalBuff()
    {
        var botanist = _target!.ClassJobId == GatheringAbilities.Botanist;
        var spare = _world.Gp - GpReserve;
        var land = botanist ? GivingLandBotanist : GivingLandMiner;
        if (spare >= GivingLandCost && !_crystalBuffsPressed.Contains(land) && !_world.HasStatus(GivingLandStatus)
            && _world.WhyCannotUse(land) is null && _world.OffCooldown(land))
            return (land, "The Giving Land");
        var bounty = botanist ? TwelvesBountyBotanist : TwelvesBountyMiner;
        if (spare >= TwelvesBountyCost && !_crystalBuffsPressed.Contains(bounty) && !_world.HasStatus(TwelvesBountyStatus)
            && _world.WhyCannotUse(bounty) is null)
            return (bounty, "The Twelve's Bounty");
        return null;
    }

    /// <summary>The yield buff to press on this node, or null: one already on, none learned, or not the GP.</summary>
    private (uint Action, string Name, int Cost)? YieldBuff()
    {
        if (_world.HasStatus(YieldUpStatus) || _world.HasStatus(YieldUpIIStatus))
            return null;
        var botanist = _target!.ClassJobId == GatheringAbilities.Botanist;
        var spare = _world.Gp - GpReserve;
        var (two, one) = botanist ? (BlessedHarvestII, BlessedHarvest) : (KingsYieldII, KingsYield);
        if (spare >= YieldIICost && _world.WhyCannotUse(two) is null)
            return (two, botanist ? "Blessed Harvest II" : "King's Yield II", YieldIICost);
        if (spare >= YieldCost && _world.WhyCannotUse(one) is null)
            return (one, botanist ? "Blessed Harvest" : "King's Yield", YieldCost);
        return null;
    }

    /// <summary>
    /// See the gathering window shut before reporting anything. The game refuses a close while an
    /// action plays and refuses a teleport while the window stands, so the order is: wait out the
    /// action, ask the window to close, wait until it has.
    /// </summary>
    private void TickClosing()
    {
        if (!_world.NodeOpen && !_world.ExecutingAction && !_world.IsOccupied)
        {
            State = _afterClose;
            if (_afterClose == GatherRunState.Done)
                Status = $"Gathered {_wanted} more of item {_itemId} at {_collectability}+ — done.";
            return;
        }

        // Not forever: past the wait, report anyway and let the caller's travel say what it hits.
        if (_world.UtcNow - _phaseStart > CloseWait)
        {
            _world.Log("The gathering window would not close; carrying on regardless.");
            State = _afterClose;
            return;
        }

        if (_world.ExecutingAction)
            return;

        if (_world.NodeOpen && _world.UtcNow - _lastAction > DismountRetry)
        {
            _lastAction = _world.UtcNow;
            Leave();
        }
    }

    /// <summary>Ask the node's window to shut, and watch a while for the game asking whether to.</summary>
    private void Leave()
    {
        _leftAt = _world.UtcNow;
        _world.CloseNode();
    }

    /// <summary>How long after leaving a node its "leave anyway?" prompt is looked for.</summary>
    private static readonly TimeSpan LeavePromptWait = TimeSpan.FromSeconds(15);

    private DateTime _leftAt;
    private DateTime _confirmedAt;

    /// <summary>Nodes move as they are worked, so the next spot is expected, not a failure.</summary>
    private void NextSpot(string why, string? unreachable = null)
    {
        if (_stopAfterNode)
        {
            _world.Log($"Node {_currentNode}: {why}; stopping there, as asked.");
            StopAsAsked();
            return;
        }

        _travel.Cancel();
        var was = _stops[_spawn % _stops.Count];
        var index = _spawn % _stops.Count + 1;
        _spawn++;

        // The twice-round limit is for laps that find nothing. A visit that filled the bag some
        // is progress: four hundred Cotton Boll is more than two laps of twelve nodes, and
        // counting every visit gave up at 186 with the nodes still giving.
        var held = _world.CollectableCount(_itemId, _collectability);
        if (!_timedOnly && held > _heldAtLastSpot)
            _visited = 0;
        _heldAtLastSpot = held;
        _visited++;
        _world.Log($"Node {was.NodeId}, stop {index}: {why}; trying the next of {_stops.Count}.");

        // Every spot in a row with no way there: the zone's mesh, not the nodes. Said once,
        // after one lap, rather than as "worked twice without filling the bag".
        if (unreachable is null)
        {
            _unreachableRun = 0;
        }
        else
        {
            _unreachableRun++;
            _lastUnreachable = unreachable;
            if (_unreachableRun >= _stops.Count)
            {
                NoWayInZone = true;
                Fault($"Ariadne found no way to any of the {_stops.Count} spots for item {_itemId} in {Zones.Describe(_target!.TerritoryId)} " +
                      $"(last: {_lastUnreachable}) — the problem is Ariadne's mesh for this zone, not the nodes");
                return;
            }
        }

        // A timed node is one node at one of its spots, and it does not come back once worked: after
        // looking at each spot once, walking the circuit again only chains the same empty spots.
        if (_timedOnly && _visited >= _stops.Count)
        {
            Fault($"the timed node for item {_itemId} was not up at any of its {_stops.Count} spots — worked already this window");
            return;
        }

        // Twice round every stop with nothing to show is not something more walking will fix.
        if (_visited > _stops.Count * 2)
        {
            Fault($"worked all {_stops.Count} stops for item {_itemId} twice without filling the bag");
            return;
        }
        Enter(GatherRunState.Walking);
    }

    /// <summary>
    /// A leg of the circuit. Fly set, because travel only flies where the zone's aether currents are
    /// done, so this is "fly if you can, walk if you cannot", and the node circuit is exactly the
    /// travel it pays off on. Land set: the traveller's landing owns the descent, including the
    /// reroute for a descent that never touches down, and the node is worked from the ground.
    /// </summary>
    private void Walk(Vector3 to, uint territory) => _travel.Begin(territory, to, fly: true, land: true);

    private void ShutBeforeItsWindow()
    {
        var away = _world.DistanceToDataId(_currentNode);
        var level = _world.PositionOfDataId(_currentNode) is { } at ? $", {Math.Abs(at.Y - _world.PlayerPosition.Y):F1}y vertically" : string.Empty;
        var where = $"{(away is { } d ? $"{d:F1}y away" : "not in the object table")}{level}";

        if (!_fromShort && _closeInNode != _currentNode)
        {
            _world.Log($"Node {_currentNode} shut before its window showed ({where}) — not near enough for the game; closing in.");
            _closeInNode = _currentNode;
            _travel.Cancel();
            Enter(GatherRunState.Walking);
            return;
        }

        if (_fromShort)
            _setAside.Add(_currentNode);
        else
            _leftUntil[_currentNode] = _world.UtcNow + LeaveRefusingNode;
        _closeInNode = 0;
        NextSpot($"opened and shut before its window showed ({where}) " +
                 (_fromShort ? "from as near as Ariadne goes — set aside for this run" : $"even close up — left for {LeaveRefusingNode.TotalMinutes:F0} minutes"));
    }

    private void BeginOpening(bool fromShort)
    {
        _travel.Cancel();
        _fromShort = fromShort;
        _windowSeen = false;
        _opens = 0;
        _stoppedAt = default;
        _slotChosenAt = default;
        _plainPressed = false;
        _plainPressTook = false;
        _yieldAsked = false;
        _revisit = false;
        _spentAt = default;
        _world.ForgetSlotAttempts();
        _fallback = null;
        Enter(GatherRunState.Opening);
    }

    private void Enter(GatherRunState state)
    {
        State = state;
        _phaseStart = _world.UtcNow;
        _lastAction = default;
    }

    private void Fault(string reason)
    {
        _travel.Cancel();
        FailReason = reason;
        Status = reason;
        State = GatherRunState.Faulted;
    }
}
