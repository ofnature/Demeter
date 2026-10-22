using System;
using System.Numerics;
using System.Threading.Tasks;

namespace Demeter.Services.Travel;

/// <summary>
/// What <see cref="Traveller"/> needs of the game and of the plugins that move the character —
/// Lifestream for the teleport into the zone, Ariadne for everything inside it. Decision-free:
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

    // ── Ariadne ──

    /// <summary>Ariadne is connected and has this zone's mesh.</summary>
    bool NavReady { get; }

    /// <summary>
    /// Hand Ariadne the point. A range above zero stops within it of the point itself. Flying, Ariadne
    /// calls the mount when on foot and keeps it at the end. False when Ariadne refused the request.
    /// </summary>
    bool MoveTo(Vector3 destination, bool fly, float range);

    /// <summary>A move is under way: pathfinding, a teleport leg, the mount being called, or following.</summary>
    bool IsMoving { get; }

    /// <summary>How the last move ended, in Ariadne's words ("goal reached", "stuck (4y short)", …).</summary>
    string MoveResult { get; }

    void StopMoving();

    /// <summary>The nearest point Ariadne's mesh reaches, asked off the frame.</summary>
    Task<Vector3?> NearestReachablePoint(Vector3 near, float within);

    // ── Lifestream ──

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
