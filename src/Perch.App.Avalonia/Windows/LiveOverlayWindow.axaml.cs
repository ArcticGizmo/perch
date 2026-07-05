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
    // The overlay floats this far below the work-area top when auto-positioned (mirrors OverlayForm).
    private const int FloatTopGap = 32;
    private const int RightMargin = 16;

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

        // The canvas owns dragging (header = drag handle) via BeginMoveDrag; it raises DragCompleted when
        // a move ends so the app can follow with the ambient glow.
    }

    /// <summary>Raised when the user finishes dragging the overlay, so the app can re-evaluate anything
    /// tied to the overlay's screen — chiefly moving the screen-edge glow to the monitor it now sits on.</summary>
    public event Action? DragCompleted
    {
        add    => Canvas.DragCompleted += value;
        remove => Canvas.DragCompleted -= value;
    }

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);

        // Auto-position at the top-right of the primary screen's work area (below any top-docked bar),
        // matching the WinForms overlay's default float. Positions are in physical pixels, so scale the
        // DIP width/gaps by the screen's DPI.
        var screen = Screens.Primary ?? (Screens.All.Count > 0 ? Screens.All[0] : null);
        if (screen is not null)
        {
            var wa = screen.WorkingArea;
            double scale = screen.Scaling;
            int w = (int)(Width * scale);
            Position = new PixelPoint(
                wa.X + wa.Width - w - (int)(RightMargin * scale),
                wa.Y + (int)(FloatTopGap * scale));
        }

        // No Alt+Tab entry and never take activation (showing must not steal focus from the terminal).
        if (OperatingSystem.IsWindows() && TryGetPlatformHandle() is { } handle)
            Perch.Platform.Windows.OverlayNativeChrome.MakeToolWindowNoActivate(handle.Handle);
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);
}
