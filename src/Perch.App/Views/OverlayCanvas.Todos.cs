using Avalonia;
using Avalonia.Media;
using Perch.Avalonia.Rendering;
using Perch.Avalonia.Theming;

namespace Perch.Avalonia.Views;

/// <summary>
/// The overlay's Todo strip, shaped like the GitHub alerts and Hypertree strips: a checkbox mark, a headline
/// counting what's open ("3 todos open"), and the overdue count — or, when nothing is late, when the next one
/// is due — right-aligned. The whole band is one control: clicking it opens the Todos window, and dwelling on
/// it shows a tooltip listing the next few items with their due times.
///
/// <para>Deliberately not a collapsible section: it is already one line. Fed by <c>TodoMonitorHost</c> with
/// the top few outstanding items (for the tooltip and the next-due label) plus the true open and overdue
/// totals. Its height is the Hypertree line metric, shared by the measure and paint passes through
/// <see cref="TodosStripHeight"/>.</para>
/// </summary>
public sealed partial class OverlayCanvas
{
    /// <summary>A todo reduced to just what the strip shows — kept toolkit-neutral (plain strings) so the
    /// canvas needs no reference to <c>Perch.Data.Todo</c>.</summary>
    internal readonly record struct TodoLine(string Id, string Title, string? DueLabel, bool Overdue);

    // The top-N outstanding items the monitor host feeds us (already sorted + capped there), plus the true
    // outstanding and overdue totals so the headline counts everything, not just the capped list.
    private IReadOnlyList<TodoLine> _topTodos = [];
    private int _todoOutstanding, _todoOverdue;
    private bool _hoveredTodos;
    // Display gate for the whole strip (the "Todos" setting). On by default.
    private bool _todosEnabled = true;
    private Rect _todosRect;

    /// <summary>Raised when the strip is clicked or "Todos…" is picked; the app opens the Todos window.</summary>
    public event Action? TodosRequested;

    private bool TodosStripVisible => _todosEnabled;

    // The same band height as the GitHub and Hypertree strips, so the one-line strips read alike.
    private double TodosStripHeight => TodosStripVisible ? HypertreeLineHeight + 12 : 0;

    private double TodosTop => _sectionTop.GetValueOrDefault(Perch.Data.OverlaySection.Todo);

    /// <summary>Show/hide the strip. Toggling it changes the panel height, so relayout.</summary>
    public void SetShowTodos(bool enabled)
    {
        if (_todosEnabled == enabled) return;
        _todosEnabled = enabled;
        RemeasurePanel();
    }

    /// <summary>Replaces the strip's contents (on the UI thread, from <c>TodoMonitorHost</c>). One line
    /// whatever the counts, so only a repaint.</summary>
    internal void SetTopTodos(IReadOnlyList<TodoLine> todos, int totalOutstanding, int totalOverdue)
    {
        _topTodos = todos;
        _todoOutstanding = totalOutstanding;
        _todoOverdue = totalOverdue;
        if (TodosStripVisible) InvalidateVisual();
    }

    private bool HitTestTodos(Point p) => ShowFullPanel && TodosStripVisible && _todosRect.Contains(p);

    private void DrawTodosStrip(DrawingContext ctx, double width, double top)
    {
        double h = TodosStripHeight;
        double midY = top + h / 2;
        var band = new Rect(HorizPad - 4, top + 3, Math.Max(0, width - 2 * (HorizPad - 4)), h - 6);
        _todosRect = new Rect(0, top, width, h);
        if (_hoveredTodos) OverlayDraw.Panel(ctx, band, FeedHoverBrush, null, 6);

        bool overdue = _todoOverdue > 0;
        DrawTodoMark(ctx, HorizPad + 6, midY, _hoveredTodos);
        if (overdue)
            DrawGlyphBadge(ctx, new Point(HorizPad + 11.5, midY - 5.5), OverlayDraw.Brush(AttentionColor));

        double textX = HorizPad + 22;
        double right = width - HorizPad;

        int n = _todoOutstanding;
        string primary = n switch { 0 => "No todos", 1 => "1 todo open", _ => $"{n} todos open" };
        IBrush primaryBrush = n > 0 || _hoveredTodos ? FgBrush : MutedBrush;
        var weight = overdue ? FontWeight.SemiBold : FontWeight.Normal;

        // Right-hand detail: the overdue count in the attention hue, else when the next item is due, else
        // (nothing open) a nudge to add one.
        string detail;
        IBrush detailBrush = MutedBrush;
        if (overdue)
        {
            detail = $"{_todoOverdue} overdue";
            detailBrush = OverlayDraw.Brush(AttentionColor);
        }
        else if (n == 0) detail = "add one…";
        else detail = _topTodos.FirstOrDefault(t => t.DueLabel is not null).DueLabel is { } next ? $"next {next}" : "";

        var primaryFt = OverlayDraw.Text(OverlayDraw.Truncate(primary, HyperRowSize, Math.Max(20, right - textX), weight),
            HyperRowSize, primaryBrush, weight);
        OverlayDraw.TextLeftMid(ctx, primaryFt, textX, midY);

        double avail = right - (textX + primaryFt.Width + 12);
        if (detail.Length > 0 && avail > 24)
        {
            var detailFt = OverlayDraw.Text(OverlayDraw.Truncate(detail, HyperMetaSize, avail),
                HyperMetaSize, detailBrush, overdue ? FontWeight.SemiBold : FontWeight.Normal);
            OverlayDraw.TextLeftMid(ctx, detailFt, right - detailFt.Width, midY);
        }
    }

    // The strip's mark: a rounded checkbox with a tick, ~11px, centred on (cx, cy), in the accent.
    private static void DrawTodoMark(DrawingContext ctx, double cx, double cy, bool hovered)
    {
        var brush = OverlayDraw.Brush(hovered ? Palette.Active.AccentHover.ToColor() : Palette.Active.Accent.ToColor());
        var pen = OverlayDraw.Pen(brush, 1.4, null, PenLineCap.Round, PenLineJoin.Round);
        ctx.DrawRectangle(null, pen, new RoundedRect(new Rect(cx - 5.5, cy - 5.5, 11, 11), 2.5));

        var tick = new StreamGeometry();
        using (var g = tick.Open())
        {
            g.BeginFigure(new Point(cx - 2.8, cy + 0.1), isFilled: false);
            g.LineTo(new Point(cx - 0.7, cy + 2.3));
            g.LineTo(new Point(cx + 3, cy - 2.4));
            g.EndFigure(false);
        }
        ctx.DrawGeometry(null, OverlayDraw.Pen(brush, 1.6, null, PenLineCap.Round, PenLineJoin.Round), tick);
    }

    // The dwell tooltip: the headline, then the next few items with their due times (overdue in the attention hue).
    private void ShowTodosTooltip()
    {
        if (!TodosStripVisible || _todosRect.Width <= 0) return;

        var lines = new List<OverlayTooltip.Line>();
        if (_todoOutstanding == 0)
            lines.Add(new("No todos", OverlayTooltip.FgColor, true));
        else
        {
            string head = _todoOutstanding == 1 ? "1 todo open" : $"{_todoOutstanding} todos open";
            if (_todoOverdue > 0) head += $" · {_todoOverdue} overdue";
            lines.Add(new(head, OverlayTooltip.FgColor, true));
            foreach (var t in _topTodos)
            {
                string text = t.DueLabel is { } due ? $"{t.Title} — {due}" : t.Title;
                lines.Add(new(text, t.Overdue ? AttentionColor : OverlayTooltip.MutedColor, false));
            }
            if (_todoOutstanding > _topTodos.Count)
                lines.Add(new($"+{_todoOutstanding - _topTodos.Count} more", OverlayTooltip.MutedColor, false));
        }
        lines.Add(new("Click to open Todos", OverlayTooltip.MutedColor, false));
        Tooltip().ShowLines(lines, ToScreen(_todosRect.Left + HorizPad, _todosRect.Bottom + 4));
    }
}
