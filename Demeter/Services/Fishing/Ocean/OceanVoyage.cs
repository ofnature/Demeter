using System;

namespace Demeter.Services.Fishing.Ocean;

/// <summary>Which boat: the Indigo route leaves from Limsa Lominsa, the Ruby route from Kugane.</summary>
public enum OceanRoute
{
    Indigo = 0,
    Ruby = 1,
}

/// <summary>Where a voyage is, as the game's own ocean fishing director reports it.</summary>
public enum VoyageStatus
{
    WaitingForPlayers,
    SwitchingZone,
    Fishing,
    NewZone,
    Finished,
}

/// <summary>
/// One read of the voyage under way. <see cref="Zone"/> counts from 0 over the route's three
/// stops; the route and zone names come from the IKDRoute and IKDSpot sheets.
/// </summary>
public sealed record VoyageReading(
    uint RouteId, string RouteName, int Zone, string ZoneName,
    VoyageStatus Status, bool Spectral, uint NormalSpot, uint SpectralSpot);

/// <summary>The voyage under way, or null when the character is not on one.</summary>
public interface IOceanVoyageSource
{
    VoyageReading? Read();
}

/// <summary>When the boats go. Pure, so it is pinned by tests rather than by waiting for one.</summary>
public static class OceanSchedule
{
    /// <summary>
    /// A boat leaves every two hours, on the even UTC hour, and sign-up for it is open from the hour
    /// for a quarter of an hour. (Henchman's On A Boat waits for the same window.)
    /// </summary>
    public static readonly TimeSpan SignUp = TimeSpan.FromMinutes(15);

    /// <summary>The routes the Indigo boat runs: IKDRoute rows 1–12. 13 onward leave from Kugane.</summary>
    public static OceanRoute RouteOf(uint routeId) => routeId is >= 1 and <= 12 ? OceanRoute.Indigo : OceanRoute.Ruby;

    public static bool SignUpOpen(DateTime utc) => utc.Hour % 2 == 0 && utc.TimeOfDay - TimeSpan.FromHours(utc.Hour) < SignUp;

    /// <summary>The hour whose sign-up is open now, or the next one to open.</summary>
    public static DateTime NextSignUp(DateTime utc)
    {
        var hour = new DateTime(utc.Year, utc.Month, utc.Day, utc.Hour - utc.Hour % 2, 0, 0, DateTimeKind.Utc);
        return utc - hour < SignUp ? hour : hour.AddHours(2);
    }
}
