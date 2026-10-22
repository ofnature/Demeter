using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Demeter.Services.Gathering;

namespace Demeter.Windows;

/// <summary>
/// The gathering probes, moved here from Odysseus's DEBUG-only Workbench with the gatherer. Each
/// button does exactly one thing and then stops: opening a node has locked a client before, so
/// nothing here loops, retries or runs on — press one, look at what happened, decide.
/// </summary>
public sealed class DebugWindow : DemeterWindow
{
    private readonly IOwnGatherer _gatherer;
    private readonly IGatherWorld _world;

    /// <summary>Every node id in the atlas is too many to pass; an empty set means "any node".</summary>
    private readonly uint[] _nearbyIds = [];

    public DebugWindow(IOwnGatherer gatherer, IGatherWorld world) : base("Demeter debug###DemeterDebug")
    {
        ArgumentNullException.ThrowIfNull(gatherer);
        ArgumentNullException.ThrowIfNull(world);
        _gatherer = gatherer;
        _world = world;
        Size = new Vector2(520, 220);
        SizeCondition = ImGuiCond.FirstUseEver;
    }

    public override void Draw()
    {
        DemeterTheme.SectionHeader("GATHER MODE");

        var mode = _gatherer.DryRun ? 0 : _gatherer.ProbeOnly ? 1 : 2;
        ImGui.SetNextItemWidth(330f);
        // Built rather than written as a literal: the items are separated by NULs, and a source
        // file with real NUL bytes in it is not a source file.
        var modes = string.Join('\u0000', "Walk only — never touch a node",
            "Probe — open one node, report, stop", "Full — work the nodes") + '\u0000';
        if (ImGui.Combo("Mode", ref mode, modes))
        {
            _gatherer.DryRun = mode == 0;
            _gatherer.ProbeOnly = mode == 1;
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(string.Join('\u000A',
                "Walk only proves the travel and node-finding and touches nothing.",
                "Probe opens one node, writes down what the window holds, and stops —",
                "it never chooses a row.",
                "Full is the real thing: choose the item, work it, collect, move on."));
        if (mode != 2)
            ImGui.TextColored(DemeterTheme.StatusYellow, "Not Full mode — the lists and other plugins' requests will not gather.");

        DemeterTheme.SectionHeader("BY HAND — stand next to a node");

        if (ImGui.Button("1. Say what is here"))
        {
            var found = _world.NearestLiveNode(_nearbyIds);
            _world.Log(found is { } n
                ? $"Probe: nearest node {n.NodeId} at {n.Distance:F1}y — mounted={_world.IsMounted}, {_world.Conditions}"
                : $"Probe: nothing up nearby — mounted={_world.IsMounted}, {_world.Conditions}");
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Touches nothing. Writes the node, the distance and every condition flag to the log.");

        ImGui.SameLine();
        if (ImGui.Button("2. Open it"))
        {
            if (_world.NearestLiveNode(_nearbyIds) is { } n)
            {
                _world.StopMoving();
                _world.Log($"Probe: opening node {n.NodeId} at {n.Distance:F1}y — {_world.Conditions}");
                _world.Log($"Probe: the interaction returned {_world.TryInteractWithDataId(n.NodeId)}.");
            }
            else
                _world.Log("Probe: nothing up nearby to open.");
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(string.Join('\u000A',
                "One interaction and nothing else. No slot is chosen, no window is closed,",
                "nothing is retried. If the client wedges on this, the interaction itself is",
                "the fault and not anything that follows it."));

        ImGui.SameLine();
        if (ImGui.Button("3. Dump the open window"))
            _world.DescribeOpenWindow();
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Reads the open window and writes what it contains. Changes nothing.");

        ImGui.SameLine();
        if (ImGui.Button("4. Leave the node"))
            _world.CloseNode();
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(string.Join('\u000A',
                "Sends the window's own cancel — what Escape does — and refuses while an action",
                "is still running. Forcing it shut mid-action is what locked the client."));
    }
}
