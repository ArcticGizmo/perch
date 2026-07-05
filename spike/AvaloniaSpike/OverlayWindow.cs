using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;

namespace AvaloniaSpike;

/// <summary>
/// Spike of the Perch floating overlay shell in Avalonia. Proves the window traits the real
/// <c>OverlayForm</c> gets today from Win32 layered-window styles can be had declaratively:
/// borderless, transparent, always-on-top, no taskbar entry, and draggable from anywhere on the panel.
/// The only piece deferred to a platform service is the per-pixel-alpha ambient glow (UpdateLayeredWindow).
/// </summary>
internal sealed class OverlayWindow : Window
{
    public OverlayWindow()
    {
        Title = "Perch (Avalonia spike)";
        WindowDecorations = WindowDecorations.None;   // borderless — no title bar / chrome
        Background = Brushes.Transparent;              // window surface is transparent...
        TransparencyLevelHint = [WindowTransparencyLevel.Transparent];
        Topmost = true;                                // always-on-top
        ShowInTaskbar = false;                         // no taskbar button (WS_EX_TOOLWINDOW analogue)
        CanResize = false;
        SizeToContent = SizeToContent.WidthAndHeight;
        WindowStartupLocation = WindowStartupLocation.Manual;
        Position = new PixelPoint(60, 60);

        // Rounded dark panel — the visible overlay body. ...with rounded content painted on top.
        var panel = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(245, 15, 15, 20)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(45, 45, 60)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(12),
            Padding = new Thickness(14),
            Width = 260,
        };

        var stack = new StackPanel { Spacing = 10 };

        var header = new TextBlock
        {
            Text = "Perch — 7 sessions",
            Foreground = new SolidColorBrush(Color.FromRgb(225, 225, 235)),
            FontWeight = FontWeight.SemiBold,
            FontSize = 14,
            HorizontalAlignment = HorizontalAlignment.Center,
        };

        var card = new StatCard { Width = 232, Height = 150 };

        var hint = new TextBlock
        {
            Text = "drag me · right-click tray to exit",
            Foreground = new SolidColorBrush(Color.FromRgb(110, 110, 130)),
            FontSize = 10,
            HorizontalAlignment = HorizontalAlignment.Center,
        };

        stack.Children.Add(header);
        stack.Children.Add(card);
        stack.Children.Add(hint);
        panel.Child = stack;
        Content = panel;

        // Drag the borderless window from anywhere on the panel (OverlayForm uses
        // ReleaseCapture + WM_NCLBUTTONDOWN today; BeginMoveDrag is the cross-platform equivalent).
        panel.PointerPressed += (_, e) =>
        {
            if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
                BeginMoveDrag(e);
        };
    }
}
