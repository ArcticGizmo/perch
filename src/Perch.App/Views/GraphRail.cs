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
    public sealed record RailItem(GraphWorktree? Worktree, GraphRef? Ref);

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

        var locals = s.Refs.Where(r => r.Kind == GraphRefKind.LocalBranch).ToList();
        _stack.Children.Add(Header("Local", locals.Count));
        foreach (var r in locals)
            _stack.Children.Add(RefRow(s, r, r.Name, s.Worktrees.Any(w => w.Branch == r.Name)));

        var remotes = s.Refs.Where(r => r.Kind == GraphRefKind.RemoteBranch).ToList();
        if (remotes.Count > 0)
        {
            _stack.Children.Add(Header("Remotes", remotes.Count, _remotesOpen, () => _remotesOpen = !_remotesOpen));
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

    private Control Header(string text, int count, bool? open = null, Action? toggle = null)
    {
        var label = new TextBlock
        {
            Text = (open is null ? "" : open.Value ? "▾ " : "▸ ") + text.ToUpperInvariant(),
            FontSize = 10.5, FontWeight = FontWeight.SemiBold, LetterSpacing = 1.1, Foreground = Ink(_c.Muted),
        };
        var n = new TextBlock { Text = count.ToString(), FontSize = 10.5, Foreground = Ink(_c.Muted), [DockPanel.DockProperty] = Dock.Right };
        var row = new Border
        {
            Padding = new Thickness(6, 4), Margin = new Thickness(0, 10, 0, 2), Background = Brushes.Transparent, CornerRadius = new CornerRadius(5),
            Child = new DockPanel { Children = { n, label } },
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
            if (e.ClickCount == 2) WorktreeActivated?.Invoke(w);
            else if ((w.Changes > 0 ? w.WipHash : w.Head) is { } target) JumpRequested?.Invoke(target);
            e.Handled = true;
        };
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
        if (checkedOut)
        {
            var f = Folder(color, new Thickness(6, 0, 0, 0));
            f.SetValue(DockPanel.DockProperty, Dock.Right);
            dock.Children.Add(f);
        }
        dock.Children.Add(new TextBlock
        {
            Text = label, FontSize = 12, Foreground = Ink(lane >= 0 ? _c.Text : _c.Muted),
            TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center,
        });
        var row = new Border
        {
            Padding = new Thickness(6 + indent, 3, 6, 3), CornerRadius = new CornerRadius(5), Background = Brushes.Transparent,
            Cursor = new Cursor(StandardCursorType.Hand), Child = dock, Tag = new RailItem(null, r),
        };
        ToolTip.SetTip(row, lane >= 0 ? r.Name : $"{r.Name} · not in the loaded graph");
        row.PointerPressed += (_, e) =>
        {
            if (e.ClickCount == 2) BranchActivated?.Invoke(r);
            else JumpRequested?.Invoke(r.Target);
            e.Handled = true;
        };
        Hover(row, lane);
        return row;
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
