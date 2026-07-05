using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Platform;
using Avalonia.Threading;
using Perch.Avalonia.Services;
using Perch.Avalonia.Windows;
using Perch.Data;
using Perch.Platform;

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
    private StatsWindow? _statsWindow;
    private FlightPathWindow? _flightWindow;
    private HistoryWindow? _historyWindow;
    private AppSettings? _appSettings;
    private IClassicDesktopStyleApplicationLifetime? _desktop;

    // Auto-close after the last session ends (only for an --autostarted tray with the setting on). The
    // overlay shows a depleting bar for this grace period; if still no sessions when it elapses, exit.
    private const int AutoCloseGraceMs = 20_000;
    private DispatcherTimer? _autoCloseTimer;
    private bool _seenSession;
    private int _lastSessionCount;
    private IReadOnlyList<ClaudeSession> _lastSessions = [];
    private IGlobalHotkey? _hotkey;

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            _desktop = desktop;
            desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown; // tray app — outlives its windows
            desktop.ShutdownRequested += (_, _) =>
            {
                _monitorHost?.Dispose();
                _usageHost?.Dispose();
                _metricsHost?.Dispose();
                _hotkey?.Dispose();
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
                _lastSessions = sessions;
                _overlay!.Canvas.Update(sessions);
                _metricsHost!.SetSessionPids(sessions.Select(s => s.Pid));
                if (_historyWindow is { } h) h.SetActiveSessions(sessions);
                UpdateGlow();
                MaybeHandleAutoClose(sessions.Count);
            });

            // One-shot grace timer: fires AutoCloseGraceMs after the last session ends; if still none by
            // then, an auto-started tray exits. Armed/cancelled from the scan callback above.
            _autoCloseTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(AutoCloseGraceMs) };
            _autoCloseTimer.Tick += (_, _) =>
            {
                _autoCloseTimer!.Stop();
                if (_monitorHost is not null && _lastSessionCount == 0) desktop.Shutdown();
            };

            // A session finishing or blocking flashes the overlay's attention chase-border (and expands
            // it if collapsed). A finish also spends any armed confetti. The balloon/chime/external push
            // are Phase-5 notification concerns.
            _monitorHost.NeedsAttention += OnNeedsAttention;
            _monitorHost.AwaitingInput += _ => _overlay!.Canvas.TriggerAttention();

            // Row click focuses the session's terminal; artifact click opens the artifact(s).
            _overlay.Canvas.SessionActivated += FocusSession;
            _overlay.Canvas.ArtifactActivated += OpenArtifacts;

            // Right-click context menu. The strip toggles persist and apply live; Exit shuts the app
            // down. History / QR / external-notify / confetti are Phase-5 concerns — their triggers are
            // wired here so the menu is complete, with best-effort/stub handlers until those windows land.
            _overlay.Canvas.ExitRequested += () => desktop.Shutdown();
            _overlay.Canvas.SystemMetricsToggleRequested += SetSystemMetricsEnabled;
            _overlay.Canvas.UsageToggleRequested += SetUsageEnabled;
            _overlay.Canvas.HistoryRequested += OpenHistory;
            _overlay.Canvas.QrRequested += ShowQrCode;
            _overlay.Canvas.ExternalNotifyToggleRequested += OnToggleExternalNotify;
            _overlay.DragCompleted += OnOverlayDragCompleted;

            // Quick-links strip: launch/focus goes through the platform seams; icons resolve off-thread.
            var settings = AppSettings.Load();
            _appSettings = settings;
            _quickLinkLauncher = new QuickLinkLauncher(PlatformServices.WindowActivator, PlatformServices.AppIconProvider);
            _overlay.Canvas.QuickLinkActivated += _quickLinkLauncher.LaunchOrFocus;

            // Drive every overlay display gate + the monitor's data-layer toggles from persisted settings
            // (the Phase-3 Settings UI will edit these; this reads whatever's on disk, defaults included).
            ApplyDisplaySettings(settings);

            _overlay.Show();
            _metricsHost.Configure(system: settings.ShowSystemMetrics, perSession: settings.ShowSessionMetrics, subprocess: true);
            _monitorHost.Start(); // initial scan (we're on the UI thread here) — also sets the pids
            if (settings.ShowUsage) _usageHost.Start(); // initial usage fetch (polls every 5 min thereafter)
            LoadQuickLinks(settings);

            // Global hotkey (Alt+Shift+W): toggle the overlay's visibility from anywhere. The callback
            // fires on the hotkey's own thread, so hop to the UI thread. A refused binding is ignored.
            _hotkey = PlatformServices.CreateGlobalHotkey();
            _hotkey.Register(HotkeyModifiers.Alt | HotkeyModifiers.Shift, 'W',
                () => Dispatcher.UIThread.Post(ToggleOverlay));
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

    // Auto-close: only an --autostarted tray with the setting on ever closes itself, so a manually
    // opened window never vanishes under the user. Once at least one session has been seen, dropping
    // back to zero arms the grace timer (and the overlay's depleting bar); any session reappearing
    // cancels both. The setting is read live, so toggling it takes effect immediately.
    private void MaybeHandleAutoClose(int sessionCount)
    {
        _lastSessionCount = sessionCount;
        if (_overlay is null || _autoCloseTimer is null) return;

        if (!Program.AutoStarted || _appSettings is not { AutoCloseAfterLastSession: true })
        {
            _autoCloseTimer.Stop();
            _overlay.Canvas.CancelAutoCloseCountdown();
            return;
        }

        if (sessionCount > 0)
        {
            _seenSession = true;
            _autoCloseTimer.Stop();
            _overlay.Canvas.CancelAutoCloseCountdown();
            return;
        }

        // Zero sessions: hold off until we've actually seen one (don't exit during the startup race).
        if (!_seenSession) return;

        // Leave an already-running countdown alone. The scan callback re-enters on every scan (not just
        // on a real change), so re-arming each time would reset the grace period so it never elapsed —
        // the countdown must measure time since sessions actually hit zero.
        if (_autoCloseTimer.IsEnabled) return;

        _autoCloseTimer.Start();
        _overlay.Canvas.StartAutoCloseCountdown(AutoCloseGraceMs);
    }

    // Applies every persisted overlay display gate to the canvas and the monitor's data-layer toggles,
    // so the overlay honours the user's settings from the first frame. Mirrors the block the WinForms
    // OverlayApplicationContext runs at startup; the Phase-3 Settings UI drives the same setters live.
    private void ApplyDisplaySettings(AppSettings s)
    {
        if (_overlay is null) return;
        var c = _overlay.Canvas;

        c.SetShowUsage(s.ShowUsage);
        c.SetShowExpectedRate(s.ShowExpectedUsageRate);
        c.SetShowSystemMetrics(s.ShowSystemMetrics);
        c.SetShowSessionMetrics(s.ShowSessionMetrics);
        c.SetShowContextPressure(s.ShowContextPressure);
        c.SetShowContextGreenSegment(s.ShowContextGreenSegment);
        c.SetContextThresholds(s.ContextPressureYellowPercent, s.ContextPressureOrangePercent, s.ContextPressureRedPercent);
        c.SetShowModeBadges(s.ShowPermissionModeBadges);
        c.SetShowTaskProgress(s.ShowTaskProgress);
        c.SetShowBurnRate(s.ShowBurnRate);
        c.SetShowGitStats(s.ShowGitStats);
        c.SetStuckDetectionEnabled(s.StuckDetectionEnabled);
        c.SetShowWaitingTimer(s.ShowWaitingTimer);
        c.SetWaitingTimerRedMinutes(s.WaitingTimerRedMinutes);
        c.SetShowArtifacts(s.ShowArtifacts);
        c.SetHideInactiveTeamMembers(s.HideInactiveTeamMembers);
        c.SetUpsideDownQuickLinks(s.UpsideDownQuickLinks);
        c.SetConfettiFinishAvailable(s.ConfettiFinish);
        c.SetExternalNotificationsAvailable(s.ExternalNotificationsEnabled);

        // Data-layer sources for the git chip / stuck glyph (off in the monitor unless enabled here).
        if (_monitorHost is not null)
        {
            _monitorHost.GitStatsEnabled = s.ShowGitStats;
            _monitorHost.StuckDetectionEnabled = s.StuckDetectionEnabled;
        }
    }

    // A session finished (NeedsAttention): flash the overlay, and if it was armed for a confetti finish,
    // spend the arming and set off the celebration on the overlay's current screen.
    private ConfettiWindow? _confetti;
    private void OnNeedsAttention(ClaudeSession session)
    {
        _overlay!.Canvas.TriggerAttention();
        if (_overlay.Canvas.ConsumeConfetti(session.SessionId))
            LaunchConfetti();
    }

    private void LaunchConfetti()
    {
        if (_overlay is null) return;
        var screen = _overlay.Screens.ScreenFromWindow(_overlay) ?? _overlay.Screens.Primary;
        if (screen is null) return;
        (_confetti ??= new ConfettiWindow()).Launch(screen);
    }

    // The overlay was dragged to a (possibly different) monitor — re-home the glow onto its screen.
    private void OnOverlayDragCompleted() => UpdateGlow();

    // Shows/hides the ambient screen-edge glow from the current session state: lit while any session
    // needs attention or is awaiting input (and the ScreenEdgeGlow setting is on), around the overlay's
    // screen in the attention orange; hidden otherwise. Called after every scan and on a drag, so it
    // self-corrects as sessions come and go and as the overlay moves between monitors.
    private GlowWindow? _glow;
    private void UpdateGlow()
    {
        if (_overlay is null) return;
        bool on = _appSettings is { ScreenEdgeGlow: true }
            && _lastSessions.Any(s => s.Status is SessionStatus.NeedsAttention or SessionStatus.AwaitingInput);

        if (!on) { _glow?.HideGlow(); return; }

        var screen = _overlay.Screens.ScreenFromWindow(_overlay) ?? _overlay.Screens.Primary;
        if (screen is null) return;
        (_glow ??= new GlowWindow()).ShowGlow(screen, Theming.Palette.Orange);
    }

    // ── Context-menu handlers ─────────────────────────────────────────────────
    // Toggle the whole-machine metrics strip: persist the choice and apply it live. (4.17 will read the
    // persisted value back at startup; for now the canvas defaults the strip on.)
    private void SetSystemMetricsEnabled(bool enabled)
    {
        if (_appSettings is null || _overlay is null) return;
        if (_appSettings.ShowSystemMetrics == enabled) return;
        _appSettings.ShowSystemMetrics = enabled;
        _appSettings.Save();
        _overlay.Canvas.SetShowSystemMetrics(enabled);
    }

    // Toggle the account-usage strip: persist the choice and apply it live.
    private void SetUsageEnabled(bool enabled)
    {
        if (_appSettings is null || _overlay is null) return;
        if (_appSettings.ShowUsage == enabled) return;
        _appSettings.ShowUsage = enabled;
        _appSettings.Save();
        _overlay.Canvas.SetShowUsage(enabled);
    }

    // ── Tray / overlay window openers (single reused instances via WindowHost) ─
    // "Session history…" (tray) or "View history" (overlay row) — opens/focuses the one viewer and, when
    // a session id is given, jumps to it. The list + transcript pane land in 5.7; the wiring is here now.
    private void OpenHistory(string? sessionId)
    {
        _historyWindow = WindowHost.ShowOrFocus(_historyWindow,
            () => new HistoryWindow(),
            () => _historyWindow = null,
            w =>
            {
                if (_monitorHost is not null) w.SetActiveSessions(_lastSessions);
                w.ShowSession(sessionId);
            });
    }

    private void OpenStats() =>
        _statsWindow = WindowHost.ShowOrFocus(_statsWindow, () => new StatsWindow(), () => _statsWindow = null);

    private void OpenFlightPath() =>
        _flightWindow = WindowHost.ShowOrFocus(_flightWindow, () => new FlightPathWindow(), () => _flightWindow = null);

    // "Show QR code" — a centred card with the session's remote-control deep-link QR. Only one is shown
    // at a time; opening another (or clicking away) closes the previous.
    private QrWindow? _qrWindow;
    private void ShowQrCode(ClaudeSession session)
    {
        if (string.IsNullOrEmpty(session.BridgeSessionId)) return;
        _qrWindow?.Close();
        _qrWindow = new QrWindow(session.DisplayName, $"https://claude.ai/code/{session.BridgeSessionId}");
        _qrWindow.Closed += (_, _) => _qrWindow = null;
        _qrWindow.Show();
        _qrWindow.Activate();
    }

    // "Enable/Disable external notifications" — the ntfy pipeline isn't ported yet (no NotificationService
    // on the Avalonia head), so the menu item stays hidden (availability defaults off). Stub the trigger.
    private static void OnToggleExternalNotify(string sessionId)
    {
        // TODO(phase5): toggle the session's external-notify marker file, then rescan.
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

        var historyItem = new NativeMenuItem("Session history…");
        historyItem.Click += (_, _) => OpenHistory(null);

        var statsItem = new NativeMenuItem("Session stats…");
        statsItem.Click += (_, _) => OpenStats();

        var flightItem = new NativeMenuItem("Flight path…");
        flightItem.Click += (_, _) => OpenFlightPath();

        var exitItem = new NativeMenuItem("Exit");
        exitItem.Click += (_, _) => desktop.Shutdown();

        var tray = new TrayIcon
        {
            Icon = icon,
            ToolTipText = "Perch",
            Menu = new NativeMenu
            {
                overlayItem,
                new NativeMenuItemSeparator(),
                settingsItem,
                historyItem,
                statsItem,
                flightItem,
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
