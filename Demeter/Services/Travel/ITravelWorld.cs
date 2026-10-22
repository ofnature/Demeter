using System;
using System.Numerics;

namespace Demeter.Services.Travel;

/// <summary>
/// What <see cref="Traveller"/> needs of the game and of the plugins that move the character —
/// vnavmesh or Ariadne for the walk and the flight, Lifestream for the teleport. Decision-free:
/// "do the thing" or "report the fact", so every judgement stays in <see cref="Traveller"/>, where a
/// fake can test it.
/// </summary>
public interface ITravelWorld
{
    DateTime UtcNow { get; }
    uint TerritoryId { get; }
    Vector3 PlayerPosition { get; }

    /// <summary>Logged in, not between areas, not in a cutscene, conversation or cast.</summary>
    bool IsReady { get; }

    /// <summary>A cutscene, a conversation or a menu owns the character.</summary>
    bool IsOccupied { get; }

    /// <summary>A teleport cast, a zone load, or Lifestream at work.</summary>
    bool IsTravelBusy { get; }
    bool IsCasting { get; }
    bool InCombat { get; }
    bool IsDead { get; }

    /// <summary>The user switched Daedalus off: nobody will fight, so travel does not wait for a fight.</summary>
    bool DaedalusDisabledByUser { get; }

    bool IsMounted { get; }

    /// <summary>Mounted and airborne — a descent in progress still counts.</summary>
    bool IsInFlight { get; }

    /// <summary><c>TerritoryType.Mount</c>: false in every city.</summary>
    bool CanMountHere { get; }

    /// <summary>The zone's aether currents are all attuned.</summary>
    bool CanFlyHere { get; }

    /// <summary>Mount roulette.</summary>
    void Mount();
    void Dismount();
    bool Sprint();
    void Jump();

    // ── pathing ──
    bool NavmeshReady { get; }

    /// <summary>0..1 while the mesh builds, negative when it is not building.</summary>
    float NavmeshBuildProgress { get; }

    /// <summary>The pathing plugin is planning or following a path.</summary>
    bool IsMoving { get; }
    bool IsPathfinding { get; }

    /// <summary>Waypoints in the last path; zero after a pathfind reads as "no route".</summary>
    int PathWaypointCount { get; }
    bool MoveTo(Vector3 destination, bool fly);
    bool MoveCloseTo(Vector3 destination, float tolerance, bool fly);
    bool MoveDirectTo(Vector3 destination, bool fly);
    void StopMoving();
    Vector3? NearestReachablePoint(Vector3 near, float within);

    // ── teleport ──

    /// <summary>The attuned aetheryte in that territory closest to a point, or null when none is attuned.</summary>
    uint? NearestAttunedAetheryte(uint territoryId, Vector3 near);
    Vector3? AetherytePosition(uint aetheryteId);

    /// <summary>The game's aetheryte list fills a frame or two after it is asked to.</summary>
    void RefreshAetheryteList();
    bool Teleport(uint aetheryteId);

    /// <summary>Stop a Lifestream hop under way.</summary>
    void AbortTravel();

    void Log(string message);
}
