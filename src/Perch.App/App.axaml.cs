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

    // Notifications: the notifier (real Windows Action Center toasts, or the owner-drawn fallback off
    // Windows) + the toolkit-neutral dispatcher over it, plus the session-lock seam the dispatcher reads
    // for the AFK-lock external override.
    private INotifier? _notifier;
    private NotificationService? _notifications;
    private ISessionLock? _sessionLock;

    // The in-app updater (startup + hourly GitHub check, and the user-initiated apply). Its
    // AvailabilityChanged event lights up the tray item, the overlay badge and any open Settings window.
    private UpdateService? _updateService;
    private NativeMenuItem? _updateItem;

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
                _sessionLock?.Dispose();
                _overlay?.Canvas.DisposeDense();
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
            // it if collapsed). A finish also spends any armed confetti. Both fire the desktop
            // toast + chime + external push (gated per settings) via the notification dispatcher.
            _monitorHost.NeedsAttention += OnNeedsAttention;
            _monitorHost.AwaitingInput += OnAwaitingInput;
            _monitorHost.OpenHistoryRequested += OpenHistory; // the plugin's jump-to-session

            // Row click focuses the session's terminal; the artifact glyph always pops a picker list, and
            // the chosen artifact is opened here.
            _overlay.Canvas.SessionActivated += FocusSession;
            _overlay.Canvas.ArtifactChosen += OpenArtifact;

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

            // Notifications: real Windows Action Center toasts (owner-drawn fallback off Windows); the
            // dispatcher gates toast/chime/external per settings. A toast click focuses that terminal and
            // acknowledges it.
            _sessionLock = PlatformServices.CreateSessionLock();
            _notifier = OperatingSystem.IsWindows()
                ? new Notifications.WindowsToastNotifier()
                : new Notifications.AvaloniaToastNotifier(
                    () => _overlay is null ? null : _overlay.Screens.ScreenFromWindow(_overlay) ?? _overlay.Screens.Primary);
            _notifier.SessionActivated += OnToastActivated;
            _notifications = new NotificationService(_notifier, settings, _sessionLock, PlatformServices.AudioCue);

            // In-app updater: reflect availability on the tray item, the overlay badge and any open
            // Settings window. The overlay's badge click and the tray/Settings actions all route here.
            _updateService = new UpdateService(settings, _notifications);
            _updateService.AvailabilityChanged += OnUpdateAvailabilityChanged;
            _overlay.Canvas.UpdateRequested += () => _updateService!.PerformUpdate(CloseAuxWindows);

            // Drive every overlay display gate + the monitor's data-layer toggles from persisted settings
            // (the Phase-3 Settings UI will edit these; this reads whatever's on disk, defaults included).
            ApplyDisplaySettings(settings);

            _overlay.Show();
            _metricsHost.Configure(system: settings.ShowSystemMetrics, perSession: settings.ShowSessionMetrics, subprocess: settings.IncludeSubprocessMetrics);
            _monitorHost.Start(); // initial scan (we're on the UI thread here) — also sets the pids
            if (settings.ShowUsage) _usageHost.Start(); // initial usage fetch (polls every 5 min thereafter)
            ReloadQuickLinks(settings);

            // Global hotkey (Alt+Shift+W): toggle dense mode from anywhere (dense replaces the old
            // show/hide). The callback fires on the hotkey's own thread, so hop to the UI thread.
            _hotkey = PlatformServices.CreateGlobalHotkey();
            _hotkey.Register(HotkeyModifiers.Alt | HotkeyModifiers.Shift, 'W',
                () => Dispatcher.UIThread.Post(ToggleDense));

            // Re-dock the dense strip when monitors are added/removed (the controller self-heals to primary).
            if (_overlay.Screens is { } screens)
                screens.Changed += (_, _) => _overlay?.Canvas.OnScreensChanged();

            // Startup + hourly update check (restores a persisted "update available" state first).
            _updateService.Start();

            // First launch after an install: add the marketplace and install the Claude Code plugin in
            // the background so the user doesn't have to. Failures are silently skipped.
            if (Program.IsFirstRun)
                AutoInstallPlugin();
        }
        base.OnFrameworkInitializationCompleted();
    }

    // Fire-and-forget plugin install on first run. Shows a tray toast up front so the work is visible,
    // then a quiet success toast; any failure is swallowed (the user can still enable it from Settings).
    private async void AutoInstallPlugin()
    {
        var (marketplace, plugin) = PluginManager.ReadInstalledState();
        if (marketplace && plugin) return; // already set up from a previous machine state — skip the noise

        _notifications?.ShowInfo("Perch", "Setting up the Claude Code plugin…", ToastLevel.Info);
        try
        {
            var (ok, _) = await new PluginManager().EnableAsync();
            if (ok)
                _notifications?.ShowInfo("Perch",
                    "Claude Code plugin installed. Run /reload-plugins (or restart) in open sessions to load it.",
                    ToastLevel.Info);
        }
        catch { /* best-effort: skip on any failure */ }
    }

    // Reflects the updater's availability on every surface: the tray menu item's wording, the overlay's
    // header badge, and any open Settings window's About page. Fires on the UI thread.
    private void OnUpdateAvailabilityChanged(bool available, string? version)
    {
        if (_updateItem is not null)
            _updateItem.Header = available ? "Update available" : "Check for Updates…";
        _overlay?.Canvas.SetUpdateAvailable(available);
        _settings?.SetUpdateAvailable(available, version);
    }

    // Closes the auxiliary windows before an update applies (the closing windows signal the update is
    // under way and stop a button being clicked again mid-download). The overlay stays up so the app
    // survives the awaits — ApplyUpdatesAndRestart tears everything down when it relaunches.
    private void CloseAuxWindows()
    {
        _settings?.Close();
        _historyWindow?.Close();
        _statsWindow?.Close();
        _flightWindow?.Close();
        _qrWindow?.Close();
    }

    // Focuses the terminal hosting a clicked session (sub-agent rows already resolve to their parent) and
    // acknowledges it, so clicking a finished ("done"/NeedsAttention) session clears its badge — the
    // WinForms SessionFocused → AcknowledgeSession behaviour. Acknowledge is a no-op for a session that
    // isn't done, and rescans so the overlay refreshes.
    private void FocusSession(ClaudeSession session)
    {
        if (int.TryParse(session.Pid, out int pid))
            PlatformServices.WindowActivator.FocusTerminalForProcess(pid, session.ProjectName);
        _monitorHost?.Acknowledge(session.Pid);
    }

    // Opens the artifact the user picked from the overlay's artifact-glyph list.
    private static void OpenArtifact(Artifact artifact)
    {
        if (string.IsNullOrWhiteSpace(artifact.Url)) return;
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(artifact.Url) { UseShellExecute = true }); }
        catch { /* best-effort */ }
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

    // A session finished (NeedsAttention): flash the overlay, fire the notification (toast/chime/external,
    // gated per settings), and if it was armed for a confetti finish, spend the arming and set off the
    // celebration on the overlay's current screen.
    private ConfettiWindow? _confetti;
    private void OnNeedsAttention(ClaudeSession session)
    {
        _overlay!.Canvas.TriggerAttention();
        _notifications?.Notify(NotificationKind.Done, session);
        if (_overlay.Canvas.ConsumeConfetti(session.SessionId))
            LaunchConfetti();
    }

    // A session blocked awaiting input: flash the overlay and fire the "waiting for input" notification.
    private void OnAwaitingInput(ClaudeSession session)
    {
        _overlay!.Canvas.TriggerAttention();
        _notifications?.Notify(NotificationKind.WaitingForInput, session);
    }

    // A toast was clicked: focus the session's terminal and acknowledge it (clears the "done" badge) —
    // the Avalonia counterpart of the WinForms balloon-click handler.
    private void OnToastActivated(string pid, string? project)
    {
        if (int.TryParse(pid, out int p))
            PlatformServices.WindowActivator.FocusTerminalForProcess(p, project);
        _monitorHost?.Acknowledge(pid);
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
        _statsWindow = WindowHost.ShowOrFocus(_statsWindow,
            () => new StatsWindow(_appSettings ?? AppSettings.Load()), () => _statsWindow = null);

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

    // "Enable/Disable external notifications" — flips the session's marker file (the same source of truth
    // the plugin's /afk toggles) and rescans so the mail glyph + menu wording refresh. Whether external
    // pushes actually fire on this marker is the Phase-3 notification pipeline's job; the opt-in itself
    // is just this file, so the toggle is wired now.
    private void OnToggleExternalNotify(string sessionId) => _monitorHost?.ToggleExternalNotify(sessionId);

    // Applies the enabled links to the overlay strip, resolving their icons off the UI thread (the first
    // shell lookup enumerates the Start Menu, ~1s) then applying on the UI thread. Icons come back as PNG
    // file paths from the seam. Always sets the strip — an empty list clears it — so a link removed or
    // disabled in Settings disappears immediately; also syncs the upside-down flag.
    private void ReloadQuickLinks(AppSettings settings)
    {
        if (_overlay is null || _quickLinkLauncher is null) return;
        _overlay.Canvas.SetUpsideDownQuickLinks(settings.UpsideDownQuickLinks);

        var links = (settings.QuickLinks ?? []).Where(l => l.Enabled).ToList();
        if (links.Count == 0)
        {
            _overlay.Canvas.SetQuickLinks(links, new List<string?>());
            return;
        }

        var launcher = _quickLinkLauncher;
        System.Threading.Tasks.Task.Run(() =>
        {
            var icons = links.Select(l => launcher.IconFile(l, 32)).ToList();
            Dispatcher.UIThread.Post(() => _overlay?.Canvas.SetQuickLinks(links, icons));
        });
    }

    private void SetUpTray(IClassicDesktopStyleApplicationLifetime desktop)
    {
        var icon = new WindowIcon(AssetLoader.Open(new Uri("avares://perch/Assets/icon.ico")));

        // Read-only version indicator (dense mode is still toggled via the global hotkey Alt+Shift+W).
        var versionItem = new NativeMenuItem($"Perch - {AppInfo.Version}") { IsEnabled = false };

        var settingsItem = new NativeMenuItem("Settings…");
        settingsItem.Click += (_, _) => OpenSettings();

        var historyItem = new NativeMenuItem("Session history…");
        historyItem.Click += (_, _) => OpenHistory(null);

        var statsItem = new NativeMenuItem("Session stats…");
        statsItem.Click += (_, _) => OpenStats();

        var flightItem = new NativeMenuItem("Flight path…");
        flightItem.Click += (_, _) => OpenFlightPath();

        // Reads "Check for Updates…" normally; flips to "Update available" once a pending update is
        // detected (see OnUpdateAvailabilityChanged). Clicking it applies the pending update, else checks.
        _updateItem = new NativeMenuItem("Check for Updates…");
        _updateItem.Click += (_, _) =>
        {
            if (_updateService is { HasPendingUpdate: true }) _updateService.PerformUpdate(CloseAuxWindows);
            else _updateService?.CheckManual();
        };

        var exitItem = new NativeMenuItem("Exit");
        exitItem.Click += (_, _) => desktop.Shutdown();

        var tray = new TrayIcon
        {
            Icon = icon,
            ToolTipText = "Perch",
            Menu = new NativeMenu
            {
                versionItem,
                new NativeMenuItemSeparator(),
                settingsItem,
                historyItem,
                statsItem,
                flightItem,
                _updateItem,
                new NativeMenuItemSeparator(),
                exitItem,
            },
        };
        // Left-clicking the tray icon opens Settings (matching the WinForms tray); dense mode is toggled
        // via the global hotkey (Alt+Shift+W).
        tray.Clicked += (_, _) => OpenSettings();

        TrayIcon.SetIcons(this, new TrayIcons { tray });
    }

    // Dense mode replaces the old show/hide: the overlay is always on screen, shrinking to a slim
    // edge strip (that expands on hover) rather than hiding entirely.
    private void ToggleDense() => _overlay?.Canvas.ToggleDense();

    // Lazily create-or-focus the single Settings window instance. The window edits the shared
    // AppSettings and applies changes live through the hooks below — the Avalonia counterpart of the
    // WinForms OverlayApplicationContext's SettingsForm wiring.
    private void OpenSettings()
    {
        if (_settings is { } w && w.IsVisible)
        {
            w.Activate();
            return;
        }
        var settings = _appSettings ??= AppSettings.Load();
        var hooks = new SettingsHooks
        {
            DisplayChanged = () => ApplyDisplaySettings(settings),
            UsageEnabledChanged = on => { if (on) _usageHost?.Start(); else _usageHost?.Stop(); },
            MetricsChanged = () => _metricsHost?.Configure(
                settings.ShowSystemMetrics, settings.ShowSessionMetrics, settings.IncludeSubprocessMetrics),
            QuickLinksChanged = () => ReloadQuickLinks(settings),
            GlowChanged = UpdateGlow,
            TestNotification = kind => _notifications?.ShowTest(kind),
            TestExternalNotification = () => { if (_notifications is { } n) _ = n.SendExternalTestAsync(); },
            CheckForUpdates = () => _updateService?.CheckManual(),
            PerformUpdate = () => _updateService?.PerformUpdate(CloseAuxWindows),
            OpenStats = OpenStats,
            OpenFlightPath = OpenFlightPath,
        };
        _settings = new SettingsWindow(settings, _usageHost!, hooks, PlatformServices.AppIconProvider);
        _settings.SetUpdateAvailable(_updateService?.HasPendingUpdate ?? false, _updateService?.PendingVersion);
        _settings.Closed += (_, _) => _settings = null;
        _settings.Show();
        _settings.Activate();
    }
}
