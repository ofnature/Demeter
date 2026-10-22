using System;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Windowing;
using Demeter.Config;
using Demeter.Services.Gathering;

namespace Demeter.Windows;

/// <summary>
/// The gather lists in a window of their own: several named lists, each item with a bag-count
/// target, run zone by zone through the gatherer. Toggled from the main window (and over IPC,
/// <c>Demeter.Ui.ToggleLists</c>, from a plugin that wants to show them).
/// </summary>
public sealed class GatherListsWindow : DemeterWindow
{
    private readonly DemeterConfig _config;
    private readonly Action _save;
    private readonly GatherListRunner _runner;
    private readonly GatherableIndex _gatherables;
    private readonly IOwnGatherer _gatherer;
    private readonly Func<uint, int> _itemCount;
    private readonly Func<bool> _runActive;
    private int _gatherListIndex;
    private string _gatherFilter = string.Empty;

    public GatherListsWindow(DemeterConfig config, Action save, GatherListRunner runner, GatherableIndex gatherables,
        IOwnGatherer gatherer, Func<uint, int> itemCount, Func<bool> runActive)
        : base("Demeter Gather lists##DemeterGather")
    {
        _config = config;
        _save = save;
        _runner = runner;
        _gatherables = gatherables;
        _gatherer = gatherer;
        _itemCount = itemCount;
        _runActive = runActive;
        Size = new Vector2(560, 420);
        SizeCondition = ImGuiCond.FirstUseEver;
        SizeConstraints = new WindowSizeConstraints { MinimumSize = new Vector2(420, 220), MaximumSize = new Vector2(1000, 1200) };
    }

    public override void Draw()
    {
        ImGui.PushStyleVar(ImGuiStyleVar.FrameRounding, 5f);
        try { DrawGatherLists(); }
        finally { ImGui.PopStyleVar(); }
    }

    /// <summary>
    /// The ready-made lists. True when one was taken, which ends the frame's drawing: the list the
    /// rest of it was about is no longer the selected one.
    /// </summary>
    private bool DrawPresetButton(System.Collections.Generic.List<GatherList> lists)
    {
        if (ImGui.SmallButton("+ Preset"))
            ImGui.OpenPopup("gatherpresets");
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("A new list, filled in — the elemental shards, crystals or clusters.");

        GatherPreset? taken = null;
        if (ImGui.BeginPopup("gatherpresets"))
        {
            foreach (var preset in GatherPresets.All)
                if (ImGui.Selectable($"{preset.Name} ({preset.ItemIds.Count})"))
                    taken = preset;
            ImGui.EndPopup();
        }

        if (taken is null)
            return false;

        lists.Add(GatherPresets.Build(taken));
        _gatherListIndex = lists.Count - 1;
        _save();
        return true;
    }

    private void DrawGatherLists()
    {
        var lists = _config.GatherLists;

        if (lists.Count == 0)
        {
            ImGui.TextColored(DemeterTheme.TextDisabled, "No lists yet.");
            ImGui.SameLine();
            if (ImGui.SmallButton("+ New list")) { lists.Add(new GatherList()); _save(); }
            ImGui.SameLine();
            DrawPresetButton(lists);
            return;
        }

        if (_gatherListIndex >= lists.Count) _gatherListIndex = lists.Count - 1;
        var list = lists[_gatherListIndex];

        ImGui.SetNextItemWidth(150f);
        if (ImGui.BeginCombo("##gatherlist", list.Name))
        {
            for (var i = 0; i < lists.Count; i++)
                if (ImGui.Selectable($"{lists[i].Name}##gl{i}", i == _gatherListIndex)) _gatherListIndex = i;
            ImGui.EndCombo();
        }
        ImGui.SameLine();
        if (ImGui.SmallButton("+ New")) { lists.Add(new GatherList()); _gatherListIndex = lists.Count - 1; _save(); return; }
        ImGui.SameLine();
        if (DrawPresetButton(lists)) return;
        ImGui.SameLine();
        if (ImGui.SmallButton("Delete")) { lists.RemoveAt(_gatherListIndex); _save(); return; }

        var name = list.Name;
        ImGui.SetNextItemWidth(150f);
        if (ImGui.InputText("##glname", ref name, 48)) list.Name = name;
        if (ImGui.IsItemDeactivatedAfterEdit()) _save();
        ImGui.SameLine();
        var enabled = list.Enabled;
        if (ImGui.Checkbox("Enabled##gl", ref enabled)) { list.Enabled = enabled; _save(); }
        ImGui.SameLine();
        var prune = list.RemoveCompleted;
        if (ImGui.Checkbox("Remove completed##gl", ref prune)) { list.RemoveCompleted = prune; _save(); }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Drop items that have reached their target when a run ends.");

        DemeterTheme.BeginCard("gathercard");
        if (ImGui.BeginTable("##gatheritems", 4, ImGuiTableFlags.SizingStretchProp))
        {
            ImGui.TableSetupColumn("item", ImGuiTableColumnFlags.WidthStretch);
            ImGui.TableSetupColumn("count", ImGuiTableColumnFlags.WidthFixed, 118f);
            ImGui.TableSetupColumn("where", ImGuiTableColumnFlags.WidthFixed, 150f);
            ImGui.TableSetupColumn("rm", ImGuiTableColumnFlags.WidthFixed, 26f);
            var current = _runner.CurrentItem;
            for (var i = 0; i < list.Items.Count; i++)
            {
                var item = list.Items[i];
                using var rowId = Dalamud.Interface.Utility.Raii.ImRaii.PushId(i);
                var held = _itemCount(item.ItemId);
                var done = held >= item.TargetCount;
                ImGui.TableNextRow();
                if (current == item.ItemId)
                    ImGui.TableSetBgColor(ImGuiTableBgTarget.RowBg0, ImGui.ColorConvertFloat4ToU32(DemeterTheme.WakeWash));

                ImGui.TableNextColumn();
                ImGui.TextColored(done ? DemeterTheme.StatusGreen : DemeterTheme.TextDisabled, "●");
                ImGui.SameLine(0f, 6f);
                ImGui.TextColored(done ? DemeterTheme.TextDisabled : DemeterTheme.TextPrimary, _gatherables.NameOf(item.ItemId));
                // Timed nodes: when it is up, counted down in real time.
                if (!done && _gatherer.Timer(item.ItemId, DateTime.UtcNow) is { Length: > 0 } timer)
                {
                    ImGui.SameLine(0f, 8f);
                    ImGui.TextColored(timer.StartsWith("up", StringComparison.Ordinal) ? DemeterTheme.StatusGreen : DemeterTheme.StatusYellow, timer);
                    if (ImGui.IsItemHovered())
                        ImGui.SetTooltip("A timed node: it is only there in its window, counted down here in real time.");
                }

                ImGui.TableNextColumn();
                ImGui.TextColored(done ? DemeterTheme.StatusGreen : DemeterTheme.TextPrimary, $"{held} /");
                ImGui.SameLine(0f, 4f);
                var target = item.TargetCount;
                ImGui.SetNextItemWidth(64f);
                if (ImGui.InputInt("##target", ref target, 0)) item.TargetCount = Math.Max(1, target);
                if (ImGui.IsItemDeactivatedAfterEdit()) _save();

                ImGui.TableNextColumn();
                ImGui.TextColored(DemeterTheme.TextDisabled, _gatherer.Where(item.ItemId));

                ImGui.TableNextColumn();
                if (DemeterTheme.IconButton("rm", FontAwesomeIcon.Trash, "Remove from the list", new Vector2(22, 20)))
                {
                    list.Items.RemoveAt(i);
                    _save();
                    break;
                }
            }
            ImGui.EndTable();
        }

        ImGui.SetNextItemWidth(-1f);
        ImGui.InputTextWithHint("##gatheradd", "Add an item… (part of its name)", ref _gatherFilter, 64);
        if (_gatherFilter.Length >= 2)
        {
            // What is already on the list is excluded by the search itself, so adding an item frees
            // its slot for the next one rather than spending it.
            foreach (var (id, itemName) in _gatherables.Search(_gatherFilter, 8, list.Items.ConvertAll(x => x.ItemId)))
            {
                if (ImGui.Selectable($"{itemName}##add{id}"))
                {
                    list.Items.Add(new GatherListItem { ItemId = id });
                    _gatherFilter = string.Empty;
                    _save();
                    break;
                }
                ImGui.SameLine();
                ImGui.TextColored(DemeterTheme.TextDisabled, _gatherer.Where(id));
            }
        }
        DemeterTheme.EndCard();

        ImGui.SetNextItemWidth(190f);
        if (ImGui.BeginCombo("Afterwards", GatherHomeCommands.Label(_config.GatherHome)))
        {
            foreach (var home in Enum.GetValues<GatherHome>())
                if (ImGui.Selectable(GatherHomeCommands.Label(home), home == _config.GatherHome))
                {
                    _config.GatherHome = home;
                    _save();
                }
            ImGui.EndCombo();
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Where a finished run takes you. Lifestream handles the inn and the estates; Return is the action, on its own cooldown.");

        var runner = _runner;
        if (runner.State == GatherListRunState.Running)
        {
            if (DemeterTheme.IconTextButton(FontAwesomeIcon.Stop, "Stop", DemeterTheme.RedDark, "Stop gathering; the gathering window is closed first."))
                runner.Stop();
            ImGui.SameLine();
            ImGui.TextColored(DemeterTheme.WakeFoam, runner.Status);
        }
        else
        {
            // "Gather" is the list on screen — a crystals list left enabled was gathered alongside
            // the yew branches it was never asked about. The enabled lists together stay one click away.
            var shown = _gatherListIndex < lists.Count ? lists[_gatherListIndex] : null;
            var canGather = _config.Enabled && !_runActive();
            var canRunShown = canGather && shown is { Items.Count: > 0 };
            using (ImRaii.Disabled(!canRunShown))
            {
                if (DemeterTheme.IconTextButton(FontAwesomeIcon.Play, "Gather", DemeterTheme.GreenDark,
                        $"Gather everything short on \"{shown?.Name}\", zone by zone — this list only.") && canRunShown)
                    runner.Begin([new GatherList { Name = shown!.Name, Enabled = true, Items = shown.Items }]);
            }
            if (ImGui.IsItemHovered() && !canRunShown)
                ImGui.SetTooltip(!_config.Enabled ? "Demeter is switched off." : _runActive() ? "Demeter is gathering for another plugin." : "Nothing on this list.");

            ImGui.SameLine();
            var canRunAll = canGather && lists.Any(l => l.Enabled && l.Items.Count > 0);
            using (ImRaii.Disabled(!canRunAll))
            {
                if (DemeterTheme.IconTextButton(FontAwesomeIcon.LayerGroup, "All enabled", DemeterTheme.GreenDark,
                        "Gather everything short on every list with Enabled ticked, together, zone by zone.") && canRunAll)
                    runner.Begin(lists);
            }
            if (runner.Status.Length > 0)
            {
                ImGui.SameLine();
                ImGui.TextColored(DemeterTheme.TextDisabled, runner.Status);
            }
        }
    }
}
