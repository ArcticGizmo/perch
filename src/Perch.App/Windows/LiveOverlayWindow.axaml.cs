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

    // Docked mode re-derives its column geometry from these two raw messages, which the OS delivers to every
    // real top-level window (ours is one). Hooking them is fully event-driven — no polling — and covers
    // everything that moves the column: WM_DISPLAYCHANGE for resolution / monitor add-remove / DPI, and
    // WM_SETTINGCHANGE with wParam == SPI_SETWORKAREA for a taskbar resize or move. We hook the raw messages
    // because Avalonia's Screens.Changed proved unreliable here — a resolution change didn't raise it, leaving
    // the column sized to the old work area (drooping under the taskbar). The field holds the delegate so it
    // isn't GC'd while registered.
    private const uint WM_DISPLAYCHANGE = 0x007E;
    private const uint WM_SETTINGCHANGE = 0x001A;
    private const uint SPI_SETWORKAREA  = 0x002F;
    private global::Avalonia.Controls.Win32Properties.CustomWndProcHookCallback? _wndProcHook;

    // Design-time / XAML-loader ctor. The app uses the canvas-taking overload below.
    public LiveOverlayWindow() : this(new OverlayCanvas()) { }

    public LiveOverlayWindow(OverlayCanvas canvas)
    {
        InitializeComponent();
        Canvas = canvas;
        Canvas.OwnerWindow = this; // the canvas reaches Position / Screens / BeginMoveDrag through this
        Content = canvas;

        // The canvas remembers a user drag corner-relative so display changes and dense toggles restore it.
        // A drag moves the window through the OS, not our code, so the window's own PositionChanged is the
        // reliable signal — it fires for the drag's final spot regardless of whether BeginMoveDrag blocks.
        PositionChanged += (_, _) => Canvas.OnWindowPositionChanged();

        // Borderless, transparent, manually-placed chrome. (In Avalonia 12 the decorations enum is
        // only reachable in code, so it's set here rather than in XAML.)
        WindowDecorations = WindowDecorations.None;
        TransparencyLevelHint = [WindowTransparencyLevel.Transparent];
        WindowStartupLocation = WindowStartupLocation.Manual;
    }

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);

        // Position at the user-defined initial placement if one was set (see SetInitialPlacements),
        // otherwise the default top-right of the primary screen's work area (below any top-docked bar),
        // matching the WinForms overlay's default float. The canvas owns floating placement so the initial
        // spot and the undock re-anchoring stay in one place.
        Canvas.PlaceAtInitialFloating();

        // No Alt+Tab entry and never take activation (showing must not steal focus from the terminal).
        if (TryGetPlatformHandle() is { } handle)
            PlatformServices.WindowChrome.MakeToolWindowNoActivate(handle.Handle);

        // Low-latency display-change signal for docked mode. Win32Properties lives in Avalonia.Controls (both
        // heads compile it); it only does anything on the Win32 backend, so gate on the OS. The hook returns
        // zero and leaves `handled` false so Avalonia's own WndProc still runs.
        if (OperatingSystem.IsWindows())
        {
            _wndProcHook = (IntPtr _, uint msg, IntPtr wParam, IntPtr _, ref bool _) =>
            {
                if (msg == WM_DISPLAYCHANGE || (msg == WM_SETTINGCHANGE && (uint)wParam == SPI_SETWORKAREA))
                    Canvas.OnDisplayChanged();
                return IntPtr.Zero;
            };
            global::Avalonia.Controls.Win32Properties.AddWndProcHookCallback(this, _wndProcHook);
        }
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);
}
