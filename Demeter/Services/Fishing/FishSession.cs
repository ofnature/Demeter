using System;
using System.Collections.Generic;
using System.Linq;

namespace Demeter.Services.Fishing;

/// <summary>What a caller asks of <c>Demeter.Fish.Start</c>. camelCase JSON; unknown fields are ignored.</summary>
public sealed class FishSessionRequest
{
    /// <summary>Who asked, for the log.</summary>
    public string RequestedBy { get; set; } = string.Empty;

    /// <summary>The caller's mission, for the log only so far.</summary>
    public uint? MissionId { get; set; }

    /// <summary>The fish worth hooking; empty hooks every bite.</summary>
    public List<uint> Wanted { get; set; } = [];

    /// <summary>The bait the caller expects on the line; checked, never changed. 0 or none: whatever is on.</summary>
    public uint? Bait { get; set; }

    /// <summary>No new cast after this many; 0 for no limit.</summary>
    public int CastBudget { get; set; }

    /// <summary>From the start: no new cast after this, and a line still waiting for a bite is reeled in. 0 for no limit.</summary>
    public double TimeBudgetSeconds { get; set; }

    public FishPaceRequest? Pace { get; set; }
}

public enum FishSessionState { Idle, Running, Finishing, Done, StoppedAsAsked, Faulted }

/// <summary>Why a session ended or was refused. Sent as camelCase text; extend-only.</summary>
public enum FishSessionCode
{
    None, BadRequest, Off, Busy, NotFisher, NoBait, CannotFishHere, LineStuck, Stalled, Amiss, CastBudget, TimeBudget,
}

/// <summary>The last bite: when it came, how it tugged, and whether it was hooked.</summary>
public sealed record FishBite(double Seconds, Tug Tug, bool Hooked);

/// <summary>
/// Fish from where the character stands, with the bait already on, until told to stop — the shape
/// another plugin needs when it has chosen the place and runs its own clock (Selene's Cosmic
/// Exploration missions).
///
/// <para>
/// Nothing here travels, changes bait, toggles Collect or moochs: a Cosmic mission's bait is set in
/// the game's own Special Bait &amp; Tackle window, and a plain Hook on a plain cast draws no
/// Inefficient Hooking (seen 2026-11-14). Every bite is hooked unless the caller named the fish it
/// wants, in which case <see cref="BiteJudge"/> lets go of a bite whose tug no wanted fish bites with.
/// </para>
///
/// <para>
/// It ends at the caller's <see cref="Stop"/> (the cast in hand first), at either budget, when the
/// fish "sense something amiss" (the spot is fished out), or when the bait runs out — each with its
/// own <see cref="FishSessionCode"/>, so the caller knows whether to move. The first cast is the water
/// test: if it does not go in, the session faults <see cref="FishSessionCode.CannotFishHere"/>.
/// </para>
///
/// <para>
/// Every wait comes from <see cref="FishPace"/>: the hook after a bite, the next cast, a longer pause
/// every so many casts, and the gap between presses. Nothing is pressed at a fixed rhythm.
/// </para>
/// </summary>
public sealed class FishSession
{
    /// <summary>A pressed cast that has not gone in by then never will: no water, or nothing to cast with.</summary>
    public static readonly TimeSpan CastTake = TimeSpan.FromSeconds(5);

    /// <summary>No state change, and no press of ours, for this long is a session stuck without saying.</summary>
    public static readonly TimeSpan Stall = TimeSpan.FromMinutes(3);

    private static readonly TimeSpan QuitWait = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan QuitGap = TimeSpan.FromSeconds(1.5);

    private readonly IFisherWorld _world;
    private readonly Func<bool> _isFisher;
    private readonly FishCatalog _fish;
    private readonly Func<uint, string> _nameOf;

    private FishSessionRequest _request = new();
    private FishPace _pace = new(null);
    private HashSet<uint> _wanted = [];
    private readonly Dictionary<uint, int> _landed = new();
    private DateTime _started;
    private DateTime _nextCastAt;
    private DateTime _gapUntil;
    private DateTime? _castPressedAt;
    private DateTime? _hookAt;
    private DateTime _lineIn;
    private bool _lineInSeen;
    private bool _judged;
    private bool _promptAnswered;
    private int _castsSincePause;
    private int _pauseAfter;
    private FishState _lastState;
    private DateTime _lastChange;
    private DateTime _phaseStart;
    private DateTime _lastQuit;
    private FishSessionState _endState;

    public FishSession(IFisherWorld world, Func<bool> isFisher, FishCatalog fish, Func<uint, string> nameOf)
    {
        _world = world;
        _isFisher = isFisher;
        _fish = fish;
        _nameOf = nameOf;
    }

    public FishSessionState State { get; private set; } = FishSessionState.Idle;
    public FishSessionCode Code { get; private set; }
    public string Reason { get; private set; } = string.Empty;
    public string RequestedBy => _request.RequestedBy;
    public bool Busy => State is FishSessionState.Running or FishSessionState.Finishing;

    /// <summary>Casts that went in this session.</summary>
    public int Casts { get; private set; }
    public IReadOnlyDictionary<uint, int> Landed => _landed;
    public FishBite? LastBite { get; private set; }
    public bool AmissSeen { get; private set; }
    public bool StopAsked { get; private set; }

    /// <summary>What is in the way of fishing here for this request; <see cref="FishSessionCode.None"/> when nothing is.</summary>
    public (FishSessionCode Code, string Reason) Check(FishSessionRequest request)
    {
        if (FishPace.Problem(request.Pace) is { } pace)
            return (FishSessionCode.BadRequest, pace);
        if (request.CastBudget < 0 || request.TimeBudgetSeconds < 0)
            return (FishSessionCode.BadRequest, "a budget cannot be negative");
        if (!_isFisher())
            return (FishSessionCode.NotFisher, "Fisher is not on");
        var bait = _world.CurrentBait;
        if (bait == 0)
            return (FishSessionCode.NoBait, "no bait is on the line");
        if (request.Bait is { } asked and not 0 && asked != bait)
            return (FishSessionCode.NoBait, $"{Name(asked)} was asked for, and {Name(bait)} is on");
        if (_world.ItemCount(bait) <= 0)
            return (FishSessionCode.NoBait, $"no {Name(bait)} left");
        return (FishSessionCode.None, string.Empty);
    }

    /// <summary>Start fishing here. False, with the reason in <see cref="Code"/> and <see cref="Reason"/>, when it cannot.</summary>
    public bool Begin(FishSessionRequest request)
    {
        _request = request;
        _landed.Clear();
        Casts = 0;
        LastBite = null;
        AmissSeen = false;
        StopAsked = false;
        var (code, reason) = Check(request);
        if (code != FishSessionCode.None)
        {
            Fault(code, reason);
            return false;
        }

        var now = _world.UtcNow;
        _pace = new FishPace(request.Pace);
        _wanted = [.. request.Wanted];
        _started = now;
        _castPressedAt = null;
        _hookAt = null;
        _lineInSeen = false;
        _judged = false;
        _promptAnswered = false;
        _castsSincePause = 0;
        _pauseAfter = _pace.LongPauseEvery();
        _gapUntil = now;
        _nextCastAt = now + _pace.Recast();
        _lastState = _world.State;
        _lastChange = now;
        Code = FishSessionCode.None;
        Reason = "fishing";
        State = FishSessionState.Running;

        var who = request.RequestedBy.Length > 0 ? request.RequestedBy : "a caller";
        var wanted = _wanted.Count == 0 ? "every bite hooked" : "wanted: " + string.Join(", ", _wanted.Select(Name));
        var budgets = (request.CastBudget > 0 ? $", {request.CastBudget} casts" : string.Empty)
                      + (request.TimeBudgetSeconds > 0 ? $", {request.TimeBudgetSeconds:F0} s" : string.Empty);
        _world.Log($"Fishing here for {who}{(request.MissionId is { } m ? $" (mission {m})" : string.Empty)} with {Name(_world.CurrentBait)} — {wanted}{budgets}; pace seed {_pace.Seed}.");
        return true;
    }

    /// <summary>The caller's stop: the cast in hand is judged and a hooked fish landed, then the line comes in.</summary>
    public void Stop()
    {
        if (State != FishSessionState.Running || StopAsked)
            return;
        StopAsked = true;
        _world.Log("Stopping after this cast, as asked.");
    }

    /// <summary>Stop now, the line reeled in whatever is on it — the player's Stop, or Demeter switched off.</summary>
    public void Cancel()
    {
        if (!Busy)
            return;
        if (_world.State != FishState.None)
            _world.UseAction(FishingRun.Quit);
        Code = FishSessionCode.None;
        Reason = "stopped from Demeter";
        State = FishSessionState.StoppedAsAsked;
        _world.Log("Fishing here: stopped.");
    }

    /// <summary>The game said "the fish sense something amiss" (LogMessage 3516): this spot is fished out.</summary>
    public void Amiss()
    {
        if (State != FishSessionState.Running || AmissSeen)
            return;
        AmissSeen = true;
        _world.Log("The fish sense something amiss — ending after this cast.");
    }

    public FishSessionState Tick()
    {
        var now = _world.UtcNow;
        switch (State)
        {
            case FishSessionState.Running:
                TickRunning(now);
                break;
            case FishSessionState.Finishing:
                if (_world.State == FishState.None && !_world.IsOccupied)
                {
                    State = _endState;
                    break;
                }
                if (now - _phaseStart > QuitWait)
                {
                    Fault(FishSessionCode.LineStuck, "the line would not come in — reel it in by hand");
                    break;
                }
                if (now - _lastQuit > QuitGap && _world.State is FishState.PoleReady or FishState.LineInWater)
                {
                    _lastQuit = now;
                    _world.UseAction(FishingRun.Quit);
                }
                break;
        }
        return State;
    }

    private void TickRunning(DateTime now)
    {
        foreach (var c in _world.TakeCatches())
        {
            _landed[c.ItemId] = _landed.GetValueOrDefault(c.ItemId) + c.Amount;
            _world.Log($"Landed {Name(c.ItemId)}{(c.Amount > 1 ? $" ×{c.Amount}" : string.Empty)}.");
        }

        var state = _world.State;
        var ready = state is FishState.None or FishState.PoleReady;
        if (state != _lastState)
        {
            // The pressed cast has gone in.
            if (!ready && _castPressedAt is not null)
            {
                _castPressedAt = null;
                Casts++;
                _castsSincePause++;
            }
            // The line is free again: the next cast waits its drawn time, longer every so often.
            if (ready && !(_lastState is FishState.None or FishState.PoleReady))
                PlanNextCast(now);
            _lastState = state;
            _lastChange = now;
        }
        else if (now - _lastChange > Stall && state != FishState.LineInWater)
        {
            Fault(FishSessionCode.Stalled, $"fishing has been in {state} for {Stall.TotalMinutes:F0} minutes");
            return;
        }

        var outOfTime = _request.TimeBudgetSeconds > 0 && (now - _started).TotalSeconds >= _request.TimeBudgetSeconds;
        switch (state)
        {
            case FishState.LineInWater:
                if (!_lineInSeen)
                {
                    _lineInSeen = true;
                    _lineIn = now;
                    _judged = false;
                }
                // Out of time with nothing on the line: no point waiting for a bite.
                if (outOfTime)
                    Finish(FishSessionCode.TimeBudget, $"the {_request.TimeBudgetSeconds:F0} s budget is spent", FishSessionState.Done);
                break;

            case FishState.Bite:
                if (!_judged)
                {
                    _judged = true;
                    Judge(now);
                }
                if (_hookAt is { } at && now >= at)
                {
                    _hookAt = null;
                    _gapUntil = now + _pace.ActionGap();
                    _world.UseAction(FishingRun.Hook);
                }
                break;

            case FishState.ConfirmingCollectable:
                // Collect is the player's, here; a collectable is never thrown back.
                if (!_promptAnswered && _world.CollectablePrompt is not null)
                {
                    _promptAnswered = true;
                    _world.AnswerCollectable(true);
                }
                break;

            case FishState.None:
            case FishState.PoleReady:
                _lineInSeen = false;
                _hookAt = null;
                _promptAnswered = false;
                if (EndNow(now, outOfTime))
                    return;
                if (_world.IsOccupied)
                    break;
                if (_castPressedAt is { } pressed)
                {
                    if (now - pressed > CastTake)
                    {
                        if (Casts == 0)
                            Fault(FishSessionCode.CannotFishHere, "the first cast did not go in — no water here, not facing it, "
                                                                  + "or the mission needs an action not learned (a collectability mission needs Collect)");
                        else
                            Fault(FishSessionCode.Stalled, "a cast did not go in");
                    }
                    break;
                }
                if (now < _nextCastAt || now < _gapUntil)
                    break;
                _castPressedAt = now;
                _gapUntil = now + _pace.ActionGap();
                _lastChange = now;
                _world.UseAction(FishingRun.Cast);
                break;
        }
    }

    /// <summary>With the line free: the reasons to stop, in the order they matter. True when one applied.</summary>
    private bool EndNow(DateTime now, bool outOfTime)
    {
        if (StopAsked)
            Finish(FishSessionCode.None, "stopped as asked", FishSessionState.StoppedAsAsked);
        else if (AmissSeen)
            Finish(FishSessionCode.Amiss, "the fish sense something amiss — this spot is fished out", FishSessionState.Done);
        else if (_request.CastBudget > 0 && Casts >= _request.CastBudget)
            Finish(FishSessionCode.CastBudget, $"{Casts} casts, the budget", FishSessionState.Done);
        else if (outOfTime)
            Finish(FishSessionCode.TimeBudget, $"the {_request.TimeBudgetSeconds:F0} s budget is spent", FishSessionState.Done);
        else if (_castPressedAt is null && (_world.CurrentBait is var bait && bait == 0 || _world.ItemCount(_world.CurrentBait) <= 0))
            Finish(FishSessionCode.NoBait, "the bait has run out", FishSessionState.Done);
        else
            return false;
        return true;
    }

    private void PlanNextCast(DateTime now)
    {
        var wait = _pace.Recast();
        if (_castsSincePause >= _pauseAfter)
        {
            var pause = _pace.LongPause();
            wait += pause;
            _castsSincePause = 0;
            _pauseAfter = _pace.LongPauseEvery();
            _world.Log($"A breather: {pause.TotalSeconds:F1} s before the next cast.");
        }
        _nextCastAt = now + wait;
    }

    private void Judge(DateTime now)
    {
        var seconds = (now - _lineIn).TotalSeconds;
        var tug = _world.Tug;
        bool hook;
        string why;
        if (_wanted.Count == 0)
        {
            hook = true;
            why = $"{seconds:F1}s{(tug == Tug.Unknown ? string.Empty : $" {tug.ToString().ToLowerInvariant()} tug")} — hooking";
        }
        else
        {
            var call = BiteJudge.Judge(_world.CurrentBait, seconds, tug, _world.HasStatus(FishingRun.ChumStatus), _wanted, [],
                id => BiteJudge.TugOf(_fish.ById(id)?.Bite), id => _fish.ById(id)?.Path is { Count: > 0 } road ? road[^1] : null);
            hook = call.Hook;
            why = call.Why;
        }
        LastBite = new FishBite(Math.Round(seconds, 1), tug, hook);
        _world.Log($"Bite: {why}.");
        if (hook)
            _hookAt = now + _pace.HookDelay();
    }

    private void Finish(FishSessionCode code, string why, FishSessionState end)
    {
        Code = code;
        Reason = why;
        _endState = end;
        State = FishSessionState.Finishing;
        _phaseStart = _world.UtcNow;
        _lastQuit = default;
        _world.Log($"Fishing here: {why} — {Casts} casts, {_landed.Values.Sum()} landed.");
    }

    private void Fault(FishSessionCode code, string reason)
    {
        Code = code;
        Reason = reason;
        State = FishSessionState.Faulted;
        _world.Log($"Fishing here stopped: {reason}");
    }

    private string Name(uint id) => _nameOf(id);
}
