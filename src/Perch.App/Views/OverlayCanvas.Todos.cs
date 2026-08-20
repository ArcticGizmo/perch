using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Perch.Avalonia.Rendering;
using Perch.Avalonia.Theming;

namespace Perch.Avalonia.Views;

/// <summary>
/// The user's own "Todo" section — the top few outstanding todos with their relative due times, sitting
/// under the Hypertree strip and above the session rows. It follows the standard collapsible-section pattern (see the Social/Friends region and the
/// convention note in CLAUDE.md): a header row with a chevron, a caption, an open-count, and a right-hand
/// "+" that opens the Todos window ready to add. Clicking the header collapses/expands (persisted); a click
/// on a line opens the window; a right-click offers "Complete".
///
/// Reuses the Hypertree/daemon line metrics for its rows and the feed's caption/hover metrics for its
/// header, and stays in the measure-or-paint discipline: <see cref="TodosStripHeight"/> feeds
/// <c>PanelBodyHeight</c> and the paint advances the same cursor, so measured height and painted layout
/// can't drift.
/// </summary>
public sealed partial class OverlayCanvas
{
    /// <summary>A todo reduced to just what the strip paints — kept toolkit-neutral (plain strings) so the
    /// canvas needs no reference to <c>Perch.Data.Todo</c>. The id rides along so a right-click Complete can
    /// name the item back to the app.</summary>
    internal readonly record struct TodoLine(string Id, string Title, string? DueLabel, bool Overdue);

    // The top-N outstanding items the monitor host feeds us (already sorted + capped there), plus the true
    // outstanding total so the header can read "N open" even when the list is capped.
    private IReadOnlyList<TodoLine> _topTodos = [];
    private int _todoOutstanding;
    private int _hoveredTodoRow = -1;
    private bool _hoveredTodoHeader, _hoveredTodoAdd;
    // Display gate for the whole section (the "Todos" setting). On by default.
    private bool _todosEnabled = true;
    // Collapsed → only the header shows (seeded from AppSettings.TodosExpanded; toggled by the chevron).
    private bool _todosExpanded = true;

    private Rect _todoHeaderRect, _todoAddRect;

    // At most this many lines on the overlay — the strip is a glance, not the list. The window shows them all.
    private const int MaxTodoRows = 3;

    /// <summary>Raised when the user clicks a todo line, the header "+", or picks "Open Todos…"; the app
    /// opens the Todos window.</summary>
    public event Action? TodosRequested;

    /// <summary>Raised when the user picks "Complete" on a todo line; carries the todo id for the app to
    /// mark it done in the store.</summary>
    public event Action<string>? TodoCompleteRequested;

    /// <summary>Raised when the section header's chevron is clicked to expand or collapse — the app persists it.</summary>
    public event Action<bool>? TodosExpandChanged;

    private bool TodosStripVisible => _todosEnabled;
    private bool TodosEmpty => _topTodos.Count == 0;
    private int VisibleTodoCount => Math.Min(_topTodos.Count, MaxTodoRows);
    // When expanded and empty, one "add one…" prompt line; otherwise one line per visible item.
    private int TodoLineCount => TodosEmpty ? 1 : VisibleTodoCount;

    private double TodoHeaderHeight => FeedCaptionHeight + 12;   // matches the Friends header band
    private double TodosBodyHeight => !_todosExpanded ? 0 : (TodoLineCount * HypertreeLineHeight) + 6;

    private double TodosStripHeight
        => !TodosStripVisible ? 0 : TodoHeaderHeight + TodosBodyHeight;

    // Sits directly under the Hypertree strip, above the session rows — your agenda reads with the branches,
    // not buried beneath the session list.
    private double TodosTop => HypertreeTop + HypertreeStripHeight;

    /// <summary>Show/hide the whole Todo section. Toggling it changes the panel height, so relayout.</summary>
    public void SetShowTodos(bool enabled)
    {
        if (_todosEnabled == enabled) return;
        _todosEnabled = enabled;
        RemeasurePanel();
    }

    /// <summary>Sets the section's initial expand/collapse state (from AppSettings) without raising the change
    /// event. Call once at wire-up.</summary>
    public void SetTodosExpanded(bool expanded)
    {
        if (_todosExpanded == expanded) return;
        _todosExpanded = expanded;
        if (TodosStripVisible) RemeasurePanel();
    }

    /// <summary>Replaces the strip's contents (on the UI thread, from <c>TodoMonitorHost</c>). A change in the
    /// visible line count alters the panel height (when expanded), so relayout; otherwise repaint in place.</summary>
    internal void SetTopTodos(IReadOnlyList<TodoLine> todos, int totalOutstanding)
    {
        int beforeLines = TodoLineCount;
        _topTodos = todos;
        _todoOutstanding = totalOutstanding;
        _hoveredTodoRow = -1;
        if (_todosExpanded && TodoLineCount != beforeLines) RemeasurePanel();
        else if (TodosStripVisible) InvalidateVisual();
    }

    // Routed from RouteClick: the header toggles expand/collapse.
    private void OnTodoHeaderClicked()
    {
        _todosExpanded = !_todosExpanded;
        TodosExpandChanged?.Invoke(_todosExpanded);
        RemeasurePanel();
    }

    private void DrawTodosStrip(DrawingContext ctx, double width, double top)
    {
        DrawTodoHeader(ctx, width, top);
        if (!_todosExpanded) return;

        double y = top + TodoHeaderHeight;
        double lineH = HypertreeLineHeight;
        const double DotR = 3, DueMaxW = 72;
        double nameX = HorizPad + DotR * 2 + 6;
        var accent = Palette.Active.Accent.ToColor();

        // Empty list → a single faint "add one…" prompt that opens the window (a second on-overlay way in
        // besides the header "+").
        if (TodosEmpty)
        {
            bool hot = _hoveredTodoRow == 0;
            if (hot)
                ctx.FillRectangle(new SolidColorBrush(Color.FromArgb(28, 255, 255, 255)),
                    new Rect(4, y, Math.Max(0, width - 8), lineH));
            var addFt = OverlayDraw.Text("add one…", HyperRowSize, hot ? FgBrush : MutedBrush);
            OverlayDraw.TextLeftMid(ctx, addFt, nameX, y + lineH / 2);
            return;
        }

        for (int i = 0; i < VisibleTodoCount; i++)
        {
            var t = _topTodos[i];
            double midY = y + lineH / 2;

            if (_hoveredTodoRow == i)
                ctx.FillRectangle(new SolidColorBrush(Color.FromArgb(28, 255, 255, 255)),
                    new Rect(4, y, Math.Max(0, width - 8), lineH));

            ctx.DrawEllipse(new SolidColorBrush(t.Overdue ? AttentionColor : accent), null,
                new Point(HorizPad + DotR, midY), DotR, DotR);

            // Trailing due label (right-aligned): overdue reads in the attention hue, upcoming is muted.
            string due = OverlayDraw.Truncate(t.DueLabel ?? "", HyperMetaSize, DueMaxW);
            double dueReserve = due.Length > 0 ? OverlayDraw.MeasureWidth(due, HyperMetaSize) + 8 : 0;

            double nameMax = Math.Max(20, width - HorizPad - nameX - dueReserve);
            var nameFt = OverlayDraw.Text(OverlayDraw.Truncate(t.Title, HyperRowSize, nameMax),
                HyperRowSize, _hoveredTodoRow == i ? FgBrush : BotBrush);
            OverlayDraw.TextLeftMid(ctx, nameFt, nameX, midY);

            if (due.Length > 0)
            {
                var dueFt = OverlayDraw.Text(due, HyperMetaSize, t.Overdue ? new SolidColorBrush(AttentionColor) : MutedBrush);
                OverlayDraw.TextLeftMid(ctx, dueFt, width - HorizPad - dueFt.Width, midY);
            }

            y += lineH;
        }
    }

    // The collapsible header: chevron + "Todo" caption + (when collapsed) an open-count + a right-hand "+".
    private void DrawTodoHeader(DrawingContext ctx, double width, double top)
    {
        double midY = top + 6 + FeedCaptionHeight / 2;
        if (_hoveredTodoHeader)
            OverlayDraw.Panel(ctx, new Rect(HorizPad - 4, top + 3, width - 2 * (HorizPad - 4), TodoHeaderHeight - 6),
                FeedHoverBrush, null, 6);

        DrawChevron(ctx, HorizPad + 4, midY, _todosExpanded);
        var capFt = OverlayDraw.Text("Todo", FeedCaptionSize, MutedBrush, FontWeight.SemiBold);
        OverlayDraw.TextLeftMid(ctx, capFt, HorizPad + 14, midY);

        // Far right: a "+" that opens the window ready to add.
        const double addBox = 18;
        double addCx = width - HorizPad - addBox / 2 + 2;
        var addRect = new Rect(addCx - addBox / 2, midY - addBox / 2, addBox, addBox);
        if (_hoveredTodoAdd) OverlayDraw.Panel(ctx, addRect, FeedHoverBrush, null, 5);
        DrawPlusGlyph(ctx, _hoveredTodoAdd ? FgBrush : MutedBrush, addCx, midY);
        _todoAddRect = addRect;

        // Left of the "+": the open-count, so a collapsed section still says how much is waiting.
        if (!_todosExpanded && _todoOutstanding > 0)
        {
            var nFt = OverlayDraw.Text($"{_todoOutstanding} open", FeedCaptionSize, MutedBrush);
            OverlayDraw.TextLeftMid(ctx, nFt, addRect.Left - 10 - nFt.Width, midY);
        }

        _todoHeaderRect = new Rect(0, top, width, TodoHeaderHeight);
    }

    // Returns the todo body line under p, or -1 if none (or the section is collapsed / hidden).
    private int HitTestTodoRow(Point p)
    {
        if (!(ShowFullPanel && TodosStripVisible && _todosExpanded)) return -1;

        double top = TodosTop + TodoHeaderHeight;
        double lineH = HypertreeLineHeight;
        int count = TodoLineCount;
        if (p.Y < top || p.Y >= top + count * lineH) return -1;

        int index = (int)((p.Y - top) / lineH);
        return index >= 0 && index < count ? index : -1;
    }

    // Right-click menu for a todo line: complete it, or open the full list. On the empty "add one…" prompt
    // there's nothing to complete, so it offers just the open item.
    private void ShowTodoMenu(int index)
    {
        if (index >= 0 && index < _topTodos.Count)
        {
            var id = _topTodos[index].Id;
            ShowFlyout(new List<Control>
            {
                MenuItem("Complete", () => TodoCompleteRequested?.Invoke(id)),
                new Separator(),
                MenuItem("Open Todos…", () => TodosRequested?.Invoke()),
            });
        }
        else
        {
            ShowFlyout(new List<Control> { MenuItem("Open Todos…", () => TodosRequested?.Invoke()) });
        }
    }
}
