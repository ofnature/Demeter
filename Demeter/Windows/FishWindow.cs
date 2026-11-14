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
    private readonly Action<FishInfo, int, FishingHole, IReadOnlyList<StandPoint>> _fishHere;
    private readonly FishingRun _run;

    /// <summary>The bait on the line now (0 for none), and how many of it are in the bags.</summary>
    private readonly Func<(uint Bait, int Count)> _bait;

    /// <summary>"Fish here": the session another plugin would start, from where the character stands.</summary>
    private readonly FishSession _session;
    private readonly Func<bool> _fishWhereStanding;
    private readonly Func<string> _lastRefusal;
    private int _count = 1;
    private readonly List<(uint Id, string Name)> _names;
    private string _filter = string.Empty;
    private uint _selected;
    private DateTime _computedAt;
    private uint _computedFor;
    private readonly Dictionary<uint, IReadOnlyList<CatchWindow>> _windows = new();

    public FishWindow(FishCatalog catalog, FishSpots spots, FishingHoles holes, HoleApproach approach,
        Action<FishingHole, IReadOnlyList<StandPoint>> goThere, Action<FishInfo, int, FishingHole, IReadOnlyList<StandPoint>> fishHere,
        FishingRun run, Func<bool> otherBusy, Func<(uint Bait, int Count)> bait,
        FishSession session, Func<bool> fishWhereStanding, Func<string> lastRefusal) : base("Demeter Fish###DemeterFish")
    {
        _bait = bait;
        _session = session;
        _fishWhereStanding = fishWhereStanding;
        _lastRefusal = lastRefusal;
        _fishHere = fishHere;
        _run = run;
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
        // Where the fish data came from is for the curious (and NOTICE.md); missing data is for everyone.
        if (_catalog.Source is { } s)
        {
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip($"{_catalog.Count} fish · data from GatherBuddy Reborn {s.Commit[..Math.Min(7, s.Commit.Length)]}");
        }
        else
        {
            ImGui.SameLine();
            ImGui.TextColored(DemeterTheme.StatusRed, "· no fish data");
        }
        DrawBait();

        DrawApproach();
        DrawFishHere();

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
        ImGui.SetNextItemWidth(90f);
        if (ImGui.InputInt("How many##fishcount", ref _count))
            _count = Math.Clamp(_count, 1, 999);
        var busy = _approach.Busy || _run.Busy || _otherBusy();
        foreach (var hole in holes)
        {
            using var id = Dalamud.Interface.Utility.Raii.ImRaii.PushId((int)hole.Id);
            var stands = _holes.StandsAt(hole.Id);
            using (ImRaii.Disabled(stands.Count == 0 || busy || fish.IsSpearfish))
            {
                if (ImGui.SmallButton("Fish"))
                    _fishHere(fish, _count, hole, stands);
            }
            if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
                ImGui.SetTooltip(fish.IsSpearfish
                    ? "Spearfishing is not done yet."
                    : $"Go there and catch {_count}: the bait and mooch road from the data, each bite judged by its timing and tug, unwanted fish let go. Stops at the count, or when the window closes.");
            ImGui.SameLine();
            using (ImRaii.Disabled(stands.Count == 0 || busy))
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

    /// <summary>
    /// The bait on the line, and how many are left. Green when it is what the selected fish's road
    /// starts with, yellow when it is not (Fish puts the right one on), red when nothing is on.
    /// </summary>
    private void DrawBait()
    {
        var (bait, count) = _bait();
        ImGui.SameLine();
        if (bait == 0)
        {
            ImGui.TextColored(DemeterTheme.StatusRed, "· no bait on");
            return;
        }
        var wants = _catalog.ById(_selected)?.Path is { Count: > 0 } road ? road[0] : 0u;
        var colour = wants == 0 || wants == bait ? DemeterTheme.StatusGreen : DemeterTheme.StatusYellow;
        ImGui.TextColored(colour, $"· Bait: {_spots.ItemName(bait)} ×{count}");
        if (ImGui.IsItemHovered() && wants != 0 && wants != bait)
            ImGui.SetTooltip($"{_spots.ItemName(_selected)} starts with {_spots.ItemName(wants)} — Fish puts it on for you.");
    }

    /// <summary>
    /// "Fish here": fish from where you stand with the bait that is on, every bite hooked, at a varied
    /// pace — what Selene starts through <c>Demeter.Fish.Start</c>, to try by hand.
    /// </summary>
    private void DrawFishHere()
    {
        if (_session.Busy)
        {
            if (DemeterTheme.IconTextButton(Dalamud.Interface.FontAwesomeIcon.Stop, "Stop##fishhere", DemeterTheme.RedDark, "Stop fishing here; the line is reeled in."))
                _session.Cancel();
            ImGui.SameLine();
            ImGui.TextColored(DemeterTheme.WakeFoam, $"Fishing here for {_session.RequestedBy} — {_session.Casts} casts"
                                                     + (_session.LastBite is { } b ? $" · last bite {b.Seconds:F1}s {b.Tug.ToString().ToLowerInvariant()}" : string.Empty));
            DrawLanded();
            return;
        }

        var busy = _approach.Busy || _run.Busy || _otherBusy();
        using (ImRaii.Disabled(busy))
        {
            if (ImGui.SmallButton("Fish here"))
                _fishWhereStanding();
        }
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip("Fish from where you stand, with the bait that is on (Cosmic Exploration's too): every bite hooked, "
                             + "no bait change, a varied pace. Stops when you press Stop, the bait runs out, or the fish sense something amiss.");
        ImGui.SameLine();
        if (_lastRefusal() is { Length: > 0 } refused)
            ImGui.TextColored(DemeterTheme.StatusRed, $"Can't: {refused}");
        else if (_session.State is FishSessionState.Done or FishSessionState.StoppedAsAsked or FishSessionState.Faulted)
        {
            ImGui.TextColored(_session.State == FishSessionState.Faulted ? DemeterTheme.StatusRed : DemeterTheme.StatusGreen,
                $"Ended: {_session.Reason} — {_session.Casts} casts.");
            DrawLanded();
        }
        else
            ImGui.NewLine();
    }

    private void DrawLanded()
    {
        if (_session.Landed.Count > 0)
            ImGui.TextColored(DemeterTheme.TextSecondary, "Landed: " + string.Join(", ", _session.Landed.Select(kv => $"{_spots.ItemName(kv.Key)} ×{kv.Value}")));
    }

    /// <summary>What "Fish" or "Go there" is doing, with a Stop.</summary>
    private void DrawApproach()
    {
        if (_run.Busy)
        {
            if (DemeterTheme.IconTextButton(Dalamud.Interface.FontAwesomeIcon.Stop, "Stop", DemeterTheme.RedDark, "Stop fishing; the line is reeled in."))
                _run.Cancel();
            ImGui.SameLine();
            ImGui.TextColored(DemeterTheme.WakeFoam, _run.Status);
            if (_run.Caught.Count > 0)
                ImGui.TextColored(DemeterTheme.TextSecondary, "Landed: " + string.Join(", ", _run.Caught.Select(kv => $"{_spots.ItemName(kv.Key)} ×{kv.Value}")));
            return;
        }
        if (_run.State is FishingRunState.Done or FishingRunState.Faulted && _run.Status.Length > 0)
        {
            ImGui.TextColored(_run.State == FishingRunState.Faulted ? DemeterTheme.StatusRed : DemeterTheme.StatusGreen, _run.Status);
            return;
        }
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
