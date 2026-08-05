using Avalonia;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Perch.Avalonia.Views;

namespace Perch.Avalonia.Windows;

/// <summary>
/// The live floating overlay: a transparent, borderless, always-on-top, no-taskbar, no-Alt+Tab,
/// no-activate window hosting the owner-drawn <see cref="OverlayCanvas"/>. The window supplies chrome +
/// position; the canvas supplies all painting. Feed sessions via <see cref="Canvas"/>.<c>Update(...)</c>.
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
        Canvas.OwnerWindow = this; // the canvas reaches Position / Screens / BeginMoveDrag through this
        Content = canvas;

        // Borderless, transparent, manually-placed chrome. (In Avalonia 12 the decorations enum is
        // only reachable in code, so it's set here rather than in XAML.)
        WindowDecorations = WindowDecorations.None;
        TransparencyLevelHint = [WindowTransparencyLevel.Transparent];
        WindowStartupLocation = WindowStartupLocation.Manual;
    }

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);

        // Auto-position at the top-right of the primary screen's work area (below any top-docked bar),
        // matching the WinForms overlay's default float. The canvas owns floating placement so the initial
        // spot and the undock re-anchoring stay in one place.
        Canvas.PlaceAtDefaultFloating();

        // No Alt+Tab entry and never take activation (showing must not steal focus from the terminal).
        if (TryGetPlatformHandle() is { } handle)
            PlatformServices.WindowChrome.MakeToolWindowNoActivate(handle.Handle);
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);
}
