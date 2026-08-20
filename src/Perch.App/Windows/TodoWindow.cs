using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.VisualTree;
using Perch.Avalonia.Theming;
using Perch.Data;

namespace Perch.Avalonia.Windows;

/// <summary>
/// The user's todo / reminder list, in its own window (opened from the tray menu, a session's right-click
/// menu, or the overlay strip). Two panes: on the left a title-first composer over the list grouped by
/// urgency (Overdue / Upcoming / No date, plus a folded Completed group); on the right a reading pane that
/// opens whatever row you select, giving the notes room to breathe. The reading pane's "Edit" turns it into
/// a form in place; the composer stays purely for adding.
///
/// <para>Reused via <c>WindowHost.ShowOrFocus</c> (<see cref="Retarget"/> reloads). Every mutation goes
/// through the shared <see cref="TodoStore"/>, then saves and calls <c>onChanged</c> so the overlay strip
/// and the reminder poller pick it up at once. IO is a small local JSON file, so it runs inline. Styled off
/// <see cref="DaemonListWindow"/> so the popups read as one app.</para>
/// </summary>
internal sealed class TodoWindow : Window
{
    private static readonly IBrush Bg       = Palette.OverlaySurfaceBrush;
    private static readonly IBrush Bg2      = Palette.ButtonBgBrush;
    private static readonly IBrush Stroke   = Palette.BorderBrush;
    private static readonly IBrush Fg       = Palette.FgBrush;
    private static readonly IBrush Muted    = Palette.MutedBrush;
    private static readonly IBrush Accent   = Palette.AccentBrush;
    private static readonly IBrush RowHover = new SolidColorBrush(Color.FromArgb(28, 255, 255, 255));
    private static Color OverdueColor => Palette.Red;
    private static Color AccentColor => Palette.Active.Accent.ToColor();
    private static IBrush SelBrush => new SolidColorBrush(Color.FromArgb(30, AccentColor.R, AccentColor.G, AccentColor.B));

    private readonly TodoStore _store;
    private readonly Action _onChanged;

    // Composer (add-only).
    private readonly TextBox _titleBox;
    private readonly DueField _composerDue;
    private readonly CheckBox _showCompleted;
    private readonly StackPanel _list = new();
    private readonly ContentControl _detailHost = new();

    // The selected todo (shown in the reading pane), and whether that pane is in edit mode.
    private string? _selectedId;
    private bool _detailEditing;

    public TodoWindow(TodoStore store, Action onChanged)
    {
        _store = store;
        _onChanged = onChanged;

        Title = "Todos";
        WindowDecorations = WindowDecorations.None;
        Background = Brushes.Transparent;
        TransparencyLevelHint = [WindowTransparencyLevel.Transparent];
        CanResize = false;
        Width = 760;
        Height = 640;
        ShowInTaskbar = true;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;

        // ── Top header bar (draggable, spans both panes) ──
        var heading = new TextBlock { Text = "Todos", Foreground = Fg, FontWeight = FontWeight.Bold, FontSize = 16 };
        var subhead = new TextBlock
        {
            Text = "Your own reminders. Add a due time and Perch nudges you.",
            Foreground = Muted, FontSize = 12, Margin = new Thickness(0, 2, 0, 0),
        };
        var closeGlyph = new Button
        {
            Content = "✕", Foreground = Muted, Background = Brushes.Transparent, BorderThickness = new Thickness(0),
            Padding = new Thickness(4, 0), FontSize = 14, Cursor = new Cursor(StandardCursorType.Hand),
            HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top,
        };
        closeGlyph.Click += (_, _) => Close();
        var header = new Grid
        {
            Margin = new Thickness(20, 16, 18, 14),
            Children = { new StackPanel { Children = { heading, subhead } }, closeGlyph },
        };
        // The whole header band drags the window. A Grid with no Background isn't hit-testable in its empty
        // space, so the drag handler lives on the Border (which has a Background) — that's what made only the
        // title text grabbable before.
        var headerBorder = new Border
        {
            Background = Bg, BorderBrush = Stroke, BorderThickness = new Thickness(0, 0, 0, 1), Child = header,
        };
        headerBorder.PointerPressed += (_, e) =>
        {
            if (e.Source is Button) return;
            if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) BeginMoveDrag(e);
        };

        // ── Left pane: composer + show-completed + list ──
        _titleBox = new TextBox { PlaceholderText = "What needs doing?", FontSize = 13 };
        _titleBox.KeyDown += (_, e) => { if (e.Key == Key.Enter) { CommitAdd(); e.Handled = true; } };
        _composerDue = new DueField(null);
        var addBtn = new Button
        {
            Content = "Add todo", Foreground = Brushes.White, Background = Accent, BorderThickness = new Thickness(0),
            CornerRadius = new CornerRadius(7), Padding = new Thickness(13, 6), FontWeight = FontWeight.SemiBold,
            Cursor = new Cursor(StandardCursorType.Hand), HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Center,
        };
        addBtn.Click += (_, _) => CommitAdd();
        var composer = new StackPanel
        {
            Margin = new Thickness(14, 13, 14, 13),
            Children = { _titleBox, Gap(8), _composerDue.Control, Gap(10), addBtn },
        };
        var composerBorder = new Border { Background = Bg2, BorderBrush = Stroke, BorderThickness = new Thickness(0, 0, 0, 1), Child = composer };

        _showCompleted = new CheckBox { Content = "Show completed", Foreground = Muted, FontSize = 12, Margin = new Thickness(14, 8, 0, 8) };
        _showCompleted.IsCheckedChanged += (_, _) => Refresh();
        var showBorder = new Border { BorderBrush = Stroke, BorderThickness = new Thickness(0, 0, 0, 1), Child = _showCompleted };

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

        // ── Right pane: the reading / edit surface ──
        _detailHost.Padding = new Thickness(22, 20, 22, 18);

        // ── Body grid: list | divider | detail ──
        var body = new Grid { ColumnDefinitions = new ColumnDefinitions("308,1,*") };
        var divider = new Border { Background = Stroke };
        Grid.SetColumn(leftGrid, 0);
        Grid.SetColumn(divider, 1);
        Grid.SetColumn(_detailHost, 2);
        body.Children.Add(leftGrid);
        body.Children.Add(divider);
        body.Children.Add(_detailHost);

        var root = new DockPanel();
        DockPanel.SetDock(headerBorder, Dock.Top);
        root.Children.Add(headerBorder);
        root.Children.Add(body);

        Content = new Border
        {
            Background = Bg, CornerRadius = new CornerRadius(12), BorderBrush = Stroke, BorderThickness = new Thickness(1.5),
            Child = root, ClipToBounds = true,
        };

        Refresh();
    }

    /// <summary>Re-points the reused window at the current store contents — a reload. Satisfies
    /// <c>WindowHost.ShowOrFocus</c>'s refresh-on-both-paths contract.</summary>
    public void Retarget() => Refresh();

    // ── The list ──

    private void Refresh()
    {
        _list.Children.Clear();
        var now = DateTime.UtcNow;
        bool showCompleted = _showCompleted.IsChecked == true;

        var incomplete = _store.All().Where(t => !t.Completed).ToList();
        var overdue = incomplete.Where(t => t.DueUtc is { } d && d < now).OrderBy(t => t.DueUtc);
        var upcoming = incomplete.Where(t => t.DueUtc is { } d && d >= now).OrderBy(t => t.DueUtc);
        var nodate = incomplete.Where(t => t.DueUtc is null).OrderBy(t => t.CreatedUtc);

        AddGroup("Overdue", OverdueColor, overdue, now);
        AddGroup("Upcoming", AccentColor, upcoming, now);
        AddGroup("No date", ((SolidColorBrush)Muted).Color, nodate, now);
        if (showCompleted)
            AddGroup("Completed", Palette.Idle,
                _store.All().Where(t => t.Completed).OrderByDescending(t => t.CompletedUtc ?? t.CreatedUtc), now);

        if (_list.Children.Count == 0)
            _list.Children.Add(new TextBlock
            {
                Text = showCompleted ? "Nothing here yet." : "Nothing to do. Add one on the left.",
                Foreground = Muted, FontSize = 12, Margin = new Thickness(16, 12),
            });

        // The selection may have been completed/removed — drop it if it's gone from the store.
        if (_selectedId is { } sel && _store.All().All(t => t.Id != sel)) { _selectedId = null; _detailEditing = false; }
        RenderDetail();
    }

    private void AddGroup(string name, Color color, IEnumerable<Todo> items, DateTime now)
    {
        var list = items.ToList();
        if (list.Count == 0) return;

        var label = new TextBlock
        {
            FontSize = 10.5, FontWeight = FontWeight.SemiBold, Foreground = new SolidColorBrush(color),
            Margin = new Thickness(15, 13, 15, 5), LetterSpacing = 0.6,
            Text = $"{name.ToUpperInvariant()}   {list.Count}",
        };
        _list.Children.Add(label);
        foreach (var t in list) _list.Children.Add(BuildRow(t, now));
    }

    private Control BuildRow(Todo t, DateTime now)
    {
        bool overdue = !t.Completed && t.DueUtc is { } d && d < now;
        bool soon = !t.Completed && !overdue && t.DueUtc is not null;

        var ring = new Ellipse
        {
            Width = 15, Height = 15, StrokeThickness = 1.8, VerticalAlignment = VerticalAlignment.Center,
            Stroke = new SolidColorBrush(overdue ? OverdueColor : soon ? AccentColor : ((SolidColorBrush)Stroke).Color),
            Fill = t.Completed ? new SolidColorBrush(Palette.Idle) : Brushes.Transparent,
        };
        var circle = new Button
        {
            Width = 22, Height = 22, Padding = new Thickness(0), Background = Brushes.Transparent,
            BorderThickness = new Thickness(0), Content = ring, Cursor = new Cursor(StandardCursorType.Hand),
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalContentAlignment = HorizontalAlignment.Center, VerticalContentAlignment = VerticalAlignment.Center,
        };
        ToolTip.SetTip(circle, t.Completed ? "Reopen" : "Complete");
        circle.Click += (_, _) => { if (t.Completed) _store.Reopen(t.Id); else _store.Complete(t.Id); Persist(); };

        var title = new TextBlock
        {
            Text = t.Title.Length > 0 ? t.Title : "(untitled)", FontSize = 13,
            Foreground = t.Completed ? Muted : Fg, TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
            TextDecorations = t.Completed ? TextDecorations.Strikethrough : null,
        };

        var meta = new TextBlock
        {
            FontSize = 11.5, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0),
            Foreground = new SolidColorBrush(overdue ? OverdueColor : soon ? AccentColor : ((SolidColorBrush)Muted).Color),
            Text = t.DueUtc is { } due && !t.Completed ? RelativeTime.DueLabel(now, due) : "",
        };

        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto") };
        Grid.SetColumn(circle, 0);
        Grid.SetColumn(title, 1);
        Grid.SetColumn(meta, 2);
        circle.Margin = new Thickness(0, 0, 8, 0);
        grid.Children.Add(circle);
        grid.Children.Add(title);
        grid.Children.Add(meta);

        bool selected = t.Id == _selectedId;
        var row = new Border
        {
            Child = grid, Padding = new Thickness(13, 7, 15, 7), Cursor = new Cursor(StandardCursorType.Hand),
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
            if (e.Source is Button || (e.Source is Visual v && v.FindAncestorOfType<Button>() is not null)) return;
            Select(t.Id);
        };
        return row;
    }

    private void Select(string id)
    {
        _selectedId = id;
        _detailEditing = false;
        Refresh();
    }

    // ── Composer (add) ──

    private void CommitAdd()
    {
        var title = _titleBox.Text?.Trim() ?? "";
        if (title.Length == 0) { _titleBox.Focus(); return; }
        var added = _store.Add(title, "", _composerDue.GetUtc());
        _titleBox.Text = "";
        _composerDue.Clear();
        _selectedId = added.Id;   // open the new one in the reading pane
        _detailEditing = false;
        Persist();
    }

    // ── The reading / edit pane ──

    private void RenderDetail()
    {
        if (_selectedId is null || _store.All().FirstOrDefault(t => t.Id == _selectedId) is not { } todo)
        {
            _detailHost.Content = EmptyDetail();
            return;
        }
        _detailHost.Content = _detailEditing ? EditDetail(todo) : ReadDetail(todo);
    }

    private Control EmptyDetail() => new StackPanel
    {
        VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center, Spacing = 6,
        Children =
        {
            new TextBlock { Text = "Select a todo", Foreground = Muted, FontSize = 14, FontWeight = FontWeight.SemiBold, HorizontalAlignment = HorizontalAlignment.Center },
            new TextBlock { Text = "Its notes and due time show here.", Foreground = new SolidColorBrush(((SolidColorBrush)Muted).Color) { Opacity = 0.7 }, FontSize = 12, HorizontalAlignment = HorizontalAlignment.Center },
        },
    };

    private Control ReadDetail(Todo t)
    {
        var now = DateTime.UtcNow;
        bool overdue = !t.Completed && t.DueUtc is { } d && d < now;
        var (groupName, groupColor) = Classify(t, now);

        var top = new StackPanel { Spacing = 0 };
        top.Children.Add(new TextBlock
        {
            Text = groupName.ToUpperInvariant(), FontSize = 10.5, FontWeight = FontWeight.SemiBold, LetterSpacing = 0.8,
            Foreground = new SolidColorBrush(groupColor), Margin = new Thickness(0, 0, 0, 10),
        });
        top.Children.Add(new TextBlock
        {
            Text = t.Title.Length > 0 ? t.Title : "(untitled)", FontSize = 21, FontWeight = FontWeight.SemiBold,
            Foreground = t.Completed ? Muted : Fg, TextWrapping = TextWrapping.Wrap,
            TextDecorations = t.Completed ? TextDecorations.Strikethrough : null,
        });

        if (t.DueUtc is { } due)
        {
            var pill = new Border
            {
                CornerRadius = new CornerRadius(999), Padding = new Thickness(9, 3),
                Background = new SolidColorBrush(overdue ? OverdueColor : AccentColor) { Opacity = 0.16 },
                Child = new TextBlock { Text = RelativeTime.DueLabel(now, due), FontSize = 12, FontWeight = FontWeight.SemiBold, Foreground = new SolidColorBrush(overdue ? OverdueColor : AccentColor) },
            };
            var abs = new TextBlock { Text = due.ToLocalTime().ToString("ddd d MMM · HH:mm"), FontSize = 12.5, Foreground = Muted, VerticalAlignment = VerticalAlignment.Center };
            top.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Spacing = 9, Margin = new Thickness(0, 12, 0, 0), Children = { pill, abs } });
        }
        else
        {
            top.Children.Add(new TextBlock { Text = "No due date", FontSize = 12.5, Foreground = Muted, Margin = new Thickness(0, 12, 0, 0) });
        }

        top.Children.Add(new TextBlock { Text = "NOTES", FontSize = 10.5, FontWeight = FontWeight.SemiBold, LetterSpacing = 0.8, Foreground = Muted, Margin = new Thickness(0, 20, 0, 7) });

        var desc = t.Description.Length > 0
            ? new TextBlock { Text = t.Description, FontSize = 14, LineHeight = 22, Foreground = Fg, TextWrapping = TextWrapping.Wrap }
            : new TextBlock { Text = "No notes.", FontSize = 13, Foreground = new SolidColorBrush(((SolidColorBrush)Muted).Color) { Opacity = 0.75 }, FontStyle = FontStyle.Italic };
        var descScroll = new ScrollViewer { Content = desc, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };

        var created = new TextBlock { Text = $"Added {t.CreatedUtc.ToLocalTime():d MMM · HH:mm}", FontSize = 11.5, Foreground = new SolidColorBrush(((SolidColorBrush)Muted).Color) { Opacity = 0.7 }, Margin = new Thickness(0, 16, 0, 0) };

        var completeBtn = SolidButton(t.Completed ? "Reopen" : "Complete", Accent, Brushes.White);
        completeBtn.Click += (_, _) => { if (t.Completed) _store.Reopen(t.Id); else _store.Complete(t.Id); Persist(); };
        var editBtn = OutlineButton("Edit", Fg, Stroke);
        editBtn.Click += (_, _) => { _detailEditing = true; RenderDetail(); };
        var deleteBtn = OutlineButton("Delete", new SolidColorBrush(OverdueColor), new SolidColorBrush(OverdueColor) { Opacity = 0.4 });
        deleteBtn.Click += (_, _) => { _store.Remove(t.Id); _selectedId = null; Persist(); };

        var actions = new Grid { Margin = new Thickness(0, 16, 0, 0) };
        var left = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { completeBtn, editBtn } };
        deleteBtn.HorizontalAlignment = HorizontalAlignment.Right;
        actions.Children.Add(left);
        actions.Children.Add(deleteBtn);
        var actionsBorder = new Border { BorderBrush = Stroke, BorderThickness = new Thickness(0, 1, 0, 0), Padding = new Thickness(0, 16, 0, 0), Child = actions };

        var grid = new Grid { RowDefinitions = new RowDefinitions("Auto,*,Auto") };
        Grid.SetRow(top, 0);
        Grid.SetRow(descScroll, 1);
        var bottom = new StackPanel { Children = { created, actionsBorder } };
        Grid.SetRow(bottom, 2);
        descScroll.Margin = new Thickness(0, 0, 0, 4);
        grid.Children.Add(top);
        grid.Children.Add(descScroll);
        grid.Children.Add(bottom);
        return grid;
    }

    private Control EditDetail(Todo t)
    {
        var titleBox = new TextBox { Text = t.Title, PlaceholderText = "Title", FontSize = 15, FontWeight = FontWeight.SemiBold };
        var descBox = new TextBox
        {
            Text = t.Description, PlaceholderText = "Notes (optional)", FontSize = 13, AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap, VerticalContentAlignment = VerticalAlignment.Top,
            [ScrollViewer.VerticalScrollBarVisibilityProperty] = ScrollBarVisibility.Auto,
        };
        var dueField = new DueField(t.DueUtc);

        var save = SolidButton("Save", Accent, Brushes.White);
        save.Click += (_, _) =>
        {
            var title = titleBox.Text?.Trim() ?? "";
            if (title.Length == 0) { titleBox.Focus(); return; }
            var edited = t.Clone();
            edited.Title = title;
            edited.Description = descBox.Text?.Trim() ?? "";
            var newDue = dueField.GetUtc();
            if (edited.DueUtc != newDue) edited.ReminderFiredUtc = null;   // rescheduled → let it nudge again
            edited.DueUtc = newDue;
            _store.Update(edited);
            _detailEditing = false;
            Persist();
        };
        var cancel = OutlineButton("Cancel", Muted, Stroke);
        cancel.Click += (_, _) => { _detailEditing = false; RenderDetail(); };

        var top = new StackPanel
        {
            Spacing = 8,
            Children =
            {
                new TextBlock { Text = "EDIT TODO", FontSize = 10.5, FontWeight = FontWeight.SemiBold, LetterSpacing = 0.8, Foreground = Muted },
                titleBox,
                dueField.Control,
            },
        };
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right, Children = { cancel, save } };
        var actionsBorder = new Border { BorderBrush = Stroke, BorderThickness = new Thickness(0, 1, 0, 0), Padding = new Thickness(0, 14, 0, 0), Child = actions };

        var grid = new Grid { RowDefinitions = new RowDefinitions("Auto,*,Auto") };
        Grid.SetRow(top, 0);
        descBox.Margin = new Thickness(0, 12, 0, 12);
        Grid.SetRow(descBox, 1);
        Grid.SetRow(actionsBorder, 2);
        grid.Children.Add(top);
        grid.Children.Add(descBox);
        grid.Children.Add(actionsBorder);
        return grid;
    }

    // Classifies a todo into its list group + colour, for the reading pane's eyebrow.
    private static (string Name, Color Color) Classify(Todo t, DateTime now)
    {
        if (t.Completed) return ("Completed", Palette.Idle);
        if (t.DueUtc is { } d) return d < now ? ("Overdue", OverdueColor) : ("Upcoming", AccentColor);
        return ("No date", ((SolidColorBrush)Muted).Color);
    }

    // ── Shared helpers ──

    private void Persist()
    {
        _store.Save();
        _onChanged();
        Refresh();
    }

    private static Button SolidButton(string text, IBrush bg, IBrush fg) => new()
    {
        Content = text, Foreground = fg, Background = bg, BorderThickness = new Thickness(0),
        CornerRadius = new CornerRadius(7), Padding = new Thickness(13, 6), FontSize = 12.5, FontWeight = FontWeight.SemiBold,
        Cursor = new Cursor(StandardCursorType.Hand),
    };

    private static Button OutlineButton(string text, IBrush fg, IBrush stroke) => new()
    {
        Content = text, Foreground = fg, Background = Brushes.Transparent, BorderBrush = stroke, BorderThickness = new Thickness(1),
        CornerRadius = new CornerRadius(7), Padding = new Thickness(12, 6), FontSize = 12.5, Cursor = new Cursor(StandardCursorType.Hand),
    };

    private static Button LinkButton(string text, Action onClick)
    {
        var b = new Button
        {
            Content = text, FontSize = 11.5, Foreground = Muted, Background = Brushes.Transparent, BorderThickness = new Thickness(0),
            Padding = new Thickness(0, 4), Cursor = new Cursor(StandardCursorType.Hand), HorizontalAlignment = HorizontalAlignment.Left,
        };
        b.Click += (_, _) => onClick();
        return b;
    }

    private static Control Gap(double h) => new Border { Height = h };

    /// <summary>
    /// A compact due-date control: a button showing the current due (or "Add due date") that opens a popup
    /// with quick presets (Today / Tomorrow / Next week), a month <see cref="Calendar"/>, and a row of time
    /// chips. Replaces the cramped inline date/time spinners — the standard modern reminder-app pattern.
    /// Holds its own local date + time; <see cref="GetUtc"/> composes them to a UTC instant (a chosen date
    /// with no time defaults to 09:00).
    /// </summary>
    private sealed class DueField
    {
        private static readonly int[] TimeHours = [9, 12, 15, 18, 21];

        public Control Control { get; }

        private readonly Calendar _cal = new() { SelectionMode = CalendarSelectionMode.SingleDate };
        private readonly TextBlock _label = new() { FontSize = 12.5, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
        private readonly List<(Button Btn, TimeSpan Time)> _timeChips = new();
        private TimeSpan _time = new(9, 0, 0);

        public DueField(DateTime? initialUtc)
        {
            if (initialUtc?.ToLocalTime() is { } local)
            {
                _time = local.TimeOfDay;
                _cal.SelectedDate = local.Date;
                _cal.DisplayDate = local.Date;
            }
            _cal.SelectedDatesChanged += (_, _) => Update();

            var presets = new StackPanel
            {
                Orientation = Orientation.Horizontal, Spacing = 6,
                Children = { Preset("Today", 0), Preset("Tomorrow", 1), Preset("Next week", 7) },
            };

            var timeRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
            foreach (var h in TimeHours)
            {
                var chip = Chip($"{h:00}:00");
                var ts = new TimeSpan(h, 0, 0);
                chip.Click += (_, _) => { _time = ts; Update(); };
                _timeChips.Add((chip, ts));
                timeRow.Children.Add(chip);
            }

            var flyout = new Flyout { Placement = PlacementMode.BottomEdgeAlignedLeft };
            var clear = LinkButton("No date", () => { _cal.SelectedDate = null; Update(); });
            var done = SolidButton("Done", Accent, Brushes.White);
            done.Padding = new Thickness(12, 4);
            done.Click += (_, _) => flyout.Hide();
            done.HorizontalAlignment = HorizontalAlignment.Right;
            var bottom = new Grid { Margin = new Thickness(0, 2, 0, 0), Children = { clear, done } };

            // No fixed width — let the month calendar set the popup width (forcing it narrower clipped the
            // left/right day columns). The preset and time rows sit under it at the same width.
            _cal.HorizontalAlignment = HorizontalAlignment.Left;
            _cal.Margin = new Thickness(0, 2, 0, 2);
            // The Fluent Calendar template paints its header from Background and the month grid from
            // BorderBrush — both default to a near-black that clashes with the popup. Point them at a Perch
            // surface so the whole calendar blends with the flyout instead of showing a black header band.
            _cal.Background = Bg2;
            _cal.BorderBrush = Bg2;
            // Dim the leading/trailing days that belong to the neighbouring months. The Fluent Calendar binds
            // those (":inactive") cells' foreground to CalendarViewOutOfScopeForeground — a near-white in this
            // theme, so they read as bright as the current month. Override that resource on the calendar with a
            // faint muted brush (and a transparent out-of-scope fill) so the current month clearly leads.
            var faint = new SolidColorBrush(((SolidColorBrush)Muted).Color) { Opacity = 0.45 };
            _cal.Resources["CalendarViewOutOfScopeForeground"] = faint;
            _cal.Resources["CalendarViewOutOfScopeBackground"] = Brushes.Transparent;
            flyout.Content = new StackPanel
            {
                Margin = new Thickness(12), Spacing = 10,
                Children =
                {
                    presets,
                    _cal,
                    new TextBlock { Text = "TIME", FontSize = 10, FontWeight = FontWeight.SemiBold, LetterSpacing = 0.8, Foreground = Muted },
                    timeRow,
                    bottom,
                },
            };

            var icon = new global::Avalonia.Controls.Shapes.Path
            {
                Data = Geometry.Parse("M1,3 H15 V15 H1 Z M1,7 H15 M5,1 V4 M11,1 V4"),
                Stroke = Muted, StrokeThickness = 1.3, VerticalAlignment = VerticalAlignment.Center,
            };
            Control = new Button
            {
                Flyout = flyout, Background = Brushes.Transparent, BorderBrush = Stroke, BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(8), Padding = new Thickness(11, 8), Cursor = new Cursor(StandardCursorType.Hand),
                HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Left,
                Content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { icon, _label } },
            };
            Update();
        }

        public DateTime? GetUtc()
            => _cal.SelectedDate is { } d
                ? new DateTime(d.Year, d.Month, d.Day, _time.Hours, _time.Minutes, 0, DateTimeKind.Local).ToUniversalTime()
                : null;

        public void Clear() { _cal.SelectedDate = null; Update(); }

        private Button Preset(string text, int addDays)
        {
            var b = Chip(text);
            b.Click += (_, _) =>
            {
                var d = DateTime.Today.AddDays(addDays);
                _cal.SelectedDate = d;
                _cal.DisplayDate = d;
                Update();
            };
            return b;
        }

        private void Update()
        {
            bool hasDate = _cal.SelectedDate is not null;
            _label.Text = _cal.SelectedDate is { } d ? d.Date.Add(_time).ToString("ddd d MMM · HH:mm") : "Add due date";
            _label.Foreground = hasDate ? Fg : Muted;
            foreach (var (btn, ts) in _timeChips)
            {
                bool on = hasDate && ts == _time;
                btn.Background = on ? new SolidColorBrush(AccentColor) { Opacity = 0.16 } : Brushes.Transparent;
                btn.Foreground = on ? Accent : Muted;
                btn.BorderBrush = on ? Accent : Stroke;
            }
        }

        private static Button Chip(string text) => new()
        {
            Content = text, FontSize = 11.5, Foreground = Muted, Background = Brushes.Transparent,
            BorderBrush = Stroke, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(6),
            Padding = new Thickness(9, 4), Cursor = new Cursor(StandardCursorType.Hand),
        };
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            // Esc backs out of an edit first, then closes.
            if (_detailEditing) { _detailEditing = false; RenderDetail(); }
            else Close();
            e.Handled = true;
        }
        base.OnKeyDown(e);
    }
}
