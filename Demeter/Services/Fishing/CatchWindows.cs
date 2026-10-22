using System;
using System.Collections.Generic;

namespace Demeter.Services.Fishing;

/// <summary>A stretch of real time in which a fish can bite.</summary>
public readonly record struct CatchWindow(DateTime Start, DateTime End)
{
    public TimeSpan Length => End - Start;
    public bool Contains(DateTime utc) => utc >= Start && utc < End;
}

/// <summary>
/// When can this fish next be caught, at a spot in a zone with these weather rates.
///
/// <para>
/// Three conditions, each read from the data: the weather now is one the fish wants, the weather of
/// the period before is one it wants (the "transition"), and the Eorzea time is inside its window.
/// Weather moves in 8-hour Eorzea periods, so the search walks period by period: a period whose
/// weather (and whose predecessor's) qualifies contributes the part of it the time window covers,
/// and touching pieces are joined into one window. A fish with no conditions is always up.
/// </para>
///
/// <para>
/// What this does not judge: Fisher's Intuition (predators first), the lure, the bait in the bag.
/// Those are the fisher's preconditions, not the fish's window.
/// </para>
/// </summary>
public static class CatchWindows
{
    /// <summary>How far ahead to look — the rarest fish open a few times a month.</summary>
    public static readonly TimeSpan DefaultHorizon = TimeSpan.FromDays(60);

    private const double RealSecondsPerEorzeaMinute = 175.0 / 60.0;
    private const int MinutesPerDay = 24 * 60;

    /// <summary>
    /// The windows starting from <paramref name="from"/> (one already open is returned from its real
    /// start), at most <paramref name="max"/> of them, within the horizon.
    /// </summary>
    public static IReadOnlyList<CatchWindow> Next(FishInfo fish, IReadOnlyList<(uint Weather, int Cumulative)> rates,
        DateTime from, int max = 3, TimeSpan? horizon = null)
    {
        var result = new List<CatchWindow>();
        if (max <= 0)
            return result;

        var weather = fish.Weather is { Count: > 0 } w ? new HashSet<uint>(w) : null;
        var previous = fish.PreviousWeather is { Count: > 0 } p ? new HashSet<uint>(p) : null;

        // Always up: nothing to wait for.
        if (weather is null && previous is null && fish.Time is null)
        {
            result.Add(new CatchWindow(from, DateTime.MaxValue));
            return result;
        }
        // A weather condition with no rates to read it against can never be judged — say never.
        if ((weather is not null || previous is not null) && rates.Count == 0)
            return result;

        var end = from + (horizon ?? DefaultHorizon);
        // Start one period back, so a window already open is reported from its real start.
        var period = WeatherForecast.PeriodStart(from).AddSeconds(-WeatherForecast.PeriodSeconds);
        DateTime? openStart = null;
        DateTime openEnd = default;

        while (period < end && result.Count < max)
        {
            var next = period.AddSeconds(WeatherForecast.PeriodSeconds);
            var ok = (weather is null || weather.Contains(WeatherForecast.WeatherAt(rates, period)))
                     && (previous is null || previous.Contains(WeatherForecast.WeatherAt(rates, period.AddSeconds(-WeatherForecast.PeriodSeconds))));
            if (ok)
            {
                foreach (var (s, e) in TimeSlices(fish.Time, period))
                {
                    if (openStart is not null && s <= openEnd)
                    {
                        openEnd = e > openEnd ? e : openEnd; // touches the open window: extend it
                        continue;
                    }
                    if (openStart is { } done && openEnd > from)
                        result.Add(new CatchWindow(done, openEnd));
                    openStart = s;
                    openEnd = e;
                }
            }
            else if (openStart is { } done)
            {
                if (openEnd > from)
                    result.Add(new CatchWindow(done, openEnd));
                openStart = null;
            }
            period = next;
        }
        if (openStart is { } last && openEnd > from && result.Count < max)
            result.Add(new CatchWindow(last, openEnd));
        return result;
    }

    /// <summary>The parts of one weather period the fish's time window covers, in real time.</summary>
    private static IEnumerable<(DateTime Start, DateTime End)> TimeSlices(int[]? time, DateTime periodStart)
    {
        var periodEnd = periodStart.AddSeconds(WeatherForecast.PeriodSeconds);
        if (time is null)
        {
            yield return (periodStart, periodEnd);
            yield break;
        }

        // The period's own span in Eorzea minutes of the day: 0–480, 480–960 or 960–1440.
        var startMinute = (int)Math.Round(MinuteOfDay(periodStart));
        var spans = time[1] > time[0]
            ? new[] { (time[0], time[1]) }
            : new[] { (time[0], MinutesPerDay), (0, time[1]) }; // wraps past midnight

        foreach (var (a, b) in spans)
        {
            var lo = Math.Max(a, startMinute);
            var hi = Math.Min(b, startMinute + WeatherForecast.PeriodMinutes);
            if (hi <= lo)
                continue;
            yield return (periodStart.AddSeconds((lo - startMinute) * RealSecondsPerEorzeaMinute),
                periodStart.AddSeconds((hi - startMinute) * RealSecondsPerEorzeaMinute));
        }
    }

    private static double MinuteOfDay(DateTime utc)
    {
        var minutes = (utc - DateTime.UnixEpoch).TotalSeconds / RealSecondsPerEorzeaMinute;
        return minutes % MinutesPerDay;
    }
}
