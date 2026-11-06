using System;
using System.Collections.Generic;
using System.Linq;

namespace Demeter.Services.Fishing;

/// <summary>The game's fishing states (ClientStructs <c>FishingState</c>), mirrored so the logic needs no game types.</summary>
public enum FishState
{
    None = 0, CastingOut = 1, PullingPoleIn = 2, Quitting = 3, PoleReady = 4, Bite = 5, Hooking = 6,
    ReleasingCatch = 7, ConfirmingCollectable = 8, AmbitiousLure = 9, ModestLure = 10, Unk11 = 11, LineInWater = 12,
}

/// <summary>A fish landed, as the catch window reported it.</summary>
public readonly record struct Catch(uint ItemId, int Amount, bool Collectable, bool Large);

/// <summary>What the fisher needs of the game.</summary>
public interface IFisherWorld
{
    DateTime UtcNow { get; }
    bool IsOccupied { get; }
    FishState State { get; }

    /// <summary>The tug of the bite in progress; Unknown when it cannot be read.</summary>
    Tug Tug { get; }
    uint CurrentBait { get; }
    int ItemCount(uint itemId);
    bool ChangeBait(uint baitId);

    /// <summary>The last catch can be mooched (Mooch), or the one before it (Mooch II).</summary>
    bool CanMooch { get; }
    bool CanMooch2 { get; }
    bool HasStatus(uint statusId);
    bool UseAction(uint actionId);

    /// <summary>Catches reported since the last call, oldest first.</summary>
    IReadOnlyList<Catch> TakeCatches();

    /// <summary>How many of the item are held at or above a collectability (0 counts every one).</summary>
    int CollectableCount(uint itemId, int minimumCollectability);

    /// <summary>The "keep it as a collectable?" prompt on screen: the fish and its collectability. Null otherwise.</summary>
    (uint ItemId, int Collectability)? CollectablePrompt { get; }

    /// <summary>Yes keeps the fish as a collectable; no lets it go.</summary>
    void AnswerCollectable(bool keep);
    void Log(string message);
}

public enum FishingRunState { Idle, Approaching, Fishing, Finishing, Done, Faulted }

/// <summary>
/// Catch a number of one fish.
///
/// <para>
/// The road comes from the fish data: a bait, then each fish mooched from in turn. The run casts with
/// the bait, judges each bite (<see cref="BiteJudge"/>: when it came and how it tugged, against what
/// bites here), hooks what it wants and lets the rest go, and moochs the moment the last catch is the
/// next fish on the road. A fish that wants Fisher's Intuition gets its predators caught first. It
/// ends when the bag holds enough — counted from the bag, not from the catches, so a target landed
/// while fishing for something else counts too — or when the fish's window closes.
/// </para>
///
/// <para>
/// Only learned actions: Hook and Cast are there from level 1; Mooch at 25 and Mooch II at 63 are
/// asked of the ability catalogue. Patience, Chum, Identical Cast and the precise/powerful hooksets
/// are later options — a plain Hook lands every tug while Patience is not up.
/// </para>
/// </summary>
public sealed class FishingRun
{
    public const uint Cast = 289, Hook = 296, Mooch = 297, Mooch2 = 268, Quit = 299;
    public const uint IntuitionStatus = 568, ChumStatus = 763;

    /// <summary>Fisher's Collect (a toggle, Lv 50, quest "Inscrutable Tastes") and the status it shows while on.</summary>
    public const uint Collect = 4101, CollectStatus = 805;

    private static readonly TimeSpan ActionGap = TimeSpan.FromSeconds(1.5);
    private static readonly TimeSpan BaitWait = TimeSpan.FromSeconds(5);

    /// <summary>No state change for this long is a run that has stopped without saying.</summary>
    private static readonly TimeSpan Stall = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan WindowCheck = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan QuitWait = TimeSpan.FromSeconds(10);

    private readonly IFisherWorld _world;
    private readonly HoleApproach _approach;
    private readonly FishCatalog _fish;
    private readonly FishingHoles _holes;
    private readonly Func<uint, bool> _knows;
    private readonly Func<uint, IReadOnlyList<(uint Weather, int Cumulative)>> _ratesFor;
    private readonly Func<uint, string> _nameOf;

    private FishInfo? _target;
    private FishingHole? _hole;
    private int _wanted;
    private int _baseline;
    private readonly Dictionary<uint, int> _caught = new();
    private uint _lastCatch;
    private uint _mooching;
    private DateTime _lineIn;
    private bool _lineInSeen;
    private bool _judged;
    private DateTime _lastAction;
    private DateTime _lastChange;
    private FishState _lastState;
    private DateTime _lastWindowCheck;
    private DateTime _phaseStart;
    private uint _baitAsked;
    private DateTime _baitAskedAt;
    private int _collectability;
    private bool _promptAnswered;

    public FishingRun(IFisherWorld world, HoleApproach approach, FishCatalog fish, FishingHoles holes,
        Func<uint, bool> knows, Func<uint, IReadOnlyList<(uint Weather, int Cumulative)>> ratesFor, Func<uint, string> nameOf)
    {
        _nameOf = nameOf;
        _world = world;
        _approach = approach;
        _fish = fish;
        _holes = holes;
        _knows = knows;
        _ratesFor = ratesFor;
    }

    public FishingRunState State { get; private set; } = FishingRunState.Idle;
    public string Status { get; private set; } = string.Empty;
    public string FailReason { get; private set; } = string.Empty;
    public bool Busy => State is FishingRunState.Approaching or FishingRunState.Fishing or FishingRunState.Finishing;

    /// <summary>How many more of the target the bag holds than when the run began — at the collectability asked for.</summary>
    public int Gathered => _target is null ? 0 : _world.CollectableCount(_target.Id, _collectability) - _baseline;

    /// <summary>Everything landed this run, by item.</summary>
    public IReadOnlyDictionary<uint, int> Caught => _caught;

    /// <param name="collectability">
    /// Above zero, the fish is wanted as a collectable at least this good (a custom delivery): Collect
    /// goes on, and each catch's prompt keeps the target only when it clears the bar. Zero fishes plainly,
    /// with Collect off so no prompt interrupts.
    /// </param>
    public void Begin(FishInfo target, int count, FishingHole hole, IReadOnlyList<StandPoint> recorded, HoleMemory memory,
        int collectability = 0)
    {
        _target = target;
        _hole = hole;
        _wanted = count;
        _collectability = Math.Max(0, collectability);
        _baseline = _world.CollectableCount(target.Id, _collectability);
        _caught.Clear();
        _lastCatch = 0;
        _mooching = 0;
        _lineInSeen = false;
        FailReason = string.Empty;
        _stopAfterCast = false;
        if (target.IsSpearfish || target.IsOcean)
        {
            Fault($"{Name(target.Id)} is {(target.IsSpearfish ? "spearfished" : "an ocean voyage catch")} — not fished from a hole");
            return;
        }
        if (target.Path is not { Count: > 0 })
        {
            Fault($"the fish data names no bait for {Name(target.Id)}");
            return;
        }
        if (_collectability > 0 && !_knows(Collect))
        {
            Fault($"{Name(target.Id)} is wanted as a collectable, and Collect is not learned (Fisher Lv 50, quest \"Inscrutable Tastes\")");
            return;
        }
        // A road through other fish is mooched; without Mooch learned the target can never come.
        if ((target.Path.Count > 1 || PredatorRoads(target)) && !_knows(Mooch))
        {
            Fault($"{Name(target.Id)} is caught by mooching, and Mooch is not learned yet (Fisher Lv 25)");
            return;
        }
        _approach.KeepFishing = false;
        _approach.Begin(hole, recorded, memory);
        State = _approach.State == HoleApproachState.Faulted ? FishingRunState.Faulted : FishingRunState.Approaching;
        FailReason = _approach.FailReason;
        Status = _approach.Status;
        _phaseStart = _world.UtcNow;
    }

    public void Cancel()
    {
        if (State == FishingRunState.Approaching)
            _approach.Cancel();
        if (State is FishingRunState.Fishing or FishingRunState.Finishing && _world.State != FishState.None)
            _world.UseAction(Quit);
        State = FishingRunState.Idle;
        Status = string.Empty;
    }

    /// <summary>Set by <see cref="StopAfterCast"/>: the cast in hand is the last. Begin clears it.</summary>
    private bool _stopAfterCast;

    /// <summary>
    /// Stop at the end of the cast in hand: its bite is judged and a hooked fish landed, then no new
    /// cast or mooch; the line comes in and the run ends. On the way to the hole it stops as soon as
    /// no test cast is in the water.
    /// </summary>
    public void StopAfterCast()
    {
        if (State is not (FishingRunState.Approaching or FishingRunState.Fishing) || _stopAfterCast)
            return;
        _stopAfterCast = true;
        _world.Log("Stopping after this cast, as asked.");
        if (State == FishingRunState.Approaching)
        {
            _approach.StopWhenFree();
            if (!_approach.Busy)
                StoppedOnTheWay();
        }
    }

    private void StoppedOnTheWay()
    {
        Status = "Stopped on the way to the hole, as asked.";
        State = FishingRunState.Idle;
    }

    public FishingRunState Tick()
    {
        var now = _world.UtcNow;
        switch (State)
        {
            case FishingRunState.Approaching:
                Status = _approach.Status;
                switch (_approach.Tick())
                {
                    case HoleApproachState.Ready or HoleApproachState.Idle when _stopAfterCast:
                        StoppedOnTheWay();
                        break;
                    case HoleApproachState.Ready:
                        State = FishingRunState.Fishing;
                        _lastChange = now;
                        _lastWindowCheck = default;
                        _world.Log($"Fishing {Name(_target!.Id)} ×{_wanted} at {_hole!.Name}.");
                        break;
                    case HoleApproachState.Faulted:
                        Fault(_approach.FailReason);
                        break;
                }
                break;
            case FishingRunState.Fishing:
                TickFishing(now);
                break;
            case FishingRunState.Finishing:
                if (_world.State == FishState.None && !_world.IsOccupied)
                {
                    State = FishingRunState.Done;
                    break;
                }
                if (now - _phaseStart > QuitWait)
                {
                    Fault("the line would not come in — reel it in by hand");
                    break;
                }
                if (now - _lastAction > ActionGap && _world.State is FishState.PoleReady or FishState.LineInWater)
                {
                    _lastAction = now;
                    _world.UseAction(Quit);
                }
                break;
        }
        return State;
    }

    private void TickFishing(DateTime now)
    {
        foreach (var c in _world.TakeCatches())
        {
            _caught[c.ItemId] = _caught.GetValueOrDefault(c.ItemId) + c.Amount;
            _lastCatch = c.ItemId;
            _world.Log($"Landed {Name(c.ItemId)}{(c.Amount > 1 ? $" ×{c.Amount}" : string.Empty)}.");
        }

        var target = _target!;
        if (Gathered >= _wanted)
        {
            Finish($"Caught {_wanted} {Name(target.Id)} — done.");
            return;
        }

        // The fish's window: a timed or weather-bound fish is not worth casting for once it has shut.
        if ((target.IsTimed || target.NeedsWeather) && now - _lastWindowCheck > WindowCheck)
        {
            _lastWindowCheck = now;
            var windows = CatchWindows.Next(target, _ratesFor(_hole!.TerritoryId), now, max: 1, horizon: TimeSpan.FromHours(1));
            if (windows.Count == 0 || !windows[0].Contains(now))
            {
                Finish($"{Name(target.Id)}'s window has closed — {Gathered} of {_wanted} caught.");
                return;
            }
        }

        var state = _world.State;
        if (state != _lastState)
        {
            _lastState = state;
            _lastChange = now;
        }
        else if (now - _lastChange > Stall && state != FishState.LineInWater)
        {
            Fault($"fishing has been in {state} for {Stall.TotalMinutes:F0} minutes");
            return;
        }

        var (immediate, why) = Immediate(target);
        var path = immediate.Path!;
        switch (state)
        {
            case FishState.LineInWater:
                if (!_lineInSeen)
                {
                    _lineInSeen = true;
                    _lineIn = now;
                    _judged = false;
                    Status = $"{why}: line in the water{(_mooching != 0 ? $" (mooching {Name(_mooching)})" : string.Empty)}.";
                }
                break;

            case FishState.Bite:
                if (_judged)
                    break;
                _judged = true;
                var bait = _mooching != 0 ? _mooching : _world.CurrentBait;
                var call = BiteJudge.Judge(bait, (now - _lineIn).TotalSeconds, _world.Tug, _world.HasStatus(ChumStatus),
                    Wanted(target, immediate), _holes.ById(_hole!.Id)?.Bites ?? [], id => BiteJudge.TugOf(_fish.ById(id)?.Bite));
                _world.Log($"Bite: {call.Why}.");
                if (call.Hook)
                {
                    _lastAction = now;
                    _world.UseAction(Hook);
                }
                break;

            case FishState.ConfirmingCollectable:
                if (_promptAnswered || _world.CollectablePrompt is not { } prompt)
                    break;
                _promptAnswered = true;
                // The target only at the collectability asked for; anything else (a fish on the road,
                // a stray) is kept — it costs nothing and may be wanted.
                var keep = prompt.ItemId != target.Id || prompt.Collectability >= _collectability;
                _world.Log($"Collectable {Name(prompt.ItemId)} at {prompt.Collectability}: {(keep ? "kept" : $"let go (below {_collectability})")}.");
                _world.AnswerCollectable(keep);
                break;

            case FishState.None:
            case FishState.PoleReady:
                _lineInSeen = false;
                _promptAnswered = false;
                if (_world.IsOccupied || now - _lastAction < ActionGap)
                    break;
                // The cast asked to be the last is in (a press still in flight has had its gap to show).
                if (_stopAfterCast)
                {
                    Finish($"Stopped after the cast, as asked — {Math.Max(0, Gathered)} of {_wanted} caught.");
                    return;
                }
                NextCast(now, immediate, path, why);
                break;
        }
    }

    /// <summary>Mooch when the last catch is next on the road; else the road's bait on, and cast.</summary>
    private void NextCast(DateTime now, FishInfo immediate, IReadOnlyList<uint> path, string why)
    {
        var step = path.ToList().IndexOf(_lastCatch);
        if (step >= 1 && _lastCatch != immediate.Id && _world.CanMooch && _knows(Mooch))
        {
            _mooching = _lastCatch;
            _lastAction = now;
            Status = $"{why}: mooching {Name(_lastCatch)}.";
            _world.UseAction(Mooch);
            return;
        }

        _mooching = 0;

        // Collect on for a collectable target, off otherwise — so a plain run never meets the prompt.
        var wantCollect = _collectability > 0;
        if (_world.HasStatus(CollectStatus) != wantCollect && (wantCollect ? _knows(Collect) : true))
        {
            _lastAction = now;
            Status = $"{why}: Collect {(wantCollect ? "on" : "off")}.";
            _world.UseAction(Collect);
            return;
        }

        var start = path[0];
        if (_world.CurrentBait != start)
        {
            if (_world.ItemCount(start) <= 0)
            {
                Fault($"no {Name(start)} in the bags for {Name(immediate.Id)} — buy some, then start again");
                return;
            }
            if (_baitAsked != start || now - _baitAskedAt > BaitWait)
            {
                _baitAsked = start;
                _baitAskedAt = now;
                _world.ChangeBait(start);
                Status = $"{why}: putting {Name(start)} on.";
            }
            return;
        }

        _lastAction = now;
        Status = $"{why}: casting with {Name(start)}.";
        _world.UseAction(Cast);
    }

    private bool PredatorRoads(FishInfo target)
        => target.Predators is { } predators && predators.Any(p => _fish.ById((uint)p[0])?.Path is { Count: > 1 });

    /// <summary>What to fish for right now: a predator still owed for Intuition, or the target itself.</summary>
    private (FishInfo Fish, string Why) Immediate(FishInfo target)
    {
        if (target.Predators is { Count: > 0 } predators && !_world.HasStatus(IntuitionStatus))
        {
            foreach (var p in predators)
            {
                var id = (uint)p[0];
                if (_caught.GetValueOrDefault(id) < p[1] && _fish.ById(id) is { Path.Count: > 0 } predator)
                    return (predator, $"Intuition: {Name(id)} {_caught.GetValueOrDefault(id)}/{p[1]}");
            }
        }
        return (target, $"{Name(target.Id)} {Math.Max(0, Gathered)}/{_wanted}");
    }

    /// <summary>The target, the fish on the road to what is being fished for now, and the predators still owed.</summary>
    private HashSet<uint> Wanted(FishInfo target, FishInfo immediate)
    {
        var wanted = new HashSet<uint> { target.Id, immediate.Id };
        foreach (var id in immediate.Path!.Skip(1))
            wanted.Add(id);
        foreach (var id in target.Path!.Skip(1))
            wanted.Add(id);
        if (target.Predators is { } predators)
            foreach (var p in predators)
                if (_caught.GetValueOrDefault((uint)p[0]) < p[1])
                    wanted.Add((uint)p[0]);
        return wanted;
    }

    private void Finish(string why)
    {
        Status = why;
        _world.Log(why);
        State = FishingRunState.Finishing;
        _phaseStart = _world.UtcNow;
    }

    private void Fault(string reason)
    {
        FailReason = reason;
        Status = reason;
        State = FishingRunState.Faulted;
        _world.Log($"Fishing stopped: {reason}");
    }

    private string Name(uint id) => _nameOf(id);
}
