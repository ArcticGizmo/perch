using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;

namespace Perch.Avalonia.Views;

/// <summary>
/// A pill button that wears the Perch Wrapped poster's gradient (indigo → violet → magenta), so it
/// advertises the feature it launches — the Avalonia port of the WinForms <c>GradientButton</c>. Built
/// on a <see cref="Border"/> rather than a themed <see cref="Button"/> so the Fluent theme can't
/// override the gradient fill on hover; brightens on hover via a sheen overlay and dims when disabled.
/// The label is a colour-emoji sparkle plus body text.
/// </summary>
internal sealed class GradientButton : Border
{
    private readonly Border _sheen;
    private bool _enabled = true;

    public event Action? Click;

    public GradientButton(string emoji, string label)
    {
        Height = 28;
        CornerRadius = new CornerRadius(14);
        ClipToBounds = true;
        Cursor = new Cursor(StandardCursorType.Hand);
        BorderBrush = new SolidColorBrush(Color.FromArgb(70, 255, 255, 255));
        BorderThickness = new Thickness(1);

        var stops = WrappedPoster.BackdropStops;
        Background = new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
            EndPoint = new RelativePoint(1, 0, RelativeUnit.Relative),
            GradientStops =
            {
                new GradientStop(stops[0], 0),
                new GradientStop(stops[1], 0.5),
                new GradientStop(stops[2], 1),
            },
        };

        var content = new StackPanel
        {
            Orientation = Orientation.Horizontal, Spacing = 4,
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
            Children =
            {
                new TextBlock
                {
                    Text = emoji, FontSize = 12, VerticalAlignment = VerticalAlignment.Center,
                    FontFamily = new FontFamily("Segoe UI Emoji, Apple Color Emoji, Noto Color Emoji"),
                },
                new TextBlock
                {
                    Text = label, FontSize = 12, FontWeight = FontWeight.SemiBold,
                    Foreground = Brushes.White, VerticalAlignment = VerticalAlignment.Center,
                },
            },
        };

        // A brightening sheen on hover, sitting above the gradient but below the label.
        _sheen = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(38, 255, 255, 255)),
            IsVisible = false,
        };

        Child = new Grid { Children = { _sheen, content } };

        PointerEntered += (_, _) => { if (_enabled) _sheen.IsVisible = true; };
        PointerExited += (_, _) => _sheen.IsVisible = false;
        PointerReleased += (_, e) =>
        {
            if (_enabled && e.InitialPressMouseButton == MouseButton.Left) Click?.Invoke();
        };
    }

    /// <summary>Enables/disables the button — disabled dims toward the toolbar and ignores clicks.</summary>
    public bool Enabled
    {
        get => _enabled;
        set
        {
            _enabled = value;
            Opacity = value ? 1.0 : 0.45;
            if (!value) _sheen.IsVisible = false;
            Cursor = new Cursor(value ? StandardCursorType.Hand : StandardCursorType.Arrow);
        }
    }
}
