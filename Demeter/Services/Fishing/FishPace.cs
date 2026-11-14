using System;

namespace Demeter.Services.Fishing;

/// <summary>
/// The pace a caller asks for, as JSON ranges <c>[min, max]</c> in milliseconds (casts, for
/// <see cref="LongPauseEvery"/>). Every field is optional; a missing one uses <see cref="FishPace"/>'s default.
/// </summary>
public sealed class FishPaceRequest
{
    /// <summary>From the bite showing to Hook being pressed.</summary>
    public int[]? HookDelayMs { get; set; }

    /// <summary>From the line being free again (landed or let go) to the next cast.</summary>
    public int[]? RecastMs { get; set; }

    /// <summary>A longer pause comes after this many casts, drawn again after each pause.</summary>
    public int[]? LongPauseEvery { get; set; }
    public int[]? LongPauseMs { get; set; }

    /// <summary>The least time between two presses of Demeter's own.</summary>
    public int[]? ActionGapMs { get; set; }

    /// <summary>The same seed draws the same pace. None: a fresh one each run.</summary>
    public int? Seed { get; set; }
}

/// <summary>
/// How long to wait before each press, drawn fresh every time from a seeded generator — so no two
/// casts, hooks or pauses come at the same interval, while a test (or a caller that wants it) can
/// replay a run exactly by its seed.
/// </summary>
public sealed class FishPace
{
    public static readonly (int Min, int Max) DefaultHookDelay = (250, 900);
    public static readonly (int Min, int Max) DefaultRecast = (600, 2200);
    public static readonly (int Min, int Max) DefaultLongPauseEvery = (8, 15);
    public static readonly (int Min, int Max) DefaultLongPause = (3000, 9000);
    public static readonly (int Min, int Max) DefaultActionGap = (1100, 2000);

    /// <summary>A bite stays on the line about three seconds; a slower hook than this risks losing it.</summary>
    public const int HookDelayCeiling = 1500;

    /// <summary>The game's own lock between actions: anything faster is a press the game drops.</summary>
    public const int ActionGapFloor = 500;

    public const int RecastCeiling = 60_000, LongPauseCeiling = 120_000, ActionGapCeiling = 10_000;

    private readonly Random _random;
    private readonly (int Min, int Max) _hookDelay, _recast, _longPauseEvery, _longPause, _actionGap;

    public FishPace(FishPaceRequest? request)
    {
        Seed = request?.Seed ?? Random.Shared.Next();
        _random = new Random(Seed);
        _hookDelay = Range(request?.HookDelayMs, DefaultHookDelay);
        _recast = Range(request?.RecastMs, DefaultRecast);
        _longPauseEvery = Range(request?.LongPauseEvery, DefaultLongPauseEvery);
        _longPause = Range(request?.LongPauseMs, DefaultLongPause);
        _actionGap = Range(request?.ActionGapMs, DefaultActionGap);
    }

    public int Seed { get; }

    public TimeSpan HookDelay() => Draw(_hookDelay);
    public TimeSpan Recast() => Draw(_recast);
    public TimeSpan LongPause() => Draw(_longPause);
    public TimeSpan ActionGap() => Draw(_actionGap);
    public int LongPauseEvery() => _random.Next(_longPauseEvery.Min, _longPauseEvery.Max + 1);

    /// <summary>What is wrong with the ranges asked for, or null when they will do.</summary>
    public static string? Problem(FishPaceRequest? request)
    {
        if (request is null)
            return null;
        return Check("hookDelayMs", request.HookDelayMs, 0, HookDelayCeiling)
               ?? Check("recastMs", request.RecastMs, 0, RecastCeiling)
               ?? Check("longPauseEvery", request.LongPauseEvery, 1, 1000)
               ?? Check("longPauseMs", request.LongPauseMs, 0, LongPauseCeiling)
               ?? Check("actionGapMs", request.ActionGapMs, ActionGapFloor, ActionGapCeiling);
    }

    private static string? Check(string name, int[]? range, int floor, int ceiling)
    {
        if (range is null)
            return null;
        if (range.Length != 2)
            return $"{name} must be [min, max]";
        if (range[0] > range[1])
            return $"{name}: min {range[0]} is above max {range[1]}";
        if (range[0] < floor || range[1] > ceiling)
            return $"{name} must lie within {floor}–{ceiling}";
        return null;
    }

    private static (int Min, int Max) Range(int[]? asked, (int Min, int Max) fallback)
        => asked is { Length: 2 } ? (asked[0], asked[1]) : fallback;

    private TimeSpan Draw((int Min, int Max) range) => TimeSpan.FromMilliseconds(_random.Next(range.Min, range.Max + 1));
}
