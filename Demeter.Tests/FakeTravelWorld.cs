using System.Numerics;
using Demeter.Services.Gathering;
using Demeter.Services.Travel;

namespace Demeter.Tests;

/// <summary>
/// The game as <see cref="Traveller"/> sees it, scripted: Lifestream teleports and Ariadne moves.
/// A move either arrives at once (<see cref="ArriveOnMove"/>), ending with <see cref="ResultOnArrive"/>,
/// or stays running until the test ends it with <see cref="FinishMove"/>.
/// </summary>
public sealed class FakeTravelWorld : ITravelWorld
{
    public DateTime UtcNow { get; set; } = new(2026, 8, 21, 12, 0, 0, DateTimeKind.Utc);
    public uint TerritoryId { get; set; } = 1187;
    public Vector3 PlayerPosition { get; set; }
    public bool IsReady { get; set; } = true;
    public bool IsOccupied { get; set; }
    public bool IsTravelBusy { get; set; }
    public bool InCombat { get; set; }
    public bool IsDead { get; set; }
    public bool DaedalusDisabledByUser { get; set; }
    public bool IsMounted { get; set; }
    public bool IsInFlight { get; set; }
    public bool CanMountHere { get; set; } = true;
    public bool CanFlyHere { get; set; }
    public bool NavReady { get; set; } = true;
    public bool IsMoving { get; set; }
    public string MoveResult { get; set; } = string.Empty;

    public List<string> Calls { get; } = [];
    public List<string> Logged { get; } = [];

    /// <summary>A move puts the player at the destination at once.</summary>
    public bool ArriveOnMove { get; set; }

    /// <summary>What Ariadne says when an arriving move ends.</summary>
    public string ResultOnArrive { get; set; } = "goal reached";

    /// <summary>Ariadne refuses moves (busy, no player).</summary>
    public bool RefuseMoves { get; set; }

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
    public float? LastMoveRange { get; private set; }
    public int MoveCount { get; private set; }

    public void Advance(double seconds) => UtcNow = UtcNow.AddSeconds(seconds);

    /// <summary>End a running move the way Ariadne would, optionally somewhere else.</summary>
    public void FinishMove(string result, Vector3? at = null)
    {
        IsMoving = false;
        MoveResult = result;
        if (at is { } p) PlayerPosition = p;
    }

    public void Mount() { Calls.Add("Mount"); IsMounted = true; }

    public void Dismount()
    {
        Calls.Add("Dismount");
        if (!HoldsMount) { IsMounted = false; IsInFlight = false; }
    }

    public bool MoveTo(Vector3 destination, bool fly, float range)
    {
        Calls.Add($"Move {destination.X:F0},{destination.Y:F0},{destination.Z:F0} fly={fly}");
        if (RefuseMoves) return false;
        LastMoveTarget = destination;
        LastMoveFly = fly;
        LastMoveRange = range;
        MoveCount++;
        if (fly && !IsMounted) IsMounted = true;   // Ariadne calls the mount for a flight
        if (ArriveOnMove)
        {
            PlayerPosition = destination;
            IsMoving = false;
            MoveResult = ResultOnArrive;
        }
        else
        {
            IsMoving = true;
            MoveResult = "pathfinding…";
        }
        return true;
    }

    public void StopMoving() { Calls.Add("Stop"); IsMoving = false; }

    public Task<Vector3?> NearestReachablePoint(Vector3 near, float within)
        => Task.FromResult(NearestReachableFn is { } f ? f(near, within) : near);

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
