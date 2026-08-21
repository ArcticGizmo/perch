using Avalonia;
using Avalonia.Media;
using Perch.Avalonia.Rendering;

namespace Perch.Avalonia.Views;

/// <summary>
/// The "Plugins" section — a glyph per installed, enabled third-party plugin (fed by the plugin monitor
/// host), sitting below the daemon strip. Follows the standard collapsible-section pattern (see the Todo
/// and Social/Friends regions and the CLAUDE.md convention note): a header row with a chevron, a caption,
/// a count, and a right-hand "+" that opens the Plugins settings page; a body of one row per plugin
/// (glyph + short text + the plugin name). Clicking the header collapses/expands (persisted); clicking a
/// body line opens the Plugins page.
///
/// Stays in the measure-or-paint discipline: <see cref="PluginsStripHeight"/> feeds <c>PanelBodyHeight</c>
/// and the paint advances the same cursor, so measured height and painted layout can't drift.
/// </summary>
public sealed partial class OverlayCanvas
{
    /// <summary>A plugin reduced to what the strip paints — toolkit-neutral strings so the canvas needs no
    /// reference to the <c>Perch.Plugins</c> types. <see cref="Ok"/> false dims the row (a faulted run).</summary>
    internal readonly record struct PluginBadge(
        string Id, string Name, string Glyph, string Text, string Tooltip, bool Ok);

    private const double PluginTextSize = 11;
    private const double PluginGlyphSize = 12;

    private IReadOnlyList<PluginBadge> _pluginBadges = [];
    private int _hoveredPluginRow = -1;
    private bool _hoveredPluginHeader, _hoveredPluginAdd;
    private bool _pluginsExpanded = true;

    private Rect _pluginHeaderRect, _pluginAddRect;
    private static double? _pluginRowH;

    /// <summary>Raised when the "+" or a body line is clicked — the App opens the Plugins settings page.</summary>
    public event Action? PluginsRequested;

    /// <summary>Raised when the chevron toggles the section, so the App can persist the state.</summary>
    public event Action<bool>? PluginsExpandChanged;

    // ── metrics ──────────────────────────────────────────────────────────────────────
    private double PluginHeaderHeight => FeedCaptionHeight + 12;       // matches the Todo/Friends header band
    private double PluginRowHeight => _pluginRowH ??= OverlayDraw.Text("Xg", PluginTextSize, FgBrush).Height + 6;
    private double PluginsBodyHeight => !_pluginsExpanded ? 0 : _pluginBadges.Count * PluginRowHeight + 6;

    /// <summary>The section only exists when at least one plugin is producing a badge (the master kill
    /// switch and per-plugin consent are resolved upstream, so "has badges" == "has something to show").</summary>
    private bool PluginsStripVisible => _pluginBadges.Count > 0;

    private double PluginsStripHeight => !PluginsStripVisible ? 0 : PluginHeaderHeight + PluginsBodyHeight;

    // ── data + state seeding (called by the App / monitor host, on the UI thread) ──────
    /// <summary>Seeds the expand state once at wire-up, without raising <see cref="PluginsExpandChanged"/>.</summary>
    public void SetPluginsExpanded(bool expanded)
    {
        if (_pluginsExpanded == expanded) return;
        _pluginsExpanded = expanded;
        if (PluginsStripVisible) RemeasurePanel();
    }

    /// <summary>Pushes the current set of plugin badges. Relayouts when the section's height changes,
    /// otherwise repaints in place.</summary>
    internal void SetPluginBadges(IReadOnlyList<PluginBadge> badges)
    {
        bool beforeVisible = PluginsStripVisible;
        int beforeCount = _pluginBadges.Count;
        _pluginBadges = badges ?? [];
        _hoveredPluginRow = -1;

        if (beforeVisible != PluginsStripVisible || (_pluginsExpanded && _pluginBadges.Count != beforeCount))
            RemeasurePanel();
        else if (PluginsStripVisible)
            InvalidateVisual();
    }

    private void OnPluginHeaderClicked()
    {
        _pluginsExpanded = !_pluginsExpanded;
        PluginsExpandChanged?.Invoke(_pluginsExpanded);
        RemeasurePanel();
    }

    private void ClearPluginsHitRects()
    {
        _pluginHeaderRect = default;
        _pluginAddRect = default;
    }

    // ── hit-testing ────────────────────────────────────────────────────────────────
    // Anchored on the header rect captured at paint, so the hit-map can't drift from the layout.
    private int HitTestPluginRow(Point p)
    {
        if (!(ShowFullPanel && PluginsStripVisible && _pluginsExpanded)) return -1;
        if (_pluginHeaderRect.Width <= 0) return -1;

        double top = _pluginHeaderRect.Bottom;
        double lineH = PluginRowHeight;
        int count = _pluginBadges.Count;
        if (p.Y < top || p.Y >= top + count * lineH) return -1;

        int index = (int)((p.Y - top) / lineH);
        return index >= 0 && index < count ? index : -1;
    }

    // ── paint ────────────────────────────────────────────────────────────────────────
    private void DrawPluginsStrip(DrawingContext ctx, double width, double top)
    {
        DrawPluginHeader(ctx, width, top);
        if (!_pluginsExpanded) return;

        double lineH = PluginRowHeight;
        double rowTop = top + PluginHeaderHeight;
        for (int i = 0; i < _pluginBadges.Count; i++)
        {
            var badge = _pluginBadges[i];
            double midY = rowTop + i * lineH + lineH / 2;

            if (i == _hoveredPluginRow)
                OverlayDraw.Panel(ctx, new Rect(HorizPad - 4, midY - lineH / 2 + 1, width - 2 * (HorizPad - 4), lineH - 2),
                    FeedHoverBrush, null, 5);

            var textBrush = badge.Ok ? FgBrush : MutedBrush;
            double x = HorizPad + 6;

            if (!string.IsNullOrEmpty(badge.Glyph))
            {
                var g = OverlayDraw.Emoji(badge.Glyph, PluginGlyphSize, textBrush);
                OverlayDraw.TextLeftMid(ctx, g, x, midY);
                x += g.Width + 6;
            }
            if (!string.IsNullOrEmpty(badge.Text))
            {
                var t = OverlayDraw.Text(badge.Text, PluginTextSize, textBrush, FontWeight.SemiBold);
                OverlayDraw.TextLeftMid(ctx, t, x, midY);
                x += t.Width + 8;
            }

            // The plugin name, muted, filling the remaining width (ellipsised if long).
            double nameMax = width - HorizPad - x;
            if (nameMax > 20 && !string.IsNullOrEmpty(badge.Name))
            {
                var n = OverlayDraw.Truncate(badge.Name, FeedCaptionSize, nameMax, FontWeight.Normal);
                OverlayDraw.TextLeftMid(ctx, OverlayDraw.Text(n, FeedCaptionSize, MutedBrush), x, midY);
            }
        }
    }

    private void DrawPluginHeader(DrawingContext ctx, double width, double top)
    {
        double midY = top + 6 + FeedCaptionHeight / 2;
        if (_hoveredPluginHeader)
            OverlayDraw.Panel(ctx, new Rect(HorizPad - 4, top + 3, width - 2 * (HorizPad - 4), PluginHeaderHeight - 6),
                FeedHoverBrush, null, 6);

        DrawChevron(ctx, HorizPad + 4, midY, _pluginsExpanded);
        var cap = OverlayDraw.Text("Plugins", FeedCaptionSize, MutedBrush, FontWeight.SemiBold);
        OverlayDraw.TextLeftMid(ctx, cap, HorizPad + 14, midY);

        const double addBox = 18;
        double addCx = width - HorizPad - addBox / 2 + 2;
        var addRect = new Rect(addCx - addBox / 2, midY - addBox / 2, addBox, addBox);
        if (_hoveredPluginAdd) OverlayDraw.Panel(ctx, addRect, FeedHoverBrush, null, 5);
        DrawPlusGlyph(ctx, _hoveredPluginAdd ? FgBrush : MutedBrush, addCx, midY);
        _pluginAddRect = addRect;

        // Collapsed → still say how many plugins are active, left of the "+".
        if (!_pluginsExpanded && _pluginBadges.Count > 0)
        {
            var nFt = OverlayDraw.Text($"{_pluginBadges.Count}", FeedCaptionSize, MutedBrush);
            OverlayDraw.TextLeftMid(ctx, nFt, addRect.Left - 10 - nFt.Width, midY);
        }

        _pluginHeaderRect = new Rect(0, top, width, PluginHeaderHeight);
    }
}
