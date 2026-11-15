using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace Demeter.Services.Fishing;

/// <summary>What one fish has scored on one mission: how often, and the range.</summary>
public sealed class FishScoreRecord
{
    public int Count { get; set; }
    public int Min { get; set; }
    public int Max { get; set; }
    public long Total { get; set; }

    public double Average => Count == 0 ? 0 : (double)Total / Count;
}

/// <summary>How long one fish has been landed on one mission, in ilms.</summary>
public sealed class FishSizeRecord
{
    public int Count { get; set; }
    public double Min { get; set; }
    public double Max { get; set; }
}

/// <summary>
/// The points each fish earns on each Cosmic Exploration mission, from the game's own "You earn a
/// score of N" after a catch (LogMessage 10804).
///
/// <para>
/// The game's sheets do not list a score per fish, so the table is recorded: a Debug build writes
/// what it sees to <c>fish-scores.json</c> beside the config, and that file is copied into
/// <c>Assets/fish-scores.json</c> to ship with a release (the user's choice, 2026-11-14). Both are
/// read at load, so a Debug build keeps adding to what shipped.
/// </para>
///
/// <para>
/// A fish is a <b>prize</b> on a mission — worth Identical Cast and a Double or Triple Hook — when at
/// least two of the mission's fish are known and its average is at least half the best average.
/// </para>
///
/// <para>
/// Some missions score only the <b>largest</b> fish ("Your score has changed to N", LogMessage 10805):
/// on 1660 the score was the length × 100 (56.9 ilms → 5,690, 116.0 → 11,600; 2026-11-14). For those
/// the table keeps every fish's lengths and the points an ilm is worth, learned from the score's rise
/// after a catch; the <b>biggest</b> fish is the one whose longest catch is the longest of the mission's.
/// </para>
/// </summary>
public sealed class FishScores
{
    public const string FileName = "fish-scores.json";

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    /// <summary>Mission (WKSMissionUnit row) → fish item → record. String keys, as JSON has them.</summary>
    public Dictionary<string, Dictionary<string, FishScoreRecord>> Missions { get; set; } = new();

    /// <summary>Mission → fish item → lengths landed.</summary>
    public Dictionary<string, Dictionary<string, FishSizeRecord>> Sizes { get; set; } = new();

    /// <summary>Mission → points an ilm is worth, for a mission scored by its largest fish.</summary>
    public Dictionary<string, double> PointsPerIlm { get; set; } = new();

    /// <summary>Missions whose casts only go in with Collect on (a first cast refused until it was).</summary>
    public List<uint> NeedsCollectMissions { get; set; } = [];

    public bool NeedsCollect(uint mission) => NeedsCollectMissions.Contains(mission);

    public void MarkNeedsCollect(uint mission)
    {
        if (mission == 0 || NeedsCollectMissions.Contains(mission))
            return;
        NeedsCollectMissions.Add(mission);
        Dirty = true;
    }

    /// <summary>A points mission with at least two of its fish known — enough to tell a prize from the rest.</summary>
    public bool HasPrize(uint mission) => Missions.GetValueOrDefault(mission.ToString()) is { Count: >= 2 };

    /// <summary>Something was recorded since the last save.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public bool Dirty { get; private set; }

    public void Record(uint mission, uint fish, int score)
    {
        if (mission == 0 || fish == 0 || score <= 0)
            return;
        if (!Missions.TryGetValue(mission.ToString(), out var fishes))
            Missions[mission.ToString()] = fishes = new();
        if (!fishes.TryGetValue(fish.ToString(), out var record))
            fishes[fish.ToString()] = record = new FishScoreRecord { Min = score, Max = score };
        record.Count++;
        record.Total += score;
        record.Min = Math.Min(record.Min, score);
        record.Max = Math.Max(record.Max, score);
        Dirty = true;
    }

    public void RecordSize(uint mission, uint fish, double ilms)
    {
        if (mission == 0 || fish == 0 || ilms <= 0)
            return;
        if (!Sizes.TryGetValue(mission.ToString(), out var fishes))
            Sizes[mission.ToString()] = fishes = new();
        if (!fishes.TryGetValue(fish.ToString(), out var record))
            fishes[fish.ToString()] = record = new FishSizeRecord { Min = ilms, Max = ilms };
        record.Count++;
        record.Min = Math.Min(record.Min, ilms);
        record.Max = Math.Max(record.Max, ilms);
        Dirty = true;
    }

    /// <summary>The mission scores its largest fish, an ilm worth this many points (the latest seen).</summary>
    public void RecordPointsPerIlm(uint mission, double points)
    {
        if (mission == 0 || points <= 0)
            return;
        var rounded = Math.Round(points, 2);
        if (PointsPerIlm.TryGetValue(mission.ToString(), out var known) && known == rounded)
            return;
        PointsPerIlm[mission.ToString()] = rounded;
        Dirty = true;
    }

    public double? PointsPerIlmOf(uint mission) => PointsPerIlm.TryGetValue(mission.ToString(), out var p) ? p : null;

    public bool IsLargestScored(uint mission) => PointsPerIlm.ContainsKey(mission.ToString());

    public FishSizeRecord? SizeOf(uint mission, uint fish)
        => Sizes.GetValueOrDefault(mission.ToString())?.GetValueOrDefault(fish.ToString());

    /// <summary>The fish whose longest catch is the longest of the mission's, once two of its fish are known.</summary>
    public bool IsBiggest(uint mission, uint fish)
    {
        if (Sizes.GetValueOrDefault(mission.ToString()) is not { Count: >= 2 } fishes
            || fishes.GetValueOrDefault(fish.ToString()) is not { } record)
            return false;
        return record.Max >= fishes.Values.Max(r => r.Max);
    }

    public FishScoreRecord? Of(uint mission, uint fish)
        => Missions.GetValueOrDefault(mission.ToString())?.GetValueOrDefault(fish.ToString());

    public bool IsPrize(uint mission, uint fish)
    {
        if (Missions.GetValueOrDefault(mission.ToString()) is not { Count: >= 2 } fishes
            || fishes.GetValueOrDefault(fish.ToString()) is not { Count: > 0 } record)
            return false;
        return record.Average >= fishes.Values.Max(r => r.Average) / 2;
    }

    /// <summary>Every file that exists, merged in order; unreadable ones are skipped with the reason logged.</summary>
    public static FishScores Load(IEnumerable<string?> files, Action<string>? log = null)
    {
        var scores = new FishScores();
        foreach (var file in files)
        {
            if (string.IsNullOrEmpty(file) || !File.Exists(file))
                continue;
            try
            {
                var read = JsonSerializer.Deserialize<FishScores>(File.ReadAllText(file), Json);
                if (read is not null)
                    scores.Merge(read);
            }
            catch (Exception ex)
            {
                log?.Invoke($"Fish scores in {Path.GetFileName(file)} could not be read ({ex.GetType().Name}: {ex.Message}).");
            }
        }
        scores.Dirty = false;
        return scores;
    }

    public void Save(string file)
    {
        File.WriteAllText(file, JsonSerializer.Serialize(this, Json));
        Dirty = false;
    }

    private void Merge(FishScores other)
    {
        foreach (var (mission, points) in other.PointsPerIlm)
            PointsPerIlm[mission] = points;
        foreach (var mission in other.NeedsCollectMissions)
            if (!NeedsCollectMissions.Contains(mission))
                NeedsCollectMissions.Add(mission);
        foreach (var (mission, fishes) in other.Sizes)
        {
            if (!Sizes.TryGetValue(mission, out var mine))
                Sizes[mission] = mine = new();
            foreach (var (fish, theirs) in fishes)
            {
                if (theirs.Count <= 0)
                    continue;
                if (!mine.TryGetValue(fish, out var record))
                {
                    mine[fish] = new FishSizeRecord { Count = theirs.Count, Min = theirs.Min, Max = theirs.Max };
                    continue;
                }
                record.Count += theirs.Count;
                record.Min = Math.Min(record.Min, theirs.Min);
                record.Max = Math.Max(record.Max, theirs.Max);
            }
        }
        foreach (var (mission, fishes) in other.Missions)
        {
            if (!Missions.TryGetValue(mission, out var mine))
                Missions[mission] = mine = new();
            foreach (var (fish, theirs) in fishes)
            {
                if (theirs.Count <= 0)
                    continue;
                if (!mine.TryGetValue(fish, out var record))
                {
                    mine[fish] = new FishScoreRecord { Count = theirs.Count, Min = theirs.Min, Max = theirs.Max, Total = theirs.Total };
                    continue;
                }
                record.Count += theirs.Count;
                record.Total += theirs.Total;
                record.Min = Math.Min(record.Min, theirs.Min);
                record.Max = Math.Max(record.Max, theirs.Max);
            }
        }
    }
}

/// <summary>
/// How fast GP is coming back: every rise seen over the last minute, as GP a second. Spending is
/// not counted against it — the meter answers "how long until I have N", not "where did it go".
/// </summary>
public sealed class GpMeter
{
    public static readonly TimeSpan Window = TimeSpan.FromMinutes(1);

    private readonly Queue<(DateTime At, int Gain)> _gains = new();
    private int? _last;
    private DateTime _first;

    public int Gp { get; private set; }
    public int MaxGp { get; private set; }

    public void Observe(DateTime now, int gp, int maxGp)
    {
        if (_last is null)
            _first = now;
        if (_last is { } last && gp > last && maxGp == MaxGp)
            _gains.Enqueue((now, gp - last));
        while (_gains.Count > 0 && now - _gains.Peek().At > Window)
            _gains.Dequeue();
        _last = gp;
        Gp = gp;
        MaxGp = maxGp;
        Now = now;
    }

    private DateTime Now { get; set; }

    /// <summary>GP a second over the last minute (or since first seen, if sooner); 0 when nothing came back.</summary>
    public double PerSecond
    {
        get
        {
            var span = Math.Min(Window.TotalSeconds, (Now - _first).TotalSeconds);
            return span <= 0 ? 0 : _gains.Sum(g => g.Gain) / span;
        }
    }

    /// <summary>Until GP reaches this much at the current rate; zero when it is there, null when nothing is coming back.</summary>
    public TimeSpan? Until(int gp)
    {
        if (Gp >= gp)
            return TimeSpan.Zero;
        var rate = PerSecond;
        return rate <= 0 ? null : TimeSpan.FromSeconds((gp - Gp) / rate);
    }
}
