using Demeter.Services.Melding;
using static Demeter.Tests.MeldingFixtures;

namespace Demeter.Tests;

/// <summary>The sheet-to-input builders: gear stats and caps, materia, overmeld rates, food.</summary>
public class MeldingSheetsTests
{
    [Fact]
    public void CrestedHq_HasExactlyStarTechsBaseStats_ButOnlyCrestedOvermelds()
    {
        var pairs = new (uint Crested, uint StarTech)[]
        {
            (CrestedHood, 49352), (CrestedCoat, StarTechCoat), (CrestedGloves, 49354), (CrestedSlops, 49355),
            (CrestedBoots, 49356), (CrestedEarrings, 49361), (CrestedNecklace, 49362), (CrestedBracelet, 49363),
            (CrestedRing, StarTechRing), (GoldThumbPickaxe, StarTechPickaxe), (GoldThumbSledgehammer, StarTechSledgehammer),
        };
        foreach (var (crested, starTech) in pairs)
        {
            var c = Piece(crested, hq: true);
            var s = Piece(starTech);
            Assert.Equal(s.BaseStats, c.BaseStats);
            Assert.Equal(s.MateriaSlots, c.MateriaSlots);
            Assert.True(c.AdvancedMeldingPermitted);
            Assert.False(s.AdvancedMeldingPermitted);
            Assert.Equal(5, c.TotalSockets);
        }

        // NQ Crested is the sheet's BaseParamValue alone.
        Assert.Equal(new GatherStats(853, 426, 8), Piece(CrestedCoat, hq: false).BaseStats);
    }

    [Fact]
    public void RelicTool_OutclassesGoldThumbHq_OnBase()
    {
        var relic = Piece(PickaxeOfStars);
        var goldThumb = Piece(GoldThumbPickaxe, hq: true);

        Assert.Equal(new GatherStats(1798, 1028, 10), relic.BaseStats);
        Assert.Equal(0, relic.MateriaSlots);
        Assert.False(relic.AdvancedMeldingPermitted);
        Assert.Equal(new GatherStats(1691, 966, 0), goldThumb.BaseStats);
        Assert.Equal(1, goldThumb.MateriaSlots);
        Assert.True(goldThumb.AdvancedMeldingPermitted);
    }

    [Fact]
    public void Caps_AreItemLevelTimesSlotPercent_RoundedHalfUp()
    {
        // i750: Gathering/Perception base 5685, GP 530 (ItemLevel), × BaseParam slot ‰ / 1000.
        Assert.Equal(new GatherStats(1990, 1137, 11), Piece(GoldThumbPickaxe).Caps); // 5685×350, ×200, 530×20
        Assert.Equal(new GatherStats(1137, 569, 11), Piece(CrestedCoat).Caps); // body 200 / 100 → 1137, 568.5 → 569
        Assert.Equal(new GatherStats(57, 625, 21), Piece(CrestedBoots).Caps); // feet 10 / 110 / 40
        Assert.Equal(new GatherStats(57, 57, 133), Piece(CrestedEarrings).Caps); // 530×250 = 132.5 → 133
        Assert.Equal(new GatherStats(57, 57, 69), Piece(CrestedRing).Caps); // 530×130 = 68.9 → 69

        Assert.Equal(171, Piece(CrestedCoat).Headroom(GatherStat.Gathering)); // 1137 − 966
        Assert.Equal(20, Piece(CrestedEarrings).Headroom(GatherStat.Gp)); // 133 − 113
        // The relic's ilvl 780 row has GP 0: no GP cap, so no GP to meld — but its own 10 GP stay.
        Assert.Equal(0, Piece(PickaxeOfStars).Headroom(GatherStat.Gp));
        Assert.Equal(1069, StatCapMath.Cap(5345, 200, 100));
        Assert.Equal(StatCapMath.Cap(5685, 350), StatCapMath.Cap(5685, 350, 0)); // a missing divisor reads as 100
    }

    [Fact]
    public void Materia_GuerdonGuileGrasp_XiAndXii()
    {
        var top = TopMateria;
        Assert.Equal(6, top.Count);
        Assert.Equal(36, Materia(GuerdonXii).Value);
        Assert.Equal(20, Materia(GuerdonXi).Value);
        Assert.Equal(36, Materia(GuileXii).Value);
        Assert.Equal(20, Materia(GuileXi).Value);
        Assert.Equal(11, Materia(GraspXii).Value);
        Assert.Equal(9, Materia(GraspXi).Value);
        Assert.All(top, m => Assert.Equal(690, m.MinItemLevel));
        Assert.Equal(GatherStat.Perception, Materia(GuileXii).Stat);
        Assert.Equal(12, Materia(GuileXii).Grade);
    }

    [Fact]
    public void OvermeldRates_FromTheSheet_XiAndXiiByParity()
    {
        var rates = Rates;
        // HQ: odd grades 17/10/7/5, even grades 17/0/0/0.
        Assert.Equal(new[] { 17, 10, 7, 5 }, Enumerable.Range(1, 4).Select(s => rates.Percent(11, s, hq: true)));
        Assert.Equal(new[] { 17, 0, 0, 0 }, Enumerable.Range(1, 4).Select(s => rates.Percent(12, s, hq: true)));
        // NQ odd 12/6/3/2.
        Assert.Equal(new[] { 12, 6, 3, 2 }, Enumerable.Range(1, 4).Select(s => rates.Percent(11, s, hq: false)));
        Assert.Equal(new[] { 12, 0, 0, 0 }, Enumerable.Range(1, 4).Select(s => rates.Percent(12, s, hq: false)));
        // The sheet's own rows for the low grades.
        Assert.Equal(90, rates.Percent(1, 1, hq: true));
        Assert.Equal(17, rates.Percent(9, 1, hq: true));
        Assert.Equal(0, rates.Percent(9, 5, hq: true));
    }

    [Fact]
    public void Food_IsAPercentOfTheStat_UpToItsMaximum()
    {
        var pie = Food(GoldentailPie, hq: true);
        Assert.Equal(188, pie.Bonus(GatherStat.Gathering, 5400)); // 7% = 378, capped at 188
        Assert.Equal(97, pie.Bonus(GatherStat.Perception, 5400)); // 3% = 162, capped at 97
        Assert.Equal(70, pie.Bonus(GatherStat.Gathering, 1000)); // 7% of 1000
        Assert.Equal(0, pie.Bonus(GatherStat.Gp, 900));

        var nq = Food(GoldentailPie, hq: false);
        Assert.Equal(150, nq.Bonus(GatherStat.Gathering, 5400)); // NQ 6% ≤ 150

        Assert.Equal(176, Food(CloudsailMeuniere).Bonus(GatherStat.Perception, 5400));
        Assert.Equal(91, Food(CloudsailMeuniere).Bonus(GatherStat.Gathering, 5400));
        Assert.Equal(32, Food(StuffedPeppers).Bonus(GatherStat.Gp, 900)); // 10% ≤ 32
        Assert.Equal(157, Food(StuffedPeppers).Bonus(GatherStat.Perception, 5400));
        Assert.Equal(58, Food(NasiGoreng).Bonus(GatherStat.Gp, 900)); // 29% ≤ 58
        Assert.Equal(80, Food(NasiGoreng).Bonus(GatherStat.Gathering, 5400));

        // The inverse: the least total before food that reaches a target with the food eaten.
        Assert.Equal(5272, pie.RequiredBefore(GatherStat.Gathering, 5460));
        Assert.Equal(5460, FoodOption.RequiredBefore(null, GatherStat.Gathering, 5460));
        var needed = pie.RequiredBefore(GatherStat.Gathering, 1070);
        Assert.True(needed + pie.Bonus(GatherStat.Gathering, needed) >= 1070);
        Assert.True(needed - 1 + pie.Bonus(GatherStat.Gathering, needed - 1) < 1070);
    }

    [Fact]
    public void Catalog_FromSheet_IsTheJobsGear_HqWherePossible()
    {
        var miner = GearCatalog.FromSheet(GatherJob.Miner, Items.Values, Categories, ItemLevels, BaseParams, minItemLevel: 750);

        Assert.Contains(miner, c => c.ItemId == GoldThumbPickaxe && c.IsHq);
        Assert.Contains(miner, c => c.ItemId == PickaxeOfStars && !c.IsHq);
        Assert.DoesNotContain(miner, c => c.ItemId == 47172); // Gold Thumb's Hatchet is the Botanist's
        Assert.DoesNotContain(miner, c => c.Slot == GearSlot.OffHand && c.ItemId == 47183); // the Botanist's scythe
        Assert.All(miner, c => Assert.True(c.ItemLevel >= 750));
        Assert.Contains(miner, c => c.Slot == GearSlot.RingRight && c.ItemId == CrestedRing);

        // A Fisher has no off-hand at this level.
        var fisher = GearCatalog.FromSheet(GatherJob.Fisher, Items.Values, Categories, ItemLevels, BaseParams, minItemLevel: 750);
        Assert.DoesNotContain(fisher, c => c.Slot == GearSlot.OffHand);
        Assert.Equal(new[] { GatherStat.Gp, GatherStat.Perception, GatherStat.Gathering }, ClassPriority.Default(GatherJob.Fisher));
        Assert.Equal(new[] { GatherStat.Gathering, GatherStat.Perception, GatherStat.Gp }, ClassPriority.Default(GatherJob.Botanist));
        Assert.Equal(new[] { GatherStat.Perception, GatherStat.Gathering, GatherStat.Gp }, ClassPriority.Normalize(new[] { GatherStat.Perception }, GatherJob.Miner));
    }
}
