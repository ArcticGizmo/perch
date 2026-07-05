using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Platform;
using Avalonia.Threading;
using Perch.Avalonia.Services;
using Perch.Avalonia.Windows;
using Perch.Data;

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
    private MetricsMonitorHost? _metricsHost;
    private QuickLinkLauncher? _quickLinkLauncher;
    private LiveOverlayWindow? _overlay;
    private SettingsWindow? _settings;

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown; // tray app — outlives its windows
            desktop.ShutdownRequested += (_, _) =>
            {
                _monitorHost?.Dispose();
                _usageHost?.Dispose();
                _metricsHost?.Dispose();
            };

            SetUpTray(desktop);

            // Live overlay + the data pipelines that feed it. Every host delivers on the UI thread, so
            // feeding the owner-drawn canvas from their callbacks is UI-thread-safe.
            _overlay = new LiveOverlayWindow();
            _usageHost = new UsageMonitorHost(_overlay.Canvas.UpdateUsage);
            _metricsHost = new MetricsMonitorHost(PlatformServices.SystemMetrics,
                _overlay.Canvas.UpdateSystemMetrics, _overlay.Canvas.UpdateSessionMetrics);

            // Each scan feeds both the canvas and the metrics sampler (which pids to measure).
            _monitorHost = new SessionMonitorHost(sessions =>
            {
                _overlay!.Canvas.Update(sessions);
                _metricsHost!.SetSessionPids(sessions.Select(s => s.Pid));
            });

            // Row click focuses the session's terminal; artifact click opens the artifact(s).
            _overlay.Canvas.SessionActivated += FocusSession;
            _overlay.Canvas.ArtifactActivated += OpenArtifacts;

            // Quick-links strip: launch/focus goes through the platform seams; icons resolve off-thread.
            var settings = AppSettings.Load();
            _quickLinkLauncher = new QuickLinkLauncher(PlatformServices.WindowActivator, PlatformServices.AppIconProvider);
            _overlay.Canvas.QuickLinkActivated += _quickLinkLauncher.LaunchOrFocus;
            _overlay.Canvas.SetUpsideDownQuickLinks(settings.UpsideDownQuickLinks);

            _overlay.Show();
            // Sampling defaults for the port (4.17 drives these from Settings): both strips on, rolled
            // up over each session's process tree.
            _metricsHost.Configure(system: true, perSession: true, subprocess: true);
            _monitorHost.Start(); // initial scan (we're on the UI thread here) — also sets the pids
            _usageHost.Start();   // initial usage fetch (polls every 5 min thereafter)
            LoadQuickLinks(settings);
        }
        base.OnFrameworkInitializationCompleted();
    }

    // Focuses the terminal hosting a clicked session (sub-agent rows already resolve to their parent).
    private static void FocusSession(ClaudeSession session)
    {
        if (int.TryParse(session.Pid, out int pid))
            PlatformServices.WindowActivator.FocusTerminalForProcess(pid, session.ProjectName);
    }

    // Opens a clicked row's artifact(s) in the browser. Single artifact opens directly; the multi-artifact
    // picker popover is a Phase-5 UI concern, so for now the first is opened.
    private static void OpenArtifacts(ClaudeSession session)
    {
        var artifacts = session.Artifacts;
        if (artifacts.Count == 0) return;
        var url = artifacts[0].Url;
        if (!string.IsNullOrWhiteSpace(url))
        {
            try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true }); }
            catch { /* best-effort */ }
        }
    }

    // Resolves the enabled links' icons off the UI thread (the first shell lookup enumerates the Start
    // Menu, ~1s), then applies them on the UI thread. Icons come back as PNG file paths from the seam.
    private void LoadQuickLinks(AppSettings settings)
    {
        var links = (settings.QuickLinks ?? []).Where(l => l.Enabled).ToList();
        if (links.Count == 0 || _quickLinkLauncher is null) return;

        var launcher = _quickLinkLauncher;
        System.Threading.Tasks.Task.Run(() =>
        {
            var icons = links.Select(l => launcher.IconFile(l, 32)).ToList();
            Dispatcher.UIThread.Post(() => _overlay?.Canvas.SetQuickLinks(links, icons));
        });
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
