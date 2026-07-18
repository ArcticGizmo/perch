using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Perch.Avalonia.Theming;
using Perch.Data;

namespace Perch.Avalonia.Windows;

/// <summary>
/// A centred, keyboard-driven session switcher — Perch's "command palette" for jumping between active
/// Claude Code sessions, summoned by a global hotkey (Alt+Shift+Space by default). A search box takes
/// focus immediately; type to filter, ↑/↓ (or Tab) to move, Enter to jump to the highlighted session's
/// terminal, Esc or clicking away to dismiss. Modelled on <see cref="QrWindow"/>'s borderless card chrome.
///
/// Unlike the overlay (a no-activate tool window), this window must take keyboard focus, so the app forces
/// it to the foreground on open (see <see cref="Perch.Platform.IWindowChrome.ForceForeground"/>) — the OS
/// otherwise blocks a background tray process from stealing focus. It works on a snapshot of the session
/// list passed at construction; it's short-lived, so it doesn't track live updates.
/// </summary>
internal sealed class SessionSwitcherWindow : Window
{
    // Same status colours the overlay uses, so a session reads the same everywhere.
    private static readonly Color RunningColor   = Color.FromRgb(34, 197, 94);
    private static readonly Color AttentionColor = Color.FromRgb(251, 146, 60);
    private static readonly Color AwaitingColor  = Color.FromRgb(250, 204, 21);
    private static readonly Color IdleColor      = Color.FromRgb(100, 116, 139);

    private static readonly IBrush CardBg   = new SolidColorBrush(Color.FromRgb(15, 15, 20));
    private static readonly IBrush Stroke   = new SolidColorBrush(Color.FromRgb(45, 45, 60));
    private static readonly IBrush RowSel    = new SolidColorBrush(Color.FromRgb(40, 44, 62));
    private static readonly IBrush SearchBg  = new SolidColorBrush(Color.FromRgb(24, 24, 34));

    private readonly List<ClaudeSession> _all;
    private readonly Action<ClaudeSession> _onChosen;
    private readonly TextBox _search;
    private readonly StackPanel _list;

    private List<ClaudeSession> _filtered;
    private readonly List<Control> _rows = new();
    private int _selected;
    private bool _chosen;
    private bool _ready; // armed once focus settles, so the foreground-forcing dance can't self-dismiss

    public SessionSwitcherWindow(IReadOnlyList<ClaudeSession> sessions, Action<ClaudeSession> onChosen)
    {
        _all = sessions.ToList();
        _filtered = _all.ToList();
        _onChosen = onChosen;

        WindowDecorations = WindowDecorations.None;
        Background = Brushes.Transparent;
        TransparencyLevelHint = [WindowTransparencyLevel.Transparent];
        Topmost = true;
        ShowInTaskbar = false;
        CanResize = false;
        Width = 560;
        SizeToContent = SizeToContent.Height;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;

        _search = new TextBox
        {
            PlaceholderText = "Jump to a session…",
            Background = SearchBg, Foreground = Palette.FgBrush,
            BorderBrush = Stroke, BorderThickness = new Thickness(0, 0, 0, 1),
            CornerRadius = new CornerRadius(11, 11, 0, 0), FontSize = 16, Padding = new Thickness(14, 12),
        };
        _search.TextChanged += (_, _) => ApplyFilter();

        _list = new StackPanel { Margin = new Thickness(6) };
        var scroll = new ScrollViewer
        {
            Content = _list, MaxHeight = 380,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        };

        var stack = new StackPanel { Children = { _search, scroll } };
        Content = new Border
        {
            Background = CardBg, CornerRadius = new CornerRadius(12),
            BorderBrush = Stroke, BorderThickness = new Thickness(1.5),
            Child = stack, ClipToBounds = true,
        };

        // Intercept navigation keys before the search box consumes them (Up/Down aren't used by a single-
        // line TextBox, but Enter/Tab are — the tunnel handler wins so typing still reaches the box).
        AddHandler(KeyDownEvent, OnPreviewKeyDown, RoutingStrategies.Tunnel);

        Opened += (_, _) => _search.Focus();
        // Click away / Alt+Tab elsewhere dismisses — but only once armed (see TakeFocus), so the
        // foreground-forcing dance on open can't trip an immediate self-close.
        Deactivated += (_, _) => { if (_ready && !_chosen) Close(); };

        Rebuild();
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
                if (_filtered.Count > 0) Choose(_filtered[_selected]);
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
        _filtered = q.Length == 0
            ? _all.ToList()
            : _all.Where(s => Matches(s, q)).ToList();
        _selected = 0;
        Rebuild();
    }

    private static bool Matches(ClaudeSession s, string q) =>
        s.DisplayName.Contains(q, StringComparison.OrdinalIgnoreCase)
        || s.ProjectName.Contains(q, StringComparison.OrdinalIgnoreCase)
        || (s.Cwd?.Contains(q, StringComparison.OrdinalIgnoreCase) ?? false);

    private void Rebuild()
    {
        _list.Children.Clear();
        _rows.Clear();

        if (_filtered.Count == 0)
        {
            _list.Children.Add(new TextBlock
            {
                Text = "No matching sessions", Foreground = Palette.MutedBrush, FontSize = 13,
                Margin = new Thickness(12, 14),
            });
            return;
        }

        for (int i = 0; i < _filtered.Count; i++)
        {
            var row = BuildRow(_filtered[i], i);
            _rows.Add(row);
            _list.Children.Add(row);
        }
        Highlight();
    }

    private Control BuildRow(ClaudeSession s, int index)
    {
        var dot = new Ellipse
        {
            Width = 10, Height = 10, Fill = new SolidColorBrush(StatusColor(s.Status)),
            VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(2, 0, 12, 0),
        };

        var name = new TextBlock
        {
            Text = s.DisplayName, Foreground = Palette.TitleBrush, FontSize = 14, FontWeight = FontWeight.SemiBold,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        var subtitle = new TextBlock
        {
            Text = Subtitle(s), Foreground = Palette.MutedBrush, FontSize = 11,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        var textStack = new StackPanel
        {
            VerticalAlignment = VerticalAlignment.Center, Children = { name, subtitle },
        };
        Grid.SetColumn(dot, 0);
        Grid.SetColumn(textStack, 1);

        var status = new TextBlock
        {
            Text = StatusLabel(s), Foreground = new SolidColorBrush(StatusColor(s.Status)), FontSize = 11,
            VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 2, 0),
            HorizontalAlignment = HorizontalAlignment.Right,
        };
        Grid.SetColumn(status, 2);

        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto") };
        grid.Children.Add(dot);
        grid.Children.Add(textStack);
        grid.Children.Add(status);

        var border = new Border
        {
            Child = grid, CornerRadius = new CornerRadius(7), Padding = new Thickness(12, 9),
            Background = Brushes.Transparent, Cursor = new Cursor(StandardCursorType.Hand),
        };
        border.PointerEntered += (_, _) => { _selected = index; Highlight(); };
        border.PointerPressed += (_, _) => Choose(s);
        return border;
    }

    private void Highlight()
    {
        for (int i = 0; i < _rows.Count; i++)
            if (_rows[i] is Border b)
                b.Background = i == _selected ? RowSel : Brushes.Transparent;
        if (_selected >= 0 && _selected < _rows.Count)
            _rows[_selected].BringIntoView();
    }

    private void Choose(ClaudeSession s)
    {
        if (_chosen) return;
        _chosen = true;
        _onChosen(s);
        Close();
    }

    private static string Subtitle(ClaudeSession s)
    {
        if (!string.IsNullOrWhiteSpace(s.Activity)) return s.Activity!;
        // When the session was renamed, the project name is the useful secondary line; otherwise the cwd.
        if (s.Title is not null && !string.IsNullOrWhiteSpace(s.ProjectName)) return s.ProjectName;
        return string.IsNullOrWhiteSpace(s.Cwd) ? s.ProjectName : s.Cwd;
    }

    private static string StatusLabel(ClaudeSession s) => s.Status switch
    {
        SessionStatus.Running        => s.RunningElapsedLabel() is { } e ? $"running · {e}" : "running",
        SessionStatus.AwaitingInput  => s.AwaitingElapsedLabel() is { } e ? $"waiting · {e}" : "waiting on you",
        SessionStatus.NeedsAttention => "needs you",
        _                            => "idle",
    };

    private static Color StatusColor(SessionStatus status) => status switch
    {
        SessionStatus.Running        => RunningColor,
        SessionStatus.NeedsAttention => AttentionColor,
        SessionStatus.AwaitingInput  => AwaitingColor,
        _                            => IdleColor,
    };

    /// <summary>Forces the switcher to the foreground and hands the search box focus — the app calls this
    /// right after <see cref="Window.Show()"/>, since a global hotkey firing in a background tray doesn't
    /// grant foreground rights on its own.</summary>
    public void TakeFocus()
    {
        if (TryGetPlatformHandle() is { } handle)
            PlatformServices.WindowChrome.ForceForeground(handle.Handle);
        Activate();
        _search.Focus();
        // Arm dismiss-on-deactivate only after this batch settles, so any transient deactivation caused by
        // the foreground-forcing above doesn't immediately close the window.
        Dispatcher.UIThread.Post(() => _ready = true, DispatcherPriority.Background);
    }
}
