using Demeter.Services.Gathering;

namespace Demeter.Tests;

public class GatheringAbilitiesTests
{
    /// <summary>The links and levels as the Action sheet has them (read off the game data 2026-10-22).</summary>
    private const uint InscrutableTastes = 67631;
    private const uint ARousingReunion = 68432;

    private static readonly GatherAbility[] Sheet =
    [
        new(240, "Collect", 16, 50, InscrutableTastes, "Inscrutable Tastes"),
        new(22184, "Meticulous Prospector", 16, 50, InscrutableTastes, "Inscrutable Tastes"),
        new(22185, "Scrutiny", 16, 50, InscrutableTastes, "Inscrutable Tastes"),
        new(22182, "Scour", 16, 50, InscrutableTastes, "Inscrutable Tastes"),
        new(4073, "Bountiful Yield", 16, 24, 0, null),
        new(297, "Mooch", 18, 25, 0, null),
        new(268, "Mooch II", 18, 63, ARousingReunion, "A Rousing Reunion"),
    ];

    private sealed class State : IAbilityState
    {
        public Dictionary<uint, int> Levels { get; } = new();
        public HashSet<uint> Done { get; } = [];
        public int LevelOf(uint classJobId) => Levels.GetValueOrDefault(classJobId);
        public bool UnlockDone(uint unlockLink) => unlockLink == 0 || Done.Contains(unlockLink);
    }

    [Fact]
    public void Level_alone_does_not_teach_Collect_the_quest_does()
    {
        var state = new State { Levels = { [16] = 90 } };
        var abilities = new GatheringAbilities(Sheet, state);

        Assert.False(abilities.Knows(240));
        Assert.Equal("Collect needs quest \"Inscrutable Tastes\"", abilities.WhyNot(240));

        state.Done.Add(InscrutableTastes);
        Assert.True(abilities.Knows(240));
        Assert.Null(abilities.WhyNot(240));
    }

    [Fact]
    public void Missing_lists_the_road_ahead_lowest_level_first_with_both_needs()
    {
        var state = new State { Levels = { [18] = 40 } };
        var abilities = new GatheringAbilities(Sheet, state);

        var missing = abilities.Missing(18);
        var mooch2 = Assert.Single(missing);
        Assert.Equal("Mooch II", mooch2.Ability.Name);
        Assert.Equal("Lv 63 (now 40) and quest \"A Rousing Reunion\"", mooch2.Missing);
    }

    [Fact]
    public void A_class_never_unlocked_says_so()
    {
        var abilities = new GatheringAbilities(Sheet, new State());
        var first = abilities.Missing(18)[0];
        Assert.Equal("Mooch", first.Ability.Name);
        Assert.StartsWith("Fisher unlocked, then Lv 25", first.Missing);
    }

    [Fact]
    public void An_action_the_catalogue_does_not_hold_is_not_refused()
    {
        var abilities = new GatheringAbilities(Sheet, new State());
        Assert.True(abilities.Knows(99999));
        Assert.Null(abilities.WhyNot(99999));
    }

    [Fact]
    public void Without_Scrutiny_the_rotation_raises_instead()
    {
        // Nothing finishes from here, GP to spare: Scrutiny if known, else the raise.
        var state = new CollectableState(Collectability: 100, Target: 600, IntegrityLeft: 4, Gp: 600,
            ScrutinyUsed: false, ScourYield: 150, MeticulousYield: 110, Minimum: 600);
        Assert.Equal(GatherMove.Scrutiny, CollectableRotation.Next(state));
        Assert.Equal(GatherMove.Meticulous, CollectableRotation.Next(state with { ScrutinyKnown = false }));
    }
}
