using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;
using Demeter.Services.Fishing;
using Demeter.Services.Gathering;

namespace Demeter.Windows;

/// <summary>
/// Look a fish up: how it is caught (bait and mooch road, hook, tug, predators) and when — the next
/// window at every hole that has it, counted down in real time. "Go there" (phase 2) takes the
/// character to the hole and proves a stand point with a test cast; nothing here fishes yet.
/// </summary>
public sealed class FishWindow : DemeterWindow
{
    private readonly FishCatalog _catalog;
    private readonly FishSpots _spots;
    private readonly FishingHoles _holes;
    private readonly HoleApproach _approach;
    private readonly Action<FishingHole, IReadOnlyList<StandPoint>> _goThere;
    private readonly Func<bool> _otherBusy;
    private readonly List<(uint Id, string Name)> _names;
    private string _filter = string.Empty;
    private uint _selected;
    private DateTime _computedAt;
    private uint _computedFor;
    private readonly Dictionary<uint, IReadOnlyList<CatchWindow>> _windows = new();

    public FishWindow(FishCatalog catalog, FishSpots spots, FishingHoles holes, HoleApproach approach,
        Action<FishingHole, IReadOnlyList<StandPoint>> goThere, Func<bool> otherBusy) : base("Demeter Fish###DemeterFish")
    {
        _catalog = catalog;
        _spots = spots;
        _holes = holes;
        _approach = approach;
        _goThere = goThere;
        _otherBusy = otherBusy;
        _names = catalog.All.Select(f => (f.Id, f.Name ?? spots.ItemName(f.Id))).OrderBy(x => x.Item2, StringComparer.OrdinalIgnoreCase).ToList();
        Size = new Vector2(560, 480);
        SizeCondition = ImGuiCond.FirstUseEver;
        SizeConstraints = new WindowSizeConstraints { MinimumSize = new Vector2(420, 240), MaximumSize = new Vector2(1000, 1400) };
    }

    public override void Draw()
    {
        var now = DateTime.UtcNow;
        var et = EorzeaClock.MinuteOfDay(now);
        ImGui.TextColored(DemeterTheme.TextSecondary, $"Eorzea {(int)et / 60:00}:{(int)et % 60:00}");
        ImGui.SameLine();
        ImGui.TextColored(DemeterTheme.TextDisabled, _catalog.Source is { } s
            ? $"· {_catalog.Count} fish · GatherBuddy Reborn {s.Commit[..Math.Min(7, s.Commit.Length)]}"
            : "· no fish data");

        DrawApproach();

        ImGui.SetNextItemWidth(-1f);
        ImGui.InputTextWithHint("##fishsearch", "Find a fish… (part of its name)", ref _filter, 64);
        if (_filter.Length >= 2)
        {
            foreach (var (id, name) in _names.Where(n => n.Name.Contains(_filter, StringComparison.OrdinalIgnoreCase)).Take(8))
            {
                if (ImGui.Selectable($"{name}##fish{id}", id == _selected))
                {
                    _selected = id;
                    _filter = string.Empty;
                }
            }
        }

        if (_catalog.ById(_selected) is not { } fish)
            return;

        DemeterTheme.SectionHeader(fish.Name ?? _spots.ItemName(fish.Id));
        DrawHowToCatch(fish);
        DrawWhen(fish, now);
    }

    private void DrawHowToCatch(FishInfo fish)
    {
        if (fish.IsSpearfish)
            Row("Spearfishing", $"{fish.SpearSize} {fish.SpearSpeed}".Trim());
        if (fish.Path is { Count: > 0 } path)
            Row("Road", string.Join(" → ", path.Select(_spots.ItemName)) + " → " + (fish.Name ?? "?"));
        if (fish.HookSet is not null || fish.Bite is not null)
            Row("Hook", $"{fish.HookSet ?? "?"} hookset on a {fish.Bite?.ToLowerInvariant() ?? "?"} tug"
                        + (fish.Legendary == true ? " · legendary" : fish.ForceBig == true ? " · big fish" : string.Empty));
        if (fish.Predators is { Count: > 0 } predators)
            Row("Intuition", string.Join(", ", predators.Select(p => $"{_spots.ItemName((uint)p[0])} ×{p[1]}"))
                             + (fish.IntuitionSeconds is { } s ? $" — lasts {s}s" : string.Empty));
        if (fish.Weather is { Count: > 0 } weather)
            Row("Weather", string.Join(", ", weather.Select(_spots.WeatherName)));
        if (fish.PreviousWeather is { Count: > 0 } previous)
            Row("After", string.Join(", ", previous.Select(_spots.WeatherName)));
        if (fish.Time is { } time)
            Row("Time", $"{time[0] / 60:00}:{time[0] % 60:00} – {time[1] / 60:00}:{time[1] % 60:00} ET");
        if (fish.Lure is { } lure)
            Row("Lure", lure);
        if (fish.Snagging == "Required")
            Row("Snagging", "required");
        if (fish.SurfaceSlap is { } slap)
            Row("Surface Slap", _spots.ItemName(slap));
        if (fish.Guide is { Length: > 0 } guide)
            DemeterTheme.TextWrappedColored(DemeterTheme.TextSecondary, guide);
    }

    private void DrawWhen(FishInfo fish, DateTime now)
    {
        var holes = _spots.HolesFor(fish.Id);
        if (fish.IsOcean)
        {
            ImGui.TextColored(DemeterTheme.TextDisabled, "An ocean fishing catch — on the voyage, not at a hole.");
            return;
        }
        if (holes.Count == 0)
        {
            ImGui.TextColored(DemeterTheme.TextDisabled, "No fishing hole in the sheets lists this fish.");
            return;
        }

        // The weather search walks up to two months of periods: once a minute is plenty.
        if (_computedFor != fish.Id || now - _computedAt > TimeSpan.FromMinutes(1))
        {
            _windows.Clear();
            foreach (var hole in holes)
                _windows[hole.Id] = CatchWindows.Next(fish, _spots.RatesFor(hole.TerritoryId), now, max: 2);
            _computedFor = fish.Id;
            _computedAt = now;
        }

        DemeterTheme.SectionHeader("WHEN");
        foreach (var hole in holes)
        {
            using var id = Dalamud.Interface.Utility.Raii.ImRaii.PushId((int)hole.Id);
            var stands = _holes.StandsAt(hole.Id);
            using (ImRaii.Disabled(stands.Count == 0 || _approach.Busy || _otherBusy()))
            {
                if (ImGui.SmallButton("Go there"))
                    _goThere(hole, stands);
            }
            if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
                ImGui.SetTooltip(stands.Count == 0
                    ? "No stand point is recorded for this hole."
                    : $"Fisher on, travel there, and prove one of {stands.Count} recorded stand points with a test cast (reeled straight back in).");
            ImGui.SameLine();
            ImGui.TextColored(DemeterTheme.TextPrimary, $"{hole.Name}");
            ImGui.SameLine();
            ImGui.TextColored(DemeterTheme.TextDisabled, $"({hole.ZoneName}, Lv {hole.Level})");
            ImGui.SameLine();
            var windows = _windows.GetValueOrDefault(hole.Id) ?? [];
            var current = windows.FirstOrDefault(w => w.End > now);
            if (windows.Count == 0 || current == default)
                ImGui.TextColored(DemeterTheme.StatusRed, "not within two months");
            else if (current.End == DateTime.MaxValue)
                ImGui.TextColored(DemeterTheme.StatusGreen, "always");
            else if (current.Contains(now))
                ImGui.TextColored(DemeterTheme.WakeFoam, $"up now · {Span(current.End - now)} left");
            else
                ImGui.TextColored(DemeterTheme.StatusYellow, $"in {Span(current.Start - now)} ({current.Start.ToLocalTime():ddd HH:mm}, for {Span(current.Length)})");
        }
    }

    /// <summary>What "Go there" is doing, with a Stop.</summary>
    private void DrawApproach()
    {
        if (_approach.Busy)
        {
            if (DemeterTheme.IconTextButton(Dalamud.Interface.FontAwesomeIcon.Stop, "Stop", DemeterTheme.RedDark, "Stop going to the hole."))
                _approach.Cancel();
            ImGui.SameLine();
            ImGui.TextColored(DemeterTheme.WakeFoam, _approach.Status);
        }
        else if (_approach.Status.Length > 0)
            ImGui.TextColored(_approach.State == HoleApproachState.Faulted ? DemeterTheme.StatusRed : DemeterTheme.StatusGreen, _approach.Status);
    }

    private static void Row(string label, string value)
    {
        ImGui.TextColored(DemeterTheme.TextSecondary, label);
        ImGui.SameLine(110f);
        ImGui.TextWrapped(value);
    }

    private static string Span(TimeSpan t) => t.TotalHours >= 24
        ? $"{(int)t.TotalDays}d {t.Hours}h"
        : t.TotalHours >= 1 ? $"{(int)t.TotalHours}h {t.Minutes:00}m" : $"{t.Minutes}:{t.Seconds:00}";
}
