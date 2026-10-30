using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using Perch.Avalonia.Rendering;
using Perch.Avalonia.Theming;
using Perch.Data;
using Perch.Data.GitGraph;

namespace Perch.Avalonia.Views;

/// <summary>
/// The Tree window's commit graph: one owner-drawn surface that scrolls itself and paints only the rows in view, so
/// a 100k-commit history costs the same per frame as a 100-commit one. Columns: ref pills (right-aligned against
/// the graph), lanes and nodes, subject (with tags), author, age. A dirty worktree's WIP row is a dashed node whose
/// dashed tether runs down its own lane to its HEAD; a commit that is some worktree's HEAD wears a ring.
///
/// <para>Paint allocates nothing per frame beyond the hit list it reuses: text comes from
/// <see cref="OverlayDraw.Text"/>'s cache, pens and brushes are built once per <see cref="Colors"/>, and curves
/// are drawn as short line runs instead of geometry objects.</para>
/// </summary>
internal sealed class GraphCanvas : Control
{
    /// <summary>The colours the graph paints with: the Tree window's Aurora surfaces plus a lane set per polarity.</summary>
    public sealed record Colors(Color Background, Color RowHover, Color RowSelected, Color Text, Color Title, Color Muted,
        Color Separator, Color[] Lanes, bool Light)
    {
        public static Colors For(bool light)
        {
            var a = AuroraPalette.For(light);
            var lanes = GraphLanes.For(light).Select(l => Color.FromRgb(l.R, l.G, l.B)).ToArray();
            return new(a.Raised, Blend(a.Raised, a.Text, light ? 0.04 : 0.05), Blend(a.Raised, a.Accent, light ? 0.12 : 0.18),
                a.Text, a.Title, a.Muted, a.Separator, lanes, light);
        }

        public IBrush LaneBrush(int lane) => OverlayDraw.Brush(Lanes[lane % Lanes.Length]);

        private static Color Blend(Color a, Color b, double t) => Color.FromRgb(
            (byte)(a.R + (b.R - a.R) * t), (byte)(a.G + (b.G - a.G) * t), (byte)(a.B + (b.B - a.B) * t));
    }

    private const double FontSize = 12.5, SmallSize = 11.5, PillSize = 11.5;
    private const double LaneW = 16, GraphPad = 12, MinGraphW = 44, MaxGraphW = 280;
    private const double DefaultRefsW = 188, MinRefsW = 70, MaxRefsW = 480, MaxLanesW = 900;
    private const double AuthorW = 136, WhenW = 62, Gap = 10, NarrowW = 760, MaxPillText = 160;

    // The pill column's width and, once the user has dragged it, the lane column's (null = sized to the graph).
    private double _refsW = DefaultRefsW;
    private double? _lanesW;

    // Column-edge resizing: which edge the pointer is over (0 = none, 1 = pills|lanes, 2 = lanes|subject), and the
    // edge being dragged with the width it started from.
    private int _edgeHover;
    private (int Edge, double StartX, double StartW)? _edgeDrag;
    private const double EdgeGrip = 4;
    private const double NodeR = 4.5, MergeR = 3.6, RingR = 8, WipR = 5.5;
    private const int CurveSteps = 8;

    private const int FlashMs = 900;

    private GraphSnapshot? _snap;
    private Colors _c = Colors.For(light: false);
    private int _sel = -1, _hover = -1;
    private int _hlLane = -1;
    private int _flashRow = -1;
    private long _flashStart;
    private global::Avalonia.Threading.DispatcherTimer? _flashTimer;
    private double _offset;
    private bool _moreRequested;
    private readonly double _rowH;

    // Built once per palette.
    private IPen[] _lane = [], _laneDash = [], _laneFaded = [], _ring = [], _ringBold = [], _pillBorder = [], _pillDash = [];
    private IBrush[] _laneFill = [], _laneFillFaded = [], _laneWash = [];
    private IBrush _bg = Brushes.Transparent, _hoverBg = Brushes.Transparent, _selBg = Brushes.Transparent,
        _text = Brushes.White, _title = Brushes.White, _muted = Brushes.Gray;
    private IPen _nodeEdge = new ImmutablePen(Brushes.Black), _sep = new ImmutablePen(Brushes.Gray), _dropPen = new ImmutablePen(Brushes.White);

    // Where the pills were painted, for hit-testing (drag sources and double-click targets in later checkpoints).
    private readonly List<(Rect Rect, int Row, GraphRef Ref)> _pillHits = new();

    public GraphCanvas()
    {
        Focusable = true;
        ClipToBounds = true;
        // Row height from the font's line height, never a fixed pixel count.
        _rowH = Math.Ceiling(OverlayDraw.Text("Ag", FontSize, Brushes.White).Height + 13);
        SetColors(_c);
    }

    /// <summary>The selected row changed (−1 when cleared).</summary>
    public event Action<int>? SelectionChanged;

    /// <summary>A row was double-clicked (anywhere but a pill) or Enter was pressed on it.</summary>
    public event Action<int>? RowActivated;

    /// <summary>A ref pill was double-clicked.</summary>
    public event Action<GraphRef>? RefActivated;

    /// <summary>A branch pill or a commit row was pressed and dragged past the threshold; the host runs the drag from
    /// here (the pointer stays captured by the canvas until release).</summary>
    public event Action<DragSource, IPointer>? DragRequested;

    /// <summary>The last loaded row came into view and the snapshot has more to load.</summary>
    public event Action? LoadMoreRequested;

    /// <summary>The extent, viewport or offset changed, for a host scroll bar.</summary>
    public event Action? ScrollChanged;

    public GraphSnapshot? Snapshot => _snap;
    public int SelectedRow => _sel;
    public double RowHeight => _rowH;
    public double Extent => (_snap?.Rows.Length ?? 0) * _rowH + (_snap?.HasMore == true ? _rowH : 0);
    public double Viewport => Bounds.Height;

    public double Offset
    {
        get => _offset;
        set
        {
            var v = Math.Clamp(value, 0, Math.Max(0, Extent - Viewport));
            if (v == _offset) return;
            _offset = v;
            InvalidateVisual();
            ScrollChanged?.Invoke();
        }
    }

    public void SetColors(Colors c)
    {
        _c = c;
        IImmutableSolidColorBrush B(Color col, double o = 1) => (IImmutableSolidColorBrush)OverlayDraw.Brush(col, o);
        var dash = new ImmutableDashStyle([2.2, 2.6], 0);
        _lane = c.Lanes.Select(l => (IPen)new ImmutablePen(B(l), 2, null, PenLineCap.Round)).ToArray();
        _laneDash = c.Lanes.Select(l => (IPen)new ImmutablePen(B(l), 1.7, dash, PenLineCap.Round)).ToArray();
        _laneFaded = c.Lanes.Select(l => (IPen)new ImmutablePen(B(l, 0.22), 2, null, PenLineCap.Round)).ToArray();
        _laneFillFaded = c.Lanes.Select(l => (IBrush)B(l, 0.3)).ToArray();
        _ring = c.Lanes.Select(l => (IPen)new ImmutablePen(B(l), 1.8)).ToArray();
        _ringBold = c.Lanes.Select(l => (IPen)new ImmutablePen(B(l), 2.6)).ToArray();
        _pillBorder = c.Lanes.Select(l => (IPen)new ImmutablePen(B(l, 0.5), 1)).ToArray();
        _pillDash = c.Lanes.Select(l => (IPen)new ImmutablePen(B(l, 0.6), 1, new ImmutableDashStyle([3, 2], 0))).ToArray();
        _laneFill = c.Lanes.Select(l => (IBrush)B(l)).ToArray();
        _laneWash = c.Lanes.Select(l => (IBrush)B(l, c.Light ? 0.10 : 0.16)).ToArray();
        _bg = B(c.Background);
        _hoverBg = B(c.RowHover);
        _selBg = B(c.RowSelected);
        _text = B(c.Text);
        _title = B(c.Title);
        _muted = B(c.Muted);
        _nodeEdge = new ImmutablePen(B(c.Background), 2);
        _sep = new ImmutablePen(B(c.Separator), 1);
        _dropPen = new ImmutablePen(B(c.Title, 0.85), 1.8);
        InvalidateVisual();
    }

    /// <summary>
    /// Shows <paramref name="snap"/>. With <paramref name="keepPlace"/>, the selected commit stays selected (found by
    /// hash, so a refresh that adds rows above doesn't move the selection) and the scroll offset is kept.
    /// </summary>
    public void SetSnapshot(GraphSnapshot? snap, bool keepPlace)
    {
        string? selected = keepPlace && _snap is not null && _sel >= 0 && _sel < _snap.Commits.Count ? _snap.Commits[_sel].Hash : null;
        _snap = snap;
        _moreRequested = false;
        _hover = -1;
        int sel = selected is not null && snap is not null ? snap.RowOf(selected) : -1;
        if (!keepPlace)
            _offset = 0;
        _offset = Math.Clamp(_offset, 0, Math.Max(0, Extent - Viewport));
        bool changed = sel != _sel;
        _sel = sel;
        InvalidateVisual();
        ScrollChanged?.Invoke();
        if (changed)
            SelectionChanged?.Invoke(_sel);
    }

    public void Select(int row, bool scrollIntoView = true)
    {
        if (_snap is null) return;
        row = Math.Clamp(row, -1, _snap.Rows.Length - 1);
        if (scrollIntoView && row >= 0)
            EnsureVisible(row);
        if (row == _sel) return;
        _sel = row;
        InvalidateVisual();
        SelectionChanged?.Invoke(_sel);
    }

    /// <summary>The session working in each worktree (by path), shown on that worktree's WIP row.</summary>
    public void SetSessions(IReadOnlyDictionary<string, GraphRail.WorktreeSession> sessions)
    {
        _sessions = sessions;
        InvalidateVisual();
    }

    private IReadOnlyDictionary<string, GraphRail.WorktreeSession> _sessions = new Dictionary<string, GraphRail.WorktreeSession>();

    /// <summary>Fades every lane but <paramref name="lane"/> (−1 restores them all): hovering a worktree or branch in
    /// the rail shows where it runs.</summary>
    public void HighlightLane(int lane)
    {
        if (_hlLane == lane) return;
        _hlLane = lane;
        InvalidateVisual();
    }

    /// <summary>Selects <paramref name="row"/>, scrolls it into view and briefly washes it, so a jump from the rail or
    /// go-to is easy to find. No wash when the OS asks for reduced motion.</summary>
    public void Flash(int row)
    {
        Select(row);
        if (row < 0 || PlatformServices.MotionPreference.ReduceMotion) return;
        _flashRow = row;
        _flashStart = Environment.TickCount64;
        _flashTimer ??= new global::Avalonia.Threading.DispatcherTimer(TimeSpan.FromMilliseconds(40), global::Avalonia.Threading.DispatcherPriority.Render, (_, _) =>
        {
            if (Environment.TickCount64 - _flashStart > FlashMs) { _flashRow = -1; _flashTimer?.Stop(); }
            InvalidateVisual();
        });
        _flashTimer.Start();
    }

    /// <summary>Scrolls so <paramref name="row"/> is in view, centring it when it was off screen.</summary>
    public void EnsureVisible(int row)
    {
        double top = row * _rowH, bottom = top + _rowH;
        if (top < _offset || bottom > _offset + Viewport)
            Offset = top - (Viewport - _rowH) / 2;
    }

    /// <summary>
    /// What a drag would drop on at <paramref name="p"/>: a branch pill; else a worktree's WIP row; else a row's local
    /// branch (this worktree's first), so the drop needn't land exactly on the pill; else the worktree whose HEAD it is.
    /// </summary>
    public DropTarget? DropTargetAt(Point p)
    {
        if (_snap is null) return null;
        if (RefAt(p) is { Kind: GraphRefKind.LocalBranch or GraphRefKind.RemoteBranch } r) return DropTarget.Of(r);
        int row = RowAt(p.Y);
        if (row < 0) return null;
        if (_snap.WipAt(row) is { } wt) return DropTarget.Of(wt);
        var hash = _snap.Commits[row].Hash;
        var locals = _snap.RefsAt(hash).Where(x => x.Kind == GraphRefKind.LocalBranch).ToList();
        if (locals.Count > 0)
            return DropTarget.Of(locals.FirstOrDefault(x => x.Name == _snap.Current?.Branch) ?? locals[0]);
        return _snap.WorktreesAt(hash).FirstOrDefault() is { } at ? DropTarget.Of(at) : null;
    }

    /// <summary>Outlines what a drag would drop on (null clears it).</summary>
    public void SetDropHighlight(DropTarget? target)
    {
        if (target == _drop) return;
        _drop = target;
        InvalidateVisual();
    }

    private DropTarget? _drop;
    private (Point At, int Row)? _press;
    private const double DragThreshold = 6;

    /// <summary>Scrolls a step when <paramref name="y"/> (canvas coordinates) is near the top or bottom edge, for a drag
    /// that wants to reach rows out of view. Returns whether it moved.</summary>
    public bool AutoScroll(double y)
    {
        const double edge = 28;
        double step = y < edge ? -(edge - Math.Max(y, 0)) : y > Viewport - edge ? y - (Viewport - edge) : 0;
        if (step == 0) return false;
        double before = _offset;
        Offset += Math.Clamp(step, -edge, edge) * _rowH / 14;
        return _offset != before;
    }

    /// <summary>The pills the last paint drew, in canvas coordinates.</summary>
    internal IEnumerable<(Rect Rect, GraphRef Ref)> PaintedPills => _pillHits.Select(h => (h.Rect, h.Ref));

    /// <summary>The ref pill under <paramref name="p"/>, if any.</summary>
    public GraphRef? RefAt(Point p)
    {
        foreach (var h in _pillHits)
            if (h.Rect.Contains(p)) return h.Ref;
        return null;
    }

    public int RowAt(double y)
    {
        if (_snap is null) return -1;
        int r = (int)Math.Floor((y + _offset) / _rowH);
        return r >= 0 && r < _snap.Rows.Length ? r : -1;
    }

    private double GraphWidth => _lanesW
        ?? (_snap is null ? MinGraphW : Math.Clamp(GraphPad * 2 + _snap.Width * LaneW, MinGraphW, MaxGraphW));

    /// <summary>The user dragged a column edge; the host persists the widths (0 = lanes sized to the graph).</summary>
    public event Action<double, double>? ColumnsResized;

    /// <summary>Restores dragged column widths (0 keeps the default pill width / lanes sized to the graph).</summary>
    public void SetColumnWidths(double refs, double lanes)
    {
        _refsW = refs > 0 ? Math.Clamp(refs, MinRefsW, MaxRefsW) : DefaultRefsW;
        _lanesW = lanes > 0 ? Math.Clamp(lanes, MinGraphW, MaxLanesW) : null;
        InvalidateVisual();
    }

    // Which column edge is under x: the pills|lanes edge, or the lanes|subject edge.
    private int EdgeAt(double x) =>
        Math.Abs(x - _refsW) <= EdgeGrip ? 1 : Math.Abs(x - (_refsW + GraphWidth)) <= EdgeGrip ? 2 : 0;
    private double LaneX(int lane) => _refsW + GraphPad + lane * LaneW;
    private double RowMid(int row) => row * _rowH - _offset + _rowH / 2;

    // ---- paint ----

    public override void Render(DrawingContext ctx)
    {
        var size = Bounds.Size;
        ctx.FillRectangle(_bg, new Rect(size));
        _pillHits.Clear();
        if (_snap is null || _snap.Rows.Length == 0)
        {
            var empty = OverlayDraw.Text(_snap is null ? "Loading…" : "No commits", FontSize, _muted);
            ctx.DrawText(empty, new Point((size.Width - empty.Width) / 2, 24));
            return;
        }

        var s = _snap;
        int first = Math.Max(0, (int)Math.Floor(_offset / _rowH));
        int last = Math.Min(s.Rows.Length - 1, (int)Math.Ceiling((_offset + size.Height) / _rowH));
        double graphW = GraphWidth;
        double subjX = _refsW + graphW + Gap;
        double whenX = size.Width - WhenW - Gap;
        // In a narrow pane the subject matters more than the author, so the author column goes first.
        double authorX = whenX - (size.Width < NarrowW ? 0 : AuthorW);
        double subjW = Math.Max(40, authorX - Gap - subjX);

        for (int r = first; r <= last; r++)
        {
            var band = new Rect(0, r * _rowH - _offset, size.Width, _rowH);
            if (r == _sel) ctx.FillRectangle(_selBg, band);
            else if (r == _hover) ctx.FillRectangle(_hoverBg, band);
            if (r == _flashRow)
            {
                // Quantised so the brush cache sees a handful of opacities, not one per frame.
                double left = 1 - Math.Min(1, (Environment.TickCount64 - _flashStart) / (double)FlashMs);
                ctx.FillRectangle(OverlayDraw.Brush(_c.Lanes[0], Math.Round(left * 10) / 25), band);
            }
        }

        using (ctx.PushClip(new Rect(_refsW, 0, graphW, size.Height)))
        {
            // The band above each visible row, plus the one below the last (its upper end may be on screen).
            for (int r = first; r <= Math.Min(last + 1, s.Rows.Length - 1); r++)
                foreach (var e in s.Rows[r].Up)
                    Edge(ctx, e, RowMid(r - 1), RowMid(r), s.IsTether(r, e));
            if (last == s.Rows.Length - 1)
                foreach (var e in s.OpenEdges)
                    Edge(ctx, e, RowMid(last), RowMid(last) + _rowH, s.IsTether(last + 1, e));
        }

        for (int r = first; r <= last; r++)
            PaintRow(ctx, s, r, subjX, subjW, authorX, whenX);

        if ((_edgeDrag?.Edge ?? _edgeHover) is var edge and > 0)
        {
            double ex = edge == 1 ? _refsW : _refsW + graphW;
            ctx.DrawLine(_dropPen, new Point(ex, 0), new Point(ex, size.Height));
        }

        if (_drop is not null)
            for (int r = first; r <= last; r++)
                if (IsDropRow(s, r))
                    ctx.DrawRectangle(null, _dropPen, new RoundedRect(new Rect(2, r * _rowH - _offset + 1.5, size.Width - 4, _rowH - 3), 6));

        if (s.HasMore && last == s.Rows.Length - 1)
        {
            var more = OverlayDraw.Text("Loading older commits…", SmallSize, _muted);
            ctx.DrawText(more, new Point(subjX, RowMid(last) + _rowH - more.Height / 2));
            if (!_moreRequested)
            {
                _moreRequested = true;
                Dispatcher.Post(() => LoadMoreRequested?.Invoke());
            }
        }
    }

    private void PaintRow(DrawingContext ctx, GraphSnapshot s, int r, double subjX, double subjW, double authorX, double whenX)
    {
        var c = s.Commits[r];
        var row = s.Rows[r];
        int lane = row.Lane % _lane.Length;
        double y = RowMid(r), x = LaneX(row.Lane);
        bool inGraph = x < _refsW + GraphWidth - 2;

        if (c.IsWorkInProgress)
        {
            var wt = s.Tethers.FirstOrDefault(t => t.WipRow == r).Worktree;
            if (inGraph)
                ctx.DrawEllipse(_bg, _laneDash[lane], new Point(x, y), WipR, WipR);
            if (wt is not null)
                WorktreePill(ctx, wt, lane, y);
            var label = OverlayDraw.Text(Fit($"{wt?.Changes ?? 0} uncommitted change{(wt?.Changes == 1 ? "" : "s")}", FontSize, subjW), FontSize, _muted);
            OverlayDraw.TextLeftMid(ctx, label, subjX, y);
            if (wt is not null && _sessions.TryGetValue(wt.Path, out var sess))
                SessionPill(ctx, sess, subjX + label.Width + 12, subjX + subjW, y);
            var now = OverlayDraw.Text("now", SmallSize, _muted);
            OverlayDraw.TextLeftMid(ctx, now, whenX + WhenW - now.Width, y);
            return;
        }

        var worktrees = s.WorktreesAt(c.Hash);
        if (inGraph)
        {
            if (worktrees.Count > 0)
                ctx.DrawEllipse(null, worktrees.Any(w => w.IsCurrent) ? _ringBold[lane] : _ring[lane], new Point(x, y), RingR, RingR);
            double nr = c.IsMerge ? MergeR : NodeR;
            var fill = _hlLane >= 0 && row.Lane != _hlLane ? _laneFillFaded[lane] : _laneFill[lane];
            ctx.DrawEllipse(fill, _nodeEdge, new Point(x, y), nr, nr);
        }

        double tagsW = 0;
        var refs = s.RefsAt(c.Hash);
        if (refs.Count > 0)
        {
            Pills(ctx, s, r, refs, worktrees, lane, x, y);
            tagsW = Tags(ctx, refs, subjX + subjW, y);
        }

        var subject = OverlayDraw.Text(Fit(c.Subject, FontSize, subjW - tagsW), FontSize, _title);
        OverlayDraw.TextLeftMid(ctx, subject, subjX, y);
        if (authorX < whenX)
        {
            var author = OverlayDraw.Text(Fit(c.AuthorName, SmallSize, AuthorW - Gap), SmallSize, _muted);
            OverlayDraw.TextLeftMid(ctx, author, authorX, y);
        }
        var when = OverlayDraw.Text(Age(c), SmallSize, _muted);
        OverlayDraw.TextLeftMid(ctx, when, whenX + WhenW - when.Width, y);
    }

    // Local branches (the window's own worktree's first), then remote branches that aren't just a local's upstream at
    // the same commit, laid out right to left from the graph edge; what doesn't fit collapses to "+N".
    private void Pills(DrawingContext ctx, GraphSnapshot s, int r, IReadOnlyList<GraphRef> refs,
        IReadOnlyList<GraphWorktree> worktrees, int lane, double nodeX, double y)
    {
        double right = _refsW - 6;
        double left = 8;
        int shown = 0, total = 0;
        string? current = null;
        foreach (var w in s.Worktrees)
            if (w.IsCurrent) current = w.Branch;
        for (int pass = 0; pass < 3; pass++)
            foreach (var rf in refs)
            {
                bool local = rf.Kind == GraphRefKind.LocalBranch;
                if (pass == 0 && !(local && rf.Name == current)) continue;
                if (pass == 1 && !(local && rf.Name != current)) continue;
                if (pass == 2 && !(rf.Kind is GraphRefKind.RemoteBranch or GraphRefKind.Stash)) continue;
                if (rf.Kind == GraphRefKind.RemoteBranch && UpstreamOfLocal(refs, rf))
                    continue;
                total++;
                bool synced = local && HasUpstreamHere(refs, rf);
                bool here = local && rf.Name == current;
                bool checkedOut = local && IsCheckedOut(s, rf.Name);
                var ink = here ? _bg : local ? _laneFill[lane] : _muted;
                double lead = checkedOut ? 13 : 0, trail = synced ? 10 : 0;
                // The first pill (this worktree's branch when it's here) always gets one, cut to the room there is;
                // later ones are capped and leave space for the "+N".
                double room = right - left - 14 - lead - trail;
                double cap = shown == 0 ? room : Math.Min(MaxPillText, room - 24);
                if (cap < 28)
                    continue;
                var ft = OverlayDraw.Text(OverlayDraw.ElideMiddle(rf.Name, PillSize, cap, FontWeight.SemiBold), PillSize, ink, FontWeight.SemiBold);
                double w = ft.Width + 14 + lead + trail, h = ft.Height + 4;
                var rect = new Rect(right - w, y - h / 2, w, h);
                if (here)
                    OverlayDraw.Panel(ctx, rect, _laneFill[lane], null, 5);
                else if (local)
                    OverlayDraw.Panel(ctx, rect, _laneWash[lane], _pillBorder[lane], 5);
                else
                    OverlayDraw.Panel(ctx, rect, null, _pillDash[lane], 5);
                if (checkedOut)
                    FolderGlyph(ctx, ink, rect.X + 7, y);
                ctx.DrawText(ft, new Point(rect.X + 7 + lead, y - ft.Height / 2));
                if (synced)
                    ctx.DrawEllipse(null, OverlayDraw.SolidPen(((ISolidColorBrush)ink).Color, 1.2), new Point(rect.Right - 9, y), 2.4, 2.4);
                _pillHits.Add((rect, r, rf));
                if (_drop?.Ref is { } dr && dr.FullName == rf.FullName)
                    ctx.DrawRectangle(null, _dropPen, new RoundedRect(rect.Inflate(2.5), 7));
                if (shown == 0)
                    ctx.DrawLine(_pillBorder[lane], new Point(_refsW - 6, y), new Point(nodeX - RingR - 1, y));
                right = rect.X - 4;
                shown++;
            }
        if (total > shown)
        {
            var more = OverlayDraw.Text($"+{total - shown}", SmallSize, _muted);
            OverlayDraw.TextLeftMid(ctx, more, Math.Max(2, right - more.Width), y);
        }
    }

    // The row a drop would land on: the target branch's commit, or the target worktree's WIP row (its HEAD when clean).
    private bool IsDropRow(GraphSnapshot s, int r)
    {
        var c = s.Commits[r];
        if (_drop?.Ref is { } dr) return dr.Target == c.Hash;
        if (_drop?.Worktree is { } w)
            return c.IsWorkInProgress ? c.Hash == w.WipHash : w.Changes == 0 && w.Head == c.Hash;
        return false;
    }

    private static bool UpstreamOfLocal(IReadOnlyList<GraphRef> refs, GraphRef remote)
    {
        foreach (var l in refs)
            if (l.Kind == GraphRefKind.LocalBranch && remote.Name.EndsWith("/" + l.Name, StringComparison.Ordinal)) return true;
        return false;
    }

    private static bool HasUpstreamHere(IReadOnlyList<GraphRef> refs, GraphRef local)
    {
        foreach (var o in refs)
            if (o.Kind == GraphRefKind.RemoteBranch && o.Name.EndsWith("/" + local.Name, StringComparison.Ordinal)) return true;
        return false;
    }

    private static bool IsCheckedOut(GraphSnapshot s, string branch)
    {
        foreach (var w in s.Worktrees)
            if (w.Branch == branch) return true;
        return false;
    }

    // A small folder outline: marks a branch that some worktree has checked out.
    private static void FolderGlyph(DrawingContext ctx, IBrush ink, double x, double cy)
    {
        var pen = OverlayDraw.SolidPen(((ISolidColorBrush)ink).Color, 1.2, PenLineCap.Round, PenLineJoin.Round);
        ctx.DrawRectangle(null, pen, new RoundedRect(new Rect(x, cy - 3, 9, 6.5), 1.2));
        ctx.DrawLine(pen, new Point(x + 0.5, cy - 3), new Point(x + 0.5, cy - 4.4));
        ctx.DrawLine(pen, new Point(x + 0.5, cy - 4.4), new Point(x + 3.6, cy - 4.4));
    }

    private void WorktreePill(DrawingContext ctx, GraphWorktree wt, int lane, double y)
    {
        var ft = OverlayDraw.Text(OverlayDraw.ElideMiddle(wt.Name, PillSize, Math.Max(0, _refsW - 42), FontWeight.SemiBold), PillSize, _laneFill[lane], FontWeight.SemiBold);
        double w = ft.Width + 27, h = ft.Height + 4;
        var rect = new Rect(_refsW - 6 - w, y - h / 2, w, h);
        OverlayDraw.Panel(ctx, rect, null, _pillDash[lane], 5);
        FolderGlyph(ctx, _laneFill[lane], rect.X + 7, y);
        ctx.DrawText(ft, new Point(rect.X + 20, y - ft.Height / 2));
    }

    // "● Running · <session name>" in the session's status colour, on a faint wash of it, clipped to the space left.
    private void SessionPill(DrawingContext ctx, GraphRail.WorktreeSession s, double x, double right, double y)
    {
        var ink = OverlayDraw.Brush(s.Color);
        var ft = OverlayDraw.Text(Fit($"{s.State} · {s.Name}", SmallSize, right - x - 24), SmallSize, ink, FontWeight.SemiBold);
        if (ft.Width < 20) return;
        double w = ft.Width + 24, h = ft.Height + 4;
        var rect = new Rect(x, y - h / 2, w, h);
        OverlayDraw.Panel(ctx, rect, OverlayDraw.Brush(s.Color, 0.12), OverlayDraw.SolidPen(s.Color, 1), h / 2);
        ctx.DrawEllipse(ink, null, new Point(x + 10, y), 3, 3);
        ctx.DrawText(ft, new Point(x + 17, y - ft.Height / 2));
    }

    // Tags sit at the right end of the subject column, muted, each after a small tag mark; returns the width taken.
    private double Tags(DrawingContext ctx, IReadOnlyList<GraphRef> refs, double right, double y)
    {
        double used = 0;
        var pen = OverlayDraw.SolidPen(_c.Muted, 1.2, PenLineCap.Round, PenLineJoin.Round);
        foreach (var rf in refs)
        {
            if (rf.Kind != GraphRefKind.Tag) continue;
            var ft = OverlayDraw.Text(rf.Name, SmallSize, _muted);
            double w = ft.Width + 11 + Gap;
            if (used + w > 220) break;
            used += w;
            double x = right - used + Gap;
            ctx.DrawLine(pen, new Point(x, y), new Point(x + 3.5, y - 3.5));
            ctx.DrawLine(pen, new Point(x + 3.5, y - 3.5), new Point(x + 7, y));
            ctx.DrawLine(pen, new Point(x + 7, y), new Point(x + 3.5, y + 3.5));
            ctx.DrawLine(pen, new Point(x + 3.5, y + 3.5), new Point(x, y));
            OverlayDraw.TextLeftMid(ctx, ft, x + 11, y);
        }
        return used;
    }

    private void Edge(DrawingContext ctx, GraphEdge e, double y1, double y2, bool dashed)
    {
        int color = e.Color % _lane.Length;
        var pen = _hlLane >= 0 && e.Color != _hlLane ? _laneFaded[color] : (dashed ? _laneDash : _lane)[color];
        double x1 = LaneX(e.From), x2 = LaneX(e.To);
        if (x1 == x2)
        {
            ctx.DrawLine(pen, new Point(x1, y1), new Point(x2, y2));
            return;
        }
        // A cubic S-curve across the band (vertical tangents at both rows), as short straight runs.
        double h = y2 - y1;
        var prev = new Point(x1, y1);
        for (int i = 1; i <= CurveSteps; i++)
        {
            double t = (double)i / CurveSteps, u = 1 - t;
            double bx = (u * u * u + 3 * u * u * t) * x1 + (3 * u * t * t + t * t * t) * x2;
            double by = u * u * u * y1 + 3 * u * u * t * (y1 + h * 0.5) + 3 * u * t * t * (y2 - h * 0.5) + t * t * t * y2;
            var p = new Point(bx, by);
            ctx.DrawLine(pen, prev, p);
            prev = p;
        }
    }

    private static string Fit(string text, double size, double width) =>
        width <= 8 ? "" : OverlayDraw.Truncate(text, size, width);

    private static string Age(GraphCommit c)
    {
        var at = c.Date.UtcDateTime;
        var now = DateTime.UtcNow;
        return now - at < TimeSpan.FromDays(30) ? RelativeTime.Span(now, at) : c.Date.LocalDateTime.ToString("d MMM yy");
    }

    // ---- input ----

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        Focus();
        var pos = e.GetPosition(this);
        if (EdgeAt(pos.X) is var edge and > 0)
        {
            // Double-clicking an edge puts the column back to its default.
            if (e.ClickCount == 2)
            {
                if (edge == 1) _refsW = DefaultRefsW; else _lanesW = null;
                ColumnsResized?.Invoke(_refsW == DefaultRefsW ? 0 : _refsW, _lanesW ?? 0);
                InvalidateVisual();
            }
            else
            {
                _edgeDrag = (edge, pos.X, edge == 1 ? _refsW : GraphWidth);
                e.Pointer.Capture(this);
            }
            e.Handled = true;
            return;
        }
        int r = RowAt(pos.Y);
        if (r < 0) return;
        Select(r, scrollIntoView: false);
        if (e.ClickCount == 2)
        {
            _press = null;
            if (RefAt(pos) is { } rf) RefActivated?.Invoke(rf);
            else RowActivated?.Invoke(r);
        }
        else
        {
            _press = (pos, r);
            e.Pointer.Capture(this);
        }
        e.Handled = true;
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        _press = null;
        if (_edgeDrag is not null)
        {
            _edgeDrag = null;
            ColumnsResized?.Invoke(_refsW == DefaultRefsW ? 0 : _refsW, _lanesW ?? 0);
        }
    }

    // Past the threshold, a press on a branch pill drags that branch and a press elsewhere on a commit row drags the
    // commit. WIP rows, tags and stashes don't drag.
    private DragSource? SourceAt(Point p, int row)
    {
        if (_snap is null || row < 0 || row >= _snap.Commits.Count) return null;
        if (RefAt(p) is { } r)
            return r.Kind is GraphRefKind.LocalBranch or GraphRefKind.RemoteBranch ? DragSource.Of(r) : null;
        var c = _snap.Commits[row];
        return c.IsWorkInProgress ? null : DragSource.Of(c);
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        var at = e.GetPosition(this);
        if (_edgeDrag is { } drag)
        {
            double w = drag.StartW + at.X - drag.StartX;
            if (drag.Edge == 1) _refsW = Math.Clamp(w, MinRefsW, MaxRefsW);
            else _lanesW = Math.Clamp(w, MinGraphW, MaxLanesW);
            InvalidateVisual();
            return;
        }
        int edge = _press is null ? EdgeAt(at.X) : 0;
        if (edge != _edgeHover)
        {
            _edgeHover = edge;
            Cursor = edge > 0 ? new Cursor(StandardCursorType.SizeWestEast) : Cursor.Default;
            InvalidateVisual();
        }
        UpdatePillTip(at);
        if (_press is { } press && e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            var d = e.GetPosition(this) - press.At;
            if (Math.Abs(d.X) + Math.Abs(d.Y) >= DragThreshold)
            {
                _press = null;
                if (SourceAt(press.At, press.Row) is { } src)
                    DragRequested?.Invoke(src, e.Pointer);
                return;
            }
        }
        int r = RowAt(e.GetPosition(this).Y);
        if (r == _hover) return;
        _hover = r;
        InvalidateVisual();
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        UpdatePillTip(null);
        if (_hover < 0 && _edgeHover == 0) return;
        _hover = -1;
        _edgeHover = 0;
        InvalidateVisual();
    }

    // A pill's full name as a tooltip, since long names are cut in the middle.
    private GraphRef? _tipRef;
    private void UpdatePillTip(Point? at)
    {
        var r = at is { } p && _edgeDrag is null ? RefAt(p) : null;
        if (r == _tipRef) return;
        _tipRef = r;
        ToolTip.SetTip(this, r?.Name);
        ToolTip.SetIsOpen(this, r is not null);
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        Offset -= e.Delta.Y * _rowH * 3;
        e.Handled = true;
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (_snap is null || _snap.Rows.Length == 0) return;
        int page = Math.Max(1, (int)(Viewport / _rowH) - 1);
        int? target = e.Key switch
        {
            Key.Down => _sel + 1,
            Key.Up => Math.Max(0, _sel - 1),
            Key.PageDown => _sel + page,
            Key.PageUp => Math.Max(0, _sel - page),
            Key.Home => 0,
            Key.End => _snap.Rows.Length - 1,
            _ => null,
        };
        if (target is int t)
        {
            Select(Math.Min(t, _snap.Rows.Length - 1));
            e.Handled = true;
        }
        else if (e.Key == Key.Enter && _sel >= 0)
        {
            RowActivated?.Invoke(_sel);
            e.Handled = true;
        }
    }

    protected override void OnSizeChanged(SizeChangedEventArgs e)
    {
        base.OnSizeChanged(e);
        _offset = Math.Clamp(_offset, 0, Math.Max(0, Extent - Viewport));
        ScrollChanged?.Invoke();
    }
}

/// <summary>A <see cref="GraphCanvas"/> with a vertical scroll bar beside it, kept in step both ways.</summary>
internal sealed class GraphPane : Grid
{
    private readonly ScrollBar _bar = new() { Orientation = Orientation.Vertical, AllowAutoHide = true };
    private bool _syncing;

    public GraphPane()
    {
        ColumnDefinitions = new ColumnDefinitions("*,Auto");
        Canvas = new GraphCanvas();
        Children.Add(Canvas);
        Grid.SetColumn(_bar, 1);
        Children.Add(_bar);
        Canvas.ScrollChanged += Sync;
        _bar.ValueChanged += (_, e) =>
        {
            if (_syncing) return;
            Canvas.Offset = e.NewValue;
        };
    }

    public GraphCanvas Canvas { get; }

    private void Sync()
    {
        _syncing = true;
        _bar.Maximum = Math.Max(0, Canvas.Extent - Canvas.Viewport);
        _bar.ViewportSize = Canvas.Viewport;
        _bar.SmallChange = Canvas.RowHeight;
        _bar.LargeChange = Math.Max(Canvas.RowHeight, Canvas.Viewport - Canvas.RowHeight);
        _bar.Value = Canvas.Offset;
        _bar.IsVisible = _bar.Maximum > 0;
        _syncing = false;
    }
}
