using Avalonia;
using Avalonia.Media;
using Perch.Avalonia.Rendering;
using Perch.Avalonia.Theming;
using Perch.Data;

namespace Perch.Avalonia.Views;

/// <summary>
/// The overlay's GitHub alerts strip: a single line saying how many of your open pull requests need you
/// ("3 PRs need you"), then one small symbol + count per kind of reason on the right (an eye for reviews
/// requested, a speech bubble for new comments, a tick for ready to merge …), or that none do. The whole band is
/// one control — clicking it opens the alerts window, which lists them by repo with the reason for each; dwelling
/// on it shows a tooltip spelling the symbols out.
///
/// <para>Symbols rather than words so the line fits the panel's width however many kinds are waiting; any chip
/// that still doesn't fit is dropped behind a "…" (the tooltip and the window have the full picture). Deliberately
/// not a collapsible section: it is already one line. Fed a toolkit-neutral <see cref="GitHubStrip"/> by
/// <c>GitHubAlertsMonitorHost</c>, so the canvas never touches gh or the classifier. Its height is derived from the
/// font's line height (the Hypertree line metric), and the measure and paint passes share
/// <see cref="GitHubStripHeight"/>.</para>
/// </summary>
public sealed partial class OverlayCanvas
{
    /// <summary>Where the poll stands: no answer yet, a good answer, or a failure whose reason is
    /// <see cref="GitHubStrip.Detail"/>.</summary>
    internal enum GitHubStripStatus { Checking, Ok, Error }

    /// <summary>What the strip paints: the counts, the per-kind <see cref="Counts"/> drawn as symbol chips, and
    /// <see cref="Detail"/> — the error line when the poll failed.</summary>
    internal readonly record struct GitHubStrip(
        GitHubStripStatus Status, int NeedsYou, int Open, string Detail,
        IReadOnlyList<(GhAlertKind Kind, int Count)>? Counts = null);

    private const double GitHubGlyphW = 11;   // a status symbol's box
    private const double GitHubChipGap = 9;   // between one chip's count and the next symbol

    private bool _gitHubEnabled;
    private GitHubStrip _gitHub = new(GitHubStripStatus.Checking, 0, 0, "");
    private bool _hoveredGitHub;
    private Rect _gitHubRect;

    /// <summary>Raised when the strip is clicked; the app opens the GitHub alerts window.</summary>
    public event Action? GitHubAlertsRequested;

    private bool GitHubStripVisible => _gitHubEnabled;
    private double GitHubStripHeight => GitHubStripVisible ? HypertreeLineHeight + 12 : 0;

    /// <summary>Show/hide the strip (the "GitHub alerts" setting). Changes the panel height, so relayout.</summary>
    public void SetShowGitHubAlerts(bool enabled)
    {
        if (_gitHubEnabled == enabled) return;
        _gitHubEnabled = enabled;
        if (!enabled) _gitHub = new(GitHubStripStatus.Checking, 0, 0, "");
        RemeasurePanel();
    }

    /// <summary>Replaces the strip's contents (UI thread). One line whatever the state, so only a repaint.</summary>
    internal void SetGitHubStrip(GitHubStrip strip)
    {
        _gitHub = strip;
        if (GitHubStripVisible) InvalidateVisual();
    }

    private bool HitTestGitHub(Point p) => ShowFullPanel && GitHubStripVisible && _gitHubRect.Contains(p);

    /// <summary>The colour each reason kind reads in — shared by the strip's symbols and the window's pills.</summary>
    internal static Color GitHubKindColor(GhAlertKind k) => k switch
    {
        GhAlertKind.ChangesRequested or GhAlertKind.ChecksFailing => Palette.Red,
        GhAlertKind.Conflicts                                     => Palette.Orange,
        GhAlertKind.ReadyToMerge                                  => Palette.Green,
        GhAlertKind.NewActivity                                   => Palette.Yellow,
        _                                                         => Palette.Active.Accent.ToColor(),
    };

    private void DrawGitHubStrip(DrawingContext ctx, double width, double top)
    {
        double h = GitHubStripHeight;
        double midY = top + h / 2;
        var band = new Rect(HorizPad - 4, top + 3, Math.Max(0, width - 2 * (HorizPad - 4)), h - 6);
        _gitHubRect = new Rect(0, top, width, h);
        if (_hoveredGitHub) OverlayDraw.Panel(ctx, band, FeedHoverBrush, null, 6);

        bool needsYou = _gitHub is { Status: GitHubStripStatus.Ok, NeedsYou: > 0 };
        DrawPrIcon(ctx, HorizPad, midY, PrState.Open, PrChecksRollup.None, hovered: _hoveredGitHub);
        if (needsYou)
            DrawGlyphBadge(ctx, new Point(HorizPad + 13.5, midY - 5.5), OverlayDraw.Brush(AttentionColor));

        double textX = HorizPad + 22;
        double right = width - HorizPad;

        string primary;
        IBrush primaryBrush = MutedBrush;
        FontWeight weight = FontWeight.Normal;
        string detail = "";
        IBrush detailBrush = MutedBrush;
        switch (_gitHub.Status)
        {
            case GitHubStripStatus.Error:
                primary = "GitHub";
                detail = _gitHub.Detail;
                detailBrush = OverlayDraw.Brush(Palette.Yellow);
                break;
            case GitHubStripStatus.Ok when needsYou:
                int n = _gitHub.NeedsYou;
                primary = n == 1 ? "1 PR needs you" : $"{n} PRs need you";
                primaryBrush = FgBrush;
                weight = FontWeight.SemiBold;
                break;
            case GitHubStripStatus.Ok:
                primary = "No PRs need you";
                detail = _gitHub.Open == 1 ? "1 open" : $"{_gitHub.Open} open";
                break;
            default:
                primary = "GitHub";
                detail = "checking…";
                break;
        }
        if (_hoveredGitHub && !needsYou) primaryBrush = FgBrush;

        var primaryFt = OverlayDraw.Text(OverlayDraw.Truncate(primary, HyperRowSize, Math.Max(20, right - textX)),
            HyperRowSize, primaryBrush, weight);
        OverlayDraw.TextLeftMid(ctx, primaryFt, textX, midY);

        double avail = right - (textX + primaryFt.Width + 12);
        if (needsYou && _gitHub.Counts is { Count: > 0 } counts)
            DrawGitHubChips(ctx, counts, right, avail, midY);
        else if (detail.Length > 0 && avail > 24)
        {
            var detailFt = OverlayDraw.Text(OverlayDraw.Truncate(detail, HyperMetaSize, avail), HyperMetaSize, detailBrush);
            OverlayDraw.TextLeftMid(ctx, detailFt, right - detailFt.Width, midY);
        }
    }

    // The symbol + count chips, right-aligned in priority order. As many as fit in `avail` are drawn; if some don't,
    // the tail is dropped behind a muted "…" so the line never spills past the panel edge.
    private void DrawGitHubChips(DrawingContext ctx, IReadOnlyList<(GhAlertKind Kind, int Count)> counts,
        double right, double avail, double midY)
    {
        var chips = new List<(GhAlertKind Kind, FormattedText Count, double W)>(counts.Count);
        foreach (var (kind, count) in counts)
        {
            var ft = OverlayDraw.Text(count.ToString(), HyperMetaSize, OverlayDraw.Brush(GitHubKindColor(kind)), FontWeight.SemiBold);
            chips.Add((kind, ft, GitHubGlyphW + 2.5 + ft.Width));
        }

        var more = OverlayDraw.Text("…", HyperMetaSize, MutedBrush);

        // Width of the first k chips, plus the "…" when some are left out.
        double WidthOf(int k)
        {
            double w = 0;
            for (int i = 0; i < k; i++) w += (i > 0 ? GitHubChipGap : 0) + chips[i].W;
            if (k < chips.Count) w += (k > 0 ? GitHubChipGap : 0) + more.Width;
            return w;
        }

        int fit = chips.Count;
        while (fit > 0 && WidthOf(fit) > avail) fit--;
        double total = WidthOf(fit);
        if (total > avail) return;   // not even a lone "…" fits
        bool truncated = fit < chips.Count;

        double x = right - total;
        for (int i = 0; i < fit; i++)
        {
            var (kind, ft, w) = chips[i];
            DrawGitHubKindGlyph(ctx, kind, x + GitHubGlyphW / 2, midY);
            OverlayDraw.TextLeftMid(ctx, ft, x + GitHubGlyphW + 2.5, midY);
            x += w + GitHubChipGap;
        }
        if (truncated) OverlayDraw.TextLeftMid(ctx, more, x, midY);
    }

    // One reason kind's symbol, ~10px around (cx, cy), stroked in its colour:
    // eye = review requested · ± = changes requested · speech bubble = new activity · ⊗ = checks failing ·
    // warning triangle = conflicts · tick = ready to merge · person = assigned.
    internal static void DrawGitHubKindGlyph(DrawingContext ctx, GhAlertKind kind, double cx, double cy)
    {
        var brush = OverlayDraw.Brush(GitHubKindColor(kind));
        var pen = OverlayDraw.Pen(brush, 1.3, null, PenLineCap.Round, PenLineJoin.Round);
        switch (kind)
        {
            case GhAlertKind.ReviewRequested:
            {
                var eye = new StreamGeometry();
                using (var g = eye.Open())
                {
                    g.BeginFigure(new Point(cx - 5, cy), isFilled: false);
                    g.QuadraticBezierTo(new Point(cx, cy - 5.6), new Point(cx + 5, cy));
                    g.QuadraticBezierTo(new Point(cx, cy + 5.6), new Point(cx - 5, cy));
                    g.EndFigure(true);
                }
                ctx.DrawGeometry(null, pen, eye);
                ctx.DrawEllipse(brush, null, new Point(cx, cy), 1.6, 1.6);
                break;
            }
            case GhAlertKind.ChangesRequested:
                ctx.DrawLine(pen, new Point(cx - 3.5, cy - 2), new Point(cx + 3.5, cy - 2));
                ctx.DrawLine(pen, new Point(cx, cy - 5.5), new Point(cx, cy + 1.5));
                ctx.DrawLine(pen, new Point(cx - 3.5, cy + 4), new Point(cx + 3.5, cy + 4));
                break;
            case GhAlertKind.NewActivity:
            {
                ctx.DrawRectangle(null, pen, new RoundedRect(new Rect(cx - 5, cy - 4.5, 10, 7), 2));
                var tail = new StreamGeometry();
                using (var g = tail.Open())
                {
                    g.BeginFigure(new Point(cx - 2.5, cy + 2.5), isFilled: false);
                    g.LineTo(new Point(cx - 3.5, cy + 5));
                    g.LineTo(new Point(cx, cy + 2.5));
                    g.EndFigure(false);
                }
                ctx.DrawGeometry(null, pen, tail);
                break;
            }
            case GhAlertKind.ChecksFailing:
                ctx.DrawEllipse(null, pen, new Point(cx, cy), 4.6, 4.6);
                ctx.DrawLine(pen, new Point(cx - 2, cy - 2), new Point(cx + 2, cy + 2));
                ctx.DrawLine(pen, new Point(cx + 2, cy - 2), new Point(cx - 2, cy + 2));
                break;
            case GhAlertKind.Conflicts:
            {
                var tri = new StreamGeometry();
                using (var g = tri.Open())
                {
                    g.BeginFigure(new Point(cx, cy - 5), isFilled: false);
                    g.LineTo(new Point(cx + 5.2, cy + 4.2));
                    g.LineTo(new Point(cx - 5.2, cy + 4.2));
                    g.EndFigure(true);
                }
                ctx.DrawGeometry(null, pen, tri);
                ctx.DrawLine(pen, new Point(cx, cy - 1.6), new Point(cx, cy + 0.8));
                ctx.DrawEllipse(brush, null, new Point(cx, cy + 2.6), 0.75, 0.75);
                break;
            }
            case GhAlertKind.ReadyToMerge:
            {
                var tick = new StreamGeometry();
                using (var g = tick.Open())
                {
                    g.BeginFigure(new Point(cx - 4.2, cy + 0.2), isFilled: false);
                    g.LineTo(new Point(cx - 1.3, cy + 3.2));
                    g.LineTo(new Point(cx + 4.5, cy - 3.5));
                    g.EndFigure(false);
                }
                ctx.DrawGeometry(null, OverlayDraw.Pen(brush, 1.6, null, PenLineCap.Round, PenLineJoin.Round), tick);
                break;
            }
            default: // Assigned: a person — head and shoulders.
            {
                ctx.DrawEllipse(null, pen, new Point(cx, cy - 2.4), 2.2, 2.2);
                var shoulders = new StreamGeometry();
                using (var g = shoulders.Open())
                {
                    g.BeginFigure(new Point(cx - 4.6, cy + 5), isFilled: false);
                    g.CubicBezierTo(new Point(cx - 4.6, cy + 0.8), new Point(cx + 4.6, cy + 0.8), new Point(cx + 4.6, cy + 5));
                    g.EndFigure(false);
                }
                ctx.DrawGeometry(null, pen, shoulders);
                break;
            }
        }
    }

    // The dwell tooltip: the headline, then one line per symbol in words, so the chips never have to be learned.
    private void ShowGitHubTooltip()
    {
        if (!GitHubStripVisible || _gitHubRect.Width <= 0) return;

        var lines = new List<OverlayTooltip.Line>();
        switch (_gitHub.Status)
        {
            case GitHubStripStatus.Ok when _gitHub.NeedsYou > 0:
                lines.Add(new(_gitHub.NeedsYou == 1 ? "1 PR needs you" : $"{_gitHub.NeedsYou} PRs need you",
                    OverlayTooltip.FgColor, true));
                foreach (var (kind, count) in _gitHub.Counts ?? [])
                    lines.Add(new(GitHubAlertsSnapshot.Describe(kind, count), GitHubKindColor(kind), false));
                break;
            case GitHubStripStatus.Ok:
                lines.Add(new("No PRs need you", OverlayTooltip.FgColor, true));
                break;
            case GitHubStripStatus.Error:
                lines.Add(new("GitHub check failed", OverlayTooltip.FgColor, true));
                lines.Add(new(_gitHub.Detail, OverlayTooltip.MutedColor, false));
                break;
            default:
                lines.Add(new("Checking GitHub…", OverlayTooltip.FgColor, true));
                break;
        }
        lines.Add(new("Click for the list", OverlayTooltip.MutedColor, false));
        Tooltip().ShowLines(lines, ToScreen(_gitHubRect.Left + HorizPad, _gitHubRect.Bottom + 4));
    }
}
