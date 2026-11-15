using System.Numerics;
using Demeter.Services.Fishing;
using Demeter.Services.Travel;

namespace Demeter.Tests;

public class FishingRunTests
{
    // Ids shaped like the real thing; the values only have to agree with each other.
    private const uint Bait = 2585, Other = 4776, Small = 4869, Target = 4872, Predator = 4874;

    private static readonly FishingHole Hole = new(36, "Test Pool", 134, "Middle La Noscea", 0, 0, 100, 10);

    // ── the judge ──

    private static readonly BiteTimes[] Bites =
    [
        new() { Fish = Target, Bait = Bait, Count = 10, Min = 15, Max = 20, MinChum = 8, MaxChum = 11 },
        new() { Fish = Other, Bait = Bait, Count = 30, Min = 5, Max = 9 },
        new() { Fish = Small, Bait = Bait, Count = 20, Min = 14, Max = 18 },
    ];

    private static Tug TugOf(uint id) => id switch { Target => Tug.Heavy, Small => Tug.Light, Other => Tug.Light, _ => Tug.Unknown };

    [Fact]
    public void A_bite_only_the_target_could_be_is_hooked()
        => Assert.True(BiteJudge.Judge(Bait, 19.5, Tug.Unknown, false, new HashSet<uint> { Target }, Bites, TugOf).Hook);

    [Fact]
    public void A_bite_only_unwanted_fish_could_be_is_let_go()
    {
        var call = BiteJudge.Judge(Bait, 6, Tug.Unknown, false, new HashSet<uint> { Target }, Bites, TugOf);
        Assert.False(call.Hook);
        Assert.Equal([Other], call.Candidates);
    }

    [Fact]
    public void Where_times_overlap_the_tug_tells_them_apart()
    {
        // 16 s fits both the target (heavy) and Small (light).
        Assert.False(BiteJudge.Judge(Bait, 16, Tug.Light, false, new HashSet<uint> { Target }, Bites, TugOf).Hook);
        Assert.True(BiteJudge.Judge(Bait, 16, Tug.Heavy, false, new HashSet<uint> { Target }, Bites, TugOf).Hook);
    }

    [Fact]
    public void Chum_uses_the_shorter_ranges()
    {
        Assert.True(BiteJudge.Judge(Bait, 9.5, Tug.Unknown, chum: true, new HashSet<uint> { Target }, Bites, TugOf).Hook);
        Assert.False(BiteJudge.Judge(Bait, 9.5, Tug.Unknown, chum: false, new HashSet<uint> { Target }, Bites, TugOf).Hook);
    }

    [Fact]
    public void A_bite_no_record_explains_is_hooked_to_see()
        => Assert.True(BiteJudge.Judge(9999, 30, Tug.Medium, false, new HashSet<uint> { Target }, Bites, TugOf).Hook);

    [Fact]
    public void Outside_every_range_but_the_wrong_tug_for_the_target_is_let_go()
        => Assert.False(BiteJudge.Judge(Bait, 40, Tug.Light, false, new HashSet<uint> { Target }, Bites, TugOf).Hook);

    [Fact]
    public void A_wanted_fish_never_recorded_on_this_bait_here_gets_every_bite_with_its_tug()
    {
        // A fish that bites this bait with a light tug but was never recorded at this hole: a light
        // bite that fits only another fish's range may still be it.
        const uint kelp = 9001;
        Tug Tugs(uint id) => id == kelp ? Tug.Light : TugOf(id);
        uint? BitesOn(uint id) => id == kelp ? Bait : null;
        var wanted = new HashSet<uint> { kelp };

        var light = BiteJudge.Judge(Bait, 6, Tug.Light, false, wanted, Bites, Tugs, BitesOn);
        Assert.True(light.Hook);
        Assert.Contains("never recorded", light.Why);

        // Not its tug: still let go.
        Assert.False(BiteJudge.Judge(Bait, 6, Tug.Heavy, false, wanted, Bites, Tugs, BitesOn).Hook);

        // A fish that bites only a mooched fish says nothing about bites on the bait.
        Assert.False(BiteJudge.Judge(Bait, 6, Tug.Light, false, wanted, Bites, Tugs, _ => 9999u).Hook);
    }

    // ── the run, against a scripted hole ──

    /// <summary>
    /// A fishing hole that answers like the game: a cast puts the line in, the next scripted bite for
    /// the bait (or mooched fish) comes after its seconds, a hook during the bite lands it, no hook and
    /// it gets away. The bag, the bait, the mooch flag and statuses are all here.
    /// </summary>
    internal sealed class Pond : IFisherWorld, IHoleWorld
    {
        public DateTime UtcNow { get; set; } = new(2026, 10, 22, 12, 0, 0, DateTimeKind.Utc);
        public bool IsOccupied => false;
        public FishState State { get; set; } = FishState.None;
        public Tug Tug { get; private set; }
        public uint CurrentBait { get; set; }
        public Dictionary<uint, int> Bag { get; } = new();
        public Dictionary<uint, Queue<(uint Fish, double Seconds)>> Script { get; } = new();
        public Dictionary<uint, Tug> Tugs { get; } = new();
        public HashSet<uint> Moochable { get; } = [];
        public HashSet<uint> Statuses { get; } = [];
        public List<uint> Hooked { get; } = [];
        public List<uint> LetGo { get; } = [];
        public List<uint> Actions { get; } = [];

        /// <summary>Every press, with when it came.</summary>
        public List<(uint Action, DateTime At)> Pressed { get; } = [];

        /// <summary>No water in front: a cast does not go in.</summary>
        public bool NoWater { get; set; }
        public List<string> Logged { get; } = [];
        public Action<uint>? OnLanded { get; set; }

        /// <summary>Collectability per landed fish, in order; a collectable catch asks before it is kept.</summary>
        public Queue<int> Values { get; } = new();
        public List<(uint Fish, int Value)> Collectables { get; } = [];
        public List<(uint Fish, int Value, bool Kept)> Answers { get; } = [];
        private (uint ItemId, int Collectability)? _prompt;
        private readonly List<Catch> _catches = [];
        private (uint Fish, DateTime At)? _bite;
        private DateTime _biteStarted;
        private uint _swimming;
        private uint _last;

        public bool CanMooch => State == FishState.PoleReady && Moochable.Contains(_last);
        public bool CanMooch2 => false;
        public int ItemCount(uint itemId) => Bag.GetValueOrDefault(itemId);

        public bool ChangeBait(uint baitId)
        {
            if (ItemCount(baitId) <= 0) return false;
            CurrentBait = baitId;
            return true;
        }

        public bool HasStatus(uint statusId) => Statuses.Contains(statusId);
        public Dictionary<uint, int> Stacks { get; } = new();

        /// <summary>Each fish's length when landed, in ilms; none when absent.</summary>
        public Dictionary<uint, double> Ilms { get; } = new();
        public int StatusStacks(uint statusId) => Stacks.GetValueOrDefault(statusId);
        public int Gp { get; set; } = 1000;
        public int MaxGp { get; set; } = 1000;

        public bool UseAction(uint actionId)
        {
            Actions.Add(actionId);
            Pressed.Add((actionId, UtcNow));
            switch (actionId)
            {
                case FishingRun.Cast when State is FishState.None or FishState.PoleReady && !NoWater:
                    Line(CurrentBait);
                    break;
                case FishingRun.Mooch when CanMooch:
                    Line(_last);
                    break;
                case FishSession.IdenticalCast:
                    Gp -= 350;
                    Statuses.Add(FishSession.IdenticalStatus);
                    break;
                case FishSession.PrizeCatch:
                    Gp -= 200;
                    Statuses.Add(FishSession.PrizeStatus);
                    break;
                case FishSession.ThaliaksFavor:
                    Gp = Math.Min(MaxGp, Gp + 150);
                    Stacks[FishSession.AnglersArt] -= 3;
                    break;
                case FishSession.DoubleHook or FishSession.TripleHook when State == FishState.Bite && _bite is { } m:
                    var amount = actionId == FishSession.TripleHook ? 3 : 2;
                    Gp -= actionId == FishSession.TripleHook ? 700 : 400;
                    Statuses.Remove(FishSession.IdenticalStatus);
                    Hooked.Add(m.Fish);
                    _last = m.Fish;
                    _bite = null;
                    Bag[m.Fish] = Bag.GetValueOrDefault(m.Fish) + amount;
                    _catches.Add(new Catch(m.Fish, amount, false, false, Ilms.GetValueOrDefault(m.Fish)));
                    State = FishState.PoleReady;
                    OnLanded?.Invoke(m.Fish);
                    break;
                case FishingRun.Hook when State == FishState.Bite && _bite is { } b:
                    Statuses.Remove(FishSession.IdenticalStatus);
                    Statuses.Remove(FishSession.PrizeStatus);
                    Hooked.Add(b.Fish);
                    _last = b.Fish;
                    _bite = null;
                    if (Statuses.Contains(FishingRun.CollectStatus))
                    {
                        _prompt = (b.Fish, Values.Count > 0 ? Values.Dequeue() : 0);
                        State = FishState.ConfirmingCollectable;
                        break;
                    }
                    Bag[b.Fish] = Bag.GetValueOrDefault(b.Fish) + 1;
                    _catches.Add(new Catch(b.Fish, 1, false, false, Ilms.GetValueOrDefault(b.Fish)));
                    State = FishState.PoleReady;
                    OnLanded?.Invoke(b.Fish);
                    break;
                case FishingRun.Collect:
                    if (!Statuses.Remove(FishingRun.CollectStatus))
                        Statuses.Add(FishingRun.CollectStatus);
                    break;
                case FishingRun.Quit:
                    State = FishState.None;
                    _bite = null;
                    break;
            }
            return true;
        }

        private void Line(uint bait)
        {
            _swimming = bait;
            State = FishState.LineInWater;
            _bite = Script.TryGetValue(bait, out var q) && q.Count > 0 ? (q.Peek().Fish, UtcNow.AddSeconds(q.Dequeue().Seconds)) : null;
        }

        public void Advance(double seconds)
        {
            UtcNow = UtcNow.AddSeconds(seconds);
            if (State == FishState.LineInWater && _bite is { } b && UtcNow >= b.At)
            {
                State = FishState.Bite;
                Tug = Tugs.GetValueOrDefault(b.Fish);
                _biteStarted = UtcNow;
            }
            else if (State == FishState.Bite && UtcNow - _biteStarted > TimeSpan.FromSeconds(3))
            {
                LetGo.Add(_bite!.Value.Fish);   // not hooked in time: it got away
                _bite = null;
                _last = 0;
                State = FishState.PoleReady;
            }
        }

        public IReadOnlyList<Catch> TakeCatches()
        {
            var r = _catches.ToList();
            _catches.Clear();
            return r;
        }

        public void Log(string message) => Logged.Add(message);

        public int CollectableCount(uint itemId, int minimumCollectability)
            => minimumCollectability <= 0
                ? Bag.GetValueOrDefault(itemId) + Collectables.Count(c => c.Fish == itemId)
                : Collectables.Count(c => c.Fish == itemId && c.Value >= minimumCollectability);

        public (uint ItemId, int Collectability)? CollectablePrompt => State == FishState.ConfirmingCollectable ? _prompt : null;

        public void AnswerCollectable(bool keep)
        {
            var (fish, value) = _prompt!.Value;
            Answers.Add((fish, value, keep));
            if (keep)
            {
                Collectables.Add((fish, value));
                _catches.Add(new Catch(fish, 1, true, false));
            }
            _prompt = null;
            State = FishState.PoleReady;
        }

        // ── the approach's view: already at the hole, Fisher on; the test cast proves it ──
        public bool IsMounted => false;
        public uint CurrentClassJob => HoleApproach.Fisher;
        public bool EquipGearsetFor(uint classJobId, out string reason) { reason = string.Empty; return true; }
        public void Face(float rotation) { }
        public uint CastStatus => 0;
        bool IHoleWorld.Cast() { TestCasts++; Line(CurrentBait); return true; }
        public int TestCasts { get; private set; }
        bool IHoleWorld.Quit() { State = FishState.None; return true; }
        public bool IsFishing => State != FishState.None;
    }

    private sealed class Here : ITravel
    {
        public TravelStatus Status { get; private set; }
        public string FailReason => string.Empty;
        public TravelFailure Failure => TravelFailure.None;
        public void Begin(uint territoryId, Vector3 to, bool fly, bool land, float stopDistance = Traveller.PointStop, bool teleport = true) => Status = TravelStatus.Running;
        public TravelStatus Tick() => Status = TravelStatus.Done;
        public void Cancel() => Status = TravelStatus.Idle;
    }

    private static FishInfo Fish(uint id, string bite, params uint[] path) => new()
    {
        Id = id, Name = $"fish {id}", Bite = bite, HookSet = "Precise", Path = [.. path],
    };

    private static (FishingRun Run, Pond Pond) Make(IEnumerable<FishInfo> fish, IReadOnlyList<BiteTimes>? bites = null,
        Func<uint, bool>? knows = null, IReadOnlyList<(uint, int)>? rates = null)
    {
        var pond = new Pond();
        pond.Bag[Bait] = 50;
        var approach = new HoleApproach(pond, new Here(), () => { });
        var holes = new FishingHoles([new HoleRecords { Id = Hole.Id, Stands = [new StandPoint { X = 1 }], Bites = [.. bites ?? Bites] }]);
        var run = new FishingRun(pond, approach, new FishCatalog(fish), holes, knows ?? (_ => true),
            _ => rates ?? [(1u, 100)], id => $"item {id}");
        return (run, pond);
    }

    private static FishingRunState Run(FishingRun run, Pond pond, int ticks = 2000)
    {
        for (var i = 0; i < ticks && run.State is not (FishingRunState.Done or FishingRunState.Faulted); i++)
        {
            run.Tick();
            pond.Advance(0.5);
        }
        return run.State;
    }

    [Fact]
    public void It_casts_with_the_bait_hooks_the_target_and_stops_at_the_count()
    {
        var target = Fish(Target, "Legendary", Bait);
        var (run, pond) = Make([target]);
        pond.Script[Bait] = new([(Target, 17), (Target, 16)]);
        pond.Tugs[Target] = Tug.Heavy;

        run.Begin(target, 2, Hole, [new StandPoint { X = 1 }], new HoleMemory());
        Assert.Equal(FishingRunState.Done, Run(run, pond));

        Assert.Equal(2, pond.Bag[Target]);
        Assert.Equal(Bait, pond.CurrentBait);           // the road's bait was put on
        Assert.Equal(FishState.None, pond.State);       // reeled in at the end
        Assert.Contains(FishingRun.Quit, pond.Actions);
    }

    [Fact]
    public void Unwanted_bites_are_let_go_and_the_target_still_comes()
    {
        var target = Fish(Target, "Legendary", Bait);
        var (run, pond) = Make([target, Fish(Other, "Weak", Bait)]);
        pond.Script[Bait] = new([(Other, 6), (Other, 7), (Target, 18)]);
        pond.Tugs[Other] = Tug.Light;
        pond.Tugs[Target] = Tug.Heavy;

        run.Begin(target, 1, Hole, [new StandPoint { X = 1 }], new HoleMemory());
        Assert.Equal(FishingRunState.Done, Run(run, pond));

        Assert.Equal([Other, Other], pond.LetGo);
        Assert.Equal([Target], pond.Hooked);
        Assert.Equal(2, pond.Logged.Count(m => m.Contains("letting it go")));
    }

    [Fact]
    public void A_mooch_road_moochs_from_the_catch()
    {
        // Bait → Small, then mooch Small → Target.
        var small = Fish(Small, "Weak", Bait);
        var target = Fish(Target, "Legendary", Bait, Small);
        var bites = new BiteTimes[]
        {
            new() { Fish = Small, Bait = Bait, Min = 5, Max = 10 },
            new() { Fish = Target, Bait = Small, Min = 12, Max = 20 },
        };
        var (run, pond) = Make([small, target], bites);
        pond.Script[Bait] = new([(Small, 7)]);
        pond.Script[Small] = new([(Target, 15)]);
        pond.Moochable.Add(Small);

        run.Begin(target, 1, Hole, [new StandPoint { X = 1 }], new HoleMemory());
        Assert.Equal(FishingRunState.Done, Run(run, pond));

        Assert.Equal([Small, Target], pond.Hooked);
        Assert.Contains(FishingRun.Mooch, pond.Actions);
    }

    [Fact]
    public void Predators_come_first_and_Intuition_lets_the_target_through()
    {
        var predator = Fish(Predator, "Strong", Bait);
        var target = Fish(Target, "Legendary", Bait);
        target.Predators = [[(int)Predator, 2]];
        var bites = new BiteTimes[]
        {
            new() { Fish = Predator, Bait = Bait, Min = 5, Max = 10 },
            new() { Fish = Target, Bait = Bait, Min = 15, Max = 20 },
        };
        var (run, pond) = Make([predator, target], bites);
        pond.Script[Bait] = new([(Predator, 7), (Predator, 8), (Target, 17)]);
        pond.OnLanded = fish =>
        {
            if (fish == Predator && pond.Bag.GetValueOrDefault(Predator) >= 2)
                pond.Statuses.Add(FishingRun.IntuitionStatus);   // the game grants it on the second
        };

        run.Begin(target, 1, Hole, [new StandPoint { X = 1 }], new HoleMemory());
        Assert.Equal(FishingRunState.Done, Run(run, pond));

        Assert.Equal([Predator, Predator, Target], pond.Hooked);
        Assert.Contains(pond.Logged, m => m.Contains("Landed item " + Predator));
    }

    [Fact]
    public void No_bait_in_the_bag_stops_and_names_it()
    {
        var target = Fish(Target, "Legendary", 2606);
        var (run, pond) = Make([target]);

        run.Begin(target, 1, Hole, [new StandPoint { X = 1 }], new HoleMemory());
        Assert.Equal(FishingRunState.Faulted, Run(run, pond));
        Assert.Contains("no item 2606 in the bags", run.FailReason);
    }

    [Fact]
    public void A_shut_window_ends_the_run()
    {
        // Wants weather 9; the zone only ever has weather 1.
        var target = Fish(Target, "Legendary", Bait);
        target.Weather = [9];
        var (run, pond) = Make([target]);
        pond.Script[Bait] = new([(Target, 17)]);

        run.Begin(target, 1, Hole, [new StandPoint { X = 1 }], new HoleMemory());
        Assert.Equal(FishingRunState.Done, Run(run, pond));
        Assert.Empty(pond.Hooked);
        Assert.Contains("window has closed", run.Status);
    }

    [Fact]
    public void Spearfish_and_ocean_fish_are_refused_at_once()
    {
        var spear = Fish(Target, "Legendary", Bait);
        spear.SpearSize = "Large";
        var (run, _) = Make([spear]);
        run.Begin(spear, 1, Hole, [new StandPoint { X = 1 }], new HoleMemory());
        Assert.Equal(FishingRunState.Faulted, run.State);
        Assert.Contains("spearfished", run.FailReason);
    }

    [Fact]
    public void A_mooch_road_without_Mooch_learned_is_refused_up_front()
    {
        var small = Fish(Small, "Weak", Bait);
        var target = Fish(Target, "Legendary", Bait, Small);
        var (run, _) = Make([small, target], knows: id => id != FishingRun.Mooch);
        run.Begin(target, 1, Hole, [new StandPoint { X = 1 }], new HoleMemory());
        Assert.Equal(FishingRunState.Faulted, run.State);
        Assert.Contains("Mooch is not learned", run.FailReason);
    }

    [Fact]
    public void A_collectable_target_puts_Collect_on_and_keeps_only_what_clears_the_bar()
    {
        var target = Fish(Target, "Legendary", Bait);
        var (run, pond) = Make([target]);
        pond.Script[Bait] = new([(Target, 17), (Target, 16), (Target, 18)]);
        pond.Values.Enqueue(310);   // below 400: let go
        pond.Values.Enqueue(452);
        pond.Values.Enqueue(400);

        run.Begin(target, 2, Hole, [new StandPoint { X = 1 }], new HoleMemory(), collectability: 400);
        Assert.Equal(FishingRunState.Done, Run(run, pond));

        Assert.Contains(FishingRun.Collect, pond.Actions);
        Assert.Equal([(Target, 310, false), (Target, 452, true), (Target, 400, true)], pond.Answers);
        Assert.Equal(2, pond.CollectableCount(Target, 400));
    }

    [Fact]
    public void A_plain_run_switches_Collect_off_so_no_prompt_comes()
    {
        var target = Fish(Target, "Legendary", Bait);
        var (run, pond) = Make([target]);
        pond.Statuses.Add(FishingRun.CollectStatus);   // left on from a delivery
        pond.Script[Bait] = new([(Target, 17)]);

        run.Begin(target, 1, Hole, [new StandPoint { X = 1 }], new HoleMemory());
        Assert.Equal(FishingRunState.Done, Run(run, pond));
        Assert.DoesNotContain(FishingRun.CollectStatus, pond.Statuses);
        Assert.Empty(pond.Answers);
    }

    [Fact]
    public void A_collectable_without_Collect_learned_is_refused_and_names_the_quest()
    {
        var target = Fish(Target, "Legendary", Bait);
        var (run, _) = Make([target], knows: id => id != FishingRun.Collect);
        run.Begin(target, 1, Hole, [new StandPoint { X = 1 }], new HoleMemory(), collectability: 400);
        Assert.Equal(FishingRunState.Faulted, run.State);
        Assert.Contains("Inscrutable Tastes", run.FailReason);
    }

    [Fact]
    public void The_test_cast_with_the_right_bait_on_is_fished_not_reeled_in()
    {
        // Limsa, 2026-11-14: the test cast came in, the rod went away and came out again, for nothing.
        var target = Fish(Target, "Legendary", Bait);
        var (run, pond) = Make([target]);
        pond.CurrentBait = Bait;
        pond.Script[Bait] = new([(Target, 17)]);
        pond.Tugs[Target] = Tug.Heavy;

        run.Begin(target, 1, Hole, [new StandPoint { X = 1 }], new HoleMemory());
        Assert.Equal(FishingRunState.Done, Run(run, pond));
        Assert.Equal(1, pond.TestCasts);
        Assert.DoesNotContain(FishingRun.Cast, pond.Actions);   // the test cast was the cast
        Assert.Equal(1, pond.Bag[Target]);
        Assert.Contains(pond.Logged, m => m.Contains("fishing it"));
    }

    [Fact]
    public void The_test_cast_with_the_wrong_bait_on_is_reeled_in_and_the_bait_changed()
    {
        var target = Fish(Target, "Legendary", Bait);
        var (run, pond) = Make([target]);
        pond.CurrentBait = 0;
        pond.Script[Bait] = new([(Target, 17)]);
        pond.Tugs[Target] = Tug.Heavy;

        run.Begin(target, 1, Hole, [new StandPoint { X = 1 }], new HoleMemory());
        Assert.Equal(FishingRunState.Done, Run(run, pond));
        Assert.True(pond.Actions.IndexOf(FishingRun.Quit) < pond.Actions.IndexOf(FishingRun.Cast));
        Assert.Equal(Bait, pond.CurrentBait);
        Assert.Equal(1, pond.Bag[Target]);
        Assert.Contains(pond.Logged, m => m.Contains("reeling in to cast with"));
    }

    [Fact]
    public void Asked_to_stop_after_the_cast_it_lands_what_bites_and_casts_no_more()
    {
        var target = Fish(Target, "Legendary", Bait);
        var (run, pond) = Make([target]);
        pond.Script[Bait] = new([(Target, 17), (Target, 16)]);
        pond.Tugs[Target] = Tug.Heavy;

        run.Begin(target, 2, Hole, [new StandPoint { X = 1 }], new HoleMemory());
        for (var i = 0; i < 200 && !(run.State == FishingRunState.Fishing && pond.State == FishState.LineInWater
                                      && pond.Actions.Contains(FishingRun.Cast)); i++)
        {
            run.Tick();
            pond.Advance(0.5);
        }
        Assert.Equal(FishState.LineInWater, pond.State);
        run.StopAfterCast();
        run.StopAfterCast();   // asking again changes nothing

        Assert.Equal(FishingRunState.Done, Run(run, pond));
        Assert.Equal(1, pond.Bag[Target]);                    // the bite on the line was landed
        Assert.Single(pond.Actions, a => a == FishingRun.Cast);   // and no second cast
        Assert.Equal(FishState.None, pond.State);             // reeled in
        Assert.Single(pond.Logged, m => m.Contains("Stopping after this cast"));
    }
}
