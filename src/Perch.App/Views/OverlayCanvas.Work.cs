using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Perch.Avalonia.Rendering;
using Perch.Avalonia.Theming;
using Perch.Data;
using Perch.Data.Work;

namespace Perch.Avalonia.Views;

/// <summary>A work item as the overlay draws it (docs/work-items-plan.md): its identity, every session it holds (the
/// overlay groups whichever are on its roster), and the ones that have ended (no longer on the roster), newest first.
/// Built by the app from the store + the latest scan.</summary>
internal sealed record OverlayWorkItem(
    string Id, string Name, string Colour, WorkItemStatus Status, IReadOnlySet<string> SessionIds,
    IReadOnlyList<OverlayEndedMember> Ended)
{
    public int EndedCount => Ended.Count;
}

/// <summary>A member session that has ended, from the item's snapshot of it: enough to name it and resume it.</summary>
internal sealed record OverlayEndedMember(string SessionId, string Title, string? Cwd);

/// <summary>
/// Work items in the sessions section (docs/work-items-plan.md W1). When the user has active or paused items, the
/// session list is grouped: each item is a collapsible header (chevron, colour swatch, name, the most urgent status
/// among its sessions plus how many want you, and a "+" that starts a session inside it), with its live sessions
/// below it wearing a thin stripe in its colour, then the ones that have ended as compact faded lines (the newest few;
/// the rest are a "N more" line that opens the item). Sessions in no item gather under "Not in a work item", then the
/// Autonomous group as before. A paused item is one dimmed line that opens the item; a done item isn't shown. With no
/// items at all, the list is exactly what it was.
///
/// <para>A session in two items is listed under both (D2). The expand state is per item and persisted by the app
/// (<c>AppSettings.CollapsedWorkItems</c>), seeded through <see cref="SetCollapsedWorkItems"/> and written back from
/// <see cref="WorkItemExpandChanged"/>, the same shape as the Friends section.</para>
///
/// <para>A session row or an ended line can be dragged onto another group: dropped on an item (its header or any line
/// under it) it moves there, out of the item it was dragged from; dropped on "Not in a work item" it leaves its item
/// (an ended one then drops off the overlay, as it would from the window's ✕). The "+ New
/// session" row carries a Work items button (a briefcase) beside Recent and the Roost.</para>
/// </summary>
public sealed partial class OverlayCanvas
{
    // The id the "Not in a work item" group's expand state is stored under.
    internal const string LooseGroupKey = "~loose";

    private IReadOnlyList<OverlayWorkItem> _workItems = [];
    private readonly HashSet<string> _collapsedWork = new(StringComparer.Ordinal);

    // The "+" box on each active item's header, keyed by row index (captured at paint), and which one is hovered.
    private readonly Dictionary<int, Rect> _workPlusRects = new();
    private int _hoveredWorkPlus = -1;

    /// <summary>The items to group the roster by (active first, then paused; done ones are left out by the app).</summary>
    internal void SetWorkItems(IReadOnlyList<OverlayWorkItem> items)
    {
        if (ReferenceEquals(items, _workItems)) return;
        _workItems = items;
        Update(_sessions);
    }

    /// <summary>Seeds the collapsed groups (item ids, plus <see cref="LooseGroupKey"/>) from settings.</summary>
    internal void SetCollapsedWorkItems(IEnumerable<string> ids)
    {
        _collapsedWork.Clear();
        foreach (var id in ids) _collapsedWork.Add(id);
        Update(_sessions);
    }

    /// <summary>A group's header was clicked open or shut: (item id or <see cref="LooseGroupKey"/>, expanded).</summary>
    internal event Action<string, bool>? WorkItemExpandChanged;

    /// <summary>"+" on an item's header, or "New session here" from its menu: start a session inside it.</summary>
    internal event Action<string>? WorkItemNewSessionRequested;

    /// <summary>Open the Work items window, on this item when an id is given (null = the list).</summary>
    internal event Action<string?>? WorkItemOpenRequested;

    /// <summary>"Log a note…" on an item: open it with the log box focused.</summary>
    internal event Action<string>? WorkItemLogRequested;

    /// <summary>A status change from an item's menu (pause, resume, done).</summary>
    internal event Action<string, WorkItemStatus>? WorkItemStatusRequested;

    /// <summary>A session row's "Work items ▸" tick (true = add to the item, false = take it out).</summary>
    internal event Action<string, ClaudeSession, bool>? WorkItemMembershipRequested;

    /// <summary>"New work item from this session…": create an item named after the session, with it inside.</summary>
    internal event Action<ClaudeSession>? WorkItemFromSessionRequested;

    /// <summary>A session (live or ended) dragged from one group to another: (session id, title, cwd, the item it
    /// leaves or null, the item it joins or null). Either item may be null, never both.</summary>
    internal event Action<string, string, string?, string?, string?>? WorkItemMoveRequested;

    /// <summary>An ended member's "Take out of this item": (item id, session id).</summary>
    internal event Action<string, string>? WorkItemRemoveEndedRequested;

    /// <summary>An ended member's "Resume in Perch": (session id, cwd).</summary>
    internal event Action<string, string>? WorkEndedResumeRequested;

    /// <summary>An ended member's "Resume in terminal": (session id, cwd).</summary>
    internal event Action<string, string>? WorkEndedTerminalRequested;

    // How many ended members an expanded item lists before a "N more" line.
    private const int EndedPreview = 5;
    private const double EndedRowHeight = 24;

    // Whether the roster is grouped at all: only once there's an item worth showing, and the user hasn't switched to
    // the flat list (the grouping button, AppSettings.GroupByWorkItem).
    private bool WorkGrouping => _groupByWork && _workItems.Count > 0;
    private bool _groupByWork = true;

    /// <summary>Seeds grouped (true) or flat (false) from settings.</summary>
    internal void SetGroupByWork(bool on)
    {
        if (_groupByWork == on) return;
        _groupByWork = on;
        Update(_sessions);
    }

    /// <summary>The grouping button flipped the list: true = grouped by work item, false = flat.</summary>
    internal event Action<bool>? GroupByWorkChanged;

    private bool WorkExpanded(string key) => !_collapsedWork.Contains(key);

    // Builds the grouped rows: each item's header (and, when it's active and expanded, its sessions), then the
    // sessions in no item under their own header, then the Autonomous group of the loose background sessions.
    private void AddWorkGroupedRows(List<DisplayRow> rows, List<ClaudeSession> interactive, List<ClaudeSession> background)
    {
        var grouped = new HashSet<string>(StringComparer.Ordinal);
        var all = interactive.Concat(background).ToList();

        foreach (var item in _workItems)
        {
            var members = all.Where(s => item.SessionIds.Contains(s.SessionId))
                .OrderBy(s => s.DisplayName, StringComparer.OrdinalIgnoreCase).ToList();
            foreach (var m in members) grouped.Add(m.SessionId);

            rows.Add(new DisplayRow(null, null, members.Count, Header: RowHeader.WorkItem, Work: item));
            if (item.Status != WorkItemStatus.Active || !WorkExpanded(item.Id)) continue;
            foreach (var s in members) AddSessionRows(rows, s, item);
            foreach (var e in item.Ended.Take(EndedPreview))
                rows.Add(new DisplayRow(null, null, Header: RowHeader.WorkEnded, Work: item, Ended: e));
            if (item.Ended.Count > EndedPreview)
                rows.Add(new DisplayRow(null, null, item.Ended.Count - EndedPreview, Header: RowHeader.WorkMore, Work: item));
        }

        var loose = interactive.Where(s => !grouped.Contains(s.SessionId)).ToList();
        if (loose.Count > 0)
        {
            rows.Add(new DisplayRow(null, null, loose.Count, Header: RowHeader.Loose));
            if (WorkExpanded(LooseGroupKey))
                foreach (var s in loose) AddSessionRows(rows, s);
        }

        var looseBackground = background.Where(s => !grouped.Contains(s.SessionId)).ToList();
        if (looseBackground.Count > 0)
        {
            rows.Add(new DisplayRow(null, null, looseBackground.Count, Header: RowHeader.Autonomous));
            if (_autonomousExpanded)
                foreach (var s in looseBackground) AddSessionRows(rows, s);
        }
        else _autonomousExpanded = false;
    }

    // The sessions an item's header summarises: the live ones on the roster that belong to it.
    private IEnumerable<ClaudeSession> WorkMembers(OverlayWorkItem item) =>
        _sessions.Where(s => item.SessionIds.Contains(s.SessionId));

    // The header's light: the most urgent state among the item's sessions (error > waiting on you > done and
    // unread > working), null when it has none on the roster. The count is how many want you.
    private (Color? Light, int Wanting) WorkLight(OverlayWorkItem item)
    {
        bool error = false, awaiting = false, attention = false, running = false, any = false;
        int wanting = 0;
        foreach (var s in WorkMembers(item))
        {
            any = true;
            switch (s.Status)
            {
                case SessionStatus.ApiError: error = true; wanting++; break;
                case SessionStatus.AwaitingInput: awaiting = true; wanting++; break;
                case SessionStatus.NeedsAttention: attention = true; wanting++; break;
                case SessionStatus.Running: running = true; break;
            }
        }
        Color? light = error ? ApiErrorColor : awaiting ? AwaitingColor : attention ? AttentionColor
                     : running ? RunningColor : any ? IdleColor : null;
        return (light, wanting);
    }

    // ── Painting ──

    private const double WorkSwatchSize = 8;
    private const double PausedOpacity = 0.55;
    private const double WorkStripeWidth = 3;

    private void DrawWorkHeaderRow(DrawingContext ctx, int rowIndex, DisplayRow row, double top, double width)
    {
        var item = row.Work!;
        double midY = top + SectionRowHeight / 2;
        bool paused = item.Status == WorkItemStatus.Paused;
        bool expanded = !paused && WorkExpanded(item.Id);
        ctx.DrawLine(SepPen, new Point(HorizPad, top), new Point(width - HorizPad, top));

        // The "+" box at the far right (active items only): its own hover, like the Friends header's.
        const double box = 18;
        double boxCx = width - HorizPad - box / 2 + 2;
        Rect plus = default;
        if (!paused && !RearrangeMode)
        {
            plus = new Rect(boxCx - box / 2, midY - box / 2, box, box);
            _workPlusRects[rowIndex] = plus;
        }

        bool hoverPlus = rowIndex == _hoveredWorkPlus;
        if (rowIndex == _hoveredRow && !hoverPlus)
            OverlayDraw.Panel(ctx, new Rect(HorizPad - 4, top + 3, width - 2 * (HorizPad - 4), SectionRowHeight - 6),
                FeedHoverBrush, null, 6);
        if (rowIndex == _workDropRow) DrawDropTarget(ctx, top, width);

        using (ctx.PushOpacity(paused ? PausedOpacity : 1))
            DrawWorkHeaderBody(ctx, item, plus, midY, width, expanded, paused);

        if (plus.Width > 0)
        {
            if (hoverPlus) OverlayDraw.Panel(ctx, plus, FeedHoverBrush, null, 5);
            DrawPlusGlyph(ctx, hoverPlus ? Palette.AccentBrush : MutedBrush, boxCx, midY);
        }
    }

    // Everything on a header left of its "+": chevron, swatch, name, and the right-hand status cluster.
    private void DrawWorkHeaderBody(DrawingContext ctx, OverlayWorkItem item, Rect plus, double midY, double width,
        bool expanded, bool paused)
    {
        DrawChevron(ctx, HorizPad + 4, midY, expanded);
        double x = HorizPad + 14;
        var swatch = new Rect(x, midY - WorkSwatchSize / 2, WorkSwatchSize, WorkSwatchSize);
        OverlayDraw.Panel(ctx, swatch, Palette.WorkHueBrush(item.Colour), null, 2);
        x += WorkSwatchSize + 7;

        // Right cluster, built inward from the "+": the status light + how many want you, then "N ended".
        double right = plus.Width > 0 ? plus.Left - 4 : width - HorizPad;
        var (light, wanting) = WorkLight(item);
        if (wanting > 0 && light is { } lc)
        {
            var nFt = OverlayDraw.Text(wanting.ToString(), FeedCaptionSize, OverlayDraw.Brush(lc), FontWeight.SemiBold);
            right -= nFt.Width;
            OverlayDraw.TextLeftMid(ctx, nFt, right, midY);
            right -= 4;
        }
        if (light is { } c)
        {
            ctx.DrawEllipse(OverlayDraw.Brush(c), null, new Point(right - 3.5, midY), 3.5, 3.5);
            right -= 11;
        }
        // Collapsed only: an expanded item lists its ended sessions below it.
        if (item.EndedCount > 0 && !expanded)
        {
            var endedFt = OverlayDraw.Text($"{item.EndedCount} ended", FeedCaptionSize, MutedBrush);
            if (right - endedFt.Width > x + 60)
            {
                right -= endedFt.Width;
                OverlayDraw.TextLeftMid(ctx, endedFt, right, midY);
                right -= 8;
            }
        }

        string name = paused ? item.Name + " · paused" : item.Name;
        var weight = paused ? FontWeight.Normal : FontWeight.SemiBold;
        var shown = OverlayDraw.Truncate(name, NameSize, Math.Max(0, right - x - 4), weight);
        OverlayDraw.TextLeftMid(ctx, OverlayDraw.Text(shown, NameSize, Palette.TitleBrush, weight), x, midY);
    }

    // "Not in a work item": the same collapsible header style, caption + count, no swatch and no "+".
    private void DrawLooseHeaderRow(DrawingContext ctx, int rowIndex, DisplayRow row, double top, double width)
    {
        double midY = top + SectionRowHeight / 2;
        ctx.DrawLine(SepPen, new Point(HorizPad, top), new Point(width - HorizPad, top));
        if (rowIndex == _hoveredRow)
            OverlayDraw.Panel(ctx, new Rect(HorizPad - 4, top + 3, width - 2 * (HorizPad - 4), SectionRowHeight - 6),
                FeedHoverBrush, null, 6);
        if (rowIndex == _workDropRow) DrawDropTarget(ctx, top, width);

        DrawChevron(ctx, HorizPad + 4, midY, WorkExpanded(LooseGroupKey));
        var capFt = OverlayDraw.Text("Not in a work item", FeedCaptionSize, MutedBrush, FontWeight.SemiBold);
        OverlayDraw.TextLeftMid(ctx, capFt, HorizPad + 14, midY);
        var nFt = OverlayDraw.Text(row.SectionCount.ToString(), FeedCaptionSize, MutedBrush);
        OverlayDraw.TextLeftMid(ctx, nFt, width - HorizPad - nFt.Width, midY);
    }

    // An ended member under an expanded item: a hollow dot, its name and "ended", faded like a not-running row. Click
    // it for resume / history / take out.
    private void DrawWorkEndedRow(DrawingContext ctx, int rowIndex, DisplayRow row, double top, double width)
    {
        double midY = top + EndedRowHeight / 2;
        if (rowIndex == _hoveredRow) ctx.FillRectangle(RowHoverBrush, new Rect(1, top, width - 2, EndedRowHeight));
        DrawWorkStripe(ctx, row.Work!, top, EndedRowHeight);

        using var dim = ctx.PushOpacity(DormantRowOpacity);
        ctx.DrawEllipse(null, OverlayDraw.Pen(MutedBrush, 1.2), new Point(HorizPad + 4, midY), 3.5, 3.5);
        var tag = OverlayDraw.Text("ended", StatusSize, MutedBrush);
        double right = width - HorizPad - tag.Width;
        OverlayDraw.TextLeftMid(ctx, tag, right, midY);
        double x = HorizPad + 14;
        var shown = OverlayDraw.Truncate(row.Ended!.Title, SubNameSize, Math.Max(0, right - x - 8));
        OverlayDraw.TextLeftMid(ctx, OverlayDraw.Text(shown, SubNameSize, Palette.FgBrush), x, midY);
    }

    // "N more ended…" after the first few: opens the item, where they're all listed.
    private void DrawWorkMoreRow(DrawingContext ctx, int rowIndex, DisplayRow row, double top, double width)
    {
        double midY = top + EndedRowHeight / 2;
        bool hover = rowIndex == _hoveredRow;
        if (hover) ctx.FillRectangle(RowHoverBrush, new Rect(1, top, width - 2, EndedRowHeight));
        DrawWorkStripe(ctx, row.Work!, top, EndedRowHeight);
        var ft = OverlayDraw.Text($"{row.SectionCount} more ended…", FeedCaptionSize, hover ? Palette.AccentBrush : MutedBrush);
        OverlayDraw.TextLeftMid(ctx, ft, HorizPad + 14, midY);
    }

    // The header a dragged session would drop on: an accent outline over the band.
    private void DrawDropTarget(DrawingContext ctx, double top, double width) =>
        OverlayDraw.Panel(ctx, new Rect(HorizPad - 4, top + 3, width - 2 * (HorizPad - 4), SectionRowHeight - 6),
            FeedHoverBrush, OverlayDraw.Pen(Palette.AccentBrush, 1.5), 6);

    // The dragged session's name in a small chip beside the pointer, kept inside the panel.
    private void DrawDragGhost(DrawingContext ctx, double width)
    {
        if (!_dragging || _drag is not { } drag) return;
        var ft = OverlayDraw.Text(OverlayDraw.Truncate(drag.Title, NameSize, 180), NameSize, Palette.TitleBrush);
        double w = ft.Width + 16, h = ft.Height + 8;
        var r = new Rect(Math.Clamp(_dragAt.X + 12, 2, Math.Max(2, width - w - 2)), _dragAt.Y + 6, w, h);
        OverlayDraw.Panel(ctx, r, Palette.FormBgBrush, OverlayDraw.Pen(Palette.AccentBrush, 1), 6);
        OverlayDraw.TextLeftMid(ctx, ft, r.X + 8, r.Center.Y);
    }

    // The thin stripe in the item's colour down the left edge of a session (or sub-agent) row inside an item.
    // Inset from the panel edge so the dev/replay border (2px) and the rounded corner don't swallow it.
    private static void DrawWorkStripe(DrawingContext ctx, OverlayWorkItem item, double top, double height) =>
        ctx.FillRectangle(Palette.WorkHueBrush(item.Colour), new Rect(3, top + 2, WorkStripeWidth, height - 3));

    // ── Input ──

    // The hovered "+" box (row index), or -1.
    private int HitTestWorkPlus(Point p) => ShowFullPanel ? HitRect(_workPlusRects, p) : -1;

    // The rows this file owns: the group headers and the ended / "N more" lines under an item.
    private static bool IsWorkRow(RowHeader h) => h is RowHeader.WorkItem or RowHeader.Loose or RowHeader.WorkEnded or RowHeader.WorkMore;

    // A click on one of them. An ended line opens its menu, "N more" opens the item. On a header the "+" starts a
    // session in the item; elsewhere an active item (or the loose group) toggles open/shut, and a paused one opens
    // the item.
    private void RouteWorkRowClick(int row, Point p)
    {
        var r = _rows[row];
        if (r is { Header: RowHeader.WorkEnded, Work: { } endedIn, Ended: { } ended })
        {
            ShowFlyout(EndedMenu(endedIn, ended));
            return;
        }
        if (r is { Header: RowHeader.WorkMore, Work: { } moreIn })
        {
            WorkItemOpenRequested?.Invoke(moreIn.Id);
            return;
        }
        if (r.Header == RowHeader.Loose)
        {
            ToggleWorkGroup(LooseGroupKey);
            return;
        }
        if (r.Work is not { } item) return;
        if (HitTestWorkPlus(p) == row)
        {
            WorkItemNewSessionRequested?.Invoke(item.Id);
            return;
        }
        if (item.Status == WorkItemStatus.Paused) WorkItemOpenRequested?.Invoke(item.Id);
        else ToggleWorkGroup(item.Id);
    }

    private void ToggleWorkGroup(string key)
    {
        bool nowExpanded = !WorkExpanded(key);
        if (nowExpanded) _collapsedWork.Remove(key); else _collapsedWork.Add(key);
        Update(_sessions);
        WorkItemExpandChanged?.Invoke(key, nowExpanded);
    }

    // An ended member's menu (left or right click): resume it here or in a terminal, read it, or take it out.
    private List<Control> EndedMenu(OverlayWorkItem item, OverlayEndedMember e)
    {
        var open = new List<Control>();
        if (e.Cwd is { Length: > 0 } cwd)
        {
            open.Add(MenuItem("Resume in Perch", () => WorkEndedResumeRequested?.Invoke(e.SessionId, cwd)));
            open.Add(MenuItem("Resume in terminal", () => WorkEndedTerminalRequested?.Invoke(e.SessionId, cwd)));
        }
        open.Add(MenuItem("View history", () => HistoryRequested?.Invoke(e.SessionId)));

        var items = new List<Control>();
        AddMenuGroups(items, open,
            [MenuItem("Take out of this item", () => WorkItemRemoveEndedRequested?.Invoke(item.Id, e.SessionId))]);
        return items;
    }

    // ── Dragging a session between groups ──

    private static readonly Cursor DragCursor = new(StandardCursorType.DragMove);
    private static readonly Cursor NoDropCursor = new(StandardCursorType.No);
    private const double DragThreshold = 4;

    // What's being dragged: a session by id, with the name and folder an item keeps for it, and the item its row was
    // listed under (null = not in one). A live row and an ended line both make one.
    private sealed record SessionDrag(string SessionId, string Title, string? Cwd, OverlayWorkItem? From);

    // Armed by a left press on a session (or sub-agent) row or an ended line; a drag once the pointer passes the
    // threshold.
    private SessionDrag? _drag;
    private Point _dragStart, _dragAt;
    private bool _dragging;
    private int _workDropRow = -1;   // the header the drop would land on (painted as the target)

    private void ArmSessionDrag(Point p)
    {
        _drag = null;
        _dragging = false;
        _workDropRow = -1;
        if (!WorkGrouping || RearrangeMode || HitResizeEdge(p) != ResizeEdge.None) return;
        int row = HitTestRow(p);
        if (row < 0) return;
        _drag = _rows[row] switch
        {
            { Header: RowHeader.None, Session: { } s } r => new SessionDrag(s.SessionId, s.DisplayName, s.Cwd, r.Work),
            { Header: RowHeader.WorkEnded, Ended: { } e } r => new SessionDrag(e.SessionId, e.Title, e.Cwd, r.Work),
            _ => null,
        };
        _dragStart = p;
    }

    // The pointer moved with a drag armed. False while it's still a click (inside the threshold), so hover goes on.
    private bool UpdateSessionDrag(Point p)
    {
        if (_drag is not { } drag) return false;
        if (!_dragging)
        {
            if (Math.Abs(p.X - _dragStart.X) <= DragThreshold && Math.Abs(p.Y - _dragStart.Y) <= DragThreshold) return false;
            _dragging = true;
            _hoveredRow = -1;
            ArmDwell(TipKind.None, -1);
        }
        _dragAt = p;
        var target = DropTargetAt(p, drag.From);
        _workDropRow = target.Valid ? target.HeaderRow : -1;
        Cursor = target.Valid ? DragCursor : NoDropCursor;
        InvalidateVisual();
        return true;
    }

    // Released: move the session into the group under the pointer. True when a drag was in flight (so it's no click).
    private bool FinishSessionDrag(Point p)
    {
        bool dragged = _dragging;
        var drag = _drag;
        CancelSessionDrag();
        if (!dragged || drag is null) return dragged;
        Cursor = Cursor.Default;

        var target = DropTargetAt(p, drag.From);
        if (!target.Valid) return true;
        // Into the target unless it's already there (a session in both items just leaves the one it came from).
        string? to = target.Item is { } item && !item.SessionIds.Contains(drag.SessionId) ? item.Id : null;
        WorkItemMoveRequested?.Invoke(drag.SessionId, drag.Title, drag.Cwd, drag.From?.Id, to);
        return true;
    }

    private void CancelSessionDrag()
    {
        bool repaint = _dragging || _workDropRow != -1;
        _drag = null;
        _dragging = false;
        _workDropRow = -1;
        if (repaint) InvalidateVisual();
    }

    // Where a drop at p lands: walk up from the row under the pointer to its group's header. An item (its header, one
    // of its sessions, an ended line) or "Not in a work item" is a target; the Autonomous group isn't, and nor is the
    // group the session was dragged from.
    private (bool Valid, OverlayWorkItem? Item, int HeaderRow) DropTargetAt(Point p, OverlayWorkItem? from)
    {
        for (int i = HitTestRow(p); i >= 0; i--)
        {
            switch (_rows[i].Header)
            {
                case RowHeader.WorkItem:
                    var item = _rows[i].Work!;
                    return (item.Id != from?.Id, item, i);
                case RowHeader.Loose:
                    return (from is not null, null, i);
                case RowHeader.Autonomous:
                    return default;
            }
        }
        return default;
    }

    // A capture taken away mid-drag (a window stealing the pointer) drops the drag rather than leaving it hanging.
    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        base.OnPointerCaptureLost(e);
        if (_drag is not null) { CancelSessionDrag(); Cursor = Cursor.Default; }
    }

    // ── The Work items button ──
    // A briefcase on the "+ New session" row, left of Recent, that opens the Work items window.

    private Rect _workButtonRect;   // laid out by DrawNewSessionRow each paint; empty in Rearrange preview
    private bool _hoveredWorkButton;

    private bool WorkButtonVisible => !RearrangeMode;

    private void DrawWorkButton(DrawingContext ctx, Rect box)
    {
        var c = box.Center;
        if (_hoveredWorkButton) OverlayDraw.Panel(ctx, box, FeedHoverBrush, null, 5);
        DrawWorkGlyph(ctx, _hoveredWorkButton ? Palette.AccentBrush : MutedBrush, c.X, c.Y);
    }

    // A briefcase: a 12×8 body with a seam across it and a handle on top; ~12×11 around (cx, cy), stroked like the
    // Roost and Recent glyphs.
    private static void DrawWorkGlyph(DrawingContext ctx, IBrush brush, double cx, double cy)
    {
        var pen = OverlayDraw.Pen(brush, 1.2);
        ctx.DrawRectangle(null, pen, new RoundedRect(new Rect(cx - 6, cy - 2.5, 12, 8), 1.5));
        ctx.DrawLine(pen, new Point(cx - 6, cy + 1), new Point(cx + 6, cy + 1));
        ctx.DrawLine(pen, new Point(cx - 2.5, cy - 2.5), new Point(cx - 2.5, cy - 5));
        ctx.DrawLine(pen, new Point(cx - 2.5, cy - 5), new Point(cx + 2.5, cy - 5));
        ctx.DrawLine(pen, new Point(cx + 2.5, cy - 5), new Point(cx + 2.5, cy - 2.5));
    }

    private bool OverWorkButton(Point p) => ShowFullPanel && _workButtonRect.Width > 0 && _workButtonRect.Contains(p);

    // The dwell tooltip: what the button opens, and how many items there are.
    private void ShowWorkButtonTooltip()
    {
        if (_workButtonRect.Width <= 0) return;
        int active = _workItems.Count(i => i.Status == WorkItemStatus.Active);
        int paused = _workItems.Count(i => i.Status == WorkItemStatus.Paused);
        string detail = _workItems.Count == 0 ? "Group sessions into pieces of work"
            : paused > 0 ? $"{active} active · {paused} paused" : $"{active} active";
        var lines = new List<OverlayTooltip.Line>
        {
            new("Work items", OverlayTooltip.FgColor, true),
            new(detail, OverlayTooltip.MutedColor, false),
        };
        Tooltip().ShowLines(lines, ToScreen(_workButtonRect.Left - 60, _workButtonRect.Bottom + 4));
    }

    // ── The grouping button ──
    // At the far right of the "+ New session" row, right of the Roost: switches the list between grouped by work item
    // and flat. Its glyph is the layout showing now. Only there once there's an item to group by.

    private Rect _groupToggleRect;   // laid out by DrawNewSessionRow each paint
    private bool _hoveredGroupToggle;

    private bool GroupToggleVisible => _workItems.Count > 0 && !RearrangeMode;

    private void DrawGroupToggle(DrawingContext ctx, Rect box)
    {
        var c = box.Center;
        if (_hoveredGroupToggle) OverlayDraw.Panel(ctx, box, FeedHoverBrush, null, 5);
        DrawGroupGlyph(ctx, _hoveredGroupToggle ? Palette.AccentBrush : MutedBrush, c.X, c.Y, _groupByWork);
    }

    // Grouped: two short heading bars, each over an indented line. Flat: four even lines. ~12×10 around (cx, cy).
    private static void DrawGroupGlyph(DrawingContext ctx, IBrush brush, double cx, double cy, bool grouped)
    {
        var pen = OverlayDraw.Pen(brush, 1.2);
        if (grouped)
        {
            ctx.DrawLine(pen, new Point(cx - 6, cy - 4.5), new Point(cx - 1, cy - 4.5));
            ctx.DrawLine(pen, new Point(cx - 3, cy - 1.5), new Point(cx + 6, cy - 1.5));
            ctx.DrawLine(pen, new Point(cx - 6, cy + 1.5), new Point(cx - 1, cy + 1.5));
            ctx.DrawLine(pen, new Point(cx - 3, cy + 4.5), new Point(cx + 6, cy + 4.5));
        }
        else
        {
            for (int i = 0; i < 4; i++)
                ctx.DrawLine(pen, new Point(cx - 6, cy - 4.5 + i * 3), new Point(cx + 6, cy - 4.5 + i * 3));
        }
    }

    private bool OverGroupToggle(Point p) => ShowFullPanel && _groupToggleRect.Width > 0 && _groupToggleRect.Contains(p);

    private void ToggleGroupByWork()
    {
        _groupByWork = !_groupByWork;
        CancelSessionDrag();
        Update(_sessions);
        GroupByWorkChanged?.Invoke(_groupByWork);
    }

    private void ShowGroupToggleTooltip()
    {
        if (_groupToggleRect.Width <= 0) return;
        var lines = new List<OverlayTooltip.Line>
        {
            new(_groupByWork ? "Grouped by work item" : "Flat list", OverlayTooltip.FgColor, true),
            new(_groupByWork ? "Click for one flat list" : "Click to group by work item", OverlayTooltip.MutedColor, false),
        };
        Tooltip().ShowLines(lines, ToScreen(_groupToggleRect.Left - 100, _groupToggleRect.Bottom + 4));
    }

    // The right-click menu of an item's header.
    private List<Control> WorkHeaderMenu(OverlayWorkItem item)
    {
        var open = new List<Control> { MenuItem("Open work item", () => WorkItemOpenRequested?.Invoke(item.Id)) };
        if (item.Status == WorkItemStatus.Active)
            open.Add(MenuItem("New session here", () => WorkItemNewSessionRequested?.Invoke(item.Id)));
        open.Add(MenuItem("Log a note…", () => WorkItemLogRequested?.Invoke(item.Id)));

        var status = new List<Control>();
        if (item.Status == WorkItemStatus.Active)
            status.Add(MenuItem("Pause", () => WorkItemStatusRequested?.Invoke(item.Id, WorkItemStatus.Paused)));
        else
            status.Add(MenuItem("Resume", () => WorkItemStatusRequested?.Invoke(item.Id, WorkItemStatus.Active)));
        status.Add(MenuItem("Mark done…", () => WorkItemStatusRequested?.Invoke(item.Id, WorkItemStatus.Done)));

        var all = new List<Control> { MenuItem("All work items…", () => WorkItemOpenRequested?.Invoke(null)) };

        var items = new List<Control>();
        AddMenuGroups(items, open, status, all);
        return items;
    }

    /// <summary>Every item a session could go in, for its row's "Work items ▸" submenu: active and paused ones (done
    /// items aren't offered). Fed by the app with the overlay list, so it's the same set the headers show.</summary>
    private IReadOnlyList<OverlayWorkItem> MenuWorkItems => _workItems;

    // A session row's "Work items ▸" submenu: one checkable entry per item (ticking adds, unticking removes, so a
    // session can be in several), then "New work item from this session…" and the window.
    private MenuItem WorkItemsSubmenu(ClaudeSession s)
    {
        var entries = new List<Control>();
        foreach (var item in MenuWorkItems)
        {
            bool member = item.SessionIds.Contains(s.SessionId);
            var mi = new MenuItem
            {
                Header = item.Status == WorkItemStatus.Paused ? item.Name + " (paused)" : item.Name,
                ToggleType = MenuItemToggleType.CheckBox,
                IsChecked = member,
                Icon = new Border
                {
                    Width = WorkSwatchSize, Height = WorkSwatchSize, CornerRadius = new CornerRadius(2),
                    Background = Palette.WorkHueBrush(item.Colour),
                },
            };
            mi.Click += (_, _) => WorkItemMembershipRequested?.Invoke(item.Id, s, !member);
            entries.Add(mi);
        }
        if (entries.Count > 0) entries.Add(new Separator());
        entries.Add(MenuItem("New work item from this session…", () => WorkItemFromSessionRequested?.Invoke(s)));
        entries.Add(MenuItem("All work items…", () => WorkItemOpenRequested?.Invoke(null)));
        return new MenuItem { Header = "Work items", ItemsSource = entries };
    }
}
