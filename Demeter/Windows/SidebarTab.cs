using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility;

namespace Demeter.Windows;

/// <summary>One mode in the main window's left-hand list: icon, name, an accent bar when chosen. Orion's, in Demeter's colours.</summary>
internal static class SidebarTab
{
    public static bool Draw(string label, FontAwesomeIcon icon, bool selected)
    {
        var height = 36f * ImGuiHelpers.GlobalScale;
        var width = ImGui.GetContentRegionAvail().X;

        var origin = ImGui.GetCursorScreenPos();
        var end = origin + new Vector2(width, height);
        var dl = ImGui.GetWindowDrawList();
        // The window has to be the one under the mouse: the rectangle alone is also "hovered"
        // through any window lying over it, and a click there would change the mode.
        var hovered = ImGui.IsWindowHovered() && ImGui.IsMouseHoveringRect(origin, end);

        var bg = selected ? DemeterTheme.AccentWash : hovered ? DemeterTheme.BgRow : new Vector4(0, 0, 0, 0);
        dl.AddRectFilled(origin, end, ImGui.GetColorU32(bg), 6f);
        if (selected)
            dl.AddRectFilled(origin, new Vector2(origin.X + 3f * ImGuiHelpers.GlobalScale, end.Y), ImGui.GetColorU32(DemeterTheme.Accent), 1f);

        var padX = 12f * ImGuiHelpers.GlobalScale;
        var iconStr = icon.ToIconString();
        ImGui.PushFont(UiBuilder.IconFont);
        var iconSize = ImGui.CalcTextSize(iconStr);
        ImGui.SetCursorScreenPos(new Vector2(origin.X + padX, origin.Y + (height - iconSize.Y) * 0.5f));
        ImGui.TextColored(selected ? DemeterTheme.Accent : DemeterTheme.TextSecondary, iconStr);
        ImGui.PopFont();

        var labelSize = ImGui.CalcTextSize(label);
        ImGui.SetCursorScreenPos(new Vector2(origin.X + padX + iconSize.X + 8f * ImGuiHelpers.GlobalScale, origin.Y + (height - labelSize.Y) * 0.5f));
        ImGui.TextColored(selected ? DemeterTheme.TextPrimary : DemeterTheme.TextSecondary, label);

        ImGui.SetCursorScreenPos(origin);
        ImGui.Dummy(new Vector2(width, height));

        if (!hovered)
            return false;
        ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
        return ImGui.IsMouseClicked(ImGuiMouseButton.Left);
    }
}
