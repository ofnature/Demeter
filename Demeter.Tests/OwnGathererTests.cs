using System.Numerics;
using Demeter.Services.Gathering;
using Demeter.Services.Travel;

namespace Demeter.Tests;

public class OwnGathererTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "odysseus-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    /// <summary>Keyed by item, as the real one is: a lookup that answers for anything is not a lookup.</summary>
    private sealed class Source : IGatheringSource
    {
        public Dictionary<uint, List<GatheringPointRef>> Points { get; } = new();
        public GatheringOrigin? For(uint itemId) => null;
        public IReadOnlyList<uint> BasesFor(uint itemId) => [];
        public IReadOnlyList<GatheringPointRef> PointsFor(uint itemId)
            => Points.TryGetValue(itemId, out var points) ? points : [];
    }

    private NodeAtlas Atlas(string json)
    {
        Directory.CreateDirectory(_dir);
        var file = Path.Combine(_dir, NodeAtlas.FileName);
        using (var raw = File.Create(file))
        using (var gzip = new System.IO.Compression.GZipStream(raw, System.IO.Compression.CompressionLevel.Optimal))
        using (var writer = new StreamWriter(gzip))
            writer.Write(json);
        return new NodeAtlas(file);
    }

    private (OwnGatherer Own, GatherRunner Runner) Ready()
    {
        var atlas = Atlas("""{ "31427": [ {"X":-18.4,"Y":12.4,"Z":-427.6}, {"X":-20.0,"Y":12.4,"Z":-430.0} ] }""");
        var source = new Source();
        source.Points[12535] = [new GatheringPointRef(31427, 401, 0, 60)]; // Glass Eye

        // A world far from the node and on the wrong class, so a real run has work to do.
        var world = new GatherWorldStub { TerritoryId = 129, CurrentClassJob = 34 };
        var runner = new GatherRunner(world, new Traveller(new FakeTravelWorld()));
        // On, because these tests are about the gathering. It ships off; see the test below.
        var own = new OwnGatherer(runner, source, atlas, _ => { }) { Enabled = true };
        return (own, runner);
    }

    /// <summary>Enough of a world to start a runner; nothing here is exercised beyond that.</summary>
    private sealed class GatherWorldStub : IGatherWorld
    {
        public DateTime UtcNow { get; set; } = new(2026, 8, 22, 12, 0, 0, DateTimeKind.Utc);
        public uint TerritoryId { get; set; }
        public Vector3 PlayerPosition { get; set; }
        public bool IsOccupied => false;
        public uint CurrentClassJob { get; set; }
        public bool IsMounted { get; set; }
        public void Dismount() => IsMounted = false;
        public bool IsInFlight => false;
        public bool HasStatus(uint statusId) => true;
        public int Gp => 0;
        public bool OffCooldown(uint actionId) => true;
        public bool EquipGearsetFor(uint classJobId, out string reason) { CurrentClassJob = classJobId; reason = string.Empty; return true; }
        public int CollectableCount(uint itemId, int minimumCollectability) => 0;
        public bool IsDataIdSpawned(uint dataId) => false;
        public float? DistanceToDataId(uint dataId) => null;
        public Vector3? PositionOfDataId(uint dataId) => null;
        public (uint NodeId, Vector3 Position, float Distance)? NearestLiveNode(IReadOnlyCollection<uint> nodeIds) => null;
        public void FaceDataId(uint dataId) { }
        public bool TryInteractWithDataId(uint dataId) => false;
        public bool NodeOpen => false;
        public bool ItemListOpen => false;
        public bool ExecutingAction => false;
        public CollectableState? Collectable => null;
        public bool SelectSlotFor(uint itemId) => false;
        public bool UseAction(uint actionId) => true;
        public void StopMoving() { }
        public string Conditions => string.Empty;
        public void DescribeOpenWindow() { }
        public void ForgetSlotAttempts() { }
        public void CloseNode() { }
        public void Log(string message) { }
    }

    [Fact]
    public void The_zone_chosen_is_one_the_character_can_work_not_the_one_with_most_spots()
    {
        // Ice Crystal: Yak T'el has forty-seven spots and no aetheryte this character has; the
        // North Shroud has fewer and is a teleport away.
        var atlas = Atlas("""{ "31427": [ {"X":1,"Y":0,"Z":1}, {"X":2,"Y":0,"Z":2}, {"X":3,"Y":0,"Z":3} ], "30329": [ {"X":5,"Y":0,"Z":5} ] }""");
        var source = new Source();
        source.Points[9] = [new GatheringPointRef(31427, 1189, 0, 95), new GatheringPointRef(30329, 154, 0, 20)];
        var log = new List<string>();
        var world = new GatherWorldStub { TerritoryId = 129, CurrentClassJob = 34 };
        var own = new OwnGatherer(new GatherRunner(world, new Traveller(new FakeTravelWorld())), source, atlas, log.Add)
        {
            Enabled = true,
            Workable = t => t.TerritoryId != 1189,
        };

        Assert.Equal(154u, own.ZoneOf(9));
        Assert.True(own.Start(9, 96, 0));
        Assert.Contains(log, l => l.Contains("in territory 154"));
    }

    /// <summary>Ice Crystal in Coerthas Western Highlands (two spots, a mesh with no path) and the South Shroud.</summary>
    private (OwnGatherer Own, GatherWorldStub World, FakeTravelWorld Fake, List<string> Log) NoWayInCoerthas(bool southShroudToo)
    {
        var atlas = Atlas("""{ "31310": [ {"X":500,"Y":0,"Z":500}, {"X":520,"Y":0,"Z":500} ], "30325": [ {"X":5,"Y":0,"Z":5} ] }""");
        var source = new Source();
        source.Points[9] = southShroudToo
            ? [new GatheringPointRef(31310, 397, 0, 50), new GatheringPointRef(30325, 153, 0, 30)]
            : [new GatheringPointRef(31310, 397, 0, 50)];
        var log = new List<string>();
        var world = new GatherWorldStub { TerritoryId = 397, CurrentClassJob = 16 };
        var fake = new FakeTravelWorld { TerritoryId = 397 };
        var own = new OwnGatherer(new GatherRunner(world, new Traveller(fake)), source, atlas, log.Add) { Enabled = true };
        return (own, world, fake, log);
    }

    private static void TickWithNoPath(OwnGatherer own, GatherWorldStub world, FakeTravelWorld fake, Func<bool> until)
    {
        for (var i = 0; i < 400 && !until(); i++)
        {
            own.Tick();
            if (fake.IsMoving) fake.FinishMove("no path (failed)");
            world.UtcNow = world.UtcNow.AddSeconds(0.5);
            fake.Advance(0.5);
        }
    }

    [Fact]
    public void A_zone_with_no_way_round_is_left_for_another_the_character_can_work()
    {
        var (own, world, fake, log) = NoWayInCoerthas(southShroudToo: true);
        Assert.True(own.Start(9, 10, 0));
        TickWithNoPath(own, world, fake, () => log.Any(l => l.Contains("instead")));

        Assert.Contains(log, l => l.Contains("found no way to any of the 2 spots") && l.Contains("trying territory 153 instead"));
        Assert.Contains(log, l => l.StartsWith("Gathering 10 × 9") && l.Contains("in territory 153"));
        Assert.True(own.Busy);
        Assert.Equal(153u, own.ZoneOf(9));   // and passed over for the rest of the session
    }

    [Fact]
    public void A_zone_with_no_way_round_and_nowhere_else_still_stops_and_says_why()
    {
        var (own, world, fake, log) = NoWayInCoerthas(southShroudToo: false);
        Assert.True(own.Start(9, 10, 0));
        TickWithNoPath(own, world, fake, () => own.Faulted);

        Assert.True(own.Faulted);
        Assert.Contains("found no way to any of the 2 spots", own.Status);
        Assert.DoesNotContain(log, l => l.Contains("instead"));
    }

    [Fact]
    public void A_zone_is_named_in_the_log_when_the_name_is_known()
    {
        Zones.Name = id => id == 1189 ? "Yak T'el" : null;
        try
        {
            Assert.Equal("territory 1189 (Yak T'el)", Zones.Describe(1189));
            Assert.Equal("territory 153", Zones.Describe(153));
        }
        finally { Zones.Name = null; }
    }

    [Fact]
    public void With_nowhere_workable_the_best_zone_is_still_chosen_so_the_run_can_say_why()
    {
        var atlas = Atlas("""{ "31427": [ {"X":1,"Y":0,"Z":1}, {"X":2,"Y":0,"Z":2} ], "30329": [ {"X":5,"Y":0,"Z":5} ] }""");
        var source = new Source();
        source.Points[9] = [new GatheringPointRef(31427, 1189, 0, 95), new GatheringPointRef(30329, 154, 0, 20)];
        var world = new GatherWorldStub { TerritoryId = 129, CurrentClassJob = 34 };
        var own = new OwnGatherer(new GatherRunner(world, new Traveller(new FakeTravelWorld())), source, atlas, _ => { })
        {
            Enabled = true,
            Workable = _ => false,
        };

        Assert.Equal(1189u, own.ZoneOf(9));
    }

    [Fact]
    public void An_item_whose_nodes_are_all_above_the_gatherers_level_is_not_started()
    {
        // Rainbow Cotton Boll: Botanist 55 nodes at Twinpools, never shown to a Botanist below it,
        // so the run walked its twelve spots finding nothing.
        var atlas = Atlas("""{ "31383": [ {"X":1,"Y":0,"Z":1}, {"X":2,"Y":0,"Z":2} ], "31382": [ {"X":5,"Y":0,"Z":5} ] }""");
        var source = new Source();
        source.Points[12597] = [new GatheringPointRef(31383, 397, 3, 55), new GatheringPointRef(31382, 397, 3, 55)];
        var log = new List<string>();
        var world = new GatherWorldStub { TerritoryId = 129, CurrentClassJob = 34 };
        var level = 49;
        var own = new OwnGatherer(new GatherRunner(world, new Traveller(new FakeTravelWorld())), source, atlas, log.Add)
        {
            Enabled = true,
            LevelOf = job => job == 17 ? level : 0,
        };

        Assert.False(own.Start(12597, 99, 0));
        Assert.False(own.Busy);
        Assert.Contains(log, l => l.Contains("needs Botanist 55; this character is Botanist 49"));
        Assert.Contains("needs Botanist 55", own.WhyNot(12597));
        Assert.Equal("needs Botanist 55; this character is Botanist 49 — its nodes do not show below that", own.LevelLock(12597));

        level = 55;
        Assert.Null(own.LevelLock(12597));
        Assert.True(own.Start(12597, 99, 0));
    }

    [Fact]
    public void A_run_that_has_only_just_begun_reads_as_busy()
    {
        // It began in SwitchingJob, which an enumeration of "the busy states" missed — and a caller
        // seeing idle one tick after starting concludes it gave up before it moved, which is exactly
        // what "gathering stopped short" meant in the field.
        var (own, runner) = Ready();

        Assert.False(own.Busy);
        Assert.True(own.Start(12535, 6, 240));
        Assert.Equal(GatherRunState.SwitchingJob, runner.State);
        Assert.True(own.Busy, "a runner that has just started must not read as finished");
    }

    [Fact]
    public void Every_state_that_is_not_finished_counts_as_busy()
    {
        // The property is the inverse of three states, so a state added later cannot quietly mean
        // "done" — this pins that rather than the three names.
        var (own, runner) = Ready();
        own.Start(12535, 6, 240);

        foreach (var state in Enum.GetValues<GatherRunState>())
        {
            var finished = state is GatherRunState.Idle or GatherRunState.Done or GatherRunState.Faulted;
            if (finished) continue;
            Assert.True(state is GatherRunState.SwitchingJob or GatherRunState.Travelling
                or GatherRunState.Walking or GatherRunState.Opening or GatherRunState.Working
                or GatherRunState.Closing,
                $"{state} is a running state and must be covered by Busy");
        }

        own.Stop();
        Assert.Equal(GatherRunState.Idle, runner.State);
        Assert.False(own.Busy);
    }

    [Fact]
    public void It_is_off_until_switched_on_so_a_delivery_falls_back_to_the_handoff()
    {
        // Opening a node has wedged the client, so nothing reaches that path by accident: a
        // delivery sees "cannot gather this" and hands to GatherBuddy exactly as it always did.
        var (own, _) = Ready();
        own.Enabled = false;

        Assert.False(own.CanGather(12535));
        Assert.False(own.Start(12535, 6, 240) && own.Busy);
    }

    [Fact]
    public void An_item_with_no_node_is_declined_so_the_caller_can_fall_back()
    {
        var (own, _) = Ready();
        Assert.False(own.CanGather(99999));
        Assert.False(own.Start(99999, 1, 240));
        Assert.True(own.CanGather(12535));
    }
}
