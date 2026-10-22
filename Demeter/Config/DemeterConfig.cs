using System;
using System.Collections.Generic;
using Dalamud.Configuration;
using Demeter.Services.Gathering;

namespace Demeter.Config;

/// <summary>Persisted settings, one file per client (<c>pluginConfigs/Demeter.json</c>).</summary>
[Serializable]
public sealed class DemeterConfig : IPluginConfiguration
{
    public int Version { get; set; } = 1;

    /// <summary>The master switch. Off, nothing runs — not the lists, not a request from another plugin.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>The gather lists, in the order they are shown. Another plugin's request is a list of its own, named for it.</summary>
    public List<GatherList> GatherLists { get; set; } = [];

    /// <summary>Per fishing hole (FishingSpot row): the stand points a test cast worked from, and those it did not.</summary>
    public Dictionary<uint, Services.Fishing.HoleMemory> HoleMemory { get; set; } = new();

    /// <summary>Where a finished gather run takes you.</summary>
    public GatherHome GatherHome { get; set; } = GatherHome.Stay;

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
}
