using System;
using System.Collections.Generic;
using System.Numerics;
using Demeter.Services.Travel;

namespace Demeter.Services.Fishing;

/// <summary>A place to fish from, as a caller picked it: where to stand, and which way to face.</summary>
public sealed class StandPointRequest
{
    public float X { get; set; }
    public float Y { get; set; }
    public float Z { get; set; }

    /// <summary>The way to face, in the game's yaw (radians; zero is south, anticlockwise).</summary>
    public float Facing { get; set; }

    [System.Text.Json.Serialization.JsonIgnore]
    public Vector3 Position => new(X, Y, Z);
}

/// <summary>What a caller asks of <c>Demeter.Fish.StandAt</c>. camelCase JSON.</summary>
public sealed class FishStandRequest
{
    public string RequestedBy { get; set; } = string.Empty;

    /// <summary>Tried in order: a point that cannot be reached gives way to the next.</summary>
    public List<StandPointRequest> Points { get; set; } = [];
}

public enum FishStandState { Idle, Walking, Facing, Stood, Faulted }

/// <summary>
/// Walk to a place another plugin chose and face the water — <c>Demeter.Fish.StandAt</c>. The caller
/// then asks <c>Demeter.Fish.Start</c>, whose first cast is the water test.
///
/// <para>
/// HoleApproach without the hole: the current zone only, Ariadne to the point and feet on the ground,
/// never a teleport (not even to an aetheryte in the zone), no class switch, no test cast, and
/// nothing remembered — a remembered best point would pull every run back to the same spot, which is
/// what Selene exists to avoid. Short ways are walked; past <see cref="Traveller.MountWorthDistance"/>
/// it may ride or fly, as the zone allows. A point Ariadne cannot reach gives way to the next.
/// </para>
/// </summary>
public sealed class FishStand
{
    private static readonly TimeSpan FaceSettle = TimeSpan.FromMilliseconds(400);

    private readonly IHoleWorld _world;
    private readonly ITravel _travel;
    private readonly Func<uint> _territory;
    private readonly Func<Vector3> _position;

    private FishStandRequest _request = new();
    private int _index;
    private DateTime _phaseStart;

    public FishStand(IHoleWorld world, ITravel travel, Func<uint> territory, Func<Vector3> position)
    {
        _world = world;
        _travel = travel;
        _territory = territory;
        _position = position;
    }

    public FishStandState State { get; private set; } = FishStandState.Idle;
    public FishSessionCode Code { get; private set; }
    public string Reason { get; private set; } = string.Empty;
    public string RequestedBy => _request.RequestedBy;
    public bool Busy => State is FishStandState.Walking or FishStandState.Facing;

    /// <summary>The point stood at (or being walked to), 1-based; 0 before any.</summary>
    public int Point => State == FishStandState.Idle ? 0 : _index + 1;

    /// <summary>What is in the way; <see cref="FishSessionCode.None"/> when nothing is.</summary>
    public (FishSessionCode Code, string Reason) Check(FishStandRequest request)
    {
        if (request.Points.Count == 0)
            return (FishSessionCode.BadRequest, "no point to stand at");
        foreach (var p in request.Points)
            if (!float.IsFinite(p.X) || !float.IsFinite(p.Y) || !float.IsFinite(p.Z) || !float.IsFinite(p.Facing))
                return (FishSessionCode.BadRequest, "a point is not a number");
        if (_world.CurrentClassJob != HoleApproach.Fisher)
            return (FishSessionCode.NotFisher, "Fisher is not on");
        return (FishSessionCode.None, string.Empty);
    }

    public bool Begin(FishStandRequest request)
    {
        _request = request;
        var (code, reason) = Check(request);
        if (code != FishSessionCode.None)
        {
            Fault(code, reason);
            return false;
        }
        _index = 0;
        Code = FishSessionCode.None;
        _world.Log($"Standing to fish for {(request.RequestedBy.Length > 0 ? request.RequestedBy : "a caller")}: {request.Points.Count} point(s) to try.");
        Walk();
        return true;
    }

    public void Cancel()
    {
        if (!Busy)
            return;
        _travel.Cancel();
        Code = FishSessionCode.None;
        Reason = "stopped";
        State = FishStandState.Idle;
        _world.Log("Standing to fish: stopped.");
    }

    public FishStandState Tick()
    {
        switch (State)
        {
            case FishStandState.Walking:
                switch (_travel.Tick())
                {
                    case TravelStatus.Done:
                        _world.Face(_request.Points[_index].Facing);
                        Enter(FishStandState.Facing, $"facing the water at point {_index + 1}");
                        break;
                    case TravelStatus.Failed when _travel.Failure == TravelFailure.Point:
                        _world.Log($"Stand point {_index + 1} could not be reached ({_travel.FailReason}).");
                        if (++_index < _request.Points.Count)
                            Walk();
                        else
                            Fault(FishSessionCode.CannotReach, $"none of the {_request.Points.Count} point(s) could be reached — {_travel.FailReason}");
                        break;
                    case TravelStatus.Failed:
                        Fault(FishSessionCode.CannotReach, $"could not get there — {_travel.FailReason}");
                        break;
                }
                break;
            case FishStandState.Facing:
                if (_world.IsMounted || _world.IsOccupied || _world.UtcNow - _phaseStart < FaceSettle)
                    break;
                var at = _request.Points[_index];
                Enter(FishStandState.Stood, $"standing at point {_index + 1}, facing the water");
                _world.Log($"Standing at ({at.X:F1}, {at.Y:F1}, {at.Z:F1}), facing {at.Facing:F2}.");
                break;
        }
        return State;
    }

    private void Walk()
    {
        var to = _request.Points[_index].Position;
        var far = Vector3.Distance(_position(), to) > Traveller.MountWorthDistance;
        _travel.Begin(_territory(), to, fly: far, land: true, teleport: false);
        Enter(FishStandState.Walking, $"walking to point {_index + 1} of {_request.Points.Count}");
    }

    private void Enter(FishStandState state, string reason)
    {
        State = state;
        Reason = reason;
        _phaseStart = _world.UtcNow;
    }

    private void Fault(FishSessionCode code, string reason)
    {
        _travel.Cancel();
        Code = code;
        Reason = reason;
        State = FishStandState.Faulted;
        _world.Log($"Standing to fish failed: {reason}");
    }
}
