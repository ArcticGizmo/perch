using Avalonia;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Perch.Avalonia.Views;

namespace Perch.Avalonia.Windows;

/// <summary>
/// The live floating overlay: a transparent, borderless, always-on-top, no-taskbar window hosting the
/// owner-drawn <see cref="OverlayCanvas"/>. The window supplies chrome + position; the canvas supplies
/// all painting. Feed sessions via <see cref="Canvas"/>.<c>Update(...)</c>.
/// </summary>
public partial class LiveOverlayWindow : Window
{
    public OverlayCanvas Canvas { get; }

    // Design-time / XAML-loader ctor. The app uses the canvas-taking overload below.
    public LiveOverlayWindow() : this(new OverlayCanvas()) { }

    public LiveOverlayWindow(OverlayCanvas canvas)
    {
        InitializeComponent();
        Canvas = canvas;
        Content = canvas;

        // Borderless, transparent, manually-placed chrome. (In Avalonia 12 the decorations enum is
        // only reachable in code, so it's set here rather than in XAML.)
        WindowDecorations = WindowDecorations.None;
        TransparencyLevelHint = [WindowTransparencyLevel.Transparent];
        WindowStartupLocation = WindowStartupLocation.Manual;
        Position = new PixelPoint(60, 60);
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);
}
