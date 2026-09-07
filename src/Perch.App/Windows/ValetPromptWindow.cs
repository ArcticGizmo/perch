using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Perch.Avalonia.Theming;
using Perch.Data;
using Perch.Data.Control;

namespace Perch.Avalonia.Windows;

/// <summary>
/// PoC: the permission valet's prompt surface (session-control M2). Each pending
/// <see cref="ValetRequest"/> a hooked session is blocked on shows as a card — project, the
/// <see cref="ToolSummary"/> phrase, the clipped tool input — with Allow / Deny / Ignore. Every card
/// answers its hook exactly once: a button click, the auto-pass timer (the terminal takes over), or the
/// window closing (everything outstanding passes). Topmost but never focus-stealing, parked at the
/// work-area's top-right.
/// </summary>
internal sealed class ValetPromptWindow : Window
{
    /// <summary>How long a card waits before answering <c>pass</c> so the terminal's own prompt takes
    /// over. Kept comfortably under the hook's 20 s read deadline. PoC constant; a setting at the gate.</summary>
    private static readonly TimeSpan AutoPass = TimeSpan.FromSeconds(18);

    private readonly StackPanel _cards = new() { Spacing = 8, Margin = new Thickness(12) };
    private readonly List<Action> _releases = new();   // one per unanswered card, so close can free its hook
    private int _pending;

    public ValetPromptWindow()
    {
        Title = "Perch — permission request (PoC)";
        Width = 460;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        Topmost = true;
        ShowActivated = false;         // never steal the keyboard from the terminal being valeted
        ShowInTaskbar = false;
        Background = Palette.FormBgBrush;
        Content = _cards;
    }

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        try
        {
            var area = Screens.Primary?.WorkingArea;
            if (area is { } wa)
            {
                var scale = Screens.Primary!.Scaling;
                Position = new PixelPoint(wa.Right - (int)(Width * scale) - 16, wa.Y + 16);
            }
        }
        catch { /* default position */ }
    }

    /// <summary>Adds one pending request. <paramref name="respond"/> is invoked exactly once, on any
    /// thread. UI-thread only.</summary>
    public void Enqueue(ValetRequest request, Action<ValetDecision> respond)
    {
        bool answered = false;
        _pending++;

        Border card = null!;
        Action release = null!;
        void Answer(ValetDecision d)
        {
            if (answered) return;
            answered = true;
            respond(d);
            _releases.Remove(release);
            _pending--;
            _cards.Children.Remove(card);
            if (_pending == 0 && IsVisible) Close();   // IsVisible guards re-entry from OnClosed's sweep
        }
        release = () => Answer(ValetDecision.Pass);
        _releases.Add(release);

        var project = string.IsNullOrEmpty(request.Cwd) ? "session" : PathLeaf.Of(request.Cwd);
        var heading = new TextBlock
        {
            Text = $"{project} — {ToolSummary.Describe(request.ToolName, request.ToolInput)}",
            Foreground = Palette.TitleBrush, FontWeight = FontWeight.Bold, FontSize = 13,
            TextWrapping = TextWrapping.Wrap,
        };
        var detail = new SelectableTextBlock
        {
            Text = ToolSummary.Clip(request.ToolInput?.ToJsonString() ?? ""),
            Foreground = Palette.MutedBrush, FontSize = 11.5, TextWrapping = TextWrapping.Wrap,
            FontFamily = new FontFamily("Cascadia Code, Consolas, Menlo, monospace"),
            Margin = new Thickness(0, 4, 0, 0), MaxHeight = 120,
        };

        var allow = new Button { Content = "Allow", FontSize = 12, CornerRadius = new CornerRadius(6), Background = Palette.AccentBrush, Foreground = Palette.OnAccentBrush };
        allow.Click += (_, _) => Answer(ValetDecision.Allowed());
        var deny = new Button { Content = "Deny", FontSize = 12, CornerRadius = new CornerRadius(6) };
        deny.Click += (_, _) => Answer(ValetDecision.Denied());
        var ignore = new Button { Content = "Ignore", FontSize = 12, CornerRadius = new CornerRadius(6) };
        ignore.Click += (_, _) => Answer(ValetDecision.Pass);
        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal, Spacing = 6, Margin = new Thickness(0, 8, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Right, Children = { allow, deny, ignore },
        };

        card = new Border
        {
            Background = Palette.ButtonBgBrush, BorderBrush = Palette.AwaitingBrush,
            BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(8),
            Padding = new Thickness(12, 10),
            Child = new StackPanel { Children = { heading, detail, buttons } },
        };
        _cards.Children.Add(card);

        // Unanswered → pass, so the terminal's own prompt takes over (the fail-open guarantee).
        var timer = new DispatcherTimer { Interval = AutoPass };
        timer.Tick += (_, _) => { timer.Stop(); Answer(ValetDecision.Pass); };
        timer.Start();
    }

    protected override void OnClosed(EventArgs e)
    {
        // The user dismissed the window with cards still pending: release every blocked hook as pass so
        // the terminals' own prompts take over immediately rather than at the auto-pass timeout.
        foreach (var release in _releases.ToList())
            release();
        base.OnClosed(e);
    }
}
