using Demeter.Services.Gathering;

namespace Demeter.Tests;

public class GearUpTests
{
    /// <summary>The game: Recommended Gear answers in a tick, the tool changes the class, the gear goes on over a few.</summary>
    private sealed class Game : IGearUpGame
    {
        public List<string> Calls { get; } = [];
        public uint CurrentClassJob { get; set; } = 1;   // a Gladiator
        public uint Tool { get; set; } = 2500;             // a Botanist's hatchet in the armoury
        public bool ToolRefused { get; set; }
        public bool SlotFree { get; set; } = true;
        public int Saved { get; set; } = -1;
        private int _busy;
        private uint _pickedFor;

        public bool RecommendBusy => _busy > 0 && _busy-- > 0;
        public bool RecommendFor(uint classJobId) { Calls.Add($"recommend {classJobId}"); _pickedFor = classJobId; _busy = 1; return true; }
        public uint RecommendedMainHand => Tool;
        public bool PutOnRecommendedMainHand()
        {
            Calls.Add("tool");
            if (!ToolRefused) CurrentClassJob = _pickedFor;
            return true;
        }
        public void PutOnRecommended() { Calls.Add("gear"); _busy = 3; }
        public bool GearsetSlotFree => SlotFree;
        public int SaveGearset() { Calls.Add("save"); return Saved = 11; }
    }

    private sealed class Clock { public DateTime Now = new(2026, 11, 22, 12, 0, 0, DateTimeKind.Utc); }

    private static (GearUp GearUp, Game Game, List<string> Log, Clock Clock) Make()
    {
        var game = new Game();
        var clock = new Clock();
        var log = new List<string>();
        return (new GearUp(game, () => clock.Now, log.Add), game, log, clock);
    }

    private static void Run(GearUp gearUp, Clock clock, int ticks = 40)
    {
        for (var i = 0; i < ticks && gearUp.Tick(); i++)
            clock.Now = clock.Now.AddSeconds(0.5);
    }

    [Fact]
    public void On_another_class_the_tool_goes_on_first_then_the_gear_then_it_is_saved()
    {
        var (gearUp, game, log, clock) = Make();
        Assert.True(gearUp.Begin(17, "Botanist"));
        Run(gearUp, clock);

        Assert.False(gearUp.Running);
        Assert.Empty(gearUp.Failure);
        Assert.Equal(["recommend 17", "tool", "recommend 17", "gear", "save"], game.Calls);
        Assert.Equal(17u, game.CurrentClassJob);
        Assert.Contains(log, l => l.Contains("Saved gearset 12 for Botanist"));
    }

    [Fact]
    public void Already_on_the_class_the_gear_goes_straight_on()
    {
        var (gearUp, game, _, clock) = Make();
        game.CurrentClassJob = 17;
        Assert.True(gearUp.Begin(17, "Botanist"));
        Run(gearUp, clock);

        Assert.Equal(["recommend 17", "gear", "save"], game.Calls);
    }

    [Fact]
    public void No_tool_owned_is_said_and_nothing_is_saved()
    {
        var (gearUp, game, _, clock) = Make();
        game.Tool = 0;
        Assert.True(gearUp.Begin(17, "Botanist"));
        Run(gearUp, clock);

        Assert.False(gearUp.Running);
        Assert.Equal("no gearset for Botanist, and none could be saved: there is no Botanist tool in the Armoury Chest or the bags", gearUp.Failure);
        Assert.DoesNotContain("save", game.Calls);
    }

    [Fact]
    public void Every_slot_taken_is_said_before_anything_is_touched()
    {
        var (gearUp, game, _, _) = Make();
        game.SlotFree = false;
        Assert.False(gearUp.Begin(18, "Fisher"));
        Assert.Contains("every gearset slot is in use", gearUp.Failure);
        Assert.Empty(game.Calls);
    }

    [Fact]
    public void A_tool_the_game_will_not_put_on_gives_up_after_its_wait()
    {
        var (gearUp, game, _, clock) = Make();
        game.ToolRefused = true;                 // in combat, say
        Assert.True(gearUp.Begin(16, "Miner"));
        Run(gearUp, clock);

        Assert.False(gearUp.Running);
        Assert.Contains("did not put the Miner tool on", gearUp.Failure);
        Assert.DoesNotContain("save", game.Calls);
    }

    [Fact]
    public void A_past_failure_is_forgotten_when_a_switch_is_asked_for_again()
    {
        var (gearUp, game, _, clock) = Make();
        game.Tool = 0;
        gearUp.Begin(17, "Botanist");
        Run(gearUp, clock);
        Assert.NotEmpty(gearUp.Failure);

        gearUp.Forget();
        Assert.Empty(gearUp.Failure);
    }
}
