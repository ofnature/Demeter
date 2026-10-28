using System;
using System.Collections.Generic;
using System.Linq;

namespace Demeter.Services.Melding;

/// <summary>
/// Gear candidates from the Item sheet. No online best-in-slot list: a piece is worth considering
/// when its ClassJobCategory includes the job, and the optimizer itself weighs base stats (with the
/// HQ bonus), socket count, overmeld permission and caps against each other.
/// </summary>
public static class GearCatalog
{
    /// <summary>The slot an EquipSlotCategory row equips to; null for slots gatherers do not use (waist, soul crystal…).</summary>
    public static GearSlot? SlotFor(uint equipSlotCategory) => equipSlotCategory switch
    {
        1 or 13 => GearSlot.MainHand,
        2 => GearSlot.OffHand,
        3 => GearSlot.Head,
        4 => GearSlot.Body,
        5 => GearSlot.Hands,
        7 => GearSlot.Legs,
        8 => GearSlot.Feet,
        9 => GearSlot.Ears,
        10 => GearSlot.Neck,
        11 => GearSlot.Wrists,
        12 => GearSlot.RingRight,
        _ => null,
    };

    /// <summary>The piece's gatherer stats as worn: NQ values, plus the Special (HQ) values when <paramref name="hq"/>.</summary>
    public static GatherStats Stats(ItemRow item, bool hq)
    {
        var stats = GatherStats.Zero;
        foreach (var p in item.BaseParams)
        {
            if (GatherStats.TryFromBaseParam(p.BaseParam, out var stat))
                stats = stats.With(stat, stats[stat] + p.Value);
        }

        if (hq && item.CanBeHq)
        {
            foreach (var p in item.SpecialParams)
            {
                if (GatherStats.TryFromBaseParam(p.BaseParam, out var stat))
                    stats = stats.With(stat, stats[stat] + p.Value);
            }
        }

        return stats;
    }

    /// <summary>Per-stat caps from ItemLevel × BaseParam slot percentage (<see cref="StatCapMath"/>). Zero where a sheet row is missing.</summary>
    public static GatherStats Caps(ItemRow item, ItemLevelRow? level, IReadOnlyDictionary<uint, BaseParamRow> baseParams)
    {
        if (level == null)
            return GatherStats.Zero;
        var caps = GatherStats.Zero;
        foreach (var stat in GatherStats.All)
        {
            if (!baseParams.TryGetValue((uint)stat, out var param))
                continue;
            var meldParam = item.BaseParamModifier >= 0 && item.BaseParamModifier < param.MeldParam.Count
                ? param.MeldParam[item.BaseParamModifier]
                : 100;
            caps = caps.With(stat, StatCapMath.Cap(level.For(stat), StatCapMath.SlotPercent(param, item.EquipSlotCategory), meldParam));
        }

        return caps;
    }

    /// <summary>One candidate from an Item row. The adapter passes the ownership facts for an owned copy.</summary>
    public static GearCandidate? Candidate(
        ItemRow item,
        bool hq,
        ItemLevelRow? level,
        IReadOnlyDictionary<uint, BaseParamRow> baseParams,
        IReadOnlyList<MeldedMateria>? currentMelds = null,
        bool owned = false,
        bool equipped = false,
        ulong? instanceId = null)
    {
        var slot = SlotFor(item.EquipSlotCategory);
        if (slot == null)
            return null;
        var isHq = hq && item.CanBeHq;
        return new GearCandidate(
            slot.Value, item.Id, item.Name, item.LevelItem, isHq,
            Stats(item, isHq), Caps(item, level, baseParams),
            item.MateriaSlotCount, item.IsAdvancedMeldingPermitted,
            currentMelds, owned, equipped, item.IsUnique, instanceId);
    }

    /// <summary>
    /// Every sheet piece the job can wear between the given levels, HQ where the item can be HQ (an NQ
    /// copy is never better: same sockets, lower stats, lower overmeld rates). Sorted by slot, then id,
    /// so the optimizer's input — and so its answer — does not depend on sheet order.
    /// </summary>
    public static IReadOnlyList<GearCandidate> FromSheet(
        GatherJob job,
        IEnumerable<ItemRow> items,
        IReadOnlyDictionary<uint, ClassJobCategoryRow> categories,
        IReadOnlyDictionary<int, ItemLevelRow> itemLevels,
        IReadOnlyDictionary<uint, BaseParamRow> baseParams,
        int minItemLevel = 0,
        int maxEquipLevel = int.MaxValue)
    {
        var result = new List<GearCandidate>();
        foreach (var item in items)
        {
            if (item.LevelItem < minItemLevel || item.LevelEquip > maxEquipLevel)
                continue;
            if (!categories.TryGetValue(item.ClassJobCategory, out var category) || !category.Includes(job))
                continue;
            if (Stats(item, hq: false) == GatherStats.Zero)
                continue;
            itemLevels.TryGetValue(item.LevelItem, out var level);
            var candidate = Candidate(item, hq: true, level, baseParams);
            if (candidate != null)
                result.Add(candidate);
        }

        return result.OrderBy(c => c.Slot).ThenBy(c => c.ItemId).ToList();
    }
}

/// <summary>Materia types from the Materia sheet (one row per stat, one column per grade).</summary>
public static class MateriaCatalog
{
    /// <summary>
    /// The gatherer materia (Guerdon, Guile, Grasp) from <paramref name="minGrade"/> up, with names and
    /// melding requirements from their Item rows. A grade whose item row is missing is skipped.
    /// </summary>
    public static IReadOnlyList<MateriaGrade> FromSheet(
        IEnumerable<MateriaRow> materia, IReadOnlyDictionary<uint, ItemRow> items, int minGrade = 1)
    {
        var result = new List<MateriaGrade>();
        foreach (var row in materia)
        {
            if (!GatherStats.TryFromBaseParam(row.BaseParam, out var stat))
                continue;
            for (var i = 0; i < Math.Min(row.Items.Count, row.Values.Count); i++)
            {
                var grade = i + 1;
                if (grade < minGrade || row.Items[i] == 0 || row.Values[i] <= 0)
                    continue;
                if (!items.TryGetValue(row.Items[i], out var item))
                    continue;
                result.Add(new MateriaGrade(item.Id, item.Name, stat, grade, row.Values[i], item.LevelItem));
            }
        }

        return result.OrderBy(m => m.Stat).ThenBy(m => m.Grade).ToList();
    }
}
