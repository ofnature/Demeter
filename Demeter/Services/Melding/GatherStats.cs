using System;
using System.Collections.Generic;

namespace Demeter.Services.Melding;

/// <summary>
/// The three gatherer stats the optimizer works on. The values ARE the BaseParam row ids, so an
/// adapter can cast a sheet's BaseParam straight to this and back.
/// </summary>
public enum GatherStat : uint
{
    Gp = 10,
    Gathering = 72,
    Perception = 73,
}

/// <summary>The gatherer jobs. Values are the ClassJob row ids.</summary>
public enum GatherJob : uint
{
    Miner = 16,
    Botanist = 17,
    Fisher = 18,
}

/// <summary>
/// Equipment slot in the game's EquippedItems container order (the belt slot, 5, no longer
/// exists). Values ARE the container indices, as in Daedalus's <c>GearSlotId</c>, so the adapter
/// reading the equipped container needs no table. A ring candidate fits either ring slot.
/// </summary>
public enum GearSlot
{
    MainHand = 0,
    OffHand = 1,
    Head = 2,
    Body = 3,
    Hands = 4,
    Legs = 6,
    Feet = 7,
    Ears = 8,
    Neck = 9,
    Wrists = 10,
    RingRight = 11,
    RingLeft = 12,
}

/// <summary>Gathering / Perception / GP as one value, so totals, caps and food can be added like numbers.</summary>
public readonly record struct GatherStats(int Gathering, int Perception, int Gp)
{
    public static readonly GatherStats Zero = new(0, 0, 0);

    /// <summary>Fixed order for array-indexed loops: Gathering, Perception, GP.</summary>
    public static readonly GatherStat[] All = [GatherStat.Gathering, GatherStat.Perception, GatherStat.Gp];

    public int this[GatherStat stat] => stat switch
    {
        GatherStat.Gathering => Gathering,
        GatherStat.Perception => Perception,
        GatherStat.Gp => Gp,
        _ => 0,
    };

    public GatherStats With(GatherStat stat, int value) => stat switch
    {
        GatherStat.Gathering => this with { Gathering = value },
        GatherStat.Perception => this with { Perception = value },
        GatherStat.Gp => this with { Gp = value },
        _ => this,
    };

    public static GatherStats operator +(GatherStats a, GatherStats b)
        => new(a.Gathering + b.Gathering, a.Perception + b.Perception, a.Gp + b.Gp);

    public static GatherStats operator -(GatherStats a, GatherStats b)
        => new(a.Gathering - b.Gathering, a.Perception - b.Perception, a.Gp - b.Gp);

    public static GatherStats Max(GatherStats a, GatherStats b)
        => new(Math.Max(a.Gathering, b.Gathering), Math.Max(a.Perception, b.Perception), Math.Max(a.Gp, b.Gp));

    /// <summary>Array index of a stat in <see cref="All"/> order.</summary>
    internal static int Index(GatherStat stat) => stat switch
    {
        GatherStat.Gathering => 0,
        GatherStat.Perception => 1,
        GatherStat.Gp => 2,
        _ => throw new ArgumentOutOfRangeException(nameof(stat)),
    };

    public static bool TryFromBaseParam(uint baseParam, out GatherStat stat)
    {
        stat = (GatherStat)baseParam;
        return baseParam is (uint)GatherStat.Gp or (uint)GatherStat.Gathering or (uint)GatherStat.Perception;
    }

    public static string Name(GatherStat stat) => stat switch
    {
        GatherStat.Gathering => "Gathering",
        GatherStat.Perception => "Perception",
        GatherStat.Gp => "GP",
        _ => stat.ToString(),
    };

    public override string ToString() => $"{Gathering} Gathering / {Perception} Perception / {Gp} GP";
}

/// <summary>
/// The order a class gives up targets in when all three cannot be reached. User-set; these are the
/// defaults: Miner and Botanist live on Gathering (yield and collectability) before Perception
/// (boon); a Fisher's GP buys the actions that land fish, so it comes first.
/// </summary>
public static class ClassPriority
{
    public static IReadOnlyList<GatherStat> Default(GatherJob job) => job switch
    {
        GatherJob.Fisher => [GatherStat.Gp, GatherStat.Perception, GatherStat.Gathering],
        _ => [GatherStat.Gathering, GatherStat.Perception, GatherStat.Gp],
    };

    /// <summary>A full, duplicate-free order: the given stats first, any left out appended in default order.</summary>
    public static IReadOnlyList<GatherStat> Normalize(IReadOnlyList<GatherStat>? order, GatherJob job)
    {
        var result = new List<GatherStat>(3);
        foreach (var stat in order ?? Default(job))
        {
            if (Array.IndexOf(GatherStats.All, stat) >= 0 && !result.Contains(stat))
                result.Add(stat);
        }

        foreach (var stat in Default(job))
        {
            if (!result.Contains(stat))
                result.Add(stat);
        }

        return result;
    }
}
