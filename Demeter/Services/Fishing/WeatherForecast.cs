using System;
using System.Collections.Generic;

namespace Demeter.Services.Fishing;

/// <summary>
/// The weather of a zone at any moment, as the game rolls it.
///
/// <para>
/// Weather changes every eight Eorzea hours (0:00, 8:00, 16:00 — 1,400 real seconds), and which
/// weather comes is a roll from 0 to 99 that depends only on the time, read against the zone's
/// <c>WeatherRate</c> row (weathers with cumulative chances). The roll is the community's long-known
/// reading of the client (the same arithmetic every weather tracker uses); the rates are the game's
/// own sheet. Nothing here is a guess about the future — it is the same calculation the game makes.
/// </para>
/// </summary>
public static class WeatherForecast
{
    /// <summary>Real seconds in one weather period: eight Eorzea hours of 175 s.</summary>
    public const int PeriodSeconds = 8 * 175;

    /// <summary>Eorzea minutes in one weather period.</summary>
    public const int PeriodMinutes = 8 * 60;

    /// <summary>The 0–99 roll that picks the weather for the period containing this moment.</summary>
    public static int Target(DateTime utc)
    {
        var unix = (uint)Math.Floor((utc - DateTime.UnixEpoch).TotalSeconds);
        var bell = unix / 175;                       // Eorzea hours since the epoch
        var increment = (bell + 8 - bell % 8) % 24;  // which of the day's three periods
        var totalDays = unix / 4200;                 // Eorzea days since the epoch
        var calcBase = totalDays * 100 + increment;
        var step1 = (calcBase << 11) ^ calcBase;
        var step2 = (step1 >> 8) ^ step1;
        return (int)(step2 % 100);
    }

    /// <summary>The real time the weather period containing this moment began.</summary>
    public static DateTime PeriodStart(DateTime utc)
    {
        var seconds = (long)Math.Floor((utc - DateTime.UnixEpoch).TotalSeconds);
        return DateTime.UnixEpoch.AddSeconds(seconds - seconds % PeriodSeconds);
    }

    /// <summary>
    /// The sheet's chances (each weather's own share — Middle La Noscea is Clouds 20, Clear Skies 30,
    /// Fair Skies 20, …, summing to 100; checked against the game 2026-10-22) made cumulative, which
    /// is what <see cref="WeatherAt"/> reads the roll against. Empty slots are dropped.
    /// </summary>
    public static IReadOnlyList<(uint Weather, int Cumulative)> Cumulative(IEnumerable<(uint Weather, int Chance)> chances)
    {
        var result = new List<(uint, int)>();
        var sum = 0;
        foreach (var (weather, chance) in chances)
        {
            if (weather == 0 || chance <= 0)
                continue;
            sum += chance;
            result.Add((weather, sum));
        }
        return result;
    }

    /// <summary>
    /// The weather a zone has at this moment. <paramref name="rates"/> is the zone's WeatherRate row:
    /// (weather, cumulative chance) in sheet order, the last chance 100. Zero when it is empty.
    /// </summary>
    public static uint WeatherAt(IReadOnlyList<(uint Weather, int Cumulative)> rates, DateTime utc)
    {
        var target = Target(utc);
        foreach (var (weather, cumulative) in rates)
            if (target < cumulative)
                return weather;
        return rates.Count > 0 ? rates[^1].Weather : 0;
    }
}
