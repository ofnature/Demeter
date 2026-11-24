using System;
using System.Collections.Generic;
using Dalamud.Configuration;
using Demeter.Services.Gathering;

namespace Demeter.Config;

/// <summary>Persisted settings, one file per client (<c>pluginConfigs/Demeter.json</c>).</summary>
[Serializable]
public sealed class DemeterConfig : IPluginConfiguration
{
    public int Version { get; set; } = 2;

    /// <summary>
    /// Brings an older config up to date; true when it changed anything. Version 2: before it, a
    /// leg that could not get into the zone at all (Limsa's Upper Decks has no crystal) marked
    /// every stand point it tried as bad. Those marks are not evidence, so they all go once.
    /// </summary>
    public bool Migrate()
    {
        if (Version >= 2)
            return false;
        foreach (var memory in HoleMemory.Values)
            memory.Bad.Clear();
        Version = 2;
        return true;
    }

    /// <summary>The master switch. Off, nothing runs — not the lists, not a request from another plugin.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>A gather list run that Demeter unloading cut off, to be picked up when it is back.</summary>
    public ListResume? ResumeLists { get; set; }

    /// <summary>The gather lists, in the order they are shown. Another plugin's request is a list of its own, named for it.</summary>
    public List<GatherList> GatherLists { get; set; } = [];

    /// <summary>Per fishing hole (FishingSpot row): the stand points a test cast worked from, and those it did not.</summary>
    public Dictionary<uint, Services.Fishing.HoleMemory> HoleMemory { get; set; } = new();

    /// <summary>Where a finished gather run takes you.</summary>
    public GatherHome GatherHome { get; set; } = GatherHome.Stay;

    /// <summary>Which of the main window's modes is showing.</summary>
    public DemeterMode Mode { get; set; } = DemeterMode.Gathering;

    /// <summary>The ocean fishing boat to sign up for. Ruby is only offered once Kugane is open.</summary>
    public Services.Fishing.Ocean.OceanRoute OceanRoute { get; set; } = Services.Fishing.Ocean.OceanRoute.Indigo;

    /// <summary>Where a finished voyage takes you.</summary>
    public GatherHome OceanHome { get; set; } = GatherHome.Stay;

    /// <summary>Repair gear during a gather run once the worst piece is at or under this percent; 0 never repairs.</summary>
    public int RepairAtPercent { get; set; } = 30;

    /// <summary>
    /// Odysseus's saved gather lists have been looked for once. Set whether or not there were any,
    /// so a list deleted here is never brought back from Odysseus's copy.
    /// </summary>
    public bool OdysseusListsMigrated { get; set; }

    // ── Look ──

    public Windows.ThemeMode Theme { get; set; } = Windows.ThemeMode.Day;

    /// <summary>The main window lives as a tab on the screen edge instead of a window.</summary>
    public bool MinimizedToTab { get; set; }

    public Windows.Dock.DockEdge EdgeTabEdge { get; set; } = Windows.Dock.DockEdge.Left;

    /// <summary>Where along the edge, 0..1.</summary>
    public float EdgeTabAlong { get; set; } = 0.5f;

    /// <summary>The gather lists live as a tab of their own on the screen edge instead of a window.</summary>
    public bool ListsMinimizedToTab { get; set; }

    public Windows.Dock.DockEdge ListsTabEdge { get; set; } = Windows.Dock.DockEdge.Left;

    /// <summary>Where along the edge, 0..1.</summary>
    public float ListsTabAlong { get; set; } = 0.7f;
}

/// <summary>The main window's modes, listed down its left side.</summary>
public enum DemeterMode
{
    Gathering = 0,
    OceanFishing = 1,
}

/// <summary>
/// A list run cut off by Demeter unloading — an update, a reload. Picked up again for the same
/// character, once it is free, within <see cref="DemeterPlugin.ResumeWithin"/> of being cut off.
/// </summary>
[Serializable]
public sealed class ListResume
{
    public List<string> Lists { get; set; } = [];
    public ulong ContentId { get; set; }
    public DateTime SavedUtc { get; set; }
}
