using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Windowing;
using Demeter.Config;
using Demeter.Services.Gathering;
using Demeter.Services.Ipc;

namespace Demeter.Windows;

/// <summary>Everything the main window needs to reach, so its constructor stays readable.</summary>
public sealed record MainWindowDeps(
    DemeterConfig Config,
    Action SaveConfig,
    GatherListRunner Lists,
    GatherGateway Gateway,
    ItemGateway Items,
    IOwnGatherer Gatherer,
    Func<uint, string> NameOf,
    Func<string, bool> PluginLoaded,
    Action ToggleLists,
    Action? OpenDebug,
    Action StopAll);

/// <summary>
/// The main window: what Demeter is doing and for whom, a Stop, the way to the gather lists, and
/// the handful of settings there are. Small on purpose — the lists window is where the work is set
/// up. Minimized, it is a tab on the screen edge stacked with Daedalus's and Odysseus's.
/// </summary>
public sealed class MainWindow : DemeterWindow
{
    private readonly MainWindowDeps _d;

    /// <summary>Being drawn as the edge tab's pop-out rather than as the window itself.</summary>
    private bool _inDock;

    public MainWindow(MainWindowDeps deps) : base("Demeter###DemeterMain")
    {
        _d = deps;
        Size = new Vector2(340, 320);
        SizeCondition = ImGuiCond.FirstUseEver;
        SizeConstraints = new WindowSizeConstraints { MinimumSize = new Vector2(280, 140), MaximumSize = new Vector2(700, 1000) };
    }

    private DemeterConfig Cfg => _d.Config;

    /// <summary>One word for the title and the chip.</summary>
    private string StateWord
        => !Cfg.Enabled ? "Disabled"
            : _d.Gateway.IsRunning ? $"Gathering for {_d.Gateway.RequestedBy}"
            : _d.Lists.State == GatherListRunState.Running ? "Gathering lists"
            : _d.Items.IsRunning ? "Gathering an item"
            : "Idle";

    private bool Running => _d.Lists.State == GatherListRunState.Running || _d.Items.IsRunning;

    public override void PreDraw()
    {
        base.PreDraw();
        WindowName = $"Demeter v{DemeterPlugin.PluginVersion} · {StateWord}###DemeterMain";
    }

    /// <summary>The body, drawn inside the edge tab's pop-out panel.</summary>
    public void DrawDocked()
    {
        _inDock = true;
        try { Draw(); }
        finally { _inDock = false; }
    }

    public override void Draw()
    {
        ImGui.PushStyleVar(ImGuiStyleVar.FrameRounding, 5f);
        try
        {
            DrawTopRow();
            DemeterTheme.BeginCard("hero");
            DrawStatus();
            DrawControls();
            DemeterTheme.EndCard();
            DrawDependencies();
            DrawSettings();
        }
        finally
        {
            ImGui.PopStyleVar();
        }
    }

    private void DrawTopRow()
    {
        DemeterTheme.StateChip(StateWord);
        ImGui.NewLine();

        var enabled = Cfg.Enabled;
        if (ImGui.Checkbox("Enabled", ref enabled))
        {
            Cfg.Enabled = enabled;
            if (!enabled)
                _d.StopAll();
            _d.SaveConfig();
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Off, nothing runs: not the lists, and no request from another plugin is taken.");

        ImGui.SameLine();
        var dockLabel = _inDock ? "Restore window" : "Minimize to edge tab";
        if (DemeterTheme.IconButton("dock", _inDock ? FontAwesomeIcon.WindowRestore : FontAwesomeIcon.WindowMinimize, dockLabel))
        {
            Cfg.MinimizedToTab = !_inDock;
            IsOpen = _inDock;
            _d.SaveConfig();
        }
        if (_d.OpenDebug is { } openDebug)
        {
            ImGui.SameLine();
            if (DemeterTheme.IconButton("debug", FontAwesomeIcon.Bug, "Gathering probes (debug builds only)"))
                openDebug();
        }
    }

    private void DrawStatus()
    {
        var status = _d.Gateway.IsRunning || _d.Lists.State == GatherListRunState.Running ? _d.Lists.Status
            : _d.Items.IsRunning || _d.Gatherer.Faulted ? _d.Gatherer.Status
            : _d.Lists.Status;
        if (_d.Lists.CurrentItem is { } item && _d.Lists.State == GatherListRunState.Running)
        {
            ImGui.TextColored(DemeterTheme.Accent, _d.NameOf(item));
            ImGui.SameLine();
            ImGui.TextColored(DemeterTheme.TextDisabled, $"· {_d.Lists.Remaining} left");
        }
        DemeterTheme.TextWrappedColored(Running ? DemeterTheme.WakeFoam : DemeterTheme.TextSecondary,
            status.Length > 0 ? status : "Nothing to do.");
    }

    private void DrawControls()
    {
        if (Running)
        {
            if (DemeterTheme.IconTextButton(FontAwesomeIcon.Stop, "Stop", DemeterTheme.RedDark,
                    "Stop gathering; an open node window is closed first."))
                _d.StopAll();
            DemeterTheme.FlowSameLine(DemeterTheme.IconTextWidth(FontAwesomeIcon.List, "Gather lists"));
        }
        if (DemeterTheme.IconTextButton(FontAwesomeIcon.List, "Gather lists", DemeterTheme.NeutralDark,
                "The lists: items with target counts, run zone by zone."))
            _d.ToggleLists();
    }

    private void DrawDependencies()
    {
        DemeterTheme.SectionHeader("NEEDS");
        var pathing = Cfg.PathingProvider == VnavIpc.AriadneProvider ? "Ariadne" : "vnavmesh";
        DemeterTheme.DependencyChip(pathing, _d.PluginLoaded(pathing));
        ImGui.SameLine(0f, 14f);
        DemeterTheme.DependencyChip("Lifestream", _d.PluginLoaded("Lifestream"));
        ImGui.SameLine(0f, 14f);
        DemeterTheme.DependencyChip("Daedalus", _d.PluginLoaded("Daedalus"), required: false);
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Fights anything that attacks on the way. Without it, travel carries on through a fight.");
    }

    private void DrawSettings()
    {
        if (!ImGui.CollapsingHeader("Settings"))
            return;

        ImGui.SetNextItemWidth(140f);
        if (ImGui.BeginCombo("Pathing", Cfg.PathingProvider))
        {
            foreach (var provider in new[] { VnavIpc.VnavmeshProvider, VnavIpc.AriadneProvider })
                if (ImGui.Selectable(provider, provider == Cfg.PathingProvider))
                {
                    Cfg.PathingProvider = provider;
                    _d.SaveConfig();
                }
            ImGui.EndCombo();
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Which plugin walks and flies the character.");

        var repair = Cfg.RepairAtPercent;
        ImGui.SetNextItemWidth(140f);
        if (ImGui.SliderInt("Repair at %", ref repair, 0, 99))
            Cfg.RepairAtPercent = repair;
        if (ImGui.IsItemDeactivatedAfterEdit())
            _d.SaveConfig();
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Self-repair during a list run once the worst piece is at or under this. 0 never repairs.");

        var dusk = Cfg.Theme == ThemeMode.Dusk;
        if (ImGui.Checkbox("Dusk theme", ref dusk))
        {
            Cfg.Theme = dusk ? ThemeMode.Dusk : ThemeMode.Day;
            _d.SaveConfig();
        }
    }
}
