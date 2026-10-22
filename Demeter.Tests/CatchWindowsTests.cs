using System.IO.Compression;
using System.Text;
using Demeter.Services.Fishing;

namespace Demeter.Tests;

public class CatchWindowsTests
{
    private const double SecondsPerEorzeaMinute = 175.0 / 60.0;

    /// <summary>A moment whose Eorzea time is exactly midnight (an Eorzea day is 4,200 real seconds).</summary>
    private static readonly DateTime EorzeaMidnight = DateTime.UnixEpoch.AddSeconds(4200L * 500_000);

    private static DateTime At(double eorzeaHour) => EorzeaMidnight.AddSeconds(eorzeaHour * 60 * SecondsPerEorzeaMinute);

    private static readonly IReadOnlyList<(uint, int)> OnlyClear = WeatherForecast.Cumulative([(1, 100)]);

    [Fact]
    public void Sheet_chances_are_made_cumulative()
    {
        var rates = WeatherForecast.Cumulative([(2, 20), (1, 30), (0, 0), (3, 50)]);
        Assert.Equal([(2u, 20), (1u, 50), (3u, 100)], rates);
    }

    [Fact]
    public void The_roll_is_0_to_99_and_constant_within_a_weather_period()
    {
        var start = WeatherForecast.PeriodStart(At(9));
        Assert.Equal(At(8), start);
        var roll = WeatherForecast.Target(start);
        Assert.InRange(roll, 0, 99);
        Assert.Equal(roll, WeatherForecast.Target(start.AddSeconds(WeatherForecast.PeriodSeconds - 1)));
    }

    [Fact]
    public void Weather_follows_the_roll_against_the_cumulative_rates()
    {
        // Two weathers split at 50: whichever the roll says, the answer must agree with it.
        var rates = WeatherForecast.Cumulative([(1, 50), (2, 50)]);
        for (var p = 0; p < 30; p++)
        {
            var t = At(8 * p);
            Assert.Equal(WeatherForecast.Target(t) < 50 ? 1u : 2u, WeatherForecast.WeatherAt(rates, t));
        }
    }

    [Fact]
    public void A_fish_with_no_conditions_is_always_up()
    {
        var window = Assert.Single(CatchWindows.Next(new FishInfo { Id = 1 }, [], At(3)));
        Assert.True(window.Contains(At(3)));
        Assert.Equal(DateTime.MaxValue, window.End);
    }

    [Fact]
    public void A_time_window_maps_to_the_right_real_time()
    {
        // Rock Lobster's shape: 17:00–22:00, any weather.
        var fish = new FishInfo { Id = 1, Time = [17 * 60, 22 * 60] };
        var first = CatchWindows.Next(fish, OnlyClear, At(3))[0];
        Assert.Equal(At(17), first.Start);
        Assert.Equal(At(22), first.End);
        Assert.Equal(TimeSpan.FromSeconds(5 * 175), first.Length);   // five Eorzea hours
    }

    [Fact]
    public void A_window_crossing_midnight_is_one_window()
    {
        var fish = new FishInfo { Id = 1, Time = [18 * 60, 6 * 60] };
        var w = CatchWindows.Next(fish, OnlyClear, At(12))[0];
        Assert.Equal(At(18), w.Start);
        Assert.Equal(At(24 + 6), w.End);
    }

    [Fact]
    public void A_window_already_open_is_reported_from_its_start()
    {
        var fish = new FishInfo { Id = 1, Time = [17 * 60, 22 * 60] };
        var w = CatchWindows.Next(fish, OnlyClear, At(19))[0];
        Assert.Equal(At(17), w.Start);
        Assert.True(w.Contains(At(19)));
    }

    [Fact]
    public void Weather_that_never_comes_gives_no_window()
    {
        var fish = new FishInfo { Id = 1, Weather = [9] };   // the zone only ever has weather 1
        Assert.Empty(CatchWindows.Next(fish, OnlyClear, At(0), horizon: TimeSpan.FromDays(2)));
    }

    [Fact]
    public void Weather_windows_agree_with_the_forecast_and_the_transition()
    {
        var rates = WeatherForecast.Cumulative([(1, 50), (2, 50)]);
        var fish = new FishInfo { Id = 1, Weather = [2], PreviousWeather = [1] };
        var windows = CatchWindows.Next(fish, rates, At(0), max: 5);
        Assert.NotEmpty(windows);
        foreach (var w in windows)
        {
            Assert.Equal(2u, WeatherForecast.WeatherAt(rates, w.Start));
            Assert.Equal(1u, WeatherForecast.WeatherAt(rates, w.Start.AddSeconds(-WeatherForecast.PeriodSeconds)));
            Assert.Equal(WeatherForecast.PeriodStart(w.Start), w.Start);   // opens on a period boundary
        }
    }

    [Fact]
    public void The_catalogue_reads_the_snapshot_shape()
    {
        const string json = """
            {"source":{"repo":"FFXIV-CombatReborn/GatherBuddyReborn","commit":"abc","license":"Apache-2.0","generated":"x"},
             "fish":[{"id":52005,"name":"Vagrant Keeper","patch":"TrailToTheHeavens","bait":43857,"path":[43857],
                      "weather":[3],"previousWeather":[6],"time":[360,480],"hookSet":"Powerful","bite":"Legendary",
                      "intuitionSeconds":300,"predators":[[43760,3]]}]}
            """;
        using var gz = new MemoryStream();
        using (var z = new GZipStream(gz, CompressionMode.Compress, leaveOpen: true))
            z.Write(Encoding.UTF8.GetBytes(json));
        gz.Position = 0;
        using var reader = new GZipStream(gz, CompressionMode.Decompress);

        var catalog = FishCatalog.Parse(reader);
        var fish = Assert.IsType<FishInfo>(catalog.ById(52005));
        Assert.Equal("Vagrant Keeper", fish.Name);
        Assert.Equal(new[] { 360, 480 }, fish.Time!);
        Assert.Equal(300, fish.IntuitionSeconds);
        Assert.Equal(new[] { 43760, 3 }, fish.Predators![0]);
        Assert.Equal("abc", catalog.Source!.Commit);
    }

    [Fact]
    public void The_shipped_snapshot_loads_and_knows_the_current_patch()
    {
        var file = Path.Combine(RepoRoot(), "Demeter", "Assets", FishCatalog.FileName);
        var catalog = FishCatalog.Load(Path.GetFullPath(file));
        Assert.True(catalog.Count > 2500, $"only {catalog.Count} fish");
        Assert.Equal("Vagrant Keeper", catalog.ById(52005)?.Name);
        Assert.Equal("Inktopus", catalog.ById(44336)?.Name);   // GatherBuddy's duplicate settled by the game's name
    }

    /// <summary>The repository root, found from this source file — the build output may be anywhere.</summary>
    private static string RepoRoot([System.Runtime.CompilerServices.CallerFilePath] string here = "")
        => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(here)!, ".."));
}
