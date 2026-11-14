using System.Numerics;
using Demeter.Services.Fishing;
using Demeter.Services.Gathering;
using Demeter.Services.Travel;

namespace Demeter.Tests;

public class FishGathererTests
{
    private const uint Bait = 2585, Always = 4776, Weathery = 4872, Mooched = 4874, Small = 4869;

    private static readonly DateTime Now = new(2026, 10, 22, 12, 0, 0, DateTimeKind.Utc);

    private static readonly FishingHole Pool = new(1, "Pool", 134, "Middle La Noscea", 0, 0, 100, 10);
    private static readonly FishingHole River = new(2, "River", 135, "Lower La Noscea", 0, 0, 100, 10);
    private static readonly FishingHole Deep = new(3, "Deep", 135, "Lower La Noscea", 0, 0, 100, 90);

    private sealed class Places : IFishPlaces
    {
        public Dictionary<uint, List<FishingHole>> Holes { get; } = new();
        public Dictionary<uint, IReadOnlyList<(uint, int)>> Rates { get; } = new();
        public IReadOnlyList<FishingHole> HolesFor(uint fishItemId) => Holes.GetValueOrDefault(fishItemId) ?? [];
        public IReadOnlyList<(uint Weather, int Cumulative)> RatesFor(uint territoryId) => Rates.GetValueOrDefault(territoryId) ?? [(1u, 100)];
        public string ItemName(uint id) => $"item {id}";
    }

    /// <summary>Enough game for a run to begin: idle, Fisher on, travel that arrives.</summary>
    private sealed class Idle : IFisherWorld, IHoleWorld
    {
        public DateTime UtcNow => Now;
        public bool IsOccupied => false;
        public FishState State => FishState.None;
        public Tug Tug => Tug.Unknown;
        public uint CurrentBait => Bait;
        public int ItemCount(uint itemId) => 0;
        public bool ChangeBait(uint baitId) => true;
        public bool CanMooch => false;
        public bool CanMooch2 => false;
        public bool HasStatus(uint statusId) => false;
        public int StatusStacks(uint statusId) => 0;
        public int Gp => 0;
        public int MaxGp => 0;
        public bool UseAction(uint actionId) => true;
        public IReadOnlyList<Catch> TakeCatches() => [];
        public int CollectableCount(uint itemId, int minimumCollectability) => 0;
        public (uint ItemId, int Collectability)? CollectablePrompt => null;
        public void AnswerCollectable(bool keep) { }
        public void Log(string message) { }
        public bool IsMounted => false;
        public uint CurrentClassJob => HoleApproach.Fisher;
        public bool EquipGearsetFor(uint classJobId, out string reason) { reason = string.Empty; return true; }
        public void Face(float rotation) { }
        public uint CastStatus => 0;
        public bool Cast() => true;
        public bool Quit() => true;
        public bool IsFishing => false;
    }

    private sealed class Nowhere : ITravel
    {
        public uint? LastTerritory { get; private set; }
        public TravelStatus Status => TravelStatus.Running;
        public string FailReason => string.Empty;
        public TravelFailure Failure => TravelFailure.None;
        public void Begin(uint territoryId, Vector3 to, bool fly, bool land, float stopDistance = Traveller.PointStop) => LastTerritory = territoryId;
        public TravelStatus Tick() => TravelStatus.Running;
        public void Cancel() { }
    }

    private static FishInfo Fish(uint id, params uint[] path) => new() { Id = id, Name = $"fish {id}", Bite = "Weak", Path = [.. path] };

    private static (FishGatherer Fisher, Places Places, FishingRun Run, Nowhere Travel) Make(int fisherLevel = 100, bool mooch = true,
        Dictionary<uint, HoleMemory>? memory = null)
    {
        var catalog = new FishCatalog([
            Fish(Always, Bait),
            Fish(Weathery, Bait) is var w ? Weatherbound(w) : null!,
            Fish(Small, Bait),
            Fish(Mooched, Bait, Small),
        ]);
        var places = new Places();
        foreach (var f in new[] { Always, Weathery, Mooched, Small })
            places.Holes[f] = [Pool, River];
        var holes = new FishingHoles([
            new HoleRecords { Id = Pool.Id, Stands = [new StandPoint { X = 1, Uses = 5 }] },
            new HoleRecords { Id = River.Id, Stands = [new StandPoint { X = 2, Uses = 50 }] },
            new HoleRecords { Id = Deep.Id, Stands = [new StandPoint { X = 3 }] },
        ]);
        var world = new Idle();
        var travel = new Nowhere();
        var run = new FishingRun(world, new HoleApproach(world, travel, () => { }), catalog, holes, _ => mooch, places.RatesFor, places.ItemName);
        memory ??= new();
        var fisher = new FishGatherer(catalog, places, holes, run,
            id => memory.TryGetValue(id, out var m) ? m : memory[id] = new HoleMemory(),
            job => job == HoleApproach.Fisher ? fisherLevel : 0, id => id != FishingRun.Mooch || mooch, () => Now);
        return (fisher, places, run, travel);
    }

    private static FishInfo Weatherbound(FishInfo f)
    {
        f.Weather = [2];
        return f;
    }

    [Fact]
    public void An_always_up_fish_can_be_gathered_and_has_no_wait()
    {
        var (fisher, _, _, _) = Make();
        Assert.True(fisher.Handles(Always));
        Assert.True(fisher.CanGather(Always));
        Assert.Null(fisher.WaitFor(Always, Now));
        Assert.Equal(string.Empty, fisher.Timer(Always, Now));
    }

    [Fact]
    public void The_most_used_hole_wins_among_equals_and_the_hint_wins_over_that()
    {
        var (fisher, _, _, _) = Make();
        Assert.Equal(River, fisher.ChooseHole(Always));               // 50 recorded catches vs 5
        Assert.Equal(Pool, fisher.ChooseHole(Always, territoryHint: Pool.TerritoryId));
    }

    [Fact]
    public void A_weather_fish_waits_where_the_weather_never_comes_and_says_so()
    {
        var (fisher, _, _, _) = Make();   // every zone only ever has weather 1
        Assert.False(fisher.CanGather(Weathery));
        Assert.Contains("does not open within two months", fisher.WhyNot(Weathery));
    }

    [Fact]
    public void A_weather_fish_opens_where_its_weather_does()
    {
        var (fisher, places, _, _) = Make();
        places.Rates[River.TerritoryId] = [(2u, 100)];   // the river always has weather 2
        Assert.True(fisher.CanGather(Weathery));
        Assert.Equal(River, fisher.ChooseHole(Weathery));
        Assert.Equal(TimeSpan.Zero, fisher.WaitFor(Weathery, Now));
        Assert.StartsWith("up", fisher.Timer(Weathery, Now));
    }

    [Fact]
    public void Holes_above_the_Fisher_level_are_left_out()
    {
        var (fisher, places, _, _) = Make(fisherLevel: 50);
        places.Holes[Always] = [Deep];
        Assert.False(fisher.CanGather(Always));
        Assert.Contains("Fisher Lv 90", fisher.WhyNot(Always));
    }

    [Fact]
    public void A_hole_whose_points_all_failed_is_not_offered()
    {
        var memory = new Dictionary<uint, HoleMemory>();
        var bad = new HoleMemory();
        bad.Failed(new StandPoint { X = 2 });
        memory[River.Id] = bad;
        var (fisher, _, _, _) = Make(memory: memory);
        Assert.Equal(Pool, fisher.ChooseHole(Always));
    }

    [Fact]
    public void A_mooch_fish_without_Mooch_says_why()
    {
        var (fisher, _, _, _) = Make(mooch: false);
        Assert.False(fisher.CanGather(Mooched));
        Assert.Contains("Mooch is not learned", fisher.WhyNot(Mooched));
        Assert.True(fisher.CanGather(Small));
    }

    [Fact]
    public void Start_begins_a_run_at_the_chosen_hole()
    {
        var (fisher, _, run, travel) = Make();
        Assert.True(fisher.Start(Always, 3, 0, territoryHint: Pool.TerritoryId));
        Assert.True(fisher.Busy);
        Assert.Equal(FishingRunState.Approaching, run.State);
        fisher.Tick();
        Assert.Equal(Pool.TerritoryId, travel.LastTerritory);
    }

    [Fact]
    public void A_collectable_ask_starts_a_collectable_run()
    {
        var (fisher, _, run, _) = Make();
        Assert.True(fisher.Start(Always, 1, collectability: 300));
        Assert.Equal(FishingRunState.Approaching, run.State);
    }

    [Fact]
    public void Not_a_fish_is_not_the_fishers()
    {
        var (fisher, _, _, _) = Make();
        Assert.False(fisher.Handles(5111));
    }

    /// <summary>A node gatherer that answers for everything, recording what it was asked.</summary>
    private sealed class Nodes : IOwnGatherer
    {
        public List<uint> Started { get; } = [];
        public bool Enabled { get; set; } = true;
        public bool CanGather(uint itemId, uint territoryHint = 0) => true;
        public string WhyNot(uint itemId, uint territoryHint = 0) => "nodes";
        public uint? ZoneOf(uint itemId) => 999;
        public string Where(uint itemId) => "a node";
        public bool Start(uint itemId, int count, int collectability, uint territoryHint = 0) { Started.Add(itemId); Busy = true; return true; }
        public void Tick() { }
        public bool Busy { get; private set; }
        public bool Faulted => false;
        public string Status => "mining";
        public bool DryRun { get; set; }
        public bool ProbeOnly { get; set; }
        public void Stop() => Busy = false;
    }

    [Fact]
    public void The_composite_sends_fish_to_the_fisher_and_ore_to_the_nodes()
    {
        var (fisher, _, run, _) = Make();
        var nodes = new Nodes();
        var all = new CompositeGatherer(nodes, fisher);

        Assert.Equal("a node", all.Where(5111));
        Assert.Contains("Fisher Lv", all.Where(Always));

        Assert.True(all.Start(5111, 1, 0));
        Assert.Equal([5111u], nodes.Started);
        Assert.Equal("mining", all.Status);
        all.Stop();

        Assert.True(all.Start(Always, 1, 0));
        Assert.Equal(FishingRunState.Approaching, run.State);
        Assert.True(all.Busy);
        Assert.Empty(nodes.Started.Skip(1));

        all.Enabled = false;
        Assert.False(fisher.Enabled);
        Assert.False(nodes.Enabled);
    }
}
