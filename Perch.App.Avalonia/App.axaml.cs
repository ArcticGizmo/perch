using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Platform;
using Perch.Avalonia.Windows;

namespace Perch.Avalonia;

/// <summary>
/// The Avalonia application shell: dark Fluent theme, the system-tray icon + menu, and lazy,
/// single-reused top-level windows — the Avalonia counterpart of the WinForms
/// <c>OverlayApplicationContext</c>. Windows are created on demand and reused while open (the
/// "single reused window" idiom from CLAUDE.md), and the app only quits on the tray's Exit.
/// </summary>
public partial class App : Application
{
    private SettingsWindow? _settings;

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown; // tray app — outlives its windows
            SetUpTray(desktop);
        }
        base.OnFrameworkInitializationCompleted();
    }

    private void SetUpTray(IClassicDesktopStyleApplicationLifetime desktop)
    {
        var icon = new WindowIcon(AssetLoader.Open(new Uri("avares://perch-avalonia/Assets/icon.ico")));

        var settingsItem = new NativeMenuItem("Settings…");
        settingsItem.Click += (_, _) => OpenSettings();

        var exitItem = new NativeMenuItem("Exit");
        exitItem.Click += (_, _) => desktop.Shutdown();

        var tray = new TrayIcon
        {
            Icon = icon,
            ToolTipText = "Perch",
            Menu = new NativeMenu { settingsItem, new NativeMenuItemSeparator(), exitItem },
        };
        // Left-click opens settings (parity with the WinForms tray).
        tray.Clicked += (_, _) => OpenSettings();

        TrayIcon.SetIcons(this, new TrayIcons { tray });
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
