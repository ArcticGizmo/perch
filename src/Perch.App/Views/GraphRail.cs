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
/// branches in a foldable group per remote under a filter box, tags (grouped by name, or in graph order) and stashes. A row's name is muted when the graph
/// hasn't loaded its commit. Clicking jumps the graph to the row, hovering highlights the lane, double-clicking raises
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

    // The remote filter box stays attached between rebuilds (a live refresh mid-typing would otherwise drop its focus),
    // so the rail is three panels around it and only the panels are refilled.
    private readonly StackPanel _before = new() { Spacing = 2 }, _remoteRows = new() { Spacing = 2 }, _after = new() { Spacing = 2 };
    private readonly TextBox _remoteFilter;
    private GraphCanvas.Colors _c = GraphCanvas.Colors.For(light: false);
    private GraphSnapshot? _snap;
    private IReadOnlyDictionary<string, WorktreeSession> _sessions = new Dictionary<string, WorktreeSession>();
    // Local, Tags and Stashes start folded: Worktrees and Remotes are what a fresh look usually wants, and the folded
    // headers still give their counts.
    private bool _worktreesOpen = true, _localOpen, _remotesOpen = true, _tagsOpen, _stashesOpen;
    private readonly HashSet<string> _remotesClosed = new(StringComparer.Ordinal);

    /// <summary>The Claude session working in a worktree, as the rail shows it.</summary>
    public sealed record WorktreeSession(string Name, string State, Color Color);

    public GraphRail()
    {
        _remoteFilter = new TextBox
        {
            PlaceholderText = "Filter remote branches", FontSize = 12, MinHeight = 26, Padding = new Thickness(8, 3),
            Margin = new Thickness(0, 2, 0, 4), Classes = { "clearButton" },
        };
        _remoteFilter.TextChanged += (_, _) => BuildRemoteRows();
        _remoteFilter.KeyDown += (_, e) =>
        {
            if (e.Key != Key.Escape || string.IsNullOrEmpty(_remoteFilter.Text)) return;
            _remoteFilter.Text = "";
            e.Handled = true;
        };
        Content = new ScrollViewer
        {
            Content = new StackPanel { Spacing = 2, Margin = new Thickness(8, 4, 8, 12), Children = { _before, _remoteFilter, _remoteRows, _after } },
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

    /// <summary>A branch or tag row was right-clicked: the host shows its menu at the pointer.</summary>
    public event Action<GraphRef, Control>? RefMenuRequested;

    /// <summary>A branch row's eye was clicked: hide it from the graph (true) or show it again.</summary>
    public event Action<GraphRef, bool>? VisibilityToggled;

    /// <summary>The "⋯" on the Local or Remotes header: the host shows that section's visibility menu there.</summary>
    public event Action<Control, GraphRefKind>? VisibilityMenuRequested;
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

    /// <summary>For the headless render: opens or collapses the Worktrees and Local sections, as a header click does.</summary>
    internal void PreviewSetOpen(bool worktrees, bool local)
    {
        (_worktreesOpen, _localOpen) = (worktrees, local);
        Rebuild();
    }

    /// <summary>For the headless render: Local and Tags open (the rest folded) with the first tag group expanded.</summary>
    internal void PreviewTags()
    {
        (_worktreesOpen, _localOpen, _remotesOpen, _tagsOpen, _stashesOpen) = (false, true, false, true, false);
        if (_tagGroups && _snap is { } s && TagTree.Build(s.Refs.Where(r => r.Kind == GraphRefKind.Tag)).FirstOrDefault(n => n.IsGroup) is { } g)
            _tagGroupsOpen.Add(g.Label);
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
        _before.Children.Clear();
        _after.Children.Clear();
        Background = Ink(_c.Background);
        if (_snap is not { } s)
        {
            _remoteFilter.IsVisible = false;
            _remoteRows.Children.Clear();
            return;
        }

        _before.Children.Add(Header("Worktrees", s.Worktrees.Count, _worktreesOpen, () => _worktreesOpen = !_worktreesOpen, first: true));
        if (_worktreesOpen)
            foreach (var w in s.Worktrees.OrderByDescending(w => w.IsCurrent))
                _before.Children.Add(WorktreeCard(s, w));
        // Even with the cards collapsed, so dropping a branch on it still makes a worktree.
        if (_dragging)
            _before.Children.Add(NewWorktreeZone());

        var locals = s.Refs.Where(r => r.Kind == GraphRefKind.LocalBranch).ToList();
        _before.Children.Add(Header("Local", locals.Count, _localOpen, () => _localOpen = !_localOpen,
            hidden: locals.Count(s.IsHidden), menu: GraphRefKind.LocalBranch));
        if (_localOpen)
            foreach (var r in locals)
                _before.Children.Add(RefRow(s, r, r.Name));

        var remotes = s.Refs.Where(r => r.Kind == GraphRefKind.RemoteBranch).ToList();
        if (remotes.Count > 0)
            _before.Children.Add(Header("Remotes", remotes.Count, _remotesOpen, () => _remotesOpen = !_remotesOpen,
                hidden: remotes.Count(s.IsHidden), menu: GraphRefKind.RemoteBranch));
        _remoteFilter.IsVisible = remotes.Count > 0 && _remotesOpen;
        BuildRemoteRows();

        var tags = s.Refs.Where(r => r.Kind == GraphRefKind.Tag).ToList();
        if (tags.Count > 0)
        {
            _after.Children.Add(Header("Tags", tags.Count, _tagsOpen, () => _tagsOpen = !_tagsOpen,
                hidden: tags.Count(s.IsHidden), menu: GraphRefKind.Tag));
            if (_tagsOpen && _tagGroups)
                AddTagNodes(s, TagTree.Build(tags), 0);
            else if (_tagsOpen)
                foreach (var r in tags.OrderBy(r => s.RowOf(r.Target) is var row and >= 0 ? row : int.MaxValue).Take(200))
                    _after.Children.Add(RefRow(s, r, r.Name));
        }

        if (s.Stashes.Count > 0)
        {
            _after.Children.Add(Header("Stashes", s.Stashes.Count, _stashesOpen, () => _stashesOpen = !_stashesOpen));
            if (_stashesOpen)
                foreach (var st in s.Stashes)
                    _after.Children.Add(RefRow(s, st.AsRef(), st.Message));
        }
    }

    // ---- remotes: one foldable group per remote, under the filter box ----

    // A filter opens every group with a match (a closed group would hide what was asked for) and leaves out the rest;
    // clearing it puts each group back as it was folded.
    private void BuildRemoteRows()
    {
        _remoteRows.Children.Clear();
        if (_snap is not { } s || !_remoteFilter.IsVisible) return;
        var terms = RemoteFilterTerms(_remoteFilter.Text);
        bool filtering = terms.Length > 0;
        bool any = false;
        foreach (var g in s.Refs.Where(r => r.Kind == GraphRefKind.RemoteBranch).GroupBy(r => r.Name.Split('/')[0]))
        {
            var all = g.ToList();
            var shown = filtering ? all.Where(r => MatchesFilter(r.Name, terms)).ToList() : all;
            if (filtering && shown.Count == 0) continue;
            any = true;
            bool open = filtering || !_remotesClosed.Contains(g.Key);
            _remoteRows.Children.Add(RemoteHeader(g.Key, open, filtering, shown.Count, all.Count, all.Count(s.IsHidden)));
            if (open)
                foreach (var r in shown)
                    _remoteRows.Children.Add(RefRow(s, r, RefLabel.Display(r.Name, r.Name[(g.Key.Length + 1)..]), indent: 12));
        }
        if (filtering && !any)
            _remoteRows.Children.Add(new TextBlock
            {
                Text = "No remote branches match", FontSize = 11.5, FontStyle = FontStyle.Italic, Foreground = Ink(_c.Muted),
                Margin = new Thickness(8, 2, 0, 4),
            });
    }

    /// <summary>The words in the remote filter box (none when it's empty or Remotes is folded).</summary>
    public string[] RemoteFilter => _remoteFilter.IsVisible ? RemoteFilterTerms(_remoteFilter.Text) : [];

    /// <summary>Every whitespace-separated word must appear somewhere in the name, in any case and any order.</summary>
    internal static string[] RemoteFilterTerms(string? text) =>
        (text ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    internal static bool MatchesFilter(string name, string[] terms)
    {
        // A merge-queue branch also answers to what its row says ("queue #145 → main").
        string shown = RefLabel.Display(name, "");
        return terms.All(t => name.Contains(t, StringComparison.OrdinalIgnoreCase) || shown.Contains(t, StringComparison.OrdinalIgnoreCase));
    }

    // A remote's row: chevron and name, then how many branches (how many match while filtering). Clicking folds it,
    // except while filtering, when every group with a match is open.
    private Control RemoteHeader(string remote, bool open, bool filtering, int shown, int total, int hidden)
    {
        var dock = new DockPanel { LastChildFill = true };
        dock.Children.Add(new TextBlock
        {
            Text = filtering ? $"{shown} of {total}" : hidden > 0 ? $"{total} · {hidden} hidden" : total.ToString(),
            FontSize = 10.5, Foreground = Ink(_c.Muted), VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(6, 0, 0, 0), [DockPanel.DockProperty] = Dock.Right,
        });
        dock.Children.Add(new TextBlock
        {
            // Heavier than the branch rows under it, so a remote's name doesn't read as one of its branches.
            Text = (open ? "▾ " : "▸ ") + remote, FontSize = 11.5, FontWeight = FontWeight.SemiBold, Foreground = Ink(_c.Text, 0.85),
            TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center,
        });
        var row = new Border
        {
            Padding = new Thickness(6, 3), Margin = new Thickness(0, 2, 0, 0), CornerRadius = new CornerRadius(5),
            Background = Brushes.Transparent, Child = dock,
        };
        if (filtering) return row;
        row.Cursor = new Cursor(StandardCursorType.Hand);
        row.PointerPressed += (_, e) =>
        {
            if (!_remotesClosed.Remove(remote)) _remotesClosed.Add(remote);
            BuildRemoteRows();
            e.Handled = true;
        };
        Hover(row, -1);
        return row;
    }

    /// <summary>For the headless render: the remote filter set to <paramref name="text"/> (empty clears it), with the
    /// Remotes section open.</summary>
    internal void PreviewRemoteFilter(string text)
    {
        _remotesOpen = true;
        Rebuild();
        _remoteFilter.Text = text;
        BuildRemoteRows();
    }

    /// <summary>For the headless render: folds (or opens) one remote's group.</summary>
    internal void PreviewFoldRemote(string remote, bool fold)
    {
        if (fold) _remotesClosed.Add(remote); else _remotesClosed.Remove(remote);
        BuildRemoteRows();
    }

    // ---- tags as a tree ----

    private bool _tagGroups = true;
    private readonly HashSet<string> _tagGroupsOpen = new(StringComparer.Ordinal);

    /// <summary>Tags grouped by name (true) or listed in graph order.</summary>
    public void SetTagGroups(bool grouped)
    {
        if (_tagGroups == grouped) return;
        _tagGroups = grouped;
        Rebuild();
    }

    /// <summary>A tag group's eye was clicked: hide every tag in it (true) or show them all again.</summary>
    public event Action<IReadOnlyList<GraphRef>, bool>? GroupVisibilityToggled;

    private void AddTagNodes(GraphSnapshot s, IReadOnlyList<TagTree.Node> nodes, int depth)
    {
        foreach (var n in nodes)
        {
            if (n.Tag is { } tag)
            {
                _after.Children.Add(RefRow(s, tag, tag.Name, indent: depth * 12));
                continue;
            }
            bool open = _tagGroupsOpen.Contains(n.Label);
            _after.Children.Add(TagGroupRow(s, n, open, depth));
            if (open) AddTagNodes(s, n.Children, depth + 1);
        }
    }

    // A group's row: chevron and shared prefix, how many tags (and how many hidden), and an eye for the whole group.
    private Control TagGroupRow(GraphSnapshot s, TagTree.Node g, bool open, int depth)
    {
        var tags = g.Tags.ToList();
        int hiddenCount = tags.Count(s.IsHidden);
        bool allHidden = hiddenCount == tags.Count;
        var dock = new DockPanel { LastChildFill = true };
        var eye = Eye(allHidden);
        eye.Opacity = allHidden ? 1 : 0;
        ToolTip.SetTip(eye, allHidden ? $"Show all {tags.Count} in the graph" : $"Hide all {tags.Count} from the graph");
        eye.PointerPressed += (_, e) => { GroupVisibilityToggled?.Invoke(tags, !allHidden); e.Handled = true; };
        dock.Children.Add(eye);
        dock.Children.Add(new TextBlock
        {
            Text = hiddenCount > 0 && !allHidden ? $"{tags.Count} · {hiddenCount} hidden" : tags.Count.ToString(),
            FontSize = 10.5, Foreground = Ink(_c.Muted), VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(6, 0, 0, 0), [DockPanel.DockProperty] = Dock.Right,
        });
        dock.Children.Add(new TextBlock
        {
            Text = (open ? "▾ " : "▸ ") + g.Label, FontSize = 12, Foreground = Ink(allHidden ? _c.Muted : _c.Text, allHidden ? 0.7 : 1),
            FontStyle = allHidden ? FontStyle.Italic : FontStyle.Normal, TextTrimming = TextTrimming.PrefixCharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
        });
        var row = new Border
        {
            Padding = new Thickness(6 + depth * 12, 3, 6, 3), CornerRadius = new CornerRadius(5), Background = Brushes.Transparent,
            Cursor = new Cursor(StandardCursorType.Hand), Child = dock,
        };
        if (!allHidden)
        {
            row.PointerEntered += (_, _) => eye.Opacity = 1;
            row.PointerExited += (_, _) => eye.Opacity = 0;
        }
        row.PointerPressed += (_, e) =>
        {
            if (!_tagGroupsOpen.Remove(g.Label)) _tagGroupsOpen.Add(g.Label);
            Rebuild();
            e.Handled = true;
        };
        Hover(row, -1);
        return row;
    }

    // A section's header is a tinted band in body ink with a rule above it (bar the first), so where Local ends and
    // Remotes begins can't be mistaken for one more row, however long the lists run.
    private Control Header(string text, int count, bool? open = null, Action? toggle = null, int hidden = 0, GraphRefKind? menu = null,
        bool first = false)
    {
        var label = new TextBlock
        {
            Text = (open is null ? "" : open.Value ? "▾ " : "▸ ") + text.ToUpperInvariant(),
            FontSize = 10.5, FontWeight = FontWeight.Bold, LetterSpacing = 1.1, Foreground = Ink(_c.Text),
            VerticalAlignment = VerticalAlignment.Center,
        };
        var n = new TextBlock
        {
            Text = hidden > 0 ? $"{count} · {hidden} hidden" : count.ToString(), FontSize = 10.5, Foreground = Ink(_c.Muted),
            VerticalAlignment = VerticalAlignment.Center, [DockPanel.DockProperty] = Dock.Right,
        };
        var dock = new DockPanel();
        if (menu is { } kind)
        {
            var more = new Border
            {
                Padding = new Thickness(6, 0), Margin = new Thickness(4, 0, 0, 0), CornerRadius = new CornerRadius(4), Background = Brushes.Transparent,
                Cursor = new Cursor(StandardCursorType.Hand), [DockPanel.DockProperty] = Dock.Right,
                Child = new TextBlock { Text = "⋯", FontSize = 13, FontWeight = FontWeight.Bold, Foreground = Ink(_c.Muted) },
            };
            ToolTip.SetTip(more, "Hide or show branches");
            more.PointerPressed += (_, e) => { VisibilityMenuRequested?.Invoke(more, kind); e.Handled = true; };
            Hover(more, -1);
            dock.Children.Add(more);
        }
        dock.Children.Add(n);
        dock.Children.Add(label);
        var rest = Ink(_c.RowHover, 0.6);
        var row = new Border
        {
            Padding = new Thickness(6, 5), Margin = new Thickness(0, 0, 0, 3), Background = rest, CornerRadius = new CornerRadius(5),
            Child = dock,
        };
        if (toggle is not null)
        {
            row.Cursor = new Cursor(StandardCursorType.Hand);
            row.PointerPressed += (_, e) => { toggle(); Rebuild(); e.Handled = true; };
            Hover(row, -1, rest);
        }
        if (first) return new Border { Margin = new Thickness(0, 6, 0, 0), Child = row };
        return new StackPanel
        {
            Margin = new Thickness(0, 12, 0, 0),
            Children = { new Border { Height = 1, Background = Ink(_c.Separator), Margin = new Thickness(2, 0, 2, 8) }, row },
        };
    }

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

    // Fixed widths, so every local row's marks line up in columns whatever the name's length.
    private const double LeadW = 18, TrackW = 40, EyeW = 22;

    // A row: for a local branch, a lead mark (⌂ checked out in this window, a tree in another worktree), then the name,
    // how far it is from its upstream, and the eye. Branches and tags can be hidden; one checked out can't, but its eye
    // column is still kept so the columns don't step.
    private Control RefRow(GraphSnapshot s, GraphRef r, string label, double indent = 0)
    {
        int lane = s.LaneOf(r.Target);
        var dock = new DockPanel { LastChildFill = true };
        bool hidden = s.IsHidden(r);
        var holder = r.Kind == GraphRefKind.LocalBranch ? s.Worktrees.FirstOrDefault(w => w.Branch == r.Name) : null;
        Control? eye = null;
        if (r.Kind is GraphRefKind.LocalBranch or GraphRefKind.RemoteBranch or GraphRefKind.Tag)
        {
            if (holder is null)
            {
                // Shown on hover, and always on a hidden row so the way back is visible.
                eye = Eye(hidden);
                eye.Opacity = hidden ? 1 : 0;
                ToolTip.SetTip(eye, hidden ? "Show in the graph" : "Hide from the graph");
                eye.PointerPressed += (_, e) => { VisibilityToggled?.Invoke(r, !hidden); e.Handled = true; };
                dock.Children.Add(eye);
            }
            else
                dock.Children.Add(new Border { Width = EyeW, [DockPanel.DockProperty] = Dock.Right });
        }
        if (r.Kind == GraphRefKind.LocalBranch)
        {
            var track = new TextBlock
            {
                Text = TrackCell(r), Width = TrackW, TextAlignment = TextAlignment.Right, FontSize = 11,
                Foreground = Ink(_c.Muted), VerticalAlignment = VerticalAlignment.Center, [DockPanel.DockProperty] = Dock.Right,
            };
            ToolTip.SetTip(track, TrackTip(r));
            dock.Children.Add(track);
            dock.Children.Add(LeadMark(holder));
        }
        dock.Children.Add(new TextBlock
        {
            Text = label, FontSize = 12, Foreground = Ink(lane >= 0 && !hidden ? _c.Text : _c.Muted, hidden ? 0.7 : 1),
            FontStyle = hidden ? FontStyle.Italic : FontStyle.Normal,
            // A ref name is path-like and keeps its tail; a stash's label is its message, which reads from the start.
            TextTrimming = r.Kind == GraphRefKind.Stash ? TextTrimming.CharacterEllipsis : TextTrimming.PrefixCharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
        });
        var row = new Border
        {
            Padding = new Thickness(6 + indent, 3, 6, 3), CornerRadius = new CornerRadius(5), Background = Brushes.Transparent,
            Cursor = new Cursor(StandardCursorType.Hand), Child = dock, Tag = new RailItem(null, r),
        };
        ToolTip.SetTip(row, GraphCanvas.Describe(r, s)
                            + (hidden ? "\nHidden from the graph" : lane >= 0 ? "" : "\nNot in the loaded graph"));
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
        row.ContextRequested += (_, e) => { RefMenuRequested?.Invoke(r, row); e.Handled = true; };
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
            // EyeW wide in all (margin + padding + the 14 px drawing), so a row without an eye keeps the same column.
            Child = c, Padding = new Thickness(3, 2), Margin = new Thickness(2, 0, 0, 0), Background = Brushes.Transparent,
            Cursor = new Cursor(StandardCursorType.Hand), VerticalAlignment = VerticalAlignment.Center,
            [DockPanel.DockProperty] = Dock.Right,
        };
    }

    // ↑ commits to push, ↓ commits to pull, ✓ level with its upstream; blank when it has none (or it's gone).
    internal static string TrackCell(GraphRef r) =>
        r.Upstream is null || r.UpstreamGone ? "" : r.InSync ? "✓" : GraphCanvas.TrackText(r);

    private static string? TrackTip(GraphRef r) => r.Upstream is null ? null
        : r.UpstreamGone ? $"Tracked {r.Upstream}, which is gone from the remote"
        : r.InSync ? $"Level with {r.Upstream}"
        : $"{(r.Ahead > 0 ? $"{r.Ahead} to push" : "")}{(r.Ahead > 0 && r.Behind > 0 ? ", " : "")}{(r.Behind > 0 ? $"{r.Behind} to pull" : "")} ({r.Upstream})";

    // Stroke icons on a 12 px box, drawn because Inter has neither glyph.
    private const string HouseIcon = "M1.5,6.2 L6,2 L10.5,6.2 M3,5 V10.5 H9 V5 M5,10.5 V7.8 H7 V10.5";
    private const string TreeIcon = "M6,1.2 L10,7.8 H2 Z M6,7.8 V11";

    // Where a local branch is checked out: ⌂ in this window's worktree, a tree in another; an empty slot otherwise.
    private Control LeadMark(GraphWorktree? holder)
    {
        var slot = new Border { Width = LeadW, VerticalAlignment = VerticalAlignment.Center, [DockPanel.DockProperty] = Dock.Left };
        if (holder is null) return slot;
        slot.Child = new global::Avalonia.Controls.Shapes.Path
        {
            Data = Geometry.Parse(holder.IsCurrent ? HouseIcon : TreeIcon), Width = 12, Height = 12,
            Stroke = Ink(holder.IsCurrent ? _c.Text : _c.Muted), StrokeThickness = 1.3,
            StrokeLineCap = PenLineCap.Round, StrokeJoin = PenLineJoin.Round, HorizontalAlignment = HorizontalAlignment.Left,
        };
        slot.Background = Brushes.Transparent;
        ToolTip.SetTip(slot, holder.IsCurrent ? "Checked out in this window" : $"Checked out in the {holder.Name} worktree");
        return slot;
    }

    // A small folder outline: this is a worktree card.
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
