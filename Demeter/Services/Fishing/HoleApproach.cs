using System;
using System.Collections.Generic;
using Demeter.Services.Travel;

namespace Demeter.Services.Fishing;

/// <summary>What getting to a fishing hole needs of the game, beyond travel.</summary>
public interface IHoleWorld
{
    DateTime UtcNow { get; }
    bool IsOccupied { get; }
    bool IsMounted { get; }
    uint CurrentClassJob { get; }

    /// <summary>Equip a gearset for this class. False when the character has none, with a reason.</summary>
    bool EquipGearsetFor(uint classJobId, out string reason);

    /// <summary>Turn to face this way (the game's yaw: zero is south, anticlockwise).</summary>
    void Face(float rotation);

    /// <summary>
    /// The game's own verdict on Cast from where the character stands: 0 means it can be cast. The
    /// codes for "no water here" are not verified, so anything non-zero is only logged and the test
    /// cast is what decides.
    /// </summary>
    uint CastStatus { get; }
    bool Cast();
    bool Quit();

    /// <summary>The line is in the water (ConditionFlag.Fishing).</summary>
    bool IsFishing { get; }

    void Log(string message);
}

public enum HoleApproachState { Idle, SwitchingJob, Travelling, Facing, TestCasting, Reeling, Ready, Faulted }

/// <summary>
/// Get to a fishing hole and prove it can be fished from: Fisher on, travel to a stand point (Lifestream
/// into the zone, Ariadne to the spot, feet on the ground), face the water, and make a test cast.
/// The line in the water is the proof; it is reeled in again unless the caller wants to keep fishing.
///
/// <para>
/// Stand points come from <see cref="HoleMemory.Order"/>: what worked here before, then the community
/// records most used first. One that cannot be reached, or where the cast does not take, is
/// remembered as bad and the next is tried — a ledge or a wall at one recorded spot is not the hole
/// being unfishable.
/// </para>
/// </summary>
public sealed class HoleApproach
{
    public const uint Fisher = 18;

    private static readonly TimeSpan JobWait = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan FaceSettle = TimeSpan.FromMilliseconds(400);

    /// <summary>A cast that took shows the line in the water within a couple of seconds.</summary>
    private static readonly TimeSpan CastWait = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan ReelWait = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan Retry = TimeSpan.FromSeconds(1.5);

    /// <summary>Stand points tried before the hole itself is given up on.</summary>
    private const int MaxStands = 4;

    private readonly IHoleWorld _world;
    private readonly ITravel _travel;
    private readonly Action _saveMemory;

    private FishingHole? _hole;
    private HoleMemory? _memory;
    private IReadOnlyList<StandPoint> _stands = [];
    private int _index;
    private int _tried;
    private DateTime _phaseStart;
    private DateTime _lastTry;

    public HoleApproach(IHoleWorld world, ITravel travel, Action saveMemory)
    {
        _world = world;
        _travel = travel;
        _saveMemory = saveMemory;
    }

    public HoleApproachState State { get; private set; } = HoleApproachState.Idle;
    public string Status { get; private set; } = string.Empty;
    public string FailReason { get; private set; } = string.Empty;

    /// <summary>Leave the line in the water once it is proven — the fisher's start. False reels it in.</summary>
    public bool KeepFishing { get; set; }

    /// <summary>The stand point in use, once one has been reached.</summary>
    public StandPoint? Stand => _index < _stands.Count ? _stands[_index] : null;

    public bool Busy => State is not (HoleApproachState.Idle or HoleApproachState.Ready or HoleApproachState.Faulted);

    public void Begin(FishingHole hole, IReadOnlyList<StandPoint> recorded, HoleMemory memory)
    {
        _hole = hole;
        _memory = memory;
        _stands = memory.Order(recorded);
        _index = 0;
        _tried = 0;
        FailReason = string.Empty;
        if (_stands.Count == 0)
        {
            Fault($"{hole.Name}: no stand point is known for this hole (none recorded, or every one failed before)");
            return;
        }
        Enter(HoleApproachState.SwitchingJob);
    }

    public void Cancel()
    {
        _travel.Cancel();
        if (_world.IsFishing)
            _world.Quit();
        State = HoleApproachState.Idle;
        Status = string.Empty;
    }

    public HoleApproachState Tick()
    {
        var now = _world.UtcNow;
        switch (State)
        {
            case HoleApproachState.SwitchingJob: TickJob(now); break;
            case HoleApproachState.Travelling: TickTravel(); break;
            case HoleApproachState.Facing: TickFacing(now); break;
            case HoleApproachState.TestCasting: TickCast(now); break;
            case HoleApproachState.Reeling: TickReel(now); break;
        }
        return State;
    }

    private void TickJob(DateTime now)
    {
        if (_world.CurrentClassJob == Fisher)
        {
            BeginTravel();
            return;
        }
        if (now - _phaseStart > JobWait)
        {
            Fault("could not switch to Fisher");
            return;
        }
        if (_lastTry != default)
            return; // asked once; the rest is the game catching up
        _lastTry = now;
        Status = "Switching to Fisher";
        if (!_world.EquipGearsetFor(Fisher, out var reason))
            Fault(reason.Length > 0 ? reason : "no Fisher gearset — save one, then try again");
    }

    private void BeginTravel()
    {
        var stand = _stands[_index];
        Status = $"{_hole!.Name}: going to stand point {_index + 1} of {_stands.Count}";
        _travel.Begin(_hole.TerritoryId, stand.Position, fly: true, land: true);
        Enter(HoleApproachState.Travelling);
    }

    private void TickTravel()
    {
        switch (_travel.Tick())
        {
            case TravelStatus.Done:
                _world.Face(_stands[_index].Rotation);
                Enter(HoleApproachState.Facing);
                break;
            case TravelStatus.Failed:
                NextStand($"could not be reached ({_travel.FailReason})");
                break;
        }
    }

    private void TickFacing(DateTime now)
    {
        if (_world.IsMounted || _world.IsOccupied || now - _phaseStart < FaceSettle)
            return;
        var status = _world.CastStatus;
        if (status != 0)
            _world.Log($"{_hole!.Name}: Cast reports status {status} from stand point {_index + 1} — trying it anyway.");
        Enter(HoleApproachState.TestCasting);
    }

    private void TickCast(DateTime now)
    {
        if (_world.IsFishing)
        {
            var stand = _stands[_index];
            _memory!.Worked(stand);
            _saveMemory();
            _world.Log($"{_hole!.Name}: fishing works from ({stand.X:F1}, {stand.Y:F1}, {stand.Z:F1}).");
            if (KeepFishing)
            {
                Status = $"{_hole.Name}: line in the water.";
                State = HoleApproachState.Ready;
                return;
            }
            Enter(HoleApproachState.Reeling);
            return;
        }
        if (now - _phaseStart > CastWait)
        {
            NextStand("the test cast did not take (no water in reach, or facing the wrong way)");
            return;
        }
        if (now - _lastTry < Retry || _world.IsOccupied)
            return;
        _lastTry = now;
        Status = $"{_hole!.Name}: test cast";
        _world.Cast();
    }

    private void TickReel(DateTime now)
    {
        if (!_world.IsFishing && !_world.IsOccupied)
        {
            Status = $"{_hole!.Name}: proven — the line came in.";
            State = HoleApproachState.Ready;
            return;
        }
        if (now - _phaseStart > ReelWait)
        {
            Fault("the line would not come in — reel it in by hand");
            return;
        }
        if (now - _lastTry < Retry)
            return;
        _lastTry = now;
        _world.Quit();
    }

    private void NextStand(string why)
    {
        var stand = _stands[_index];
        _memory!.Failed(stand);
        _saveMemory();
        _world.Log($"{_hole!.Name}, stand point {_index + 1}: {why}.");
        _index++;
        _tried++;
        if (_index >= _stands.Count || _tried >= MaxStands)
        {
            Fault($"{_hole.Name}: none of {_tried} stand point(s) worked — {why}");
            return;
        }
        BeginTravel();
    }

    private void Enter(HoleApproachState state)
    {
        State = state;
        _phaseStart = _world.UtcNow;
        _lastTry = default;
    }

    private void Fault(string reason)
    {
        _travel.Cancel();
        FailReason = reason;
        Status = reason;
        State = HoleApproachState.Faulted;
    }
}
