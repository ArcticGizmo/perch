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
/// A centred, keyboard-driven session palette — Perch's "command palette" for jumping to and reopening
/// Claude Code sessions, summoned by a global hotkey (Alt+Shift+Space by default). A search box takes focus
/// immediately; type to filter, ↑/↓ (or Tab) to move, Enter to act on the highlighted row, Ctrl+Enter to
/// copy its <c>claude --resume</c> command, Esc or clicking away to dismiss. Modelled on
/// <see cref="QrWindow"/>'s borderless card chrome.
///
/// The list is unified: <b>active</b> sessions (a live process, focusing its terminal on Enter) sit above a
/// hairline divider, then <b>recently-closed</b> sessions (reopened in a fresh terminal on Enter). Active
/// rows come from the live monitor and are shown immediately; the closed roster is read from disk off the UI
/// thread and streamed in via <see cref="SetClosedSessions"/> so the hotkey stays instant. It works on a
/// snapshot — short-lived, so it doesn't track live updates.
///
/// Unlike the overlay (a no-activate tool window), this window must take keyboard focus, so the app forces
/// it to the foreground on open (see <see cref="Perch.Platform.IWindowChrome.ForceForeground"/>) — the OS
/// otherwise blocks a background tray process from stealing focus.
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

    private const string HintText = "↵ focus / reopen        Ctrl+↵ copy resume command";

    // One filterable row, built from either a live session or a closed transcript. IsActive picks the
    // Enter action (focus vs reopen) and the row's look (filled vs hollow dot).
    private sealed class Entry
    {
        public bool IsActive { get; init; }
        public ClaudeSession? Session { get; init; } // set iff IsActive — the target for FocusSession
        public string SessionId { get; init; } = "";
        public string Cwd { get; init; } = "";
        public string DisplayName { get; init; } = "";
        public string ProjectName { get; init; } = "";
        public string Subtitle { get; init; } = "";
        public string StatusText { get; init; } = "";
        public Color StatusColor { get; init; }

        public bool Matches(string q) =>
            DisplayName.Contains(q, StringComparison.OrdinalIgnoreCase)
            || ProjectName.Contains(q, StringComparison.OrdinalIgnoreCase)
            || Cwd.Contains(q, StringComparison.OrdinalIgnoreCase);
    }

    private readonly Action<ClaudeSession> _onFocus;
    private readonly Action<string, string> _onReopen; // (cwd, sessionId)
    private readonly Action<string> _onCopy;            // (sessionId)

    private readonly List<Entry> _active;
    private List<Entry> _closed = new();
    private List<Entry> _filtered;

    private readonly TextBox _search;
    private readonly StackPanel _list;
    private readonly TextBlock _hint;
    private readonly List<Control> _rows = new();
    private DispatcherTimer? _hintTimer;
    private int _selected;
    private bool _chosen;
    private bool _ready; // armed once focus settles, so the foreground-forcing dance can't self-dismiss

    public SessionSwitcherWindow(
        IReadOnlyList<ClaudeSession> active,
        Action<ClaudeSession> onFocus,
        Action<string, string> onReopen,
        Action<string> onCopy)
    {
        _onFocus = onFocus;
        _onReopen = onReopen;
        _onCopy = onCopy;
        _active = active.Select(ActiveEntry).ToList();
        _filtered = _active.ToList();

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
            PlaceholderText = "Jump to or reopen a session…",
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

        _hint = new TextBlock
        {
            Text = HintText, Foreground = Palette.MutedBrush, FontSize = 11,
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        var footer = new Border
        {
            BorderBrush = Stroke, BorderThickness = new Thickness(0, 1, 0, 0),
            Padding = new Thickness(12, 9), Child = _hint,
        };

        var stack = new StackPanel { Children = { _search, scroll, footer } };
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

    /// <summary>Folds the recently-closed roster (read from disk off the UI thread) into the list beneath
    /// the active sessions. Called on the UI thread after the window is already showing; a no-op once a row
    /// has been chosen. Preserves the current search text and re-applies the filter.</summary>
    public void SetClosedSessions(IReadOnlyList<HistoryEntry> closed)
    {
        if (_chosen) return;
        // Keep the user's place if they've already started navigating the active rows while the roster loaded.
        var selectedId = _selected >= 0 && _selected < _filtered.Count ? _filtered[_selected].SessionId : null;
        _closed = closed.Select(ClosedEntry).ToList();
        ApplyFilter();
        if (selectedId is not null)
        {
            int i = _filtered.FindIndex(e => e.SessionId == selectedId);
            if (i >= 0) { _selected = i; Highlight(); }
        }
    }

    private static Entry ActiveEntry(ClaudeSession s) => new()
    {
        IsActive = true,
        Session = s,
        SessionId = s.SessionId,
        Cwd = s.Cwd ?? "",
        DisplayName = s.DisplayName,
        ProjectName = s.ProjectName,
        Subtitle = Subtitle(s),
        StatusText = StatusLabel(s),
        StatusColor = StatusColor(s.Status),
    };

    private static Entry ClosedEntry(HistoryEntry e) => new()
    {
        IsActive = false,
        SessionId = e.SessionId,
        Cwd = e.Cwd,
        DisplayName = e.DisplayName, // the /rename title when set, else the project name
        ProjectName = e.ProjectName,
        Subtitle = string.IsNullOrWhiteSpace(e.Cwd) ? e.ProjectName : e.Cwd,
        StatusText = $"closed · {e.RelativeTime}",
        StatusColor = IdleColor,
    };

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
                if (_filtered.Count > 0) Choose(_filtered[_selected], copy: e.KeyModifiers.HasFlag(KeyModifiers.Control));
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
        IEnumerable<Entry> all = _active.Concat(_closed);
        _filtered = q.Length == 0 ? all.ToList() : all.Where(e => e.Matches(q)).ToList();
        _selected = 0;
        Rebuild();
    }

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
            // Hairline divider at the active→closed boundary (only when both groups are present). It's not
            // added to _rows, so it never gets keyboard/selection focus.
            if (i > 0 && _filtered[i - 1].IsActive && !_filtered[i].IsActive)
                _list.Children.Add(BuildDivider());

            var row = BuildRow(_filtered[i], i);
            _rows.Add(row);
            _list.Children.Add(row);
        }
        Highlight();
    }

    private static Control BuildDivider() => new Border
    {
        Height = 1, Background = Stroke, Margin = new Thickness(12, 5),
    };

    private Control BuildRow(Entry e, int index)
    {
        var dot = new Ellipse
        {
            Width = 10, Height = 10, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(2, 0, 12, 0),
        };
        if (e.IsActive)
        {
            dot.Fill = new SolidColorBrush(e.StatusColor);
        }
        else
        {
            // Hollow dot marks a closed session — nothing to focus, only to reopen.
            dot.Fill = Brushes.Transparent;
            dot.Stroke = new SolidColorBrush(IdleColor);
            dot.StrokeThickness = 1.5;
        }

        var name = new TextBlock
        {
            Text = e.DisplayName, Foreground = Palette.TitleBrush, FontSize = 14, FontWeight = FontWeight.SemiBold,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        var subtitle = new TextBlock
        {
            Text = e.Subtitle, Foreground = Palette.MutedBrush, FontSize = 11,
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
            Text = e.StatusText, Foreground = new SolidColorBrush(e.StatusColor), FontSize = 11,
            VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Right,
        };
        Grid.SetColumn(status, 2);

        // Trailing affordance: → jump to the live terminal, ↻ reopen a closed session.
        var glyph = new TextBlock
        {
            Text = e.IsActive ? "→" : "↻",
            Foreground = e.IsActive ? Palette.MutedBrush : Palette.TitleBrush, FontSize = 14,
            VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 2, 0),
        };
        Grid.SetColumn(glyph, 3);

        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto,Auto") };
        grid.Children.Add(dot);
        grid.Children.Add(textStack);
        grid.Children.Add(status);
        grid.Children.Add(glyph);

        var border = new Border
        {
            Child = grid, CornerRadius = new CornerRadius(7), Padding = new Thickness(12, 9),
            Background = Brushes.Transparent, Cursor = new Cursor(StandardCursorType.Hand),
        };
        border.PointerEntered += (_, _) => { _selected = index; Highlight(); };
        border.PointerPressed += (_, _) => Choose(e, copy: false);
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

    // Enter (copy=false): focus the live terminal, or reopen a closed session in a fresh one, then dismiss.
    // Ctrl+Enter (copy=true): copy the `claude --resume` command and stay open with a brief confirmation,
    // so the user can grab several or keep browsing.
    private void Choose(Entry e, bool copy)
    {
        if (_chosen) return;

        if (copy)
        {
            _onCopy(e.SessionId);
            FlashCopied();
            return;
        }

        _chosen = true;
        if (e.IsActive) _onFocus(e.Session!);
        else _onReopen(e.Cwd, e.SessionId);
        Close();
    }

    // Briefly replace the hint with a "copied" confirmation, then restore it.
    private void FlashCopied()
    {
        _hint.Text = "Copied  ·  claude --resume …";
        _hintTimer?.Stop();
        _hintTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1600) };
        _hintTimer.Tick += (_, _) =>
        {
            _hintTimer!.Stop();
            _hint.Text = HintText;
        };
        _hintTimer.Start();
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
