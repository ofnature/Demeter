using System.Collections.Generic;
using System.Linq;

namespace Demeter.Services.Melding;

public enum TargetSource
{
    /// <summary>A GatheringPointBonus row.</summary>
    Sheet,

    /// <summary>The user's override table (community tiers the sheets do not show).</summary>
    Override,

    /// <summary>Given directly by the caller.</summary>
    Given,
}

/// <summary>One stat's target and where it came from, so the plan can say why it aims there.</summary>
/// <param name="NodeLinked">The bonus row is carried by at least one GatheringPoint (a real node), not only by other content.</param>
/// <param name="RangeTop">The value is the top of a ranged bonus (where it stops growing), not where it starts.</param>
public sealed record StatTarget(
    GatherStat Stat,
    int Value,
    TargetSource Source,
    string Reason,
    uint? BonusRowId = null,
    uint BonusType = 0,
    int BonusValue = 0,
    bool NodeLinked = false,
    bool RangeTop = false);

/// <summary>Targets for the three stats; a null stat has no target (anything goes).</summary>
public sealed record TargetSet(StatTarget? Gathering, StatTarget? Perception, StatTarget? Gp)
{
    public static readonly TargetSet None = new(null, null, null);

    public StatTarget? For(GatherStat stat) => stat switch
    {
        GatherStat.Gathering => Gathering,
        GatherStat.Perception => Perception,
        GatherStat.Gp => Gp,
        _ => null,
    };

    public int Value(GatherStat stat) => For(stat)?.Value ?? 0;

    public TargetSet With(StatTarget? target, GatherStat stat) => stat switch
    {
        GatherStat.Gathering => this with { Gathering = target },
        GatherStat.Perception => this with { Perception = target },
        GatherStat.Gp => this with { Gp = target },
        _ => this,
    };

    /// <summary>Targets typed in by the caller (tests, or a "what if" in the UI). Zero = no target.</summary>
    public static TargetSet Of(int gathering, int perception, int gp, string reason = "given")
        => new(
            gathering > 0 ? new StatTarget(GatherStat.Gathering, gathering, TargetSource.Given, reason) : null,
            perception > 0 ? new StatTarget(GatherStat.Perception, perception, TargetSource.Given, reason) : null,
            gp > 0 ? new StatTarget(GatherStat.Gp, gp, TargetSource.Given, reason) : null);

    public override string ToString()
        => $"{Gathering?.Value.ToString() ?? "-"} / {Perception?.Value.ToString() ?? "-"} / {Gp?.Value.ToString() ?? "-"}";
}

public enum TargetOverrideMode
{
    /// <summary>Raise the target to this value if it is lower.</summary>
    AtLeast,

    /// <summary>Use this value whatever the sheets say.</summary>
    Exactly,
}

/// <summary>A row in the user-editable override table.</summary>
/// <param name="Job">Only for this job; null for every gatherer.</param>
public sealed record TargetOverride(
    GatherStat Stat,
    int Value,
    string Label,
    TargetOverrideMode Mode = TargetOverrideMode.AtLeast,
    GatherJob? Job = null,
    bool Enabled = true);

/// <summary>
/// The stat targets, computed from GatheringPoint → GatheringPointBonus when the sheets load, never
/// typed in. A bonus row reads "if Gathering (14) / Perception (15) / Max GP (19) ≥ ConditionValue,
/// then BonusType +BonusValue". The target for a stat is the highest ConditionValue among the rows in
/// scope: every row (<see cref="Highest"/>) or only those the chosen nodes carry (<see cref="ForNodes"/>).
/// </summary>
public static class GatheringTargets
{
    public const uint ConditionGathering = 14;
    public const uint ConditionPerception = 15;
    public const uint ConditionMaxGp = 19;

    /// <summary>
    /// Community tiers the sheets do not state, as the plugin's author supplied them (not scraped).
    /// The user edits this table; nothing applies it unless the caller passes it to <see cref="Apply"/>.
    /// 990 GP is also the top of the sheet's ranged Max GP ≥ 960 row, so <c>includeRangeTop</c> finds it too.
    /// </summary>
    public static readonly IReadOnlyList<TargetOverride> CommunityTiers =
    [
        new(GatherStat.Gp, 990, "990 GP (community tier)"),
        new(GatherStat.Gathering, 4570, "4570 Gathering for big fish (community tier)", Job: GatherJob.Fisher),
        new(GatherStat.Perception, 5090, "5090 Perception minimum (community tier)"),
    ];

    public static GatherStat? StatFor(uint condition) => condition switch
    {
        ConditionGathering => GatherStat.Gathering,
        ConditionPerception => GatherStat.Perception,
        ConditionMaxGp => GatherStat.Gp,
        _ => null,
    };

    /// <summary>
    /// The highest threshold anywhere in the sheet, linked to a node or not (rows only other content
    /// carries — Cosmic Exploration, by the look of them — count too).
    /// </summary>
    /// <param name="points">Optional: only used to mark which winning rows are node-linked.</param>
    /// <param name="includeRangeTop">Aim for where a ranged bonus stops growing instead of where it starts.</param>
    /// <param name="bonusTypeText">Optional GatheringPointBonusType texts, for readable reasons.</param>
    public static TargetSet Highest(
        IEnumerable<GatheringPointBonusRow> bonuses,
        IEnumerable<GatheringPointRow>? points = null,
        bool includeRangeTop = false,
        IReadOnlyDictionary<uint, string>? bonusTypeText = null)
    {
        var linked = points == null ? new HashSet<uint>() : LinkedIds(points);
        return Pick(bonuses, linked, includeRangeTop, bonusTypeText, "highest in game");
    }

    /// <summary>The highest threshold among the bonuses the given nodes carry (all nodes when <paramref name="pointIds"/> is null).</summary>
    public static TargetSet ForNodes(
        IEnumerable<GatheringPointRow> points,
        IEnumerable<GatheringPointBonusRow> bonuses,
        IEnumerable<uint>? pointIds = null,
        bool includeRangeTop = false,
        IReadOnlyDictionary<uint, string>? bonusTypeText = null)
    {
        var wanted = pointIds == null ? null : new HashSet<uint>(pointIds);
        var inScope = points.Where(p => wanted == null || wanted.Contains(p.Id)).ToList();
        var linked = LinkedIds(inScope);
        var scope = wanted == null ? "highest on any node" : $"highest on {inScope.Count} chosen node(s)";
        return Pick(bonuses.Where(b => linked.Contains(b.Id)), linked, includeRangeTop, bonusTypeText, scope);
    }

    /// <summary>Applies the enabled overrides for <paramref name="job"/> in order.</summary>
    public static TargetSet Apply(TargetSet targets, IEnumerable<TargetOverride> overrides, GatherJob job)
    {
        foreach (var o in overrides)
        {
            if (!o.Enabled || (o.Job != null && o.Job != job) || o.Value <= 0)
                continue;
            var current = targets.For(o.Stat);
            if (o.Mode == TargetOverrideMode.AtLeast && current != null && current.Value >= o.Value)
                continue;
            var reason = current == null || o.Mode == TargetOverrideMode.Exactly
                ? o.Label
                : $"{o.Label}, above the sheet's {current.Value}";
            targets = targets.With(new StatTarget(o.Stat, o.Value, TargetSource.Override, reason), o.Stat);
        }

        return targets;
    }

    private static HashSet<uint> LinkedIds(IEnumerable<GatheringPointRow> points)
    {
        var ids = new HashSet<uint>();
        foreach (var p in points)
        {
            foreach (var b in p.Bonuses)
            {
                if (b != 0)
                    ids.Add(b);
            }
        }

        return ids;
    }

    private static TargetSet Pick(
        IEnumerable<GatheringPointBonusRow> rows,
        HashSet<uint> linked,
        bool includeRangeTop,
        IReadOnlyDictionary<uint, string>? bonusTypeText,
        string scope)
    {
        var best = new Dictionary<GatherStat, (GatheringPointBonusRow Row, int Value, bool Top)>();
        foreach (var row in rows)
        {
            if (StatFor(row.Condition) is not { } stat || row.ConditionValue <= 0)
                continue;
            var top = includeRangeTop && row.IsRanged;
            var value = top ? row.RangeTopConditionValue : row.ConditionValue;
            if (!best.TryGetValue(stat, out var current)
                || value > current.Value
                // Same value: prefer a node-linked row, then the lower id, so the reason is stable.
                || (value == current.Value && Better(row, current.Row, linked)))
                best[stat] = (row, value, top);
        }

        var set = TargetSet.None;
        foreach (var (stat, (row, value, top)) in best)
        {
            var isLinked = linked.Contains(row.Id);
            var bonus = top ? row.RangeTopBonusValue : row.BonusValue;
            var reason = $"{GatherStats.Name(stat)} ≥ {value} → {BonusText(row.BonusType, bonus, bonusTypeText)} "
                       + $"(GatheringPointBonus #{row.Id}{(isLinked ? ", on nodes" : ", not on any node")}{(top ? ", top of its range" : "")}; {scope})";
            set = set.With(new StatTarget(stat, value, TargetSource.Sheet, reason, row.Id, row.BonusType, bonus, isLinked, top), stat);
        }

        return set;
    }

    private static bool Better(GatheringPointBonusRow row, GatheringPointBonusRow current, HashSet<uint> linked)
    {
        var rowLinked = linked.Contains(row.Id);
        var currentLinked = linked.Contains(current.Id);
        if (rowLinked != currentLinked)
            return rowLinked;
        return row.Id < current.Id;
    }

    private static string BonusText(uint bonusType, int value, IReadOnlyDictionary<uint, string>? texts)
    {
        if (texts != null && texts.TryGetValue(bonusType, out var text) && !string.IsNullOrWhiteSpace(text))
            return text.Replace("UNKNOWN", value.ToString()).Trim();
        return $"bonus type {bonusType} +{value}";
    }
}
