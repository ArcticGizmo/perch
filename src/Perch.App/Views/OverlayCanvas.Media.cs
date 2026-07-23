using Avalonia;
using Avalonia.Media;
using Perch.Avalonia.Rendering;
using Perch.Platform;

namespace Perch.Avalonia.Views;

/// <summary>
/// The overlay's now-playing strip: a slim owner-drawn band below the session rows showing what's playing
/// (from the platform <see cref="IMediaController"/>) with previous / play-pause / next transport buttons.
/// Opt-in and only present while something is actually playing — otherwise it takes no height. Follows the
/// same measure-or-paint / captured-hit-rect discipline as the rest of <see cref="OverlayCanvas"/>: the
/// height is folded into <c>Draw</c>/<c>FullPanelHeight</c>, the buttons capture their rects at paint time
/// for hit-testing, and the button clicks are surfaced as events the App relays to the controller.
/// </summary>
public sealed partial class OverlayCanvas
{
    private const double MediaStripHeight = 36;
    private const double MediaTextSize    = 10.5;

    private static readonly IBrush MediaBtnBrush      = new SolidColorBrush(Color.FromRgb(205, 205, 215));
    private static readonly IBrush MediaDisabledBrush = new SolidColorBrush(Color.FromRgb(78, 78, 94));
    private static readonly IBrush MediaHoverBrush    = new SolidColorBrush(Color.FromArgb(30, 255, 255, 255));
    private static readonly IBrush MediaNoteBrush     = new SolidColorBrush(Color.FromRgb(129, 201, 149)); // a soft "now playing" green
    private static readonly IBrush MediaTrackBrush    = new SolidColorBrush(Color.FromRgb(38, 38, 52));    // progress-bar track

    private bool _mediaEnabled;
    private MediaSnapshot? _media;

    // The hovered transport button (0 = previous, 1 = play/pause, 2 = next; -1 = none) drives the button
    // highlight. The three hit-rects are captured at paint time; a disabled button gets a zero-size rect so
    // it can't be hovered or clicked.
    private int _hoveredMediaButton = -1;
    private Rect _mediaPrevRect, _mediaPlayRect, _mediaNextRect;

    // The track label's hit-rect, captured at paint time — but ONLY when the label was actually truncated to
    // fit, so hovering pops a tooltip with the full title/artist and an untruncated label shows no redundant
    // tooltip. The tooltip text is rebuilt from the current snapshot, so no need to stash the full string.
    private Rect _mediaTitleRect;

    // The strip is on screen only when the feature is enabled and there's something playing.
    private bool MediaStripVisible => _mediaEnabled && _media is not null;

    /// <summary>Raised when the user clicks the play/pause button; the App relays it to the controller.</summary>
    public event Action? MediaPlayPauseRequested;

    /// <summary>Raised when the user clicks the next button.</summary>
    public event Action? MediaNextRequested;

    /// <summary>Raised when the user clicks the previous button.</summary>
    public event Action? MediaPreviousRequested;

    /// <summary>Show/hide the whole now-playing strip. Toggling it can change the panel height (when
    /// something is playing), so relayout in that case; otherwise nothing visible changes.</summary>
    public void SetShowMediaController(bool enabled)
    {
        if (_mediaEnabled == enabled) return;
        bool before = MediaStripVisible;
        _mediaEnabled = enabled;
        if (MediaStripVisible != before) RemeasurePanel();
    }

    /// <summary>Feeds the latest now-playing snapshot (on the UI thread), or null when nothing is playing.
    /// When the strip's visibility flips (something started/stopped) the panel height changes, so relayout;
    /// otherwise just repaint the changed metadata/state.</summary>
    public void UpdateMedia(MediaSnapshot? media)
    {
        bool before = MediaStripVisible;
        _media = media;
        if (MediaStripVisible != before) RemeasurePanel();
        else if (_mediaEnabled) InvalidateVisual();
    }

    private void ClearMediaHitRects()
    {
        _mediaPrevRect = _mediaPlayRect = _mediaNextRect = _mediaTitleRect = default;
    }

    // Returns the hovered transport button under p (0 = prev, 1 = play/pause, 2 = next), or -1. Reads the
    // paint-time rects, so only enabled (non-zero-rect) buttons can be hit.
    private int HitTestMedia(Point p)
    {
        if (_mediaPrevRect.Contains(p)) return 0;
        if (_mediaPlayRect.Contains(p)) return 1;
        if (_mediaNextRect.Contains(p)) return 2;
        return -1;
    }

    // Paints the strip at y=top (its height is already reserved in Draw). Draws a separator, the transport
    // cluster on the right, and the music glyph + "Title — Artist" (truncated) on the left.
    private void DrawMediaStrip(DrawingContext ctx, double width, double top)
    {
        ClearMediaHitRects();
        if (_media is not { } snap) return;

        ctx.DrawLine(SepPen, new Point(HorizPad, top + 0.5), new Point(width - HorizPad, top + 0.5));

        // Content sits a touch above centre so the progress bar has room along the bottom edge.
        double midY = top + 16;

        // Transport cluster on the right: prev · play/pause · next.
        const double Btn = 22, Gap = 2;
        double nextCx = width - HorizPad - Btn / 2 + 2;
        double playCx = nextCx - Btn - Gap;
        double prevCx = playCx - Btn - Gap;
        double clusterLeft = prevCx - Btn / 2;

        _mediaPrevRect = DrawMediaButton(ctx, prevCx, midY, Btn, snap.CanPrevious, _hoveredMediaButton == 0,
            b => DrawPrevGlyph(ctx, b, prevCx, midY));
        _mediaPlayRect = DrawMediaButton(ctx, playCx, midY, Btn, snap.CanPlayPause, _hoveredMediaButton == 1,
            b => DrawPlayPauseGlyph(ctx, b, playCx, midY, snap.IsPlaying));
        _mediaNextRect = DrawMediaButton(ctx, nextCx, midY, Btn, snap.CanNext, _hoveredMediaButton == 2,
            b => DrawNextGlyph(ctx, b, nextCx, midY));

        // Left: the music glyph, then the track label truncated to whatever space is left before the cluster.
        double textX = HorizPad;
        DrawMusicGlyph(ctx, MediaNoteBrush, textX, midY);
        textX += 13;
        double textMax = clusterLeft - 8 - textX;
        if (textMax > 8)
        {
            string label = string.IsNullOrEmpty(snap.Artist) ? snap.Title : $"{snap.Title}  —  {snap.Artist}";
            string shown = OverlayDraw.Truncate(label, MediaTextSize, textMax);
            OverlayDraw.TextLeftMid(ctx, OverlayDraw.Text(shown, MediaTextSize, FgBrush), textX, midY);

            // Only make the label a dwell target when it was actually clipped — no point tooltipping text
            // that's already fully on screen.
            if (shown != label)
                _mediaTitleRect = new Rect(HorizPad, top, clusterLeft - 8 - HorizPad, MediaStripHeight);
        }

        // Progress bar along the bottom edge — only when the source reports a real timeline (a live stream
        // or most browser media reports none, so Duration is zero and there's nothing to draw).
        if (snap.Duration > TimeSpan.Zero)
        {
            double frac = Math.Clamp(snap.Position.TotalSeconds / snap.Duration.TotalSeconds, 0, 1);
            double barY = top + MediaStripHeight - 5, barLeft = HorizPad, barRight = width - HorizPad;
            OverlayDraw.Panel(ctx, new Rect(barLeft, barY, barRight - barLeft, 2.5), MediaTrackBrush, null, 1.25);
            if (frac > 0)
                OverlayDraw.Panel(ctx, new Rect(barLeft, barY, (barRight - barLeft) * frac, 2.5), MediaNoteBrush, null, 1.25);
        }
    }

    // Draws one transport button (hover wash + glyph) and returns its hit-rect — default (zero) when
    // disabled, so a disabled control can't be hovered or clicked.
    private Rect DrawMediaButton(DrawingContext ctx, double cx, double cy, double box, bool enabled, bool hovered, Action<IBrush> glyph)
    {
        var rect = new Rect(cx - box / 2, cy - box / 2, box, box);
        if (enabled && hovered) OverlayDraw.Panel(ctx, rect, MediaHoverBrush, null, 5);
        glyph(enabled ? (hovered ? FgBrush : MediaBtnBrush) : MediaDisabledBrush);
        return enabled ? rect : default;
    }

    // ── Transport glyphs (hand-drawn vectors, like the overlay's other icons) ──────────
    private static void DrawPrevGlyph(DrawingContext ctx, IBrush b, double cx, double cy)
    {
        const double gh = 4.5, barW = 2, barH = 11;
        ctx.FillRectangle(b, new Rect(cx - gh - barW - 1, cy - barH / 2, barW, barH));
        FillTriangle(ctx, b, cx + gh, cy - gh, cx + gh, cy + gh, cx - gh, cy); // pointing left
    }

    private static void DrawNextGlyph(DrawingContext ctx, IBrush b, double cx, double cy)
    {
        const double gh = 4.5, barW = 2, barH = 11;
        FillTriangle(ctx, b, cx - gh, cy - gh, cx - gh, cy + gh, cx + gh, cy); // pointing right
        ctx.FillRectangle(b, new Rect(cx + gh + 1, cy - barH / 2, barW, barH));
    }

    private static void DrawPlayPauseGlyph(DrawingContext ctx, IBrush b, double cx, double cy, bool playing)
    {
        if (playing)
        {
            const double barW = 3, gap = 3, barH = 12;
            ctx.FillRectangle(b, new Rect(cx - gap / 2 - barW, cy - barH / 2, barW, barH));
            ctx.FillRectangle(b, new Rect(cx + gap / 2, cy - barH / 2, barW, barH));
        }
        else
        {
            const double gh = 6;
            FillTriangle(ctx, b, cx - gh + 1, cy - gh, cx - gh + 1, cy + gh, cx + gh, cy);
        }
    }

    // A small eighth-note: a filled note head with a stem and a short flag.
    private static void DrawMusicGlyph(DrawingContext ctx, IBrush b, double x, double cy)
    {
        const double headR = 2.7;
        double headCx = x + headR, headCy = cy + 3;
        ctx.DrawEllipse(b, null, new Point(headCx, headCy), headR, headR * 0.82);
        var pen = new Pen(b, 1.4);
        double stemX = headCx + headR - 0.4;
        ctx.DrawLine(pen, new Point(stemX, headCy), new Point(stemX, cy - 5));
        ctx.DrawLine(pen, new Point(stemX, cy - 5), new Point(stemX + 3, cy - 2.5));
    }

    // The dwell tooltip for a truncated track label: the full title (bold), the artist, and — when there's
    // a timeline — the elapsed/total time. Split across lines so a long "Title — Artist" reads cleanly
    // rather than as one very wide line.
    private void ShowMediaTooltip()
    {
        if (_media is not { } snap || _mediaTitleRect.Width <= 0) return;
        var lines = new List<OverlayTooltip.Line> { new(snap.Title, OverlayTooltip.FgColor, true) };
        if (!string.IsNullOrEmpty(snap.Artist))
            lines.Add(new(snap.Artist, OverlayTooltip.MutedColor, false));
        if (snap.Duration > TimeSpan.Zero)
            lines.Add(new($"{FormatMediaTime(snap.Position)} / {FormatMediaTime(snap.Duration)}", OverlayTooltip.MutedColor, false));
        Tooltip().ShowLines(lines, ToScreen(_mediaTitleRect.Left, _mediaTitleRect.Bottom + 4));
    }

    private static string FormatMediaTime(TimeSpan t) =>
        t.TotalHours >= 1 ? $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}" : $"{t.Minutes}:{t.Seconds:00}";

    private static void FillTriangle(DrawingContext ctx, IBrush b, double x1, double y1, double x2, double y2, double x3, double y3)
    {
        var geo = new StreamGeometry();
        using (var g = geo.Open())
        {
            g.BeginFigure(new Point(x1, y1), isFilled: true);
            g.LineTo(new Point(x2, y2));
            g.LineTo(new Point(x3, y3));
            g.EndFigure(true);
        }
        ctx.DrawGeometry(b, null, geo);
    }
}
