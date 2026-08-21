using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input.Platform;
using Avalonia.Markup.Xaml;
using Avalonia.Platform;
using Avalonia.Threading;
using Perch.Social;
using Perch.Avalonia.Services;
using Perch.Avalonia.Theming;
using Perch.Avalonia.Views;
using Perch.Avalonia.Windows;
using Perch.Data;
using Perch.Data.Hypertree;
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
    private StatusMonitorHost? _statusHost;
    private MediaMonitorHost? _mediaHost;
    private MicMonitorHost? _micHost;
    private SupabaseSocialClient? _social;
    private SocialFeedMonitorHost? _feedHost;
    private HypertreeMonitorHost? _hypertreeHost;
    private DaemonMonitorHost? _daemonHost;
    // The latest daemon roster, kept so the "show +N more" window opens on current data; the single
    // reused list window itself follows the WindowHost idiom below.
    private IReadOnlyList<DaemonWorker> _lastDaemonWorkers = [];
    private DaemonListWindow? _daemonListWindow;
    private QuickLinkLauncher? _quickLinkLauncher;
    private GitKrakenLauncher? _gitKrakenLauncher;
    private LiveOverlayWindow? _overlay;
    private SettingsWindow? _settings;
    private StatsWindow? _statsWindow;
    private AchievementsWindow? _achievementsWindow;
    private FlightPathWindow? _flightWindow;
    private ArcadeMenuWindow? _arcadeWindow;        // shhh
    private SpaceInvadersWindow? _invadersWindow;   // shhh
    private FroggerWindow? _froggerWindow;          // shhh
    private WordleWindow? _wordleWindow;            // shhh
    private HistoryWindow? _historyWindow;
    private GitTreeWindow? _treeWindow;
    private MarkdownWindow? _markdownWindow;
    private PlacementEditorWindow? _placementEditor;
    private ComposeWindow? _composeWindow;
    private FriendsWindow? _friendsWindow;
    private DebugSocialWindow? _debugSocialWindow;
    // Open sticky notes, keyed so a second request for the same note focuses the existing one rather than
    // stacking a duplicate: "__scratch__" for the global pad, the sessionId for a session's row note. They
    // are non-modal and owned by the overlay (see StickyNoteWindow); closed together in CloseAuxWindows.
    private readonly Dictionary<string, StickyNoteWindow> _noteWindows = new();
    private const string ScratchNoteKey = "__scratch__";
    // The searchable project-note picker (right-click the note button). A single reused instance, owned by
    // the overlay like the sticky notes; torn down in CloseAuxWindows.
    private ProjectNotePickerWindow? _projectPicker;
    // The user's to-do list: a single shared store, the reused editor window, and the poller that feeds the
    // overlay strip + fires due reminders. All three share the one TodoStore instance so an edit in the
    // window is seen by the poller (and vice versa) without reloading from disk.
    private TodoStore? _todoStore;
    private TodoWindow? _todoWindow;
    private Services.TodoMonitorHost? _todoHost;
    private AppSettings? _appSettings;
    // The effective settings: _appSettings with the "playful" features masked off while Quiet mode is active
    // (see Perch.Data.QuietMode). Behavioral reads use Effective; editing/persistence uses _appSettings.
    // Recomputed by ApplyEffectiveSettings on a settings change, a Quiet-mode toggle, and at window expiry.
    private AppSettings? _effectiveSettings;
    private DispatcherTimer? _quietTimer;
    private IClassicDesktopStyleApplicationLifetime? _desktop;

    // Debug-only replay transport, present only under `perch replay <recording>`. It advances the scrub
    // position, projects the sandbox, and reconciles the monitor. Null in a normal launch.
    private Services.Replay.ReplayController? _replayController;
    private ReplayControllerWindow? _replayWindow;

    // Notifications: the notifier (real Windows Action Center toasts, or the owner-drawn fallback off
    // Windows) + the toolkit-neutral dispatcher over it, plus the session-lock seam the dispatcher reads
    // for the AFK-lock external override.
    private INotifier? _notifier;
    private NotificationService? _notifications;
    private ISessionLock? _sessionLock;

    // Achievement badges: evaluates lifetime trophies against an all-time scan and toasts newly-unlocked
    // ones (once). The all-time scan is the slowest stats path, so checks are throttled — one at startup,
    // then at most one per AchievementCheckInterval when a session finishes.
    private AchievementService? _achievements;
    private DateTime _lastAchievementCheck = DateTime.MinValue;
    private bool _achievementCheckInFlight;
    private static readonly TimeSpan AchievementCheckInterval = TimeSpan.FromMinutes(3);
    // Above this many new unlocks in one check (a first run, a store migration, a long-away return), we
    // collapse to a single "you've earned N achievements" toast instead of stacking that many.
    private const int AchievementToastMax = 3;

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

    // Global keyboard shortcuts. Each configured binding gets its own IGlobalHotkey instance (a Windows
    // instance owns a fixed hotkey id + message-loop thread), rebuilt whenever the Hotkeys settings page
    // edits them. _lastCycledSessionId tracks the "jump to next session" round-robin; _switcher is the
    // single reused keyboard session-switcher popup.
    private readonly List<IGlobalHotkey> _hotkeys = new();
    private string? _lastCycledSessionId;
    private SessionSwitcherWindow? _switcher;

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            _desktop = desktop;
            desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown; // tray app — outlives its windows
            desktop.ShutdownRequested += (_, _) =>
            {
                _replayController?.Dispose();
                _replayWindow?.Close();
                _monitorHost?.Dispose();
                _usageHost?.Dispose();
                _metricsHost?.Dispose();
                _statusHost?.Dispose();
                _mediaHost?.Dispose();
                _micHost?.Dispose();
                _feedHost?.Dispose();
                _hypertreeHost?.Dispose();
                _daemonHost?.Dispose();
                _todoHost?.Dispose();
                foreach (var hk in _hotkeys) hk.Dispose();
                _sessionLock?.Dispose();
                _overlay?.Canvas.ReleaseDocked();   // give the reserved screen edge back to the desktop
                _overlay?.Canvas.DisposeDense();
                Services.Replay.ReplayBootstrap.Cleanup(); // delete the disposable replay sandbox
            };

            SetUpTray(desktop);

            // Live overlay + the data pipelines that feed it. Every host delivers on the UI thread, so
            // feeding the owner-drawn canvas from their callbacks is UI-thread-safe.
            _overlay = new LiveOverlayWindow();
            // Under `perch replay`, brand the overlay light-blue "Perch - Replay" (set before first paint)
            // so it's unmistakably a recording and not live sessions.
            _overlay.Canvas.ReplayMode = Services.Replay.ReplaySession.IsActive;
            var settings = AppSettings.Load();
            _appSettings = settings;

            // Seed the user-defined initial placements before the window is shown (OnOpened applies the
            // floating one; the dense one is used on first dense entry). Null on either keeps the default.
            _overlay.Canvas.SetInitialPlacements(settings.FloatingPlacement, settings.DensePlacement, settings.DockedPlacement);
            // Seed the user's chosen overlay widths (set in the placement editor; drag a live grip to adjust for
            // the session). Null keeps the default width for that presentation.
            _overlay.Canvas.SetFloatingWidth(settings.FloatingWidthDip);
            _overlay.Canvas.SetDockedWidth(settings.DockedWidthDip);

            // Register the curated palettes harvested from the ArcticGizmo package as built-in-like presets,
            // so a saved ActiveThemeId that names one (e.g. "nord-dark") resolves below.
            Perch.Theming.ThemeCatalog.RegisterImported(Theming.PaletteImport.All());

            // Colour theme first, before anything paints, so the overlay's first frame is already themed.
            // Via ThemeService so the Fluent variant is flipped for a light theme from the very first frame.
            ThemeService.Apply(Perch.Theming.ThemeCatalog.Resolve(settings.ActiveThemeId, settings.CustomThemes), desktop);

            // First launch after an update: grab the changelog entries newer than the version that last
            // ran here, then stamp the current version so they're only ever shown once. A null last-seen is
            // a fresh install (or the first run of this feature) — seed it silently, nothing to show.
            _pendingChangelog = ResolvePendingChangelog(settings);
            if (settings.LastSeenVersion != AppInfo.Version)
            {
                settings.LastSeenVersion = AppInfo.Version;
                settings.Save();
            }
            _usageHost = new UsageMonitorHost(_overlay.Canvas.UpdateUsage, PlatformServices.ClaudeCredentials);
            _metricsHost = new MetricsMonitorHost(PlatformServices.SystemMetrics,
                _overlay.Canvas.UpdateSystemMetrics, _overlay.Canvas.UpdateSessionMetrics);
            // Public Claude service status → the overlay's outage footer (only shown when there's an issue).
            _statusHost = new StatusMonitorHost(_overlay.Canvas.UpdateStatus, settings.ServiceStatusIntervalMinutes);
            // System media session → the overlay's now-playing strip (opt-in; started below when enabled).
            _mediaHost = new MediaMonitorHost(PlatformServices.CreateMediaController(), _overlay.Canvas.UpdateMedia);
            // Who holds the microphone → the overlay's mic strip (opt-in; started below).
            _micHost = new MicMonitorHost(
                PlatformServices.CreateMicrophoneMonitor(), _overlay.Canvas.UpdateMic);
            // Social feed backend (GitHub sign-in + friends/posts). Constructed always (cheap — no network
            // until you sign in); the overlay's sign-in strip only appears when SocialEnabled is on and you're
            // signed out. Push auth-state changes to the overlay, and restore any saved session in the
            // background (a no-op when unconfigured or signed out).
            _social = new SupabaseSocialClient(SupabaseConfig.Resolve(),
                PlatformServices.SecretStore, PlatformServices.UrlOpener);
            _feedHost = new SocialFeedMonitorHost(_social,
                snap => { _overlay?.Canvas.UpdateRoster(snap); CheckDnd(); },   // re-check DND on each roster tick
                OnFriendPosted,
                OnReactionToMyPost);
            _feedHost.Diagnostic += m => _reactionDiag?.Invoke(m);   // stream to the debug tool when it's open
            _overlay.Canvas.SetSocialRegionExpanded(settings.SocialRegionExpanded);
            _social.AuthChanged += st => Dispatcher.UIThread.Post(() =>
            {
                _overlay?.Canvas.SetSocialAccount(st.SignedIn, st.Me is not null);
                _feedHost?.SetActive(Effective.SocialEnabled && st.SignedIn);   // poll the feed while signed in (paused in Quiet mode)
                if (!st.SignedIn) { _reactionBubbles?.Close(); _reactionBubbles = null; }
            });
            _ = _social.TryRestoreAsync();
            // Hypertree's published status file → the overlay's branch strip (opt-in; started below).
            _hypertreeHost = new HypertreeMonitorHost(_overlay.Canvas.SetHypertree);
            _overlay.Canvas.SetHypertreeExpanded(settings.HypertreeExpanded);
            _overlay.Canvas.HypertreeExpandChanged += expanded =>
            {
                if (_appSettings is { } s) { s.HypertreeExpanded = expanded; s.Save(); }
            };
            // The Claude Code background daemon's worker roster → the overlay's "daemon" section. These
            // headless sessions have no window to focus, so they get their own rows with an options menu.
            // The full-list window (opened from the strip's overflow line) follows the roster live too.
            _daemonHost = new DaemonMonitorHost(workers =>
            {
                _lastDaemonWorkers = workers;
                _overlay!.Canvas.SetDaemonWorkers(workers);
                _daemonListWindow?.SetWorkers(workers);
            });

            // Each scan feeds both the canvas and the metrics sampler (which pids to measure). Under
            // replay the projector doubles as the process-probe so recorded (dead) pids read as alive.
            _monitorHost = new SessionMonitorHost(sessions =>
            {
                _lastSessions = sessions;
                _overlay!.Canvas.Update(sessions);
                _metricsHost!.SetSessionPids(sessions.Select(s => s.Pid));
                if (_historyWindow is { } h) h.SetActiveSessions(sessions);
                MaybeHandleAutoClose(sessions.Count);
            }, Services.Replay.ReplaySession.Current?.Projector);

            // One-shot grace timer: fires AutoCloseGraceMs after the last session ends; if still none by
            // then, an auto-started tray exits. Armed/cancelled from the scan callback above.
            _autoCloseTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(AutoCloseGraceMs) };
            _autoCloseTimer.Tick += (_, _) =>
            {
                _autoCloseTimer!.Stop();
                if (_monitorHost is not null && _lastSessionCount == 0) desktop.Shutdown();
            };

            // A session finishing or blocking flashes the overlay's attention chase-border (and expands
            // it if collapsed). Both fire the desktop toast + chime + external push (gated per settings)
            // via the notification dispatcher.
            _monitorHost.NeedsAttention += OnNeedsAttention;
            _monitorHost.AwaitingInput += OnAwaitingInput;
            _monitorHost.ApiError += OnApiError;
            _monitorHost.PrFinished += OnPrFinished;
            _monitorHost.PrReviewed += OnPrReviewed;
            _monitorHost.PrApproved += OnPrApproved;
            _monitorHost.OpenHistoryRequested += OpenHistory; // the plugin's jump-to-session

            // Row click focuses the session's terminal; the artifact glyph always pops a picker list, and
            // the chosen artifact is opened here.
            _overlay.Canvas.SessionActivated += FocusSession;
            _overlay.Canvas.ArtifactChosen += OpenArtifact;
            _overlay.Canvas.DaemonListRequested += OpenDaemonList;

            // Media transport buttons on the now-playing strip → the system media session.
            _overlay.Canvas.MediaPlayPauseRequested += () => _mediaHost?.Controller.TogglePlayPause();
            _overlay.Canvas.MediaNextRequested += () => _mediaHost?.Controller.Next();
            _overlay.Canvas.MediaPreviousRequested += () => _mediaHost?.Controller.Previous();

            // Mic strip: its one control is the app's name, which focuses whatever holds the microphone.
            _overlay.Canvas.MicJumpRequested += JumpToMicApp;

            // Social entry points from the overlay (strip + right-click menu) — the same actions as the
            // Settings Social page, so Social isn't Settings-only.
            _overlay.Canvas.SignInRequested += OnSocialSignInRequested;
            _overlay.Canvas.SignOutRequested += () => { _ = _social?.SignOutAsync(); };
            _overlay.Canvas.SocialManageRequested += OpenSocialSettings;
            _overlay.Canvas.PostStatusRequested += OpenCompose;
            _overlay.Canvas.FriendsRequested += OpenFriends;
            _overlay.Canvas.ReactRequested += OnReactRequested;
            _overlay.Canvas.SocialRegionExpandChanged += expanded =>
            {
                if (_appSettings is { } s) { s.SocialRegionExpanded = expanded; s.Save(); }
            };

            // Right-click context menu. The strip toggles persist and apply live; Exit shuts the app
            // down. History / QR / external-notify are Phase-5 concerns — their triggers are wired here so
            // the menu is complete, with best-effort/stub handlers until those windows land.
            _overlay.Canvas.ExitRequested += () => desktop.Shutdown();
            _overlay.Canvas.SetPlacementsRequested += OpenPlacementEditor;
            _overlay.Canvas.OverlayModeToggleRequested += ToggleOverlayMode;
            _overlay.Canvas.QuietModeRequested += OnQuietModeRequested;
            _overlay.Canvas.SystemMetricsToggleRequested += SetSystemMetricsEnabled;
            _overlay.Canvas.UsageToggleRequested += SetUsageEnabled;
            _overlay.Canvas.HistoryRequested += OpenHistory;
            _overlay.Canvas.QrRequested += ShowQrCode;
            _overlay.Canvas.ExternalNotifyToggleRequested += OnToggleExternalNotify;
            _overlay.Canvas.NoteEditRequested += OnEditNote;
            _overlay.Canvas.MarkdownRequested += OnOpenMarkdown;
            _overlay.Canvas.ViewTreeRequested += OnViewTree;
            // "Open in GitKraken" — only offered when GitKraken's CLI is on PATH (detected once). The launch
            // + window-focus runs off-thread through the platform activator (see GitKrakenLauncher).
            _gitKrakenLauncher = new GitKrakenLauncher(PlatformServices.WindowActivator);
            _overlay.Canvas.SetGitKrakenAvailable(GitKrakenLauncher.CliPath.Value is not null);
            _overlay.Canvas.OpenInGitKrakenRequested += s => _gitKrakenLauncher.Open(s.Cwd);
#if DEBUG
            // DEBUG-only: the PR glyph's right-click test items drive the real PR alert path (toast/chime/push
            // + banner) with the PR state/reviews synthesised, so any of them can be previewed without a real
            // merge/review. The overlay banner is forced (bypassing its setting) so both surfaces always show.
            _overlay.Canvas.DebugTestPrEventRequested += (s, kind) => FirePrEvent(s, kind, forceBanner: true);
#endif
            _overlay.Canvas.NoteClearRequested += sessionId => _monitorHost?.SetNote(sessionId, null);
            _overlay.Canvas.TerminateRequested += OnTerminateSession;
            _overlay.Canvas.ScratchPadRequested += OnOpenScratchPad;
            _overlay.Canvas.ProjectNotesRequested += OnOpenProjectNotePicker;

            // Quick-links strip: launch/focus goes through the platform seams; icons resolve off-thread.
            _quickLinkLauncher = new QuickLinkLauncher(PlatformServices.WindowActivator, PlatformServices.AppIconProvider);
            _overlay.Canvas.QuickLinkActivated += _quickLinkLauncher.LaunchOrFocus;

            // Hypertree branch strip: a click hands the jump to Hypertree's tray via its CLI.
            _overlay.Canvas.HypertreeRowActivated += OnHypertreeRowActivated;

            // Notifications: real Windows Action Center toasts (owner-drawn fallback off Windows); the
            // dispatcher gates toast/chime/external per settings. A toast click focuses that terminal and
            // acknowledges it.
            _sessionLock = PlatformServices.CreateSessionLock();
            Func<Screen?> toastScreen =
                () => _overlay is null ? null : _overlay.Screens.ScreenFromWindow(_overlay) ?? _overlay.Screens.Primary;
#if WINDOWS
            // The UWP Action Center notifier only exists in the Windows head; off Windows it isn't compiled.
            _notifier = OperatingSystem.IsWindows()
                ? new Notifications.WindowsToastNotifier()
                : new Notifications.AvaloniaToastNotifier(toastScreen);
#else
            _notifier = new Notifications.AvaloniaToastNotifier(toastScreen);
#endif
            _notifier.SessionActivated += OnToastActivated;
            _notifications = new NotificationService(_notifier, settings, _sessionLock, PlatformServices.AudioCue);
            _achievements = new AchievementService(AchievementStore.Load());

            // The user's to-do list: the shared store, the poller that keeps the overlay "To do" strip current
            // and fires due reminders. Started/stopped from ApplyDisplaySettings per the ShowTodos / reminders
            // settings. A Complete from the overlay writes through the same store and re-runs the poller.
            _todoStore = TodoStore.Load();
            _todoHost = new Services.TodoMonitorHost(
                _todoStore, (lines, total) => _overlay!.Canvas.SetTopTodos(lines, total), _notifications, settings);
            _overlay.Canvas.SetTodosExpanded(settings.TodosExpanded);
            _overlay.Canvas.TodosExpandChanged += expanded =>
            {
                if (_appSettings is { } s) { s.TodosExpanded = expanded; s.Save(); }
            };
            _overlay.Canvas.TodosRequested += OpenTodos;
            _overlay.Canvas.TodoCompleteRequested += id =>
            {
                _todoStore!.Complete(id);
                _todoStore.Save();
                _todoHost?.RefreshNow();
                _todoWindow?.Retarget();
            };

            // In-app updater: reflect availability on the tray item, the overlay badge and any open
            // Settings window. The overlay's badge click and the tray/Settings actions all route here.
            _updateService = new UpdateService(settings, _notifications);
            _updateService.AvailabilityChanged += OnUpdateAvailabilityChanged;
            _overlay.Canvas.UpdateRequested += () => _updateService!.PerformUpdate(CloseAuxWindows);
            // Secret: press and hold the brand mark (~700ms) to open the little arcade chooser (Invaders /
            // Crossing / Wordle). Once discovered, ArcadeUnlocked flips on and is persisted so the header's
            // right-click menu can offer a quick shortcut thereafter.
            _overlay.Canvas.ArcadeUnlocked = settings.ArcadeUnlocked;
            _overlay.Canvas.EasterEggTriggered += OpenArcade;
            _overlay.Canvas.ArcadeUnlockChanged += () =>
            {
                if (_appSettings is { } s && !s.ArcadeUnlocked) { s.ArcadeUnlocked = true; s.Save(); }
            };
            // Clicking the "update available" toast starts the update, same as the update button.
            _notifier.UpdateActivated += () => _updateService!.PerformUpdate(CloseAuxWindows);

            // Drive every overlay display gate + the monitor's data-layer toggles from persisted settings
            // (the Phase-3 Settings UI will edit these; this reads whatever's on disk, defaults included).
            // Goes through the Quiet-mode resolver so a quiet window restored from disk applies at startup.
            ApplyEffectiveSettings();

            _overlay.Show();

            // If the persisted mode is Docked, reserve the edge column now the window (and its handle) exist.
            // Floating is the default, so an older settings file just keeps floating.
            if (settings.OverlayMode == OverlayPresentationMode.Docked)
                _overlay.Canvas.SetOverlayMode(OverlayPresentationMode.Docked);

            // Once the UI is up, pop the post-update "what's new" window (if this launch detected an update).
            if (_pendingChangelog is { Count: > 0 } changelog)
                Dispatcher.UIThread.Post(() => ShowChangelog(changelog), DispatcherPriority.Background);

            _metricsHost.Configure(system: settings.ShowSystemMetrics, perSession: settings.ShowSessionMetrics, subprocess: settings.IncludeSubprocessMetrics);
            _monitorHost.Start(); // initial scan (we're on the UI thread here) — also sets the pids

            // Replay: hand the monitor over to the transport, which advances the scrub position, projects
            // the sandbox, and forces a rescan. The controller window binds play/pause/scrub/markers to
            // this engine. It starts paused at the beginning — the user hits Play (or scrubs) when ready.
            if (Services.Replay.ReplaySession.Current is { } replay)
            {
                _replayController = new Services.Replay.ReplayController(
                    replay.Projector, replay.SceneDurationMs, () => _monitorHost!.Reconcile());
                _replayWindow = new ReplayControllerWindow(
                    _replayController, Perch.Data.Replay.MarkerExtractor.Extract(replay.Recording));
                _replayWindow.Show();
            }
            CheckAchievements(force: true); // background all-time scan → celebrate anything unlocked while away
            if (settings.ShowUsage) _usageHost.Start(); // initial usage fetch (polls every 5 min thereafter)
            if (settings.ShowServiceStatus) _statusHost.Start(); // initial fetch (polls every 2 min thereafter)
            if (settings.ShowMediaController) _mediaHost.Start(); // begin listening to the system media session
            SyncMicMonitor();                                     // watch the mic if either strip needs it
            if (settings.HypertreeEnabled) _hypertreeHost.Start(); // begin polling Hypertree's status file
            // Begin watching the daemon roster (a no-op until the directory exists); off with the setting.
            if (settings.ShowDaemonProcesses) _daemonHost.Start();
            ReloadQuickLinks(settings);

            // Global hotkeys: dense-toggle, jump-to-next-session, and the keyboard switcher — each read
            // from settings and (re)registered together. Callbacks fire on a hotkey's own thread, so each
            // hops to the UI thread.
            RegisterHotkeys();

            // Re-dock the dense strip when monitors are added/removed (the controller self-heals to primary).
            if (_overlay.Screens is { } screens)
                screens.Changed += (_, _) => _overlay?.Canvas.OnScreensChanged();

            // Live-only startup work — skipped entirely under replay, which must never mutate the real
            // ~/.claude/settings.json, install hooks, register PATH, or nag about updates while it drives
            // a disposable sandbox.
            if (!Services.Replay.ReplaySession.IsActive)
            {
                // Startup + hourly update check (restores a persisted "update available" state first).
                _updateService.Start();

                // Self-managed hooks: on every launch, copy perch-hook to a stable per-user path and
                // reconcile our managed block in ~/.claude/settings.json (idempotent; self-corrects after
                // an update changes the versioned install dir), then migrate any user still on the retired
                // marketplace plugin so events aren't delivered twice. All off the UI thread, best-effort.
                System.Threading.Tasks.Task.Run(async () =>
                {
                    // macOS has no Velopack install callback (the .app is drag-installed), so keep the
                    // `perch` PATH symlink in sync here instead — but only for a real installed bundle, never a
                    // dev `dotnet run` (which would point ~/.local/bin/perch at a throwaway build dir). On
                    // Windows the equivalent runs from Velopack's install/update fast callbacks (see Program).
                    if (!OperatingSystem.IsWindows() && IsInsideAppBundle())
                        PlatformServices.PathInstaller.Register();

                    // Re-assert the login registration against the setting: registering again refreshes a
                    // path an update (or a move) left stale, and a stray registration is cleared if the
                    // mode is no longer "on login" — e.g. the settings file was edited while Perch was shut.
                    SyncLoginItem(settings.StartMode);

                    HookInstaller.Install();
                    await MigrateOffPlugin();
                });
            }
        }
        base.OnFrameworkInitializationCompleted();
    }

    /// <summary>Brings the OS login registration in line with <paramref name="mode"/> — registered only
    /// while the mode is <see cref="StartMode.OnLogin"/>. Called at startup and whenever the Getting
    /// started page changes the mode. Best-effort: the registration is a convenience, never load-bearing.</summary>
    internal static void SyncLoginItem(StartMode mode)
    {
        try
        {
            if (mode == StartMode.OnLogin) PlatformServices.LoginItem.Register();
            else if (PlatformServices.LoginItem.IsRegistered()) PlatformServices.LoginItem.Unregister();
        }
        catch { /* best-effort */ }
    }

    // True when this process is running from inside a macOS .app bundle (…/Perch.app/Contents/MacOS/perch),
    // i.e. an installed build rather than a `dotnet run` from the repo. Gates the mac PATH-symlink install
    // so a dev run never clobbers an installed Perch's ~/.local/bin/perch link with a build-output path.
    private static bool IsInsideAppBundle() =>
        Environment.ProcessPath?.Contains("/Contents/MacOS/", StringComparison.Ordinal) == true;

    // One-time-ish migration off the retired marketplace plugin. Only acts when the plugin/marketplace
    // is still registered (a fast settings.json read), so it's a no-op for fresh installs and on every
    // launch after the first successful migration. Best-effort — any failure is swallowed.
    private static async System.Threading.Tasks.Task MigrateOffPlugin()
    {
        try
        {
            var (marketplace, plugin) = PluginManager.ReadInstalledState();
            if (!marketplace && !plugin) return;
            await new PluginManager().RemoveAsync();
        }
        catch { /* best-effort */ }
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
        _treeWindow?.Close();
        _markdownWindow?.CloseWithoutPrompt();
        _placementEditor?.Close();
        _composeWindow?.Close();
        _friendsWindow?.Close();
        _debugSocialWindow?.Close();
        _statsWindow?.Close();
        _daemonListWindow?.Close();
        _achievementsWindow?.Close();
        _achievementCard?.Close();
        _reactionBubbles?.Close();
        _flightWindow?.Close();
        _arcadeWindow?.Close();
        _invadersWindow?.Close();
        _froggerWindow?.Close();
        _wordleWindow?.Close();
        _qrWindow?.Close();
        _changelogWindow?.Close();
        _switcher?.Close();
        _projectPicker?.Close();
        _todoWindow?.Close();
        foreach (var note in _noteWindows.Values.ToList())
            note.CloseWithoutPrompt();
    }

    // Focuses the terminal hosting a clicked session (sub-agent rows already resolve to their parent) and
    // acknowledges it, so clicking a finished ("done"/NeedsAttention) session clears its badge — the
    // WinForms SessionFocused → AcknowledgeSession behaviour. Acknowledge is a no-op for a session that
    // isn't done, and rescans so the overlay refreshes.
    private void FocusSession(ClaudeSession session)
    {
        if (int.TryParse(session.Pid, out int pid))
        {
            if (session.IsDesktop)
                FocusDesktopSession(session, pid);
            else if (session.IsBackground)
                // An autonomous / SDK-driven run has no window of its own to bring forward — the same
                // reason the jump hotkey and switcher skip these. Say so and acknowledge; walking the
                // process ancestry (as FocusTerminalForProcess does) would only risk un-hiding some
                // unrelated host window — the shell that launched the agent, say — which isn't the session.
                _notifier?.Show(
                    "Autonomous session",
                    $"{session.DisplayName} is an autonomous (SDK) session — there's no window to open.",
                    ToastLevel.Info, null, null);
            else if (!PlatformServices.WindowActivator.FocusTerminalForProcess(pid, session.ProjectName))
            {
                // The session is alive but has no terminal to bring forward — its window was hidden or torn
                // down while the process kept running. Say so: an unexplained no-op click reads as Perch
                // being broken, and the row itself gives no hint that there's nothing behind it. A plain
                // info toast (null pid), so clicking it just dismisses rather than retrying the failed focus.
                _notifier?.Show(
                    "No window to focus",
                    $"{session.DisplayName} is still running (PID {pid}) but has no terminal window on "
                        + "screen. Right-click the row to terminate it.",
                    ToastLevel.Warning, null, null);
            }
        }
        _monitorHost?.Acknowledge(session.Pid);
    }

    // Focuses a Claude Desktop session. The claude process runs under the Claude Desktop app, whose window
    // is a process *ancestor*, so FocusAppWindowForProcess walks up to it and raises it (it can't pick out
    // the specific in-app session — one app window hosts them all, the best Win32 allows). When no window
    // is found the app has been quit or closed to the tray, so we launch/re-activate it and say so rather
    // than leaving the click dead.
    private void FocusDesktopSession(ClaudeSession session, int pid)
    {
        if (PlatformServices.WindowActivator.FocusAppWindowForProcess(pid)) return;

        if (PlatformServices.SessionLauncher.OpenClaudeDesktop())
            _notifier?.Show(
                "Opening Claude Desktop",
                $"Claude Desktop wasn't showing a window, so Perch is bringing it up for {session.DisplayName}.",
                ToastLevel.Info, null, null);
        else
            _notifier?.Show(
                "Couldn't open Claude Desktop",
                $"{session.DisplayName} is hosted by Claude Desktop, which has no window on screen and "
                    + "couldn't be launched. Open it manually to get back to this session.",
                ToastLevel.Warning, null, null);
    }

    // Terminates a session from the row's right-click menu, after an explicit confirmation — this kills
    // the process tree, so whatever turn it was mid-way through is lost. The escape hatch for a session
    // that has outlived its terminal (see FocusSession): still running, still holding context, with no
    // window left to interrupt it in.
    private async void OnTerminateSession(ClaudeSession session)
    {
        if (_overlay is not { } owner) return;

        bool confirmed = await ConfirmDialog.ShowAsync(
            owner,
            "Terminate session?",
            $"Kill {session.DisplayName} (PID {session.Pid}) and everything it started? "
                + "Any work in its current turn is lost. This can't be undone.",
            "Terminate", "Cancel");
        if (!confirmed) return;

        var result = SessionTerminator.Terminate(session.Pid);

        // The kill leaves the {pid}.json behind, so no watcher event fires — rescan to drop the row (the
        // monitor discards a session file whose pid is dead).
        _monitorHost?.Rescan();

        var (title, body, level) = result switch
        {
            TerminateResult.Terminated => ("Session terminated",
                $"{session.DisplayName} (PID {session.Pid}) was stopped.", ToastLevel.Info),
            TerminateResult.AlreadyGone => ("Session already gone",
                $"{session.DisplayName} had already exited.", ToastLevel.Info),
            TerminateResult.NotTheSession => ("Not terminated",
                $"PID {session.Pid} no longer looks like {session.DisplayName}, so Perch left it alone.",
                ToastLevel.Warning),
            _ => ("Couldn't terminate",
                $"{session.DisplayName} (PID {session.Pid}) refused to stop — it may be running elevated.",
                ToastLevel.Error),
        };
        _notifier?.Show(title, body, level, null, null);
    }

    // Opens the artifact the user picked from the overlay's artifact-glyph list. Middle-click asks for a
    // fresh browser window (lands on the current virtual desktop) instead of reusing a running instance.
    private static void OpenArtifact(Artifact artifact, bool newWindow)
    {
        if (newWindow) PlatformServices.UrlOpener.OpenInNewWindow(artifact.Url);
        else PlatformServices.UrlOpener.Open(artifact.Url);
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
    // ── Quiet mode ───────────────────────────────────────────────────────────────────────────────────────
    // The effective settings (playful features masked while quiet); falls back to the raw settings before the
    // first resolve. Non-null once _appSettings is set (which is before any behavioral read can fire).
    private AppSettings Effective => _effectiveSettings ?? _appSettings!;

    private bool QuietActive => _appSettings is { } s && QuietMode.IsActive(s.QuietUntil, DateTime.Now);

    // Recompute the effective settings from the raw settings + the live quiet state, push them onto every gate
    // and host (ApplyDisplaySettings), and (re)arm the one-shot expiry timer. The single place quiet state and
    // a settings change both funnel through, so the two can never drift.
    private void ApplyEffectiveSettings()
    {
        if (_appSettings is not { } raw) return;
        _effectiveSettings = QuietMode.Resolve(raw, QuietActive);
        ApplyDisplaySettings(_effectiveSettings);
        ScheduleQuietExpiry();
    }

    // Arms a one-shot timer to un-quiet exactly when the window ends; a null/past deadline just clears it.
    private void ScheduleQuietExpiry()
    {
        _quietTimer?.Stop();
        _quietTimer = null;
        if (_appSettings?.QuietUntil is not { } until) return;
        var delay = until - DateTime.Now;
        if (delay <= TimeSpan.Zero) return;   // already elapsed; the next resolve reads it as off
        _quietTimer = new DispatcherTimer { Interval = delay };
        _quietTimer.Tick += (_, _) => OnQuietExpired();
        _quietTimer.Start();
    }

    private void OnQuietExpired()
    {
        _quietTimer?.Stop();
        _quietTimer = null;
        if (_appSettings is { } s) { s.QuietUntil = null; s.Save(); }
        ApplyEffectiveSettings();   // bring the playful features back
    }

    // The header menu picked a Quiet-mode duration (or "off"): set the deadline, persist, and re-resolve so the
    // playful features go quiet / return at once.
    private void OnQuietModeRequested(QuietDuration duration)
    {
        if (_appSettings is not { } s) return;
        s.QuietUntil = QuietMode.DeadlineFor(duration, DateTime.Now);
        s.Save();
        ApplyEffectiveSettings();
    }

    private void ApplyDisplaySettings(AppSettings s)
    {
        if (_overlay is null) return;

        // Push every display gate onto the live canvas. The same helper drives the Settings live-preview
        // pane against a detached canvas + a cloned AppSettings, so preview and overlay can't diverge.
        OverlaySettingsGates.Apply(_overlay.Canvas, s);

        // Poll the feed only while Social is enabled and signed in (turning Social off stops the poll).
        _feedHost?.SetActive(s.SocialEnabled && (_social?.Current.SignedIn ?? false));

        // Run the to-do poller while either the overlay strip or the due reminders are enabled; stop it (and
        // clear the strip) when both are off. The canvas gate (SetShowTodos, applied above) hides the strip
        // independently, so the poller can keep firing reminders with the strip turned off.
        if (_todoHost is { } todoHost)
        {
            if (s.ShowTodos || s.TodoRemindersEnabled) todoHost.Start();
            else todoHost.Stop();
        }

        // Watch Windows Do Not Disturb only while Social is on and the auto-close option is enabled.
        ApplyDndMonitor(s);

        // Data-layer sources for the git chip / stuck glyph (off in the monitor unless enabled here).
        if (_monitorHost is not null)
        {
            _monitorHost.GitStatsEnabled = s.ShowGitStats;
            _monitorHost.StuckDetectionEnabled = s.StuckDetectionEnabled;
            _monitorHost.PrEnabled = s.ShowPullRequests;
            _monitorHost.PrIntervalMinutes = s.PullRequestIntervalMinutes;
            _monitorHost.JiraEnabled = s.ShowJiraTicket;
            _monitorHost.JiraSubdomain = s.JiraSubdomain;
            _monitorHost.JiraProjectFilter = s.JiraProjectFilter;
        }
    }

    // The overlay sign-in strip was clicked: run the GitHub sign-in (browser + loopback). SignInAsync is
    // async throughout, so the UI stays responsive while the user completes it in the browser; AuthChanged
    // then hides the strip. Errors surface as a toast rather than throwing out of the event handler.
    private async void OnSocialSignInRequested()
    {
        if (_social is null) return;
        try
        {
            await _social.SignInAsync();
        }
        catch (SocialException ex)
        {
            _notifier?.Show("Perch Social", ex.Message, ToastLevel.Info, null, null);
        }
        catch
        {
            _notifier?.Show("Perch Social", "Sign-in didn't complete. Please try again.", ToastLevel.Info, null, null);
        }
    }

    // Open Settings on the Social page (from the overlay strip's "finish setup" click or its menu item).
    private void OpenSocialSettings()
    {
        OpenSettings();
        _settings?.NavigateTo("social");
    }

    // Compose a status — posts via the client, then refreshes the feed so it appears at once.
    private void OpenCompose()
    {
        if (_social is null) return;
        _composeWindow = WindowHost.ShowOrFocus(
            _composeWindow,
            () => new ComposeWindow(async (body, mood) =>
            {
                await _social.PostAsync(body, mood);
                _feedHost?.RefreshSoon();
            }, _social.Current.Me?.MoodEmoji),   // seed the composer with your current mood
            () => _composeWindow = null);
    }

    // Manage friends (add / accept / list).
    private void OpenFriends()
    {
        if (_social is null) return;
        _friendsWindow = WindowHost.ShowOrFocus(
            _friendsWindow,
            () => new FriendsWindow(_social, () => _feedHost?.RefreshSoon()),
            () => _friendsWindow = null,
            w => w.RefreshExternal());
    }

    // Developer testing tool: drive a puppet account (gated behind PERCH_SOCIAL_DEBUG; the Settings button
    // that opens this only shows when that flag is set).
    private void OpenSocialDebug()
    {
        if (_social is null) return;
        _debugSocialWindow = WindowHost.ShowOrFocus(
            _debugSocialWindow,
            () =>
            {
                var w = new DebugSocialWindow(_social, () => _feedHost?.RefreshSoon(), ShowReactionBubble,
                    () => $"ShowLargeReactions={_appSettings?.ShowLargeReactions}, DND active={_dndActive}, " +
                          $"DND suppressing={DndSuppressing}, SocialEnabled={_appSettings?.SocialEnabled}, " +
                          $"feed polling={_feedHost is not null}");
                _reactionDiag = m => Dispatcher.UIThread.Post(() => w.Diag(m));   // stream host + gate diagnostics
                return w;
            },
            () => { _reactionDiag = null; _debugSocialWindow = null; });
    }

    // A reaction chip / "+" picker in the overlay social region was used: toggle the reaction on the backend,
    // then refresh the roster so the chip settles to the server truth. Errors are swallowed (best-effort, like
    // the other social overlay actions) — a failed reaction just leaves the chip as it was.
    private async void OnReactRequested(Guid postId, string emoji, bool on)
    {
        if (_social is null) return;
        try
        {
            await _social.ReactAsync(postId, emoji, on);
            _feedHost?.RefreshSoon();
        }
        catch (SocialException ex)
        {
            // Surface it — a reaction that silently does nothing is impossible to diagnose (e.g. the reactions
            // migration not applied yet, or an RLS denial).
            _notifier?.Show("Perch Social", $"Couldn't react: {ex.Message}", ToastLevel.Info, null, null);
        }
        catch { /* transient network blip — the next poll reconciles */ }
    }

    // ── Do Not Disturb → close the friends region ────────────────────────────────────────────────────────
    // No dedicated poll: the DND state is re-checked whenever the feed's roster stream ticks (the 60s poll, or a
    // realtime nudge) and once when the setting changes. Lightweight for a low-stakes convenience.
    private bool _dndActive;

    private void ApplyDndMonitor(AppSettings s)
    {
        if (s.SocialEnabled && s.CloseFeedInDoNotDisturb) CheckDnd();   // apply immediately on enable
        else _dndActive = false;
    }

    // On the rising edge (not-DND → DND), collapse the region once (it won't spring back open on a new post).
    // Guarded on the setting so it's a no-op unless enabled. IsActive is a cheap OS query, safe on the UI thread.
    private void CheckDnd()
    {
        if (_appSettings is not { SocialEnabled: true, CloseFeedInDoNotDisturb: true }) { _dndActive = false; return; }
        bool now;
        try { now = PlatformServices.DoNotDisturb.IsActive; } catch { now = false; }
        if (now && !_dndActive) _overlay?.Canvas.SetSocialRegionExpanded(false);
        _dndActive = now;
    }

    private bool DndSuppressing => _dndActive && (_appSettings?.CloseFeedInDoNotDisturb ?? false);

    // A friend posted a new status (surfaced by the feed poll, whether nudged live by Realtime or found on the
    // next tick): a quiet desktop toast, gated by the master notifications switch and NotifyOnFriendPost. Never
    // fires for your own posts or the backlog present when the feed starts (see SocialFeedMonitorHost).
    private void OnFriendPosted(FeedItem item)
    {
        if (Effective is not { NotificationsEnabled: true, NotifyOnFriendPost: true }) return;   // NotifyOnFriendPost is masked off in Quiet mode
        if (DndSuppressing) return;   // Do Not Disturb: stay quiet
        var body = item.Body.Length <= 120 ? item.Body : item.Body[..117] + "…";
        _notifier?.Show($"@{item.Author.Handle} just posted", body, ToastLevel.Info, null, null);
    }

    // A session finished (NeedsAttention): flash the overlay and fire the notification (toast/chime/external,
    // gated per settings).
    // True when the session is one of the daemon's headless background workers. Those are deliberately
    // silent: no attention flash, no toast/chime/external push — they have no terminal to jump to, so an
    // alert would only lead to a dead click. Read from the roster watcher's latest list, which is empty
    // while the "Display daemon processes" setting is off (daemon alerts return with the section).
    private bool IsDaemonSession(ClaudeSession session)
        => _lastDaemonWorkers.Any(w => w.SessionId == session.SessionId);

    private void OnNeedsAttention(ClaudeSession session)
    {
        if (IsDaemonSession(session)) return;
        _overlay!.Canvas.TriggerAttention(SessionStatus.NeedsAttention);
        _notifications?.Notify(NotificationKind.Done, session);
        CheckAchievements(force: false); // a finish is a natural moment to have crossed a threshold
    }

    // Evaluates lifetime achievement badges off the UI thread and toasts any newly-unlocked ones (once,
    // via the store). The all-time scan is the slowest stats path, so this is throttled (force bypasses it
    // for the startup check) and single-flighted so overlapping finishes can't stack scans.
    private void CheckAchievements(bool force)
    {
        if (_achievements is not { } svc || _appSettings is not { } settings || _achievementCheckInFlight)
            return;
        var now = DateTime.Now;
        if (!force && now - _lastAchievementCheck < AchievementCheckInterval)
            return;
        _lastAchievementCheck = now;
        _achievementCheckInFlight = true;

        bool includeCost = settings.ShowEstimatedCost;
        var today = DateOnly.FromDateTime(now);
        Task.Run(() =>
        {
            var range = SessionStatsService.ReportAllTime(today);
            return svc.Sync(range.Totals, range, includeCost);
        }).ContinueWith(t => Dispatcher.UIThread.Post(() =>
        {
            _achievementCheckInFlight = false;
            if (!t.IsCompletedSuccessfully || t.Result.Count == 0)
                return;
            PresentAchievementUnlocks(t.Result);
        }));
    }

    // Announces a batch of freshly-crossed rungs through two independent channels, each with its own gate:
    // optional desktop toasts (off by default — noisy; a summary for a big batch, else one each), and the
    // full-screen card reveal (the primary celebration). When both are off the batch unlocks silently — the
    // levels are still recorded and show in the Achievements window.
    private void PresentAchievementUnlocks(IReadOnlyList<AchievementUnlock> unlocks)
    {
        // The unlocks are already recorded by the achievement store; this only presents them, so the celebration
        // gates read the effective settings — NotifyOnAchievement / AchievementToasts are masked off in Quiet mode.
        if (unlocks.Count == 0 || _appSettings is null) return;
        var settings = Effective;

        if (settings.AchievementToasts && _notifications is { } n)
        {
            if (unlocks.Count > AchievementToastMax)
                n.ShowInfo("🏆 Achievements unlocked",
                    $"You've earned {unlocks.Count} achievements — open Achievements to see them.", ToastLevel.Info);
            else
                foreach (var u in unlocks)
                    n.ShowInfo("🏆 Achievement unlocked", $"{u.Emoji} {u.Name} — {u.Criteria}", ToastLevel.Info);
        }

        // The reveal stays reserved for a notable batch — one that crossed a rare gold-tier rung — so a lone
        // bronze unlock mid-work won't throw up a full-screen card. It shows the whole batch (shiniest first,
        // up to a few cards) with the rest folded into a "+N more" card.
        if (settings.NotifyOnAchievement && unlocks.Any(u => u.Tier == AchievementTier.Gold))
            ShowAchievementCards(unlocks);
    }

    // A session blocked awaiting input: flash the overlay and fire the "waiting for input" notification.
    private void OnAwaitingInput(ClaudeSession session)
    {
        if (IsDaemonSession(session)) return;
        _overlay!.Canvas.TriggerAttention(SessionStatus.AwaitingInput);
        _notifications?.Notify(NotificationKind.WaitingForInput, session);
    }

    // A session's last API request failed (e.g. 529 Overloaded): flash the overlay and fire the API-error
    // notification. Deliberately not a "done" — this is the failure alert that replaces it.
    private void OnApiError(ClaudeSession session)
    {
        if (IsDaemonSession(session)) return;
        _overlay!.Canvas.TriggerAttention(SessionStatus.ApiError);
        _notifications?.Notify(NotificationKind.ApiFailed, session);
    }

    // A tracked PR changed state (merged/closed, reviewed, approved): fire the matching desktop alert (toast/
    // chime/push, each gated inside NotificationService) and — if the banner setting is on — the overlay
    // banner, two independent surfaces for the same event.
    private void OnPrFinished(ClaudeSession session) => FirePrEvent(session, NotificationKind.PrFinished, forceBanner: false);
    private void OnPrReviewed(ClaudeSession session) => FirePrEvent(session, NotificationKind.PrReviewed, forceBanner: false);
    private void OnPrApproved(ClaudeSession session) => FirePrEvent(session, NotificationKind.PrApproved, forceBanner: false);

    private void FirePrEvent(ClaudeSession session, NotificationKind kind, bool forceBanner)
    {
        if (IsDaemonSession(session)) return;
        _notifications?.Notify(kind, session);
        if ((forceBanner || _appSettings?.PrFinishedOverlayBanner == true) && session.PullRequest is { } pr)
            _overlay?.Canvas.ShowPrBanner(session.SessionId, PrBannerText(kind, pr), PrBannerKindOf(kind, pr));
    }

    // The banner's label — concise, naming the reviewer/approver where relevant (the toast carries the
    // fuller wording).
    private static string PrBannerText(NotificationKind kind, PullRequestInfo pr) => kind switch
    {
        NotificationKind.PrFinished => pr.State == PrState.Closed ? "Closed" : "Merged",
        NotificationKind.PrApproved => pr.NewestApproval?.Author is { Length: > 0 } a ? $"Approved by {a}" : "Approved",
        _ => pr.NewestReview is { State: PrReviewState.ChangesRequested, Author: { Length: > 0 } cr } ? $"{cr} requested changes"
           : pr.NewestReview?.Author is { Length: > 0 } r ? $"Reviewed by {r}"
           : "Reviewed",
    };

    // The banner's colour, from the event kind (and, for reviews, the review's state).
    private static OverlayCanvas.PrBannerKind PrBannerKindOf(NotificationKind kind, PullRequestInfo pr) => kind switch
    {
        NotificationKind.PrFinished => pr.State == PrState.Closed ? OverlayCanvas.PrBannerKind.Closed : OverlayCanvas.PrBannerKind.Merged,
        NotificationKind.PrApproved => OverlayCanvas.PrBannerKind.Approved,
        _ => pr.NewestReview?.State == PrReviewState.ChangesRequested
            ? OverlayCanvas.PrBannerKind.ChangesRequested : OverlayCanvas.PrBannerKind.Reviewed,
    };

    // A toast was clicked: focus the session's terminal and acknowledge it (clears the "done" badge) —
    // the Avalonia counterpart of the WinForms balloon-click handler.
    private void OnToastActivated(string pid, string? project)
    {
        // Focus failure is deliberately swallowed here, unlike in FocusSession: answering a toast click
        // with another toast about the first toast is worse than doing nothing quietly.
        if (int.TryParse(pid, out int p))
            _ = PlatformServices.WindowActivator.FocusTerminalForProcess(p, project);
        _monitorHost?.Acknowledge(pid);
    }

    // Reveals the achievement card(s) — a vignette + coin-flip reveal in the middle of the overlay's
    // screen. "Don't show again" turns off "Celebrate new unlocks" (NotifyOnAchievement) — the same
    // setting the toggle on the Achievements settings page drives — so it never fires again until
    // re-enabled there. Reuses one live window across batches.
    // "Big reactions": a friend reacted to your own status. Float it up the screen as a large poppable bubble,
    // gated on the setting (and quiet in Do Not Disturb, like the friend-post toasts). Arrives on the UI
    // thread from SocialFeedMonitorHost. Best-effort — a missing overlay/screen just skips the flourish.
    private ReactionBubbleWindow? _reactionBubbles;
    private Action<string>? _reactionDiag;   // set by the debug tool while it's open; streams gate + poll diagnostics
    private void OnReactionToMyPost(string emoji)
    {
        bool showByGate = Effective is { ShowLargeReactions: true };   // masked off in Quiet mode
        bool suppressed = _dndActive && (_appSettings?.CloseFeedInDoNotDisturb ?? false);
        _reactionDiag?.Invoke($"handler: {emoji} — ShowLargeReactions={_appSettings?.ShowLargeReactions}, " +
            $"DND suppressing={suppressed} -> {(showByGate && !suppressed ? "SHOWING bubble" : "BLOCKED by a gate")}");
        if (!showByGate || suppressed) return;
        ShowReactionBubble(emoji);
    }

    // Puts a bubble on screen, creating the layer window if needed. Separate from OnReactionToMyPost's gates so
    // the debug testing tool can force a bubble regardless of the setting / Do Not Disturb.
    private void ShowReactionBubble(string emoji)
    {
        if (_overlay is null) return;
        var screen = _overlay.Screens.ScreenFromWindow(_overlay) ?? _overlay.Screens.Primary;
        if (screen is null) return;

        if (_reactionBubbles is null)
        {
            _reactionBubbles = new ReactionBubbleWindow();
            _reactionBubbles.TurnOff += () =>
            {
                if (_appSettings is not { } s) return;
                s.ShowLargeReactions = false;
                s.Save();
                _settings?.SyncLargeReactions();   // keep an open settings window's toggle in step
            };
            _reactionBubbles.Closed += (_, _) => _reactionBubbles = null;
            _reactionBubbles.Present(screen);
        }
        _reactionBubbles.Spawn(emoji);
    }

    private AchievementCardWindow? _achievementCard;
    private void ShowAchievementCards(IReadOnlyList<AchievementUnlock> unlocks)
    {
        if (_overlay is null || unlocks.Count == 0) return;
        var screen = _overlay.Screens.ScreenFromWindow(_overlay) ?? _overlay.Screens.Primary;
        if (screen is null) return;

        if (_achievementCard is { IsVisible: true } live)
        {
            live.Enqueue(unlocks);   // a reveal's still up — add these to its queue
            return;
        }
        _achievementCard = new AchievementCardWindow();
        _achievementCard.DoNotShowAgain += () =>
        {
            if (_appSettings is not { } s) return;
            s.NotifyOnAchievement = false;
            s.Save();
            _settings?.SyncAchievementCelebration();   // keep an open settings window's toggle in step
        };
        _achievementCard.Closed += (_, _) => _achievementCard = null;
        _achievementCard.Present(unlocks, screen);
    }

    // ── Context-menu handlers ─────────────────────────────────────────────────
    // Toggle the whole-machine metrics strip from the overlay's right-click menu. This is a full settings
    // change — the same one the "System metrics" toggle in Settings makes: persist the flag, apply it to
    // the canvas, reconfigure the sampler (so turning it on actually starts collection and off stops it),
    // and keep an open Settings window's toggle in step.
    private void SetSystemMetricsEnabled(bool enabled)
    {
        if (_appSettings is null || _overlay is null) return;
        if (_appSettings.ShowSystemMetrics == enabled) return;
        _appSettings.ShowSystemMetrics = enabled;
        _appSettings.Save();
        _overlay.Canvas.SetShowSystemMetrics(enabled);
        _metricsHost?.Configure(_appSettings.ShowSystemMetrics, _appSettings.ShowSessionMetrics, _appSettings.IncludeSubprocessMetrics);
        _settings?.SyncDisplayToggles();
    }

    // Toggle the account-usage strip from the right-click menu — the counterpart of the Settings "Usage
    // limits" toggle: persist the flag, apply it to the canvas, start/stop the poller (so turning it on
    // fetches data rather than showing an empty strip), and sync an open Settings window.
    private void SetUsageEnabled(bool enabled)
    {
        if (_appSettings is null || _overlay is null) return;
        if (_appSettings.ShowUsage == enabled) return;
        _appSettings.ShowUsage = enabled;
        _appSettings.Save();
        _overlay.Canvas.SetShowUsage(enabled);
        if (enabled) _usageHost?.Start(); else _usageHost?.Stop();
        _settings?.SyncDisplayToggles();
    }

    // The mic monitor backs two things: the mic strip itself, and the media strip's suppression of a call app
    // that grabbed the media controls (which needs to know who holds the mic). So it runs whenever either
    // strip is enabled and stops only when neither does. Start/Stop are idempotent, so this is safe to call
    // on every relevant settings change.
    private void SyncMicMonitor()
    {
        if (_appSettings is not { } s || _micHost is null) return;
        if (s.ShowMicPresence || s.ShowMediaController) _micHost.Start();
        else _micHost.Stop();
    }

    // "Take me back to the call I'm talking into": focus the window of whatever app currently holds the
    // microphone. The pid to hand over is the one owning the capture stream, which for Teams (and any
    // Electron/WebView2 app) is a windowless media child process — FocusAppWindowForProcess widens the
    // search to the app's other processes, and a window parked on another virtual desktop is the normal case
    // here rather than a problem: foregrounding it makes Windows switch desktop, which is the whole point.
    private void JumpToMicApp()
    {
        if (_micHost?.Microphone.Current?.Primary is not { } holder || holder.ProcessId <= 0) return;
        if (PlatformServices.WindowActivator.FocusAppWindowForProcess(holder.ProcessId)) return;

        // Same reasoning as FocusSession: a click that silently does nothing reads as Perch being broken,
        // and the strip gives no hint that the app has no window to go to.
        _notifier?.Show(
            "No window to focus",
            $"{holder.DisplayName} is using your microphone (PID {holder.ProcessId}) but has no window "
                + "on screen to bring forward.",
            ToastLevel.Warning, null, null);
    }

#if DEBUG
    // A sample outage the Settings "Show example outage" button pushes onto the overlay so the footer can
    // be eyeballed without waiting for a real incident. A real poll replaces it within a couple of minutes.
    private static StatusInfo SampleOutage() =>
        new(StatusLevel.Major, "Partial System Outage",
            [new StatusIncident("Elevated errors on the Messages API", "major", "investigating",
                "We are investigating elevated error rates.", "https://status.claude.com")],
            StatusInfo.DefaultPageUrl, DateTime.Now, true, null);

    // Four fake unlocks (a mix of tiers) the Settings "Simulate 4 unlocks" button fires through the real
    // announce path, to test the post-update / first-run batch case (a single summary toast, no cards).
    private static IReadOnlyList<AchievementUnlock> SampleAchievementBatch() =>
    [
        new("Token Titan", "🏆", "Tokens · Lvl 5", "1B input tokens", AchievementTier.Gold),
        new("Night Owl", "🦉", "Sessions · Lvl 3", "100 sessions", AchievementTier.Silver),
        new("Streak Keeper", "🔥", "Streak · Lvl 2", "7-day streak", AchievementTier.Bronze),
        new("Tool Master", "🛠", "Tools · Lvl 4", "100,000 tool calls", AchievementTier.Gold),
    ];
#endif

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

    // "Set initial placements…" (overlay header) — opens/focuses the placement editor on the overlay's
    // current monitor, seeded with the saved placements and the real preview sizes so what's dragged
    // matches what will appear. Commit persists via ApplyPlacements.
    private void OpenPlacementEditor()
    {
        if (_overlay is not { } o || o.Screens is not { } screens) return;
        var screen = screens.ScreenFromWindow(o) ?? screens.Primary
                     ?? (screens.All.Count > 0 ? screens.All[0] : null);
        if (screen is null) return;

        var ctx = new PlacementEditorContext(
            screen,
            _appSettings?.FloatingPlacement, _appSettings?.DensePlacement, _appSettings?.DockedPlacement,
            Views.OverlayCanvas.DefaultFloatingPlacement(), o.Canvas.DefaultDensePlacement(),
            Views.OverlayCanvas.DefaultDockedPlacement(),
            o.Canvas.FloatingMockSizeDip(), o.Canvas.DenseMockSizeDip(), o.Canvas.DockedMockSizeDip(),
            Views.OverlayCanvas.MinOverlayWidthDip,
            ApplyPlacements);

        _placementEditor = WindowHost.ShowOrFocus(_placementEditor,
            () => new PlacementEditorWindow(ctx), () => _placementEditor = null);
    }

    // Persists the chosen placements (null = "use the default") and applies them: the floating one lands
    // live now; the dense one takes effect on the next dense entry / launch. See OverlayCanvas.
    private void ApplyPlacements(OverlayPlacement? floating, OverlayPlacement? dense, OverlayPlacement? docked,
        double? floatingWidth, double? dockedWidth)
    {
        if (_appSettings is not { } s) return;
        s.FloatingPlacement = floating;
        s.DensePlacement = dense;
        s.DockedPlacement = docked;
        s.FloatingWidthDip = floatingWidth;
        s.DockedWidthDip = dockedWidth;
        s.Save();
        _overlay?.Canvas.ApplyPlacementsLive(floating, dense, docked);
        // SetFloatingWidth/SetDockedWidth reseed the configured width and reset the runtime width to it.
        _overlay?.Canvas.SetFloatingWidth(floatingWidth);
        _overlay?.Canvas.SetDockedWidth(dockedWidth);
    }

    // "View tree…" (overlay row) — opens/focuses the one git Tree window and points it at the clicked
    // session's working directory. The menu item only appears when the feature is on and the cwd is a git
    // repo (see OverlayCanvas), so this just re-points the reused window.
    private void OnViewTree(ClaudeSession session)
    {
        var pr = session.PullRequest;
        // "Active" = the session is live and may be writing to this tree (working or waiting to resume), so
        // the Tree window guards a commit behind a confirm. Idle / finished / errored sessions don't.
        bool isActive = session.Status is SessionStatus.Running or SessionStatus.AwaitingInput;
        _treeWindow = WindowHost.ShowOrFocus(_treeWindow,
            () => new GitTreeWindow(_appSettings ?? AppSettings.Load()),
            () => _treeWindow = null,
            w => w.Retarget(session.Cwd, session.DisplayName, pr, isActive));
    }

    private void OnOpenMarkdown(ClaudeSession session)
    {
        // "Active" = the session is live and may still be writing to these files, so the editor warns
        // before overwriting one that changed on disk. Idle / finished sessions don't.
        bool isActive = session.Status is SessionStatus.Running or SessionStatus.AwaitingInput;
        _markdownWindow = WindowHost.ShowOrFocus(_markdownWindow,
            () => new MarkdownWindow(_appSettings ?? AppSettings.Load()),
            () => _markdownWindow = null,
            w => w.Retarget(session.Cwd, session.SessionId, session.DisplayName, isActive));
    }

    // Opens (or focuses) the to-do list window. Edits flow through the shared TodoStore; an edit there
    // saves and re-runs the poller so the overlay strip and reminders track it — see the onChanged callback.
    private void OpenTodos()
    {
        _todoStore ??= TodoStore.Load();
        _todoWindow = WindowHost.ShowOrFocus(_todoWindow,
            () => new TodoWindow(_todoStore, () => _todoHost?.RefreshNow()),
            () => _todoWindow = null,
            w => w.Retarget());
    }

    private void OpenAchievements() =>
        _achievementsWindow = WindowHost.ShowOrFocus(_achievementsWindow,
            () =>
            {
                var w = new AchievementsWindow(_appSettings ?? AppSettings.Load());
#if DEBUG
                w.PreviewReveal = u => ShowAchievementCards([u]);   // click a badge to test the reveal
#endif
                return w;
            }, () => _achievementsWindow = null);

    private void OpenFlightPath() =>
        _flightWindow = WindowHost.ShowOrFocus(_flightWindow, () => new FlightPathWindow(), () => _flightWindow = null);

    // The reward for long-pressing the brand mark: the arcade chooser. It hands off to one of the three toys
    // below and closes as it does. All are reused like every other aux window.
    private void OpenArcade() =>
        _arcadeWindow = WindowHost.ShowOrFocus(_arcadeWindow,
            () => new ArcadeMenuWindow(OpenInvaders, OpenFrogger, OpenWordle), () => _arcadeWindow = null);

    private void OpenInvaders() =>
        _invadersWindow = WindowHost.ShowOrFocus(_invadersWindow, () => new SpaceInvadersWindow(), () => _invadersWindow = null);

    private void OpenFrogger() =>
        _froggerWindow = WindowHost.ShowOrFocus(_froggerWindow, () => new FroggerWindow(), () => _froggerWindow = null);

    // The daily Wordle keeps today's progress in AppSettings so it survives a restart; the window reads and
    // writes AppSettings.WordleState directly and saves after each guess.
    private void OpenWordle() =>
        _wordleWindow = WindowHost.ShowOrFocus(_wordleWindow, () => new WordleWindow(_appSettings!), () => _wordleWindow = null);

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

    // The "what's new" entries captured at startup (newer than the last-run version), or null if this
    // launch wasn't a post-update one. Shown once, then discarded.
    private System.Collections.Generic.IReadOnlyList<ChangelogSection>? _pendingChangelog;
    private ChangelogWindow? _changelogWindow;

    // Picks the changelog sections to surface on this launch: nothing unless the feature is on, we have a
    // prior version on record, and it differs from the current one — then only the sections in between.
    private static System.Collections.Generic.IReadOnlyList<ChangelogSection>? ResolvePendingChangelog(AppSettings settings)
    {
        if (!settings.ShowChangelogOnUpdate) return null;
        if (string.IsNullOrWhiteSpace(settings.LastSeenVersion)) return null; // fresh install — nothing to show
        if (settings.LastSeenVersion == AppInfo.Version) return null;         // same version — no update
        var markdown = ChangelogMarkdown.LoadEmbedded();
        if (markdown is null) return null;
        var sections = ChangelogParser.UnseenSince(markdown, settings.LastSeenVersion, AppInfo.Version);
        return sections.Count > 0 ? sections : null;
    }

    private void ShowChangelog(System.Collections.Generic.IReadOnlyList<ChangelogSection> sections)
    {
        string subhead = sections.Count == 1
            ? $"Updated to {sections[0].Display}."
            : $"Updated to {sections[0].Display} — {sections.Count} releases since {sections[^1].Display}.";
        DisplayChangelogWindow(subhead, sections);
    }

    private void DisplayChangelogWindow(string subhead, System.Collections.Generic.IReadOnlyList<ChangelogSection> sections)
    {
        _changelogWindow?.Close();
        _changelogWindow = new ChangelogWindow("What's new in Perch", subhead, sections, onSuppress: () =>
        {
            if (_appSettings is { } s) { s.ShowChangelogOnUpdate = false; s.Save(); }
        });
        _changelogWindow.Closed += (_, _) => _changelogWindow = null;
        _changelogWindow.Show();
        _changelogWindow.Activate();
    }

#if DEBUG
    // Debug preview: pop the "what's new" window for any (from, to] version range, exactly as a real
    // update from `from` to `to` would render it — including the empty case when nothing lies between.
    private void PreviewChangelogWindow(string fromVersion, string toVersion)
    {
        var markdown = ChangelogMarkdown.LoadEmbedded();
        if (markdown is null) return;
        var sections = ChangelogParser.UnseenSince(markdown, fromVersion, toVersion);
        string subhead = sections.Count == 0
            ? $"Nothing between {fromVersion} and {toVersion}."
            : $"Preview: {fromVersion} → {toVersion} ({sections.Count} release{(sections.Count == 1 ? "" : "s")}).";
        DisplayChangelogWindow(subhead, sections);
    }
#endif

    // "Enable/Disable external notifications" — flips the session's marker file (the same source of truth
    // the plugin's /afk toggles) and rescans so the mail glyph + menu wording refresh. Whether external
    // pushes actually fire on this marker is the Phase-3 notification pipeline's job; the opt-in itself
    // is just this file, so the toggle is wired now.
    private void OnToggleExternalNotify(string sessionId) => _monitorHost?.ToggleExternalNotify(sessionId);

    // "Add note…/Edit note…" — opens the multi-line scratch pad prefilled from the session's current note,
    // then writes the result to its .note sidecar (empty clears it). The note shows inline on the session's
    // overlay row. Modal on the overlay so it can take focus, which the no-activate overlay window can't.
    // Best-effort: a closed overlay mid-flow just no-ops.
    private void OnEditNote(ClaudeSession session)
    {
        if (_monitorHost is not { } host) return;
        OpenStickyNote(session.SessionId, () =>
            StickyNoteWindow.ForSessionRow(
                session.DisplayName, session.ProjectName,
                host.ReadProjectNote(session.Cwd), session.Note,
                (projectText, sessionText) =>
                {
                    host.SetProjectNote(session.Cwd, projectText);
                    host.SetNote(session.SessionId, sessionText);
                }));
    }

    // The global scratch pad — opened from the note button leading the overlay's quick-links row. Multi-line
    // free text persisted in AppSettings (not tied to any session). A non-modal sticky note like the
    // per-session editor.
    private void OnOpenScratchPad()
    {
        var settings = _appSettings ??= AppSettings.Load();
        OpenStickyNote(ScratchNoteKey, () =>
            StickyNoteWindow.Global(settings.ScratchText, text =>
            {
                settings.ScratchText = string.IsNullOrWhiteSpace(text) ? null : text;
                settings.Save();
            }));
    }

    // Right-clicking the note button: a searchable list of every known project, so a note can be pinned to
    // one that has no live session. Enumerating reads a transcript head per project (the project-folder name
    // is a lossy encoding of the cwd, and a project note is keyed by the real cwd), so it runs off the UI
    // thread; the picker then shows on it. A single reused instance — a repeat request re-focuses it.
    private void OnOpenProjectNotePicker()
    {
        if (_overlay is not { } overlay) return;
        if (_projectPicker is { } existing)
        {
            if (existing.WindowState == WindowState.Minimized) existing.WindowState = WindowState.Normal;
            existing.Activate();
            return;
        }

        System.Threading.Tasks.Task.Run(ProjectNoteCatalog.Enumerate)
            .ContinueWith(t =>
            {
                if (_overlay is not { } live) return; // overlay torn down mid-flight
                var picker = new ProjectNotePickerWindow(t.Result);
                picker.ProjectChosen += OnEditProjectNote;
                picker.Closed += (_, _) => { if (ReferenceEquals(_projectPicker, picker)) _projectPicker = null; };
                _projectPicker = picker;
                picker.Show(live);   // owned + non-modal, like the sticky notes
                picker.Activate();   // take focus so the search box is ready to type
            }, System.Threading.CancellationToken.None, System.Threading.Tasks.TaskContinuationOptions.OnlyOnRanToCompletion,
               System.Threading.Tasks.TaskScheduler.FromCurrentSynchronizationContext());
    }

    // A project chosen from the picker: open the sticky-note editor on its project note (prefilled from the
    // current note, re-read for freshness) and persist to the project's project.note sidecar. Keyed by cwd
    // so picking the same project again focuses the open note rather than stacking a duplicate.
    private void OnEditProjectNote(ProjectEntry entry)
    {
        if (_monitorHost is not { } host) return;
        OpenStickyNote("projnote:" + entry.Cwd, () =>
            StickyNoteWindow.ForProject(
                entry.ProjectName, host.ReadProjectNote(entry.Cwd),
                text => host.SetProjectNote(entry.Cwd, text)));
    }

    // Opens (or re-focuses) a sticky note, owned by the overlay so it stays above it and never falls
    // behind. Keyed so a repeat request focuses the live note rather than stacking a duplicate; new notes
    // cascade off the count already open. Wired into CloseAuxWindows via the tracking dictionary.
    private void OpenStickyNote(string key, Func<StickyNoteWindow> factory)
    {
        if (_overlay is not { } overlay) return;
        if (_noteWindows.TryGetValue(key, out var existing))
        {
            if (existing.WindowState == WindowState.Minimized) existing.WindowState = WindowState.Normal;
            existing.Activate();
            return;
        }

        var note = factory();
        note.CascadeIndex = _noteWindows.Count;
        note.Closed += (_, _) => _noteWindows.Remove(key);
        _noteWindows[key] = note;
        note.Show(overlay); // owned + non-modal: above the overlay, but the overlay stays clickable
    }

    /// <summary>
    /// Jumps to a Hypertree branch — to its resume desktop, or to the specific desktop picked from the
    /// row's trailing chip (<paramref name="desktopIndex"/>, 0-based; -1 for the resume point). The work is
    /// a process launch that waits on Hypertree's tray, so it runs off the UI thread; the strip's own poll
    /// then picks the new position up.
    /// </summary>
    /// <remarks>
    /// A failed jump is silent, like a failed quick-link launch — and here it is also self-correcting:
    /// the two realistic failures are Hypertree having exited (the strip clears on the next poll) and the
    /// branch having been removed (it disappears on the next poll). Reporting either would be telling the
    /// user something the overlay is about to show them anyway.
    /// </remarks>
    private void OnHypertreeRowActivated(HypertreeRow row, int desktopIndex)
    {
        var target = row.Target;
        var cli = _hypertreeHost?.Last?.Cli;
        System.Threading.Tasks.Task.Run(() => HypertreeBridge.GoTo(target, cli, desktopIndex));
    }

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
        // The (Dev) suffix marks an isolated development instance running alongside an installed Perch.
        var versionItem = new NativeMenuItem($"Perch{AppProfile.DisplaySuffix} - {AppInfo.Version}") { IsEnabled = false };

        var settingsItem = new NativeMenuItem("Settings…");
        settingsItem.Click += (_, _) => OpenSettings();

        var historyItem = new NativeMenuItem("Session history…");
        historyItem.Click += (_, _) => OpenHistory(null);

        var statsItem = new NativeMenuItem("Session stats…");
        statsItem.Click += (_, _) => OpenStats();

        var flightItem = new NativeMenuItem("Flight path…");
        flightItem.Click += (_, _) => OpenFlightPath();

        var achievementsItem = new NativeMenuItem("Achievements…");
        achievementsItem.Click += (_, _) => OpenAchievements();

        var todosItem = new NativeMenuItem("Todos…");
        todosItem.Click += (_, _) => OpenTodos();

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
            ToolTipText = $"Perch{AppProfile.DisplaySuffix}",
            Menu = new NativeMenu
            {
                versionItem,
                new NativeMenuItemSeparator(),
                settingsItem,
                historyItem,
                statsItem,
                flightItem,
                achievementsItem,
                todosItem,
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

    // Ctrl+Shift+W — collapse/expand the docked column. No-op unless the overlay is in Docked mode.
    private void ToggleDocked() => _overlay?.Canvas.ToggleDockedCollapsed();

    // Switch the live overlay between Floating and Docked from the settings segmented control.
    private void SetOverlayMode()
    {
        if (_appSettings is { } s) _overlay?.Canvas.SetOverlayMode(s.OverlayMode);
    }

    // Flip Floating ↔ Docked from the overlay header's right-click menu: persist the new mode and apply it
    // live (the settings segmented control reads it back next time it opens).
    private void ToggleOverlayMode()
    {
        if (_appSettings is not { } s) return;
        s.OverlayMode = s.OverlayMode == OverlayPresentationMode.Docked
            ? OverlayPresentationMode.Floating
            : OverlayPresentationMode.Docked;
        s.Save();
        _overlay?.Canvas.SetOverlayMode(s.OverlayMode);
    }

    // ── Global hotkeys ────────────────────────────────────────────────────────────
    // Disposes any current bindings and re-registers the three configured shortcuts from settings. Called
    // once at startup and again whenever the Hotkeys settings page edits a binding, so a change takes
    // effect live. A disabled/invalid binding is skipped; a combo the OS refuses (another app owns it) is
    // dropped without fuss.
    private void RegisterHotkeys()
    {
        foreach (var hk in _hotkeys) hk.Dispose();
        _hotkeys.Clear();
        if (_appSettings is not { } s) return;

        TryRegister(s.HotkeyToggleDense,   () => Dispatcher.UIThread.Post(ToggleDense));
        TryRegister(s.HotkeyCycleSessions, () => Dispatcher.UIThread.Post(CycleSessions));
        TryRegister(s.HotkeyOpenSwitcher,  () => Dispatcher.UIThread.Post(OpenSwitcher));
        TryRegister(s.HotkeyToggleDocked,  () => Dispatcher.UIThread.Post(ToggleDocked));
    }

    private void TryRegister(HotkeyBinding binding, Action onPressed)
    {
        if (binding is not { Enabled: true } || !binding.IsValid) return;
        var hk = PlatformServices.CreateGlobalHotkey();
        if (hk.Register(binding.Modifiers, binding.KeyChar, onPressed)) _hotkeys.Add(hk);
        else hk.Dispose(); // OS refused the combo — leave the slot empty rather than hold a dead binding
    }

    // "Jump to next session": focus the host of the session after the last one we jumped to, wrapping
    // around — so repeatedly pressing the hotkey walks every interactive session in turn (terminal
    // sessions raise their terminal, Claude Desktop sessions raise the desktop app). Only background/SDK
    // sessions are skipped — no human is at the keyboard. Focusing also acknowledges the session, clearing
    // a "done" badge just like a click.
    private void CycleSessions()
    {
        var targets = _lastSessions.Where(s => !s.IsBackground).ToList();
        if (targets.Count == 0) return;

        int last = _lastCycledSessionId is null ? -1 : targets.FindIndex(s => s.SessionId == _lastCycledSessionId);
        var next = targets[(last + 1) % targets.Count];
        _lastCycledSessionId = next.SessionId;
        FocusSession(next);
        _overlay?.Canvas.HighlightCycledSession(next.SessionId); // mark the row so the user sees where they landed
    }

    // How many recently-closed sessions the switcher lists beneath the active ones. A cap keeps the palette
    // from turning into a full transcript history — that's what the History window is for.
    private const int SwitcherClosedLimit = 20;

    // "Session switcher": pop the centred keyboard palette over the current interactive sessions plus the
    // recently-closed ones (Enter reopens those in a fresh terminal). Pressing the hotkey again while it's
    // open dismisses it (Esc / clicking away do too). Focus is forced because a global hotkey firing in a
    // background tray doesn't grant foreground rights on its own.
    private void OpenSwitcher()
    {
        if (_switcher is { IsVisible: true } open) { open.Close(); return; }

        var active = _lastSessions.Where(s => !s.IsBackground).ToList();

        var switcher = new SessionSwitcherWindow(active, FocusSession, ReopenSession, CopyResumeCommand);
        _switcher = switcher;
        switcher.Closed += (_, _) => { if (ReferenceEquals(_switcher, switcher)) _switcher = null; };
        switcher.Show();
        switcher.TakeFocus();

        // The closed roster lives on disk (SessionMonitor only ever surfaces live-PID sessions), so read it
        // off the UI thread and stream it in — the hotkey stays instant even when many transcripts exist.
        var activeIds = new HashSet<string>(active.Select(s => s.SessionId));
        System.Threading.Tasks.Task.Run(() => SessionHistory.ListAll(activeIds)).ContinueWith(t =>
        {
            if (!t.IsCompletedSuccessfully) return;
            var closed = t.Result
                .Where(e => !e.IsActive && !string.IsNullOrEmpty(e.SessionId) && !string.IsNullOrEmpty(e.Cwd))
                .Take(SwitcherClosedLimit)
                .ToList();
            if (closed.Count == 0) return;
            Dispatcher.UIThread.Post(() =>
            {
                if (ReferenceEquals(_switcher, switcher) && switcher.IsVisible)
                    switcher.SetClosedSessions(closed);
            });
        });
    }

    // Reopen a closed session: spawn a fresh terminal running `claude --resume <id>` in its working
    // directory. If no terminal can be launched (or the platform doesn't implement it yet), fall back to
    // copying the command so the user can paste it wherever they like.
    private void ReopenSession(string cwd, string sessionId)
    {
        var terminal = _appSettings?.ReopenTerminal ?? TerminalApp.Auto;
        if (!PlatformServices.SessionLauncher.Reopen(cwd, sessionId, terminal))
            CopyResumeCommand(sessionId);
    }

    // The centred window listing every daemon worker, opened from the overlay strip's "show +N more"
    // overflow line. Single reused instance via the WindowHost idiom; refreshed live by the daemon
    // host's callback while open, and closed with the other aux windows.
    private void OpenDaemonList()
    {
        _daemonListWindow = WindowHost.ShowOrFocus(
            _daemonListWindow,
            () => new DaemonListWindow(
                OpenHistory,
                sessionId => _lastSessions.FirstOrDefault(s => s.SessionId == sessionId)?.Status),
            () => _daemonListWindow = null,
            w => w.SetWorkers(_lastDaemonWorkers));
    }

    // Copy `claude --resume <id>` to the clipboard (via the overlay's TopLevel, which is always alive —
    // the switcher may be mid-close). Best-effort; a clipboard failure is swallowed.
    private void CopyResumeCommand(string sessionId)
    {
        try
        {
            if (_overlay is { } o && TopLevel.GetTopLevel(o)?.Clipboard is { } clip)
                _ = clip.SetTextAsync(ClaudeCli.ResumeCommand(sessionId));
        }
        catch { /* clipboard unavailable — best-effort */ }
    }

    // Lazily create-or-focus the single Settings window instance. The window edits the shared
    // AppSettings and applies changes live through the hooks below — the Avalonia counterpart of the
    // WinForms OverlayApplicationContext's SettingsForm wiring.
    private void OpenSettings()
    {
        if (_settings is { } w && w.IsVisible)
        {
            // Pull it onto the current virtual desktop before activating, so re-opening lands on the desktop
            // the user is looking at rather than switching them away to wherever it was left. (Settings
            // doesn't go through WindowHost, so it needs the same nudge WindowHost.ShowOrFocus applies.)
            PlatformServices.VirtualDesktops.MoveWindowToCurrentDesktop(w.TryGetPlatformHandle()?.Handle ?? 0);
            w.Activate();
            return;
        }
        var settings = _appSettings ??= AppSettings.Load();
        var hooks = new SettingsHooks
        {
            DisplayChanged = () => ApplyDisplaySettings(settings),
            ThemeChanged = () => ThemeService.Apply(
                Perch.Theming.ThemeCatalog.Resolve(settings.ActiveThemeId, settings.CustomThemes), _desktop),
            UsageEnabledChanged = on => { if (on) _usageHost?.Start(); else _usageHost?.Stop(); },
            ServiceStatusEnabledChanged = on => { if (on) _statusHost?.Start(); else _statusHost?.Stop(); },
            ServiceStatusIntervalChanged = () => _statusHost?.SetInterval(settings.ServiceStatusIntervalMinutes),
            // The media strip's call-app suppression reads the mic holder, so turning either strip on or off
            // re-evaluates whether the shared mic monitor needs to run.
            MediaEnabledChanged = on => { if (on) _mediaHost?.Start(); else _mediaHost?.Stop(); SyncMicMonitor(); },
            MicEnabledChanged = _ => SyncMicMonitor(),
            HypertreeEnabledChanged = on => { if (on) _hypertreeHost?.Start(); else _hypertreeHost?.Stop(); },
            DaemonProcessesEnabledChanged = on => { if (on) _daemonHost?.Start(); else _daemonHost?.Stop(); },
#if DEBUG
            TestServiceStatus = () => _overlay?.Canvas.UpdateStatus(SampleOutage()),
            TestAchievementBatch = () => ShowAchievementCards(SampleAchievementBatch()),
            PreviewChangelog = PreviewChangelogWindow,
#endif
            MetricsChanged = () => _metricsHost?.Configure(
                settings.ShowSystemMetrics, settings.ShowSessionMetrics, settings.IncludeSubprocessMetrics),
            QuickLinksChanged = () => ReloadQuickLinks(settings),
            HotkeysChanged = RegisterHotkeys,
            OverlayModeChanged = SetOverlayMode,
            TestNotification = kind => _notifications?.ShowTest(kind),
            TestExternalNotification = () => { if (_notifications is { } n) _ = n.SendExternalTestAsync(); },
            CheckForUpdates = () => _updateService?.CheckManual(),
            PerformUpdate = () => _updateService?.PerformUpdate(CloseAuxWindows),
            OpenStats = OpenStats,
            OpenFlightPath = OpenFlightPath,
            OpenAchievements = OpenAchievements,
            OpenPlacements = OpenPlacementEditor,
            OpenSocialCompose = OpenCompose,
            OpenSocialFriends = OpenFriends,
            OpenSocialDebug = OpenSocialDebug,
        };
        _settings = new SettingsWindow(settings, _usageHost!, hooks, PlatformServices.AppIconProvider, _social);
        _settings.SetUpdateAvailable(_updateService?.HasPendingUpdate ?? false, _updateService?.PendingVersion);
        _settings.Closed += (_, _) => _settings = null;
        _settings.Show();
        _settings.Activate();
    }
}
