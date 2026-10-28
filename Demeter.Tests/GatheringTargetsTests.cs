using Demeter.Services.Melding;
using static Demeter.Tests.MeldingFixtures;

namespace Demeter.Tests;

/// <summary>Targets computed from GatheringPoint → GatheringPointBonus (fixture sheets, datamining 44948e4).</summary>
public class GatheringTargetsTests
{
    [Fact]
    public void Highest_TakesTheTopThresholdOfEveryRow_LinkedToANodeOrNot()
    {
        var targets = GatheringTargets.Highest(Bonuses, Points, bonusTypeText: BonusTypeText);

        // 5500 Gathering: rows 648/649 (Collectability +70), on no node — newer than the 5460 rows.
        Assert.Equal(5500, targets.Value(GatherStat.Gathering));
        Assert.Equal(648u, targets.Gathering!.BonusRowId);
        Assert.False(targets.Gathering.NodeLinked);
        // 5600 Perception: the Lv100 legendary nodes' Boon +30% (row 403) — linked, so it wins the tie-free top.
        Assert.Equal(5600, targets.Value(GatherStat.Perception));
        Assert.Equal(403u, targets.Perception!.BonusRowId);
        Assert.True(targets.Perception.NodeLinked);
        // 960 Max GP: row 405, integrity +1 (ranged up to 990), on no node.
        Assert.Equal(960, targets.Value(GatherStat.Gp));
        Assert.Equal(405u, targets.Gp!.BonusRowId);
        Assert.Contains("Gatherer's Boon Chance +30%", targets.Perception.Reason);
    }

    [Fact]
    public void Highest_WithRangeTop_AimsWhereTheRangedBonusesStopGrowing()
    {
        var targets = GatheringTargets.Highest(Bonuses, Points, includeRangeTop: true);

        Assert.Equal(5886, targets.Value(GatherStat.Gathering)); // row 404: Yield +1 at 5400, +3 at 5886
        Assert.Equal(5760, targets.Value(GatherStat.Perception)); // rows 659-664: Boon from 5400, top at 5760
        Assert.Equal(990, targets.Value(GatherStat.Gp)); // row 405: integrity +1 at 960, +2 at 990
        Assert.True(targets.Gp!.RangeTop);
        Assert.Equal(2, targets.Gp.BonusValue);
    }

    [Fact]
    public void ForNodes_Lv100Regular_IsTheNodeLinkedYieldAndIntegrityTiers()
    {
        // Two Lv100 regular nodes (point type 1) in territory 1192, and the legendary nodes there.
        var targets = GatheringTargets.ForNodes(Points, Bonuses, [34929, 34931, 34933]);

        Assert.Equal(4431, targets.Value(GatherStat.Gathering)); // row 484: Yield +1
        Assert.Equal(840, targets.Value(GatherStat.Gp)); // row 493: integrity +1
        Assert.Equal(3475, targets.Value(GatherStat.Perception)); // row 507: Boon +20%～
        Assert.True(targets.Gathering!.NodeLinked);
    }

    [Fact]
    public void ForNodes_Legendary_ReadsTheBoonTiers()
    {
        Assert.Equal(4907, GatheringTargets.ForNodes(Points, Bonuses, [34992]).Value(GatherStat.Perception));
        Assert.Equal(5254, GatheringTargets.ForNodes(Points, Bonuses, [35237]).Value(GatherStat.Perception));
        Assert.Equal(5600, GatheringTargets.ForNodes(Points, Bonuses, [35461]).Value(GatherStat.Perception));
        // A legendary node carries a Perception bonus only.
        Assert.Null(GatheringTargets.ForNodes(Points, Bonuses, [35461]).Gathering);
    }

    [Fact]
    public void ForNodes_AllNodes_IncludesTheDiademGpTier()
    {
        // Over every node, Max GP tops out at the Diadem's 858 (point type 7), above the overworld's 840;
        // Gathering at 4431 and Perception at 5600 — the unlinked 5500 / 960 rows are out of scope.
        var targets = GatheringTargets.ForNodes(Points, Bonuses);

        Assert.Equal(4431, targets.Value(GatherStat.Gathering));
        Assert.Equal(5600, targets.Value(GatherStat.Perception));
        Assert.Equal(858, targets.Value(GatherStat.Gp));
        Assert.Equal(178u, targets.Gp!.BonusRowId);
    }

    [Fact]
    public void Overrides_RaiseOnlyWhereLower_AndRespectTheirJob()
    {
        var nodes = GatheringTargets.ForNodes(Points, Bonuses, [34929, 34931, 34933]);

        var miner = GatheringTargets.Apply(nodes, GatheringTargets.CommunityTiers, GatherJob.Miner);
        Assert.Equal(990, miner.Value(GatherStat.Gp));
        Assert.Equal(TargetSource.Override, miner.Gp!.Source);
        Assert.Equal(5090, miner.Value(GatherStat.Perception));
        Assert.Equal(4431, miner.Value(GatherStat.Gathering)); // the big-fish tier is the Fisher's

        var fisher = GatheringTargets.Apply(nodes, GatheringTargets.CommunityTiers, GatherJob.Fisher);
        Assert.Equal(4570, fisher.Value(GatherStat.Gathering));

        var highest = GatheringTargets.Highest(Bonuses);
        var raised = GatheringTargets.Apply(highest, GatheringTargets.CommunityTiers, GatherJob.Miner);
        Assert.Equal(5600, raised.Value(GatherStat.Perception)); // 5090 is below the sheet's 5600: untouched
        Assert.Equal(TargetSource.Sheet, raised.Perception!.Source);

        var exact = GatheringTargets.Apply(highest, [new TargetOverride(GatherStat.Gp, 900, "cap at 900", TargetOverrideMode.Exactly)], GatherJob.Miner);
        Assert.Equal(900, exact.Value(GatherStat.Gp));
        var disabled = GatheringTargets.Apply(highest, [new TargetOverride(GatherStat.Gp, 999, "off", Enabled: false)], GatherJob.Miner);
        Assert.Equal(960, disabled.Value(GatherStat.Gp));
    }

    [Fact]
    public void ConditionCodes_MatchTheConditionSheet()
    {
        var conditions = File.ReadAllLines(Path.Combine(AppContext.BaseDirectory, "Fixtures", "Melding", "GatheringCondition.csv"));
        Assert.Contains("14,Gathering ≥ UNKNOWN", conditions);
        Assert.Contains("15,Perception ≥ UNKNOWN", conditions);
        Assert.Contains("19,Max GP ≥ UNKNOWN", conditions);
        Assert.Equal(GatherStat.Gp, GatheringTargets.StatFor(GatheringTargets.ConditionMaxGp));
        Assert.Null(GatheringTargets.StatFor(16)); // "Gathering <" is not a target
    }
}
