using System;
using System.Collections.Generic;
using System.Linq;

namespace Demeter.Services.Melding;

/// <summary>A materia already in a socket, as the game reports it (any stat, not only gatherer ones).</summary>
/// <param name="BaseParam">The materia's stat (BaseParam id). Non-gatherer materia count for nothing here.</param>
/// <param name="Grade">1-based grade (XII = 12).</param>
public sealed record MeldedMateria(uint BaseParam, int Grade, int Value, uint ItemId = 0);

/// <summary>A materia type that can be melded: one stat at one grade.</summary>
/// <param name="Grade">1-based (Guerdon XII = 12).</param>
/// <param name="MinItemLevel">The materia item's own item level, which is its melding requirement on the base item.</param>
public sealed record MateriaGrade(uint ItemId, string Name, GatherStat Stat, int Grade, int Value, int MinItemLevel)
{
    public override string ToString() => Name;
}

/// <summary>
/// One piece the optimizer may put on: an item the gatherer owns (with its melds) or one from the
/// Item sheet it could get. Stats are as worn, so an HQ candidate already includes its HQ bonus.
/// </summary>
/// <param name="Slot">Where it goes. A ring candidate fits either ring slot whichever ring slot it names.</param>
/// <param name="Caps">Per-stat cap for this piece (<see cref="StatCapMath"/>). Melds stop at the cap; base stats never shrink.</param>
/// <param name="MateriaSlots">Guaranteed sockets (Item.MateriaSlotCount).</param>
/// <param name="AdvancedMeldingPermitted">Overmelds allowed up to five sockets (Item.IsAdvancedMeldingPermitted).</param>
/// <param name="CurrentMelds">What is in the sockets now, in socket order. Empty for sheet candidates.</param>
/// <param name="InstanceId">The adapter's id for an owned copy (container and slot); one copy cannot be worn twice.</param>
public sealed record GearCandidate(
    GearSlot Slot,
    uint ItemId,
    string Name,
    int ItemLevel,
    bool IsHq,
    GatherStats BaseStats,
    GatherStats Caps,
    int MateriaSlots,
    bool AdvancedMeldingPermitted,
    IReadOnlyList<MeldedMateria>? CurrentMelds = null,
    bool IsOwned = false,
    bool IsEquipped = false,
    bool IsUnique = false,
    ulong? InstanceId = null)
{
    /// <summary>The most sockets any piece can have once overmelded.</summary>
    public const int MaxSockets = 5;

    public IReadOnlyList<MeldedMateria> Melds => CurrentMelds ?? [];

    public int TotalSockets => AdvancedMeldingPermitted ? Math.Max(MateriaSlots, MaxSockets) : MateriaSlots;

    public bool IsRing => Slot is GearSlot.RingRight or GearSlot.RingLeft;

    /// <summary>How much melding can still add to a stat before the cap.</summary>
    public int Headroom(GatherStat stat) => Math.Max(0, Caps[stat] - BaseStats[stat]);

    public string DisplayName => IsHq ? $"{Name} (HQ)" : Name;
}

/// <summary>
/// Overmeld success, percent, by materia grade and overmeld slot, from MateriaJoinRateGatherCraft.
/// The sheet has rows for grades I–X only; XI and XII take the rate of the grade two below (odd
/// grades overmeld everywhere, even grades only in the first overmeld) — Teamcraft's
/// <c>dohdolMeldingRates</c> table (MIT) does the same. To be checked in game.
/// </summary>
public sealed class OvermeldRates
{
    private readonly int[][] _nq;
    private readonly int[][] _hq;

    public OvermeldRates(IReadOnlyList<int[]> nqByGradeIndex, IReadOnlyList<int[]> hqByGradeIndex)
    {
        if (nqByGradeIndex.Count == 0 || nqByGradeIndex.Count != hqByGradeIndex.Count)
            throw new ArgumentException("NQ and HQ tables must be non-empty and the same length.");
        _nq = nqByGradeIndex.Select(r => r.ToArray()).ToArray();
        _hq = hqByGradeIndex.Select(r => r.ToArray()).ToArray();
    }

    public static OvermeldRates FromSheet(IEnumerable<MateriaJoinRateRow> rows)
    {
        var ordered = rows.OrderBy(r => r.Id).ToList();
        return new OvermeldRates(
            ordered.Select(r => r.NqOvermeldPercent.ToArray()).ToList(),
            ordered.Select(r => r.HqOvermeldPercent.ToArray()).ToList());
    }

    /// <summary>Success percent (0–100) for a materia of <paramref name="grade"/> in overmeld slot 1–4.</summary>
    public int Percent(int grade, int overmeldSlot, bool hq)
    {
        var table = hq ? _hq : _nq;
        var index = grade - 1;
        if (index < 0)
            return 0;
        // Past the sheet: same parity, two grades down, until a real row (XI → IX, XII → X).
        while (index >= table.Length)
            index -= 2;
        if (index < 0)
            return 0;
        var row = table[index];
        var slot = overmeldSlot - 1;
        return slot >= 0 && slot < row.Length ? row[slot] : 0;
    }

    /// <summary>True when no rate improves at a later overmeld slot, which the cost bound relies on.</summary>
    internal bool IsNonIncreasing(int maxGrade)
    {
        for (var grade = 1; grade <= maxGrade; grade++)
        {
            foreach (var hq in new[] { false, true })
            {
                for (var slot = 2; slot <= 4; slot++)
                {
                    if (Percent(grade, slot, hq) > Percent(grade, slot - 1, hq))
                        return false;
                }
            }
        }

        return true;
    }
}

/// <summary>What one food does to one stat.</summary>
/// <param name="Percent">For a relative effect, percent of the stat; for a flat one, the value added.</param>
public sealed record FoodEffect(GatherStat Stat, bool IsRelative, int Percent, int Max);

/// <summary>A food the optimizer may pick, NQ or HQ (they are separate options with separate values).</summary>
/// <param name="Owned">How many are in the bags; 0 means it would have to be bought or cooked.</param>
public sealed record FoodOption(uint ItemId, string Name, bool IsHq, IReadOnlyList<FoodEffect> Effects, int Owned = 0)
{
    public string DisplayName => IsHq ? $"{Name} (HQ)" : Name;

    /// <summary>
    /// What the food adds to a stat whose total before food is <paramref name="total"/>. Relative
    /// effects round down (to be checked in game; the in-game tooltip shows the capped value).
    /// </summary>
    public int Bonus(GatherStat stat, int total)
    {
        var bonus = 0;
        foreach (var effect in Effects)
        {
            if (effect.Stat != stat)
                continue;
            bonus += effect.IsRelative
                ? Math.Min((int)Math.Floor(Math.Max(0, total) * (long)effect.Percent / 100.0), effect.Max)
                : effect.Percent;
        }

        return bonus;
    }

    public GatherStats Apply(GatherStats totals)
        => totals + new GatherStats(
            Bonus(GatherStat.Gathering, totals.Gathering),
            Bonus(GatherStat.Perception, totals.Perception),
            Bonus(GatherStat.Gp, totals.Gp));

    /// <summary>The smallest total before food that reaches <paramref name="target"/> with this food eaten.</summary>
    public int RequiredBefore(GatherStat stat, int target) => RequiredBefore(this, stat, target);

    internal static int RequiredBefore(FoodOption? food, GatherStat stat, int target)
    {
        if (target <= 0)
            return 0;
        if (food == null)
            return target;
        // total + bonus(total) never falls as total rises, so the first total that reaches is a binary search.
        int lo = 0, hi = target;
        while (lo < hi)
        {
            var mid = lo + (hi - lo) / 2;
            if (mid + food.Bonus(stat, mid) >= target)
                hi = mid;
            else
                lo = mid + 1;
        }

        return lo;
    }

    /// <summary>A food option from its ItemFood row. Params that are not gatherer stats are dropped.</summary>
    public static FoodOption FromSheet(uint itemId, string name, ItemFoodRow row, bool hq, int owned = 0)
    {
        var effects = new List<FoodEffect>();
        foreach (var p in row.Params)
        {
            if (!GatherStats.TryFromBaseParam(p.BaseParam, out var stat))
                continue;
            effects.Add(new FoodEffect(stat, p.IsRelative, hq ? p.ValueHq : p.Value, hq ? p.MaxHq : p.Max));
        }

        return new FoodOption(itemId, name, hq, effects, owned);
    }
}

/// <summary>Knobs. The defaults plan with what the gatherer has.</summary>
/// <param name="AllowUnownedGear">Consider sheet pieces the gatherer does not own (marked "acquire" in the plan).</param>
/// <param name="AllowUnownedFood">Consider foods with none in the bags.</param>
/// <param name="ShoppingConfidence">For buying materia: enough to land each overmeld with this probability.</param>
/// <param name="NodeBudget">
/// Search nodes for the plan with unlimited materia before it settles for its best so far (and says
/// so). The tests' full Lv100 sets prove optimal in under 130,000.
/// </param>
/// <param name="StockNodeBudget">
/// The same for the plan limited to the bags, which is much wider (each stack is tracked) and is
/// rarely proven within any budget: it starts from the unlimited plan cut to fit, and every slot pair
/// is re-solved before and after the search, so a small budget is enough.
/// </param>
public sealed record MeldOptions(
    bool AllowUnownedGear = false,
    bool AllowUnownedFood = true,
    double ShoppingConfidence = 0.9,
    long NodeBudget = 500_000,
    long StockNodeBudget = 20_000);

/// <summary>Everything the optimizer needs. Immutable; safe to hand to a background task.</summary>
/// <param name="Candidates">Pieces per slot — owned (with melds) and, if allowed, from the sheet.</param>
/// <param name="Materia">The materia types that may be melded (stock and shop alike).</param>
/// <param name="Stock">Materia in the bags, by materia item id.</param>
/// <param name="CharacterBase">The gatherer's stats with nothing equipped (live total minus gear, read by the adapter).</param>
/// <param name="Priority">Which target to give up last. Null = <see cref="ClassPriority.Default"/>.</param>
public sealed record MeldRequest(
    GatherJob Job,
    IReadOnlyList<GearCandidate> Candidates,
    TargetSet Targets,
    IReadOnlyList<MateriaGrade> Materia,
    IReadOnlyDictionary<uint, int> Stock,
    OvermeldRates Rates,
    IReadOnlyList<FoodOption> Foods,
    GatherStats CharacterBase,
    IReadOnlyList<GatherStat>? Priority = null,
    MeldOptions? Options = null);
