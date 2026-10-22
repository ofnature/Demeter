using System.Numerics;
using Demeter.Services.Gathering;
using Demeter.Services.Travel;

namespace Demeter.Tests;

/// <summary>
/// The game as <see cref="Traveller"/> sees it, scripted. Replaces Odysseus's <c>FakeStepWorld</c>
/// for the tests that came over with the gatherer: the same switches (arrive on move, arrive on
/// teleport, hold the mount while coming down) on the much smaller surface travel needs.
/// </summary>
public sealed class FakeTravelWorld : ITravelWorld
{
    public DateTime UtcNow { get; set; } = new(2026, 8, 21, 12, 0, 0, DateTimeKind.Utc);
    public uint TerritoryId { get; set; } = 1187;
    public Vector3 PlayerPosition { get; set; }
    public bool IsReady { get; set; } = true;
    public bool IsOccupied { get; set; }
    public bool IsTravelBusy { get; set; }
    public bool IsCasting { get; set; }
    public bool InCombat { get; set; }
    public bool IsDead { get; set; }
    public bool DaedalusDisabledByUser { get; set; }
    public bool IsMounted { get; set; }
    public bool IsInFlight { get; set; }
    public bool CanMountHere { get; set; } = true;
    public bool CanFlyHere { get; set; }
    public bool NavmeshReady { get; set; } = true;
    public float NavmeshBuildProgress { get; set; } = -1f;
    public bool IsMoving { get; set; }
    public bool IsPathfinding { get; set; }
    public int PathWaypointCount { get; set; } = 5;

    public List<string> Calls { get; } = [];
    public List<string> Logged { get; } = [];

    /// <summary>A move puts the player at the destination at once.</summary>
    public bool ArriveOnMove { get; set; }

    /// <summary>A teleport lands the player in the aetheryte's territory at once.</summary>
    public bool ArriveOnTeleport { get; set; } = true;
    public bool TeleportAccepted { get; set; } = true;

    /// <summary>True models a dismount from the air: asked for, but still coming down.</summary>
    public bool HoldsMount { get; set; }

    /// <summary>Attuned aetherytes: id → (territory, position).</summary>
    public Dictionary<uint, (uint Territory, Vector3 At)> Attuned { get; } = new();

    public Func<Vector3, float, Vector3?>? NearestReachableFn { get; set; }
    public Vector3? LastMoveTarget { get; private set; }
    public bool? LastMoveFly { get; private set; }

    public void Advance(double seconds) => UtcNow = UtcNow.AddSeconds(seconds);

    public void Mount() { Calls.Add("Mount"); IsMounted = true; }

    public void Dismount()
    {
        Calls.Add("Dismount");
        if (!HoldsMount) { IsMounted = false; IsInFlight = false; }
    }

    public bool Sprint() { Calls.Add("Sprint"); return true; }
    public void Jump() => Calls.Add("Jump");

    public bool MoveTo(Vector3 destination, bool fly) => Move(destination, fly);
    public bool MoveCloseTo(Vector3 destination, float tolerance, bool fly) => Move(destination, fly);
    public bool MoveDirectTo(Vector3 destination, bool fly) => Move(destination, fly, direct: true);

    private bool Move(Vector3 destination, bool fly, bool direct = false)
    {
        Calls.Add($"Move{(direct ? "Direct" : "")} {destination.X:F0},{destination.Y:F0},{destination.Z:F0} fly={fly}");
        LastMoveTarget = destination;
        LastMoveFly = fly;
        // A pathfind that finds nothing leaves you standing still — that is what zero waypoints means.
        IsMoving = PathWaypointCount != 0;
        if (ArriveOnMove) { PlayerPosition = destination; IsMoving = false; }
        return true;
    }

    public void StopMoving() { Calls.Add("Stop"); IsMoving = false; }

    public Vector3? NearestReachablePoint(Vector3 near, float within) => NearestReachableFn is { } f ? f(near, within) : near;

    public uint? NearestAttunedAetheryte(uint territoryId, Vector3 near)
        => Attuned.Where(a => a.Value.Territory == territoryId)
            .OrderBy(a => Vector3.Distance(a.Value.At, near))
            .Select(a => (uint?)a.Key)
            .FirstOrDefault();

    public Vector3? AetherytePosition(uint aetheryteId) => Attuned.TryGetValue(aetheryteId, out var a) ? a.At : null;

    public void RefreshAetheryteList() => Calls.Add("RefreshAetherytes");

    public bool Teleport(uint aetheryteId)
    {
        Calls.Add($"Teleport {aetheryteId}");
        if (!TeleportAccepted) return false;
        if (ArriveOnTeleport && Attuned.TryGetValue(aetheryteId, out var a))
        {
            TerritoryId = a.Territory;
            PlayerPosition = a.At;
        }
        return true;
    }

    public void AbortTravel() => Calls.Add("AbortTravel");
    public void Log(string message) => Logged.Add(message);
}

/// <summary>Self-repair's view of the game, scripted.</summary>
public sealed class FakeRepairWorld : IRepairWorld
{
    public DateTime UtcNow { get; set; } = new(2026, 8, 21, 12, 0, 0, DateTimeKind.Utc);
    public int LowestGearConditionPercent { get; set; } = 100;
    public bool RepairWindowOpens { get; set; } = true;
    public bool RepairAllMends { get; set; } = true;
    public HashSet<string> VisibleAddons { get; } = [];
    public List<string> Calls { get; } = [];

    public void Advance(double seconds) => UtcNow = UtcNow.AddSeconds(seconds);

    public void OpenRepairWindow() { Calls.Add("OpenRepair"); if (RepairWindowOpens) VisibleAddons.Add("Repair"); }

    public bool PressRepairAll()
    {
        if (!VisibleAddons.Contains("Repair")) return false;
        Calls.Add("RepairAll");
        VisibleAddons.Add("SelectYesno");   // the game asks before spending dark matter
        if (RepairAllMends) LowestGearConditionPercent = 100;
        return true;
    }

    public void CloseRepairWindow() { Calls.Add("CloseRepair"); VisibleAddons.Remove("Repair"); }
    public bool IsAddonVisible(string name) => VisibleAddons.Contains(name);

    public void SelectYesNo(bool yes)
    {
        Calls.Add($"YesNo {yes}");
        VisibleAddons.Remove("SelectYesno");
    }
}
