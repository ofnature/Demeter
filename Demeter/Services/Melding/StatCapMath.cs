using System;

namespace Demeter.Services.Melding;

/// <summary>
/// Per-piece stat caps: a stat cannot be melded past what the piece's item level allows in its slot.
///
/// <para>
/// Ported from Daedalus (<c>ofnature/Daedalus</c>, <c>Daedalus/Services/Gear/StatCapMath.cs</c>, LGPL-3.0,
/// same author): <c>cap = round(ItemLevel[stat] × BaseParam slot‰ / 1000)</c>, rounded half up because
/// banker's rounding gives off-by-ones against the game. Teamcraft (<c>ffxiv-teamcraft</c>,
/// <c>materia.service.ts</c> <c>getItemCapForStat</c>, MIT) divides by the item's
/// <c>BaseParam.MeldParam[Item.BaseParamModifier]</c> instead of a flat 100; that is kept here so an
/// item class with a different divisor comes out right. For Gathering, Perception and GP every
/// MeldParam is 100 today, so both give the same numbers.
/// </para>
/// </summary>
public static class StatCapMath
{
    /// <param name="ilvlStatBase">ItemLevel value for the stat at the piece's item level.</param>
    /// <param name="slotPercent">BaseParam slot percentage, per mille (body Gathering = 200).</param>
    /// <param name="meldParam">BaseParam <c>MeldParam[Item.BaseParamModifier]</c>; 0 or less reads as 100.</param>
    public static int Cap(int ilvlStatBase, int slotPercent, int meldParam = 100)
    {
        if (ilvlStatBase <= 0 || slotPercent <= 0)
            return 0;
        if (meldParam <= 0)
            meldParam = 100;

        return (int)Math.Floor(ilvlStatBase * (double)slotPercent / (meldParam * 10.0) + 0.5);
    }

    /// <summary>The BaseParam percentage column for an EquipSlotCategory row (0 for slots that carry no stats).</summary>
    public static int SlotPercent(BaseParamRow row, uint equipSlotCategory) => equipSlotCategory switch
    {
        1 => row.OneHandWeaponPercent,
        2 => row.OffHandPercent,
        3 => row.HeadPercent,
        4 => row.ChestPercent,
        5 => row.HandsPercent,
        7 => row.LegsPercent,
        8 => row.FeetPercent,
        9 => row.EarringPercent,
        10 => row.NecklacePercent,
        11 => row.BraceletPercent,
        12 => row.RingPercent,
        13 => row.TwoHandWeaponPercent,
        _ => 0,
    };
}
