using System.Collections.Generic;

namespace Demeter.Services.Melding;

// The game sheets the optimizer reads, as plain records. Each mirrors the sheet's own columns (names as
// in xivapi/ffxiv-datamining's csv headers) so the in-game adapter copies Lumina rows across field for
// field, and the tests fill them from the same csv files. Nothing here knows about Lumina or Dalamud.

/// <summary>One stat on an item: <c>BaseParam[i]</c> with <c>BaseParamValue[i]</c> (or the Special pair, the HQ bonus).</summary>
public readonly record struct ItemParam(uint BaseParam, int Value);

/// <summary>The Item columns gear selection needs.</summary>
/// <param name="BaseParams">Non-zero <c>BaseParam[0..5]</c> / <c>BaseParamValue[0..5]</c> pairs: the NQ stats.</param>
/// <param name="SpecialParams">Non-zero <c>BaseParamSpecial[0..5]</c> / <c>BaseParamValueSpecial[0..5]</c>: added on HQ.</param>
/// <param name="BaseParamModifier">Index into BaseParam's <c>MeldParam[]</c> for this item's caps (Teamcraft's "modifier").</param>
public sealed record ItemRow(
    uint Id,
    string Name,
    int LevelItem,
    int LevelEquip,
    uint ClassJobCategory,
    uint EquipSlotCategory,
    IReadOnlyList<ItemParam> BaseParams,
    IReadOnlyList<ItemParam> SpecialParams,
    int BaseParamModifier,
    int MateriaSlotCount,
    bool IsAdvancedMeldingPermitted,
    bool CanBeHq,
    bool IsUnique);

/// <summary>ClassJobCategory, only the gatherer columns.</summary>
public sealed record ClassJobCategoryRow(uint Id, bool Miner, bool Botanist, bool Fisher)
{
    public bool Includes(GatherJob job) => job switch
    {
        GatherJob.Miner => Miner,
        GatherJob.Botanist => Botanist,
        GatherJob.Fisher => Fisher,
        _ => false,
    };
}

/// <summary>ItemLevel: the per-ilvl stat base the caps scale from.</summary>
public sealed record ItemLevelRow(int Level, int Gp, int Gathering, int Perception)
{
    public int For(GatherStat stat) => stat switch
    {
        GatherStat.Gp => Gp,
        GatherStat.Gathering => Gathering,
        GatherStat.Perception => Perception,
        _ => 0,
    };
}

/// <summary>BaseParam: per-equip-slot percentages (per mille) and the melding divisors.</summary>
public sealed record BaseParamRow(
    uint Id,
    string Name,
    int OneHandWeaponPercent,
    int OffHandPercent,
    int HeadPercent,
    int ChestPercent,
    int HandsPercent,
    int LegsPercent,
    int FeetPercent,
    int EarringPercent,
    int NecklacePercent,
    int BraceletPercent,
    int RingPercent,
    int TwoHandWeaponPercent,
    IReadOnlyList<int> MeldParam);

/// <summary>Materia: one row per stat, <c>Item[i]</c>/<c>Value[i]</c> per grade (index 0 = grade I).</summary>
public sealed record MateriaRow(uint Id, uint BaseParam, IReadOnlyList<uint> Items, IReadOnlyList<int> Values);

/// <summary>MateriaJoinRateGatherCraft: overmeld success percent by overmeld slot (1–4), NQ and HQ gear.</summary>
public sealed record MateriaJoinRateRow(int Id, IReadOnlyList<int> NqOvermeldPercent, IReadOnlyList<int> HqOvermeldPercent);

/// <summary>One ItemFood param: a percent of the stat up to a maximum (relative), or a flat value.</summary>
public sealed record ItemFoodParam(uint BaseParam, bool IsRelative, int Value, int Max, int ValueHq, int MaxHq);

/// <summary>ItemFood (reached from the food item through ItemAction's <c>Data[1]</c>).</summary>
public sealed record ItemFoodRow(uint Id, IReadOnlyList<ItemFoodParam> Params);

/// <summary>GatheringPoint: which bonus rows a node carries.</summary>
/// <param name="Type">The point type: 1 regular, 5 legendary, 7 Diadem, … (the sheet's own column).</param>
public sealed record GatheringPointRow(uint Id, uint GatheringPointBase, IReadOnlyList<uint> Bonuses, uint TerritoryType, int Type);

/// <summary>GatheringPointBonus: "if stat ≥ ConditionValue, then BonusType +BonusValue".</summary>
/// <param name="RangeTopConditionValue">
/// The sheet's unnamed column after BonusValue (csv <c>Unknown1</c>). On the ranged bonus types (the
/// ones whose text ends in "～") it reads as the stat at which the bonus reaches
/// <paramref name="RangeTopBonusValue"/> (csv <c>Unknown2</c>): Max GP ≥ 960 → integrity +1, rising to
/// +2 at 990. Interpreted, not named by the game — to be checked in game.
/// </param>
public sealed record GatheringPointBonusRow(
    uint Id,
    uint Condition,
    int ConditionValue,
    uint BonusType,
    int BonusValue,
    int RangeTopConditionValue = 0,
    int RangeTopBonusValue = 0)
{
    /// <summary>Both range columns set and above the start: a bonus that scales up to a second threshold.</summary>
    public bool IsRanged => RangeTopConditionValue > ConditionValue && RangeTopBonusValue > BonusValue;
}
