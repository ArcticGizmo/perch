using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Perch.Avalonia.Theming;

namespace Perch.Avalonia.Views;

/// <summary>The three button roles of the rich session UI (mockup: <c>.btn-primary</c> / <c>.btn-ghost</c> /
/// <c>.btn-quiet</c>).</summary>
internal enum SessionButtonKind { Primary, Ghost, Quiet }

/// <summary>
/// A warm, rounded button for the session window, built on a <see cref="Border"/> (like
/// <see cref="GradientButton"/>) so the Fluent theme's hover/pressed template can't repaint it in the app's
/// cool chrome. Label plus an optional keyboard hint in the mono face ("Allow ↵"). Hover swaps the fill;
/// disabled dims and ignores clicks.
/// </summary>
internal sealed class SessionButton : Border
{
    private readonly IBrush _rest, _hover;
    private bool _enabled = true;

    public event Action? Click;

    public SessionButton(SessionPalette p, string label, SessionButtonKind kind, string? kbd = null, bool compact = false)
    {
        (_rest, _hover, IBrush fg, IBrush line) = kind switch
        {
            SessionButtonKind.Primary => (p.Brand, p.BrandHover, p.BrandInk, p.Brand),
            SessionButtonKind.Ghost   => (p.Raised2, p.Raised, p.Text, p.Border),
            _                         => ((IBrush)Brushes.Transparent, p.Raised2, p.Muted, p.Border),
        };
        Background = _rest;
        BorderBrush = line;
        BorderThickness = new Thickness(1);
        CornerRadius = SessionPalette.ButtonRadius;
        Padding = compact ? new Thickness(10, 6) : new Thickness(16, 9);
        Cursor = new Cursor(StandardCursorType.Hand);
        VerticalAlignment = VerticalAlignment.Center;

        var row = new StackPanel
        {
            Orientation = Orientation.Horizontal, Spacing = 8,
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
        };
        row.Children.Add(new TextBlock
        {
            Text = label, FontSize = compact ? 13 : 14, FontWeight = FontWeight.SemiBold,
            FontFamily = p.Body, Foreground = fg, VerticalAlignment = VerticalAlignment.Center,
        });
        if (!string.IsNullOrEmpty(kbd))
            row.Children.Add(new TextBlock
            {
                Text = kbd, FontSize = 11, FontFamily = p.Mono, Foreground = fg, Opacity = 0.7,
                VerticalAlignment = VerticalAlignment.Center,
            });
        Child = row;

        PointerEntered += (_, _) => { if (_enabled) Background = _hover; };
        PointerExited += (_, _) => Background = _rest;
        PointerReleased += (_, e) =>
        {
            if (_enabled && e.InitialPressMouseButton == MouseButton.Left) Click?.Invoke();
        };
    }

    public bool Enabled
    {
        get => _enabled;
        set
        {
            _enabled = value;
            Opacity = value ? 1.0 : 0.45;
            Background = _rest;
            Cursor = new Cursor(value ? StandardCursorType.Hand : StandardCursorType.Arrow);
        }
    }
}
