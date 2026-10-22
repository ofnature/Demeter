using System.Numerics;
using Demeter.Services.Travel;

namespace Demeter.Tests;

public class TravellerTests
{
    private static readonly Vector3 Node = new(100, 0, 100);

    private static TravelStatus Run(Traveller travel, FakeTravelWorld world, int ticks = 120, double step = 0.5)
    {
        for (var i = 0; i < ticks && travel.Tick() == TravelStatus.Running; i++)
            world.Advance(step);
        return travel.Status;
    }

    [Fact]
    public void Another_zone_is_reached_by_teleporting_to_the_attuned_aetheryte_nearest_the_point()
    {
        var world = new FakeTravelWorld { TerritoryId = 129, ArriveOnMove = true };
        world.Attuned[1] = (1187, new Vector3(900, 0, 900));
        world.Attuned[2] = (1187, new Vector3(120, 0, 120));
        var travel = new Traveller(world);

        travel.Begin(1187, Node, fly: false, land: false);
        Assert.Equal(TravelStatus.Done, Run(travel, world));

        Assert.Contains("Teleport 2", world.Calls);
        Assert.DoesNotContain("Teleport 1", world.Calls);
        Assert.Equal(Node, world.PlayerPosition);
    }

    [Fact]
    public void A_zone_with_nothing_attuned_fails_and_says_so()
    {
        var world = new FakeTravelWorld { TerritoryId = 129 };
        var travel = new Traveller(world);

        travel.Begin(1187, Node, fly: true, land: true);
        Assert.Equal(TravelStatus.Failed, Run(travel, world));
        Assert.Contains("no aetheryte", travel.FailReason);
        Assert.DoesNotContain(world.Calls, c => c.StartsWith("Teleport", StringComparison.Ordinal));
    }

    [Fact]
    public void The_aetheryte_list_is_warmed_before_the_teleport_is_asked()
    {
        var world = new FakeTravelWorld { TerritoryId = 129, ArriveOnMove = true };
        world.Attuned[2] = (1187, new Vector3(120, 0, 120));
        var travel = new Traveller(world);

        travel.Begin(1187, Node, fly: false, land: false);
        Run(travel, world);
        Assert.True(world.Calls.IndexOf("RefreshAetherytes") < world.Calls.IndexOf("Teleport 2"));
    }

    [Fact]
    public void A_swallowed_teleport_is_asked_for_again()
    {
        // The gather runner switches job and then teleports; the cast falls into the action lock
        // and Lifestream's "yes" goes nowhere.
        var world = new FakeTravelWorld { TerritoryId = 129, ArriveOnTeleport = false, ArriveOnMove = true };
        world.Attuned[2] = (1187, new Vector3(120, 0, 120));
        var travel = new Traveller(world);

        travel.Begin(1187, Node, fly: false, land: false);
        for (var i = 0; i < 20 && world.Calls.Count(c => c == "Teleport 2") < 2; i++) { travel.Tick(); world.Advance(0.5); }

        Assert.Equal(2, world.Calls.Count(c => c == "Teleport 2"));
        Assert.Contains(world.Logged, m => m.Contains("never started"));
    }

    [Fact]
    public void A_long_leg_mounts_and_flies_where_the_currents_are_done()
    {
        var world = new FakeTravelWorld { CanFlyHere = true, ArriveOnMove = true };
        var travel = new Traveller(world);

        travel.Begin(1187, Node, fly: true, land: false);
        Assert.Equal(TravelStatus.Done, Run(travel, world));

        Assert.Contains("Mount", world.Calls);
        Assert.True(world.LastMoveFly);
    }

    [Fact]
    public void Without_currents_the_leg_is_ridden_on_the_ground()
    {
        var world = new FakeTravelWorld { CanFlyHere = false, ArriveOnMove = true };
        var travel = new Traveller(world);

        travel.Begin(1187, Node, fly: true, land: false);
        Assert.Equal(TravelStatus.Done, Run(travel, world));
        Assert.False(world.LastMoveFly);
    }

    [Fact]
    public void A_short_leg_is_walked_without_mounting()
    {
        var world = new FakeTravelWorld { ArriveOnMove = true, PlayerPosition = Node + new Vector3(10, 0, 0) };
        var travel = new Traveller(world);

        travel.Begin(1187, Node, fly: true, land: false);
        Assert.Equal(TravelStatus.Done, Run(travel, world));
        Assert.DoesNotContain("Mount", world.Calls);
    }

    [Fact]
    public void Landing_waits_for_both_feet_on_the_ground()
    {
        var world = new FakeTravelWorld { CanFlyHere = true, ArriveOnMove = true, HoldsMount = true };
        var travel = new Traveller(world);
        travel.Begin(1187, Node, fly: true, land: true);

        for (var i = 0; i < 20; i++) { travel.Tick(); world.Advance(0.5); }
        Assert.Equal(TravelStatus.Running, travel.Status);   // still coming down
        Assert.Contains("Dismount", world.Calls);

        world.IsMounted = false;
        world.IsInFlight = false;
        Assert.Equal(TravelStatus.Done, Run(travel, world, ticks: 6));
    }

    [Fact]
    public void A_descent_that_never_lands_is_rerouted_over_a_floor_beside_the_point()
    {
        var floor = Node + new Vector3(4, 0, 0);
        // Hovering three yalms over the point, against a ledge: the dismount never touches down.
        var world = new FakeTravelWorld
        {
            CanFlyHere = true, HoldsMount = true, IsMounted = true, IsInFlight = true,
            PlayerPosition = Node + new Vector3(0, 3, 0),
            NearestReachableFn = (_, _) => floor,
        };
        var travel = new Traveller(world);
        travel.Begin(1187, Node, fly: true, land: true);

        for (var i = 0; i < 30 && !world.Logged.Any(m => m.Contains("not landing")); i++) { travel.Tick(); world.Advance(0.5); }

        Assert.Contains(world.Logged, m => m.Contains("not landing"));
        for (var i = 0; i < 4 && world.LastMoveTarget != floor; i++) { travel.Tick(); world.Advance(0.5); }
        Assert.Equal(floor, world.LastMoveTarget);
        Assert.True(world.LastMoveFly);
    }

    [Fact]
    public void A_fight_on_the_way_holds_the_leg_and_gets_off_the_mount()
    {
        var world = new FakeTravelWorld { CanFlyHere = true };
        var travel = new Traveller(world);
        travel.Begin(1187, Node, fly: true, land: false);
        for (var i = 0; i < 6; i++) { travel.Tick(); world.Advance(0.5); }
        Assert.True(world.IsMounted);

        world.InCombat = true;
        for (var i = 0; i < 400; i++) { travel.Tick(); world.Advance(0.5); }   // long past every move clock
        Assert.Equal(TravelStatus.Running, travel.Status);
        Assert.Contains("Dismount", world.Calls);
        Assert.False(world.IsMounted);

        // Over: back in the saddle and on to the point.
        world.InCombat = false;
        world.ArriveOnMove = true;
        Assert.Equal(TravelStatus.Done, Run(travel, world));
        Assert.True(world.IsMounted);
        Assert.Contains(world.Logged, m => m.Contains("mounting up"));
    }

    [Fact]
    public void With_Daedalus_switched_off_nobody_fights_so_nothing_holds()
    {
        var world = new FakeTravelWorld { InCombat = true, DaedalusDisabledByUser = true, ArriveOnMove = true };
        var travel = new Traveller(world);

        travel.Begin(1187, Node, fly: false, land: false);
        Assert.Equal(TravelStatus.Done, Run(travel, world));
    }

    [Fact]
    public void A_point_the_mesh_will_not_reach_is_finished_on_foot_when_close()
    {
        var near = Node + new Vector3(8, 0, 0);
        var world = new FakeTravelWorld { PlayerPosition = near, PathWaypointCount = 0 };
        var travel = new Traveller(world);

        travel.Begin(1187, Node, fly: false, land: false);
        for (var i = 0; i < 20 && !world.Calls.Any(c => c.StartsWith("MoveDirect", StringComparison.Ordinal)); i++) { travel.Tick(); world.Advance(0.5); }

        Assert.Contains(world.Calls, c => c.StartsWith("MoveDirect", StringComparison.Ordinal));
    }

    [Fact]
    public void No_route_anywhere_fails_with_the_reason()
    {
        var world = new FakeTravelWorld { PathWaypointCount = 0, NearestReachableFn = (_, _) => null };
        var travel = new Traveller(world);

        travel.Begin(1187, Node, fly: false, land: false);
        Assert.Equal(TravelStatus.Failed, Run(travel, world));
        Assert.Contains("no path", travel.FailReason);
    }

    [Fact]
    public void Cancel_stops_the_walk_and_any_hop()
    {
        var world = new FakeTravelWorld();
        var travel = new Traveller(world);
        travel.Begin(1187, Node, fly: false, land: false);
        for (var i = 0; i < 6; i++) { travel.Tick(); world.Advance(0.5); }

        travel.Cancel();
        Assert.Equal(TravelStatus.Idle, travel.Status);
        Assert.Contains("Stop", world.Calls);
        Assert.Contains("AbortTravel", world.Calls);
    }
}
