using System;
using System.Collections.Generic;
using System.Linq;

namespace Demeter.Services.Fishing;

/// <summary>What a caller asks of <c>Demeter.Fish.Start</c>. camelCase JSON; unknown fields are ignored.</summary>
public sealed class FishSessionRequest
{
    /// <summary>Who asked, for the log.</summary>
    public string RequestedBy { get; set; } = string.Empty;

    /// <summary>The caller's mission; when absent, the one the game says is in progress. Keys the fish scores.</summary>
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
    MissionComplete, CannotReach,
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
/// GP is spent as it comes ("fish on, spend later", the user's choice): after landing a mission's
/// prize fish (<see cref="FishScores.IsPrize"/>), Identical Cast when GP covers it, and on that cast's
/// bite Triple Hook, else Double Hook, as far as GP still goes; Thaliak's Favor whenever three
/// Angler's Art stacks are up and its 150 GP fit. Only learned actions; Surface Slap is not used.
/// </para>
///
/// <para>
/// On a Cosmic mission the GP spent comes back when it is complete ("You recovered all 950 GP
/// consumed during the stellar mission", seen 2026-11-14), so it is spent as Korha spent it by hand
/// for a Gold Star: once a mission's prize is known, Prize Catch before every cast (a large-sized
/// fish scores more — 7.7 ilms 228 points where 4.3 scored 100), Big-game Fishing on top when two
/// Angler's Art stacks are up — but never the 350 GP an Identical Cast after a prize would need. A
/// mission whose first cast does not go in gets Collect turned on and one more cast; when that
/// one goes in, the mission is remembered as needing Collect and it goes on before the first cast.
/// Each special action is pressed at most once a cast, so one the game refuses holds nothing up.
/// </para>
///
/// <para>
/// A mission that scores only its <b>largest</b> fish (<see cref="FishScores.IsLargestScored"/>) is
/// fished for length instead: a bite whose tug only fish never landed longer than the score already
/// stands is let go, and after landing the mission's biggest fish comes Identical Cast and then Prize
/// Catch (a large-sized catch guaranteed) — no Double or Triple Hook, as only one fish counts.
/// </para>
///
/// <para>
/// It ends at the caller's <see cref="Stop"/> (the cast in hand first), when the mission's objectives
/// are all complete (the game puts the rod away), at either budget, when the fish "sense something
/// amiss", or when the bait runs out — each with its own <see cref="FishSessionCode"/>. The first cast
/// is the water test: if it does not go in, the session faults <see cref="FishSessionCode.CannotFishHere"/>.
/// </para>
///
/// <para>
/// Every wait comes from <see cref="FishPace"/>: the hook after a bite, the next cast, a longer pause
/// every so many casts, and the gap between presses. Nothing is pressed at a fixed rhythm.
/// </para>
/// </summary>
public sealed class FishSession
{
    public const uint IdenticalCast = 4596, IdenticalStatus = 1804, DoubleHook = 269, TripleHook = 27523;
    public const uint ThaliaksFavor = 26804, AnglersArt = 2778, PrizeCatch = 26806, PrizeStatus = 2780;
    public const int IdenticalCost = 350, DoubleHookCost = 400, TripleHookCost = 700, ThaliaksRestore = 150, ThaliaksStacks = 3;
    public const int PrizeCatchCost = 200;
    public const uint BigGameFishing = 37046, BigGameStatus = 3907;
    public const int BigGameStacks = 2;

    /// <summary>A pressed cast that has not gone in by then never will: no water, or nothing to cast with.</summary>
    public static readonly TimeSpan CastTake = TimeSpan.FromSeconds(5);

    /// <summary>No state change, and no press of ours, for this long is a session stuck without saying.</summary>
    public static readonly TimeSpan Stall = TimeSpan.FromMinutes(3);

    /// <summary>A score message this soon after a catch is that catch's.</summary>
    public static readonly TimeSpan ScoreWindow = TimeSpan.FromSeconds(10);

    private static readonly TimeSpan QuitWait = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan QuitGap = TimeSpan.FromSeconds(1.5);

    private readonly IFisherWorld _world;
    private readonly Func<bool> _isFisher;
    private readonly FishCatalog _fish;
    private readonly Func<uint, string> _nameOf;
    private readonly Func<uint, bool> _knows;
    private readonly FishScores _scores;
    private readonly Func<uint> _missionNow;
    private readonly Func<uint> _missionScore;
    private int? _lastTotal;

    private FishSessionRequest _request = new();
    private FishPace _pace = new(null);
    private HashSet<uint> _wanted = [];
    private readonly Dictionary<uint, int> _landed = new();
    private DateTime _started;
    private DateTime _nextCastAt;
    private DateTime _gapUntil;
    private DateTime? _castPressedAt;
    private DateTime? _hookAt;
    private uint _hookAction;
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

    /// <summary>The cast in the water (or the last one) went in under Identical Cast.</summary>
    private bool _castIdentical;
    private uint _lastLanded;
    private int _lastLandedAmount;
    private DateTime _lastLandedAt;
    private bool _lastLandedScored;
    private bool _missionComplete;

    /// <summary>The special actions pressed since the last cast: each at most once a cast.</summary>
    private readonly HashSet<uint> _triedThisCast = [];

    /// <summary>The first cast was refused and Collect has been turned on for one more.</summary>
    private bool _collectRetried;
    private double _lastLandedIlms;

    /// <summary>The longest fish landed this session, in ilms.</summary>
    private double _bestIlms;
    private string _gameSaid = string.Empty;

    /// <param name="knows">Whether the character has learned an action.</param>
    /// <param name="scores">What each fish scores on each mission; the session records into it.</param>
    /// <param name="missionNow">The mission the game says is in progress; 0 for none.</param>
    /// <param name="missionScore">That mission's score as the game keeps it; 0 for none.</param>
    public FishSession(IFisherWorld world, Func<bool> isFisher, FishCatalog fish, Func<uint, string> nameOf,
        Func<uint, bool> knows, FishScores scores, Func<uint> missionNow, Func<uint> missionScore)
    {
        _missionScore = missionScore;
        _world = world;
        _isFisher = isFisher;
        _fish = fish;
        _nameOf = nameOf;
        _knows = knows;
        _scores = scores;
        _missionNow = missionNow;
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

    /// <summary>Points the game said this session earned.</summary>
    public int Points { get; private set; }

    /// <summary>The mission's whole score, as the game keeps it.</summary>
    public uint MissionScore => _missionScore();

    /// <summary>The mission the scores are kept under: the caller's, else the game's.</summary>
    public uint Mission => _request.MissionId ?? _missionNow();

    public int Gp => _world.Gp;
    public int MaxGp => _world.MaxGp;

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
        Points = 0;
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
        _castIdentical = false;
        _lastLanded = 0;
        _lastLandedScored = true;
        _missionComplete = false;
        _triedThisCast.Clear();
        _collectRetried = false;
        _bestIlms = 0;
        _lastLandedIlms = 0;
        _gameSaid = string.Empty;
        _lastTotal = null;
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
        _world.Log($"Fishing here for {who}{(Mission is var m and not 0 ? $" (mission {m})" : string.Empty)} with {Name(_world.CurrentBait)} — {wanted}{budgets}; pace seed {_pace.Seed}.");
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

    /// <summary>"All stellar mission objectives complete!" (LogMessage 10766): the game puts the rod away; nothing more to cast for.</summary>
    public void MissionComplete()
    {
        if (State != FishSessionState.Running || _missionComplete)
            return;
        _missionComplete = true;
        _world.Log("All mission objectives are complete.");
    }

    /// <summary>The game refused a cast, in its own words ("Unable to fish at this location."); kept for the reason.</summary>
    public void CastRefused(string what)
    {
        if (State == FishSessionState.Running)
            _gameSaid = what;
    }

    /// <summary>
    /// "You earn a score of N" (LogMessage 10804): the points of the catch just landed. Recorded per
    /// fish under the mission, a multi-hook's points shared out over the fish it landed.
    /// </summary>
    public void Scored(int score)
    {
        if (score <= 0 || State == FishSessionState.Idle)
            return;
        var now = _world.UtcNow;
        TakeCatches(now);
        Points += score;
        if (_lastLandedScored || _lastLanded == 0 || now - _lastLandedAt > ScoreWindow)
        {
            _world.Log($"Scored {score}.");
            return;
        }
        _lastLandedScored = true;
        var each = score / Math.Max(1, _lastLandedAmount);
        var mission = Mission;
        _scores.Record(mission, _lastLanded, each);
        _world.Log($"Scored {score} for {Name(_lastLanded)}{(_lastLandedAmount > 1 ? $" ×{_lastLandedAmount} ({each} each)" : string.Empty)}"
                   + (mission == 0 ? " — no mission, not recorded." : $" on mission {mission}."));
    }

    /// <summary>
    /// "Your score has changed to N" (LogMessage 10805): a mission scored as a whole — "largest fish
    /// caught will be scored" — reports its running total, not a catch's points. The rise is this
    /// session's, but it is not a fish's worth, so nothing is recorded per fish from it.
    /// </summary>
    public void ScoreChanged(int total)
    {
        if (total <= 0 || State == FishSessionState.Idle)
            return;
        var now = _world.UtcNow;
        TakeCatches(now);
        var rise = _lastTotal is { } last ? total - last : (int?)null;
        if (rise is > 0)
            Points += rise.Value;
        _lastTotal = total;
        _lastLandedScored = true;
        // The score only changes when the catch just landed is the new largest: it is that fish's length.
        if (_lastLanded != 0 && now - _lastLandedAt <= ScoreWindow && _lastLandedIlms > 0)
            _scores.RecordPointsPerIlm(Mission, total / _lastLandedIlms);
        _world.Log($"Mission score now {total}{(rise is { } r ? $" ({(r >= 0 ? "+" : string.Empty)}{r})" : string.Empty)}"
                   + (_lastLanded != 0 && now - _lastLandedAt <= ScoreWindow ? $" after {Name(_lastLanded)}." : "."));
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
                TakeCatches(now);
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

    private void TakeCatches(DateTime now)
    {
        foreach (var c in _world.TakeCatches())
        {
            _landed[c.ItemId] = _landed.GetValueOrDefault(c.ItemId) + c.Amount;
            _lastLanded = c.ItemId;
            _lastLandedAmount = c.Amount;
            _lastLandedAt = now;
            _lastLandedScored = false;
            _lastLandedIlms = c.Ilms;
            if (c.Ilms > 0)
            {
                _bestIlms = Math.Max(_bestIlms, c.Ilms);
                _scores.RecordSize(Mission, c.ItemId, c.Ilms);
            }
            _world.Log($"Landed {Name(c.ItemId)}{(c.Amount > 1 ? $" ×{c.Amount}" : string.Empty)}{(c.Ilms > 0 ? $" ({c.Ilms:F1} ilms)" : string.Empty)}.");
        }
    }

    private void TickRunning(DateTime now)
    {
        TakeCatches(now);

        var state = _world.State;
        var ready = state is FishState.None or FishState.PoleReady;
        if (state != _lastState)
        {
            // The pressed cast has gone in.
            if (!ready && _castPressedAt is not null)
            {
                _castPressedAt = null;
                if (Casts == 0 && _collectRetried && Mission is var needing and not 0)
                {
                    _scores.MarkNeedsCollect(needing);
                    _world.Log($"The cast went in with Collect on: mission {needing} needs it, and gets it from the start next time.");
                }
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
                    _world.UseAction(_hookAction);
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
                        var said = _gameSaid.Length > 0 ? $" (the game: \"{_gameSaid}\")" : string.Empty;
                        // A mission may want Collect on before a cast goes in at all.
                        if (Casts == 0 && !_collectRetried && Mission != 0 && !_world.HasStatus(FishingRun.CollectStatus))
                        {
                            if (!_knows(FishingRun.Collect))
                            {
                                Fault(FishSessionCode.CannotFishHere, "the first cast did not go in — the mission may need Collect, which is not learned "
                                                                      + $"(Fisher Lv 50, quest \"Inscrutable Tastes\"){said}");
                                break;
                            }
                            _collectRetried = true;
                            _castPressedAt = null;
                            _gameSaid = string.Empty;
                            Press(now, FishingRun.Collect, $"The first cast did not go in{said} — the mission may need Collect: turning it on.");
                            break;
                        }
                        if (Casts == 0)
                            Fault(FishSessionCode.CannotFishHere, "the first cast did not go in — no water here, not facing it, "
                                                                  + $"or the mission needs an action not learned (a collectability mission needs Collect){said}");
                        else
                            Fault(FishSessionCode.Stalled, $"a cast did not go in{said}");
                    }
                    break;
                }
                if (now < _nextCastAt || now < _gapUntil)
                    break;
                if (Special(now))
                    break;
                _castIdentical = _world.HasStatus(IdenticalStatus);
                _triedThisCast.Clear();
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
        else if (_missionComplete)
            Finish(FishSessionCode.MissionComplete, "all mission objectives are complete", FishSessionState.Done);
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

    /// <summary>
    /// Before a cast, in this order: Collect for a mission that needs it, Thaliak's Favor when it fits,
    /// Identical Cast after a prize, Prize Catch, Big-game Fishing on a Prize Catch. True when one was pressed.
    /// </summary>
    private bool Special(DateTime now)
    {
        var gp = _world.Gp;
        var mission = Mission;
        if (Casts == 0 && mission != 0 && _scores.NeedsCollect(mission) && !_world.HasStatus(FishingRun.CollectStatus) && Can(FishingRun.Collect))
        {
            Press(now, FishingRun.Collect, $"Collect on: mission {mission} needs it.");
            return true;
        }

        if (Can(ThaliaksFavor) && _world.StatusStacks(AnglersArt) >= ThaliaksStacks && gp + ThaliaksRestore <= _world.MaxGp)
        {
            Press(now, ThaliaksFavor, $"Thaliak's Favor: {gp} GP + {ThaliaksRestore}.");
            return true;
        }

        // The same fish again, after a prize: not twice running (the game refuses consecutive casts).
        var largest = _scores.IsLargestScored(mission);
        var prize = largest ? _scores.IsBiggest(mission, _lastLanded) : _scores.IsPrize(mission, _lastLanded);
        if (_lastLanded != 0 && !_castIdentical && !_world.HasStatus(IdenticalStatus) && gp >= IdenticalCost
            && Can(IdenticalCast) && (_wanted.Count == 0 || _wanted.Contains(_lastLanded)) && prize)
        {
            var worth = largest
                ? $"up to {_scores.SizeOf(mission, _lastLanded)!.Max:F1} ilms"
                : $"about {_scores.Of(mission, _lastLanded)!.Average:F0} points";
            Press(now, IdenticalCast, $"Identical Cast for {Name(_lastLanded)} ({worth}; {gp} GP).");
            return true;
        }

        // On a mission the GP comes back: a large-sized fish every cast, once the prize is known — but
        // never out of the GP an Identical Cast would need, unless one is already up.
        var identicalUp = _world.HasStatus(IdenticalStatus);
        if (mission != 0 && (largest || _scores.HasPrize(mission)) && !_world.HasStatus(PrizeStatus) && Can(PrizeCatch)
            && (identicalUp ? gp >= PrizeCatchCost : gp - PrizeCatchCost >= IdenticalCost))
        {
            Press(now, PrizeCatch, $"Prize Catch: a large-sized one ({gp} GP).");
            return true;
        }

        // A large one, and with luck an especially large one.
        if (mission != 0 && _world.HasStatus(PrizeStatus) && !_world.HasStatus(BigGameStatus)
            && _world.StatusStacks(AnglersArt) >= BigGameStacks && Can(BigGameFishing))
        {
            Press(now, BigGameFishing, $"Big-game Fishing ({_world.StatusStacks(AnglersArt)} Angler's Art).");
            return true;
        }
        return false;
    }

    /// <summary>Learned, and not yet pressed since the last cast.</summary>
    private bool Can(uint action) => _knows(action) && !_triedThisCast.Contains(action);

    private void Press(DateTime now, uint action, string why)
    {
        _triedThisCast.Add(action);
        _gapUntil = now + _pace.ActionGap();
        _lastChange = now;
        _world.Log(why);
        _world.UseAction(action);
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
        var largest = _scores.IsLargestScored(Mission);
        if (_wanted.Count == 0 && largest && CannotBeat(tug) is { } hopeless)
        {
            hook = false;
            why = $"{seconds:F1}s {tug.ToString().ToLowerInvariant()} tug — {hopeless}; letting it go";
        }
        else if (_wanted.Count == 0)
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

        // Under Identical Cast the bite is the prize again: land as many as GP allows.
        _hookAction = FishingRun.Hook;
        if (hook && _castIdentical && !largest)
        {
            var gp = _world.Gp;
            if (_knows(TripleHook) && gp >= TripleHookCost)
                _hookAction = TripleHook;
            else if (_knows(DoubleHook) && gp >= DoubleHookCost)
                _hookAction = DoubleHook;
            if (_hookAction != FishingRun.Hook)
                why += $", {(_hookAction == TripleHook ? "Triple" : "Double")} Hook ({gp} GP)";
        }

        LastBite = new FishBite(Math.Round(seconds, 1), tug, hook);
        _world.Log($"Bite: {why}.");
        if (hook)
            _hookAt = now + _pace.HookDelay();
    }

    /// <summary>
    /// On a mission scored by its largest fish: why a bite with this tug cannot raise the score — every
    /// fish of the mission with that tug has been landed, and never longer than the score stands now.
    /// Null when it might (an unknown tug, an unknown fish, a fish never measured, nothing to beat yet,
    /// or the mission's biggest fish — whose longest is usually what set the score, and can be beaten).
    /// </summary>
    private string? CannotBeat(Tug tug)
    {
        var mission = Mission;
        var perIlm = _scores.PointsPerIlmOf(mission);
        var bar = Math.Max(_bestIlms, perIlm is > 0 ? MissionScore / perIlm.Value : 0);
        if (tug == Tug.Unknown || bar <= 0)
            return null;
        var withTug = _fish.All
            .Where(f => f.CosmicMission == (int)mission && BiteJudge.TugOf(f.Bite) is var t && (t == Tug.Unknown || t == tug))
            .ToList();
        if (withTug.Count == 0
            || withTug.Any(f => _scores.SizeOf(mission, f.Id) is not { } size || size.Max > bar || _scores.IsBiggest(mission, f.Id)))
            return null;
        return $"{string.Join(", ", withTug.Select(f => Name(f.Id)))} never landed longer than {bar:F1} ilms";
    }

    private void Finish(FishSessionCode code, string why, FishSessionState end)
    {
        Code = code;
        Reason = why;
        _endState = end;
        State = FishSessionState.Finishing;
        _phaseStart = _world.UtcNow;
        _lastQuit = default;
        _world.Log($"Fishing here: {why} — {Casts} casts, {_landed.Values.Sum()} landed{(Points > 0 ? $", {Points} points" : string.Empty)}.");
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
