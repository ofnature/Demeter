using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text.Json;

namespace Demeter.Services.Fishing;

/// <summary>
/// One fish as Demeter ships it: GatherBuddy Reborn's catch data (Apache-2.0), converted and checked
/// against the game's sheets by <c>Tools/FishImport</c>. Field names are the snapshot's. Ids are game
/// ids: items for fish and bait, Weather rows for weather.
/// </summary>
public sealed class FishInfo
{
    public uint Id { get; set; }
    public string? Name { get; set; }
    public string Patch { get; set; } = string.Empty;

    /// <summary>The bait the road starts with, when GatherBuddy names one.</summary>
    public uint? Bait { get; set; }

    /// <summary>The fish mooched from, in order, ending with the one that bites for this fish.</summary>
    public List<uint>? Mooch { get; set; }

    /// <summary>The whole road from the tackle box: the bait, then each fish mooched.</summary>
    public List<uint>? Path { get; set; }

    /// <summary>Any of these weathers now; empty or null is any weather.</summary>
    public List<uint>? Weather { get; set; }

    /// <summary>Any of these weathers in the period before; empty or null is any.</summary>
    public List<uint>? PreviousWeather { get; set; }

    /// <summary>Eorzea minutes of the day, start and end; null is all day. End may be before start (wraps).</summary>
    public int[]? Time { get; set; }

    /// <summary>"Precise", "Powerful", "Stellar", … as GatherBuddy names them.</summary>
    public string? HookSet { get; set; }

    /// <summary>"Weak", "Strong", "Legendary" — the tug.</summary>
    public string? Bite { get; set; }
    public string? Snagging { get; set; }
    public string? Lure { get; set; }
    public int? IntuitionSeconds { get; set; }

    /// <summary>(fish id, count) pairs to catch first for Fisher's Intuition.</summary>
    public List<int[]>? Predators { get; set; }
    public int[]? MultiHook { get; set; }
    public string? SpearSize { get; set; }
    public string? SpearSpeed { get; set; }
    public List<string>? Ocean { get; set; }
    public string? OceanSpecies { get; set; }
    public bool? ForceBig { get; set; }
    public bool? Legendary { get; set; }
    public uint? SurfaceSlap { get; set; }
    public int? Points { get; set; }
    public int? CosmicMission { get; set; }
    public string? Guide { get; set; }

    public bool IsSpearfish => SpearSize is not null;
    public bool IsOcean => Ocean is { Count: > 0 } || OceanSpecies is not null;
    public bool IsTimed => Time is not null;
    public bool NeedsWeather => Weather is { Count: > 0 } || PreviousWeather is { Count: > 0 };
}

/// <summary>Where the snapshot came from — carried into the window so its age is never a mystery.</summary>
public sealed class FishSource
{
    public string Repo { get; set; } = string.Empty;
    public string Commit { get; set; } = string.Empty;
    public string License { get; set; } = string.Empty;
    public string Generated { get; set; } = string.Empty;
}

/// <summary>The shipped fish data: <c>Assets/fish-gbr.json.gz</c> beside the DLL.</summary>
public sealed class FishCatalog
{
    public const string FileName = "fish-gbr.json.gz";

    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };

    private readonly Dictionary<uint, FishInfo> _byId = new();

    public FishCatalog(IEnumerable<FishInfo> fish, FishSource? source = null, FishSource? crossCheck = null)
    {
        foreach (var f in fish)
            _byId.TryAdd(f.Id, f);
        Source = source;
        CrossCheck = crossCheck;
    }

    public FishSource? Source { get; }
    public FishSource? CrossCheck { get; }
    public int Count => _byId.Count;
    public IEnumerable<FishInfo> All => _byId.Values;

    public FishInfo? ById(uint itemId) => _byId.GetValueOrDefault(itemId);

    public static string? PathBeside(string? assemblyDirectory)
        => string.IsNullOrEmpty(assemblyDirectory) ? null : Path.Combine(assemblyDirectory, "Assets", FileName);

    /// <summary>Read the snapshot; an empty catalogue (with the reason logged) when it is missing or unreadable.</summary>
    public static FishCatalog Load(string? file, Action<string>? log = null)
    {
        try
        {
            if (file is null || !File.Exists(file))
            {
                log?.Invoke($"Fish data not found at {file ?? "(no path)"} — fishing knows no fish.");
                return new FishCatalog([]);
            }
            using var stream = File.OpenRead(file);
            using var gzip = new GZipStream(stream, CompressionMode.Decompress);
            return Parse(gzip);
        }
        catch (Exception ex)
        {
            log?.Invoke($"Fish data unreadable ({ex.GetType().Name}: {ex.Message}) — fishing knows no fish.");
            return new FishCatalog([]);
        }
    }

    public static FishCatalog Parse(Stream json)
    {
        var doc = JsonSerializer.Deserialize<Snapshot>(json, Json) ?? new Snapshot();
        return new FishCatalog(doc.Fish ?? [], doc.Source, doc.CrossCheck);
    }

    private sealed class Snapshot
    {
        public FishSource? Source { get; set; }
        public FishSource? CrossCheck { get; set; }
        public List<FishInfo>? Fish { get; set; }
    }
}
