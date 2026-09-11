using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Perch.Avalonia.Theming;
using Perch.Data;

namespace Perch.Avalonia.Windows;

/// <summary>
/// The session-history search palette — a centred, keyboard-driven modal for picking a session to read,
/// summoned by the history window's "Search" button. Deliberately the same command-palette idiom as the
/// global session switcher (<see cref="SessionSwitcherWindow"/>): a search box takes focus immediately,
/// type to filter (name / path / id), ↑/↓ move, Enter opens the highlighted row, Esc or clicking away
/// dismisses. Unlike the switcher it does nothing itself — it raises <see cref="Picked"/> and lets the
/// history window load the chosen transcript.
///
/// The list is a snapshot handed in at open time (the history window keeps it fresh); the palette is
/// short-lived, so it doesn't track live updates. Rows are the rich session rows the old inline dropdown
/// used — a live dot, the project/title name, the cwd (tail kept), and when · size.
/// </summary>
internal sealed class HistorySearchWindow : Window
{
    private const int SearchCap = 80;   // most rows shown at once while filtering

    private readonly SessionPalette _p = SessionPalette.Current;

    private readonly List<HistoryEntry> _all;
    private List<HistoryEntry> _filtered;
    private readonly string? _currentId;

    private readonly TextBox _search;
    private readonly StackPanel _list;
    private readonly ScrollViewer _scroll;
    private readonly TextBlock _empty;
    private readonly List<Border> _rows = new();
    private int _selected;
    private bool _chosen;
    private bool _ready;   // armed once focus settles, so the open-time activation can't self-dismiss

    /// <summary>A session was chosen (click / Enter). The history window loads its transcript.</summary>
    public event Action<HistoryEntry>? Picked;

    public HistorySearchWindow(IReadOnlyList<HistoryEntry> entries, string? currentId)
    {
        _all = entries.Where(e => !e.IsPlaceholder).ToList();
        _filtered = _all.ToList();
        _currentId = currentId;

        WindowDecorations = WindowDecorations.None;
        Background = Brushes.Transparent;
        TransparencyLevelHint = [WindowTransparencyLevel.Transparent];
        Topmost = true;
        ShowInTaskbar = false;
        CanResize = false;
        Width = 580;
        SizeToContent = SizeToContent.Height;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        // A magnifier glyph on the left reads the field as a search box.
        var magnifier = new global::Avalonia.Controls.Shapes.Path
        {
            Stroke = _p.Faint, StrokeThickness = 1.5, Width = 15, Height = 15,
            Data = Geometry.Parse("M2,6 a4,4 0 1 0 8,0 a4,4 0 1 0 -8,0 M9.2,9.2 L13,13"),
            VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(2, 0, 12, 0),
            [DockPanel.DockProperty] = Dock.Left,
        };
        _search = new TextBox
        {
            FontFamily = _p.Body, FontSize = 16, Foreground = _p.Text, CaretBrush = _p.Brand,
            Background = Brushes.Transparent, BorderThickness = new Thickness(0), Padding = new Thickness(0),
            VerticalAlignment = VerticalAlignment.Center,
            PlaceholderText = "Search sessions — name, path or id",
        };
        _search.TextChanged += (_, _) => ApplyFilter();
        var searchRow = new Border
        {
            Background = _p.Raised, BorderBrush = _p.Border, BorderThickness = new Thickness(0, 0, 0, 1),
            Padding = new Thickness(16, 14),
            Child = new DockPanel { LastChildFill = true, Children = { magnifier, _search } },
        };

        _list = new StackPanel { Margin = new Thickness(6) };
        _scroll = new ScrollViewer
        {
            Content = _list, MaxHeight = 400,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        };
        _empty = new TextBlock
        {
            Text = "No matching sessions", FontFamily = _p.Mono, FontSize = 13, Foreground = _p.Faint,
            Margin = new Thickness(16, 16), IsVisible = false,
        };

        var hint = new TextBlock
        {
            Text = "↑↓ move        ↵ open        esc close", FontFamily = _p.Mono, FontSize = 11,
            Foreground = _p.Faint, HorizontalAlignment = HorizontalAlignment.Center,
        };
        var footer = new Border
        {
            Background = _p.Raised, BorderBrush = _p.Border, BorderThickness = new Thickness(0, 1, 0, 0),
            Padding = new Thickness(12, 9), Child = hint,
        };

        Content = new Border
        {
            Background = _p.Surface, CornerRadius = new CornerRadius(14),
            BorderBrush = _p.Border, BorderThickness = new Thickness(1.5),
            BoxShadow = BoxShadows.Parse("0 18 48 0 #77000000"), ClipToBounds = true,
            Child = new StackPanel { Children = { searchRow, _scroll, _empty, footer } },
        };

        // Intercept navigation keys before the search box consumes them.
        AddHandler(KeyDownEvent, OnPreviewKeyDown, RoutingStrategies.Tunnel);

        Opened += (_, _) =>
        {
            _search.Focus();
            Dispatcher.UIThread.Post(() => _ready = true, DispatcherPriority.Background);
        };
        // Click away / Alt+Tab elsewhere dismisses — but only once armed, so the open-time activation
        // dance can't trip an immediate self-close.
        Deactivated += (_, _) => { if (_ready && !_chosen) Close(); };

        Rebuild();
        // Pre-select the currently-open session's row (else the first), so Enter reopens it.
        if (_currentId is { Length: > 0 })
        {
            int i = _filtered.FindIndex(e => e.SessionId == _currentId);
            if (i >= 0) _selected = i;
        }
        Highlight();
    }

    private void OnPreviewKeyDown(object? sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Escape:
                Close();
                e.Handled = true;
                break;
            case Key.Down or Key.Tab when !e.KeyModifiers.HasFlag(KeyModifiers.Shift):
                Move(1);
                e.Handled = true;
                break;
            case Key.Up:
            case Key.Tab when e.KeyModifiers.HasFlag(KeyModifiers.Shift):
                Move(-1);
                e.Handled = true;
                break;
            case Key.Enter:
                if (_filtered.Count > 0 && _selected >= 0 && _selected < _filtered.Count)
                    Choose(_filtered[_selected]);
                e.Handled = true;
                break;
        }
    }

    private void Move(int delta)
    {
        if (_filtered.Count == 0) return;
        int n = _filtered.Count;
        _selected = ((_selected + delta) % n + n) % n;
        Highlight();
    }

    private void ApplyFilter()
    {
        string q = (_search.Text ?? "").Trim();
        _filtered = (q.Length == 0 ? _all : _all.Where(e => MatchesSearch(e, q))).Take(SearchCap).ToList();
        _selected = _filtered.Count > 0 ? 0 : -1;
        Rebuild();
    }

    // Every space-separated term must appear (case-insensitive) across the entry's searchable fields.
    private static bool MatchesSearch(HistoryEntry e, string query)
    {
        var hay = $"{e.ProjectName}\n{e.Title}\n{e.Cwd}\n{e.SessionId}";
        foreach (var term in query.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            if (hay.IndexOf(term, StringComparison.OrdinalIgnoreCase) < 0) return false;
        return true;
    }

    private void Rebuild()
    {
        _list.Children.Clear();
        _rows.Clear();
        for (int i = 0; i < _filtered.Count; i++)
        {
            var row = Row(_filtered[i], i);
            _rows.Add(row);
            _list.Children.Add(row);
        }
        _empty.IsVisible = _filtered.Count == 0;
        _scroll.IsVisible = _filtered.Count > 0;
        Highlight();
    }

    // One row — a live dot, the project/title name, the cwd (tail kept), and when · size on the right.
    private Border Row(HistoryEntry e, int i)
    {
        var dot = new Border
        {
            Width = 9, Height = 9, CornerRadius = new CornerRadius(4.5),
            Background = e.IsActive ? _p.Brand : Brushes.Transparent,
            BorderBrush = e.IsActive ? Brushes.Transparent : _p.Faint, BorderThickness = new Thickness(1.5),
            VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(2, 0, 12, 0),
            [DockPanel.DockProperty] = Dock.Left,
        };
        var name = new TextBlock
        {
            FontFamily = _p.Body, FontSize = 14.5, Foreground = _p.Title, FontWeight = FontWeight.SemiBold,
            Text = e.DisplayName, TextTrimming = TextTrimming.CharacterEllipsis,
        };
        // A path keeps its tail (the folder name) and elides the head — the file-path truncation convention.
        var path = new TextBlock
        {
            FontFamily = _p.Mono, FontSize = 12, Foreground = _p.Muted, Text = e.Cwd,
            TextTrimming = TextTrimming.PrefixCharacterEllipsis, Margin = new Thickness(0, 1, 0, 0),
        };
        var meta = new TextBlock
        {
            FontFamily = _p.Mono, FontSize = 11.5, Foreground = _p.Faint,
            Text = e.SizeBytes > 0 ? $"{e.RelativeTime} · {e.SizeLabel}" : e.RelativeTime,
            VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 0, 0),
            [DockPanel.DockProperty] = Dock.Right,
        };
        var row = new Border
        {
            CornerRadius = SessionPalette.ButtonRadius, Padding = new Thickness(12, 9),
            Background = Brushes.Transparent, Cursor = new Cursor(StandardCursorType.Hand),
            Child = new DockPanel { LastChildFill = true, Children = { dot, meta, new StackPanel { Children = { name, path } } } },
        };
        row.PointerEntered += (_, _) => { _selected = i; Highlight(); };
        row.PointerReleased += (_, ev) => { if (ev.InitialPressMouseButton == MouseButton.Left) Choose(e); };
        return row;
    }

    private void Highlight()
    {
        for (int i = 0; i < _rows.Count; i++)
            _rows[i].Background = i == _selected ? _p.Raised2 : Brushes.Transparent;
        if (_selected >= 0 && _selected < _rows.Count) _rows[_selected].BringIntoView();
    }

    private void Choose(HistoryEntry e)
    {
        if (_chosen) return;
        _chosen = true;
        Picked?.Invoke(e);
        Close();
    }
}
