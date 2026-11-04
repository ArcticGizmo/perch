using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Perch.Avalonia.Rendering;
using Perch.Avalonia.Theming;
using Perch.Data;

namespace Perch.Avalonia.Views;

/// <summary>
/// Keeps the panel on its monitor however many sessions there are (docs/session-list-scroll-testing.md). The panel has a
/// height budget: the work area less the default margins when floating or dense, the reserved column when docked. When
/// the full layout runs past it, only the session rows give way. They become a viewport that scrolls with the wheel or
/// a thin thumb, while every other section keeps its height. Rows needing the user that are scrolled out of view are
/// counted in a cue at the viewport's edge, so a long list can't hide a blocked session.
///
/// <para>Also the row-density toggle on the "+ New session" row: compact rows drop the activity/elapsed line, so a
/// session takes 26px instead of 46px and far more of them fit before the list needs to scroll at all.</para>
/// </summary>
public sealed partial class OverlayCanvas
{
    private const double CompactRowHeight = 26;
    // The viewport never shrinks below this, so the list stays usable when the other sections fill the screen.
    private const double MinRowsViewport = 3 * RowHeight;
    // The panel stops this far above the work area's bottom, matching the default float's gap at the top.
    private const double FloatBottomGap = 16;
    // Room kept clear under the docked column's content for its bottom collapse handle.
    private const double DockedHandleReserve = 2 * DockToggleR + DockToggleBottomPad + 4;
    private const double WheelStep = RowHeight;
    private const double ThumbW = 3, ThumbHoverW = 5, ThumbZoneW = 11, MinThumbH = 18;
    private const double CueH = 16;

    private double _panelMaxHeight = double.PositiveInfinity;   // DIPs; infinite when there's no window to fit
    private bool _panelBudgetPinned;                            // headless render pins a budget; never re-read
    private double _rowsContent;                                // every row's natural height, summed
    private double _rowsViewport = double.PositiveInfinity;     // the rows' height cap, set by the measure pass
    private double _rowsScroll;                                 // DIPs scrolled into the rows

    private bool _thumbDragging;
    private double _thumbDragStartY, _thumbDragStartScroll;
    private bool _hoveredThumb;
    private Rect _cueUpRect, _cueDownRect;
    private bool _hoveredCueUp, _hoveredCueDown;

    private bool _compactRows;
    private Rect _compactToggleRect;   // laid out by DrawNewSessionRow each paint
    private bool _hoveredCompactToggle;

    /// <summary>The density toggle flipped the rows: true = compact (one line each), false = roomy.</summary>
    internal event Action<bool>? CompactRowsChanged;

    /// <summary>The session rows ran past the panel's budget and became a scroller. Raised at most once per
    /// canvas, from the measure pass, so handlers must defer any real work.</summary>
    internal event Action? RowsOverflowed;
    private bool _rowsOverflowRaised;

    private double RowsViewportHeight => Math.Min(_rowsContent, _rowsViewport);
    private bool RowsScrollable => _rowsContent > _rowsViewport + 0.5;
    private double MaxRowsScroll => Math.Max(0, _rowsContent - RowsViewportHeight);

    private double RowsContentHeight()
    {
        double h = 0;
        foreach (var r in _rows) h += HeightOf(r);
        return h;
    }

    // ── The height budget ──

    // Re-reads the budget from the monitor the window is on. The docked column sets its own in ApplyDockedGeometry.
    private void RefreshPanelBudget()
    {
        if (_panelBudgetPinned || _docked) return;
        if (HostWindow is not { } w) { _panelMaxHeight = double.PositiveInfinity; return; }
        try
        {
            var screen = w.Screens?.ScreenFromWindow(w) ?? w.Screens?.Primary;
            if (screen is null) return;
            double workDip = screen.WorkingArea.Height / ScreenSpacePerDip(screen.Scaling <= 0 ? 1.0 : screen.Scaling);
            _panelMaxHeight = Math.Max(HeaderHeight + MinRowsViewport, workDip - FloatTopGap - FloatBottomGap);
        }
        catch { /* best-effort; keep the last budget */ }
    }

    private void SetDockedPanelBudget(double columnDip)
    {
        if (_panelBudgetPinned) return;
        _panelMaxHeight = Math.Max(HeaderHeight + MinRowsViewport, columnDip - DockedHandleReserve);
    }

    /// <summary>Headless render: lays the panel out as if its monitor left this many DIPs.</summary>
    internal void SetPanelMaxHeightForRender(double dip)
    {
        _panelBudgetPinned = true;
        _panelMaxHeight = dip;
        InvalidateMeasure();
    }

    /// <summary>Headless render: scrolls the rows as far as they go, or to the top.</summary>
    internal void ScrollRowsForRender(bool toEnd)
    {
        Draw(null, CurrentFloatingWidth);
        _rowsScroll = toEnd ? MaxRowsScroll : 0;
    }

    // The measure pass: lays the sections out at their natural heights, then, if the panel runs past its budget,
    // caps the rows to a viewport and lays out again. Rearrange preview always shows the whole list.
    private double PanelBodyHeight()
    {
        _rowsContent = RowsContentHeight();
        _rowsViewport = double.PositiveInfinity;
        double body = LayoutSections();
        if (double.IsFinite(_panelMaxHeight) && !RearrangeMode)
        {
            double over = BodyTop + body + (ShowFooter ? FooterHeight : 0) - _panelMaxHeight;
            if (over > 0.5)
            {
                _rowsViewport = Math.Max(Math.Min(_rowsContent, MinRowsViewport), _rowsContent - over);
                body = LayoutSections();
                // A render's pinned budget is staged, not a real monitor running out of room.
                if (RowsScrollable && !_panelBudgetPinned && !_rowsOverflowRaised)
                {
                    _rowsOverflowRaised = true;
                    RowsOverflowed?.Invoke();
                }
            }
        }
        _rowsScroll = Math.Clamp(_rowsScroll, 0, MaxRowsScroll);
        return body;
    }

    // A taller panel keeps its top where it is, so near the bottom of the screen it would run off. Lifts it just
    // enough to sit inside the work area. Not written to the remembered placement, which stays where the user put it.
    private void KeepFloatingBottomOnScreen(Window w, double dipHeight)
    {
        try
        {
            var screen = w.Screens?.ScreenFromWindow(w);
            if (screen is null) return;
            var wa = screen.WorkingArea;
            int physH = (int)(dipHeight * ScreenSpacePerDip(screen.Scaling <= 0 ? 1.0 : screen.Scaling));
            int maxY = wa.Y + wa.Height - physH;
            if (w.Position.Y <= maxY) return;
            SetFloatingPosition(w, new PixelPoint(w.Position.X, Math.Max(wa.Y, maxY)));
        }
        catch { /* best-effort */ }
    }

    // ── Scrolling ──

    private bool InRowsViewport(Point p) =>
        ShowFullPanel && _rows.Count > 0 && p.Y >= RowsTop && p.Y < RowsTop + RowsViewportHeight;

    private void ScrollRowsBy(double delta)
    {
        double next = Math.Clamp(_rowsScroll + delta, 0, MaxRowsScroll);
        if (Math.Abs(next - _rowsScroll) < 0.01) return;
        _rowsScroll = next;
        InvalidateVisual();
    }

    private void ScrollRowIntoView(int index)
    {
        if (!RowsScrollable || index < 0 || index >= _rows.Count) return;
        double top = 0;
        for (int i = 0; i < index; i++) top += HeightOf(_rows[i]);
        double h = HeightOf(_rows[index]);
        double vp = RowsViewportHeight;
        if (top < _rowsScroll) _rowsScroll = top;
        else if (top + h > _rowsScroll + vp) _rowsScroll = top + h - vp;
        _rowsScroll = Math.Clamp(_rowsScroll, 0, MaxRowsScroll);
        InvalidateVisual();
    }

    // Brings a session's own row into view, for the moments that point at one (the jump hotkey, a PR banner).
    private void ScrollSessionIntoView(string sessionId)
    {
        for (int i = 0; i < _rows.Count; i++)
            if (_rows[i] is { IsSubAgent: false, IsSectionHeader: false } r && r.Session?.SessionId == sessionId)
            {
                ScrollRowIntoView(i);
                return;
            }
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        var p = e.GetPosition(this);
        if (!RearrangeMode && RowsScrollable && InRowsViewport(p))
        {
            ScrollRowsBy(-e.Delta.Y * WheelStep);
            _hoveredRow = HitTestRow(p);
            _tipKind = TipKind.None;
            _tipRow = -1;
            _dwellTimer?.Stop();
            _tooltip?.HideTip();
            e.Handled = true;
            return;
        }
        base.OnPointerWheelChanged(e);
    }

    // The thumb rides the panel's right edge, clear of the right-aligned status text. Alongside the viewport its band
    // takes the press from the right-hand resize grip, which still works above and below the rows.
    private Rect ThumbZone(double width) => new(width - ThumbZoneW, RowsTop, ThumbZoneW, RowsViewportHeight);

    private (double Top, double Height) ThumbGeometry()
    {
        double vp = RowsViewportHeight;
        double h = Math.Clamp(vp * vp / Math.Max(1, _rowsContent), MinThumbH, vp);
        double max = MaxRowsScroll;
        double top = RowsTop + (max <= 0 ? 0 : (vp - h) * _rowsScroll / max);
        return (top, h);
    }

    private bool OverThumbZone(Point p) => RowsScrollable && !RearrangeMode && ShowFullPanel && ThumbZone(_layoutWidth).Contains(p);

    // A press in the thumb's band: on the thumb it grabs it, elsewhere in the band it jumps the thumb's centre there
    // first and then grabs it. Returns false when the press is somewhere else.
    private bool TryBeginThumbDrag(Point p)
    {
        if (!OverThumbZone(p)) return false;
        var (top, h) = ThumbGeometry();
        if (p.Y < top || p.Y >= top + h)
        {
            double vp = RowsViewportHeight;
            double frac = (p.Y - RowsTop - h / 2) / Math.Max(1, vp - h);
            _rowsScroll = Math.Clamp(frac * MaxRowsScroll, 0, MaxRowsScroll);
        }
        _thumbDragging = true;
        _thumbDragStartY = p.Y;
        _thumbDragStartScroll = _rowsScroll;
        _hoveredRow = -1;
        InvalidateVisual();
        return true;
    }

    private void DragThumbTo(Point p)
    {
        var (_, h) = ThumbGeometry();
        double travel = Math.Max(1, RowsViewportHeight - h);
        _rowsScroll = Math.Clamp(_thumbDragStartScroll + (p.Y - _thumbDragStartY) * MaxRowsScroll / travel, 0, MaxRowsScroll);
        InvalidateVisual();
    }

    private void EndThumbDrag()
    {
        if (!_thumbDragging) return;
        _thumbDragging = false;
        InvalidateVisual();
    }

    // ── Painting the viewport ──

    // Draws the rows that fall inside the viewport, at their scrolled positions, clipped to it. Returns the
    // viewport's bottom, where the daemon strip continues.
    private double PaintRows(DrawingContext ctx, double width, double rowsTop)
    {
        double vp = RowsViewportHeight;
        double vpBottom = rowsTop + vp;
        bool scrolling = RowsScrollable;
        var clip = scrolling ? ctx.PushClip(new Rect(0, rowsTop, width, vp)) : (DrawingContext.PushedState?)null;
        try
        {
            double y = rowsTop - _rowsScroll;
            for (int i = 0; i < _rows.Count; i++)
            {
                var r = _rows[i];
                double h = HeightOf(r);
                if (y + h > rowsTop && y < vpBottom) PaintRow(ctx, width, i, r, y);
                y += h;
            }
            if (scrolling) PaintScrollChrome(ctx, width, rowsTop, vpBottom);
            else _cueUpRect = _cueDownRect = default;
        }
        finally { clip?.Dispose(); }

        if (scrolling) DropHitRectsOutside(rowsTop, vpBottom);
        return vpBottom;
    }

    private void PaintRow(DrawingContext ctx, double width, int i, DisplayRow r, double y)
    {
        if (r.Header == RowHeader.Autonomous) DrawSectionHeaderRow(ctx, r, y, width);
        else if (r.IsSubAgent) DrawSubAgentRow(ctx, i, r, y, width);
        else DrawSessionRow(ctx, i, r.Session!, y, width);
    }

    // A glyph on a row half out of the viewport was painted but clipped. Its rect would still catch the pointer over
    // whatever sits beyond the viewport, so only glyphs wholly inside it stay live.
    private void DropHitRectsOutside(double top, double bottom)
    {
        Prune(_artifactRects); Prune(_mdRects); Prune(_thermoRects); Prune(_warnRects); Prune(_taskRects);
        Prune(_bgTaskRects); Prune(_metricsRects); Prune(_noteRects); Prune(_originRects); Prune(_prRects);
        Prune(_jiraRects); Prune(_dirRects); Prune(_subChevronRects);
        if (_autonomousHeaderRect.Width > 0)
        {
            var clipped = _autonomousHeaderRect.Intersect(new Rect(0, top, _autonomousHeaderRect.Right, bottom - top));
            _autonomousHeaderRect = clipped.Height > 0 ? clipped : default;
        }

        void Prune(Dictionary<int, Rect> rects)
        {
            foreach (var (row, rect) in rects)
                if (rect.Top < top || rect.Bottom > bottom) rects.Remove(row);
        }
    }

    private void PaintScrollChrome(DrawingContext ctx, double width, double top, double bottom)
    {
        var (thumbTop, thumbH) = ThumbGeometry();
        bool lit = _hoveredThumb || _thumbDragging;
        double w = lit ? ThumbHoverW : ThumbW;
        double cx = width - 5;
        var m = Palette.Muted;
        // A divider where rows slide under the "+ New session" row, once some have.
        if (_rowsScroll > 0.5) ctx.DrawLine(SepPen, new Point(HorizPad, top + 0.5), new Point(width - HorizPad, top + 0.5));
        OverlayDraw.Panel(ctx, new Rect(cx - w / 2, thumbTop + 2, w, Math.Max(0, thumbH - 4)),
            OverlayDraw.Brush(Color.FromArgb(lit ? (byte)200 : (byte)110, m.R, m.G, m.B)), null, w / 2);

        var (upCount, upColor, downCount, downColor) = HiddenNeedsYou(top, bottom);
        _cueUpRect = upCount > 0 ? DrawCue(ctx, width, top + 4, upCount, upColor, up: true, _hoveredCueUp) : default;
        _cueDownRect = downCount > 0 ? DrawCue(ctx, width, bottom - 4 - CueH, downCount, downColor, up: false, _hoveredCueDown) : default;
    }

    // A pill at the viewport's edge: a status dot, then "2 above" / "1 below".
    private Rect DrawCue(DrawingContext ctx, double width, double top, int count, Color color, bool up, bool hovered)
    {
        string label = CueLabel(count, up);
        var text = OverlayDraw.Text(label, StatusSize, FgBrush);
        double pillW = 8 + 6 + 4 + text.Width + 8;
        double right = width - ThumbZoneW - 4;
        var rect = new Rect(right - pillW, top, pillW, CueH);
        OverlayDraw.Panel(ctx, rect, hovered ? Palette.AccentBrush : Palette.FormBgBrush, BorderPen, CueH / 2);
        double midY = rect.Center.Y;
        ctx.DrawEllipse(OverlayDraw.Brush(color), null, new Point(rect.Left + 8 + 3, midY), 3, 3);
        OverlayDraw.TextLeftMid(ctx, text, rect.Left + 8 + 6 + 4, midY);
        return rect;
    }

    private static readonly Dictionary<(int, bool), string> CueLabels = new();
    private static string CueLabel(int count, bool up)
    {
        if (!CueLabels.TryGetValue((count, up), out var s))
            CueLabels[(count, up)] = s = $"{count} {(up ? "above" : "below")}";
        return s;
    }

    // Sessions waiting on the user whose row is at least half scrolled out of view, above and below, each with the
    // most urgent colour among them (an API error over awaiting input over a finished turn).
    private (int Up, Color UpColor, int Down, Color DownColor) HiddenNeedsYou(double top, double bottom)
    {
        int up = 0, down = 0, upRank = 0, downRank = 0;
        double y = top - _rowsScroll;
        foreach (var r in _rows)
        {
            double h = HeightOf(r);
            double mid = y + h / 2;
            y += h;
            if (r.IsSubAgent || r.IsSectionHeader || r.Session is not { } s) continue;
            int rank = NeedsYouRank(s.Status);
            if (rank == 0) continue;
            if (mid < top) { up++; upRank = Math.Max(upRank, rank); }
            else if (mid > bottom) { down++; downRank = Math.Max(downRank, rank); }
        }
        return (up, RankColor(upRank), down, RankColor(downRank));
    }

    private static int NeedsYouRank(SessionStatus status) => status switch
    {
        SessionStatus.ApiError => 3,
        SessionStatus.AwaitingInput => 2,
        SessionStatus.NeedsAttention => 1,
        _ => 0,
    };

    private static Color RankColor(int rank) => rank switch
    {
        3 => ApiErrorColor,
        2 => AwaitingColor,
        _ => AttentionColor,
    };

    // A cue was clicked: bring the nearest hidden session waiting on the user into view.
    private bool RouteCueClick(Point p)
    {
        bool up = _cueUpRect.Width > 0 && _cueUpRect.Contains(p);
        bool down = !up && _cueDownRect.Width > 0 && _cueDownRect.Contains(p);
        if (!up && !down) return false;

        double top = RowsTop, bottom = RowsTop + RowsViewportHeight;
        double y = top - _rowsScroll;
        int target = -1;
        for (int i = 0; i < _rows.Count; i++)
        {
            var r = _rows[i];
            double h = HeightOf(r);
            double mid = y + h / 2;
            y += h;
            if (r.IsSubAgent || r.IsSectionHeader || r.Session is not { } s || NeedsYouRank(s.Status) == 0) continue;
            if (up && mid < top) target = i;                          // keep the last one above: the nearest
            else if (down && mid > bottom) { target = i; break; }     // the first one below is the nearest
        }
        ScrollRowIntoView(target);
        return true;
    }

    // Pointer-move hover for the thumb and the cues. Returns true when the pointer is over one of them.
    private bool UpdateScrollHover(Point p)
    {
        bool thumb = OverThumbZone(p);
        bool cueUp = _cueUpRect.Width > 0 && _cueUpRect.Contains(p);
        bool cueDown = _cueDownRect.Width > 0 && _cueDownRect.Contains(p);
        if (thumb != _hoveredThumb || cueUp != _hoveredCueUp || cueDown != _hoveredCueDown)
        {
            _hoveredThumb = thumb;
            _hoveredCueUp = cueUp;
            _hoveredCueDown = cueDown;
            InvalidateVisual();
        }
        return thumb || cueUp || cueDown;
    }

    private bool ClearScrollHover()
    {
        if (!_hoveredThumb && !_hoveredCueUp && !_hoveredCueDown && !_hoveredCompactToggle) return false;
        _hoveredThumb = _hoveredCueUp = _hoveredCueDown = _hoveredCompactToggle = false;
        return true;
    }

    // ── The row-density toggle ──
    // At the far right of the "+ New session" row: roomy two-line rows ⇄ compact one-line rows. Its glyph is the
    // density showing now.

    private bool CompactToggleVisible => !RearrangeMode;

    /// <summary>Seeds compact (true) or roomy (false) rows from settings, without raising the change event.</summary>
    internal void SetCompactRows(bool on)
    {
        if (_compactRows == on) return;
        _compactRows = on;
        RemeasurePanel();
    }

    private void ToggleCompactRows()
    {
        _compactRows = !_compactRows;
        RemeasurePanel();
        CompactRowsChanged?.Invoke(_compactRows);
    }

    private bool OverCompactToggle(Point p) => ShowFullPanel && _compactToggleRect.Width > 0 && _compactToggleRect.Contains(p);

    private void DrawCompactToggle(DrawingContext ctx, Rect box)
    {
        var c = box.Center;
        if (_hoveredCompactToggle) OverlayDraw.Panel(ctx, box, FeedHoverBrush, null, 5);
        DrawDensityGlyph(ctx, _hoveredCompactToggle ? Palette.AccentBrush : MutedBrush, c.X, c.Y, _compactRows);
    }

    // Rows as a status dot and a name line: three tight rows when compact, two spaced rows when roomy. ~12×10.
    private static void DrawDensityGlyph(DrawingContext ctx, IBrush brush, double cx, double cy, bool compact)
    {
        var pen = OverlayDraw.Pen(brush, 1.2);
        if (compact)
        {
            for (int i = -1; i <= 1; i++)
            {
                double y = cy + i * 4;
                ctx.DrawEllipse(brush, null, new Point(cx - 5, y), 1.1, 1.1);
                ctx.DrawLine(pen, new Point(cx - 2.5, y), new Point(cx + 6, y));
            }
        }
        else
        {
            for (int i = -1; i <= 1; i += 2)
            {
                double y = cy + i * 3;
                ctx.DrawEllipse(brush, null, new Point(cx - 4.5, y), 1.5, 1.5);
                ctx.DrawLine(pen, new Point(cx - 1.5, y), new Point(cx + 6, y));
            }
        }
    }

    private void ShowCompactToggleTooltip()
    {
        if (_compactToggleRect.Width <= 0) return;
        var lines = new List<OverlayTooltip.Line>
        {
            new(_compactRows ? "Compact rows" : "Roomy rows", OverlayTooltip.FgColor, true),
            new(_compactRows ? "Click for two lines per session" : "Click for one line per session",
                OverlayTooltip.MutedColor, false),
        };
        Tooltip().ShowLines(lines, ToScreen(_compactToggleRect.Left - 100, _compactToggleRect.Bottom + 4));
    }
}
