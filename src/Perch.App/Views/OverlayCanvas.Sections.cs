using Avalonia.Media;
using Perch.Data;

namespace Perch.Avalonia.Views;

/// <summary>
/// The overlay panel's section ordering — the single source of truth for the vertical sequence of movable
/// sections (system info, claude metrics, quick links, hypertree, todo, sessions, friends, media, call). The
/// header (top) and outage status bar (bottom) are fixed chrome and are laid out directly by <c>Draw</c>, so
/// they aren't members here.
///
/// <para>Both the measure pass (<c>PanelBodyHeight</c>) and the paint pass (<c>Draw</c>) walk
/// <see cref="_sectionOrder"/> through the one <see cref="SectionVisible"/>/<see cref="SectionHeight"/>/
/// <see cref="PaintSection"/> triple, recording each visible section's top in <see cref="_sectionTop"/>. The
/// old chained <c>…Top</c> getters now read that dictionary, so hit-testing follows the painted layout no
/// matter the order — the same measure-or-paint discipline the rest of the overlay uses.</para>
/// </summary>
public sealed partial class OverlayCanvas
{
    // The effective top-to-bottom order of the movable sections (normalized), and the absolute top Y of each
    // visible section, repopulated on every measure/paint. Seeded to the default; SetSectionOrder overrides it.
    private OverlaySection[] _sectionOrder = OverlaySectionOrder.Default.ToArray();
    private readonly Dictionary<OverlaySection, double> _sectionTop = new();

    /// <summary>Sets the vertical order of the movable sections (from AppSettings via OverlaySettingsGates).
    /// The list is normalized (unknown/duplicate dropped, missing spliced in) so a stale file can't drop a
    /// section. Changing the order relayouts the panel.</summary>
    public void SetSectionOrder(IReadOnlyList<OverlaySection> order)
    {
        var arr = OverlaySectionOrder.Normalize(order).ToArray();
        if (_sectionOrder.AsSpan().SequenceEqual(arr)) return;
        _sectionOrder = arr;
        RemeasurePanel();
    }

    // Whether a section contributes to the panel at all. Uses the same gates the individual strips already
    // expose; Sessions is always present (an empty roster simply contributes its rows-plus-daemon block, which
    // may be near-empty). In Rearrange mode every gate is forced on (see OverlayCanvas.Rearrange.cs), so all
    // nine sections are visible and can be positioned.
    private bool SectionVisible(OverlaySection s) => s switch
    {
        OverlaySection.SystemInfo    => _showSystemMetrics,
        OverlaySection.ClaudeMetrics => UsageStripVisible,
        OverlaySection.QuickLinks    => HasQuickLinksRow,
        OverlaySection.Hypertree     => HypertreeStripVisible,
        OverlaySection.Todo          => TodosStripVisible,
        OverlaySection.Sessions      => true,
        OverlaySection.Friends       => FeedStripVisible || SocialSignInStripVisible,
        OverlaySection.Media         => MediaStripVisible,
        OverlaySection.Call          => MicStripVisible,
        _ => false,
    };

    // The height a visible section occupies. Only ever called for a section SectionVisible has cleared, so the
    // strip getters return their real (non-zero) heights.
    private double SectionHeight(OverlaySection s) => s switch
    {
        OverlaySection.SystemInfo    => SysMetricsStripHeight,
        OverlaySection.ClaudeMetrics => UsageStripHeight,
        OverlaySection.QuickLinks    => QuickLinksRowHeight,
        OverlaySection.Hypertree     => HypertreeStripHeight,
        OverlaySection.Todo          => TodosStripHeight,
        OverlaySection.Sessions      => SessionsSectionHeight,
        OverlaySection.Friends       => FriendsSectionHeight,
        OverlaySection.Media         => MediaStripHeight,
        OverlaySection.Call          => MicStripHeight,
        _ => 0,
    };

    // The session rows plus the daemon-worker strip that rides directly beneath them, and the 2px trailing gap
    // the old PanelBodyHeight added after the block — kept as one movable unit.
    private double SessionsSectionHeight
    {
        get
        {
            double h = 0;
            foreach (var r in _rows) h += HeightOf(r);
            if (DaemonStripVisible) h += DaemonStripHeight;
            return h + 2;
        }
    }

    // The Social region, or the sign-in prompt in the complementary (signed-out) state — never both.
    private double FriendsSectionHeight
        => FeedStripVisible ? FeedStripHeight
         : SocialSignInStripVisible ? SocialStripHeight
         : 0;

    // The next visible section after s in the current order, or null if s is last (used for adjacency-sensitive
    // chrome like the system-metrics/usage divider, which only reads right when the two are neighbours).
    private OverlaySection? NextVisibleSection(OverlaySection s)
    {
        int i = Array.IndexOf(_sectionOrder, s);
        if (i < 0) return null;
        for (int j = i + 1; j < _sectionOrder.Length; j++)
            if (SectionVisible(_sectionOrder[j])) return _sectionOrder[j];
        return null;
    }

    // Paints one section at its computed top, dimmed when Rearrange mode is showing it as a disabled section.
    private void PaintSection(DrawingContext ctx, double width, OverlaySection s, double top)
    {
        if (RearrangeMode && _rearrangeDimmed.Contains(s))
        {
            using (ctx.PushOpacity(RearrangeDimOpacity))
                PaintSectionCore(ctx, width, s, top);
        }
        else
        {
            PaintSectionCore(ctx, width, s, top);
        }
    }

    private void PaintSectionCore(DrawingContext ctx, double width, OverlaySection s, double top)
    {
        switch (s)
        {
            // These four read their own top through the _sectionTop-backed getters, so they need no argument.
            case OverlaySection.SystemInfo:    DrawSystemMetricsStrip(ctx, width); break;
            case OverlaySection.ClaudeMetrics: DrawUsageBars(ctx, width); break;
            case OverlaySection.QuickLinks:    DrawQuickLinksRow(ctx, width); break;
            case OverlaySection.Hypertree:     DrawHypertreeStrip(ctx, width); break;
            case OverlaySection.Todo:          DrawTodosStrip(ctx, width, top); break;
            case OverlaySection.Sessions:      PaintSessions(ctx, width, top); break;
            case OverlaySection.Friends:
                if (FeedStripVisible) DrawSocialRegion(ctx, width, top);
                else if (SocialSignInStripVisible) DrawSocialSignInStrip(ctx, width, top);
                break;
            case OverlaySection.Media:         DrawMediaStrip(ctx, width, top); break;
            case OverlaySection.Call:          DrawMicStrip(ctx, width, top); break;
        }
    }

    // The Sessions unit: one line per display row, then the daemon-worker strip directly beneath.
    private void PaintSessions(DrawingContext ctx, double width, double top)
    {
        _autonomousHeaderRect = default; // re-armed below only when the section is actually drawn
        double y = top;
        for (int i = 0; i < _rows.Count; i++)
        {
            var r = _rows[i];
            if (r.IsSectionHeader) DrawSectionHeaderRow(ctx, r, y, width);
            else if (r.IsSubAgent) DrawSubAgentRow(ctx, i, r, y, width);
            else DrawSessionRow(ctx, i, r.Session!, y, width);
            y += HeightOf(r);
        }

        if (DaemonStripVisible) DrawDaemonStrip(ctx, width, y);
    }
}
