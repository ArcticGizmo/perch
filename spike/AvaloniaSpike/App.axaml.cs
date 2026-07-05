using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Platform;

namespace AvaloniaSpike;

public partial class App : Application
{
    private OverlayWindow? _overlay;

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            // Tray app: don't quit when the overlay is closed — only the tray "Exit" shuts down.
            desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;

            _overlay = new OverlayWindow();
            _overlay.Show();

            SetUpTray(desktop);
        }

        base.OnFrameworkInitializationCompleted();
    }

    // Proves TrayIcon + NativeMenu on Windows: left-click toggles the overlay, right-click shows the menu.
    private void SetUpTray(IClassicDesktopStyleApplicationLifetime desktop)
    {
        var icon = new WindowIcon(AssetLoader.Open(new Uri("avares://AvaloniaSpike/icon.ico")));

        var showItem = new NativeMenuItem("Show overlay");
        showItem.Click += (_, _) => ToggleOverlay();

        var exitItem = new NativeMenuItem("Exit");
        exitItem.Click += (_, _) => desktop.Shutdown();

        var tray = new TrayIcon
        {
            Icon = icon,
            ToolTipText = "Perch (Avalonia spike)",
            Menu = new NativeMenu { showItem, exitItem },
        };
        tray.Clicked += (_, _) => ToggleOverlay();

        TrayIcon.SetIcons(this, new TrayIcons { tray });
    }

    private void ToggleOverlay()
    {
        if (_overlay is null) return;
        if (_overlay.IsVisible) _overlay.Hide();
        else { _overlay.Show(); _overlay.Activate(); }
    }
}
