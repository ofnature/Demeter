using System.Numerics;
using System.Text.Json;
using Demeter.Services.Fishing;
using Demeter.Services.Travel;
using Travel = Demeter.Tests.HoleApproachTests.Travel;
using World = Demeter.Tests.HoleApproachTests.World;

namespace Demeter.Tests;

public class FishStandTests
{
    private const uint Zone = 1237;

    private static (FishStand Stand, World World, Travel Travel) Make()
    {
        var world = new World();
        var travel = new Travel(world);
        return (new FishStand(world, travel, () => Zone, () => world.Position), world, travel);
    }

    private static FishStandRequest Ask(params (float X, float Facing)[] points) => new()
    {
        RequestedBy = "Selene",
        Points = [.. points.Select(p => new StandPointRequest { X = p.X, Y = 0, Z = 0, Facing = p.Facing })],
    };

    private static FishStandState Run(FishStand stand, World world, int ticks = 60)
    {
        for (var i = 0; i < ticks && stand.Busy; i++)
        {
            stand.Tick();
            world.Advance(0.25);
        }
        return stand.State;
    }

    [Fact]
    public void It_walks_there_in_this_zone_faces_the_water_and_casts_nothing()
    {
        var (stand, world, travel) = Make();
        Assert.True(stand.Begin(Ask((10, 2.5f))));
        Assert.Equal(FishStandState.Stood, Run(stand, world));

        Assert.Equal(new Vector3(10, 0, 0), world.Position);
        Assert.Equal(2.5f, world.Facing);
        Assert.Equal(0, world.Casts);
        Assert.Equal((Zone, false, false), travel.Asked.Single());   // 10 y: walked, never a teleport
        Assert.Equal(1, stand.Point);
    }

    [Fact]
    public void A_far_point_may_be_flown_to_but_still_never_teleported_to()
    {
        var (stand, world, travel) = Make();
        stand.Begin(Ask((200, 0)));
        Run(stand, world);
        Assert.Equal((Zone, true, false), travel.Asked.Single());
    }

    [Fact]
    public void A_point_that_cannot_be_reached_gives_way_to_the_next()
    {
        var (stand, world, travel) = Make();
        travel.Unreachable.Add(10);
        stand.Begin(Ask((10, 1), (12, 3)));
        Assert.Equal(FishStandState.Stood, Run(stand, world));
        Assert.Equal(12, world.Position.X);
        Assert.Equal(3f, world.Facing);
        Assert.Equal(2, stand.Point);
    }

    [Fact]
    public void When_no_point_can_be_reached_it_says_so()
    {
        var (stand, world, travel) = Make();
        travel.Unreachable.Add(10);
        travel.Unreachable.Add(12);
        stand.Begin(Ask((10, 1), (12, 3)));
        Assert.Equal(FishStandState.Faulted, Run(stand, world));
        Assert.Equal(FishSessionCode.CannotReach, stand.Code);
    }

    [Fact]
    public void It_waits_to_be_off_the_mount_before_it_says_it_stands()
    {
        var (stand, world, _) = Make();
        world.IsMounted = true;
        stand.Begin(Ask((10, 1)));
        Assert.Equal(FishStandState.Facing, Run(stand, world, ticks: 20));
        world.IsMounted = false;
        Assert.Equal(FishStandState.Stood, Run(stand, world));
    }

    [Fact]
    public void It_refuses_without_Fisher_or_without_a_point()
    {
        var (stand, world, _) = Make();
        world.CurrentClassJob = 16;
        Assert.False(stand.Begin(Ask((10, 1))));
        Assert.Equal(FishSessionCode.NotFisher, stand.Code);

        var (empty, _, _) = Make();
        Assert.False(empty.Begin(Ask()));
        Assert.Equal(FishSessionCode.BadRequest, empty.Code);
    }

    [Fact]
    public void Through_the_gate_the_status_speaks_of_the_stand_until_fishing_starts()
    {
        var (stand, world, _) = Make();
        var pond = new FishingRunTests.Pond { CurrentBait = 52247 };
        pond.Bag[52247] = 10;
        var session = new FishSession(pond, () => true, new FishCatalog([]), id => $"item {id}", _ => true, new FishScores(), () => 0, () => 0);
        var gate = new FishGateway(session, stand, () => true, () => false, _ => { });

        Assert.True(gate.StandAt("{\"requestedBy\":\"Selene\",\"points\":[{\"x\":10,\"y\":0,\"z\":0,\"facing\":1.5}]}"));
        Assert.False(gate.Start("{}"));   // not while walking
        Assert.Equal("standing", Status(gate).GetProperty("state").GetString());

        Run(stand, world);
        var stood = Status(gate);
        Assert.Equal("stood", stood.GetProperty("state").GetString());
        Assert.Equal(1, stood.GetProperty("standPoint").GetInt32());

        Assert.True(gate.Start("{\"castBudget\":1}"));
        Assert.Equal("running", Status(gate).GetProperty("state").GetString());
    }

    private static JsonElement Status(FishGateway gate) => JsonDocument.Parse(gate.StatusJson()).RootElement;
}
