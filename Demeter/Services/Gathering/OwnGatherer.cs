using System;
using System.Collections.Generic;
using System.Linq;
using Demeter.Services.Travel;
using Demeter.Services.Gathering;

namespace Demeter.Services.Gathering;

/// <summary>
/// Demeter's gatherer, one item at a time — what the gather lists run each item through, and what
/// <c>Demeter.Item.*</c> offers a plugin that would otherwise hand off.
///
/// <para>
/// The interface is the seam Odysseus keeps: its quest Gather steps and custom deliveries take an
/// <c>IOwnGatherer</c>, which is now an IPC client for this one, and behave exactly as before when
/// there is none: hand to GatherBuddy, and stop with a reason when that cannot help.
/// GatherBuddy's auto-gather lists have no notion of a collectability threshold, which is why a
/// delivery for six Glass Eye at 240 ends with it switching itself off having gathered nothing
/// useful — the failure this exists to replace.
/// </para>
/// </summary>
public interface IOwnGatherer
{
    /// <summary>
    /// Off by default. Opening a node has locked the client more than once and the cause is not
    /// understood, so nothing reaches that code without being switched on deliberately — a delivery
    /// falls back to the GatherBuddy handoff exactly as it did before.
    /// </summary>
    bool Enabled { get; set; }

    /// <summary>There is a node with coordinates for this item, so it is worth trying.</summary>
    bool CanGather(uint itemId, uint territoryHint = 0);

    /// <summary>Why <see cref="CanGather"/> said no, for the log: which link is missing.</summary>
    string WhyNot(uint itemId, uint territoryHint = 0);

    /// <summary>The zone the gatherer would go to for this item, or null when it cannot place it.</summary>
    uint? ZoneOf(uint itemId);

    /// <summary>Where the item is found, for people: "Yak T'el — Miner Lv 90". Empty when the sheets do not say.</summary>
    string Where(uint itemId);

    /// <summary>"up · 4:12 left" or "in 12:34" for an item only timed nodes yield; empty otherwise.</summary>
    string Timer(uint itemId, DateTime utc) => string.Empty;

    /// <summary>
    /// For an item only timed nodes yield: zero when it can be gathered now, else the wait until it
    /// can. Null when the item is not timed-only — the ordinary <see cref="CanGather"/> applies.
    /// </summary>
    TimeSpan? WaitFor(uint itemId, DateTime utc) => null;

    /// <summary>At a node — opening it, working it, or closing its window. Stopping now would waste it.</summary>
    bool AtNode => false;

    /// <summary>For a timed-only item whose window is up: how long it stays up. Null otherwise.</summary>
    TimeSpan? UpFor(uint itemId, DateTime utc) => null;

    /// <summary>Go and get <paramref name="count"/> of it at <paramref name="collectability"/> or better.</summary>
    bool Start(uint itemId, int count, int collectability, uint territoryHint = 0);

    /// <summary>Drive it. Whatever started it is responsible for ticking it.</summary>
    void Tick();

    bool Busy { get; }

    /// <summary>Gave up. <see cref="Status"/> says why.</summary>
    bool Faulted { get; }

    string Status { get; }

    /// <summary>Walk it all through without touching a node. See <see cref="GatherRunner.DryRun"/>.</summary>
    bool DryRun { get; set; }

    /// <summary>Open one node, report what the window says, and stop. See <see cref="GatherRunner.ProbeOnly"/>.</summary>
    bool ProbeOnly { get; set; }

    void Stop();

    /// <summary>
    /// Finish the node or cast in hand, then stop: no next spot, no next cast. <see cref="Busy"/>
    /// falls once the character is free; <see cref="Start"/> clears it. Without a gentler way, stop now.
    /// </summary>
    void RequestStopAfterRun() => Stop();
}

/// <summary>
/// <see cref="GatherRunner"/> behind <see cref="IOwnGatherer"/>: resolve the item to a node, start
/// the runner on it, and report how it is doing.
/// </summary>
public sealed class OwnGatherer : IOwnGatherer
{
    private readonly GatherRunner _runner;
    private readonly IGatheringSource _source;
    private readonly NodeAtlas _atlas;
    private readonly Action<string> _log;

    public OwnGatherer(GatherRunner runner, IGatheringSource source, NodeAtlas atlas, Action<string> log)
    {
        ArgumentNullException.ThrowIfNull(runner);
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(atlas);

        _runner = runner;
        _source = source;
        _atlas = atlas;
        _log = log;
    }

    public bool Enabled { get; set; }

    /// <summary>
    /// Whether this character can work a node now: high enough on its class, and a way into its
    /// zone. Unset, every node is. Ice Crystal went to Yak T'el for its forty-seven spots, on a
    /// character with no aetheryte there and a level short of its nodes, past zones it could reach.
    /// </summary>
    public Func<GatheringTarget, bool>? Workable { get; init; }

    /// <summary>The node to go for: the best one this character can work, else the best there is — and the run says why not.</summary>
    public GatheringTarget? Choose(uint itemId, uint territoryHint = 0, DateTime? now = null)
        => Choose(GatheringPlan.All(itemId, _source, _atlas, territoryHint, now));

    private GatheringTarget? Choose(IReadOnlyList<GatheringTarget> targets)
    {
        bool Ok(GatheringTarget t) => Workable?.Invoke(t) ?? true;
        return targets.FirstOrDefault(t => Ok(t) && !_noWayIn.Contains(t.TerritoryId))
               ?? targets.FirstOrDefault(Ok)
               ?? targets.FirstOrDefault();
    }

    /// <summary>Zones Ariadne found no way around this session: passed over while another will do.</summary>
    private readonly HashSet<uint> _noWayIn = [];

    /// <summary>The run under way, to start again elsewhere if its zone has no way around.</summary>
    private uint _item, _hint, _zone;
    private int _count, _collectability;

    public bool CanGather(uint itemId, uint territoryHint = 0)
        => Enabled && GatheringPlan.For(itemId, _source, _atlas, territoryHint) is not null;

    public uint? ZoneOf(uint itemId) => Choose(itemId)?.TerritoryId;

    public string Where(uint itemId) => _source.For(itemId)?.Describe() ?? string.Empty;

    public string Timer(uint itemId, DateTime utc) => EorzeaClock.Describe(_source.WindowsFor(itemId), utc);

    public TimeSpan? WaitFor(uint itemId, DateTime utc)
    {
        if (!Enabled) return null;
        var windows = _source.WindowsFor(itemId);
        if (windows.Count == 0) return null;
        // A timed node the atlas cannot place is no use in or out of its window.
        if (!_source.PointsFor(itemId).Any(p => p.Timed && p.HasZone && _atlas.SpawnsOf(p.NodeId).Count > 0))
            return null;
        return EorzeaClock.UntilGatherable(windows, utc, GatheringPlan.TimedMinLeft);
    }

    public string WhyNot(uint itemId, uint territoryHint = 0)
    {
        var points = _source.PointsFor(itemId);
        if (points.Count == 0)
            return $"the sheets name no gathering point for item {itemId}";
        var placed = points.Count(p => p.HasZone);
        var withSpawns = points.Count(p => _atlas.SpawnsOf(p.NodeId).Count > 0);
        return $"item {itemId} has {points.Count} point(s) in the sheets, {placed} placed in a zone " +
               $"(hint {territoryHint}), {withSpawns} with atlas spawns";
    }

    public bool Start(uint itemId, int count, int collectability, uint territoryHint = 0)
    {
        // Checked here as well as in CanGather: a caller that asks directly must not get through
        // either, or "off" is only off for the callers that thought to ask first.
        if (!Enabled)
            return false;

        var targets = GatheringPlan.All(itemId, _source, _atlas, territoryHint, DateTime.UtcNow);
        if (targets.Count == 0)
        {
            _log($"Nothing in the node atlas yields item {itemId}.");
            return false;
        }

        var first = Choose(targets)!;
        var usable = targets.Where(t => t.TerritoryId == first.TerritoryId && t.ClassJobId == first.ClassJobId).ToList();
        _log($"Gathering {count} × {itemId} at {collectability}+ in {Zones.Describe(first.TerritoryId)} — " +
             $"{usable.Count} node(s), {usable.Sum(t => t.Spawns.Count)} stop(s).");
        (_item, _count, _collectability, _hint, _zone) = (itemId, count, collectability, territoryHint, first.TerritoryId);
        _runner.Begin(usable, count, collectability);
        return true;
    }

    public void Tick()
    {
        _runner.Tick();

        // A zone with no way to any spot (a broken mesh) is no reason to stop while the item grows
        // somewhere else this character can go.
        if (_runner.State != GatherRunState.Faulted || !_runner.NoWayInZone || _zone == 0)
            return;
        _noWayIn.Add(_zone);
        _zone = 0;
        var left = _count - _runner.Gathered;
        if (left <= 0 || Choose(_item, _hint, DateTime.UtcNow) is not { } next || _noWayIn.Contains(next.TerritoryId))
            return;   // nowhere else: the fault stands
        _log($"{_runner.FailReason}; trying {Zones.Describe(next.TerritoryId)} instead.");
        Start(_item, left, _collectability, _hint);
    }

    /// <summary>
    /// Anything that is not finished. Written as the inverse on purpose: listing the busy states
    /// meant adding one to the enum and forgetting it here, and a runner reported idle the instant
    /// it began reads to its caller as having given up before it moved.
    /// </summary>
    public bool Busy => _runner.State is not (GatherRunState.Idle or GatherRunState.Done or GatherRunState.Faulted);

    public bool Faulted => _runner.State == GatherRunState.Faulted;

    public bool AtNode => _runner.State is GatherRunState.Opening or GatherRunState.Working or GatherRunState.Closing;

    public TimeSpan? UpFor(uint itemId, DateTime utc)
        => EorzeaClock.Next(_source.WindowsFor(itemId), utc) is { Up: true } now ? now.Change : null;

    public string Status => _runner.State == GatherRunState.Faulted ? _runner.FailReason : _runner.Status;

    public bool DryRun { get => _runner.DryRun; set => _runner.DryRun = value; }

    public bool ProbeOnly { get => _runner.ProbeOnly; set => _runner.ProbeOnly = value; }

    public void Stop() => _runner.Cancel();

    public void RequestStopAfterRun() => _runner.StopAfterNode();
}
