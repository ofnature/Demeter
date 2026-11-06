using System;
using System.Collections.Generic;
using System.Linq;
using Demeter.Services.Gathering;

namespace Demeter.Services.Fishing;

/// <summary>
/// The fisher behind <see cref="IOwnGatherer"/>, so fish go wherever gathered items go: on the gather
/// lists, through <c>Demeter.Gather.*</c> for Hephaestus and through <c>Demeter.Item.*</c> for
/// Odysseus's quest and delivery fish.
///
/// <para>
/// The list runner already knows how to wait for a timed node — it asks <see cref="WaitFor"/> and
/// <see cref="UpFor"/>, sets aside what is not up yet, jumps to what has opened and requeues what
/// closed short. A fish's window (weather, the weather before, the hour) answers the same questions,
/// so a weather-bound fish waits its turn on a list exactly as an unspoiled node does.
/// </para>
///
/// <para>
/// Which hole: one with recorded stand points not all known bad, the character's Fisher level high
/// enough, in the zone a caller hinted at when there is one, then the hole open now (longest left),
/// then the soonest to open. Windows are computed once a minute per fish and reused — the list window
/// asks for every row, every frame.
/// </para>
/// </summary>
public sealed class FishGatherer : IOwnGatherer
{
    /// <summary>Not worth going for a window with less than this left.</summary>
    public static readonly TimeSpan MinLeft = TimeSpan.FromMinutes(1);

    private static readonly TimeSpan Recompute = TimeSpan.FromMinutes(1);

    private readonly FishCatalog _fish;
    private readonly IFishPlaces _places;
    private readonly FishingHoles _holes;
    private readonly FishingRun _run;
    private readonly Func<uint, HoleMemory> _memory;
    private readonly Func<uint, int> _levelOf;
    private readonly Func<uint, bool> _knows;
    private readonly Func<DateTime> _now;
    private readonly Dictionary<uint, (DateTime At, List<(FishingHole Hole, CatchWindow? Window)> Holes)> _cache = new();

    public FishGatherer(FishCatalog fish, IFishPlaces places, FishingHoles holes, FishingRun run,
        Func<uint, HoleMemory> memory, Func<uint, int> levelOf, Func<uint, bool> knows, Func<DateTime>? now = null)
    {
        _fish = fish;
        _places = places;
        _holes = holes;
        _run = run;
        _memory = memory;
        _levelOf = levelOf;
        _knows = knows;
        _now = now ?? (() => DateTime.UtcNow);
    }

    public bool Enabled { get; set; } = true;

    /// <summary>This item is a fish the catalogue knows — the fisher's to answer for, yes or no.</summary>
    public bool Handles(uint itemId) => _fish.ById(itemId) is not null;

    public bool CanGather(uint itemId, uint territoryHint = 0) => Enabled && WhyNotFish(itemId, territoryHint) is null;

    public string WhyNot(uint itemId, uint territoryHint = 0)
        => !Enabled ? "Demeter is switched off" : WhyNotFish(itemId, territoryHint) ?? "nothing is in the way";

    private string? WhyNotFish(uint itemId, uint territoryHint)
    {
        if (_fish.ById(itemId) is not { } fish)
            return $"item {itemId} is not a fish Demeter knows";
        var name = _places.ItemName(itemId);
        if (fish.IsSpearfish)
            return $"{name} is spearfished — not done yet";
        if (fish.IsOcean)
            return $"{name} is an ocean fishing catch — not done yet";
        if (fish.Path is not { Count: > 0 })
            return $"the fish data names no bait for {name}";
        if ((fish.Path.Count > 1 || fish.Predators?.Any(p => _fish.ById((uint)p[0])?.Path is { Count: > 1 }) == true) && !_knows(FishingRun.Mooch))
            return $"{name} is caught by mooching, and Mooch is not learned yet (Fisher Lv 25)";
        var holes = Usable(itemId);
        if (holes.Count == 0)
        {
            var all = _places.HolesFor(itemId);
            if (all.Count == 0)
                return $"no fishing hole in the sheets lists {name}";
            var fisher = _levelOf(HoleApproach.Fisher);
            if (all.All(h => h.Level > fisher))
                return $"{name}'s holes need Fisher Lv {all.Min(h => h.Level)} (Fisher is {(fisher > 0 ? $"Lv {fisher}" : "not unlocked")})";
            return $"no stand point is known at {name}'s holes (none recorded, or every one failed before)";
        }
        if ((fish.IsTimed || fish.NeedsWeather) && Windows(itemId).All(h => h.Window is null))
            return $"{name}'s window does not open within two months at any hole";
        return null;
    }

    /// <summary>Holes with stand points left to try and within the Fisher's level.</summary>
    private List<FishingHole> Usable(uint itemId)
    {
        var fisher = _levelOf(HoleApproach.Fisher);
        return _places.HolesFor(itemId)
            .Where(h => h.Level <= fisher && _memory(h.Id).Order(_holes.StandsAt(h.Id)).Count > 0)
            .ToList();
    }

    /// <summary>Each usable hole with its current or next window (null: none within the horizon; Always for an always-up fish).</summary>
    private List<(FishingHole Hole, CatchWindow? Window)> Windows(uint itemId)
    {
        var now = _now();
        if (_cache.TryGetValue(itemId, out var cached) && now - cached.At < Recompute
            && cached.Holes.All(h => h.Window is not { } w || w.End > now))
            return cached.Holes;

        var fish = _fish.ById(itemId);
        var result = new List<(FishingHole, CatchWindow?)>();
        foreach (var hole in Usable(itemId))
        {
            var windows = fish is null ? [] : CatchWindows.Next(fish, _places.RatesFor(hole.TerritoryId), now, max: 2);
            // The first window that is still worth going for: an open one with enough left, else the next.
            var useful = windows.FirstOrDefault(w => w.End - (w.Start > now ? w.Start : now) >= MinLeft && w.End > now);
            result.Add((hole, useful == default ? null : useful));
        }
        _cache[itemId] = (now, result);
        return result;
    }

    /// <summary>The hole to fish: the hinted zone, then open now (longest left), then soonest.</summary>
    public FishingHole? ChooseHole(uint itemId, uint territoryHint = 0)
    {
        var now = _now();
        var candidates = Windows(itemId).Where(h => h.Window is not null).ToList();
        if (candidates.Count == 0)
            return null;
        IEnumerable<(FishingHole Hole, CatchWindow? Window)> pool = candidates;
        if (territoryHint != 0 && candidates.Any(c => c.Hole.TerritoryId == territoryHint))
            pool = candidates.Where(c => c.Hole.TerritoryId == territoryHint);
        return pool
            .OrderBy(c => c.Window!.Value.Contains(now) ? 0 : 1)
            .ThenByDescending(c => c.Window!.Value.Contains(now) ? c.Window.Value.End : DateTime.MinValue)
            .ThenBy(c => c.Window!.Value.Start)
            .ThenByDescending(c => _holes.StandsAt(c.Hole.Id).Sum(s => s.Uses))
            .First().Hole;
    }

    public uint? ZoneOf(uint itemId) => ChooseHole(itemId)?.TerritoryId;

    public string Where(uint itemId)
        => ChooseHole(itemId) is { } hole ? $"{hole.Name} ({hole.ZoneName}) — Fisher Lv {hole.Level}" : string.Empty;

    /// <summary>For a fish with a window: zero when it is up (with a minute left), else the wait. Null when it has none.</summary>
    public TimeSpan? WaitFor(uint itemId, DateTime utc)
    {
        if (!Enabled || _fish.ById(itemId) is not { } fish || !(fish.IsTimed || fish.NeedsWeather))
            return null;
        var next = Windows(itemId).Where(h => h.Window is not null).Select(h => h.Window!.Value).ToList();
        if (next.Count == 0)
            return null;
        if (next.Any(w => w.Contains(utc) && w.End - utc >= MinLeft))
            return TimeSpan.Zero;
        var soonest = next.Where(w => w.Start > utc).Select(w => w.Start - utc).DefaultIfEmpty(TimeSpan.Zero).Min();
        return soonest;
    }

    public TimeSpan? UpFor(uint itemId, DateTime utc)
    {
        if (_fish.ById(itemId) is not { } fish || !(fish.IsTimed || fish.NeedsWeather))
            return null;
        var open = Windows(itemId).Where(h => h.Window is { } w && w.Contains(utc)).Select(h => h.Window!.Value.End - utc).ToList();
        return open.Count == 0 ? null : open.Max();
    }

    /// <summary>"up · 4:12 left" or "in 12:34", like a timed node; empty for a fish with no window.</summary>
    public string Timer(uint itemId, DateTime utc)
    {
        if (UpFor(itemId, utc) is { } left)
            return $"up · {Span(left)} left";
        return WaitFor(itemId, utc) is { } wait && wait > TimeSpan.Zero ? $"in {Span(wait)}" : string.Empty;
    }

    public bool Start(uint itemId, int count, int collectability, uint territoryHint = 0)
    {
        if (!Enabled || _run.Busy || _fish.ById(itemId) is not { } fish)
            return false;
        if (WhyNotFish(itemId, territoryHint) is not null || ChooseHole(itemId, territoryHint) is not { } hole)
            return false;
        _run.Begin(fish, count, hole, _holes.StandsAt(hole.Id), _memory(hole.Id), collectability);
        return _run.State != FishingRunState.Faulted;
    }

    public void Tick() => _run.Tick();

    public bool Busy => _run.Busy;
    public bool Faulted => _run.State == FishingRunState.Faulted;
    public bool AtNode => _run.State == FishingRunState.Fishing;
    public string Status => Faulted ? _run.FailReason : _run.Status;

    /// <summary>The bench modes are the node gatherer's; a fisher has none.</summary>
    public bool DryRun { get => false; set { } }
    public bool ProbeOnly { get => false; set { } }

    public void Stop() => _run.Cancel();

    public void RequestStopAfterRun() => _run.StopAfterCast();

    private static string Span(TimeSpan t) => t.TotalHours >= 1 ? $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}" : $"{t.Minutes}:{t.Seconds:00}";
}

/// <summary>
/// Nodes or fish, whichever the item is: one <see cref="IOwnGatherer"/> for everything that asks
/// Demeter to gather. Fish the fisher knows go to it; everything else to the node gatherer.
/// </summary>
public sealed class CompositeGatherer : IOwnGatherer
{
    private readonly IOwnGatherer _nodes;
    private readonly FishGatherer _fish;
    private IOwnGatherer? _active;

    public CompositeGatherer(IOwnGatherer nodes, FishGatherer fish)
    {
        _nodes = nodes;
        _fish = fish;
    }

    private IOwnGatherer For(uint itemId) => _fish.Handles(itemId) ? _fish : _nodes;
    private IOwnGatherer Current => _active ?? _nodes;

    public bool Enabled
    {
        get => _nodes.Enabled;
        set
        {
            _nodes.Enabled = value;
            _fish.Enabled = value;
        }
    }

    public bool CanGather(uint itemId, uint territoryHint = 0) => For(itemId).CanGather(itemId, territoryHint);
    public string WhyNot(uint itemId, uint territoryHint = 0) => For(itemId).WhyNot(itemId, territoryHint);
    public uint? ZoneOf(uint itemId) => For(itemId).ZoneOf(itemId);
    public string Where(uint itemId) => For(itemId).Where(itemId);
    public string Timer(uint itemId, DateTime utc) => For(itemId).Timer(itemId, utc);
    public TimeSpan? WaitFor(uint itemId, DateTime utc) => For(itemId).WaitFor(itemId, utc);
    public TimeSpan? UpFor(uint itemId, DateTime utc) => For(itemId).UpFor(itemId, utc);

    public bool Start(uint itemId, int count, int collectability, uint territoryHint = 0)
    {
        var g = For(itemId);
        if (!g.Start(itemId, count, collectability, territoryHint))
            return false;
        _active = g;
        return true;
    }

    public void Tick() => Current.Tick();
    public bool Busy => Current.Busy;
    public bool Faulted => Current.Faulted;
    public bool AtNode => Current.AtNode;
    public string Status => Current.Status;
    public bool DryRun { get => _nodes.DryRun; set => _nodes.DryRun = value; }
    public bool ProbeOnly { get => _nodes.ProbeOnly; set => _nodes.ProbeOnly = value; }
    public void Stop() => Current.Stop();
    public void RequestStopAfterRun() => Current.RequestStopAfterRun();
}
