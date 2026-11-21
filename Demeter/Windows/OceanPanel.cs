using System;
using Dalamud.Bindings.ImGui;
using Demeter.Config;
using Demeter.Services.Fishing.Ocean;
using Demeter.Services.Gathering;

namespace Demeter.Windows;

/// <summary>Everything the Ocean Fishing panel reads.</summary>
public sealed record OceanPanelDeps(
    DemeterConfig Config,
    Action SaveConfig,
    IOceanVoyageSource Voyage,
    Func<bool> KuganeOpen,
    Func<bool> OceanUnlocked,
    Func<int> FisherLevel);

/// <summary>
/// The Ocean Fishing mode: when the next boat goes, which route, where to go after, and what the
/// voyage under way is doing. Signing up and fishing the voyage come next; this is the part that
/// reads the boat.
/// </summary>
public sealed class OceanPanel
{
    /// <summary>"All the Fish in the Sea", in Limsa Lominsa: the quest that opens ocean fishing.</summary>
    public const uint UnlockQuest = 69379;

    private readonly OceanPanelDeps _d;

    public OceanPanel(OceanPanelDeps deps) => _d = deps;

    public void Draw()
    {
        DrawNeeds();
        DrawNextBoat();
        DrawSettings();
        DrawVoyage();
    }

    private void DrawNeeds()
    {
        if (_d.FisherLevel() <= 0)
            DemeterTheme.TextWrappedColored(DemeterTheme.StatusRed, "Fisher is not unlocked on this character.");
        else if (!_d.OceanUnlocked())
            DemeterTheme.TextWrappedColored(DemeterTheme.StatusYellow, "Ocean fishing is not open yet: it needs the quest \"All the Fish in the Sea\" in Limsa Lominsa.");
    }

    private static void DrawNextBoat()
    {
        DemeterTheme.SectionHeader("NEXT BOAT");
        var now = DateTime.UtcNow;
        var signUp = OceanSchedule.NextSignUp(now);
        var leaves = signUp + OceanSchedule.SignUp;
        if (OceanSchedule.SignUpOpen(now))
            ImGui.TextColored(DemeterTheme.StatusGreen, $"Sign-up open — closes in {Clock(leaves - now)}");
        else
            ImGui.TextColored(DemeterTheme.TextSecondary, $"Sign-up opens in {Clock(signUp - now)}");
        DemeterTheme.TextWrappedColored(DemeterTheme.TextDisabled, $"Boat leaves at {leaves.ToLocalTime():HH:mm} your time");
    }

    private void DrawSettings()
    {
        DemeterTheme.SectionHeader("SETTINGS");
        var cfg = _d.Config;
        var kugane = _d.KuganeOpen();
        var route = kugane ? cfg.OceanRoute : OceanRoute.Indigo;

        // Labels above, combos the full width: the edge tab's pop-out is narrow.
        ImGui.TextColored(DemeterTheme.TextSecondary, "Route");
        ImGui.SetNextItemWidth(-1f);
        using (ImRaii.Disabled(!kugane))
        {
            if (ImGui.BeginCombo("##oceanRoute", RouteLabel(route)))
            {
                foreach (var r in Enum.GetValues<OceanRoute>())
                    if (ImGui.Selectable(RouteLabel(r), r == route))
                    {
                        cfg.OceanRoute = r;
                        _d.SaveConfig();
                    }
                ImGui.EndCombo();
            }
        }
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip(kugane ? "Indigo leaves from Limsa Lominsa, Ruby from Kugane." : "Indigo, from Limsa Lominsa. The Ruby route is offered once Kugane's aetheryte is attuned.");

        ImGui.TextColored(DemeterTheme.TextSecondary, "Afterwards");
        ImGui.SetNextItemWidth(-1f);
        if (ImGui.BeginCombo("##oceanHome", GatherHomeCommands.Label(cfg.OceanHome)))
        {
            foreach (var home in Enum.GetValues<GatherHome>())
                if (ImGui.Selectable(GatherHomeCommands.Label(home), home == cfg.OceanHome))
                {
                    cfg.OceanHome = home;
                    _d.SaveConfig();
                }
            ImGui.EndCombo();
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Where to go once the voyage is over and the results are closed.");
    }

    private void DrawVoyage()
    {
        DemeterTheme.SectionHeader("VOYAGE");
        if (_d.Voyage.Read() is not { } v)
        {
            ImGui.TextColored(DemeterTheme.TextDisabled, "Not on a voyage.");
            return;
        }
        DemeterTheme.TextWrappedColored(DemeterTheme.Accent, $"{v.RouteName} ({OceanSchedule.RouteOf(v.RouteId)} route {v.RouteId})");
        DemeterTheme.TextWrappedColored(DemeterTheme.TextPrimary, $"Stop {v.Zone + 1} of 3: {v.ZoneName}");
        ImGui.TextColored(DemeterTheme.TextSecondary, StatusText(v.Status));
        if (v.Spectral)
            ImGui.TextColored(DemeterTheme.StatusGreen, "Spectral current!");
    }

    private static string RouteLabel(OceanRoute route) => route switch
    {
        OceanRoute.Indigo => "Indigo (Limsa)",
        OceanRoute.Ruby => "Ruby (Kugane)",
        _ => route.ToString(),
    };

    private static string StatusText(VoyageStatus status) => status switch
    {
        VoyageStatus.WaitingForPlayers => "Waiting for everyone to board",
        VoyageStatus.SwitchingZone => "Sailing to the next stop",
        VoyageStatus.Fishing => "Fishing",
        VoyageStatus.NewZone => "Arriving at a new stop",
        VoyageStatus.Finished => "Voyage over",
        _ => status.ToString(),
    };

    private static string Clock(TimeSpan t)
        => t.TotalHours >= 1 ? $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}" : $"{t.Minutes}:{t.Seconds:00}";
}
