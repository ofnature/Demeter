using System.Diagnostics;
using Demeter.Services.Melding;
using Xunit.Abstractions;
using static Demeter.Tests.MeldingFixtures;

namespace Demeter.Tests;

/// <summary>The optimizer on fixture sheet data (datamining 44948e4): gear choice, melds, food, stock, ranking.</summary>
public class MateriaOptimizerTests(ITestOutputHelper output)
{
    private static readonly GatherStats NoBase = GatherStats.Zero;

    private static IReadOnlyList<GearCandidate> SheetGear(GatherJob job, int minItemLevel = 750)
        => GearCatalog.FromSheet(job, Items.Values, Categories, ItemLevels, BaseParams, minItemLevel);

    private static MeldRequest Request(
        IReadOnlyList<GearCandidate> candidates,
        TargetSet targets,
        IReadOnlyDictionary<uint, int>? stock = null,
        IReadOnlyList<FoodOption>? foods = null,
        GatherJob job = GatherJob.Miner,
        IReadOnlyList<GatherStat>? priority = null,
        bool allowUnownedGear = false,
        GatherStats? characterBase = null)
        => new(job, candidates, targets, TopMateria, stock ?? PlentifulStock(), Rates, foods ?? [],
            characterBase ?? AssumedCharacterBase, priority, new MeldOptions(AllowUnownedGear: allowUnownedGear));

    private static Dictionary<uint, int> Stock(params (uint Item, int Count)[] stacks)
        => stacks.ToDictionary(s => s.Item, s => s.Count);

    private static IEnumerable<SocketPlan> Melds(SlotPlan slot)
        => slot.Sockets.Where(k => k.Action is SocketAction.Meld or SocketAction.RetrieveThenMeld);

    private void Dump(MeldPlan plan)
    {
        foreach (var s in plan.Slots)
        {
            output.WriteLine($"{s.Slot}: {s.Wear.DisplayName} → {s.Stats} (waste {s.Waste}) — {s.Why}");
            foreach (var k in s.Sockets.Where(k => k.Action != SocketAction.Empty))
                output.WriteLine($"    [{k.Index}{(k.IsOvermeld ? $" om{k.OvermeldSlot}" : "")}] {k.Action} {k.Materia?.Name} {k.SuccessPercent}% ≈{k.ExpectedMateria:0.##}");
        }

        output.WriteLine($"Food: {plan.FoodWhy}");
        foreach (var s in plan.Stats)
            output.WriteLine($"{s.Stat}: {s.Why}");
        foreach (var m in plan.Materia)
            output.WriteLine($"uses {m.Materia.Name}: {m.Sockets} socket(s), ≈{m.Expected:0.##} ({m.AtConfidence} at 90%), {m.InStock} held, buy {m.ToBuy}");
        foreach (var m in plan.ToAcquire)
            output.WriteLine($"acquire {m.Materia.Name}: {m.ToBuy} ({m.ToBuyAtConfidence} at 90%)");
        foreach (var n in plan.Notes)
            output.WriteLine($"note: {n}");
        output.WriteLine($"≈{plan.ExpectedMateria:0.##} materia (bound {plan.ExpectedMateriaLowerBound:0.##}), waste {plan.Waste}, {plan.NodesSearched} nodes, proven {plan.ProvenOptimal}");
    }

    // ── gear choice ──────────────────────────────────────────────────────────────

    [Fact]
    public void CrestedBeatsStarTech_WhenOvermeldsAreNeeded()
    {
        // Identical base stats (966 Gathering on the body); 1080 needs a third Gathering materia,
        // which only Crested's overmeld sockets can take.
        var candidates = new[] { Piece(StarTechCoat, owned: false), Piece(CrestedCoat, hq: true, owned: false) };
        var plan = MateriaOptimizer.Optimize(Request(candidates, TargetSet.Of(1080, 0, 0), allowUnownedGear: true, characterBase: NoBase));
        Dump(plan);

        var body = plan.For(GearSlot.Body)!;
        Assert.Equal(CrestedCoat, body.Wear.ItemId);
        Assert.True(body.Wear.IsHq);
        Assert.True(body.Acquire);
        Assert.Contains(Melds(body), k => k.IsOvermeld);
        Assert.Contains("only this one takes overmelds", body.Why);
        Assert.True(plan.AllReached);

        // Without overmelds Star Tech tops out at 966 + 72 and says so.
        var starTechOnly = MateriaOptimizer.Optimize(Request([Piece(StarTechCoat)], TargetSet.Of(1080, 0, 0), characterBase: NoBase));
        Assert.False(starTechOnly.AllReached);
        Assert.Equal(1038, starTechOnly.For(GatherStat.Gathering).Total);
        Assert.Equal(42, starTechOnly.For(GatherStat.Gathering).ShortBy);
        Assert.Contains("tops out at 1038", starTechOnly.For(GatherStat.Gathering).Why);
    }

    [Fact]
    public void RelicTool_BeatsGoldThumb_OnBase_UnlessMoreThanItsBaseIsNeeded()
    {
        var tools = new[] { Piece(GoldThumbPickaxe, hq: true), Piece(PickaxeOfStars) };

        // The relic's 1798 / 1028 / 10 for free.
        var plan = MateriaOptimizer.Optimize(Request(tools, TargetSet.Of(1798, 1028, 0), characterBase: NoBase));
        var main = plan.For(GearSlot.MainHand)!;
        Assert.Equal(PickaxeOfStars, main.Wear.ItemId);
        Assert.Empty(Melds(main));
        Assert.Equal(0, plan.ExpectedMateria);
        Assert.Contains("Higher base stats than Gold Thumb's Pickaxe (HQ)", main.Why);

        // 1810 Gathering is past the relic (0 sockets): only a melded Gold Thumb gets there (1691 + up to 299).
        var more = MateriaOptimizer.Optimize(Request(tools, TargetSet.Of(1810, 0, 0), characterBase: NoBase));
        Assert.Equal(GoldThumbPickaxe, more.For(GearSlot.MainHand)!.Wear.ItemId);
        Assert.True(more.AllReached);
    }

    [Fact]
    public void Caps_AreNeverExceeded_AndOverflowIsCountedAsWaste()
    {
        // Ask for more than every piece can hold so the optimizer is pushed against every cap.
        var plan = MateriaOptimizer.Optimize(Request(SheetGear(GatherJob.Miner), TargetSet.Of(6500, 6500, 1200), allowUnownedGear: true));
        Dump(plan);

        Assert.NotEmpty(plan.Slots);
        foreach (var slot in plan.Slots)
        {
            var piece = slot.Wear;
            var overflow = 0;
            foreach (var stat in GatherStats.All)
            {
                Assert.True(slot.Stats[stat] <= Math.Max(piece.Caps[stat], piece.BaseStats[stat]), $"{slot.Slot} {stat} over its cap");
                var melded = slot.Sockets
                    .Where(k => k.Action is SocketAction.Keep or SocketAction.Meld or SocketAction.RetrieveThenMeld)
                    .Where(k => k.Materia?.Stat == stat)
                    .Sum(k => k.Materia!.Value);
                overflow += melded - (slot.Stats[stat] - piece.BaseStats[stat]);
            }

            Assert.Equal(overflow, slot.Waste);
        }

        // Even grades never sit past the first overmeld (their rate there is 0%).
        Assert.DoesNotContain(plan.Slots.SelectMany(s => s.Sockets), k => k.Materia?.Grade == 12 && k.OvermeldSlot > 1);
    }

    // ── food ─────────────────────────────────────────────────────────────────────

    [Fact]
    public void Food_ClosesAGapTheGearCannot()
    {
        // Crested Hood (HQ): 966 Perception; five sockets add at most 148 (1114). 1190 is past that:
        // only Perception food bridges it.
        var hood = new[] { Piece(CrestedHood, hq: true) };
        var plan = MateriaOptimizer.Optimize(Request(hood, TargetSet.Of(0, 1190, 0), foods: CurrentFoods(), characterBase: NoBase));
        Dump(plan);

        Assert.True(plan.AllReached);
        Assert.Equal(CloudsailMeuniere, plan.Food!.ItemId); // 7% Perception; the pie's 3% is not enough
        Assert.True(plan.For(GatherStat.Perception).Food > 0);
        Assert.Contains("Cloudsail Meuniere (HQ)", plan.FoodWhy);

        // Without food the same gear is short, and says the gear tops out.
        var hungry = MateriaOptimizer.Optimize(Request(hood, TargetSet.Of(0, 1190, 0), characterBase: NoBase));
        Assert.False(hungry.AllReached);
        Assert.Equal(1114, hungry.For(GatherStat.Perception).Total);
        Assert.Contains("tops out at 1114", hungry.For(GatherStat.Perception).Why);
    }

    [Fact]
    public void Food_IsChosenWhenItSavesMateria_AndSkippedWhenItDoesNot()
    {
        var hood = new[] { Piece(CrestedHood, hq: true) };
        // 1030 Perception: two Guile without food; with Cloudsail (7% of 966 = 67) none at all.
        var fed = MateriaOptimizer.Optimize(Request(hood, TargetSet.Of(0, 1030, 0), foods: CurrentFoods(), characterBase: NoBase));
        Assert.Equal(CloudsailMeuniere, fed.Food!.ItemId);
        Assert.Equal(0, fed.ExpectedMateria);

        // 966 is the hood's own Perception: food saves nothing, so none is picked.
        var plain = MateriaOptimizer.Optimize(Request(hood, TargetSet.Of(0, 966, 0), foods: CurrentFoods(), characterBase: NoBase));
        Assert.Null(plain.Food);
        Assert.StartsWith("No food", plain.FoodWhy);
    }

    // ── ranking ──────────────────────────────────────────────────────────────────

    [Fact]
    public void Priority_DecidesWhichTargetGivesWay_WhenAllThreeCannotBeReached()
    {
        // The tops of the sheet's ranged bonuses: reachable one at a time, not together.
        var targets = GatheringTargets.Highest(Bonuses, Points, includeRangeTop: true);
        Assert.Equal("5886 / 5760 / 990", targets.ToString());

        var miner = MateriaOptimizer.Optimize(Request(SheetGear(GatherJob.Miner), targets, foods: CurrentFoods(), allowUnownedGear: true));
        Dump(miner);
        Assert.True(miner.For(GatherStat.Gathering).Reached);
        Assert.True(miner.For(GatherStat.Gp).Reached);
        Assert.False(miner.For(GatherStat.Perception).Reached);
        Assert.Contains("go to Gathering first", miner.For(GatherStat.Perception).Why);
        Assert.Equal(new[] { GatherStat.Gathering, GatherStat.Perception, GatherStat.Gp }, miner.Stats.Select(s => s.Stat));

        var perceptionFirst = MateriaOptimizer.Optimize(Request(SheetGear(GatherJob.Miner), targets, foods: CurrentFoods(),
            priority: [GatherStat.Perception, GatherStat.Gathering, GatherStat.Gp], allowUnownedGear: true));
        Dump(perceptionFirst);
        Assert.True(perceptionFirst.For(GatherStat.Perception).Reached);
        Assert.False(perceptionFirst.For(GatherStat.Gathering).Reached);
        Assert.Contains("go to Perception first", perceptionFirst.For(GatherStat.Gathering).Why);
    }

    // ── stock ────────────────────────────────────────────────────────────────────

    [Fact]
    public void Stock_ShortOfGuerdonXii_FallsBackToXi()
    {
        // 966 + 72 Gathering on the coat: two XII would do it; with one XII the rest is XI, overmeld included.
        var coat = new[] { Piece(CrestedCoat, hq: true) };
        var plan = MateriaOptimizer.Optimize(Request(coat, TargetSet.Of(1038, 0, 0), stock: Stock((GuerdonXii, 1), (GuerdonXi, 10)), characterBase: NoBase));
        Dump(plan);

        Assert.True(plan.AllReached);
        var melds = Melds(plan.For(GearSlot.Body)!).ToList();
        Assert.Single(melds, k => k.Materia!.ItemId == GuerdonXii);
        Assert.Contains(melds, k => k.Materia!.ItemId == GuerdonXi && k.IsOvermeld);
        Assert.Empty(plan.ToAcquire);
        Assert.Contains(plan.Notes, n => n.Contains("Gatherer's Guerdon Materia XII: all 1 in the bags used"));
        Assert.All(plan.Materia, m => Assert.True(Math.Ceiling(m.Expected - 1e-6) <= m.InStock));
    }

    [Fact]
    public void Stock_TooShort_ReportsWhatIsMissing()
    {
        // One XII and two XI cannot cover 72 on the coat (an overmeld costs ≈5.9 XI on average).
        var coat = new[] { Piece(CrestedCoat, hq: true) };
        var plan = MateriaOptimizer.Optimize(Request(coat, TargetSet.Of(1038, 0, 0), stock: Stock((GuerdonXii, 1), (GuerdonXi, 2)), characterBase: NoBase));
        Dump(plan);

        var gathering = plan.For(GatherStat.Gathering);
        Assert.False(gathering.Reached);
        Assert.Equal(1022, gathering.Total); // 966 + XII 36 + XI 20
        Assert.Contains("bags run short", gathering.Why);
        var buy = Assert.Single(plan.ToAcquire);
        Assert.Equal(GuerdonXii, buy.Materia.ItemId);
        Assert.Equal(1, buy.ToBuy);
        Assert.NotNull(plan.WithPurchases);
        Assert.True(plan.WithPurchases!.AllReached);
    }

    // ── overmelds ────────────────────────────────────────────────────────────────

    [Fact]
    public void Overmelds_CostOneOverTheirSuccessRate()
    {
        // 966 + 148 Gathering fills all five sockets: XII, XII, XII (om1 17%), XI (om2 10%), XI (om3 7%).
        var coat = new[] { Piece(CrestedCoat, hq: true) };
        var plan = MateriaOptimizer.Optimize(Request(coat, TargetSet.Of(1114, 0, 0), characterBase: NoBase));
        Dump(plan);

        var sockets = plan.For(GearSlot.Body)!.Sockets;
        Assert.Equal(new[] { 100, 100, 17, 10, 7 }, sockets.Select(k => k.SuccessPercent));
        Assert.Equal(new[] { 0, 0, 1, 2, 3 }, sockets.Select(k => k.OvermeldSlot));
        Assert.Equal(new uint[] { GuerdonXii, GuerdonXii, GuerdonXii, GuerdonXi, GuerdonXi }, sockets.Select(k => k.Materia!.ItemId));
        Assert.Equal(100.0 / 17, sockets[2].ExpectedMateria, 6);
        Assert.Equal(1 + 1 + 100.0 / 17 + 10 + 100.0 / 7, plan.ExpectedMateria, 6);
        // Bringing enough for a 90% chance per overmeld (Teamcraft's formula).
        Assert.Equal(new[] { 1, 1, 13, 22, 32 }, sockets.Select(k => k.AttemptsAtConfidence));
        var xi = plan.Materia.Single(m => m.Materia.ItemId == GuerdonXi);
        Assert.Equal(10 + 100.0 / 7, xi.Expected, 6);
        Assert.Equal(54, xi.AtConfidence);

        // The NQ coat overmelds at the NQ rates: 12% for the first.
        var nq = MateriaOptimizer.Optimize(Request([Piece(CrestedCoat, hq: false)], TargetSet.Of(853 + 108, 0, 0), characterBase: NoBase));
        Assert.Equal(12, nq.For(GearSlot.Body)!.Sockets[2].SuccessPercent);
    }

    // ── current melds ────────────────────────────────────────────────────────────

    [Fact]
    public void CurrentMelds_AreKeptWhenRight_AndRetrievedWhenNot()
    {
        var melded = Piece(CrestedCoat, hq: true, equipped: true, melds: [Melded(GuerdonXii), Melded(GuerdonXii)]);
        var keep = MateriaOptimizer.Optimize(Request([melded], TargetSet.Of(1038, 0, 0), characterBase: NoBase));
        var body = keep.For(GearSlot.Body)!;
        Assert.Equal(new[] { SocketAction.Keep, SocketAction.Keep }, body.Sockets.Take(2).Select(k => k.Action));
        Assert.Equal(0, keep.ExpectedMateria);
        Assert.False(body.Swap);

        // Guile where Gathering is wanted: retrieve it and meld Guerdon.
        var wrong = Piece(CrestedCoat, hq: true, equipped: true, melds: [Melded(GuerdonXii), Melded(GuileXii)]);
        var fix = MateriaOptimizer.Optimize(Request([wrong], TargetSet.Of(1038, 0, 0), characterBase: NoBase));
        var sockets = fix.For(GearSlot.Body)!.Sockets;
        Assert.Equal(SocketAction.Keep, sockets[0].Action);
        Assert.Equal(SocketAction.RetrieveThenMeld, sockets[1].Action);
        Assert.Equal(GuerdonXii, sockets[1].Materia!.ItemId);
        Assert.True(fix.AllReached);
    }

    [Fact]
    public void Rings_OneOwnedCopyIsWornOnce()
    {
        // GP from both rings is needed (59 each); the one owned Crested ring cannot fill both slots.
        var owned = Piece(CrestedRing, hq: true, instance: 1);
        var other = Piece(StarTechRing, instance: 2);
        var plan = MateriaOptimizer.Optimize(Request([owned, other], TargetSet.Of(0, 0, 118), characterBase: NoBase));

        var rings = new[] { plan.For(GearSlot.RingRight)!, plan.For(GearSlot.RingLeft)! };
        Assert.Contains(rings, r => r.Wear.ItemId == CrestedRing);
        Assert.Contains(rings, r => r.Wear.ItemId == StarTechRing);
        Assert.True(plan.AllReached);
    }

    // ── the whole set ────────────────────────────────────────────────────────────

    [Fact]
    public void FullLv100MinerSet_ReachesTheBriefsHighestTargets()
    {
        var request = Request(SheetGear(GatherJob.Miner), TargetSet.Of(5460, 5600, 960, "the brief's highest"),
            foods: CurrentFoods(), allowUnownedGear: true);

        var watch = Stopwatch.StartNew();
        var plan = MateriaOptimizer.Optimize(request);
        watch.Stop();
        output.WriteLine($"run time {watch.ElapsedMilliseconds} ms");
        Dump(plan);

        Assert.True(plan.AllReached);
        Assert.True(plan.ProvenOptimal);
        Assert.Equal(plan.ExpectedMateria, plan.ExpectedMateriaLowerBound!.Value, 6);
        Assert.Equal(12, plan.Slots.Count); // two rings, off-hand included
        Assert.Equal(PickaxeOfStars, plan.For(GearSlot.MainHand)!.Wear.ItemId);
        Assert.Equal(GoldentailPie, plan.Food!.ItemId);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(30), $"took {watch.Elapsed}");
    }

    [Fact]
    public void FullLv100MinerSet_FromTheSheetsHighest_SaysExactlyHowFarShort()
    {
        // Straight from the sheets: highest thresholds, ranged tops included (5886 / 5760 / 990).
        var targets = GatheringTargets.Highest(Bonuses, Points, includeRangeTop: true, bonusTypeText: BonusTypeText);
        var plan = MateriaOptimizer.Optimize(Request(SheetGear(GatherJob.Miner), targets, foods: CurrentFoods(), allowUnownedGear: true));
        Dump(plan);

        Assert.True(plan.ProvenOptimal); // so the shortfall below is the least possible, not a search artefact
        var perception = plan.For(GatherStat.Perception);
        Assert.False(perception.Reached);
        Assert.Equal(targets.Value(GatherStat.Perception) - perception.Total, perception.ShortBy);
        Assert.Equal(158, perception.ShortBy);
        Assert.Contains($"Short by {perception.ShortBy}", perception.Why);
        Assert.Contains("Gatherer's Boon Chance", perception.Why); // the target's own reason travels with it
        Assert.Null(plan.WithPurchases); // materia is not what is missing: the sockets are
    }

    [Fact]
    public void FullLv100MinerSet_WithFewMateria_UsesOnlyTheBags_AndSaysWhatToBuy()
    {
        var few = Stock((GuerdonXii, 5), (GuileXii, 5), (GraspXii, 5), (GuerdonXi, 20), (GuileXi, 20), (GraspXi, 20));
        var request = Request(SheetGear(GatherJob.Miner), TargetSet.Of(5460, 5600, 960), stock: few, foods: CurrentFoods(), allowUnownedGear: true);

        var watch = Stopwatch.StartNew();
        var plan = MateriaOptimizer.Optimize(request);
        watch.Stop();
        output.WriteLine($"run time {watch.ElapsedMilliseconds} ms");
        Dump(plan);

        Assert.All(plan.Materia, m => Assert.True(m.Expected <= m.InStock + 1e-6, $"{m.Materia.Name}: {m.Expected} of {m.InStock}"));
        Assert.True(plan.For(GatherStat.Gathering).Reached); // the Miner's first priority holds
        Assert.False(plan.AllReached);
        Assert.NotEmpty(plan.ToAcquire);
        Assert.True(plan.WithPurchases!.AllReached);
        Assert.Contains(plan.Stats.Where(s => !s.Reached), s => s.Why.Contains("bags run short"));
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(60), $"took {watch.Elapsed}");
    }

    [Fact]
    public void SheetsHighestWithoutRanges_IsReachedWithFood()
    {
        var targets = GatheringTargets.Highest(Bonuses, Points);
        var plan = MateriaOptimizer.Optimize(Request(SheetGear(GatherJob.Miner), targets, foods: CurrentFoods(), allowUnownedGear: true));
        Dump(plan);

        Assert.Equal("5500 / 5600 / 960", targets.ToString());
        Assert.True(plan.AllReached);
        Assert.True(plan.ProvenOptimal);
    }

    [Fact]
    public void SameRequest_SamePlan()
    {
        var request = Request(SheetGear(GatherJob.Botanist), TargetSet.Of(5000, 5200, 900), foods: CurrentFoods(), allowUnownedGear: true);
        var a = MateriaOptimizer.Optimize(request);
        var b = MateriaOptimizer.Optimize(request);

        Assert.Equal(a.Totals, b.Totals);
        Assert.Equal(a.Food?.ItemId, b.Food?.ItemId);
        Assert.Equal(a.ExpectedMateria, b.ExpectedMateria);
        Assert.Equal(
            a.Slots.Select(s => (s.Slot, s.Wear.ItemId, string.Join(",", s.Sockets.Select(k => k.Materia?.ItemId ?? 0)))),
            b.Slots.Select(s => (s.Slot, s.Wear.ItemId, string.Join(",", s.Sockets.Select(k => k.Materia?.ItemId ?? 0)))));
    }
}
