using System.Numerics;
using Dalamud.Bindings.ImGui;

namespace Demeter.Windows;

/// <summary>Which of the two palettes is live.</summary>
public enum ThemeMode
{
    /// <summary>Wheat-paper body, ink text.</summary>
    Day,
    /// <summary>Dark soil-green body, light text.</summary>
    Dusk,
}

/// <summary>
/// Demeter palette + shared draw helpers, ported from Odysseus's theme (same author, same vocabulary).
///
/// <para>
/// Two palettes, one vocabulary. Every window reads colours through the static properties below,
/// which forward to the live <see cref="Palette"/>; <see cref="Mode"/> swaps it. Windows derive
/// from <see cref="DemeterWindow"/>, which pushes the palette into ImGui's own window/frame/
/// button colours around each draw — that is what makes a light window actually light instead of
/// light text on Dalamud's dark chrome.
/// </para>
///
/// <para>
/// <b>Accent: harvest olive</b> on a wheat-paper body. Olive rather than gold, because gold is
/// Daedalus's, and yellow-green rather than the status ramp's sea green, so "Demeter's colour" and
/// "this is fine" never read as the same thing.
/// <b>Foam/teal is reserved for time windows</b> — a timed node or a fish that is up now — never
/// ordinary chrome.
/// </para>
/// </summary>
internal static class DemeterTheme
{
    /// <summary>One complete palette. Immutable; swap the whole thing.</summary>
    public sealed record Palette(
        Vector4 BgDeep, Vector4 BgPanel, Vector4 BgRow, Vector4 TitleBg, Vector4 Border,
        Vector4 Accent, Vector4 AccentDim, Vector4 AccentWash,
        Vector4 WakeFoam, Vector4 WakeDim, Vector4 WakeWash,
        Vector4 StatusGreen, Vector4 StatusYellow, Vector4 StatusRed, Vector4 StatusGrey,
        Vector4 TextPrimary, Vector4 TextSecondary, Vector4 TextDisabled,
        Vector4 GreenDark, Vector4 RedDark, Vector4 YellowDark, Vector4 NeutralDark, Vector4 ButtonText,
        Vector4 ChipBg, Vector4 ChipFg, Vector4 StateChipBg, Vector4 StateChipFg, Vector4 JobChipBg, Vector4 JobChipFg,
        Vector4 BarBg, Vector4 OnAccent);

    /// <summary>Day: wheat-paper body, ink text.</summary>
    public static readonly Palette Day = new(
        BgDeep: C(0xF4F0E4), BgPanel: C(0xFAF8F1), BgRow: C(0xEAE3CF), TitleBg: C(0xE6DCC2), Border: C(0xCFC3A3),
        Accent: C(0x5B7F2A), AccentDim: C(0x46641F), AccentWash: C(0x5B7F2A, 0.12f),
        WakeFoam: C(0x1F6E8C), WakeDim: C(0x3F8FAF), WakeWash: C(0x1F6E8C, 0.10f),
        StatusGreen: C(0x2E8B57), StatusYellow: C(0xB8860B), StatusRed: C(0xC0392B), StatusGrey: C(0x9A9178),
        TextPrimary: C(0x2A2618), TextSecondary: C(0x6A6148), TextDisabled: C(0x9A9178),
        GreenDark: C(0x2E8B57), RedDark: C(0xC0392B), YellowDark: C(0xE3B94D), NeutralDark: C(0xFAF8F1), ButtonText: C(0xFFFFFF),
        ChipBg: C(0xE9E1C8), ChipFg: C(0x4A4128), StateChipBg: C(0xDCEBC8), StateChipFg: C(0x34511A), JobChipBg: C(0xF0E2C4), JobChipFg: C(0x7A5A1E),
        BarBg: C(0xE0D7BD), OnAccent: C(0xFFFFFF));

    /// <summary>Dusk: dark soil-green body, light text.</summary>
    public static readonly Palette Dusk = new(
        BgDeep: C(0x1E231A), BgPanel: C(0x283023), BgRow: C(0x2D3727), TitleBg: C(0x171C14), Border: C(0x3A4632),
        Accent: C(0x9CC25A), AccentDim: C(0x4E6B2A), AccentWash: C(0x9CC25A, 0.12f),
        WakeFoam: C(0xBFE3F2), WakeDim: C(0x7FB3C8), WakeWash: C(0xBFE3F2, 0.08f),
        StatusGreen: C(0x3FCB73), StatusYellow: C(0xE0B84A), StatusRed: C(0xE0604F), StatusGrey: C(0x7A846A),
        TextPrimary: C(0xEEF0E4), TextSecondary: C(0xB3BA9E), TextDisabled: C(0x7A846A),
        GreenDark: C(0x2E8B57), RedDark: C(0xB03A2E), YellowDark: C(0xC99A2E), NeutralDark: C(0x3A4632), ButtonText: C(0xEEF0E4),
        ChipBg: C(0x2D3727), ChipFg: C(0xD6DCC0), StateChipBg: C(0x2A4A1F), StateChipFg: C(0xB8E08F), JobChipBg: C(0x4A3D22), JobChipFg: C(0xF0D9A0),
        BarBg: C(0x2D3727), OnAccent: C(0x1E231A));

    private static Palette _p = Day;

    public static ThemeMode Mode { get; private set; } = ThemeMode.Day;

    public static void SetMode(ThemeMode mode)
    {
        Mode = mode;
        _p = mode == ThemeMode.Dusk ? Dusk : Day;
    }

    public static Palette Current => _p;

    // ── forwarding properties: the vocabulary every window uses ──
    public static Vector4 BgDeep => _p.BgDeep;
    public static Vector4 BgPanel => _p.BgPanel;
    public static Vector4 BgRow => _p.BgRow;
    public static Vector4 Border => _p.Border;
    public static Vector4 Accent => _p.Accent;
    public static Vector4 AccentDim => _p.AccentDim;
    public static Vector4 AccentWash => _p.AccentWash;
    public static Vector4 WakeFoam => _p.WakeFoam;
    public static Vector4 WakeDim => _p.WakeDim;
    public static Vector4 WakeWash => _p.WakeWash;
    public static Vector4 StatusGreen => _p.StatusGreen;
    public static Vector4 StatusYellow => _p.StatusYellow;
    public static Vector4 StatusRed => _p.StatusRed;
    public static Vector4 StatusGrey => _p.StatusGrey;
    public static Vector4 TextPrimary => _p.TextPrimary;
    public static Vector4 TextSecondary => _p.TextSecondary;
    public static Vector4 TextDisabled => _p.TextDisabled;
    public static Vector4 PeerOnline => _p.StatusGreen;
    public static Vector4 PeerStale => _p.StatusYellow;
    public static Vector4 PeerGone => _p.StatusGrey;
    public static Vector4 GreenDark => _p.GreenDark;
    public static Vector4 RedDark => _p.RedDark;
    public static Vector4 YellowDark => _p.YellowDark;
    public static Vector4 NeutralDark => _p.NeutralDark;

    private static Vector4 C(uint rgb, float a = 1f)
        => new(((rgb >> 16) & 0xFF) / 255f, ((rgb >> 8) & 0xFF) / 255f, (rgb & 0xFF) / 255f, a);

    private static Vector4 Scale(Vector4 c, float f)
        => new(System.Math.Clamp(c.X * f, 0f, 1f), System.Math.Clamp(c.Y * f, 0f, 1f), System.Math.Clamp(c.Z * f, 0f, 1f), c.W);

    // ── window chrome ──

    /// <summary>Push the palette into ImGui's window/frame/button colours. Returns the count for <see cref="ImGui.PopStyleColor(int)"/>.</summary>
    public static int PushWindowColors()
    {
        var p = _p;
        var n = 0;
        void Push(ImGuiCol col, Vector4 v) { ImGui.PushStyleColor(col, v); n++; }
        Push(ImGuiCol.WindowBg, p.BgDeep);
        Push(ImGuiCol.ChildBg, p.BgPanel);
        Push(ImGuiCol.PopupBg, p.BgPanel);
        Push(ImGuiCol.Border, p.Border);
        Push(ImGuiCol.TitleBg, p.TitleBg);
        Push(ImGuiCol.TitleBgActive, p.TitleBg);
        Push(ImGuiCol.TitleBgCollapsed, p.TitleBg);
        Push(ImGuiCol.MenuBarBg, p.TitleBg);
        Push(ImGuiCol.Text, p.TextPrimary);
        Push(ImGuiCol.TextDisabled, p.TextDisabled);
        Push(ImGuiCol.FrameBg, p.BgRow);
        Push(ImGuiCol.FrameBgHovered, Scale(p.BgRow, Mode == ThemeMode.Day ? 0.95f : 1.15f));
        Push(ImGuiCol.FrameBgActive, Scale(p.BgRow, Mode == ThemeMode.Day ? 0.90f : 1.25f));
        Push(ImGuiCol.Button, p.NeutralDark);
        Push(ImGuiCol.ButtonHovered, Scale(p.NeutralDark, Mode == ThemeMode.Day ? 0.94f : 1.2f));
        Push(ImGuiCol.ButtonActive, Scale(p.NeutralDark, Mode == ThemeMode.Day ? 0.88f : 1.35f));
        Push(ImGuiCol.Header, p.AccentWash);
        Push(ImGuiCol.HeaderHovered, p.AccentWash);
        Push(ImGuiCol.HeaderActive, p.AccentWash);
        Push(ImGuiCol.Separator, p.Border);
        Push(ImGuiCol.CheckMark, p.Accent);
        Push(ImGuiCol.SliderGrab, p.Accent);
        Push(ImGuiCol.SliderGrabActive, p.AccentDim);
        Push(ImGuiCol.ScrollbarBg, p.BgDeep);
        Push(ImGuiCol.ScrollbarGrab, p.Border);
        Push(ImGuiCol.TableHeaderBg, p.BgRow);
        Push(ImGuiCol.TableRowBg, p.BgDeep);
        Push(ImGuiCol.TableRowBgAlt, p.BgPanel);
        Push(ImGuiCol.TableBorderLight, p.Border);
        Push(ImGuiCol.TableBorderStrong, p.Border);
        Push(ImGuiCol.ResizeGrip, p.Border);
        Push(ImGuiCol.Tab, p.BgRow);
        Push(ImGuiCol.TabHovered, p.AccentWash);
        Push(ImGuiCol.TabActive, p.AccentWash);
        return n;
    }

    // ── shared widgets ──

    /// <summary>
    /// Accent-coloured section header. This Dalamud ImGui binding has no SeparatorText, so it is
    /// hand-drawn: coloured label with a hairline continuing to the right edge (family pattern).
    /// </summary>
    public static void SectionHeader(string label) => SectionHeader(label, Accent);

    /// <summary>Section header in an explicit colour — pass <see cref="WakeFoam"/> for Wake panels.</summary>
    public static void SectionHeader(string label, Vector4 color)
    {
        ImGui.Spacing();
        ImGui.TextColored(color, label);
        var min = ImGui.GetItemRectMin();
        var max = ImGui.GetItemRectMax();
        var lineY = (min.Y + max.Y) / 2f;
        var lineStart = new Vector2(max.X + 8f, lineY);
        var lineEnd = new Vector2(
            ImGui.GetWindowPos().X + ImGui.GetWindowWidth() - ImGui.GetStyle().WindowPadding.X, lineY);
        if (lineEnd.X > lineStart.X)
            ImGui.GetWindowDrawList().AddLine(lineStart, lineEnd, ImGui.ColorConvertFloat4ToU32(Border), 1f);
        ImGui.Spacing();
    }

    // ── cards ──
    // These bindings predate auto-resizing children, so a card is drawn behind its content: split
    // the draw list, lay the content out in a group on the top channel, then paint the panel and
    // border underneath on the bottom one. Not nestable, which no window here needs.
    private static Vector2 _cardOrigin;
    private static float _cardRight;
    private static bool _inCard;
    private const float CardPadX = 10f;
    private const float CardPadY = 8f;

    /// <summary>Open a panel card; close with <see cref="EndCard"/>. Content is inset by the pad.</summary>
    public static void BeginCard(string id)
    {
        var dl = ImGui.GetWindowDrawList();
        dl.ChannelsSplit(2);
        dl.ChannelsSetCurrent(1);
        _cardOrigin = ImGui.GetCursorScreenPos();
        _cardRight = _cardOrigin.X + ImGui.GetContentRegionAvail().X;
        ImGui.SetCursorScreenPos(_cardOrigin + new Vector2(CardPadX, CardPadY));
        ImGui.BeginGroup();
        ImGui.PushID(id);
        _inCard = true;
    }

    /// <summary>
    /// Same line as the last item when the next one, this wide, still fits inside the card (or the
    /// window, outside one); otherwise it starts the next line. A row of buttons wraps instead of
    /// running off the card's edge in a narrow window or the edge tab's pop-out.
    /// </summary>
    public static void FlowSameLine(float nextWidth, float spacing = -1f)
    {
        var gap = spacing >= 0f ? spacing : ImGui.GetStyle().ItemSpacing.X;
        var right = _inCard ? _cardRight - CardPadX : ImGui.GetWindowPos().X + ImGui.GetWindowContentRegionMax().X;
        if (ImGui.GetItemRectMax().X + gap + nextWidth <= right)
            ImGui.SameLine(0f, gap);
    }

    /// <summary>How wide an <see cref="IconTextButton"/> with no fixed size comes out.</summary>
    public static float IconTextWidth(Dalamud.Interface.FontAwesomeIcon icon, string text)
        => Dalamud.Interface.Components.ImGuiComponents.GetIconButtonWithTextWidth(icon, text);

    public static void EndCard()
    {
        _inCard = false;
        ImGui.PopID();
        ImGui.EndGroup();
        var bottom = ImGui.GetItemRectMax().Y + CardPadY;
        var dl = ImGui.GetWindowDrawList();
        dl.ChannelsSetCurrent(0);
        var max = new Vector2(_cardRight, bottom);
        dl.AddRectFilled(_cardOrigin, max, ImGui.ColorConvertFloat4ToU32(_p.BgPanel), 4f);
        dl.AddRect(_cardOrigin, max, ImGui.ColorConvertFloat4ToU32(_p.Border), 4f);
        dl.ChannelsMerge();
        ImGui.SetCursorScreenPos(new Vector2(_cardOrigin.X, bottom + 6f));
        ImGui.Dummy(System.Numerics.Vector2.Zero); // registers the cursor move with the layout
    }

    /// <summary>Coloured status dot + label.</summary>
    public static void StatusDot(bool active, string activeLabel = "Active", string inactiveLabel = "Idle")
    {
        ImGui.TextColored(active ? StatusGreen : StatusGrey, "●");
        ImGui.SameLine(0f, 4f);
        ImGui.TextColored(active ? StatusGreen : TextSecondary, active ? activeLabel : inactiveLabel);
    }

    /// <summary>Dependency chip — green when the plugin is present, red (hard) or grey (soft) when missing.</summary>
    public static void DependencyChip(string label, bool available, bool required = true)
    {
        var missingColor = required ? StatusRed : StatusGrey;
        ImGui.TextColored(available ? StatusGreen : missingColor, available ? "●" : "○");
        ImGui.SameLine(0f, 4f);
        ImGui.TextColored(available ? TextPrimary : missingColor, label);
    }

    /// <summary>A small rounded pill of text — quest id, job, state.</summary>
    public static void Chip(string text, Vector4 bg, Vector4 fg)
    {
        var pad = new Vector2(7f, 1f);
        var size = ImGui.CalcTextSize(text) + pad * 2f;
        var pos = ImGui.GetCursorScreenPos();
        var draw = ImGui.GetWindowDrawList();
        draw.AddRectFilled(pos, pos + size, ImGui.ColorConvertFloat4ToU32(bg), size.Y / 2f);
        ImGui.SetCursorScreenPos(pos + pad);
        ImGui.TextColored(fg, text);
        ImGui.SetCursorScreenPos(new Vector2(pos.X + size.X, pos.Y));
        ImGui.Dummy(new Vector2(0f, size.Y));
        ImGui.SameLine(0f, 0f);
        ImGui.SetCursorScreenPos(new Vector2(pos.X + size.X, pos.Y));
    }

    public static void IdChip(string text) => Chip(text, _p.ChipBg, _p.ChipFg);
    public static void StateChip(string text) => Chip(text, _p.StateChipBg, _p.StateChipFg);
    public static void JobChip(string text) => Chip(text, _p.JobChipBg, _p.JobChipFg);

    /// <summary>Filled badge for the step kind.</summary>
    public static void KindBadge(string text)
    {
        var pad = new Vector2(6f, 1f);
        var size = ImGui.CalcTextSize(text) + pad * 2f;
        var pos = ImGui.GetCursorScreenPos();
        ImGui.GetWindowDrawList().AddRectFilled(pos, pos + size, ImGui.ColorConvertFloat4ToU32(Accent), 3f);
        ImGui.SetCursorScreenPos(pos + pad);
        ImGui.TextColored(_p.OnAccent, text);
        ImGui.SetCursorScreenPos(new Vector2(pos.X + size.X, pos.Y));
        ImGui.Dummy(new Vector2(0f, size.Y));
        ImGui.SameLine(0f, 0f);
        ImGui.SetCursorScreenPos(new Vector2(pos.X + size.X, pos.Y));
    }

    /// <summary>Thin progress bar in the accent.</summary>
    public static void ProgressBar(float fraction, float height = 4f)
    {
        var width = ImGui.GetContentRegionAvail().X;
        var pos = ImGui.GetCursorScreenPos();
        var draw = ImGui.GetWindowDrawList();
        draw.AddRectFilled(pos, pos + new Vector2(width, height), ImGui.ColorConvertFloat4ToU32(_p.BarBg), height / 2f);
        var f = System.Math.Clamp(fraction, 0f, 1f);
        if (f > 0f)
            draw.AddRectFilled(pos, pos + new Vector2(width * f, height), ImGui.ColorConvertFloat4ToU32(Accent), height / 2f);
        ImGui.Dummy(new Vector2(width, height + 4f));
    }

    /// <summary>Solid-fill button in a base colour, brighter on hover, darker when pressed.</summary>
    public static bool SolidButton(string label, Vector4 baseColor, Vector2 size)
    {
        ImGui.PushStyleColor(ImGuiCol.Button, baseColor);
        ImGui.PushStyleColor(ImGuiCol.ButtonHovered, Scale(baseColor, 1.15f));
        ImGui.PushStyleColor(ImGuiCol.ButtonActive, Scale(baseColor, 0.85f));
        ImGui.PushStyleColor(ImGuiCol.Text, _p.ButtonText);
        try
        {
            return ImGui.Button(label, size);
        }
        finally
        {
            ImGui.PopStyleColor(4);
        }
    }

    public static bool StartButton(string label, Vector2 size) => SolidButton(label, GreenDark, size);

    public static bool StopButton(string label, Vector2 size) => SolidButton(label, RedDark, size);

    /// <summary>A toggle that is yellow while armed and neutral otherwise ("stop after this quest").</summary>
    public static bool ArmedButton(string label, bool armed, Vector2 size)
    {
        if (!armed)
            return ImGui.Button(label, size);
        ImGui.PushStyleColor(ImGuiCol.Button, YellowDark);
        ImGui.PushStyleColor(ImGuiCol.ButtonHovered, Scale(YellowDark, 1.1f));
        ImGui.PushStyleColor(ImGuiCol.ButtonActive, Scale(YellowDark, 0.9f));
        ImGui.PushStyleColor(ImGuiCol.Text, C(0x2B2200));
        try
        {
            return ImGui.Button(label, size);
        }
        finally
        {
            ImGui.PopStyleColor(4);
        }
    }

    /// <summary>
    /// Full-width call-to-action button in the accent.
    /// </summary>
    public static bool AccentButton(string label, float height = 30f)
        => SolidButton(label, AccentDim, new Vector2(-1f, height));

    // ── icon buttons (FontAwesome via Dalamud — the game font has no ▶ ■ ⏭ ✎ ⚙; those render as tofu) ──

    /// <summary>Icon-only button in the palette's neutral colour, with an id suffix for uniqueness.</summary>
    public static bool IconButton(string id, Dalamud.Interface.FontAwesomeIcon icon, string tooltip, Vector2? size = null)
    {
        var clicked = Dalamud.Interface.Components.ImGuiComponents.IconButton(id, icon, NeutralDark,
            Scale(NeutralDark, Mode == ThemeMode.Day ? 0.88f : 1.35f), Scale(NeutralDark, Mode == ThemeMode.Day ? 0.94f : 1.2f), size);
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip(tooltip);
        return clicked;
    }

    /// <summary>Icon + text button in a solid colour (green go, red stop, yellow armed, neutral).</summary>
    public static bool IconTextButton(Dalamud.Interface.FontAwesomeIcon icon, string text, Vector4 baseColor, string tooltip, Vector2? size = null, bool darkText = false)
    {
        ImGui.PushStyleColor(ImGuiCol.Text, darkText ? C(0x2B2200) : baseColor == NeutralDark ? TextPrimary : _p.ButtonText);
        bool clicked;
        try
        {
            clicked = Dalamud.Interface.Components.ImGuiComponents.IconButtonWithText(icon, text, baseColor, Scale(baseColor, 0.85f), Scale(baseColor, 1.15f), size);
        }
        finally
        {
            ImGui.PopStyleColor();
        }
        if (tooltip.Length > 0 && ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip(tooltip);
        return clicked;
    }

    /// <summary>Wrapped paragraph in a colour — <see cref="ImGui.TextWrapped"/> has no coloured overload.</summary>
    public static void TextWrappedColored(Vector4 color, string text)
    {
        ImGui.PushStyleColor(ImGuiCol.Text, color);
        try
        {
            ImGui.TextWrapped(text);
        }
        finally
        {
            ImGui.PopStyleColor();
        }
    }

    /// <summary>Hover "(?)" tooltip for a non-obvious control.</summary>
    public static void HelpMarker(string text)
    {
        ImGui.SameLine();
        ImGui.TextDisabled("(?)");
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(text);
    }
}

/// <summary>Minimal scoped-disable helper (Dalamud's ImRaii is not exposed by this binding).</summary>
internal static class ImRaii
{
    public static DisabledScope Disabled(bool disabled) => new(disabled);

    internal readonly struct DisabledScope : System.IDisposable
    {
        private readonly bool _active;

        public DisabledScope(bool active)
        {
            _active = active;
            if (_active)
                ImGui.BeginDisabled();
        }

        public void Dispose()
        {
            if (_active)
                ImGui.EndDisabled();
        }
    }
}

/// <summary>Base for every Demeter window: the palette goes into ImGui's own colours around each draw.</summary>
public abstract class DemeterWindow : Dalamud.Interface.Windowing.Window
{
    private int _pushed;

    protected DemeterWindow(string name) : base(name) { }

    public override void PreDraw() => _pushed = DemeterTheme.PushWindowColors();

    public override void PostDraw()
    {
        if (_pushed > 0)
            ImGui.PopStyleColor(_pushed);
        _pushed = 0;
    }
}
