using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Platform;
using Perch.Avalonia.Services;
using Perch.Avalonia.Windows;

namespace Perch.Avalonia;

/// <summary>
/// The Avalonia application shell: dark Fluent theme, the system-tray icon + menu, the live overlay,
/// and lazy single-reused windows — the Avalonia counterpart of the WinForms
/// <c>OverlayApplicationContext</c>. The overlay is driven by a <see cref="SessionMonitorHost"/> over
/// Perch.Core, so the app shows live sessions end-to-end. Only the tray's Exit quits the app.
/// </summary>
public partial class App : Application
{
    private SessionMonitorHost? _monitorHost;
    private UsageMonitorHost? _usageHost;
    private LiveOverlayWindow? _overlay;
    private SettingsWindow? _settings;

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown; // tray app — outlives its windows
            desktop.ShutdownRequested += (_, _) => { _monitorHost?.Dispose(); _usageHost?.Dispose(); };

            SetUpTray(desktop);

            // Live overlay + the data pipelines that feed it. Both hosts deliver on the UI thread, so
            // feeding the owner-drawn canvas from their callbacks is UI-thread-safe.
            _overlay = new LiveOverlayWindow();
            _monitorHost = new SessionMonitorHost(_overlay.Canvas.Update);
            _usageHost = new UsageMonitorHost(_overlay.Canvas.UpdateUsage);
            _overlay.Show();
            _monitorHost.Start(); // initial scan (we're on the UI thread here)
            _usageHost.Start();   // initial usage fetch (polls every 5 min thereafter)
        }
        base.OnFrameworkInitializationCompleted();
    }

    private void SetUpTray(IClassicDesktopStyleApplicationLifetime desktop)
    {
        var icon = new WindowIcon(AssetLoader.Open(new Uri("avares://perch-avalonia/Assets/icon.ico")));

        var overlayItem = new NativeMenuItem("Show / hide overlay");
        overlayItem.Click += (_, _) => ToggleOverlay();

        var settingsItem = new NativeMenuItem("Settings…");
        settingsItem.Click += (_, _) => OpenSettings();

        var exitItem = new NativeMenuItem("Exit");
        exitItem.Click += (_, _) => desktop.Shutdown();

        var tray = new TrayIcon
        {
            Icon = icon,
            ToolTipText = "Perch",
            Menu = new NativeMenu
            {
                overlayItem,
                settingsItem,
                new NativeMenuItemSeparator(),
                exitItem,
            },
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

    // Lazily create-or-focus the single Settings window instance.
    private void OpenSettings()
    {
        if (_settings is { } w && w.IsVisible)
        {
            w.Activate();
            return;
        }
        _settings = new SettingsWindow();
        _settings.Closed += (_, _) => _settings = null;
        _settings.Show();
        _settings.Activate();
    }
}
