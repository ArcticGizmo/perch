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

    private readonly OverlayForm _overlay;
    private readonly SessionMonitor _monitor;
    private readonly UsageMonitor _usageMonitor = new();
    private readonly System.Windows.Forms.Timer _reconcileTimer;
    private readonly System.Windows.Forms.Timer _deadlineTimer;
    private readonly System.Windows.Forms.Timer _usageTimer;

    // One-shot grace timer for "auto-close after last session" (see AutoCloseGraceMs).
    private readonly System.Windows.Forms.Timer _autoCloseTimer;

    // Latched once we've observed at least one live session, so the startup race (the tray launches
    // before the opening session's file appears) can't trigger an immediate auto-close.
    private bool _seenSession;
    private readonly NotifyIcon _notifyIcon;
    private readonly AppSettings _settings;

    // The tray menu's "Today: N sessions · Hh Mm active" info line, refreshed each time the menu opens.
    private readonly ToolStripMenuItem _statsItem;

    // Tracks workstation lock state so the AFK override can push any session's alert while locked.
    private readonly LockMonitor _lockMonitor = new();

    // The settings window, lazily created on first open and reused while it stays open.
    private SettingsForm? _settingsForm;

    // The history viewer, lazily created on first open and reused while it stays open.
    private HistoryViewerForm? _historyForm;

    // The stats window, lazily created on first open and reused while it stays open.
    private StatsForm? _statsForm;

    // The most recent set of live sessions, so a freshly-opened history viewer knows which sessions
    // are active without waiting for the next scan.
    private IReadOnlyList<ClaudeSession> _sessions = [];

    // Most recent usage reading, so a freshly-opened settings window can show it without waiting
    // for the next poll. Empty until the first successful (or attempted) fetch.
    private UsageInfo _lastUsage = UsageInfo.Empty;

    // Dispatches tray balloons, chimes and external (ntfy) pushes, and tracks the last-notified
    // session so a balloon click can focus its terminal. Created once the tray icon exists (see ctor).
    private readonly NotificationService _notifications;

    // Latched while a check/download/apply is in flight so a second click (the menu and the settings
    // window both reach CheckForUpdates) can't kick off a parallel run and race two installs.
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

        _notifyIcon = new NotifyIcon
        {
            Visible = true,
            Text    = "Perch",
            Icon    = LoadTrayIcon(),
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
        var updateItem = new ToolStripMenuItem("Check for Updates…");
        updateItem.Click += (_, _) => CheckForUpdates();
        var exitItem = new ToolStripMenuItem("Exit Perch");
        exitItem.Click += (_, _) => Exit();

        trayMenu.Items.Add(header);
        trayMenu.Items.Add(_statsItem);
        trayMenu.Items.Add(new ToolStripSeparator());
        trayMenu.Items.Add(settingsItem);
        trayMenu.Items.Add(historyItem);
        trayMenu.Items.Add(statsItem2);
        trayMenu.Items.Add(updateItem);
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
        _overlay.SetContextThresholds(
            _settings.ContextPressureYellowPercent,
            _settings.ContextPressureOrangePercent,
            _settings.ContextPressureRedPercent);
        ApplyStuckDetectionSettings();
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
                f.ContextThresholdsChanged += SetContextThresholds;
                f.StuckDetectionChanged += SetStuckDetection;
                f.CheckForUpdatesRequested += (_, _) => CheckForUpdates();
                f.TestNotificationRequested += _notifications.ShowTest;
                f.ExternalNotificationsEnabledChanged += SetExternalNotificationsEnabled;
                f.TestExternalNotificationRequested   += () => _ = _notifications.SendExternalTestAsync();
                f.QuickLinksChanged       += SetQuickLinks;
                f.UpsideDownQuickLinksChanged += SetUpsideDownQuickLinks;
                f.OpenStatsRequested      += OpenStats;
            });
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
        if (_historyForm is { IsDisposed: false })
            _historyForm.SetActiveSessions(sessions);
        // Refresh the overlay's mail glyphs from the per-session opt-in marker files just read.
        PushExternalNotifyGlyphs();
        ArmDeadlineTimer();

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

    private async void CheckForUpdates()
    {
        // A run is already in flight — the window has likely already closed, so just ignore the click.
        if (_updateInProgress)
            return;
        _updateInProgress = true;

        // Update balloons aren't tied to a session (ShowInfo clears the last-notified pid so a click
        // can't focus a stale terminal).
        try
        {
            // Querying GitHub can take a few seconds; show an immediate balloon so the click feels
            // acknowledged rather than dead until the check resolves.
            _notifications.ShowInfo("Perch", "Checking for updates…", ToolTipIcon.Info, 3000);

            var mgr = new UpdateManager(new GithubSource("https://github.com/ArcticGizmo/perch", null, false));
            var update = await mgr.CheckForUpdatesAsync();
            if (update == null)
            {
                _notifications.ShowInfo("Perch", "You're on the latest version.", ToolTipIcon.Info, 4000);
                return;
            }

            _notifications.ShowInfo("Perch — Updating",
                $"Downloading v{update.TargetFullRelease.Version}…", ToolTipIcon.Info, 5000);

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

            await mgr.DownloadUpdatesAsync(update);
            mgr.ApplyUpdatesAndRestart(update);
        }
        catch (Exception ex)
        {
            _notifications.ShowInfo("Perch — Update Failed", ex.Message, ToolTipIcon.Error, 6000);
        }
        finally
        {
            // ApplyUpdatesAndRestart exits the process, so this only runs on the "up to date" or
            // failure paths — both of which should allow another check later.
            _updateInProgress = false;
        }
    }

    private void Exit()
    {
        _reconcileTimer.Stop();
        _deadlineTimer.Stop();
        _usageTimer.Stop();
        _autoCloseTimer.Stop();
        _notifyIcon.Visible = false;
        if (_settingsForm is { IsDisposed: false })
            _settingsForm.Close();
        if (_historyForm is { IsDisposed: false })
            _historyForm.Close();
        if (_statsForm is { IsDisposed: false })
            _statsForm.Close();
        _overlay.Close();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _reconcileTimer.Dispose();
            _deadlineTimer.Dispose();
            _usageTimer.Dispose();
            _autoCloseTimer.Dispose();
            _monitor.Dispose();
            _lockMonitor.Dispose();
            _notifyIcon.Icon?.Dispose();
            _notifyIcon.Dispose();
            _settingsForm?.Dispose();
            _historyForm?.Dispose();
            _statsForm?.Dispose();
            _overlay.Dispose();
        }
        base.Dispose(disposing);
    }
}
