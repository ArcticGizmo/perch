using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Threading;
using Perch.Avalonia.Theming;
using Perch.Platform;

namespace Perch.Avalonia.Notifications;

/// <summary>
/// The Avalonia <see cref="INotifier"/>: an owner-drawn toast, in Perch's own look, rather than a native
/// OS toast — so it needs no packaging/AUMID registration and is cross-platform by construction. Toasts
/// stack at the bottom-right of the overlay's screen (a lazily-created <see cref="ToastHostWindow"/>),
/// auto-dismiss, and — for session toasts — focus the terminal on click via <see cref="SessionActivated"/>.
/// </summary>
internal sealed class AvaloniaToastNotifier : INotifier
{
    private readonly Func<Screen?> _targetScreen;
    private ToastHostWindow? _host;

    public event Action<string, string?>? SessionActivated;

    /// <param name="targetScreen">Resolves the screen to show toasts on (the overlay's current screen);
    /// falls back to primary when it returns null.</param>
    public AvaloniaToastNotifier(Func<Screen?> targetScreen) => _targetScreen = targetScreen;

    public void Show(string title, string body, ToastLevel level, string? pid, string? project)
    {
        // Always marshal to the UI thread — a monitor callback may arrive on it already, but ntfy-test
        // continuations resume off it.
        Dispatcher.UIThread.Post(() =>
        {
            _host ??= CreateHost();
            _host.AddToast(title, body, level, pid, project);
        });
    }

    private ToastHostWindow CreateHost()
    {
        var host = new ToastHostWindow(_targetScreen);
        host.ToastActivated += (pid, project) => SessionActivated?.Invoke(pid, project);
        host.Closed += (_, _) => _host = null; // recreate if it's ever torn down
        return host;
    }
}

/// <summary>
/// A transparent, topmost, non-activating, no-taskbar window that hosts a bottom-anchored stack of toast
/// cards at the corner of a screen. Sizes to its content and repositions to the screen's bottom-right as
/// cards come and go; hides itself when the last card is gone. Never takes focus (so a toast can't steal
/// the caret from the terminal you're typing in).
/// </summary>
internal sealed class ToastHostWindow : Window
{
    private const int MaxCards = 4;
    private const double EdgeMargin = 12;
    private static readonly TimeSpan Dwell = TimeSpan.FromSeconds(8);

    private readonly StackPanel _stack;
    private readonly Func<Screen?> _targetScreen;
    private readonly Dictionary<Control, DispatcherTimer> _timers = new();

    /// <summary>Raised when a session card (one built with a non-null pid) is clicked.</summary>
    public event Action<string, string?>? ToastActivated;

    public ToastHostWindow(Func<Screen?> targetScreen)
    {
        _targetScreen = targetScreen;

        WindowDecorations = WindowDecorations.None;
        Background = Brushes.Transparent;
        TransparencyLevelHint = [WindowTransparencyLevel.Transparent];
        Topmost = true;
        ShowInTaskbar = false;
        ShowActivated = false; // never steal focus when a toast pops
        CanResize = false;
        SizeToContent = SizeToContent.WidthAndHeight;
        WindowStartupLocation = WindowStartupLocation.Manual;

        _stack = new StackPanel { Orientation = Orientation.Vertical, Spacing = 8, Margin = new Thickness(EdgeMargin) };
        Content = _stack;
    }

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        // Tool-window + no-activate so it stays out of Alt+Tab and never takes focus. NOT click-through:
        // the cards must receive clicks.
        if (OperatingSystem.IsWindows() && TryGetPlatformHandle() is { } h)
            Perch.Platform.Windows.OverlayNativeChrome.MakeToolWindowNoActivate(h.Handle);
    }

    public void AddToast(string title, string body, ToastLevel level, string? pid, string? project)
    {
        if (!IsVisible)
        {
            PositionBeforeShow();
            Show();
        }

        // Drop the oldest card once we're at the cap so the stack can't grow without bound.
        while (_stack.Children.Count >= MaxCards)
            RemoveCard((Control)_stack.Children[0]);

        var card = BuildCard(title, body, level, pid, project);
        _stack.Children.Add(card);

        var timer = new DispatcherTimer { Interval = Dwell };
        timer.Tick += (_, _) => RemoveCard(card);
        _timers[card] = timer;
        timer.Start();

        Reposition();
    }

    private void RemoveCard(Control card)
    {
        if (_timers.Remove(card, out var timer)) timer.Stop();
        _stack.Children.Remove(card);
        if (_stack.Children.Count == 0) Hide();
        else Reposition();
    }

    private Control BuildCard(string title, string body, ToastLevel level, string? pid, string? project)
    {
        var dot = new Ellipse
        {
            Width = 10, Height = 10, Fill = new SolidColorBrush(LevelColor(level)),
            VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 3, 10, 0),
        };
        var texts = new StackPanel { Orientation = Orientation.Vertical, Spacing = 2 };
        texts.Children.Add(new TextBlock
        {
            Text = title, FontSize = 13, FontWeight = FontWeight.SemiBold, Foreground = Palette.TitleBrush,
        });
        texts.Children.Add(new TextBlock
        {
            Text = body, FontSize = 12, Foreground = Palette.MutedBrush,
            TextWrapping = TextWrapping.Wrap, MaxWidth = 280,
        });
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(dot);
        row.Children.Add(texts);

        var card = new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(30, 30, 40)),
            BorderBrush = Palette.BorderBrush,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(12, 10),
            Width = 320,
            Cursor = new Cursor(StandardCursorType.Hand),
            Child = row,
            BoxShadow = new BoxShadows(new BoxShadow { Blur = 12, OffsetY = 3, Color = Color.FromArgb(120, 0, 0, 0) }),
        };
        card.PointerPressed += (_, _) =>
        {
            if (!string.IsNullOrEmpty(pid)) ToastActivated?.Invoke(pid, project);
            RemoveCard(card);
        };
        return card;
    }

    private static Color LevelColor(ToastLevel level) => level switch
    {
        ToastLevel.Warning => Palette.Orange,
        ToastLevel.Error => Palette.Red,
        _ => Palette.Accent,
    };

    // Nudge the window to the target screen's bottom-right before the first show, so it doesn't flash at
    // the top-left corner while the real size settles.
    private void PositionBeforeShow()
    {
        var screen = TargetScreen();
        if (screen is null) return;
        var wa = screen.WorkingArea;
        double s = screen.Scaling;
        int estW = (int)(344 * s), estH = (int)(110 * s);
        Position = new PixelPoint(wa.X + wa.Width - estW, wa.Y + wa.Height - estH);
    }

    // Re-anchor to the bottom-right after the content size settles (SizeToContent updates Bounds on the
    // next layout pass, so defer a frame).
    private void Reposition() => Dispatcher.UIThread.Post(() =>
    {
        var screen = TargetScreen();
        if (screen is null) return;
        var wa = screen.WorkingArea;
        double s = screen.Scaling;
        int w = (int)(Bounds.Width * s), h = (int)(Bounds.Height * s);
        int margin = (int)(EdgeMargin * s);
        Position = new PixelPoint(
            wa.X + wa.Width - w - margin,
            wa.Y + wa.Height - h - margin);
    }, DispatcherPriority.Loaded);

    private Screen? TargetScreen() =>
        _targetScreen() ?? Screens.Primary ?? (Screens.All.Count > 0 ? Screens.All[0] : null);
}
