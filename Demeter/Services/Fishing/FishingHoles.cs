using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Numerics;
using System.Text.Json;

namespace Demeter.Services.Fishing;

/// <summary>A place to fish from: where to stand, which way to face (the water), and how often people did.</summary>
public sealed class StandPoint
{
    public float X { get; set; }
    public float Y { get; set; }
    public float Z { get; set; }

    /// <summary>The game's facing, radians: zero is south (+Z), turning anticlockwise.</summary>
    public float Rotation { get; set; }

    /// <summary>How many recorded catches were made from here; 0 for one Demeter found itself.</summary>
    public int Uses { get; set; }

    public Vector3 Position => new(X, Y, Z);

    /// <summary>Close enough to be the same place to stand.</summary>
    public bool SameAs(StandPoint other, float within = 2.5f) => Vector3.Distance(Position, other.Position) <= within;
}

/// <summary>How long bites take for one fish on one bait at one hole, in seconds, with and without Chum.</summary>
public sealed class BiteTimes
{
    public uint Fish { get; set; }
    public uint Bait { get; set; }
    public int Count { get; set; }
    public double? Min { get; set; }
    public double? Max { get; set; }
    public double? MinChum { get; set; }
    public double? MaxChum { get; set; }
}

/// <summary>One fishing hole's recorded stand points and bite times.</summary>
public sealed class HoleRecords
{
    public uint Id { get; set; }
    public List<StandPoint> Stands { get; set; } = [];
    public List<BiteTimes> Bites { get; set; } = [];
}

/// <summary>
/// Where people actually stood to fish, per hole: GatherBuddy Reborn's community catch records
/// (Apache-2.0), grouped by <c>Tools/FishImport</c> into distinct stand points with the facing they
/// cast in, most used first. The <c>FishingSpot</c> sheet's own X/Z is a rough map marker — tens to
/// hundreds of yalms from the shore at some holes — so these are the stand points; the sheet is not.
/// </summary>
public sealed class FishingHoles
{
    public const string FileName = "fishing-holes.json.gz";

    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };
    private readonly Dictionary<uint, HoleRecords> _byId = new();

    public FishingHoles(IEnumerable<HoleRecords> holes)
    {
        foreach (var h in holes)
            _byId.TryAdd(h.Id, h);
    }

    public int Count => _byId.Count;

    public HoleRecords? ById(uint holeId) => _byId.GetValueOrDefault(holeId);

    public IReadOnlyList<StandPoint> StandsAt(uint holeId) => _byId.GetValueOrDefault(holeId)?.Stands ?? [];

    public static string? PathBeside(string? assemblyDirectory)
        => string.IsNullOrEmpty(assemblyDirectory) ? null : Path.Combine(assemblyDirectory, "Assets", FileName);

    public static FishingHoles Load(string? file, Action<string>? log = null)
    {
        try
        {
            if (file is null || !File.Exists(file))
            {
                log?.Invoke($"Fishing hole data not found at {file ?? "(no path)"} — no stand points known.");
                return new FishingHoles([]);
            }
            using var stream = File.OpenRead(file);
            using var gzip = new GZipStream(stream, CompressionMode.Decompress);
            return Parse(gzip);
        }
        catch (Exception ex)
        {
            log?.Invoke($"Fishing hole data unreadable ({ex.GetType().Name}: {ex.Message}) — no stand points known.");
            return new FishingHoles([]);
        }
    }

    public static FishingHoles Parse(Stream json)
        => new(JsonSerializer.Deserialize<Snapshot>(json, Json)?.Holes ?? []);

    private sealed class Snapshot
    {
        public List<HoleRecords>? Holes { get; set; }
    }
}

/// <summary>
/// What Demeter learned about a hole's stand points by trying them: the ones a cast worked from, and
/// the ones it did not (no water in reach, a ledge, a wall). Persisted per character config, so a
/// spot that failed once is not walked to again.
/// </summary>
public sealed class HoleMemory
{
    public List<StandPoint> Good { get; set; } = [];
    public List<StandPoint> Bad { get; set; } = [];

    /// <summary>The order to try: what worked here before, then the records most used first, never a known bad one.</summary>
    public IReadOnlyList<StandPoint> Order(IReadOnlyList<StandPoint> recorded)
    {
        var result = new List<StandPoint>();
        foreach (var s in Good)
            if (!Bad.Exists(b => b.SameAs(s)) && !result.Exists(r => r.SameAs(s)))
                result.Add(s);
        foreach (var s in recorded)
            if (!Bad.Exists(b => b.SameAs(s)) && !result.Exists(r => r.SameAs(s)))
                result.Add(s);
        return result;
    }

    public void Worked(StandPoint s)
    {
        Bad.RemoveAll(b => b.SameAs(s));
        if (!Good.Exists(g => g.SameAs(s)))
            Good.Insert(0, s);
    }

    public void Failed(StandPoint s)
    {
        Good.RemoveAll(g => g.SameAs(s));
        if (!Bad.Exists(b => b.SameAs(s)))
            Bad.Add(s);
    }
}
