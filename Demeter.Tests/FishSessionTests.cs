using System.Text.Json;
using Demeter.Services.Fishing;
using Pond = Demeter.Tests.FishingRunTests.Pond;

namespace Demeter.Tests;

public class FishSessionTests
{
    private const uint Bait = 52247, Small = 52218, Big = 52219;

    private static (FishSession Session, Pond Pond) Make(bool fisher = true, IEnumerable<FishInfo>? fish = null)
    {
        var pond = new Pond { CurrentBait = Bait };
        pond.Bag[Bait] = 999;
        var session = new FishSession(pond, () => fisher, new FishCatalog(fish ?? []), id => $"item {id}");
        return (session, pond);
    }

    /// <summary>A quick, fixed pace so tests read in seconds; the seed makes it the same every run.</summary>
    private static FishSessionRequest Ask(int castBudget = 0, double timeBudget = 0, params uint[] wanted) => new()
    {
        RequestedBy = "Selene",
        Wanted = [.. wanted],
        CastBudget = castBudget,
        TimeBudgetSeconds = timeBudget,
        Pace = new FishPaceRequest { Seed = 7, LongPauseEvery = [1000, 1000] },
    };

    private static void Script(Pond pond, params (uint Fish, double Seconds)[] bites)
    {
        pond.Script[Bait] = new(bites);
        foreach (var (fish, _) in bites)
            pond.Tugs.TryAdd(fish, Tug.Light);
    }

    /// <summary>Ticks every 50 ms until the session is no longer busy, or the step cap is reached.</summary>
    private static FishSessionState Run(FishSession session, Pond pond, int steps = 20_000, Func<bool>? until = null)
    {
        for (var i = 0; i < steps && session.Busy && until?.Invoke() != true; i++)
        {
            session.Tick();
            pond.Advance(0.05);
        }
        return session.State;
    }

    [Fact]
    public void Every_bite_is_hooked_and_a_stop_lands_the_cast_in_hand_first()
    {
        var (session, pond) = Make();
        Script(pond, (Small, 8), (Big, 12), (Small, 9), (Small, 30));
        Assert.True(session.Begin(Ask()));

        Run(session, pond, until: () => pond.Hooked.Count == 2);
        session.Tick();
        Run(session, pond, until: () => pond.State == FishState.LineInWater);
        session.Stop();   // the third cast is in: it is still fished
        Assert.Equal(FishSessionState.StoppedAsAsked, Run(session, pond));

        Assert.Equal([Small, Big, Small], pond.Hooked);
        Assert.Equal(3, session.Casts);
        Assert.Equal(FishState.None, pond.State);
        Assert.Equal(2, session.Landed[Small]);
        Assert.Empty(pond.LetGo);
    }

    [Fact]
    public void The_bait_is_never_changed()
    {
        var (session, pond) = Make();
        Script(pond, (Small, 5), (Small, 5));
        session.Begin(Ask(castBudget: 2));
        Run(session, pond);
        Assert.Equal(Bait, pond.CurrentBait);
        Assert.DoesNotContain(FishingRun.Collect, pond.Actions);
        Assert.DoesNotContain(FishingRun.Mooch, pond.Actions);
    }

    [Fact]
    public void The_cast_budget_ends_it_with_its_own_code()
    {
        var (session, pond) = Make();
        Script(pond, (Small, 5), (Small, 5), (Small, 5), (Small, 5));
        session.Begin(Ask(castBudget: 3));
        Assert.Equal(FishSessionState.Done, Run(session, pond));
        Assert.Equal(FishSessionCode.CastBudget, session.Code);
        Assert.Equal(3, session.Casts);
        Assert.Equal(3, pond.Actions.Count(a => a == FishingRun.Cast));
    }

    [Fact]
    public void Out_of_time_a_line_still_waiting_is_reeled_in()
    {
        var (session, pond) = Make();
        Script(pond, (Big, 100));
        session.Begin(Ask(timeBudget: 20));
        Assert.Equal(FishSessionState.Done, Run(session, pond));
        Assert.Equal(FishSessionCode.TimeBudget, session.Code);
        Assert.Contains(FishingRun.Quit, pond.Actions);
        Assert.Empty(pond.Hooked);
        Assert.True(pond.UtcNow - new DateTime(2026, 10, 22, 12, 0, 0, DateTimeKind.Utc) < TimeSpan.FromSeconds(25));
    }

    [Fact]
    public void A_first_cast_that_does_not_go_in_is_no_water_here()
    {
        var (session, pond) = Make();
        pond.NoWater = true;
        session.Begin(Ask());
        Assert.Equal(FishSessionState.Faulted, Run(session, pond));
        Assert.Equal(FishSessionCode.CannotFishHere, session.Code);
        Assert.Equal(1, pond.Actions.Count(a => a == FishingRun.Cast));   // tried once, not pressed over and over
    }

    [Fact]
    public void Something_amiss_ends_it_after_the_cast_in_hand()
    {
        var (session, pond) = Make();
        Script(pond, (Small, 6), (Small, 6), (Small, 6));
        session.Begin(Ask());
        Run(session, pond, until: () => pond.State == FishState.LineInWater);
        session.Amiss();
        Assert.Equal(FishSessionState.Done, Run(session, pond));
        Assert.Equal(FishSessionCode.Amiss, session.Code);
        Assert.Single(pond.Hooked);
    }

    [Fact]
    public void Running_out_of_bait_ends_it()
    {
        var (session, pond) = Make();
        Script(pond, (Small, 5), (Small, 5));
        pond.OnLanded = _ => pond.Bag[Bait] = 0;
        session.Begin(Ask());
        Assert.Equal(FishSessionState.Done, Run(session, pond));
        Assert.Equal(FishSessionCode.NoBait, session.Code);
        Assert.Equal(1, session.Casts);
    }

    [Fact]
    public void It_refuses_without_Fisher_without_bait_or_with_another_bait_on()
    {
        var (notFisher, _) = Make(fisher: false);
        Assert.False(notFisher.Begin(Ask()));
        Assert.Equal(FishSessionCode.NotFisher, notFisher.Code);

        var (noBait, pond) = Make();
        pond.CurrentBait = 0;
        Assert.False(noBait.Begin(Ask()));
        Assert.Equal(FishSessionCode.NoBait, noBait.Code);

        var (wrong, _) = Make();
        var ask = Ask();
        ask.Bait = 2585;
        Assert.False(wrong.Begin(ask));
        Assert.Equal(FishSessionCode.NoBait, wrong.Code);

        var (bad, _) = Make();
        ask = Ask();
        ask.Pace!.HookDelayMs = [900, 4000];
        Assert.False(bad.Begin(ask));
        Assert.Equal(FishSessionCode.BadRequest, bad.Code);
    }

    [Fact]
    public void Named_fish_let_go_a_bite_with_a_tug_none_of_them_has()
    {
        var big = new FishInfo { Id = Big, Name = "big", Bite = "Legendary", Path = [Bait] };
        var (session, pond) = Make(fish: [big]);
        Script(pond, (Small, 5), (Big, 7));
        pond.Tugs[Big] = Tug.Heavy;
        session.Begin(Ask(castBudget: 2, wanted: Big));
        Run(session, pond);
        Assert.Equal([Small], pond.LetGo);
        Assert.Equal([Big], pond.Hooked);
    }

    [Fact]
    public void Casts_and_hooks_come_at_drawn_intervals_and_a_long_pause_comes_every_so_often()
    {
        var (session, pond) = Make();
        Script(pond, [.. Enumerable.Range(0, 7).Select(_ => (Small, 4.0))]);
        var ask = Ask(castBudget: 7);
        ask.Pace = new FishPaceRequest
        {
            Seed = 11, RecastMs = [600, 2200], HookDelayMs = [250, 900], LongPauseEvery = [3, 3], LongPauseMs = [6000, 6000],
        };
        session.Begin(ask);
        Run(session, pond);

        // From a landing to the next cast: drawn, never the same twice in a row at this resolution;
        // the third and sixth carry the long pause.
        var hooks = pond.Pressed.Where(p => p.Action == FishingRun.Hook).Select(p => p.At).ToList();
        var casts = pond.Pressed.Where(p => p.Action == FishingRun.Cast).Select(p => p.At).ToList();
        var waits = hooks.Zip(casts.Skip(1), (h, c) => (c - h).TotalSeconds).ToList();
        Assert.Equal(6, waits.Count);
        Assert.All(waits, w => Assert.InRange(w, 0.6, 9.5));
        Assert.True(waits[2] >= 6 && waits[5] >= 6);
        Assert.True(waits.Where((_, i) => i is not (2 or 5)).All(w => w < 3));
        Assert.True(waits.Distinct().Count() > 3);

        // Bite to hook: within the range asked, at 50 ms ticks.
        var bites = casts.Select(c => c.AddSeconds(4)).ToList();
        Assert.All(hooks.Zip(bites, (h, b) => (h - b).TotalMilliseconds), ms => Assert.InRange(ms, 200, 1000));
    }

    // ── the pace ──

    [Fact]
    public void The_same_seed_draws_the_same_pace_and_every_draw_is_in_range()
    {
        var a = new FishPace(new FishPaceRequest { Seed = 3 });
        var b = new FishPace(new FishPaceRequest { Seed = 3 });
        for (var i = 0; i < 200; i++)
        {
            var hook = a.HookDelay();
            Assert.Equal(hook, b.HookDelay());
            Assert.InRange(hook.TotalMilliseconds, FishPace.DefaultHookDelay.Min, FishPace.DefaultHookDelay.Max);
            var gap = a.ActionGap();
            Assert.Equal(gap, b.ActionGap());
            Assert.InRange(gap.TotalMilliseconds, FishPace.DefaultActionGap.Min, FishPace.DefaultActionGap.Max);
        }
    }

    [Theory]
    [InlineData(new[] { 900, 300 })]       // min above max
    [InlineData(new[] { 100, 2500 })]      // a hook that late loses the fish
    [InlineData(new[] { 500 })]            // not a range
    public void A_bad_hook_range_is_refused(int[] range)
        => Assert.NotNull(FishPace.Problem(new FishPaceRequest { HookDelayMs = range }));

    [Fact]
    public void An_action_gap_under_the_game_lock_is_refused()
        => Assert.NotNull(FishPace.Problem(new FishPaceRequest { ActionGapMs = [100, 400] }));

    // ── the gates ──

    [Fact]
    public void The_gate_answers_in_JSON_and_refuses_while_anything_else_drives()
    {
        var (session, pond) = Make();
        var othersBusy = false;
        var log = new List<string>();
        var gate = new FishGateway(session, () => true, () => othersBusy, log.Add);

        using (var ok = JsonDocument.Parse(gate.CanFishHere("{\"requestedBy\":\"Selene\"}")))
        {
            Assert.True(ok.RootElement.GetProperty("ok").GetBoolean());
            Assert.Equal("none", ok.RootElement.GetProperty("code").GetString());
        }

        othersBusy = true;
        Assert.False(gate.Start("{}"));
        Assert.Contains("busy with something else", gate.LastRefusal);
        othersBusy = false;

        using (var bad = JsonDocument.Parse(gate.CanFishHere("{not json")))
            Assert.Equal("badRequest", bad.RootElement.GetProperty("code").GetString());

        Script(pond, (Small, 5));
        Assert.True(gate.Start("{\"requestedBy\":\"Selene\",\"missionId\":1675,\"castBudget\":1,\"pace\":{\"seed\":1}}"));
        Assert.False(gate.Start("{}"));   // one at a time
        Run(session, pond);

        using var status = JsonDocument.Parse(gate.StatusJson());
        var root = status.RootElement;
        Assert.Equal("done", root.GetProperty("state").GetString());
        Assert.Equal("castBudget", root.GetProperty("code").GetString());
        Assert.Equal("Selene", root.GetProperty("requestedBy").GetString());
        Assert.Equal(1, root.GetProperty("casts").GetInt32());
        Assert.Equal(1, root.GetProperty("landed").GetProperty(Small.ToString()).GetInt32());
        Assert.True(root.GetProperty("lastBite").GetProperty("hooked").GetBoolean());
        Assert.Equal("light", root.GetProperty("lastBite").GetProperty("tug").GetString());
        Assert.False(root.GetProperty("amiss").GetBoolean());
    }

    [Fact]
    public void Switched_off_the_gate_says_so()
    {
        var (session, _) = Make();
        var gate = new FishGateway(session, () => false, () => false, _ => { });
        using var doc = JsonDocument.Parse(gate.CanFishHere("{}"));
        Assert.Equal("off", doc.RootElement.GetProperty("code").GetString());
    }
}
