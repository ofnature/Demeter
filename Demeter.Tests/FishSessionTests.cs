using System.Text.Json;
using Demeter.Services.Fishing;
using Pond = Demeter.Tests.FishingRunTests.Pond;

namespace Demeter.Tests;

public class FishSessionTests
{
    private const uint Bait = 52247, Small = 52218, Big = 52219;

    private const uint Mission = 1675;

    private static (FishSession Session, Pond Pond) Make(bool fisher = true, IEnumerable<FishInfo>? fish = null,
        FishScores? scores = null, Func<uint, bool>? knows = null, Func<uint>? missionScore = null, uint mission = Mission)
    {
        var pond = new Pond { CurrentBait = Bait };
        pond.Bag[Bait] = 999;
        var session = new FishSession(pond, () => fisher, new FishCatalog(fish ?? []), id => $"item {id}",
            knows ?? (_ => true), scores ?? new FishScores(), () => mission, missionScore ?? (() => 0));
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
        var (session, pond) = Make(mission: 0);
        pond.NoWater = true;
        session.Begin(Ask());
        Assert.Equal(FishSessionState.Faulted, Run(session, pond));
        Assert.Equal(FishSessionCode.CannotFishHere, session.Code);
        Assert.Equal(1, pond.Actions.Count(a => a == FishingRun.Cast));   // tried once, not pressed over and over
    }

    [Fact]
    public void On_a_mission_a_refused_first_cast_tries_Collect_once_before_giving_up()
    {
        var (session, pond) = Make();
        pond.NoWater = true;
        session.Begin(Ask());
        Assert.Equal(FishSessionState.Faulted, Run(session, pond));
        Assert.Equal(FishSessionCode.CannotFishHere, session.Code);
        Assert.Equal(2, pond.Actions.Count(a => a == FishingRun.Cast));
        Assert.Single(pond.Actions, a => a == FishingRun.Collect);
    }

    [Fact]
    public void A_mission_that_needs_Collect_gets_it_and_is_remembered()
    {
        var scores = new FishScores();
        var (session, pond) = Make(scores: scores);
        pond.CastNeedsCollect = true;
        Script(pond, (Small, 5), (Small, 5));
        session.Begin(Ask(castBudget: 1));
        Assert.Equal(FishSessionState.Done, Run(session, pond));
        Assert.Equal(FishSessionCode.CastBudget, session.Code);
        Assert.True(scores.NeedsCollect(Mission));
        Assert.Single(pond.Answers);   // the collectable prompt was answered, and kept

        // Next time Collect goes on before the first cast, and nothing is refused.
        var (again, pond2) = Make(scores: scores);
        pond2.CastNeedsCollect = true;
        Script(pond2, (Small, 5));
        again.Begin(Ask(castBudget: 1));
        Run(again, pond2);
        Assert.Equal([FishingRun.Collect, FishingRun.Cast], pond2.Actions.Take(2));
        Assert.Single(pond2.Actions, a => a == FishingRun.Cast);
    }

    [Fact]
    public void Without_Collect_learned_it_says_the_mission_may_need_it()
    {
        var (session, pond) = Make(knows: id => id != FishingRun.Collect);
        pond.CastNeedsCollect = true;
        session.Begin(Ask());
        Assert.Equal(FishSessionState.Faulted, Run(session, pond));
        Assert.Contains("Inscrutable Tastes", session.Reason);
    }

    [Fact]
    public void With_a_prize_known_Prize_Catch_goes_before_every_cast_keeping_GP_for_Identical_Cast()
    {
        var (session, pond) = Make(scores: Known(), knows: id => id is not (FishSession.IdenticalCast or FishSession.BigGameFishing));
        Script(pond, (Small, 5), (Small, 5), (Small, 5));
        pond.Gp = pond.MaxGp = 800;   // 800, 600, 400: the third would leave less than 350
        session.Begin(Ask(castBudget: 3));
        Run(session, pond);
        var presses = pond.Actions.Where(a => a is FishSession.PrizeCatch or FishingRun.Cast).ToList();
        Assert.Equal([FishSession.PrizeCatch, FishingRun.Cast, FishSession.PrizeCatch, FishingRun.Cast, FishingRun.Cast], presses);
        Assert.Equal(400, pond.Gp);
    }

    [Fact]
    public void Without_a_mission_or_a_known_prize_there_is_no_Prize_Catch()
    {
        var (open, pond) = Make(scores: Known(), mission: 0);
        Script(pond, (Small, 5));
        open.Begin(Ask(castBudget: 1));
        Run(open, pond);
        Assert.DoesNotContain(FishSession.PrizeCatch, pond.Actions);

        var (fresh, pond2) = Make();   // a mission, nothing known of it yet
        Script(pond2, (Small, 5));
        fresh.Begin(Ask(castBudget: 1));
        Run(fresh, pond2);
        Assert.DoesNotContain(FishSession.PrizeCatch, pond2.Actions);
    }

    [Fact]
    public void Big_game_Fishing_goes_on_a_Prize_Catch_with_two_stacks()
    {
        var (session, pond) = Make(scores: Known());
        Script(pond, (Small, 5));
        pond.Stacks[FishSession.AnglersArt] = 2;
        session.Begin(Ask(castBudget: 1));
        Run(session, pond);
        Assert.Equal([FishSession.PrizeCatch, FishSession.BigGameFishing, FishingRun.Cast], pond.Actions.Take(3));
    }

    [Fact]
    public void An_action_the_game_refuses_is_pressed_once_a_cast_and_holds_nothing_up()
    {
        var (session, pond) = Make(scores: Known());
        Script(pond, (Small, 5), (Small, 5));
        pond.Refused.Add(FishSession.PrizeCatch);
        session.Begin(Ask(castBudget: 2));
        Assert.Equal(FishSessionState.Done, Run(session, pond));
        Assert.Equal(2, pond.Actions.Count(a => a == FishSession.PrizeCatch));
        Assert.Equal(2, session.Casts);
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

    // ── points, GP and the special actions ──

    /// <summary>Small scores 100, Big 700 on the mission: Big is the prize.</summary>
    private static FishScores Known()
    {
        var scores = new FishScores();
        scores.Record(Mission, Small, 100);
        scores.Record(Mission, Big, 700);
        return scores;
    }

    [Fact]
    public void A_score_after_a_catch_is_recorded_for_that_fish_on_the_mission()
    {
        var scores = new FishScores();
        var (session, pond) = Make(scores: scores);
        Script(pond, (Big, 5), (Small, 5));
        pond.OnLanded = fish => session.Scored(fish == Big ? 700 : 100);
        session.Begin(Ask(castBudget: 2));
        Run(session, pond);

        Assert.Equal(700, scores.Of(Mission, Big)!.Max);
        Assert.Equal(100, scores.Of(Mission, Small)!.Max);
        Assert.Equal(800, session.Points);
        Assert.True(scores.Dirty);
    }

    [Fact]
    public void A_running_total_counts_its_rise_and_records_no_fish()
    {
        var scores = new FishScores();
        var (session, pond) = Make(scores: scores);
        Script(pond, (Big, 5), (Small, 5));
        var totals = new Queue<int>([5690, 6800]);
        pond.OnLanded = _ => session.ScoreChanged(totals.Dequeue());
        session.Begin(Ask(castBudget: 2));
        Run(session, pond);

        Assert.Equal(1110, session.Points);   // the first total is where it stood, not earned here
        Assert.Empty(scores.Missions);
    }

    // ── missions that score the largest fish ──

    /// <summary>Scored by length at 100 points an ilm; Small never past 60 ilms, Big the biggest at 116.</summary>
    private static FishScores Lengths()
    {
        var scores = new FishScores();
        scores.RecordPointsPerIlm(Mission, 100);
        scores.RecordSize(Mission, Small, 60);
        scores.RecordSize(Mission, Big, 116);
        return scores;
    }

    private static readonly FishInfo[] MissionFish =
    [
        new() { Id = Small, Name = "small", Bite = "Weak", Path = [Bait], CosmicMission = (int)Mission },
        new() { Id = Big, Name = "big", Bite = "Legendary", Path = [Bait], CosmicMission = (int)Mission },
    ];

    [Fact]
    public void Lengths_and_what_an_ilm_is_worth_are_learned_from_the_catches_and_the_total()
    {
        var scores = new FishScores();
        var (session, pond) = Make(scores: scores);
        Script(pond, (Small, 5), (Big, 5));
        pond.Ilms[Small] = 56.9;
        pond.Ilms[Big] = 116.0;
        pond.OnLanded = fish => session.ScoreChanged(fish == Big ? 11600 : 5690);
        session.Begin(Ask(castBudget: 2));
        Run(session, pond);

        Assert.Equal(116.0, scores.SizeOf(Mission, Big)!.Max);
        Assert.Equal(56.9, scores.SizeOf(Mission, Small)!.Max);
        Assert.Equal(100, scores.PointsPerIlmOf(Mission));
        Assert.True(scores.IsLargestScored(Mission));
        Assert.True(scores.IsBiggest(Mission, Big));
    }

    [Fact]
    public void A_bite_that_cannot_beat_the_score_is_let_go_and_the_biggest_is_always_hooked()
    {
        var (session, pond) = Make(fish: MissionFish, scores: Lengths(), missionScore: () => 11600);
        Script(pond, (Small, 5), (Big, 5));
        pond.Tugs[Big] = Tug.Heavy;
        session.Begin(Ask(castBudget: 2));
        Run(session, pond);
        Assert.Equal([Small], pond.LetGo);
        Assert.Equal([Big], pond.Hooked);
        Assert.Contains(pond.Logged, m => m.Contains("never landed longer than 116.0 ilms"));
    }

    [Fact]
    public void With_nothing_to_beat_yet_every_bite_is_hooked()
    {
        var (session, pond) = Make(fish: MissionFish, scores: Lengths());   // the mission has no score yet
        Script(pond, (Small, 5));
        session.Begin(Ask(castBudget: 1));
        Run(session, pond);
        Assert.Equal([Small], pond.Hooked);
    }

    [Fact]
    public void After_the_biggest_Identical_Cast_and_Prize_Catch_and_no_multi_hook()
    {
        var (session, pond) = Make(fish: MissionFish, scores: Lengths());
        Script(pond, (Big, 5), (Big, 5));
        pond.Tugs[Big] = Tug.Heavy;
        pond.Gp = pond.MaxGp = 1000;
        session.Begin(Ask(castBudget: 2));
        Run(session, pond);

        var presses = pond.Actions.Where(a => a is not FishingRun.Quit).ToList();
        Assert.Equal([FishSession.PrizeCatch, FishingRun.Cast, FishingRun.Hook, FishSession.IdenticalCast, FishSession.PrizeCatch,
            FishingRun.Cast, FishingRun.Hook], presses);
        Assert.Equal(1000 - 200 - 350 - 200, pond.Gp);
    }

    [Fact]
    public void Short_of_GP_for_Prize_Catch_Identical_Cast_goes_alone()
    {
        var (session, pond) = Make(fish: MissionFish, scores: Lengths());
        Script(pond, (Big, 5), (Big, 5));
        pond.Gp = 400;
        session.Begin(Ask(castBudget: 2));
        Run(session, pond);
        Assert.Contains(FishSession.IdenticalCast, pond.Actions);
        Assert.DoesNotContain(FishSession.PrizeCatch, pond.Actions);
    }

    [Fact]
    public void After_a_prize_Identical_Cast_then_Triple_Hook_when_GP_covers_both()
    {
        var (session, pond) = Make(scores: Known(), knows: id => id != FishSession.PrizeCatch);
        Script(pond, (Big, 5), (Big, 5), (Small, 5));
        pond.Gp = pond.MaxGp = 1050;
        session.Begin(Ask(castBudget: 3));
        Run(session, pond);

        var presses = pond.Actions.Where(a => a is not FishingRun.Quit).ToList();
        Assert.Equal([FishingRun.Cast, FishingRun.Hook, FishSession.IdenticalCast, FishingRun.Cast, FishSession.TripleHook,
            FishingRun.Cast, FishingRun.Hook], presses);   // not Identical Cast twice running
        Assert.Equal(4, pond.Bag[Big]);
        Assert.Equal(1050 - 350 - 700, pond.Gp);
    }

    [Fact]
    public void Short_of_GP_it_double_hooks_or_hooks_plainly_and_fishes_on()
    {
        var (session, pond) = Make(scores: Known(), knows: id => id != FishSession.PrizeCatch);
        Script(pond, (Big, 5), (Big, 5), (Big, 5), (Big, 5));
        pond.Gp = 760;   // Identical Cast, then 410: Double Hook, not Triple
        session.Begin(Ask(castBudget: 4));
        Run(session, pond);

        Assert.Contains(FishSession.DoubleHook, pond.Actions);
        Assert.DoesNotContain(FishSession.TripleHook, pond.Actions);
        Assert.Single(pond.Actions, a => a == FishSession.IdenticalCast);   // 10 GP left: none again
        Assert.Equal(4, pond.Actions.Count(a => a == FishingRun.Cast));
    }

    [Fact]
    public void A_fish_that_is_not_the_prize_gets_no_Identical_Cast()
    {
        var (session, pond) = Make(scores: Known());
        Script(pond, (Small, 5), (Small, 5));
        session.Begin(Ask(castBudget: 2));
        Run(session, pond);
        Assert.DoesNotContain(FishSession.IdenticalCast, pond.Actions);
    }

    [Fact]
    public void Unlearned_actions_are_never_pressed()
    {
        var (session, pond) = Make(scores: Known(), knows: id => id is FishingRun.Cast or FishingRun.Hook);
        Script(pond, (Big, 5), (Big, 5));
        pond.Stacks[FishSession.AnglersArt] = 5;
        pond.Gp = 100;
        session.Begin(Ask(castBudget: 2));
        Run(session, pond);
        Assert.All(pond.Actions, a => Assert.True(a is FishingRun.Cast or FishingRun.Hook or FishingRun.Quit, $"pressed {a}"));
    }

    [Fact]
    public void Thaliaks_Favor_at_three_stacks_when_its_GP_fits()
    {
        var (session, pond) = Make();
        Script(pond, (Small, 5), (Small, 5));
        pond.Gp = 500;
        pond.MaxGp = 900;
        pond.Stacks[FishSession.AnglersArt] = 3;
        session.Begin(Ask(castBudget: 2));
        Run(session, pond);
        Assert.Equal(FishSession.ThaliaksFavor, pond.Actions[0]);
        Assert.Equal(650, pond.Gp);

        var (full, fullPond) = Make();
        Script(fullPond, (Small, 5));
        fullPond.Gp = 800;
        fullPond.MaxGp = 900;   // 150 would spill over
        fullPond.Stacks[FishSession.AnglersArt] = 3;
        full.Begin(Ask(castBudget: 1));
        Run(full, fullPond);
        Assert.DoesNotContain(FishSession.ThaliaksFavor, fullPond.Actions);
    }

    [Fact]
    public void Mission_complete_ends_it_with_its_own_code_and_no_more_casts()
    {
        var (session, pond) = Make();
        Script(pond, (Small, 5), (Small, 5));
        pond.OnLanded = _ => session.MissionComplete();
        session.Begin(Ask());
        Assert.Equal(FishSessionState.Done, Run(session, pond));
        Assert.Equal(FishSessionCode.MissionComplete, session.Code);
        Assert.Single(pond.Actions, a => a == FishingRun.Cast);
    }

    [Fact]
    public void The_prize_needs_two_known_fish_and_half_the_best_average()
    {
        var scores = new FishScores();
        scores.Record(Mission, Big, 700);
        Assert.False(scores.IsPrize(Mission, Big));   // nothing to compare with
        scores.Record(Mission, Small, 100);
        scores.Record(Mission, 52231, 400);
        Assert.True(scores.IsPrize(Mission, Big));
        Assert.True(scores.IsPrize(Mission, 52231));   // 400 >= 350
        Assert.False(scores.IsPrize(Mission, Small));
        Assert.False(scores.IsPrize(1676, Big));        // per mission
    }

    [Fact]
    public void Recorded_scores_survive_a_save_and_merge_with_what_shipped()
    {
        var dir = Directory.CreateTempSubdirectory("demeter-scores").FullName;
        try
        {
            var shipped = new FishScores();
            shipped.Record(Mission, Big, 600);
            shipped.Save(Path.Combine(dir, "shipped.json"));
            var recorded = new FishScores();
            recorded.Record(Mission, Big, 800);
            recorded.Record(Mission, Small, 100);
            recorded.Save(Path.Combine(dir, "recorded.json"));

            var both = FishScores.Load([Path.Combine(dir, "shipped.json"), Path.Combine(dir, "recorded.json"), Path.Combine(dir, "missing.json")]);
            var big = both.Of(Mission, Big)!;
            Assert.Equal((2, 600, 800, 700.0), (big.Count, big.Min, big.Max, big.Average));
            Assert.Equal(1, both.Of(Mission, Small)!.Count);
            Assert.False(both.Dirty);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void The_GP_meter_counts_what_comes_back_not_what_is_spent()
    {
        var meter = new GpMeter();
        var t = new DateTime(2026, 11, 14, 12, 0, 0, DateTimeKind.Utc);
        meter.Observe(t, 500, 900);
        meter.Observe(t.AddSeconds(3), 505, 900);
        meter.Observe(t.AddSeconds(4), 155, 900);   // spent 350: not a rate
        meter.Observe(t.AddSeconds(6), 160, 900);
        meter.Observe(t.AddSeconds(10), 170, 900);
        Assert.Equal(2.0, meter.PerSecond, 3);       // 20 GP over 10 s
        Assert.Equal(TimeSpan.FromSeconds(365), meter.Until(900));
        Assert.Equal(TimeSpan.Zero, meter.Until(100));
        Assert.Null(new GpMeter().Until(10));
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
        var gate = new FishGateway(session, new FishStand(new HoleApproachTests.World(), new HoleApproachTests.Travel(new HoleApproachTests.World()), () => 0, () => default), () => true, () => othersBusy, log.Add);

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
        var gate = new FishGateway(session, new FishStand(new HoleApproachTests.World(), new HoleApproachTests.Travel(new HoleApproachTests.World()), () => 0, () => default), () => false, () => false, _ => { });
        using var doc = JsonDocument.Parse(gate.CanFishHere("{}"));
        Assert.Equal("off", doc.RootElement.GetProperty("code").GetString());
    }
}
