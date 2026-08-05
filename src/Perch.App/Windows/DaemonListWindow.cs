using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Layout;
using Avalonia.Media;
using Perch.Data;

namespace Perch.Avalonia.Windows;

/// <summary>
/// The full daemon roster, centred on screen — opened from the overlay strip's "show +N more" line when
/// the daemon has more workers than the strip's cap. One row per worker mirroring the strip's line
/// (status dot, task name or project, trailing "spare"/project label) plus the details the strip has no
/// room for (session id, started time) as a tooltip. Clicking a row opens the same options menu the
/// strip's lines do — these sessions have no window to focus, so the menu is the row's interaction.
/// Reused via <c>WindowHost.ShowOrFocus</c> and refreshed live as the roster changes; styled off
/// <see cref="ChangelogWindow"/> so the popups read as one app.
/// </summary>
internal sealed class DaemonListWindow : Window
{
    private static readonly IBrush Bg     = new SolidColorBrush(Color.FromRgb(15, 15, 20));
    private static readonly IBrush Stroke = new SolidColorBrush(Color.FromRgb(45, 45, 60));
    private static readonly IBrush Fg     = new SolidColorBrush(Color.FromRgb(225, 225, 235));
    private static readonly IBrush Muted  = new SolidColorBrush(Color.FromRgb(120, 120, 140));
    private static readonly IBrush RowHover = new SolidColorBrush(Color.FromArgb(28, 255, 255, 255));

    // Status dot colours, matching the overlay's palette so the two surfaces read as one.
    private static readonly Color Running   = Color.FromRgb(34, 197, 94);
    private static readonly Color Attention = Color.FromRgb(251, 146, 60);
    private static readonly Color Awaiting  = Color.FromRgb(250, 204, 21);
    private static readonly Color ApiError  = Color.FromRgb(239, 68, 68);
    private static readonly Color Idle      = Color.FromRgb(100, 116, 139);

    private readonly Action<string> _openHistory;
    private readonly Func<string, SessionStatus?> _statusOf;
    private readonly StackPanel _list = new() { Spacing = 2 };
    private readonly TextBlock _subhead;

    /// <param name="openHistory">Opens the history viewer on a session id (the app's OpenHistory).</param>
    /// <param name="statusOf">Resolves a worker's live session status when its hooks have written a
    /// session file; null for a roster-only worker (dot renders idle-grey).</param>
    public DaemonListWindow(Action<string> openHistory, Func<string, SessionStatus?> statusOf)
    {
        _openHistory = openHistory;
        _statusOf = statusOf;

        WindowDecorations = WindowDecorations.None;
        Background = Brushes.Transparent;
        TransparencyLevelHint = [WindowTransparencyLevel.Transparent];
        Topmost = true;
        CanResize = false;
        Width = 460;
        SizeToContent = SizeToContent.Height;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;

        var title = new TextBlock
        {
            Text = "Daemon sessions", Foreground = Fg, FontWeight = FontWeight.Bold, FontSize = 16,
        };
        _subhead = new TextBlock
        {
            Foreground = Muted, FontSize = 12, Margin = new Thickness(0, 2, 0, 0),
            TextWrapping = TextWrapping.Wrap,
            Text = "Background workers hosted by the Claude Code daemon. They have no terminal window — " +
                   "click one for its options.",
        };
        var headingStack = new StackPanel { Children = { title, _subhead } };

        var closeGlyph = new Button
        {
            Content = "✕", Foreground = Muted, Background = Brushes.Transparent,
            BorderThickness = new Thickness(0), Padding = new Thickness(4, 0), FontSize = 14,
            HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top,
            Cursor = new Cursor(StandardCursorType.Hand),
        };
        closeGlyph.Click += (_, _) => Close();

        var header = new Grid { Children = { headingStack, closeGlyph } };

        var scroller = new ScrollViewer
        {
            Content = _list,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            MaxHeight = 420,
            Margin = new Thickness(0, 12, 0, 0),
        };

        var grid = new Grid { RowDefinitions = new RowDefinitions("Auto,Auto") };
        Grid.SetRow(header, 0);
        Grid.SetRow(scroller, 1);
        grid.Children.Add(header);
        grid.Children.Add(scroller);
        grid.Margin = new Thickness(22);

        Content = new Border
        {
            Background = Bg, CornerRadius = new CornerRadius(12),
            BorderBrush = Stroke, BorderThickness = new Thickness(1.5),
            Child = grid,
        };
    }

    /// <summary>Replaces the listed workers. Called on open and again whenever the roster changes while
    /// the window is up, so a worker finishing or a spare being dispatched updates the list live.</summary>
    public void SetWorkers(IReadOnlyList<DaemonWorker> workers)
    {
        _list.Children.Clear();

        if (workers.Count == 0)
        {
            _list.Children.Add(new TextBlock
            {
                Text = "No daemon workers are running.", Foreground = Muted, FontSize = 12,
                Margin = new Thickness(4, 8),
            });
            return;
        }

        foreach (var w in workers)
            _list.Children.Add(BuildRow(w));
    }

    private Control BuildRow(DaemonWorker w)
    {
        var dotColor = _statusOf(w.SessionId) switch
        {
            SessionStatus.Running        => Running,
            SessionStatus.NeedsAttention => Attention,
            SessionStatus.AwaitingInput  => Awaiting,
            SessionStatus.ApiError       => ApiError,
            _                            => Idle,
        };
        var dot = new Ellipse
        {
            Width = 8, Height = 8, Fill = new SolidColorBrush(dotColor),
            VerticalAlignment = VerticalAlignment.Center,
        };

        var name = new TextBlock
        {
            Text = w.DisplayName, Foreground = Fg, FontSize = 13,
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
        };

        string metaText = w.IsSpare ? "spare"
            : w.Name != null && w.ProjectName.Length > 0 ? w.ProjectName
            : w.ShortId;
        var meta = new TextBlock
        {
            Text = metaText, Foreground = Muted, FontSize = 11,
            VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 0, 0),
        };

        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto") };
        Grid.SetColumn(dot, 0);
        Grid.SetColumn(name, 1);
        Grid.SetColumn(meta, 2);
        name.Margin = new Thickness(10, 0, 0, 0);
        grid.Children.Add(dot);
        grid.Children.Add(name);
        grid.Children.Add(meta);

        var row = new Border
        {
            Child = grid,
            Padding = new Thickness(10, 7),
            CornerRadius = new CornerRadius(6),
            Background = Brushes.Transparent,
            Cursor = new Cursor(StandardCursorType.Hand),
        };
        ToolTip.SetTip(row, $"{w.SessionId}\n{w.Cwd}\npid {w.Pid} - started {w.StartedAt:HH:mm}");

        row.PointerEntered += (_, _) => row.Background = RowHover;
        row.PointerExited += (_, _) => row.Background = Brushes.Transparent;
        row.PointerPressed += (_, _) => ShowOptions(row, w);

        return row;
    }

    // The same first-pass options the overlay strip's lines offer, anchored to the clicked row. This is
    // a normal activatable window, so the flyout's built-in light dismiss works here.
    private void ShowOptions(Control anchor, DaemonWorker w)
    {
        var items = new List<Control>
        {
            Item("View history", () => _openHistory(w.SessionId)),
            Item("Open transcript in VS Code", () => OpenTranscript(w)),
            Item("Copy session ID", () => CopyToClipboard(w.SessionId)),
            Item("Copy resume command", () => CopyToClipboard(ClaudeCli.ResumeCommand(w.SessionId))),
        };
        new MenuFlyout { ItemsSource = items, Placement = PlacementMode.Pointer }
            .ShowAt(anchor, showAtPointer: true);
    }

    private static MenuItem Item(string header, Action onClick)
    {
        var item = new MenuItem { Header = header };
        item.Click += (_, _) => onClick();
        return item;
    }

    private void CopyToClipboard(string text) => Clipboard?.SetTextAsync(text);

    private static void OpenTranscript(DaemonWorker w)
    {
        var path = TranscriptLocator.Resolve(w.SessionId, w.Cwd);
        if (path == null) return;
        try
        {
            System.Diagnostics.Process.Start(
                new System.Diagnostics.ProcessStartInfo("code", $"\"{path}\"") { UseShellExecute = true });
        }
        catch { /* best-effort — VS Code may not be on PATH */ }
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Escape) { Close(); e.Handled = true; }
        base.OnKeyDown(e);
    }
}
