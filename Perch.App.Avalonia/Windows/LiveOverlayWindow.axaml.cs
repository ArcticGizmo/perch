using Avalonia;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Perch.Avalonia.ViewModels;

namespace Perch.Avalonia.Windows;

/// <summary>
/// The live floating overlay: a transparent, borderless, always-on-top, no-taskbar window hosting the
/// <c>OverlayView</c> bound to the live session list. This is the seed the full Phase-4 owner-drawn
/// overlay grows from; row-click focus and header-drag live in OverlayView.
/// </summary>
public partial class LiveOverlayWindow : Window
{
    // Design-time / XAML-loader ctor. The app always uses the VM-taking overload below.
    public LiveOverlayWindow() : this(new OverlayViewModel()) { }

    public LiveOverlayWindow(OverlayViewModel vm)
    {
        InitializeComponent();
        DataContext = vm; // inherited by the hosted OverlayView

        // Borderless, transparent, manually-placed chrome. (In Avalonia 12 the decorations enum is
        // only reachable in code, so it's set here rather than in XAML.)
        WindowDecorations = WindowDecorations.None;
        TransparencyLevelHint = [WindowTransparencyLevel.Transparent];
        WindowStartupLocation = WindowStartupLocation.Manual;
        Position = new PixelPoint(60, 60);
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);
}
