using System;
using System.Collections.Generic;
using System.Linq;

namespace Demeter.Services.Fishing;

/// <summary>The tug on the line: how hard the fish took it. Unknown when it could not be read.</summary>
public enum Tug { Unknown, Light, Medium, Heavy }

/// <summary>Hook or let it go, why, and which fish it could have been.</summary>
public sealed record BiteCall(bool Hook, string Why, IReadOnlyList<uint> Candidates);

/// <summary>
/// Is this bite one worth hooking?
///
/// <para>
/// A bite says two things: how long it took after the cast, and how hard it tugged. Each fish at a hole
/// bites a given bait within a known range of seconds (recorded by GatherBuddy Reborn's community, kept
/// per hole, fish and bait, Chum apart because it shortens the wait), and with a known tug. The fish
/// that fit both are the candidates. Hooking costs the cast and the reel; letting an unwanted fish go
/// costs nothing but the wait, and keeps the line free for the one wanted.
/// </para>
///
/// <para>
/// When the records say nothing — a bait nobody recorded, a bite outside every range — the bite is
/// hooked: a guess that loses a few seconds beats one that throws the target back.
/// </para>
/// </summary>
public static class BiteJudge
{
    /// <summary>Slack around a recorded range: the records are the extremes seen, not the limits.</summary>
    public const double Slack = 0.5;

    public static Tug TugOf(string? bite) => bite switch
    {
        "Weak" => Tug.Light,
        "Strong" => Tug.Medium,
        "Legendary" => Tug.Heavy,
        _ => Tug.Unknown,
    };

    /// <param name="bait">The bait on the hook — or, mooching, the fish swum as bait (the records key it the same way).</param>
    /// <param name="seconds">From the line going in to the bite.</param>
    /// <param name="wanted">The target, the fish its mooch road still needs, the predators still owed.</param>
    /// <param name="holeBites">This hole's recorded bite ranges.</param>
    /// <param name="tugOfFish">The tug a fish bites with (from the fish data); Unknown when not known.</param>
    /// <param name="biteBaitOf">What a fish bites on: the last step of its road (a bait, or the fish mooched).</param>
    public static BiteCall Judge(uint bait, double seconds, Tug tug, bool chum, IReadOnlySet<uint> wanted,
        IReadOnlyList<BiteTimes> holeBites, Func<uint, Tug> tugOfFish, Func<uint, uint?>? biteBaitOf = null)
    {
        var onBait = holeBites.Where(b => b.Bait == bait).ToList();
        var candidates = onBait
            .Where(b => Fits(b, seconds, chum))
            .Where(b => tug == Tug.Unknown || tugOfFish(b.Fish) is var t && (t == Tug.Unknown || t == tug))
            .Select(b => b.Fish)
            .Distinct()
            .ToList();

        var wantedHere = candidates.Where(wanted.Contains).ToList();
        if (wantedHere.Count > 0)
            return new BiteCall(true, $"{seconds:F1}s{TugText(tug)} fits fish {string.Join(", ", wantedHere)}", candidates);

        // A wanted fish that takes this bait but was never recorded taking it here could bite at any
        // second, so "fits only another fish" is no evidence against it. Hook what has its tug.
        var unrecorded = wanted
            .Where(f => biteBaitOf?.Invoke(f) == bait && !onBait.Any(b => b.Fish == f))
            .Where(f => tug == Tug.Unknown || tugOfFish(f) is var t && (t == Tug.Unknown || t == tug))
            .ToList();
        if (unrecorded.Count > 0)
            return new BiteCall(true, $"{seconds:F1}s{TugText(tug)} — could be fish {string.Join(", ", unrecorded)}, never recorded on this bait here; hooking to see", candidates);

        if (candidates.Count > 0)
            return new BiteCall(false, $"{seconds:F1}s{TugText(tug)} fits only fish {string.Join(", ", candidates)} — letting it go", candidates);

        // Nothing recorded fits. If a wanted fish has records on this bait and its tug is known and
        // different, it is not that one; otherwise hook — unknown is not unwanted.
        var wantedOnBait = onBait.Where(b => wanted.Contains(b.Fish)).Select(b => b.Fish)
            .Concat(wanted.Where(f => biteBaitOf?.Invoke(f) == bait))
            .Distinct().ToList();
        if (tug != Tug.Unknown && wantedOnBait.Count > 0
            && wantedOnBait.All(f => tugOfFish(f) is var t && t != Tug.Unknown && t != tug))
            return new BiteCall(false, $"{seconds:F1}s{TugText(tug)} — not the tug the wanted fish bites with; letting it go", candidates);

        return new BiteCall(true, $"{seconds:F1}s{TugText(tug)} — no record fits; hooking to see", candidates);
    }

    private static bool Fits(BiteTimes b, double seconds, bool chum)
    {
        var (min, max) = chum && b.MinChum is { } cMin && b.MaxChum is { } cMax
            ? (cMin, cMax)
            : (b.Min, b.Max) is ({ } nMin, { } nMax) ? (nMin, nMax) : (double.NaN, double.NaN);
        return !double.IsNaN(min) && seconds >= min - Slack && seconds <= max + Slack;
    }

    private static string TugText(Tug tug) => tug == Tug.Unknown ? string.Empty : $" {tug.ToString().ToLowerInvariant()} tug";
}
