using System.Numerics;
using Demeter.Services.Fishing;
using Demeter.Services.Travel;

namespace Demeter.Tests;

public class HoleApproachTests
{
    private static readonly FishingHole Hole = new(321, "Westbound Zorgor", 1190, "Shaaloani", 753, 1860, 1000, 100);

    private static StandPoint At(float x, float rotation = 1f, int uses = 1) => new() { X = x, Y = 0, Z = 0, Rotation = rotation, Uses = uses };

    /// <summary>The hole as the game sees it: a cast takes only from the stand points listed as wet.</summary>
    private sealed class World : IHoleWorld
    {
        public DateTime UtcNow { get; set; } = new(2026, 10, 22, 12, 0, 0, DateTimeKind.Utc);
        public bool IsOccupied { get; set; }
        public bool IsMounted { get; set; }
        public uint CurrentClassJob { get; set; } = HoleApproach.Fisher;
        public bool HasFisherGearset { get; set; } = true;
        public Vector3 Position { get; set; }
        public float Facing { get; private set; }
        public HashSet<float> Wet { get; } = [];
        public bool IsFishing { get; set; }
        public int Casts { get; private set; }
        public int Quits { get; private set; }
        public List<string> Logged { get; } = [];
        public uint CastStatus => 0;

        public bool EquipGearsetFor(uint classJobId, out string reason)
        {
            reason = HasFisherGearset ? string.Empty : "no gearset for Fisher";
            if (HasFisherGearset) CurrentClassJob = classJobId;
            return HasFisherGearset;
        }

        public void Face(float rotation) => Facing = rotation;

        public bool Cast()
        {
            Casts++;
            if (Wet.Contains(Position.X)) IsFishing = true;
            return true;
        }

        public bool Quit()
        {
            Quits++;
            IsFishing = false;
            return true;
        }

        public void Log(string message) => Logged.Add(message);
        public void Advance(double seconds) => UtcNow = UtcNow.AddSeconds(seconds);
    }

    /// <summary>Arrives at once, except at the points listed as unreachable.</summary>
    private sealed class Travel(World world) : ITravel
    {
        public HashSet<float> Unreachable { get; } = [];

        /// <summary>Set: every leg fails this way, before any point is reached (no way into the zone).</summary>
        public TravelFailure? FailEveryLeg { get; set; }
        public TravelStatus Status { get; private set; }
        public string FailReason { get; private set; } = string.Empty;
        public TravelFailure Failure { get; private set; }
        public List<Vector3> Legs { get; } = [];
        private Vector3 _to;

        public void Begin(uint territoryId, Vector3 to, bool fly, bool land, float stopDistance = Traveller.PointStop)
        {
            _to = to;
            Legs.Add(to);
            Status = TravelStatus.Running;
        }

        public TravelStatus Tick()
        {
            if (Status != TravelStatus.Running) return Status;
            if (FailEveryLeg is { } kind)
            {
                FailReason = "territory 128 has no aetheryte this character has attuned";
                Failure = kind;
                return Status = TravelStatus.Failed;
            }
            if (Unreachable.Contains(_to.X))
            {
                FailReason = "stuck (12y short)";
                Failure = TravelFailure.Point;
                return Status = TravelStatus.Failed;
            }
            world.Position = _to;
            return Status = TravelStatus.Done;
        }

        public void Cancel() => Status = TravelStatus.Idle;
    }

    private static (HoleApproach Approach, World World, Travel Travel, int[] Saves) Make()
    {
        var world = new World();
        var travel = new Travel(world);
        var saves = new int[1];
        return (new HoleApproach(world, travel, () => saves[0]++), world, travel, saves);
    }

    private static HoleApproachState Run(HoleApproach approach, World world, int ticks = 60)
    {
        for (var i = 0; i < ticks && approach.Busy; i++)
        {
            approach.Tick();
            world.Advance(0.5);
        }
        return approach.State;
    }

    [Fact]
    public void The_most_used_point_is_tried_first_and_the_proof_is_reeled_in()
    {
        var (approach, world, travel, saves) = Make();
        world.Wet.Add(10);
        var memory = new HoleMemory();
        approach.Begin(Hole, [At(10, rotation: 2.5f, uses: 30), At(20, uses: 3)], memory);

        Assert.Equal(HoleApproachState.Ready, Run(approach, world));
        Assert.Equal(new Vector3(10, 0, 0), Assert.Single(travel.Legs));
        Assert.Equal(2.5f, world.Facing);          // faced the water the record was cast at
        Assert.False(world.IsFishing);            // reeled back in
        Assert.Equal(1, world.Quits);
        Assert.Single(memory.Good);
        Assert.True(saves[0] > 0);
    }

    [Fact]
    public void A_dry_point_is_remembered_and_the_next_is_tried()
    {
        var (approach, world, travel, _) = Make();
        world.Wet.Add(20);
        var memory = new HoleMemory();
        approach.Begin(Hole, [At(10, uses: 30), At(20, uses: 3)], memory);

        Assert.Equal(HoleApproachState.Ready, Run(approach, world));
        Assert.Equal(2, travel.Legs.Count);
        Assert.Equal(10f, Assert.Single(memory.Bad).X);
        Assert.Equal(20f, Assert.Single(memory.Good).X);
        Assert.Contains(world.Logged, m => m.Contains("did not take"));
    }

    [Fact]
    public void An_unreachable_point_is_skipped()
    {
        var (approach, world, travel, _) = Make();
        world.Wet.Add(20);
        travel.Unreachable.Add(10);
        var memory = new HoleMemory();
        approach.Begin(Hole, [At(10, uses: 30), At(20)], memory);

        Assert.Equal(HoleApproachState.Ready, Run(approach, world));
        Assert.Contains(world.Logged, m => m.Contains("could not be reached"));
        Assert.Equal(10f, Assert.Single(memory.Bad).X);
    }

    [Fact]
    public void No_way_into_the_zone_stops_at_once_and_holds_nothing_against_the_points()
    {
        // Limsa's Upper Decks, 2026-11-14: no crystal of its own, so every point "failed" and all
        // four were remembered as bad. A route failure is not the point's.
        var (approach, world, travel, saves) = Make();
        world.Wet.Add(10);
        travel.FailEveryLeg = TravelFailure.Route;
        var memory = new HoleMemory();
        approach.Begin(Hole, [At(10), At(20), At(30)], memory);

        Assert.Equal(HoleApproachState.Faulted, Run(approach, world));
        Assert.Single(travel.Legs);
        Assert.Empty(memory.Bad);
        Assert.Contains("could not get there", approach.FailReason);
    }

    [Fact]
    public void Next_time_the_proven_point_goes_first_and_the_bad_one_is_never_walked_to()
    {
        var memory = new HoleMemory();
        memory.Failed(At(10));
        memory.Worked(At(30));
        var order = memory.Order([At(10, uses: 30), At(20, uses: 5), At(30, uses: 1)]);
        Assert.Equal([30f, 20f], order.Select(s => s.X));
    }

    [Fact]
    public void Keep_fishing_leaves_the_line_in()
    {
        var (approach, world, _, _) = Make();
        world.Wet.Add(10);
        approach.KeepFishing = true;
        approach.Begin(Hole, [At(10)], new HoleMemory());

        Assert.Equal(HoleApproachState.Ready, Run(approach, world));
        Assert.True(world.IsFishing);
        Assert.Equal(0, world.Quits);
    }

    [Fact]
    public void Fisher_is_put_on_first()
    {
        var (approach, world, travel, _) = Make();
        world.CurrentClassJob = 16;
        world.Wet.Add(10);
        approach.Begin(Hole, [At(10)], new HoleMemory());

        Assert.Equal(HoleApproachState.Ready, Run(approach, world));
        Assert.Equal(HoleApproach.Fisher, world.CurrentClassJob);
    }

    [Fact]
    public void Without_a_Fisher_gearset_it_stops_before_travelling()
    {
        var (approach, world, travel, _) = Make();
        world.CurrentClassJob = 16;
        world.HasFisherGearset = false;
        approach.Begin(Hole, [At(10)], new HoleMemory());

        Assert.Equal(HoleApproachState.Faulted, Run(approach, world));
        Assert.Contains("Fisher", approach.FailReason);
        Assert.Empty(travel.Legs);
    }

    [Fact]
    public void Every_point_dry_gives_up_after_a_few_and_says_so()
    {
        var (approach, world, travel, _) = Make();
        approach.Begin(Hole, Enumerable.Range(1, 8).Select(i => At(i * 10)).ToList(), new HoleMemory());

        Assert.Equal(HoleApproachState.Faulted, Run(approach, world, ticks: 200));
        Assert.Equal(4, travel.Legs.Count);
        Assert.Contains("none of 4", approach.FailReason);
    }

    [Fact]
    public void A_hole_with_no_points_left_says_so_at_once()
    {
        var (approach, _, travel, _) = Make();
        var memory = new HoleMemory();
        memory.Failed(At(10));
        approach.Begin(Hole, [At(10)], memory);

        Assert.Equal(HoleApproachState.Faulted, approach.State);
        Assert.Contains("no stand point", approach.FailReason);
        Assert.Empty(travel.Legs);
    }

    [Fact]
    public void The_shipped_holes_have_stand_points_and_bite_times()
    {
        var file = Path.Combine(RepoRoot(), "Demeter", "Assets", FishingHoles.FileName);
        var holes = FishingHoles.Load(file);
        Assert.True(holes.Count > 300, $"only {holes.Count} holes");
        var zorgor = holes.ById(321);
        Assert.NotNull(zorgor);
        Assert.NotEmpty(zorgor!.Stands);
        Assert.True(zorgor.Stands[0].Uses >= zorgor.Stands[^1].Uses, "most used first");
        Assert.NotEmpty(zorgor.Bites);
    }

    private static string RepoRoot([System.Runtime.CompilerServices.CallerFilePath] string here = "")
        => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(here)!, ".."));
}
