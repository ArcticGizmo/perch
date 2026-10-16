using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Perch.Avalonia.Rendering;
using Perch.Avalonia.Theming;
using Perch.Data;
using Perch.Data.Work;

namespace Perch.Avalonia.Views;

/// <summary>A work item as the overlay draws it (docs/work-items-plan.md): its identity, and which of the sessions
/// it holds are on the roster right now. Built by the app from the store + the latest scan.</summary>
internal sealed record OverlayWorkItem(
    string Id, string Name, string Colour, WorkItemStatus Status, IReadOnlySet<string> SessionIds, int EndedCount);

/// <summary>
/// Work items in the sessions section (docs/work-items-plan.md W1). When the user has active or paused items, the
/// session list is grouped: each item is a collapsible header (chevron, colour swatch, name, the most urgent status
/// among its sessions plus how many want you, and a "+" that starts a session inside it), with its live sessions
/// below it wearing a thin stripe in its colour. Sessions in no item gather under "Not in a work item", then the
/// Autonomous group as before. A paused item is one dimmed line that opens the item; a done item isn't shown. With no
/// items at all, the list is exactly what it was.
///
/// <para>A session in two items is listed under both (D2). The expand state is per item and persisted by the app
/// (<c>AppSettings.CollapsedWorkItems</c>), seeded through <see cref="SetCollapsedWorkItems"/> and written back from
/// <see cref="WorkItemExpandChanged"/>, the same shape as the Friends section.</para>
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

    // Whether the roster is grouped at all: only once there's an item worth showing.
    private bool WorkGrouping => _workItems.Count > 0;

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
            if (item.Status == WorkItemStatus.Active && WorkExpanded(item.Id))
                foreach (var s in members) AddSessionRows(rows, s, item);
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
        if (item.EndedCount > 0)
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

        DrawChevron(ctx, HorizPad + 4, midY, WorkExpanded(LooseGroupKey));
        var capFt = OverlayDraw.Text("Not in a work item", FeedCaptionSize, MutedBrush, FontWeight.SemiBold);
        OverlayDraw.TextLeftMid(ctx, capFt, HorizPad + 14, midY);
        var nFt = OverlayDraw.Text(row.SectionCount.ToString(), FeedCaptionSize, MutedBrush);
        OverlayDraw.TextLeftMid(ctx, nFt, width - HorizPad - nFt.Width, midY);
    }

    // The thin stripe in the item's colour down the left edge of a session (or sub-agent) row inside an item.
    // Inset from the panel edge so the dev/replay border (2px) and the rounded corner don't swallow it.
    private static void DrawWorkStripe(DrawingContext ctx, OverlayWorkItem item, double top, double height) =>
        ctx.FillRectangle(Palette.WorkHueBrush(item.Colour), new Rect(3, top + 2, WorkStripeWidth, height - 3));

    // ── Input ──

    // The hovered "+" box (row index), or -1.
    private int HitTestWorkPlus(Point p) => ShowFullPanel ? HitRect(_workPlusRects, p) : -1;

    // A click on a group header: the "+" starts a session in the item; elsewhere an active item (or the loose
    // group) toggles open/shut, and a paused one opens the item.
    private void RouteWorkHeaderClick(int row, Point p)
    {
        var r = _rows[row];
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
