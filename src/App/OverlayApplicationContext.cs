using Perch.App;
using Perch.Ui;
using Velopack;
using Velopack.Sources;

using Perch.Data;
namespace Perch.App;

internal sealed class OverlayApplicationContext : ApplicationContext
{
    // FileSystemWatcher can silently drop events on buffer overflow, so a slow
    // reconciliation scan keeps state honest even if a change notification is missed.
    private const int ReconcileIntervalMs = 30_000;

    // Account-wide rate-limit usage changes slowly; poll on startup then every 5 minutes.
    private const int UsageIntervalMs = 300_000;

    // Grace period after the last session ends before an auto-started tray closes itself, so a quick
    // session restart/compact (or opening the next session) doesn't tear the tray down and back up.
    private const int AutoCloseGraceMs = 20_000;

    // How often to poll GitHub for a newer release. Checked once on startup, then on this interval.
    // Checking is cheap (a metadata fetch) and downloads nothing until the user asks.
    private const int UpdateCheckIntervalMs = 3_600_000; // hourly

    private readonly OverlayForm _overlay;
    // Ambient screen-edge glow, driven from session state (see UpdateGlow). Created up front but never
    // shown until a session needs attention and the (experimental, off-by-default) setting is on.
    private readonly GlowForm _glow = new();
    private readonly SessionMonitor _monitor;
    private readonly MetricsMonitor _metricsMonitor = new();
    private readonly UsageMonitor _usageMonitor = new();
    private readonly System.Windows.Forms.Timer _reconcileTimer;
    private readonly System.Windows.Forms.Timer _deadlineTimer;
    private readonly System.Windows.Forms.Timer _usageTimer;
    private readonly System.Windows.Forms.Timer _updateCheckTimer;

    // One-shot grace timer for "auto-close after last session" (see AutoCloseGraceMs).
    private readonly System.Windows.Forms.Timer _autoCloseTimer;

    // Latched once we've observed at least one live session, so the startup race (the tray launches
    // before the opening session's file appears) can't trigger an immediate auto-close.
    private bool _seenSession;
    private readonly NotifyIcon _notifyIcon;
    private readonly AppSettings _settings;

    // "Perch reacts": renders the mood-bird tray icons / overlay logos and caches them. The plain
    // startup icon is kept separately so toggling the feature off can restore it without a rebuild.
    private readonly BirdMoodArt _moodArt = new();
    private readonly Icon _baseTrayIcon = LoadTrayIcon();
    // Last mood pushed to the surfaces, so a scan that doesn't change the mood repaints nothing.
    private BirdMood? _birdMood;

    // The tray menu's "Today: N sessions · Hh Mm active" info line, refreshed each time the menu opens.
    private readonly ToolStripMenuItem _statsItem;

    // The tray menu's update entry. Reads "Check for Updates…" normally; flips to a bold "Update
    // available" once an update is detected (see SetTrayUpdateAvailable). Clicking it checks, or —
    // when an update is pending — performs the update.
    private readonly ToolStripMenuItem _updateItem;

    // Tracks workstation lock state so the AFK override can push any session's alert while locked.
    private readonly LockMonitor _lockMonitor = new();

    // The settings window, lazily created on first open and reused while it stays open.
    private SettingsForm? _settingsForm;

    // The history viewer, lazily created on first open and reused while it stays open.
    private HistoryViewerForm? _historyForm;

    // The stats window, lazily created on first open and reused while it stays open.
    private StatsForm? _statsForm;

    // The flight-path window (daily session timeline), lazily created on first open and reused.
    private FlightPathForm? _flightForm;

    // The most recent set of live sessions, so a freshly-opened history viewer knows which sessions
    // are active without waiting for the next scan.
    private IReadOnlyList<ClaudeSession> _sessions = [];

    // Most recent usage reading, so a freshly-opened settings window can show it without waiting
    // for the next poll. Empty until the first successful (or attempted) fetch.
    private UsageInfo _lastUsage = UsageInfo.Empty;

    // Dispatches tray balloons, chimes and external (ntfy) pushes, and tracks the last-notified
    // session so a balloon click can focus its terminal. Created once the tray icon exists (see ctor).
    private readonly NotificationService _notifications;

    // Latched while a download/apply is in flight so a second click (the overlay badge, the tray menu
    // and the settings window all reach PerformUpdate) can't kick off a parallel run and race two
    // installs. Also blocks a background check from starting mid-apply.
    private bool _updateInProgress;

    public OverlayApplicationContext()
    {
        _settings = AppSettings.Load();
        // Apply the saved active-time idle threshold to the (otherwise static) stats engine.
        SessionStatsService.IdleThreshold = TimeSpan.FromMinutes(Math.Clamp(_settings.StatsActiveIdleMinutes, 1, 30));

        _overlay = new OverlayForm();
        _overlay.FormClosed     += (_, _) => ExitThread();
        _overlay.ExitRequested  += (_, _) => Exit();
        _overlay.SessionFocused += AcknowledgeSession;
        _overlay.ExternalNotifyToggleRequested += OnToggleExternalNotify;
        _overlay.HistoryRequested += OpenHistoryViewer;
        _overlay.UpdateRequested += (_, _) => PerformUpdate();
        // When the overlay is dragged to another monitor, follow it with the ambient glow.
        _overlay.DragCompleted += (_, _) => UpdateGlow();

        _notifyIcon = new NotifyIcon
        {
            Visible = true,
            Text    = "Perch",
            Icon    = _baseTrayIcon,
        };
        // Left-click opens the first-class settings window; right-click shows the slim menu below.
        _notifyIcon.MouseClick += (_, e) => { if (e.Button == MouseButtons.Left) OpenSettings(); };
        _notifyIcon.BalloonTipClicked += OnBalloonTipClicked;

        _notifications = new NotificationService(_notifyIcon, _settings, _lockMonitor);

        var trayMenu = new ContextMenuStrip();

        var header = new ToolStripMenuItem($"Perch — v{AppInfo.Version}") { Enabled = false };
        // Today's headline stats (sessions + active time), refreshed each time the menu opens. Disabled
        // so it reads as an info line, not a command.
        _statsItem = new ToolStripMenuItem(DayStats.Empty(DateOnly.FromDateTime(DateTime.Now)).TraySummary())
        {
            Enabled = false,
        };
        var settingsItem = new ToolStripMenuItem("Settings…");
        settingsItem.Click += (_, _) => OpenSettings();
        var historyItem = new ToolStripMenuItem("Session history…");
        historyItem.Click += (_, _) => OpenHistoryViewer(null);
        var statsItem2 = new ToolStripMenuItem("Session stats…");
        statsItem2.Click += (_, _) => OpenStats();
        var flightItem = new ToolStripMenuItem("Flight path…");
        flightItem.Click += (_, _) => OpenFlightPath();
        _updateItem = new ToolStripMenuItem("Check for Updates…");
        // Route by state: perform the pending update, or run a manual (user-initiated) check.
        _updateItem.Click += (_, _) =>
        {
            if (!string.IsNullOrEmpty(_settings.PendingUpdateVersion))
                PerformUpdate();
            else
                ManualCheckForUpdates();
        };
        var exitItem = new ToolStripMenuItem("Exit Perch");
        exitItem.Click += (_, _) => Exit();

        trayMenu.Items.Add(header);
        trayMenu.Items.Add(_statsItem);
        trayMenu.Items.Add(new ToolStripSeparator());
        trayMenu.Items.Add(settingsItem);
        trayMenu.Items.Add(historyItem);
        trayMenu.Items.Add(statsItem2);
        trayMenu.Items.Add(flightItem);
        trayMenu.Items.Add(_updateItem);
        trayMenu.Items.Add(new ToolStripSeparator());
        trayMenu.Items.Add(exitItem);
        // Recompute today's stats just before the menu shows; the scan runs off the UI thread and the
        // line updates in place a moment later (it's already visible by then).
        trayMenu.Opening += (_, _) => RefreshTodayStats();
        _notifyIcon.ContextMenuStrip = trayMenu;

        _monitor = new SessionMonitor();
        _monitor.SessionsChanged += OnSessionsChanged;
        _monitor.NeedsAttention  += OnNeedsAttention;
        _monitor.AwaitingInput   += OnAwaitingInput;
        // The plugin's /history command drops a one-shot trigger file the monitor turns into this event.
        _monitor.OpenHistoryRequested += OpenHistoryViewer;
        // Fires on a thread-pool thread (watcher / process-exit callbacks); marshal to the UI thread.
        _monitor.ChangeDetected  += RequestScan;

        // Resource sampler: fires on its own timer thread; marshal the reading onto the UI thread.
        _metricsMonitor.Updated += OnMetricsUpdated;

        // One-shot timer that fires the moment a "needs attention" window lapses back to idle —
        // a purely time-based transition with no corresponding file change to drive it.
        _deadlineTimer = new System.Windows.Forms.Timer();
        _deadlineTimer.Tick += (_, _) => { _deadlineTimer.Stop(); _monitor.Scan(); };

        // Low-frequency safety net against dropped FileSystemWatcher events.
        _reconcileTimer = new System.Windows.Forms.Timer { Interval = ReconcileIntervalMs };
        _reconcileTimer.Tick += (_, _) => _monitor.Scan();
        _reconcileTimer.Start();

        // Periodic account-usage refresh for the overlay's session/weekly bars. Only runs while
        // the feature is enabled — when off, no OAuth query is ever made.
        _usageTimer = new System.Windows.Forms.Timer { Interval = UsageIntervalMs };
        _usageTimer.Tick += (_, _) => RefreshUsage();

        // Hourly background check for a newer release. Started unconditionally (checking downloads
        // nothing); the initial startup check is kicked off below once the overlay handle exists.
        _updateCheckTimer = new System.Windows.Forms.Timer { Interval = UpdateCheckIntervalMs };
        _updateCheckTimer.Tick += (_, _) => AutoCheckForUpdates();
        _updateCheckTimer.Start();

        // Fires once, AutoCloseGraceMs after the last session ends. If still no sessions by then,
        // an auto-started tray exits. Armed/cancelled from OnSessionsChanged.
        _autoCloseTimer = new System.Windows.Forms.Timer { Interval = AutoCloseGraceMs };
        _autoCloseTimer.Tick += (_, _) =>
        {
            _autoCloseTimer.Stop();
            if (_sessions.Count == 0)
                Exit();
        };

        _overlay.Show();
        _overlay.SetUsageEnabled(_settings.ShowUsage);
        _overlay.SetShowExpectedRate(_settings.ShowExpectedUsageRate);
        _overlay.SetShowContextPressure(_settings.ShowContextPressure);
        _overlay.SetShowContextGreenSegment(_settings.ShowContextGreenSegment);
        _overlay.SetShowModeBadges(_settings.ShowPermissionModeBadges);
        _overlay.SetShowTaskProgress(_settings.ShowTaskProgress);
        _overlay.SetShowBurnRate(_settings.ShowBurnRate);
        _overlay.SetShowWaitingTimer(_settings.ShowWaitingTimer);
        _overlay.SetWaitingTimerRedMinutes(_settings.WaitingTimerRedMinutes);
        _overlay.SetShowArtifacts(_settings.ShowArtifacts);
        _overlay.SetHideInactiveTeamMembers(_settings.HideInactiveTeamMembers);
        _overlay.SetContextThresholds(
            _settings.ContextPressureYellowPercent,
            _settings.ContextPressureOrangePercent,
            _settings.ContextPressureRedPercent);
        ApplyStuckDetectionSettings();
        _overlay.SetShowSystemMetrics(_settings.ShowSystemMetrics);
        _overlay.SetShowSessionMetrics(_settings.ShowSessionMetrics);
        ApplyMonitoringSettings();
        _overlay.SetExternalNotificationsAvailable(_settings.ExternalNotificationsEnabled);
        // Warm the (slow, one-off) Start Menu app lookup off the UI thread so the first quick-link
        // icon load and the Add/Edit dialog don't stall on it.
        System.Threading.Tasks.Task.Run(ShellIcon.WarmCache);
        _overlay.SetQuickLinks(_settings.QuickLinks ?? []);
        _overlay.SetUpsideDownQuickLinks(_settings.UpsideDownQuickLinks);
        _monitor.Scan();

        if (_settings.ShowUsage)
        {
            _usageTimer.Start();
            RefreshUsage();
        }

        // Restore the "update available" UI from a pending record persisted in a previous session, so
        // the badge / tray text survive a restart without re-notifying. Then run the startup check.
        if (!string.IsNullOrEmpty(_settings.PendingUpdateVersion))
        {
            _overlay.SetUpdateAvailable(true);
            SetTrayUpdateAvailable(true);
        }
        AutoCheckForUpdates();

        // First launch after an install: add the marketplace and install the Claude Code plugin in
        // the background so the user doesn't have to. Failures are silently skipped (treated as ok).
        if (Program.IsFirstRun)
            AutoInstallPlugin();
    }

    // Fire-and-forget plugin install on first run. Shows a tray balloon up front so the work is
    // visible, then a quiet success balloon; any failure is swallowed (the user can still enable it
    // later from Settings).
    private async void AutoInstallPlugin()
    {
        // Already set up from a previous machine state? Skip the work and the noise.
        var (marketplace, plugin) = PluginManager.ReadInstalledState();
        if (marketplace && plugin)
            return;

        _notifications.ShowInfo("Perch",
            "Setting up the Claude Code plugin…", ToolTipIcon.Info);

        try
        {
            var (ok, _) = await new PluginManager().EnableAsync();
            if (ok)
                _notifications.ShowInfo("Perch",
                    "Claude Code plugin installed. Run /reload-plugins (or restart) in open sessions to load it.", ToolTipIcon.Info);
        }
        catch { /* best-effort: skip on any failure */ }
    }

    // Opens (or re-focuses) the settings window, wiring it to the shared state and callbacks.
    private void OpenSettings()
    {
        _settingsForm = WindowHost.ShowOrFocus(_settingsForm,
            () => new SettingsForm(_settings, _usageMonitor, _lastUsage),
            () => _settingsForm = null,
            beforeShow: f =>
            {
                f.UsageEnabledChanged    += SetUsageEnabled;
                f.ExpectedRateChanged    += SetExpectedRateEnabled;
                f.ContextPressureChanged += SetContextPressureEnabled;
                f.ContextGreenSegmentChanged += SetContextGreenSegmentEnabled;
                f.PermissionModeBadgesChanged += SetPermissionModeBadgesEnabled;
                f.TaskProgressChanged += SetTaskProgressEnabled;
                f.BurnRateChanged += SetBurnRateEnabled;
                f.WaitingTimerChanged += SetWaitingTimerEnabled;
                f.WaitingTimerRedMinutesChanged += SetWaitingTimerRedMinutes;
                f.ArtifactsChanged += SetArtifactsEnabled;
                f.HideInactiveTeamMembersChanged += SetHideInactiveTeamMembers;
                f.ScreenEdgeGlowChanged += SetScreenEdgeGlow;
                f.PerchReactsChanged += SetPerchReacts;
                f.ContextThresholdsChanged += SetContextThresholds;
                f.StuckDetectionChanged += SetStuckDetection;
                f.SystemMetricsChanged += SetSystemMetricsEnabled;
                f.SessionMetricsChanged += SetSessionMetricsEnabled;
                f.SubprocessMetricsChanged += SetSubprocessMetricsEnabled;
                f.CheckForUpdatesRequested += (_, _) => ManualCheckForUpdates();
                f.UpdateNowRequested += (_, _) => PerformUpdate();
                f.TestNotificationRequested += _notifications.ShowTest;
                f.ExternalNotificationsEnabledChanged += SetExternalNotificationsEnabled;
                f.TestExternalNotificationRequested   += () => _ = _notifications.SendExternalTestAsync();
                f.QuickLinksChanged       += SetQuickLinks;
                f.UpsideDownQuickLinksChanged += SetUpsideDownQuickLinks;
                f.OpenStatsRequested      += OpenStats;
                f.OpenFlightPathRequested += OpenFlightPath;
            },
            // Sync the About "update available" highlight/button on every open (runs on both paths).
            refresh: f => f.SetUpdateAvailable(
                !string.IsNullOrEmpty(_settings.PendingUpdateVersion), _settings.PendingUpdateVersion));
    }

    // Opens (or re-focuses) the history viewer and points it at the given session. A null sessionId
    // (from the tray menu) lands on the most-recent session. Seeds the viewer with the current live
    // sessions so active indicators are right immediately.
    private void OpenHistoryViewer(string? sessionId)
    {
        _historyForm = WindowHost.ShowOrFocus(_historyForm,
            () => new HistoryViewerForm(),
            () => _historyForm = null,
            refresh: f =>
            {
                f.SetActiveSessions(_sessions);
                f.SelectSession(sessionId);
            });
    }

    // Opens (or re-focuses) the stats window and refreshes today's figures. A single reused instance,
    // like the settings and history windows.
    private void OpenStats()
    {
        // refresh runs on both the reuse and create paths, so it also kicks the first load on open
        // (StatsForm no longer self-loads in OnShown).
        _statsForm = WindowHost.ShowOrFocus(_statsForm,
            () => new StatsForm(_settings),
            () => _statsForm = null,
            refresh: f => f.RefreshStats());
    }

    // Opens (or re-focuses) the flight-path window and refreshes the day's timeline. A single reused
    // instance, like the settings / history / stats windows; refresh runs on both paths, so it also
    // kicks the first load on open.
    private void OpenFlightPath()
    {
        _flightForm = WindowHost.ShowOrFocus(_flightForm,
            () => new FlightPathForm(),
            () => _flightForm = null,
            refresh: f => f.RefreshPath());
    }

    // Toggles the usage bars. Disabling stops all polling so no OAuth query ever goes out;
    // enabling kicks off an immediate refresh and resumes the timer.
    private void SetUsageEnabled(bool enabled)
    {
        if (_settings.ShowUsage == enabled)
            return;

        _settings.ShowUsage = enabled;
        _settings.Save();
        _overlay.SetUsageEnabled(enabled);

        if (enabled)
        {
            _usageTimer.Start();
            RefreshUsage();
        }
        else
        {
            _usageTimer.Stop();
        }
    }

    private void SetExpectedRateEnabled(bool enabled)
    {
        if (_settings.ShowExpectedUsageRate == enabled) return;
        _settings.ShowExpectedUsageRate = enabled;
        _settings.Save();
        _overlay.SetShowExpectedRate(enabled);
    }

    private void SetContextPressureEnabled(bool enabled)
    {
        if (_settings.ShowContextPressure == enabled) return;
        _settings.ShowContextPressure = enabled;
        _settings.Save();
        _overlay.SetShowContextPressure(enabled);
    }

    private void SetContextGreenSegmentEnabled(bool enabled)
    {
        if (_settings.ShowContextGreenSegment == enabled) return;
        _settings.ShowContextGreenSegment = enabled;
        _settings.Save();
        _overlay.SetShowContextGreenSegment(enabled);
    }

    private void SetPermissionModeBadgesEnabled(bool enabled)
    {
        if (_settings.ShowPermissionModeBadges == enabled) return;
        _settings.ShowPermissionModeBadges = enabled;
        _settings.Save();
        _overlay.SetShowModeBadges(enabled);
    }

    private void SetTaskProgressEnabled(bool enabled)
    {
        if (_settings.ShowTaskProgress == enabled) return;
        _settings.ShowTaskProgress = enabled;
        _settings.Save();
        _overlay.SetShowTaskProgress(enabled);
    }

    private void SetBurnRateEnabled(bool enabled)
    {
        if (_settings.ShowBurnRate == enabled) return;
        _settings.ShowBurnRate = enabled;
        _settings.Save();
        _overlay.SetShowBurnRate(enabled);
    }

    private void SetWaitingTimerEnabled(bool enabled)
    {
        if (_settings.ShowWaitingTimer == enabled) return;
        _settings.ShowWaitingTimer = enabled;
        _settings.Save();
        _overlay.SetShowWaitingTimer(enabled);
    }

    private void SetWaitingTimerRedMinutes(int minutes)
    {
        minutes = Math.Max(1, minutes);
        if (_settings.WaitingTimerRedMinutes == minutes) return;
        _settings.WaitingTimerRedMinutes = minutes;
        _settings.Save();
        _overlay.SetWaitingTimerRedMinutes(minutes);
    }

    private void SetArtifactsEnabled(bool enabled)
    {
        if (_settings.ShowArtifacts == enabled) return;
        _settings.ShowArtifacts = enabled;
        _settings.Save();
        _overlay.SetShowArtifacts(enabled);
    }

    private void SetHideInactiveTeamMembers(bool enabled)
    {
        if (_settings.HideInactiveTeamMembers == enabled) return;
        _settings.HideInactiveTeamMembers = enabled;
        _settings.Save();
        _overlay.SetHideInactiveTeamMembers(enabled);
    }

    private void SetScreenEdgeGlow(bool enabled)
    {
        if (_settings.ScreenEdgeGlow == enabled) return;
        _settings.ScreenEdgeGlow = enabled;
        _settings.Save();
        // Re-evaluate at once: light up (if something already needs you) or fade out immediately.
        UpdateGlow();
    }

    // Shows or hides the ambient screen-edge glow from the current session state. Lit while any session
    // needs attention or is awaiting input (and the setting is on), around the screen the overlay is on,
    // in the overlay's attention orange; hidden otherwise. Called after every scan, so it self-corrects
    // as sessions come and go and as the needs-attention window lapses.
    private void UpdateGlow()
    {
        if (!_settings.ScreenEdgeGlow)
        {
            _glow.HideGlow();
            return;
        }

        bool needs = _sessions.Any(
            s => s.Status is SessionStatus.NeedsAttention or SessionStatus.AwaitingInput);
        if (needs)
            _glow.ShowGlow(ScreenForOverlay(), Theme.Orange);
        else
            _glow.HideGlow();
    }

    // "Perch reacts": pushes the current aggregate mood onto the tray icon and the overlay logo. When
    // the feature is off, restores the plain bird. Called after every scan (so the mood tracks sessions
    // as they come and go) and when the setting is toggled; a no-op when the mood hasn't changed.
    private void ApplyBirdMood()
    {
        if (!_settings.PerchReacts)
        {
            if (_birdMood == null) return;  // already showing the plain bird
            _birdMood = null;
            _notifyIcon.Icon = _baseTrayIcon;
            _overlay.SetBirdMood(null);
            return;
        }

        var mood = BirdMoodArt.MoodFor(_sessions);
        if (_birdMood == mood) return;
        _birdMood = mood;
        _notifyIcon.Icon = _moodArt.TrayIcon(mood);
        _overlay.SetBirdMood(_moodArt.OverlayBitmap(mood));
    }

    // Toggles "perch reacts". Re-applies at once so the bird's expression appears (or the plain logo
    // returns) without waiting for the next scan.
    private void SetPerchReacts(bool enabled)
    {
        if (_settings.PerchReacts == enabled) return;
        _settings.PerchReacts = enabled;
        _settings.Save();
        ApplyBirdMood();
    }

    // The bounds of the screen the overlay currently sits on (falls back to the primary screen), so
    // the glow lights up the monitor the user is most likely watching.
    private Rectangle ScreenForOverlay()
    {
        try
        {
            if (_overlay.IsHandleCreated && !_overlay.IsDisposed)
                return Screen.FromControl(_overlay).Bounds;
        }
        catch (ObjectDisposedException) { }
        catch (InvalidOperationException) { }
        return Screen.PrimaryScreen!.Bounds;
    }

    private void SetContextThresholds(int yellow, int orange, int red)
    {
        _settings.ContextPressureYellowPercent = yellow;
        _settings.ContextPressureOrangePercent = orange;
        _settings.ContextPressureRedPercent    = red;
        _settings.Save();
        _overlay.SetContextThresholds(yellow, orange, red);
    }

    // Pushes the three stuck-detection settings onto the monitor (which computes the signal) and the
    // overlay (which draws the glyph). Shared by startup and the live settings change below.
    private void ApplyStuckDetectionSettings()
    {
        _monitor.StuckDetectionEnabled = _settings.StuckDetectionEnabled;
        _monitor.DetectErrorStreaks    = _settings.DetectErrorStreaks;
        _monitor.DetectFailingLoops    = _settings.DetectFailingLoops;
        _overlay.SetStuckDetectionEnabled(_settings.StuckDetectionEnabled);
    }

    private void SetStuckDetection(bool enabled, bool errorStreaks, bool failingLoops)
    {
        _settings.StuckDetectionEnabled = enabled;
        _settings.DetectErrorStreaks    = errorStreaks;
        _settings.DetectFailingLoops    = failingLoops;
        _settings.Save();
        ApplyStuckDetectionSettings();
        // Re-scan so the change takes effect at once: signals recompute (or clear) without waiting
        // for the next transcript write or reconciliation tick.
        RequestScan();
    }

    // ── Resource monitoring ───────────────────────────────────────────────────────
    // Marshals a metrics sample onto the UI thread and pushes it into the overlay. Fires on the
    // MetricsMonitor's own timer thread.
    private void OnMetricsUpdated(SystemMetrics system, IReadOnlyDictionary<string, SessionMetrics> sessions)
        => UiDispatch.Post(_overlay, () =>
        {
            _overlay.UpdateSystemMetrics(system);
            _overlay.UpdateSessionMetrics(sessions);
        });

    // Configures the sampler from the current settings — sampling runs only while system or per-session
    // metrics are on. Shared by startup and the live setting changes below.
    private void ApplyMonitoringSettings()
        => _metricsMonitor.Configure(
            _settings.ShowSystemMetrics,
            _settings.ShowSessionMetrics,
            _settings.IncludeSubprocessMetrics);

    private void SetSystemMetricsEnabled(bool enabled)
    {
        if (_settings.ShowSystemMetrics == enabled) return;
        _settings.ShowSystemMetrics = enabled;
        _settings.Save();
        _overlay.SetShowSystemMetrics(enabled);
        ApplyMonitoringSettings();
    }

    private void SetSessionMetricsEnabled(bool enabled)
    {
        if (_settings.ShowSessionMetrics == enabled) return;
        _settings.ShowSessionMetrics = enabled;
        _settings.Save();
        _overlay.SetShowSessionMetrics(enabled);
        ApplyMonitoringSettings();
    }

    private void SetSubprocessMetricsEnabled(bool enabled)
    {
        if (_settings.IncludeSubprocessMetrics == enabled) return;
        _settings.IncludeSubprocessMetrics = enabled;
        _settings.Save();
        ApplyMonitoringSettings();
    }

    // Fetches usage off the UI thread, then pushes the result back onto it for rendering in both
    // the overlay and (if open) the settings window. Caches the latest reading for new windows.
    private async void RefreshUsage()
    {
        if (!_settings.ShowUsage) return;
        var info = await _usageMonitor.FetchAsync();
        _lastUsage = info;
        UiDispatch.Post(_overlay, () =>
        {
            _overlay.UpdateUsage(info);
            if (_settingsForm is { IsDisposed: false })
                _settingsForm.UpdateUsage(info);
        });
    }

    // Recomputes today's headline stats off the UI thread (scanning transcripts can touch several
    // files) and updates the tray menu's info line in place. Fired on menu-open; the line is already
    // visible, so it simply refreshes from its previous value a moment after the menu appears.
    private void RefreshTodayStats()
    {
        if (!_settings.ShowTodayStatsInTray)
        {
            _statsItem.Visible = false;
            return;
        }
        _statsItem.Visible = true;

        var today = DateOnly.FromDateTime(DateTime.Now);
        UiDispatch.RunThenPost(_overlay,
            () => SessionStatsService.ForDay(today),
            stats => _statsItem.Text = stats.TraySummary(),
            DayStats.Empty(today));
    }

    private void OnSessionsChanged(IReadOnlyList<ClaudeSession> sessions)
    {
        _sessions = sessions;
        _overlay.UpdateSessions(sessions);
        // Tell the resource sampler which session processes to measure this round.
        _metricsMonitor.SetSessionPids(sessions.Select(s => s.Pid));
        if (_historyForm is { IsDisposed: false })
            _historyForm.SetActiveSessions(sessions);
        // Refresh the overlay's mail glyphs from the per-session opt-in marker files just read.
        PushExternalNotifyGlyphs();
        ArmDeadlineTimer();
        UpdateGlow();
        ApplyBirdMood();

        _notifyIcon.Text = sessions.Count switch
        {
            0 => "Perch — No active sessions",
            1 => "Perch — 1 session",
            _ => $"Perch — {sessions.Count} sessions",
        };

        MaybeHandleAutoClose(sessions.Count);
    }

    // Auto-close: only an auto-started tray with the setting on ever closes itself, so a manually-
    // opened window never vanishes under the user. Once at least one session has been seen, dropping
    // back to zero arms the grace timer; any session reappearing cancels it. The setting is read
    // live, so toggling it in settings takes effect immediately.
    private void MaybeHandleAutoClose(int sessionCount)
    {
        if (!Program.AutoStarted || !_settings.AutoCloseAfterLastSession)
        {
            _autoCloseTimer.Stop();
            _overlay.CancelAutoCloseCountdown();
            return;
        }

        if (sessionCount > 0)
        {
            _seenSession = true;
            _autoCloseTimer.Stop();
            _overlay.CancelAutoCloseCountdown();
            return;
        }

        // Zero sessions: hold off until we've actually seen one (don't exit during the startup race),
        // then start the grace countdown.
        if (!_seenSession)
            return;

        // Leave an already-running countdown alone. SessionsChanged fires on every scan (not just
        // on a real change), so the 30s reconcile poll — plus any file write in ~/.claude/sessions
        // (a busy session's .json heartbeat, a .mode rewrite) — re-enters here while still at zero.
        // Re-arming on each of those would reset the grace period so it never elapsed; instead the
        // countdown must measure time since sessions actually hit zero.
        if (_autoCloseTimer.Enabled)
            return;

        _autoCloseTimer.Start();
        // Surface the grace countdown on the overlay as a quiet depleting bar.
        _overlay.StartAutoCloseCountdown(AutoCloseGraceMs);
    }

    // Marshals a re-scan onto the UI thread. SessionMonitor raises ChangeDetected from
    // FileSystemWatcher and Process.Exited callbacks, which run on thread-pool threads.
    private void RequestScan()
    {
        try
        {
            if (_overlay.IsHandleCreated && !_overlay.IsDisposed)
                _overlay.BeginInvoke((Action)(() => _monitor.Scan()));
        }
        catch (ObjectDisposedException) { }
        catch (InvalidOperationException) { }
    }

    // Arms the one-shot timer for the next needs-attention deadline reported by the monitor.
    // Called from OnSessionsChanged (i.e. after every Scan), so it always reflects current state.
    private void ArmDeadlineTimer()
    {
        _deadlineTimer.Stop();

        var deadline = _monitor.NextNeedsAttentionDeadline;
        if (deadline == null)
            return;

        var ms = (deadline.Value - DateTime.Now).TotalMilliseconds;
        // Fire on the next message-loop tick if already due (never re-scan re-entrantly here).
        _deadlineTimer.Interval = (int)Math.Clamp(ms, 1, int.MaxValue);
        _deadlineTimer.Start();
    }

    private void AcknowledgeSession(string pid)
    {
        _monitor.Acknowledge(pid);
        _overlay.BeginInvoke(_monitor.Scan);
    }

    private void OnNeedsAttention(ClaudeSession session)
    {
        // The overlay's own attention flash is always on; the balloon/chime/external push are gated
        // (per their settings) inside the notification service.
        _overlay.TriggerAttention();
        _notifications.Notify(NotificationKind.Done, session);
    }

    private void OnAwaitingInput(ClaudeSession session)
    {
        _overlay.TriggerAttention();
        _notifications.Notify(NotificationKind.WaitingForInput, session);
    }

    // ── External (ntfy) notifications ─────────────────────────────────────────────
    // Flips a session's external-notify opt-in from the overlay's right-click menu by writing or
    // deleting its marker file — the same single source of truth the plugin's /afk command toggles,
    // so the two paths can never disagree. The follow-up scan re-reads the file and refreshes glyphs.
    private void OnToggleExternalNotify(string sessionId)
    {
        _monitor.ToggleExternalNotify(sessionId);
        _monitor.Scan();
    }

    // Pushes the set of opted-in sessions (those carrying a marker file, per the latest scan) to the
    // overlay so its mail glyphs and right-click wording match.
    private void PushExternalNotifyGlyphs()
        => _overlay.SetExternalNotifySessions(
            _sessions.Where(s => s.ExternalNotify).Select(s => s.SessionId).ToHashSet());

    // Mirrors the master switch into the overlay (it gates the glyph and the right-click item) and
    // persists it. The host/topic are saved by the settings window itself.
    private void SetExternalNotificationsEnabled(bool enabled)
    {
        _settings.ExternalNotificationsEnabled = enabled;
        _settings.Save();
        _overlay.SetExternalNotificationsAvailable(enabled);
    }

    private void SetQuickLinks(IReadOnlyList<QuickLink> links)
    {
        _settings.QuickLinks = links.Select(l => l.Clone()).ToList();
        _settings.Save();
        _overlay.SetQuickLinks(links);
    }

    private void SetUpsideDownQuickLinks(bool upsideDown)
    {
        if (_settings.UpsideDownQuickLinks == upsideDown) return;
        _settings.UpsideDownQuickLinks = upsideDown;
        _settings.Save();
        _overlay.SetUpsideDownQuickLinks(upsideDown);
    }

    // Clicking the desktop notification focuses the terminal for the session that
    // raised it and acknowledges the alert, mirroring an overlay/indicator click.
    private void OnBalloonTipClicked(object? sender, EventArgs e)
    {
        var pid = _notifications.LastNotifiedPid;
        if (pid == null) return;

        if (int.TryParse(pid, out int pidInt))
            NativeMethods.FocusTerminalForProcess(pidInt, _notifications.LastNotifiedProject);

        AcknowledgeSession(pid);
    }

    // Loads the multi-resolution app icon and picks the frame that best fits the tray at the
    // current DPI (the .ico ships a true 16px image), so the orange logo stays crisp and colour-
    // accurate instead of being downscaled from the 32px PNG.
    private static Icon LoadTrayIcon()
    {
        using var stream = typeof(OverlayApplicationContext).Assembly.GetManifestResourceStream("Perch.icon.ico")!;
        return new Icon(stream, SystemInformation.SmallIconSize);
    }

    // ── Update checking ──────────────────────────────────────────────────────────
    // The outcome of a single check, marshalled back from the thread-pool worker below.
    private enum UpdateCheckOutcome { UpToDate, Available, Error }
    private readonly record struct UpdateCheckResult(UpdateCheckOutcome Outcome, string? Version, string? Error);

    // Queries GitHub for a newer release. Runs on the thread pool (Velopack's check is a synchronous-
    // over-async metadata fetch); all failures — including "not installed" in a dev run — collapse to
    // an Error result the callers decide whether to surface.
    private static UpdateCheckResult RunUpdateCheck()
    {
        try
        {
            var mgr = new UpdateManager(new GithubSource(AppInfo.RepoUrl, null, false));
            var update = mgr.CheckForUpdatesAsync().GetAwaiter().GetResult();
            return update == null
                ? new UpdateCheckResult(UpdateCheckOutcome.UpToDate, null, null)
                : new UpdateCheckResult(UpdateCheckOutcome.Available, update.TargetFullRelease.Version.ToString(), null);
        }
        catch (Exception ex)
        {
            return new UpdateCheckResult(UpdateCheckOutcome.Error, null, ex.Message);
        }
    }

    // Startup + hourly background check. Silent: errors and "up to date" produce no balloon, and an
    // available update notifies only the first time it's surfaced (see MarkUpdateAvailable).
    private void AutoCheckForUpdates()
    {
        if (_updateInProgress) return;  // don't check while an apply is under way
        UiDispatch.RunThenPost(_overlay, RunUpdateCheck, r =>
        {
            if (r.Outcome == UpdateCheckOutcome.Available && r.Version is { } v)
            {
                bool firstTime = string.IsNullOrEmpty(_settings.PendingUpdateVersion);
                MarkUpdateAvailable(v, notify: firstTime);
            }
        }, new UpdateCheckResult(UpdateCheckOutcome.Error, null, null));
    }

    // The tray "Check for Updates…" item / settings "Check for Updates" button: user-initiated, so it
    // always gives explicit feedback (checking → up-to-date / available / failed).
    private void ManualCheckForUpdates()
    {
        if (_updateInProgress) return;
        _notifications.ShowInfo("Perch", "Checking for updates…", ToolTipIcon.Info, 3000);
        UiDispatch.RunThenPost(_overlay, RunUpdateCheck, r =>
        {
            switch (r.Outcome)
            {
                case UpdateCheckOutcome.Available when r.Version is { } v:
                    MarkUpdateAvailable(v, notify: true);
                    break;
                case UpdateCheckOutcome.UpToDate:
                    _notifications.ShowInfo("Perch", "You're on the latest version.", ToolTipIcon.Info, 4000);
                    break;
                default:
                    _notifications.ShowInfo("Perch — Update check failed",
                        r.Error ?? "Could not check for updates.", ToolTipIcon.Error, 6000);
                    break;
            }
        }, new UpdateCheckResult(UpdateCheckOutcome.Error, null, "Could not check for updates."));
    }

    // Records a pending update and lights up every surface (overlay badge, tray item, About highlight).
    // The persisted version is what suppresses re-notifying and restores the UI across restarts, so it
    // is always (re)written; the balloon fires only when asked (the first surfacing — not for a newer
    // version found on a later background check).
    private void MarkUpdateAvailable(string version, bool notify)
    {
        _settings.PendingUpdateVersion = version;
        _settings.Save();

        _overlay.SetUpdateAvailable(true);
        SetTrayUpdateAvailable(true);
        if (_settingsForm is { IsDisposed: false })
            _settingsForm.SetUpdateAvailable(true, version);

        if (notify)
            _notifications.ShowInfo("Perch — Update available",
                $"Version {version} is ready to install. Use the update button to update.",
                ToolTipIcon.Info, 8000);
    }

    // Clears a pending update and returns every surface to its default state. Used both after applying
    // an update and to self-heal when a check finds the pending update is no longer actually available.
    private void ClearPendingUpdate()
    {
        _settings.PendingUpdateVersion = null;
        _settings.Save();

        _overlay.SetUpdateAvailable(false);
        SetTrayUpdateAvailable(false);
        if (_settingsForm is { IsDisposed: false })
            _settingsForm.SetUpdateAvailable(false, null);
    }

    // Flips the tray menu's update entry between the neutral "Check for Updates…" and a bold, brand-
    // coloured "Update available" call to action.
    private void SetTrayUpdateAvailable(bool available)
    {
        _updateItem.Text = available ? "Update available" : "Check for Updates…";
        var baseFont = _updateItem.Font ?? SystemFonts.MenuFont!;
        _updateItem.Font = new Font(baseFont, available ? FontStyle.Bold : FontStyle.Regular);
        _updateItem.ForeColor = available ? Theme.Brand : SystemColors.ControlText;
    }

    // Downloads and applies the pending update, then restarts. Triggered by any "update" affordance
    // (overlay badge, tray item, About button). Clears the persisted record before applying so a stale
    // entry can't stick and the post-restart startup check re-detects any further drift.
    private async void PerformUpdate()
    {
        // A run is already in flight — the window has likely already closed, so just ignore the click.
        if (_updateInProgress)
            return;
        _updateInProgress = true;

        try
        {
            _notifications.ShowInfo("Perch — Updating", "Preparing update…", ToolTipIcon.Info, 5000);

            var mgr = new UpdateManager(new GithubSource(AppInfo.RepoUrl, null, false));
            var update = await mgr.CheckForUpdatesAsync();
            if (update == null)
            {
                // Drift: the update was applied elsewhere or the release was pulled. Self-heal the UI
                // instead of leaving a stuck "update available" state.
                ClearPendingUpdate();
                _notifications.ShowInfo("Perch", "You're already on the latest version.", ToolTipIcon.Info, 4000);
                return;
            }

            _notifications.ShowInfo("Perch — Updating",
                $"Downloading v{update.TargetFullRelease.Version}…", ToolTipIcon.Info, 5000);

            // Clear the persisted record up front: the process is about to be replaced, and we want a
            // clean slate so the next startup re-detects any version drift from scratch.
            _settings.PendingUpdateVersion = null;
            _settings.Save();

            // Close the open windows up front: the closing window is the visible signal that the
            // update is under way, and it stops the button being clicked again mid-download. The
            // overlay stays up so the message loop survives the awaits below — ApplyUpdatesAndRestart
            // tears everything down when it relaunches.
            if (_settingsForm is { IsDisposed: false })
                _settingsForm.Close();
            if (_historyForm is { IsDisposed: false })
                _historyForm.Close();
            if (_statsForm is { IsDisposed: false })
                _statsForm.Close();
            if (_flightForm is { IsDisposed: false })
                _flightForm.Close();

            await mgr.DownloadUpdatesAsync(update);
            mgr.ApplyUpdatesAndRestart(update);
        }
        catch (Exception ex)
        {
            _notifications.ShowInfo("Perch — Update Failed", ex.Message, ToolTipIcon.Error, 6000);
        }
        finally
        {
            // ApplyUpdatesAndRestart exits the process, so this only runs on the drift or failure
            // paths — both of which should allow another attempt later.
            _updateInProgress = false;
        }
    }

    private void Exit()
    {
        _reconcileTimer.Stop();
        _deadlineTimer.Stop();
        _usageTimer.Stop();
        _updateCheckTimer.Stop();
        _autoCloseTimer.Stop();
        _notifyIcon.Visible = false;
        if (_settingsForm is { IsDisposed: false })
            _settingsForm.Close();
        if (_historyForm is { IsDisposed: false })
            _historyForm.Close();
        if (_statsForm is { IsDisposed: false })
            _statsForm.Close();
        if (_flightForm is { IsDisposed: false })
            _flightForm.Close();
        _glow.HideGlow();
        _glow.Close();
        _overlay.Close();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _reconcileTimer.Dispose();
            _deadlineTimer.Dispose();
            _usageTimer.Dispose();
            _updateCheckTimer.Dispose();
            _autoCloseTimer.Dispose();
            _monitor.Dispose();
            _metricsMonitor.Dispose();
            _lockMonitor.Dispose();
            // Detach before disposing the icon owners: the current icon may be an art-owned mood icon
            // (the base and the mood cache both dispose below), so don't let NotifyIcon touch it.
            _notifyIcon.Icon = null;
            _notifyIcon.Dispose();
            _baseTrayIcon.Dispose();
            _moodArt.Dispose();
            _settingsForm?.Dispose();
            _historyForm?.Dispose();
            _statsForm?.Dispose();
            _flightForm?.Dispose();
            _glow.Dispose();
            _overlay.Dispose();
        }
        base.Dispose(disposing);
    }
}
