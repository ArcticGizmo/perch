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
    /// <summary>The name field, all selected (a freshly created item named after its session).</summary>
    Rename,
    /// <summary>The log entry box ("Log a note…").</summary>
    Log,
    /// <summary>The "new work item" field on the left.</summary>
    Compose,
}

/// <summary>
/// The work items window (docs/work-items-plan.md W3), opened from the overlay's item headers and session menus and
/// the tray. Left: a field to name a new item, over the list grouped Active / Paused (and Done, behind a toggle).
/// Right: the selected item's home: its name, colour and status; its links; its notes (markdown, edited in place);
/// its sessions with how each joined, live ones opening their window or terminal and ended ones resumable; and its
/// log, newest first, with a box to add a line and a "Copy as Markdown".
///
/// <para>Every change goes through <see cref="WorkItemsHost"/> (a read-modify-write of the item's one file), which
/// raises <c>Changed</c>; the detail pane is rebuilt only when the selected item's content actually moved, so a reload
/// of our own write, or a scan, never wipes a field the user is typing in. Styled off <see cref="TodoWindow"/>.</para>
/// </summary>
internal sealed class WorkItemsWindow : Window
{
    private static readonly IBrush Bg = Palette.OverlaySurfaceBrush;
    private static readonly IBrush Bg2 = Palette.ButtonBgBrush;
    private static readonly IBrush Stroke = Palette.BorderBrush;
    private static readonly IBrush Fg = Palette.FgBrush;
    private static readonly IBrush Muted = Palette.MutedBrush;
    private static readonly IBrush Accent = Palette.AccentBrush;
    private static readonly IBrush RowHover = new SolidColorBrush(Color.FromArgb(28, 255, 255, 255));
    private static Color AccentColor => Palette.Active.Accent.ToColor();
    private static IBrush SelBrush => new SolidColorBrush(Color.FromArgb(30, AccentColor.R, AccentColor.G, AccentColor.B));

    private readonly WorkItemsHost _host;
    private readonly Func<IReadOnlyList<ClaudeSession>> _roster;

    private readonly TextBox _composer;
    private readonly CheckBox _showDone;
    private readonly StackPanel _list = new();
    private readonly ContentControl _detailHost = new();
    private readonly ScrollViewer _detailScroll;

    private string? _selectedId;
    private string _detailSignature = "";
    private string _sessionsSignature = "";
    private bool _editingNotes;
    private StackPanel? _sessionsPanel;
    private TextBox? _nameBox, _logBox;

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

    public WorkItemsWindow(WorkItemsHost host, Func<IReadOnlyList<ClaudeSession>> roster)
    {
        _host = host;
        _roster = roster;

        Title = "Work items";
        WindowDecorations = WindowDecorations.None;
        Background = Brushes.Transparent;
        TransparencyLevelHint = [WindowTransparencyLevel.Transparent];
        CanResize = false;
        Width = 900;
        Height = 680;
        ShowInTaskbar = true;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;

        // ── Header (draggable) ──
        var heading = new TextBlock { Text = "Work items", Foreground = Fg, FontWeight = FontWeight.Bold, FontSize = 16 };
        var subhead = new TextBlock
        {
            Text = "Name a piece of work, then put the sessions doing it inside, however you run them.",
            Foreground = Muted, FontSize = 12, Margin = new Thickness(0, 2, 0, 0),
        };
        var closeGlyph = new Button
        {
            Content = "✕", Foreground = Muted, Background = Brushes.Transparent, BorderThickness = new Thickness(0),
            Padding = new Thickness(4, 0), FontSize = 14, Cursor = new Cursor(StandardCursorType.Hand),
            HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top,
        };
        closeGlyph.Click += (_, _) => Close();
        var headerBorder = new Border
        {
            Background = Bg, BorderBrush = Stroke, BorderThickness = new Thickness(0, 0, 0, 1),
            Child = new Grid
            {
                Margin = new Thickness(20, 16, 18, 14),
                Children = { new StackPanel { Children = { heading, subhead } }, closeGlyph },
            },
        };
        headerBorder.PointerPressed += (_, e) =>
        {
            if (e.Source is Button) return;
            if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) BeginMoveDrag(e);
        };

        // ── Left: composer + show-done + list ──
        _composer = new TextBox { PlaceholderText = "Name a new work item…", FontSize = 13 };
        _composer.KeyDown += (_, e) => { if (e.Key == Key.Enter) { CommitCreate(); e.Handled = true; } };
        var addBtn = SolidButton("New work item");
        addBtn.HorizontalAlignment = HorizontalAlignment.Stretch;
        addBtn.HorizontalContentAlignment = HorizontalAlignment.Center;
        addBtn.Click += (_, _) => CommitCreate();
        var composerBorder = new Border
        {
            Background = Bg2, BorderBrush = Stroke, BorderThickness = new Thickness(0, 0, 0, 1),
            Child = new StackPanel { Margin = new Thickness(14, 13), Spacing = 8, Children = { _composer, addBtn } },
        };

        _showDone = new CheckBox { Content = "Show done", Foreground = Muted, FontSize = 12, Margin = new Thickness(14, 8, 0, 8) };
        _showDone.IsCheckedChanged += (_, _) => RefreshList();
        var showBorder = new Border { BorderBrush = Stroke, BorderThickness = new Thickness(0, 0, 0, 1), Child = _showDone };

        var listScroller = new ScrollViewer
        {
            Content = _list, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Padding = new Thickness(0, 0, 0, 8),
        };
        var leftGrid = new Grid { RowDefinitions = new RowDefinitions("Auto,Auto,*") };
        Grid.SetRow(composerBorder, 0);
        Grid.SetRow(showBorder, 1);
        Grid.SetRow(listScroller, 2);
        leftGrid.Children.Add(composerBorder);
        leftGrid.Children.Add(showBorder);
        leftGrid.Children.Add(listScroller);

        // ── Right: the selected item ──
        _detailHost.Padding = new Thickness(24, 20, 24, 24);
        _detailScroll = new ScrollViewer
        {
            Content = _detailHost, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        };

        var body = new Grid { ColumnDefinitions = new ColumnDefinitions("290,1,*") };
        var divider = new Border { Background = Stroke };
        Grid.SetColumn(leftGrid, 0);
        Grid.SetColumn(divider, 1);
        Grid.SetColumn(_detailScroll, 2);
        body.Children.Add(leftGrid);
        body.Children.Add(divider);
        body.Children.Add(_detailScroll);

        var root = new DockPanel();
        DockPanel.SetDock(headerBorder, Dock.Top);
        root.Children.Add(headerBorder);
        root.Children.Add(body);

        Content = new Border
        {
            Background = Bg, CornerRadius = new CornerRadius(12), BorderBrush = Stroke, BorderThickness = new Thickness(1.5),
            Child = root, ClipToBounds = true,
        };

        _host.Changed += OnHostChanged;
        Closed += (_, _) => _host.Changed -= OnHostChanged;
        Refresh();
    }

    /// <summary>Points the reused window at an item (or just the list) and puts the caret where the caller asked.</summary>
    public void Retarget(string? itemId, WorkFocus focus = WorkFocus.None)
    {
        if (itemId is not null && _host.Find(itemId) is { } item)
        {
            if (item.Status == WorkItemStatus.Done) _showDone.IsChecked = true;
            _selectedId = itemId;
            _editingNotes = false;
            _detailSignature = "";
        }
        Refresh();
        Dispatcher.UIThread.Post(() =>
        {
            switch (focus)
            {
                case WorkFocus.Rename: _nameBox?.Focus(); _nameBox?.SelectAll(); break;
                case WorkFocus.Log: _logBox?.Focus(); break;
                case WorkFocus.Compose: _composer.Focus(); break;
            }
        }, DispatcherPriority.Input);
    }

    /// <summary>The roster moved (a scan): redraw the sessions block if any member's state changed.</summary>
    public void RosterChanged()
    {
        if (_selectedId is null || _host.Find(_selectedId) is not { } item || _sessionsPanel is null) return;
        var sig = SessionsSignature(item);
        if (sig == _sessionsSignature) return;
        FillSessions(_sessionsPanel, item);
    }

    private void OnHostChanged() => Refresh();

    private void Refresh()
    {
        RefreshList();
        RenderDetail();
    }

    // ── The list ──

    private void RefreshList()
    {
        _list.Children.Clear();
        var items = _host.Items;
        AddGroup("Active", items.Where(i => i.Status == WorkItemStatus.Active));
        AddGroup("Paused", items.Where(i => i.Status == WorkItemStatus.Paused));
        if (_showDone.IsChecked == true)
            AddGroup("Done", items.Where(i => i.Status == WorkItemStatus.Done).Reverse());

        if (_list.Children.Count == 0)
            _list.Children.Add(new TextBlock
            {
                Text = items.Count == 0
                    ? "No work items yet. Name one above, or right-click a session in the overlay and choose Work items."
                    : "Nothing active. Tick Show done to see finished items.",
                Foreground = Muted, FontSize = 12, Margin = new Thickness(16, 12), TextWrapping = TextWrapping.Wrap,
            });

        if (_host.Store.Unreadable.Count > 0)
            _list.Children.Add(new TextBlock
            {
                Text = $"{_host.Store.Unreadable.Count} file(s) in the work folder couldn't be read and are left alone: "
                       + string.Join(", ", _host.Store.Unreadable.Select(Path.GetFileName)),
                Foreground = new SolidColorBrush(Palette.Danger), FontSize = 11.5, Margin = new Thickness(16, 12),
                TextWrapping = TextWrapping.Wrap,
            });

        if (_selectedId is { } sel && _host.Find(sel) is null) _selectedId = null;
    }

    private void AddGroup(string name, IEnumerable<WorkItem> items)
    {
        var list = items.ToList();
        if (list.Count == 0) return;
        _list.Children.Add(new TextBlock
        {
            FontSize = 10.5, FontWeight = FontWeight.SemiBold, Foreground = Muted, LetterSpacing = 0.6,
            Margin = new Thickness(15, 13, 15, 5), Text = $"{name.ToUpperInvariant()}   {list.Count}",
        });
        foreach (var i in list) _list.Children.Add(BuildListRow(i));
    }

    private Control BuildListRow(WorkItem item)
    {
        var roster = _roster();
        int live = item.Members.Count(m => roster.Any(s => s.SessionId == m.SessionId && !s.IsDormant));
        var swatch = Swatch(item.Colour, 9);
        var title = new TextBlock
        {
            Text = item.Name, FontSize = 13, Foreground = item.Status == WorkItemStatus.Done ? Muted : Fg,
            TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(9, 0, 8, 0),
        };
        var meta = new TextBlock
        {
            FontSize = 11.5, Foreground = Muted, VerticalAlignment = VerticalAlignment.Center,
            Text = live > 0 ? $"{live} live" : item.Members.Count > 0 ? $"{item.Members.Count}" : "",
        };
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto") };
        Grid.SetColumn(swatch, 0);
        Grid.SetColumn(title, 1);
        Grid.SetColumn(meta, 2);
        grid.Children.Add(swatch);
        grid.Children.Add(title);
        grid.Children.Add(meta);

        bool selected = item.Id == _selectedId;
        var row = new Border
        {
            Child = grid, Padding = new Thickness(13, 8, 15, 8), Cursor = new Cursor(StandardCursorType.Hand),
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
        _selectedId = id;
        _editingNotes = false;
        _detailSignature = "";
        Refresh();
        _detailScroll.Offset = default;
    }

    private void CommitCreate()
    {
        var name = _composer.Text?.Trim() ?? "";
        if (name.Length == 0) { _composer.Focus(); return; }
        if (_host.Create(name) is not { } item) return;
        _composer.Text = "";
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
        // Rebuild only when the item's content moved (our own write's echo, a scan, or a no-op reload must not
        // wipe a field mid-typing), or when switching notes between read and edit.
        var sig = WorkItemFile.Serialize(item) + "|" + _editingNotes + "|" + _showDone.IsChecked;
        if (sig == _detailSignature) { RosterChanged(); return; }
        _detailSignature = sig;
        _detailHost.Content = Detail(item);
    }

    private Control EmptyDetail() => new StackPanel
    {
        VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center, Spacing = 6,
        Margin = new Thickness(0, 180, 0, 0),
        Children =
        {
            new TextBlock { Text = "Select a work item", Foreground = Muted, FontSize = 14, FontWeight = FontWeight.SemiBold, HorizontalAlignment = HorizontalAlignment.Center },
            new TextBlock { Text = "Its links, notes, sessions and log show here.", Foreground = Muted, Opacity = 0.75, FontSize = 12, HorizontalAlignment = HorizontalAlignment.Center },
        },
    };

    private Control Detail(WorkItem item)
    {
        var stack = new StackPanel { Spacing = 0 };

        // Title row: colour swatch (a picker), the name (edited in place), the status.
        var swatchBtn = new Button
        {
            Background = Brushes.Transparent, BorderThickness = new Thickness(0), Padding = new Thickness(0, 0, 10, 0),
            Cursor = new Cursor(StandardCursorType.Hand), VerticalAlignment = VerticalAlignment.Center,
            Content = Swatch(item.Colour, 16),
            Flyout = ColourFlyout(item),
        };
        ToolTip.SetTip(swatchBtn, "Colour");

        _nameBox = new TextBox
        {
            Text = item.Name, FontSize = 20, FontWeight = FontWeight.SemiBold, Background = Brushes.Transparent,
            BorderThickness = new Thickness(0, 0, 0, 1), Padding = new Thickness(0, 2), MinWidth = 120,
            VerticalAlignment = VerticalAlignment.Center,
        };
        void CommitName()
        {
            var n = _nameBox!.Text?.ReplaceLineEndings(" ").Trim() ?? "";
            if (n.Length == 0) { _nameBox.Text = item.Name; return; }
            if (n != item.Name) _host.Mutate(item.Id, i => { i.Name = n; i.AddLog($"Renamed to \"{n}\"", byPerch: true); });
        }
        _nameBox.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter) { CommitName(); e.Handled = true; }
            else if (e.Key == Key.Escape) { _nameBox.Text = item.Name; e.Handled = true; }
        };
        _nameBox.LostFocus += (_, _) => CommitName();

        var titleRow = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto") };
        var status = StatusPicker(item);
        status.Margin = new Thickness(12, 0, 0, 0);
        Grid.SetColumn(swatchBtn, 0);
        Grid.SetColumn(_nameBox, 1);
        Grid.SetColumn(status, 2);
        titleRow.Children.Add(swatchBtn);
        titleRow.Children.Add(_nameBox);
        titleRow.Children.Add(status);
        stack.Children.Add(titleRow);

        int live = LiveMembers(item).Count();
        stack.Children.Add(new TextBlock
        {
            Text = $"Started {item.CreatedUtc.ToLocalTime():d MMM yyyy} · {Plural(item.Members.Count, "session")}"
                   + (live > 0 ? $", {live} running" : ""),
            Foreground = Muted, FontSize = 12, Margin = new Thickness(26, 6, 0, 0),
        });

        // Actions.
        var newSession = SolidButton("+ New session");
        newSession.IsEnabled = item.Status != WorkItemStatus.Done;
        newSession.Click += (_, _) => NewSessionRequested?.Invoke(item.Id);
        var openFile = OutlineButton("Open file", Fg);
        ToolTip.SetTip(openFile, item.FilePath);
        openFile.Click += (_, _) => { if (item.FilePath is { } p) PlatformServices.FileRevealer.OpenInEditor(p); };
        var reveal = OutlineButton("Show in folder", Fg);
        reveal.Click += (_, _) => { if (item.FilePath is { } p) PlatformServices.FileRevealer.RevealInFileManager(p); };
        var delete = OutlineButton("Delete…", new SolidColorBrush(Palette.Danger));
        delete.Click += async (_, _) =>
        {
            bool ok = await ConfirmDialog.ShowAsync(this, "Delete this work item?",
                $"\"{item.Name}\" and its notes, links and log are deleted. Its sessions aren't touched. "
                + "To finish an item and keep its history, mark it Done instead.", "Delete", "Cancel", delete);
            if (ok) { _host.Delete(item.Id); _selectedId = null; Refresh(); }
        };
        var actions = new Grid { Margin = new Thickness(0, 16, 0, 0) };
        actions.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { newSession, openFile, reveal } });
        delete.HorizontalAlignment = HorizontalAlignment.Right;
        actions.Children.Add(delete);
        stack.Children.Add(actions);

        stack.Children.Add(LinksSection(item));
        stack.Children.Add(NotesSection(item));
        stack.Children.Add(SessionsSection(item));
        stack.Children.Add(LogSection(item));
        return stack;
    }

    // Active / Paused / Done as a small segmented control. Only the user moves an item between them (D3).
    private Control StatusPicker(WorkItem item)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 0, VerticalAlignment = VerticalAlignment.Center };
        foreach (var st in new[] { WorkItemStatus.Active, WorkItemStatus.Paused, WorkItemStatus.Done })
        {
            bool on = item.Status == st;
            var b = new Button
            {
                Content = st.ToString(), FontSize = 12, Padding = new Thickness(11, 5),
                Background = on ? new SolidColorBrush(AccentColor) { Opacity = 0.18 } : Brushes.Transparent,
                Foreground = on ? Accent : Muted, BorderBrush = on ? Accent : Stroke, BorderThickness = new Thickness(1),
                CornerRadius = st switch
                {
                    WorkItemStatus.Active => new CornerRadius(7, 0, 0, 7),
                    WorkItemStatus.Done => new CornerRadius(0, 7, 7, 0),
                    _ => new CornerRadius(0),
                },
                Margin = new Thickness(st == WorkItemStatus.Active ? 0 : -1, 0, 0, 0),
                Cursor = new Cursor(StandardCursorType.Hand),
            };
            b.Click += (_, _) => _host.SetStatus(item.Id, st);
            row.Children.Add(b);
        }
        return row;
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
                if (!on) _host.Mutate(item.Id, i => i.Colour = name);
            };
            grid.Children.Add(b);
        }
        flyout.Content = grid;
        return flyout;
    }

    // ── Links ──

    private Control LinksSection(WorkItem item)
    {
        var body = new StackPanel { Spacing = 2 };
        foreach (var link in item.Links)
        {
            var label = new Button
            {
                Content = link.Label.Length > 0 ? link.Label : LinkLabel(link.Url), Foreground = Accent,
                Background = Brushes.Transparent, BorderThickness = new Thickness(0), Padding = new Thickness(0, 3),
                FontSize = 13, Cursor = new Cursor(StandardCursorType.Hand), VerticalAlignment = VerticalAlignment.Center,
            };
            var url = link.Url;
            label.Click += (_, _) => PlatformServices.UrlOpener.Open(url);
            // A URL keeps its tail visible, the way a path does (CLAUDE.md: never trim the end off).
            var urlText = new TextBlock
            {
                Text = link.Url, Foreground = Muted, FontSize = 11.5, VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.PrefixCharacterEllipsis, Margin = new Thickness(10, 0, 8, 0),
            };
            ToolTip.SetTip(urlText, link.Url);
            var remove = IconButton("✕", "Remove link");
            var target = link;
            remove.Click += (_, _) => _host.Mutate(item.Id, i =>
                i.Links.RemoveAll(l => l.Url == target.Url && l.Label == target.Label) > 0);
            var row = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto") };
            Grid.SetColumn(label, 0);
            Grid.SetColumn(urlText, 1);
            Grid.SetColumn(remove, 2);
            row.Children.Add(label);
            row.Children.Add(urlText);
            row.Children.Add(remove);
            body.Children.Add(row);
        }
        if (item.Links.Count == 0)
            body.Children.Add(Hint("Design docs, PRs, tickets: anything with a URL."));

        // The add form, folded behind "+ Add link".
        var labelBox = new TextBox { PlaceholderText = "Label (optional)", FontSize = 12.5, Width = 150 };
        var urlBox = new TextBox { PlaceholderText = "https://…", FontSize = 12.5 };
        var add = SolidButton("Add");
        var form = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"), Margin = new Thickness(0, 6, 0, 0), IsVisible = false };
        Grid.SetColumn(labelBox, 0);
        Grid.SetColumn(urlBox, 1);
        Grid.SetColumn(add, 2);
        urlBox.Margin = new Thickness(6, 0);
        form.Children.Add(labelBox);
        form.Children.Add(urlBox);
        form.Children.Add(add);
        void Commit()
        {
            var url = urlBox.Text?.Trim() ?? "";
            if (url.Length == 0) { urlBox.Focus(); return; }
            var label = labelBox.Text?.Trim() ?? "";
            _host.Mutate(item.Id, i => i.Links.Add(new WorkLink { Label = label, Url = url }));
        }
        add.Click += (_, _) => Commit();
        urlBox.KeyDown += (_, e) => { if (e.Key == Key.Enter) { Commit(); e.Handled = true; } };
        labelBox.KeyDown += (_, e) => { if (e.Key == Key.Enter) { urlBox.Focus(); e.Handled = true; } };
        body.Children.Add(form);

        var toggle = CaptionLink("+ Add link", () => { form.IsVisible = true; urlBox.Focus(); });
        return Section("Links", toggle, body);
    }

    // A label for a bare URL: its host plus the last path segment, so "github.com · 412".
    private static string LinkLabel(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var u)) return url;
        var last = u.Segments.LastOrDefault()?.Trim('/') ?? "";
        return last.Length > 0 ? $"{u.Host} · {Uri.UnescapeDataString(last)}" : u.Host;
    }

    // ── Notes ──

    private Control NotesSection(WorkItem item)
    {
        Control body;
        Control action;
        if (_editingNotes)
        {
            var box = new TextBox
            {
                Text = item.Notes, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, FontSize = 13, MinHeight = 140,
                PlaceholderText = "A goal, a plan, a scratchpad. Markdown.", VerticalContentAlignment = VerticalAlignment.Top,
            };
            var save = SolidButton("Save");
            save.Click += (_, _) =>
            {
                _editingNotes = false;
                var text = box.Text ?? "";
                if (_host.Mutate(item.Id, i => { if (i.Notes == text.Trim()) return false; i.Notes = text; return true; }) is null)
                    RenderDetail();
                else { _detailSignature = ""; RenderDetail(); }
            };
            var cancel = OutlineButton("Cancel", Muted);
            cancel.Click += (_, _) => { _editingNotes = false; _detailSignature = ""; RenderDetail(); };
            body = new StackPanel
            {
                Spacing = 8,
                Children = { box, new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right, Children = { cancel, save } } },
            };
            action = new Border();
            Dispatcher.UIThread.Post(() => box.Focus(), DispatcherPriority.Input);
        }
        else
        {
            body = item.Notes.Length > 0
                ? MarkdownView.Build(item.Notes, NotesStyle())
                : Hint("No notes yet.");
            action = CaptionLink("Edit", () => { _editingNotes = true; _detailSignature = ""; RenderDetail(); });
        }
        return Section("Notes", action, body);
    }

    private static MarkdownStyle NotesStyle() => new(
        Fg: Palette.FgBrush, Muted: Palette.MutedBrush, Title: Palette.TitleBrush, Link: Palette.AccentBrush,
        CodeFg: Palette.FgBrush, CodeBg: Palette.SurfaceSunkenBrush, QuoteBar: Palette.BorderBrush,
        Rule: Palette.SeparatorBrush, TableBorder: Palette.BorderBrush, TableHeaderBg: Palette.SurfaceSunkenBrush,
        Syntax: Palette.Active.IsDark ? CodeSyntax.Dark() : CodeSyntax.Light())
    {
        BodySize = 13, BlockGap = 8, RootMargin = new Thickness(0), PlainClickLinks = true,
        InlineCodeBg = Palette.SurfaceSunkenBrush,
    };

    // ── Sessions ──

    private Control SessionsSection(WorkItem item)
    {
        _sessionsPanel = new StackPanel { Spacing = 1 };
        FillSessions(_sessionsPanel, item);
        var add = new Button
        {
            Content = "Add a running session ▾", Foreground = Accent, Background = Brushes.Transparent,
            BorderThickness = new Thickness(0), Padding = new Thickness(0), FontSize = 11.5,
            Cursor = new Cursor(StandardCursorType.Hand),
        };
        add.Click += (_, _) => ShowAddSessionMenu(add, item.Id);
        return Section("Sessions", add, _sessionsPanel);
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
            panel.Children.Add(Hint("No sessions yet. Start one here, tick this item in a session's right-click menu, "
                + $"or start claude in any terminal with PERCH_WORK={item.Id} (perch work help)."));
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
        var dotColor = !running ? Palette.Idle : s!.Status switch
        {
            SessionStatus.Running => Palette.Green,
            SessionStatus.NeedsAttention => Palette.Orange,
            SessionStatus.AwaitingInput => Palette.Yellow,
            SessionStatus.ApiError => Palette.Red,
            _ => Palette.Idle,
        };
        var dot = new Border
        {
            Width = 8, Height = 8, CornerRadius = new CornerRadius(4), Background = new SolidColorBrush(dotColor),
            VerticalAlignment = VerticalAlignment.Center, Opacity = running ? 1 : 0.6,
        };

        string title = s?.DisplayName ?? m.Title ?? $"Session {m.SessionId[..Math.Min(8, m.SessionId.Length)]}";
        var name = new TextBlock
        {
            Text = title, FontSize = 13, Foreground = running ? Fg : Muted, TextTrimming = TextTrimming.CharacterEllipsis,
        };
        var cwd = s?.Cwd ?? m.Cwd;
        var folder = new TextBlock
        {
            Text = cwd ?? "", FontSize = 11, Foreground = Muted, Opacity = 0.85, IsVisible = !string.IsNullOrEmpty(cwd),
            TextTrimming = TextTrimming.PrefixCharacterEllipsis,   // keep the folder's own name, lose the drive
        };
        var text = new StackPanel { Children = { name, folder }, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 8, 0) };

        var via = new Border
        {
            BorderBrush = Stroke, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(4),
            Padding = new Thickness(5, 0), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0),
            Child = new TextBlock { Text = m.Via.ToString().ToLowerInvariant(), FontSize = 10.5, Foreground = Muted },
        };
        ToolTip.SetTip(via, m.Via switch
        {
            WorkJoin.Launched => "Started inside this item",
            WorkJoin.Terminal => "Joined from a terminal (PERCH_WORK)",
            _ => "Added by you",
        } + $" · {m.AddedUtc.ToLocalTime():d MMM HH:mm}");

        var meta = new TextBlock
        {
            FontSize = 11.5, Foreground = Muted, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0),
            Text = s is null ? "ended" : s.IsDormant ? "not running" : s.Status switch
            {
                SessionStatus.Running => "working",
                SessionStatus.NeedsAttention => "done",
                SessionStatus.AwaitingInput => "needs you",
                SessionStatus.ApiError => "api error",
                _ => "idle",
            },
        };
        var remove = IconButton("✕", "Take out of this item");
        remove.Click += (_, _) => _host.RemoveSession(item.Id, m.SessionId);

        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto,Auto,Auto") };
        Grid.SetColumn(dot, 0);
        Grid.SetColumn(text, 1);
        Grid.SetColumn(via, 2);
        Grid.SetColumn(meta, 3);
        Grid.SetColumn(remove, 4);
        grid.Children.Add(dot);
        grid.Children.Add(text);
        grid.Children.Add(via);
        grid.Children.Add(meta);
        grid.Children.Add(remove);

        var row = new Border
        {
            Child = grid, Padding = new Thickness(8, 6), CornerRadius = new CornerRadius(6),
            Background = Brushes.Transparent, Cursor = new Cursor(StandardCursorType.Hand),
        };
        row.PointerEntered += (_, _) => row.Background = RowHover;
        row.PointerExited += (_, _) => row.Background = Brushes.Transparent;
        row.PointerReleased += (_, e) =>
        {
            if (e.InitialPressMouseButton != MouseButton.Left) return;
            if (e.Source is Visual v && v.FindAncestorOfType<Button>() is not null) return;
            if (s is not null) FocusSessionRequested?.Invoke(s);
            else ShowEndedMenu(row, m);
        };
        ToolTip.SetTip(row, s is null ? "Click to resume" : "Click to open");
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
                () => _host.AddSession(itemId, s.SessionId, s.DisplayName, s.Cwd, WorkJoin.Manual)));
        if (items.Count == 0)
            items.Add(new MenuItem { Header = "Every running session is already in it", IsEnabled = false });
        new MenuFlyout { ItemsSource = items, Placement = PlacementMode.BottomEdgeAlignedLeft }.ShowAt(anchor);
    }

    // ── Log ──

    private Control LogSection(WorkItem item)
    {
        _logBox = new TextBox { PlaceholderText = "Write a log entry and press Enter…", FontSize = 13 };
        _logBox.KeyDown += (_, e) =>
        {
            if (e.Key != Key.Enter) return;
            e.Handled = true;
            var text = _logBox.Text?.Trim() ?? "";
            if (text.Length == 0) return;
            if (_host.AddNote(item.Id, text) is not null) _logBox.Text = "";
            Dispatcher.UIThread.Post(() => _logBox?.Focus(), DispatcherPriority.Input);
        };

        var entries = new StackPanel { Spacing = 0, Margin = new Thickness(0, 10, 0, 0) };
        string? lastDay = null;
        var today = DateTime.Today;
        foreach (var e in Enumerable.Reverse(item.Log))
        {
            var local = e.IsRaw ? (DateTime?)null : e.At.LocalDateTime;
            var day = local is { } d ? (d.Date == today ? "Today" : d.Date == today.AddDays(-1) ? "Yesterday" : d.ToString("dddd d MMM")) : "";
            if (day.Length > 0 && day != lastDay)
            {
                entries.Children.Add(new TextBlock
                {
                    Text = day.ToUpperInvariant(), FontSize = 10, FontWeight = FontWeight.SemiBold, LetterSpacing = 0.6,
                    Foreground = Muted, Margin = new Thickness(0, lastDay is null ? 0 : 10, 0, 4),
                });
                lastDay = day;
            }
            var time = new TextBlock
            {
                Text = local?.ToString("HH:mm") ?? "", FontSize = 11.5, Foreground = Muted, Width = 44,
                FontFeatures = FontFeatureCollection.Parse("tnum"),
            };
            var bullet = new Border
            {
                Width = 6, Height = 6, CornerRadius = new CornerRadius(3), Margin = new Thickness(0, 6, 9, 0),
                VerticalAlignment = VerticalAlignment.Top,
                Background = e.ByPerch || e.IsRaw ? Muted : Accent,
            };
            var text = new TextBlock
            {
                Text = e.Text, FontSize = 12.5, TextWrapping = TextWrapping.Wrap,
                Foreground = e.ByPerch || e.IsRaw ? Muted : Fg, FontStyle = e.IsRaw ? FontStyle.Italic : FontStyle.Normal,
            };
            var row = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,Auto,*"), Margin = new Thickness(0, 2) };
            Grid.SetColumn(time, 0);
            Grid.SetColumn(bullet, 1);
            Grid.SetColumn(text, 2);
            row.Children.Add(time);
            row.Children.Add(bullet);
            row.Children.Add(text);
            entries.Children.Add(row);
        }
        if (item.Log.Count == 0) entries.Children.Add(Hint("Nothing logged yet. Perch notes when sessions join and end; you write what it meant."));

        var copy = CaptionLink("Copy as Markdown", () =>
            TopLevel.GetTopLevel(this)?.Clipboard?.SetTextAsync(LogMarkdown(item)));
        return Section("Log", copy, new StackPanel { Children = { _logBox, entries } });
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

    private static Control Section(string caption, Control action, Control body)
    {
        var head = new Grid { Margin = new Thickness(0, 26, 0, 8) };
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

    private static Button CaptionLink(string text, Action onClick)
    {
        var b = new Button
        {
            Content = text, Foreground = Accent, Background = Brushes.Transparent, BorderThickness = new Thickness(0),
            Padding = new Thickness(0), FontSize = 11.5, Cursor = new Cursor(StandardCursorType.Hand),
        };
        b.Click += (_, _) => onClick();
        return b;
    }

    private static TextBlock Hint(string text) => new()
    {
        Text = text, FontSize = 12.5, Foreground = Muted, FontStyle = FontStyle.Italic, TextWrapping = TextWrapping.Wrap,
        Opacity = 0.85,
    };

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
            Padding = new Thickness(6, 2), FontSize = 11, Cursor = new Cursor(StandardCursorType.Hand),
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

    private static string Plural(int n, string noun) => n == 1 ? $"1 {noun}" : $"{n} {noun}s";

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            if (_editingNotes) { _editingNotes = false; _detailSignature = ""; RenderDetail(); }
            else Close();
            e.Handled = true;
        }
        base.OnKeyDown(e);
    }
}
