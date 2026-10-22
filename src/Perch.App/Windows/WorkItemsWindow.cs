using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Perch.Avalonia.Rendering;
using Perch.Avalonia.Services;
using Perch.Avalonia.Theming;
using Perch.Data;
using Perch.Data.Work;

namespace Perch.Avalonia.Windows;

/// <summary>Where the Work items window puts the caret when it opens on an item.</summary>
internal enum WorkFocus
{
    None,
    /// <summary>The name, in rename mode with all of it selected (a freshly created item named after its session).</summary>
    Rename,
    /// <summary>The activity box ("Log a note…").</summary>
    Log,
    /// <summary>The "new work item" field in the list.</summary>
    Compose,
}

/// <summary>
/// The work items window (docs/work-items-plan.md W3), opened from the overlay's item headers and session menus and
/// the tray. Laid out for progressive disclosure: only what an item actually has is on screen.
///
/// <para>Left, the list: a quiet "+ New work item" row that turns into a name field, then Active and Paused, with
/// Done folded into a row of its own. Right, the selected item: its colour, its name (click to rename), a status pill
/// and a "⋯" menu for the rarer actions (open the file, show it in its folder, copy the log, delete). Then its
/// sessions, which are the point of an item; then its notes and links, each only once it has some (until then a
/// small "+ Notes" / "+ Link" chip); then its activity, the last few entries with the rest a click away.</para>
///
/// <para>Every change goes through <see cref="WorkItemsHost"/> (a read-modify-write of the item's one file), which
/// raises <c>Changed</c>. The window's own changes rebuild the pane at once; an outside one (a scan's write, the CLI,
/// a hand edit) waits while the user is typing in the pane and lands when they leave the field, and a notes draft
/// survives any rebuild. Chrome styled off <see cref="TodoWindow"/>.</para>
/// </summary>
internal sealed class WorkItemsWindow : Window
{
    private static readonly IBrush Bg = Palette.OverlaySurfaceBrush;
    private static readonly IBrush Bg2 = Palette.ButtonBgBrush;
    private static readonly IBrush Stroke = Palette.BorderBrush;
    private static readonly IBrush Fg = Palette.FgBrush;
    private static readonly IBrush Title2 = Palette.TitleBrush;
    private static readonly IBrush Muted = Palette.MutedBrush;
    private static readonly IBrush Accent = Palette.AccentBrush;
    private static readonly IBrush RowHover = new SolidColorBrush(Color.FromArgb(24, 255, 255, 255));
    private static Color AccentColor => Palette.Active.Accent.ToColor();
    private static IBrush SelBrush => new SolidColorBrush(Color.FromArgb(30, AccentColor.R, AccentColor.G, AccentColor.B));

    // How many activity entries show before "Show all".
    private const int ActivityPreview = 5;

    private readonly WorkItemsHost _host;
    private readonly Func<IReadOnlyList<ClaudeSession>> _roster;
    private readonly Func<bool> _overlayGrouped;

    private readonly StackPanel _list = new();
    private readonly ContentControl _detailHost = new();
    private readonly ScrollViewer _detailScroll;

    private string? _selectedId;
    private string _detailSignature = "";
    private string _sessionsSignature = "";

    // Disclosure state: what's open right now. Reset when another item is selected.
    private bool _composing, _doneExpanded, _renaming, _editingNotes, _addingLink, _showAllActivity;
    private string? _notesDraft;
    // A change the window made itself (rebuild the pane at once) vs one from elsewhere, which waits while the user is
    // typing in the pane (_detailStale) and lands when they leave the field.
    private bool _ownChange, _detailStale;

    private StackPanel? _sessionsPanel;
    private TextBox? _composerBox, _nameBox, _logBox;

    /// <summary>A live session's row was clicked: focus it (its Perch window or its terminal).</summary>
    public event Action<ClaudeSession>? FocusSessionRequested;
    /// <summary>An ended session: resume it in a Perch window (id, cwd).</summary>
    public event Action<string, string>? ResumeInPerchRequested;
    /// <summary>An ended session: resume it with <c>claude --resume</c> in a terminal (id, cwd).</summary>
    public event Action<string, string>? ResumeInTerminalRequested;
    /// <summary>Open a session's history (id).</summary>
    public event Action<string>? HistoryRequested;
    /// <summary>"+ New session" on an item: start a session inside it (item id).</summary>
    public event Action<string>? NewSessionRequested;
    /// <summary>"Group by work item" under the list: turn the overlay's grouping on.</summary>
    public event Action? GroupOverlayRequested;

    public WorkItemsWindow(WorkItemsHost host, Func<IReadOnlyList<ClaudeSession>> roster, Func<bool> overlayGrouped)
    {
        _host = host;
        _roster = roster;
        _overlayGrouped = overlayGrouped;

        Title = "Work items";
        WindowDecorations = WindowDecorations.None;
        Background = Brushes.Transparent;
        TransparencyLevelHint = [WindowTransparencyLevel.Transparent];
        // Frameless, so there's no system resize border: the grips laid over the edges (ResizeGrips) drive it.
        CanResize = true;
        Width = 860;
        Height = 640;
        MinWidth = 600;
        MinHeight = 400;
        ShowInTaskbar = true;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;

        // ── Header (draggable): just the title and close ──
        var closeGlyph = new Button
        {
            Content = "✕", Foreground = Muted, Background = Brushes.Transparent, BorderThickness = new Thickness(0),
            Padding = new Thickness(4, 0), FontSize = 14, Cursor = new Cursor(StandardCursorType.Hand),
            HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center,
        };
        closeGlyph.Click += (_, _) => Close();
        var headerBorder = new Border
        {
            Background = Bg, BorderBrush = Stroke, BorderThickness = new Thickness(0, 0, 0, 1),
            Child = new Grid
            {
                Margin = new Thickness(20, 13, 16, 13),
                Children =
                {
                    new TextBlock { Text = "Work items", Foreground = Title2, FontWeight = FontWeight.Bold, FontSize = 15, VerticalAlignment = VerticalAlignment.Center },
                    closeGlyph,
                },
            },
        };
        headerBorder.PointerPressed += (_, e) =>
        {
            if (e.Source is Button) return;
            if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) BeginMoveDrag(e);
        };

        // ── Left: the list ──
        var listScroller = new ScrollViewer
        {
            Content = _list, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Padding = new Thickness(0, 6, 0, 10),
        };

        // ── Right: the selected item ──
        _detailHost.Padding = new Thickness(28, 22, 28, 28);
        _detailScroll = new ScrollViewer
        {
            Content = _detailHost, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        };

        var body = new Grid { ColumnDefinitions = new ColumnDefinitions("250,1,*") };
        var left = new Border { Background = Palette.SurfaceSunkenBrush, Child = listScroller };
        var divider = new Border { Background = Stroke };
        Grid.SetColumn(left, 0);
        Grid.SetColumn(divider, 1);
        Grid.SetColumn(_detailScroll, 2);
        body.Children.Add(left);
        body.Children.Add(divider);
        body.Children.Add(_detailScroll);

        var root = new DockPanel();
        DockPanel.SetDock(headerBorder, Dock.Top);
        root.Children.Add(headerBorder);
        root.Children.Add(body);

        var layers = new Grid
        {
            Children =
            {
                new Border
                {
                    Background = Bg, CornerRadius = new CornerRadius(12), BorderBrush = Stroke, BorderThickness = new Thickness(1.5),
                    Child = root, ClipToBounds = true,
                },
            },
        };
        foreach (var grip in ResizeGrips.For(this)) layers.Children.Add(grip);
        Content = layers;

        _host.Changed += OnHostChanged;
        Closed += (_, _) => _host.Changed -= OnHostChanged;
        _detailHost.AddHandler(LostFocusEvent, (_, _) => OnDetailLostFocus(), handledEventsToo: true);
        Refresh();
    }

    /// <summary>Points the reused window at an item (or just the list) and puts the caret where the caller asked.</summary>
    public void Retarget(string? itemId, WorkFocus focus = WorkFocus.None)
    {
        if (itemId is not null && _host.Find(itemId) is { } item)
        {
            if (item.Status == WorkItemStatus.Done) _doneExpanded = true;
            if (item.Id != _selectedId) ResetDisclosure();
            _selectedId = itemId;
            _detailSignature = "";
        }
        if (focus == WorkFocus.Rename && _selectedId is not null) { _renaming = true; _detailSignature = ""; }
        if (focus == WorkFocus.Compose) _composing = true;
        Refresh();
        Dispatcher.UIThread.Post(() =>
        {
            switch (focus)
            {
                case WorkFocus.Rename: _nameBox?.Focus(); _nameBox?.SelectAll(); break;
                case WorkFocus.Log: _logBox?.Focus(); break;
                case WorkFocus.Compose: _composerBox?.Focus(); break;
            }
        }, DispatcherPriority.Input);
    }

    /// <summary>The roster moved (a scan): redraw the sessions block if any member's state changed.</summary>
    public void RosterChanged()
    {
        if (_selectedId is null || _host.Find(_selectedId) is not { } item || _sessionsPanel is null) return;
        if (SessionsSignature(item) == _sessionsSignature) return;
        FillSessions(_sessionsPanel, item);
    }

    /// <summary>The overlay's grouping was switched (here or on the overlay): show or drop the list's link.</summary>
    public void GroupingChanged() => RefreshList();

    private void ResetDisclosure()
    {
        _renaming = _editingNotes = _addingLink = _showAllActivity = false;
        _notesDraft = null;
    }

    private void OnHostChanged()
    {
        RefreshList();
        if (!_ownChange && TypingInDetail()) { _detailStale = true; return; }
        RenderDetail();
    }

    // Runs one of the window's own writes, so the Changed it raises rebuilds the pane straight away.
    private T Own<T>(Func<T> act)
    {
        _ownChange = true;
        try { return act(); }
        finally { _ownChange = false; }
    }

    // The keyboard focus is in a text field inside the detail pane.
    private bool TypingInDetail() =>
        FocusManager?.GetFocusedElement() is Visual v
        && (v as TextBox ?? v.FindAncestorOfType<TextBox>()) is { } box
        && _detailHost.IsVisualAncestorOf(box);

    // Focus left a field: apply an outside change that waited for it.
    private void OnDetailLostFocus()
    {
        if (!_detailStale) return;
        Dispatcher.UIThread.Post(() =>
        {
            if (!_detailStale || TypingInDetail()) return;
            _detailStale = false;
            RenderDetail();
        }, DispatcherPriority.Background);
    }

    private void Refresh()
    {
        RefreshList();
        RenderDetail();
    }

    // Rebuild the pane after a disclosure toggle (not a data change).
    private void Rerender()
    {
        _detailSignature = "";
        RenderDetail();
    }

    // ── The list ──

    private void RefreshList()
    {
        _list.Children.Clear();
        _list.Children.Add(ComposerRow());

        var items = _host.Items;
        AddGroup("Active", items.Where(i => i.Status == WorkItemStatus.Active));
        AddGroup("Paused", items.Where(i => i.Status == WorkItemStatus.Paused));

        var done = items.Where(i => i.Status == WorkItemStatus.Done).Reverse().ToList();
        if (done.Count > 0)
        {
            _list.Children.Add(FoldRow($"Done · {done.Count}", _doneExpanded, () => { _doneExpanded = !_doneExpanded; RefreshList(); }));
            if (_doneExpanded) foreach (var i in done) _list.Children.Add(BuildListRow(i));
        }

        if (items.Count == 0)
            _list.Children.Add(new TextBlock
            {
                Text = "Name a piece of work, then put the sessions doing it inside. You can also right-click a session "
                       + "in the overlay and choose Work items.",
                Foreground = Muted, FontSize = 12, Margin = new Thickness(16, 10), TextWrapping = TextWrapping.Wrap,
                LineHeight = 18,
            });
        else if (!_overlayGrouped())
            _list.Children.Add(GroupOverlayRow());

        if (_host.Store.Unreadable.Count > 0)
            _list.Children.Add(new TextBlock
            {
                Text = $"Couldn't read {string.Join(", ", _host.Store.Unreadable.Select(Path.GetFileName))}. It's left as it is.",
                Foreground = new SolidColorBrush(Palette.Danger), FontSize = 11.5, Margin = new Thickness(16, 12),
                TextWrapping = TextWrapping.Wrap,
            });

        if (_selectedId is { } sel && _host.Find(sel) is null) _selectedId = null;
    }

    // "+ New work item": a quiet row that becomes a name field. Enter creates, Esc (or leaving it empty) folds back.
    private Control ComposerRow()
    {
        if (!_composing)
        {
            var row = Hoverable(new TextBlock { Text = "+  New work item", Foreground = Accent, FontSize = 12.5, FontWeight = FontWeight.SemiBold },
                new Thickness(16, 9));
            row.Margin = new Thickness(0, 0, 0, 4);
            row.PointerReleased += (_, e) =>
            {
                if (e.InitialPressMouseButton != MouseButton.Left) return;
                _composing = true;
                RefreshList();
                Dispatcher.UIThread.Post(() => _composerBox?.Focus(), DispatcherPriority.Input);
            };
            return row;
        }

        _composerBox = new TextBox { PlaceholderText = "Name it, then press Enter", FontSize = 13, Margin = new Thickness(12, 4, 12, 8) };
        _composerBox.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter) { CommitCreate(); e.Handled = true; }
            else if (e.Key == Key.Escape) { _composing = false; RefreshList(); e.Handled = true; }
        };
        _composerBox.LostFocus += (_, _) =>
        {
            if (string.IsNullOrWhiteSpace(_composerBox?.Text) && _composing)
                Dispatcher.UIThread.Post(() => { _composing = false; RefreshList(); }, DispatcherPriority.Background);
        };
        return _composerBox;
    }

    // Under the list while the overlay is flat: where grouping lives, offered where you're already looking at items,
    // and one click to turn it on. The overlay's own button turns it back off.
    private Control GroupOverlayRow()
    {
        var link = new TextBlock
        {
            Text = "Group by work item", Foreground = Accent, FontSize = 12, FontWeight = FontWeight.SemiBold,
            Cursor = new Cursor(StandardCursorType.Hand), Margin = new Thickness(0, 4, 0, 0),
        };
        link.PointerReleased += (_, e) => { if (e.InitialPressMouseButton == MouseButton.Left) GroupOverlayRequested?.Invoke(); };
        return new StackPanel
        {
            Margin = new Thickness(16, 18, 16, 6),
            Children =
            {
                new TextBlock
                {
                    Text = "The overlay lists your sessions flat. Group it to show each item's sessions under it.",
                    Foreground = Muted, FontSize = 11.5, TextWrapping = TextWrapping.Wrap, LineHeight = 17,
                },
                link,
            },
        };
    }

    private void AddGroup(string name, IEnumerable<WorkItem> items)
    {
        var list = items.ToList();
        if (list.Count == 0) return;
        _list.Children.Add(new TextBlock
        {
            FontSize = 10.5, FontWeight = FontWeight.SemiBold, Foreground = Muted, LetterSpacing = 0.6,
            Margin = new Thickness(16, 12, 16, 4), Text = name.ToUpperInvariant(),
        });
        foreach (var i in list) _list.Children.Add(BuildListRow(i));
    }

    private Control FoldRow(string text, bool open, Action toggle)
    {
        var row = Hoverable(new TextBlock
        {
            Text = (open ? "▾  " : "▸  ") + text, FontSize = 11.5, Foreground = Muted, FontWeight = FontWeight.SemiBold,
        }, new Thickness(16, 8));
        row.Margin = new Thickness(0, 10, 0, 0);
        row.PointerReleased += (_, e) => { if (e.InitialPressMouseButton == MouseButton.Left) toggle(); };
        return row;
    }

    private Control BuildListRow(WorkItem item)
    {
        var roster = _roster();
        int live = item.Members.Count(m => roster.Any(s => s.SessionId == m.SessionId && !s.IsDormant));
        bool done = item.Status == WorkItemStatus.Done;
        var title = new TextBlock
        {
            Text = item.Name, FontSize = 13, Foreground = done ? Muted : Fg,
            TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(10, 0, 8, 0),
        };
        // A running count only when something runs: a quiet list reads as a list of names.
        var meta = new TextBlock
        {
            FontSize = 11, Foreground = new SolidColorBrush(Palette.Green), VerticalAlignment = VerticalAlignment.Center,
            Text = live > 0 ? $"● {live}" : "",
        };
        if (live > 0) ToolTip.SetTip(meta, live == 1 ? "1 session running" : $"{live} sessions running");
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto") };
        var swatch = Swatch(item.Colour, 9);
        if (done) swatch.Opacity = 0.5;
        Grid.SetColumn(swatch, 0);
        Grid.SetColumn(title, 1);
        Grid.SetColumn(meta, 2);
        grid.Children.Add(swatch);
        grid.Children.Add(title);
        grid.Children.Add(meta);

        bool selected = item.Id == _selectedId;
        var row = new Border
        {
            Child = grid, Padding = new Thickness(14, 8, 14, 8), Cursor = new Cursor(StandardCursorType.Hand),
            BorderThickness = new Thickness(2, 0, 0, 0),
            BorderBrush = selected ? Accent : Brushes.Transparent,
            Background = selected ? SelBrush : Brushes.Transparent,
        };
        if (!selected)
        {
            row.PointerEntered += (_, _) => row.Background = RowHover;
            row.PointerExited += (_, _) => row.Background = Brushes.Transparent;
        }
        row.PointerReleased += (_, e) =>
        {
            if (e.InitialPressMouseButton != MouseButton.Left) return;
            Select(item.Id);
        };
        return row;
    }

    private void Select(string id)
    {
        if (id != _selectedId) ResetDisclosure();
        _selectedId = id;
        _detailSignature = "";
        Refresh();
        _detailScroll.Offset = default;
    }

    private void CommitCreate()
    {
        var name = _composerBox?.Text?.Trim() ?? "";
        if (name.Length == 0) { _composerBox?.Focus(); return; }
        if (Own(() => _host.Create(name)) is not { } item) return;
        _composing = false;
        Select(item.Id);
    }

    // ── The detail ──

    private void RenderDetail()
    {
        if (_selectedId is null || _host.Find(_selectedId) is not { } item)
        {
            _detailSignature = "";
            _sessionsPanel = null;
            _nameBox = _logBox = null;
            _detailHost.Content = EmptyDetail();
            return;
        }
        // Rebuild only when the item's content or the disclosure state moved, never for a no-op reload.
        var sig = $"{WorkItemFile.Serialize(item)}|{_renaming}{_editingNotes}{_addingLink}{_showAllActivity}";
        if (sig == _detailSignature) { RosterChanged(); return; }
        _detailSignature = sig;
        _detailHost.Content = Detail(item);
    }

    private Control EmptyDetail() => new StackPanel
    {
        HorizontalAlignment = HorizontalAlignment.Center, Spacing = 6, Margin = new Thickness(0, 200, 0, 0),
        Children =
        {
            new TextBlock { Text = _host.Items.Count == 0 ? "No work items yet" : "Select a work item", Foreground = Muted, FontSize = 14, FontWeight = FontWeight.SemiBold, HorizontalAlignment = HorizontalAlignment.Center },
            new TextBlock { Text = "Its sessions, notes and activity show here.", Foreground = Muted, Opacity = 0.75, FontSize = 12, HorizontalAlignment = HorizontalAlignment.Center },
        },
    };

    private Control Detail(WorkItem item)
    {
        var stack = new StackPanel();
        stack.Children.Add(TitleBlock(item));
        stack.Children.Add(SessionsSection(item));

        // Notes and links appear once there's something in them (or they're being written); until then a chip each.
        bool showNotes = item.Notes.Length > 0 || _editingNotes;
        bool showLinks = item.Links.Count > 0 || _addingLink;
        if (showNotes) stack.Children.Add(NotesSection(item));
        if (showLinks) stack.Children.Add(LinksSection(item));
        if (!showNotes || !showLinks)
        {
            var chips = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Thickness(0, 22, 0, 0) };
            if (!showNotes) chips.Children.Add(Chip("+ Notes", () => { _editingNotes = true; Rerender(); }));
            if (!showLinks) chips.Children.Add(Chip("+ Link", () => { _addingLink = true; Rerender(); }));
            stack.Children.Add(chips);
        }

        stack.Children.Add(ActivitySection(item));
        return stack;
    }

    // Swatch · name (click to rename) · status pill · ⋯, over one muted line of facts.
    private Control TitleBlock(WorkItem item)
    {
        var swatchBtn = new Button
        {
            Background = Brushes.Transparent, BorderThickness = new Thickness(0), Padding = new Thickness(0, 0, 12, 0),
            Cursor = new Cursor(StandardCursorType.Hand), VerticalAlignment = VerticalAlignment.Center,
            Content = Swatch(item.Colour, 14), Flyout = ColourFlyout(item),
        };
        ToolTip.SetTip(swatchBtn, "Change colour");

        Control name;
        if (_renaming)
        {
            _nameBox = new TextBox
            {
                Text = item.Name, FontSize = 19, FontWeight = FontWeight.SemiBold, Padding = new Thickness(6, 2),
                VerticalAlignment = VerticalAlignment.Center,
            };
            bool committed = false;
            void Commit()
            {
                if (committed) return;
                committed = true;
                _renaming = false;
                var n = _nameBox!.Text?.ReplaceLineEndings(" ").Trim() ?? "";
                if (n.Length > 0 && n != item.Name)
                    Own(() => _host.Mutate(item.Id, i => { i.Name = n; i.AddLog($"Renamed to \"{n}\"", byPerch: true); }));
                Rerender();
            }
            _nameBox.KeyDown += (_, e) =>
            {
                if (e.Key == Key.Enter) { Commit(); e.Handled = true; }
                else if (e.Key == Key.Escape) { committed = true; _renaming = false; Rerender(); e.Handled = true; }
            };
            _nameBox.LostFocus += (_, _) => Commit();
            name = _nameBox;
        }
        else
        {
            var text = new TextBlock
            {
                Text = item.Name, FontSize = 19, FontWeight = FontWeight.SemiBold, Foreground = Title2,
                TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center,
                Cursor = new Cursor(StandardCursorType.Ibeam), Background = Brushes.Transparent,
            };
            ToolTip.SetTip(text, "Click to rename");
            text.PointerReleased += (_, e) =>
            {
                if (e.InitialPressMouseButton != MouseButton.Left) return;
                _renaming = true;
                Rerender();
                Dispatcher.UIThread.Post(() => { _nameBox?.Focus(); _nameBox?.SelectAll(); }, DispatcherPriority.Input);
            };
            name = text;
        }

        var status = StatusPill(item);
        status.Margin = new Thickness(12, 0, 6, 0);
        var more = MoreButton(item);

        var row = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto,Auto") };
        Grid.SetColumn(swatchBtn, 0);
        Grid.SetColumn(name, 1);
        Grid.SetColumn(status, 2);
        Grid.SetColumn(more, 3);
        row.Children.Add(swatchBtn);
        row.Children.Add(name);
        row.Children.Add(status);
        row.Children.Add(more);

        int live = LiveMembers(item).Count();
        var facts = $"Started {item.CreatedUtc.ToLocalTime():d MMM yyyy}";
        if (live > 0) facts += live == 1 ? " · 1 session running" : $" · {live} sessions running";
        var sub = new TextBlock { Text = facts, Foreground = Muted, FontSize = 12, Margin = new Thickness(26, 4, 0, 0) };

        return new StackPanel { Children = { row, sub } };
    }

    // "● Active ▾": the status, and a menu to change it. Only the user moves an item between them (D3).
    private Control StatusPill(WorkItem item)
    {
        var dot = item.Status switch
        {
            WorkItemStatus.Active => Palette.Green,
            WorkItemStatus.Paused => Palette.Yellow,
            _ => Palette.Idle,
        };
        var pill = new Button
        {
            Background = Brushes.Transparent, BorderBrush = Stroke, BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(999), Padding = new Thickness(10, 3, 9, 3), Cursor = new Cursor(StandardCursorType.Hand),
            VerticalAlignment = VerticalAlignment.Center,
            Content = new StackPanel
            {
                Orientation = Orientation.Horizontal, Spacing = 6,
                Children =
                {
                    new Border { Width = 7, Height = 7, CornerRadius = new CornerRadius(4), Background = new SolidColorBrush(dot), VerticalAlignment = VerticalAlignment.Center },
                    new TextBlock { Text = item.Status.ToString(), FontSize = 12, Foreground = Fg, VerticalAlignment = VerticalAlignment.Center },
                    new TextBlock { Text = "▾", FontSize = 10, Foreground = Muted, VerticalAlignment = VerticalAlignment.Center },
                },
            },
        };
        pill.Click += (_, _) =>
        {
            var items = new List<Control>();
            foreach (var (st, label) in new[]
                     {
                         (WorkItemStatus.Active, "Active"), (WorkItemStatus.Paused, "Paused: one dim line in the overlay"),
                         (WorkItemStatus.Done, "Done: off the overlay, kept here"),
                     })
            {
                var mi = new MenuItem { Header = label, ToggleType = MenuItemToggleType.Radio, IsChecked = item.Status == st };
                mi.Click += (_, _) => Own(() => _host.SetStatus(item.Id, st));
                items.Add(mi);
            }
            new MenuFlyout { ItemsSource = items, Placement = PlacementMode.BottomEdgeAlignedRight }.ShowAt(pill);
        };
        return pill;
    }

    // "⋯": the rarer actions, out of the way.
    private Control MoreButton(WorkItem item)
    {
        var more = new Button
        {
            Content = "⋯", FontSize = 16, Foreground = Muted, Background = Brushes.Transparent, BorderThickness = new Thickness(0),
            Padding = new Thickness(8, 0, 2, 2), Cursor = new Cursor(StandardCursorType.Hand), VerticalAlignment = VerticalAlignment.Center,
        };
        ToolTip.SetTip(more, "More");
        more.Click += (_, _) =>
        {
            var items = new List<Control>
            {
                Item("Open file", () => { if (item.FilePath is { } p) PlatformServices.FileRevealer.OpenInEditor(p); }),
                Item("Show in folder", () => { if (item.FilePath is { } p) PlatformServices.FileRevealer.RevealInFileManager(p); }),
                Item("Copy activity as Markdown", () => TopLevel.GetTopLevel(this)?.Clipboard?.SetTextAsync(LogMarkdown(item))),
                new Separator(),
                Item("Delete…", async () =>
                {
                    bool ok = await ConfirmDialog.ShowAsync(this, "Delete this work item?",
                        $"\"{item.Name}\" and its notes, links and activity are deleted. Its sessions aren't touched. "
                        + "To finish an item and keep its history, mark it Done instead.", "Delete", "Cancel", more);
                    if (ok) { Own(() => _host.Delete(item.Id)); _selectedId = null; ResetDisclosure(); Refresh(); }
                }),
            };
            new MenuFlyout { ItemsSource = items, Placement = PlacementMode.BottomEdgeAlignedRight }.ShowAt(more);
        };
        return more;
    }

    private Flyout ColourFlyout(WorkItem item)
    {
        var flyout = new Flyout { Placement = PlacementMode.BottomEdgeAlignedLeft };
        var grid = new WrapPanel { Width = 4 * 30, Margin = new Thickness(8) };
        foreach (var name in WorkHues.Names)
        {
            bool on = WorkHues.IndexOf(item.Colour) == WorkHues.IndexOf(name);
            var b = new Button
            {
                Width = 26, Height = 26, Margin = new Thickness(2), Padding = new Thickness(0),
                Background = Brushes.Transparent, BorderThickness = new Thickness(on ? 2 : 0), BorderBrush = Fg,
                CornerRadius = new CornerRadius(6), Cursor = new Cursor(StandardCursorType.Hand),
                Content = Swatch(name, 16), HorizontalContentAlignment = HorizontalAlignment.Center,
                VerticalContentAlignment = VerticalAlignment.Center,
            };
            ToolTip.SetTip(b, name);
            b.Click += (_, _) =>
            {
                flyout.Hide();
                if (!on) Own(() => _host.Mutate(item.Id, i => i.Colour = name));
            };
            grid.Children.Add(b);
        }
        flyout.Content = grid;
        return flyout;
    }

    // ── Sessions ──

    private Control SessionsSection(WorkItem item)
    {
        _sessionsPanel = new StackPanel { Spacing = 1 };
        FillSessions(_sessionsPanel, item);

        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 14 };
        if (item.Status != WorkItemStatus.Done)
            actions.Children.Add(TextAction("+ New session", Accent, _ => NewSessionRequested?.Invoke(item.Id)));
        actions.Children.Add(TextAction("Add running ▾", Muted, b => ShowAddSessionMenu(b, item.Id)));
        return Section("Sessions", actions, _sessionsPanel, top: 26);
    }

    private IEnumerable<ClaudeSession> LiveMembers(WorkItem item)
    {
        var roster = _roster();
        return item.Members.Select(m => roster.FirstOrDefault(s => s.SessionId == m.SessionId && !s.IsDormant))
            .OfType<ClaudeSession>();
    }

    private string SessionsSignature(WorkItem item)
    {
        var roster = _roster();
        return string.Join("|", item.Members.Select(m =>
        {
            var s = roster.FirstOrDefault(r => r.SessionId == m.SessionId);
            return $"{m.SessionId}:{s?.Status}:{s?.IsDormant}:{s?.DisplayName}";
        }));
    }

    private void FillSessions(StackPanel panel, WorkItem item)
    {
        _sessionsSignature = SessionsSignature(item);
        panel.Children.Clear();
        if (item.Members.Count == 0)
        {
            panel.Children.Add(new Border
            {
                BorderBrush = Stroke, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(8),
                Padding = new Thickness(14, 12),
                Child = new TextBlock
                {
                    Text = "No sessions yet. Start one with + New session, tick this item in a session's right-click menu, "
                           + $"or run claude in any terminal with PERCH_WORK={item.Id}.",
                    Foreground = Muted, FontSize = 12.5, TextWrapping = TextWrapping.Wrap, LineHeight = 19,
                },
            });
            return;
        }
        var roster = _roster();
        // Running ones first, then the rest newest first.
        var ordered = item.Members
            .Select(m => (Member: m, Live: roster.FirstOrDefault(s => s.SessionId == m.SessionId)))
            .OrderBy(x => x.Live is { IsDormant: false } ? 0 : 1)
            .ThenByDescending(x => x.Member.AddedUtc)
            .ToList();
        foreach (var (m, s) in ordered) panel.Children.Add(SessionRow(item, m, s));
    }

    private Control SessionRow(WorkItem item, WorkMember m, ClaudeSession? s)
    {
        bool running = s is { IsDormant: false };
        var (statusText, statusColor) = s is null ? ("ended", Palette.Idle)
            : s.IsDormant ? ("not running", Palette.Idle)
            : s.Status switch
            {
                SessionStatus.Running => ("working", Palette.Green),
                SessionStatus.NeedsAttention => ("done", Palette.Orange),
                SessionStatus.AwaitingInput => ("needs you", Palette.Yellow),
                SessionStatus.ApiError => ("api error", Palette.Red),
                _ => ("idle", Palette.Idle),
            };
        var dot = new Border
        {
            Width = 8, Height = 8, CornerRadius = new CornerRadius(4), Background = new SolidColorBrush(statusColor),
            VerticalAlignment = VerticalAlignment.Center, Opacity = running ? 1 : 0.55,
        };

        string title = s?.DisplayName ?? m.Title ?? $"Session {m.SessionId[..Math.Min(8, m.SessionId.Length)]}";
        var name = new TextBlock
        {
            Text = title, FontSize = 13, Foreground = running ? Fg : Muted, TextTrimming = TextTrimming.CharacterEllipsis,
        };
        var cwd = s?.Cwd ?? m.Cwd;
        var folder = new TextBlock
        {
            Text = cwd ?? "", FontSize = 11, Foreground = Muted, Opacity = 0.8, IsVisible = !string.IsNullOrEmpty(cwd),
            TextTrimming = TextTrimming.PrefixCharacterEllipsis,   // keep the folder's own name, lose the drive
        };
        var text = new StackPanel { Children = { name, folder }, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(11, 0, 10, 0) };

        var status = new TextBlock
        {
            Text = statusText, FontSize = 11.5, VerticalAlignment = VerticalAlignment.Center,
            Foreground = running && statusColor != Palette.Idle ? new SolidColorBrush(statusColor) : Muted,
        };
        // Taking a session out is a hover action, so a list of sessions isn't a column of crosses.
        var remove = IconButton("✕", "Take out of this item");
        remove.Opacity = 0;
        remove.Click += (_, _) => Own(() => _host.RemoveSession(item.Id, m.SessionId));

        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto,Auto") };
        Grid.SetColumn(dot, 0);
        Grid.SetColumn(text, 1);
        Grid.SetColumn(status, 2);
        Grid.SetColumn(remove, 3);
        grid.Children.Add(dot);
        grid.Children.Add(text);
        grid.Children.Add(status);
        grid.Children.Add(remove);

        var row = new Border
        {
            Child = grid, Padding = new Thickness(10, 7, 4, 7), CornerRadius = new CornerRadius(7),
            Background = Brushes.Transparent, Cursor = new Cursor(StandardCursorType.Hand), Margin = new Thickness(-10, 0, 0, 0),
        };
        row.PointerEntered += (_, _) => { row.Background = RowHover; remove.Opacity = 1; };
        row.PointerExited += (_, _) => { row.Background = Brushes.Transparent; remove.Opacity = 0; };
        row.PointerReleased += (_, e) =>
        {
            if (e.InitialPressMouseButton != MouseButton.Left) return;
            if (e.Source is Visual v && v.FindAncestorOfType<Button>() is not null) return;
            if (s is not null) FocusSessionRequested?.Invoke(s);
            else ShowEndedMenu(row, m);
        };
        var via = m.Via switch
        {
            WorkJoin.Launched => "Started inside this item",
            WorkJoin.Terminal => "Joined from a terminal (PERCH_WORK)",
            _ => "Added by you",
        };
        ToolTip.SetTip(row, $"{(s is null ? "Click to resume" : "Click to open")}\n{via}, {m.AddedUtc.ToLocalTime():d MMM HH:mm}");
        return row;
    }

    // An ended session: resume it (here or in a terminal), or read its history.
    private void ShowEndedMenu(Control anchor, WorkMember m)
    {
        var items = new List<Control>();
        if (m.Cwd is { } cwd)
        {
            items.Add(Item("Resume in Perch", () => ResumeInPerchRequested?.Invoke(m.SessionId, cwd)));
            items.Add(Item("Resume in terminal", () => ResumeInTerminalRequested?.Invoke(m.SessionId, cwd)));
        }
        items.Add(Item("View history", () => HistoryRequested?.Invoke(m.SessionId)));
        new MenuFlyout { ItemsSource = items, Placement = PlacementMode.BottomEdgeAlignedLeft }.ShowAt(anchor);
    }

    // The running sessions not in this item yet.
    private void ShowAddSessionMenu(Control anchor, string itemId)
    {
        if (_host.Find(itemId) is not { } item) return;
        var candidates = _roster().Where(s => !item.HasMember(s.SessionId))
            .GroupBy(s => s.SessionId).Select(g => g.First())
            .OrderBy(s => s.DisplayName, StringComparer.OrdinalIgnoreCase).ToList();
        var items = new List<Control>();
        foreach (var s in candidates)
            items.Add(Item(s.IsDormant ? s.DisplayName + " (not running)" : s.DisplayName,
                () => Own(() => _host.AddSession(itemId, s.SessionId, s.DisplayName, s.Cwd, WorkJoin.Manual))));
        if (items.Count == 0)
            items.Add(new MenuItem { Header = "Every running session is already in it", IsEnabled = false });
        new MenuFlyout { ItemsSource = items, Placement = PlacementMode.BottomEdgeAlignedRight }.ShowAt(anchor);
    }

    // ── Notes ──

    private Control NotesSection(WorkItem item)
    {
        if (_editingNotes)
        {
            // The draft outlives a rebuild of the pane (a status click, a scan's write), so unsaved notes are never lost.
            var box = new TextBox
            {
                Text = _notesDraft ?? item.Notes, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, FontSize = 13,
                MinHeight = 120, PlaceholderText = "A goal, a plan, a scratchpad. Markdown.",
                VerticalContentAlignment = VerticalAlignment.Top,
            };
            box.TextChanged += (_, _) => _notesDraft = box.Text;
            var save = SolidButton("Save");
            save.Click += (_, _) =>
            {
                _editingNotes = false;
                _notesDraft = null;
                var text = box.Text ?? "";
                Own(() => _host.Mutate(item.Id, i => { if (i.Notes == text.Trim()) return false; i.Notes = text; return true; }));
                Rerender();
            };
            var cancel = OutlineButton("Cancel", Muted);
            cancel.Click += (_, _) => { _editingNotes = false; _notesDraft = null; Rerender(); };
            Dispatcher.UIThread.Post(() => box.Focus(), DispatcherPriority.Input);
            return Section("Notes", new Border(), new StackPanel
            {
                Spacing = 8,
                Children = { box, new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right, Children = { cancel, save } } },
            });
        }

        var edit = TextAction("Edit", Muted, _ => { _editingNotes = true; Rerender(); });
        return Section("Notes", edit, MarkdownView.Build(item.Notes, NotesStyle()));
    }

    private static MarkdownStyle NotesStyle() => new(
        Fg: Palette.FgBrush, Muted: Palette.MutedBrush, Title: Palette.TitleBrush, Link: Palette.AccentBrush,
        CodeFg: Palette.FgBrush, CodeBg: Palette.SurfaceSunkenBrush, QuoteBar: Palette.BorderBrush,
        Rule: Palette.SeparatorBrush, TableBorder: Palette.BorderBrush, TableHeaderBg: Palette.SurfaceSunkenBrush,
        Syntax: Palette.Active.IsDark ? CodeSyntax.Dark() : CodeSyntax.Light())
    {
        // Each block reserves an 11px caret-highlight bar (MarkdownView.Wrap) the notes never use; pull it back so the
        // text lines up with the rest of the pane.
        BodySize = 13, BlockGap = 8, RootMargin = new Thickness(-11, 0, 0, 0), PlainClickLinks = true,
        InlineCodeBg = Palette.SurfaceSunkenBrush,
    };

    // ── Links ──

    private Control LinksSection(WorkItem item)
    {
        var body = new StackPanel { Spacing = 0 };
        foreach (var link in item.Links)
        {
            var url = link.Url;
            var label = new TextBlock
            {
                Text = link.Label.Length > 0 ? link.Label : LinkLabel(link.Url), Foreground = Accent, FontSize = 13,
                VerticalAlignment = VerticalAlignment.Center,
            };
            // A URL keeps its tail visible, the way a path does (CLAUDE.md: never trim the end off).
            var urlText = new TextBlock
            {
                Text = link.Url, Foreground = Muted, FontSize = 11.5, VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.PrefixCharacterEllipsis, Margin = new Thickness(10, 0, 8, 0),
            };
            var remove = IconButton("✕", "Remove link");
            remove.Opacity = 0;
            var target = link;
            remove.Click += (_, _) => Own(() => _host.Mutate(item.Id, i =>
                i.Links.RemoveAll(l => l.Url == target.Url && l.Label == target.Label) > 0));
            var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto") };
            Grid.SetColumn(label, 0);
            Grid.SetColumn(urlText, 1);
            Grid.SetColumn(remove, 2);
            grid.Children.Add(label);
            grid.Children.Add(urlText);
            grid.Children.Add(remove);
            var row = new Border
            {
                Child = grid, Padding = new Thickness(10, 5, 4, 5), CornerRadius = new CornerRadius(6),
                Background = Brushes.Transparent, Cursor = new Cursor(StandardCursorType.Hand), Margin = new Thickness(-10, 0, 0, 0),
            };
            row.PointerEntered += (_, _) => { row.Background = RowHover; remove.Opacity = 1; };
            row.PointerExited += (_, _) => { row.Background = Brushes.Transparent; remove.Opacity = 0; };
            row.PointerReleased += (_, e) =>
            {
                if (e.InitialPressMouseButton != MouseButton.Left) return;
                if (e.Source is Visual v && v.FindAncestorOfType<Button>() is not null) return;
                PlatformServices.UrlOpener.Open(url);
            };
            ToolTip.SetTip(row, link.Url);
            body.Children.Add(row);
        }

        if (_addingLink) body.Children.Add(LinkForm(item));
        var add = TextAction("+ Add", Muted, _ =>
        {
            _addingLink = true;
            Rerender();
        });
        return Section("Links", _addingLink ? new Border() : add, body);
    }

    private Control LinkForm(WorkItem item)
    {
        var urlBox = new TextBox { PlaceholderText = "Paste a URL", FontSize = 12.5 };
        var labelBox = new TextBox { PlaceholderText = "Label (optional)", FontSize = 12.5, Width = 150, Margin = new Thickness(6, 0) };
        var add = SolidButton("Add");
        void Commit()
        {
            var url = urlBox.Text?.Trim() ?? "";
            if (url.Length == 0) { urlBox.Focus(); return; }
            var label = labelBox.Text?.Trim() ?? "";
            _addingLink = false;
            Own(() => _host.Mutate(item.Id, i => i.Links.Add(new WorkLink { Label = label, Url = url })));
            Rerender();
        }
        void Cancel() { _addingLink = false; Rerender(); }
        add.Click += (_, _) => Commit();
        urlBox.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter) { Commit(); e.Handled = true; }
            else if (e.Key == Key.Escape) { Cancel(); e.Handled = true; }
        };
        labelBox.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter) { Commit(); e.Handled = true; }
            else if (e.Key == Key.Escape) { Cancel(); e.Handled = true; }
        };
        var form = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto"), Margin = new Thickness(0, 6, 0, 0) };
        Grid.SetColumn(urlBox, 0);
        Grid.SetColumn(labelBox, 1);
        Grid.SetColumn(add, 2);
        form.Children.Add(urlBox);
        form.Children.Add(labelBox);
        form.Children.Add(add);
        Dispatcher.UIThread.Post(() => urlBox.Focus(), DispatcherPriority.Input);
        return form;
    }

    // A label for a bare URL: its host plus the last path segment, so "github.com · 412".
    private static string LinkLabel(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var u)) return url;
        var last = u.Segments.LastOrDefault()?.Trim('/') ?? "";
        return last.Length > 0 ? $"{u.Host} · {Uri.UnescapeDataString(last)}" : u.Host;
    }

    // ── Activity ──

    private Control ActivitySection(WorkItem item)
    {
        _logBox = new TextBox { PlaceholderText = "Add a note to the activity…", FontSize = 12.5 };
        _logBox.KeyDown += (_, e) =>
        {
            if (e.Key != Key.Enter) return;
            e.Handled = true;
            var text = _logBox.Text?.Trim() ?? "";
            if (text.Length == 0) return;
            if (Own(() => _host.AddNote(item.Id, text)) is not null) _logBox.Text = "";
            Dispatcher.UIThread.Post(() => _logBox?.Focus(), DispatcherPriority.Input);
        };

        var entries = new StackPanel { Spacing = 0, Margin = new Thickness(0, 12, 0, 0) };
        var newestFirst = Enumerable.Reverse(item.Log).ToList();
        var shown = _showAllActivity ? newestFirst : newestFirst.Take(ActivityPreview).ToList();
        foreach (var e in shown) entries.Children.Add(ActivityRow(e));

        if (newestFirst.Count > shown.Count)
            entries.Children.Add(TextAction($"Show all {newestFirst.Count}", Muted, _ => { _showAllActivity = true; Rerender(); }, new Thickness(0, 6, 0, 0)));
        else if (_showAllActivity && newestFirst.Count > ActivityPreview)
            entries.Children.Add(TextAction("Show less", Muted, _ => { _showAllActivity = false; Rerender(); }, new Thickness(0, 6, 0, 0)));

        return Section("Activity", new Border(), new StackPanel { Children = { _logBox, entries } });
    }

    // One activity line: your notes in full colour with an accent bullet; Perch's facts small and muted.
    private static Control ActivityRow(WorkLogEntry e)
    {
        bool mine = !e.ByPerch && !e.IsRaw;
        var when = new TextBlock
        {
            Text = e.IsRaw ? "" : When(e.At.LocalDateTime), FontSize = 11, Foreground = Muted, Width = 92,
            VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, mine ? 1 : 0, 0, 0),
        };
        var bullet = new Border
        {
            Width = 5, Height = 5, CornerRadius = new CornerRadius(3), Margin = new Thickness(0, 6, 10, 0),
            VerticalAlignment = VerticalAlignment.Top, Background = mine ? Accent : Brushes.Transparent,
        };
        var text = new TextBlock
        {
            Text = e.Text, TextWrapping = TextWrapping.Wrap,
            FontSize = mine ? 13 : 12, Foreground = mine ? Fg : Muted,
            FontStyle = e.IsRaw ? FontStyle.Italic : FontStyle.Normal,
        };
        var row = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,Auto,*"), Margin = new Thickness(0, 3) };
        Grid.SetColumn(when, 0);
        Grid.SetColumn(bullet, 1);
        Grid.SetColumn(text, 2);
        row.Children.Add(when);
        row.Children.Add(bullet);
        row.Children.Add(text);
        return row;
    }

    // "14:45" today, "Yesterday 09:10", else "Tue 14 Oct".
    private static string When(DateTime local)
    {
        var today = DateTime.Today;
        if (local.Date == today) return local.ToString("HH:mm");
        if (local.Date == today.AddDays(-1)) return "Yesterday " + local.ToString("HH:mm");
        return local.ToString(local.Year == today.Year ? "ddd d MMM" : "d MMM yyyy");
    }

    /// <summary>The log as a pasteable markdown list, oldest first, under the item's name.</summary>
    internal static string LogMarkdown(WorkItem item)
    {
        var sb = new StringBuilder();
        sb.Append("## ").Append(item.Name).Append('\n').Append('\n');
        foreach (var e in item.Log)
        {
            if (e.IsRaw) { sb.Append(e.Text.Trim()).Append('\n'); continue; }
            sb.Append("- ").Append(e.At.LocalDateTime.ToString("yyyy-MM-dd HH:mm")).Append(' ').Append(e.Text).Append('\n');
        }
        return sb.ToString();
    }

    // ── Shared bits ──

    private static Control Section(string caption, Control action, Control body, double top = 30)
    {
        var head = new Grid { Margin = new Thickness(0, top, 0, 8) };
        head.Children.Add(new TextBlock
        {
            Text = caption.ToUpperInvariant(), FontSize = 10.5, FontWeight = FontWeight.SemiBold, LetterSpacing = 0.8,
            Foreground = Muted, VerticalAlignment = VerticalAlignment.Center,
        });
        action.HorizontalAlignment = HorizontalAlignment.Right;
        action.VerticalAlignment = VerticalAlignment.Center;
        head.Children.Add(action);
        return new StackPanel { Children = { head, body } };
    }

    // A text-only button (an action that shouldn't shout).
    private static Button TextAction(string text, IBrush fg, Action<Button> onClick, Thickness? margin = null)
    {
        var b = new Button
        {
            Content = text, Foreground = fg, Background = Brushes.Transparent, BorderThickness = new Thickness(0),
            Padding = new Thickness(0), FontSize = 12, Cursor = new Cursor(StandardCursorType.Hand),
            HorizontalAlignment = HorizontalAlignment.Left, Margin = margin ?? default,
        };
        b.Click += (_, _) => onClick(b);
        return b;
    }

    // A dashed-outline chip for something an item doesn't have yet ("+ Notes", "+ Link").
    private static Control Chip(string text, Action onClick)
    {
        var b = new Button
        {
            Content = text, Foreground = Muted, Background = Brushes.Transparent, BorderBrush = Stroke,
            BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(999), Padding = new Thickness(12, 4),
            FontSize = 12, Cursor = new Cursor(StandardCursorType.Hand),
        };
        b.Click += (_, _) => onClick();
        return b;
    }

    // A transparent row with a hover wash.
    private static Border Hoverable(Control child, Thickness padding)
    {
        var row = new Border { Child = child, Padding = padding, Background = Brushes.Transparent, Cursor = new Cursor(StandardCursorType.Hand) };
        row.PointerEntered += (_, _) => row.Background = RowHover;
        row.PointerExited += (_, _) => row.Background = Brushes.Transparent;
        return row;
    }

    private static Border Swatch(string colour, double size) => new()
    {
        Width = size, Height = size, CornerRadius = new CornerRadius(size / 4),
        Background = Palette.WorkHueBrush(colour), VerticalAlignment = VerticalAlignment.Center,
    };

    private static Button IconButton(string glyph, string tip)
    {
        var b = new Button
        {
            Content = glyph, Foreground = Muted, Background = Brushes.Transparent, BorderThickness = new Thickness(0),
            Padding = new Thickness(8, 2), FontSize = 11, Cursor = new Cursor(StandardCursorType.Hand),
            VerticalAlignment = VerticalAlignment.Center,
        };
        ToolTip.SetTip(b, tip);
        return b;
    }

    private static Button SolidButton(string text) => new()
    {
        Content = text, Foreground = Palette.OnAccentBrush, Background = Accent, BorderThickness = new Thickness(0),
        CornerRadius = new CornerRadius(7), Padding = new Thickness(13, 6), FontSize = 12.5, FontWeight = FontWeight.SemiBold,
        Cursor = new Cursor(StandardCursorType.Hand),
    };

    private static Button OutlineButton(string text, IBrush fg) => new()
    {
        Content = text, Foreground = fg, Background = Brushes.Transparent, BorderBrush = Stroke, BorderThickness = new Thickness(1),
        CornerRadius = new CornerRadius(7), Padding = new Thickness(12, 6), FontSize = 12.5, Cursor = new Cursor(StandardCursorType.Hand),
    };

    private static MenuItem Item(string header, Action onClick)
    {
        var mi = new MenuItem { Header = header };
        mi.Click += (_, _) => onClick();
        return mi;
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            if (_editingNotes || _addingLink || _renaming) { ResetDisclosure(); Rerender(); }
            else Close();
            e.Handled = true;
        }
        base.OnKeyDown(e);
    }
}
