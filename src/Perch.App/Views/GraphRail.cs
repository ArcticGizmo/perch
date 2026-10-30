using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Perch.Avalonia.Rendering;
using Perch.Data.GitGraph;

namespace Perch.Avalonia.Views;

/// <summary>
/// The Tree window's left rail: the repo's worktrees as cards (this window's first), then local branches, remote
/// branches by remote, tags and stashes. A row's colour mark is its lane in the graph (muted when the graph hasn't
/// loaded that commit). Clicking jumps the graph to the row, hovering highlights the lane, double-clicking raises
/// <see cref="WorktreeActivated"/> / <see cref="BranchActivated"/>. Every card and branch row carries a
/// <see cref="RailItem"/> in its <c>Tag</c>, which is how a drag finds what it was dropped on.
/// </summary>
internal sealed class GraphRail : UserControl
{
    /// <summary>What a rail row stands for.</summary>
    public sealed record RailItem(GraphWorktree? Worktree, GraphRef? Ref, bool NewWorktree = false)
    {
        /// <summary>As a drop target: a worktree card, a branch row, or the "New worktree" zone. Tags and stashes take no drops.</summary>
        public DropTarget? Target => NewWorktree ? DropTarget.NewWorktreeZone
            : Worktree is not null ? DropTarget.Of(Worktree)
            : Ref is { Kind: GraphRefKind.LocalBranch or GraphRefKind.RemoteBranch } r ? DropTarget.Of(r)
            : null;
    }

    /// <summary>A branch row or a worktree card (its branch) was pressed and dragged past the threshold; the host runs
    /// the drag from here.</summary>
    public event Action<DragSource, IPointer>? DragRequested;

    private bool _dragging;
    private (Control Row, Point At)? _press;
    private Control? _dropHit;
    private IBrush? _dropHitWas;

    private readonly StackPanel _stack = new() { Spacing = 2, Margin = new Thickness(8, 4, 8, 12) };
    private GraphCanvas.Colors _c = GraphCanvas.Colors.For(light: false);
    private GraphSnapshot? _snap;
    private IReadOnlyDictionary<string, WorktreeSession> _sessions = new Dictionary<string, WorktreeSession>();
    private bool _remotesOpen = true, _tagsOpen, _stashesOpen;

    /// <summary>The Claude session working in a worktree, as the rail shows it.</summary>
    public sealed record WorktreeSession(string Name, string State, Color Color);

    public GraphRail()
    {
        Content = new ScrollViewer
        {
            Content = _stack,
            HorizontalScrollBarVisibility = global::Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
        };
    }

    /// <summary>A row was clicked: jump the graph to this commit.</summary>
    public event Action<string>? JumpRequested;

    /// <summary>The pointer is over a row of this lane (−1 when it left).</summary>
    public event Action<int>? LaneHovered;

    public event Action<GraphWorktree>? WorktreeActivated;

    /// <summary>A worktree card was right-clicked: the host shows its menu at the pointer.</summary>
    public event Action<GraphWorktree, Control>? WorktreeMenuRequested;

    /// <summary>A branch row's eye was clicked: hide it from the graph (true) or show it again.</summary>
    public event Action<GraphRef, bool>? VisibilityToggled;

    /// <summary>The "⋯" on the Local or Remotes header: the host shows the visibility menu there.</summary>
    public event Action<Control>? VisibilityMenuRequested;
    public event Action<GraphRef>? BranchActivated;

    public void SetColors(GraphCanvas.Colors c)
    {
        _c = c;
        Rebuild();
    }

    public void SetSnapshot(GraphSnapshot? snap)
    {
        _snap = snap;
        Rebuild();
    }

    /// <summary>The sessions working in each worktree, keyed by worktree path.</summary>
    public void SetSessions(IReadOnlyDictionary<string, WorktreeSession> sessions)
    {
        _sessions = sessions;
        Rebuild();
    }

    /// <summary>While a drag is on, the rail offers a "New worktree" drop zone under the worktree cards.</summary>
    public void SetDragging(bool dragging)
    {
        if (_dragging == dragging) return;
        _dragging = dragging;
        HighlightDrop(null);
        Rebuild();
    }

    /// <summary>Washes the card or row a drag is over (null clears it).</summary>
    public void HighlightDrop(Control? hit)
    {
        if (ReferenceEquals(hit, _dropHit)) return;
        if (_dropHit is Border was) was.Background = _dropHitWas;
        _dropHit = hit;
        if (hit is Border b)
        {
            _dropHitWas = b.Background;
            b.Background = Ink(_c.Lanes[0], 0.22);
        }
    }

    // Press-and-drag on a row starts a drag of whatever it stands for; a plain click still does what it did.
    private void TrackPress(Control row, PointerPressedEventArgs e)
    {
        if (e.ClickCount != 1 || !e.GetCurrentPoint(row).Properties.IsLeftButtonPressed) { _press = null; return; }
        _press = (row, e.GetPosition(this));
        e.Pointer.Capture(row);
    }

    private void Draggable(Control row, Func<DragSource?> source)
    {
        row.PointerMoved += (_, e) =>
        {
            if (_press is not { } p || !ReferenceEquals(p.Row, row) || !e.GetCurrentPoint(row).Properties.IsLeftButtonPressed) return;
            var d = e.GetPosition(this) - p.At;
            if (Math.Abs(d.X) + Math.Abs(d.Y) < 6) return;
            _press = null;
            if (source() is { } s) DragRequested?.Invoke(s, e.Pointer);
        };
        row.PointerReleased += (_, _) => _press = null;
    }

    // The drop zone for a new worktree, only while dragging.
    private Control NewWorktreeZone() => new Border
    {
        Padding = new Thickness(8, 9), Margin = new Thickness(0, 2), CornerRadius = new CornerRadius(8),
        BorderThickness = new Thickness(1.2), BorderBrush = Ink(_c.Muted, 0.7), Background = Brushes.Transparent,
        Child = new TextBlock { Text = "+ New worktree", FontSize = 12, Foreground = Ink(_c.Text), HorizontalAlignment = HorizontalAlignment.Center },
        Tag = new RailItem(null, null, NewWorktree: true),
    };

    private IBrush Ink(Color c, double o = 1) => OverlayDraw.Brush(c, o);

    private void Rebuild()
    {
        _stack.Children.Clear();
        Background = Ink(_c.Background);
        if (_snap is not { } s)
            return;

        _stack.Children.Add(Header($"Worktrees", s.Worktrees.Count));
        foreach (var w in s.Worktrees.OrderByDescending(w => w.IsCurrent))
            _stack.Children.Add(WorktreeCard(s, w));
        if (_dragging)
            _stack.Children.Add(NewWorktreeZone());

        var locals = s.Refs.Where(r => r.Kind == GraphRefKind.LocalBranch).ToList();
        _stack.Children.Add(Header("Local", locals.Count, hidden: locals.Count(s.IsHidden), menu: true));
        foreach (var r in locals)
            _stack.Children.Add(RefRow(s, r, r.Name, s.Worktrees.Any(w => w.Branch == r.Name)));

        var remotes = s.Refs.Where(r => r.Kind == GraphRefKind.RemoteBranch).ToList();
        if (remotes.Count > 0)
        {
            _stack.Children.Add(Header("Remotes", remotes.Count, _remotesOpen, () => _remotesOpen = !_remotesOpen,
                hidden: remotes.Count(s.IsHidden), menu: true));
            if (_remotesOpen)
                foreach (var g in remotes.GroupBy(r => r.Name.Split('/')[0]))
                {
                    _stack.Children.Add(SubHeader(g.Key));
                    foreach (var r in g)
                        _stack.Children.Add(RefRow(s, r, r.Name[(g.Key.Length + 1)..], false, indent: 12));
                }
        }

        var tags = s.Refs.Where(r => r.Kind == GraphRefKind.Tag)
            .OrderBy(r => s.RowOf(r.Target) is var row and >= 0 ? row : int.MaxValue).ToList();
        if (tags.Count > 0)
        {
            _stack.Children.Add(Header("Tags", tags.Count, _tagsOpen, () => _tagsOpen = !_tagsOpen));
            if (_tagsOpen)
                foreach (var r in tags.Take(200))
                    _stack.Children.Add(RefRow(s, r, r.Name, false));
        }

        var stashes = s.Refs.Where(r => r.Kind == GraphRefKind.Stash).ToList();
        if (stashes.Count > 0)
        {
            _stack.Children.Add(Header("Stashes", stashes.Count, _stashesOpen, () => _stashesOpen = !_stashesOpen));
            if (_stashesOpen)
                foreach (var r in stashes)
                    _stack.Children.Add(RefRow(s, r, r.Name, false));
        }
    }

    private Control Header(string text, int count, bool? open = null, Action? toggle = null, int hidden = 0, bool menu = false)
    {
        var label = new TextBlock
        {
            Text = (open is null ? "" : open.Value ? "▾ " : "▸ ") + text.ToUpperInvariant(),
            FontSize = 10.5, FontWeight = FontWeight.SemiBold, LetterSpacing = 1.1, Foreground = Ink(_c.Muted),
        };
        var n = new TextBlock
        {
            Text = hidden > 0 ? $"{count} · {hidden} hidden" : count.ToString(), FontSize = 10.5, Foreground = Ink(_c.Muted),
            VerticalAlignment = VerticalAlignment.Center, [DockPanel.DockProperty] = Dock.Right,
        };
        var dock = new DockPanel();
        if (menu)
        {
            var more = new Border
            {
                Padding = new Thickness(6, 0), Margin = new Thickness(4, 0, 0, 0), CornerRadius = new CornerRadius(4), Background = Brushes.Transparent,
                Cursor = new Cursor(StandardCursorType.Hand), [DockPanel.DockProperty] = Dock.Right,
                Child = new TextBlock { Text = "⋯", FontSize = 13, FontWeight = FontWeight.Bold, Foreground = Ink(_c.Muted) },
            };
            ToolTip.SetTip(more, "Hidden branches");
            more.PointerPressed += (_, e) => { VisibilityMenuRequested?.Invoke(more); e.Handled = true; };
            Hover(more, -1);
            dock.Children.Add(more);
        }
        dock.Children.Add(n);
        dock.Children.Add(label);
        var row = new Border
        {
            Padding = new Thickness(6, 4), Margin = new Thickness(0, 10, 0, 2), Background = Brushes.Transparent, CornerRadius = new CornerRadius(5),
            Child = dock,
        };
        if (toggle is not null)
        {
            row.Cursor = new Cursor(StandardCursorType.Hand);
            row.PointerPressed += (_, e) => { toggle(); Rebuild(); e.Handled = true; };
            Hover(row, -1);
        }
        return row;
    }

    private Control SubHeader(string text) => new TextBlock
    {
        Text = text, FontSize = 11, Foreground = Ink(_c.Muted), Margin = new Thickness(8, 4, 0, 1),
    };

    private Control WorktreeCard(GraphSnapshot s, GraphWorktree w)
    {
        int lane = w.Head is { } h ? s.LaneOf(h) : -1;
        var laneColor = lane >= 0 ? _c.Lanes[lane % _c.Lanes.Length] : _c.Muted;

        var name = new TextBlock
        {
            Text = w.Name, FontSize = 12.5, FontWeight = FontWeight.SemiBold, Foreground = Ink(_c.Title),
            TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center,
        };
        var top = new DockPanel { LastChildFill = true };
        top.Children.Add(Folder(laneColor, new Thickness(0, 0, 6, 0)));
        if (w.IsCurrent)
            top.Children.Add(new Border
            {
                [DockPanel.DockProperty] = Dock.Right, BorderBrush = Ink(_c.Lanes[0], 0.6), BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(8), Padding = new Thickness(5, 0), VerticalAlignment = VerticalAlignment.Center,
                Child = new TextBlock { Text = "this window", FontSize = 9.5, Foreground = Ink(_c.Lanes[0]) },
            });
        top.Children.Add(name);

        var branch = new TextBlock
        {
            Text = w.Branch ?? (w.Head is { } hh ? $"detached @ {hh[..Math.Min(7, hh.Length)]}" : "no commits"),
            FontSize = 11.5, Foreground = Ink(laneColor), TextTrimming = TextTrimming.CharacterEllipsis,
        };
        var second = new DockPanel { LastChildFill = true, Margin = new Thickness(0, 2, 0, 0) };
        if (w.Changes > 0)
            second.Children.Add(new TextBlock
            {
                Text = $"● {w.Changes}", FontSize = 11, Foreground = Ink(_c.Lanes[5]), Margin = new Thickness(6, 0, 0, 0),
                [DockPanel.DockProperty] = Dock.Right,
            });
        second.Children.Add(branch);

        var body = new StackPanel { Children = { top, second } };
        if (_sessions.TryGetValue(w.Path, out var sess))
            body.Children.Add(new DockPanel
            {
                Margin = new Thickness(0, 3, 0, 0),
                Children =
                {
                    new Ellipse { Width = 7, Height = 7, Fill = Ink(sess.Color), Margin = new Thickness(0, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center },
                    new TextBlock { Text = sess.State, FontSize = 10.5, FontWeight = FontWeight.SemiBold, Foreground = Ink(sess.Color), Margin = new Thickness(6, 0, 0, 0), [DockPanel.DockProperty] = Dock.Right },
                    new TextBlock { Text = sess.Name, FontSize = 11, Foreground = Ink(_c.Text), TextTrimming = TextTrimming.CharacterEllipsis },
                },
            });

        var card = new Border
        {
            Padding = new Thickness(8, 6), Margin = new Thickness(0, 2), CornerRadius = new CornerRadius(8),
            BorderThickness = new Thickness(1),
            BorderBrush = w.IsCurrent ? Ink(laneColor, 0.7) : Ink(_c.Separator),
            Background = w.IsCurrent ? Ink(laneColor, 0.08) : Brushes.Transparent,
            Cursor = new Cursor(StandardCursorType.Hand),
            Child = body,
            Tag = new RailItem(w, null),
        };
        ToolTip.SetTip(card, w.Path);
        card.PointerPressed += (_, e) =>
        {
            TrackPress(card, e);
            if (e.ClickCount == 2) WorktreeActivated?.Invoke(w);
            else if ((w.Changes > 0 ? w.WipHash : w.Head) is { } target) JumpRequested?.Invoke(target);
            e.Handled = true;
        };
        card.ContextRequested += (_, e) => { WorktreeMenuRequested?.Invoke(w, card); e.Handled = true; };
        Draggable(card, () => s.Refs.FirstOrDefault(r => r.Kind == GraphRefKind.LocalBranch && r.Name == w.Branch) is { } br
            ? DragSource.Of(br) : null);
        Hover(card, lane, w.IsCurrent ? card.Background : null);
        return card;
    }

    private Control RefRow(GraphSnapshot s, GraphRef r, string label, bool checkedOut, double indent = 0)
    {
        int lane = s.LaneOf(r.Target);
        var color = lane >= 0 ? _c.Lanes[lane % _c.Lanes.Length] : _c.Muted;
        var dock = new DockPanel { LastChildFill = true };
        dock.Children.Add(new Border
        {
            Width = 8, Height = 8, CornerRadius = new CornerRadius(2), Background = Ink(color, lane >= 0 ? 1 : 0.5),
            Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center,
        });
        bool hidden = s.IsHidden(r);
        Control? eye = null;
        if (checkedOut)
        {
            var f = Folder(color, new Thickness(6, 0, 0, 0));
            f.SetValue(DockPanel.DockProperty, Dock.Right);
            dock.Children.Add(f);
        }
        else if (r.Kind is GraphRefKind.LocalBranch or GraphRefKind.RemoteBranch)
        {
            // Shown on hover, and always on a hidden row so the way back is visible.
            eye = Eye(hidden);
            eye.Opacity = hidden ? 1 : 0;
            ToolTip.SetTip(eye, hidden ? "Show in the graph" : "Hide from the graph");
            eye.PointerPressed += (_, e) => { VisibilityToggled?.Invoke(r, !hidden); e.Handled = true; };
            dock.Children.Add(eye);
        }
        dock.Children.Add(new TextBlock
        {
            Text = label, FontSize = 12, Foreground = Ink(lane >= 0 && !hidden ? _c.Text : _c.Muted, hidden ? 0.7 : 1),
            FontStyle = hidden ? FontStyle.Italic : FontStyle.Normal,
            TextTrimming = TextTrimming.PrefixCharacterEllipsis, VerticalAlignment = VerticalAlignment.Center,
        });
        var row = new Border
        {
            Padding = new Thickness(6 + indent, 3, 6, 3), CornerRadius = new CornerRadius(5), Background = Brushes.Transparent,
            Cursor = new Cursor(StandardCursorType.Hand), Child = dock, Tag = new RailItem(null, r),
        };
        ToolTip.SetTip(row, hidden ? $"{r.Name} · hidden from the graph" : lane >= 0 ? r.Name : $"{r.Name} · not in the loaded graph");
        if (eye is not null && !hidden)
        {
            row.PointerEntered += (_, _) => eye.Opacity = 1;
            row.PointerExited += (_, _) => eye.Opacity = 0;
        }
        row.PointerPressed += (_, e) =>
        {
            TrackPress(row, e);
            if (e.ClickCount == 2) BranchActivated?.Invoke(r);
            else JumpRequested?.Invoke(r.Target);
            e.Handled = true;
        };
        if (r.Kind is GraphRefKind.LocalBranch or GraphRefKind.RemoteBranch)
            Draggable(row, () => DragSource.Of(r));
        Hover(row, lane);
        return row;
    }

    // An eye outline (struck through when the branch is hidden), drawn because Inter has no eye glyph.
    private Control Eye(bool struck)
    {
        var ink = Ink(_c.Muted);
        var c = new global::Avalonia.Controls.Canvas { Width = 14, Height = 10 };
        c.Children.Add(new Ellipse { Width = 13, Height = 8, Stroke = ink, StrokeThickness = 1.2, [global::Avalonia.Controls.Canvas.LeftProperty] = 0.5, [global::Avalonia.Controls.Canvas.TopProperty] = 1.0 });
        c.Children.Add(new Ellipse { Width = 4, Height = 4, Fill = ink, [global::Avalonia.Controls.Canvas.LeftProperty] = 5.0, [global::Avalonia.Controls.Canvas.TopProperty] = 3.0 });
        if (struck)
            c.Children.Add(new Line { StartPoint = new Point(1, 9.5), EndPoint = new Point(13, 0.5), Stroke = ink, StrokeThickness = 1.4 });
        return new Border
        {
            Child = c, Padding = new Thickness(3, 2), Margin = new Thickness(6, 0, 0, 0), Background = Brushes.Transparent,
            Cursor = new Cursor(StandardCursorType.Hand), VerticalAlignment = VerticalAlignment.Center,
            [DockPanel.DockProperty] = Dock.Right,
        };
    }

    // A small folder outline: the branch is checked out in a worktree (or this is a worktree card).
    private Control Folder(Color color, Thickness margin) => new Border
    {
        Width = 10, Height = 8, BorderThickness = new Thickness(1.2), CornerRadius = new CornerRadius(1.5),
        BorderBrush = Ink(color), Margin = margin, VerticalAlignment = VerticalAlignment.Center,
    };

    private void Hover(Border b, int lane, IBrush? rest = null)
    {
        b.PointerEntered += (_, _) => { b.Background = Ink(_c.RowHover); if (lane >= 0) LaneHovered?.Invoke(lane); };
        b.PointerExited += (_, _) => { b.Background = rest ?? Brushes.Transparent; LaneHovered?.Invoke(-1); };
    }
}
