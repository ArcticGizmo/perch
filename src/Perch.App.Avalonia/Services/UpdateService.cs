using Avalonia.Threading;
using Perch.Data;
using Perch.Platform;
using Velopack;
using Velopack.Sources;

namespace Perch.Avalonia.Services;

/// <summary>
/// The in-app updater: a startup + hourly background check against the GitHub release source, plus the
/// user-initiated "Check for Updates" / "Update now" flows. Ported from the WinForms
/// <c>OverlayApplicationContext</c> update block; the Velopack install/uninstall lifecycle itself lives
/// in <see cref="Program"/>. A pending update is persisted (<see cref="AppSettings.PendingUpdateVersion"/>)
/// so the UI restores its "update available" state across restarts and doesn't re-notify.
///
/// Every callback marshals to the UI thread. The check itself is a synchronous-over-async metadata fetch,
/// so it runs on the thread pool; failures (including "not installed" in a dev run) collapse to a quiet
/// error the callers decide whether to surface.
/// </summary>
internal sealed class UpdateService
{
    private const int CheckIntervalMs = 3_600_000; // hourly

    private readonly AppSettings _settings;
    private readonly NotificationService _notifications;
    private readonly DispatcherTimer _timer;

    // Latched while a download/apply is in flight so a second trigger (overlay badge, tray item, or the
    // Settings button all reach PerformUpdate) can't kick off a parallel run and race two installs. Also
    // blocks a background check from starting mid-apply.
    private bool _inProgress;

    /// <summary>Raised on the UI thread whenever the pending-update state changes, so the tray item,
    /// overlay badge and open Settings window can light up (or clear) together.</summary>
    public event Action<bool, string?>? AvailabilityChanged;

    public UpdateService(AppSettings settings, NotificationService notifications)
    {
        _settings = settings;
        _notifications = notifications;
        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(CheckIntervalMs) };
        _timer.Tick += (_, _) => CheckAuto();
    }

    /// <summary>True while a previously-detected update is waiting to be applied.</summary>
    public bool HasPendingUpdate => !string.IsNullOrEmpty(_settings.PendingUpdateVersion);
    public string? PendingVersion => _settings.PendingUpdateVersion;

    /// <summary>Kicks off the initial check and starts the hourly timer. Call once at startup, after the
    /// tray/overlay are wired so <see cref="AvailabilityChanged"/> lands on live surfaces.</summary>
    public void Start()
    {
        // Restore a persisted "update available" state up front so the surfaces reflect it before the
        // first check completes.
        if (HasPendingUpdate) AvailabilityChanged?.Invoke(true, PendingVersion);
        _timer.Start();
        CheckAuto();
    }

    // ── Update checking ──────────────────────────────────────────────────────────
    private enum Outcome { UpToDate, Available, Error }
    private readonly record struct CheckResult(Outcome Outcome, string? Version, string? Error);

    private static CheckResult RunCheck()
    {
        try
        {
            var mgr = new UpdateManager(new GithubSource(AppInfo.RepoUrl, null, false));
            var update = mgr.CheckForUpdatesAsync().GetAwaiter().GetResult();
            return update == null
                ? new CheckResult(Outcome.UpToDate, null, null)
                : new CheckResult(Outcome.Available, update.TargetFullRelease.Version.ToString(), null);
        }
        catch (Exception ex)
        {
            return new CheckResult(Outcome.Error, null, ex.Message);
        }
    }

    // Startup + hourly background check. Silent: errors and "up to date" produce no toast, and an
    // available update notifies only the first time it is surfaced.
    private void CheckAuto()
    {
        if (_inProgress) return;
        Task.Run(RunCheck).ContinueWith(t =>
        {
            var r = t.Result;
            if (r.Outcome == Outcome.Available && r.Version is { } v)
                MarkAvailable(v, notify: !HasPendingUpdate);
        }, TaskScheduler.FromCurrentSynchronizationContext());
    }

    // The tray "Check for Updates…" item / Settings "Check for updates" button: user-initiated, so it
    // always gives explicit feedback (checking → up-to-date / available / failed).
    public void CheckManual()
    {
        if (_inProgress) return;
        _notifications.ShowInfo("Perch", "Checking for updates…", ToastLevel.Info);
        Task.Run(RunCheck).ContinueWith(t =>
        {
            var r = t.Result;
            switch (r.Outcome)
            {
                case Outcome.Available when r.Version is { } v:
                    MarkAvailable(v, notify: true);
                    break;
                case Outcome.UpToDate:
                    _notifications.ShowInfo("Perch", "You're on the latest version.", ToastLevel.Info);
                    break;
                default:
                    _notifications.ShowInfo("Perch — Update check failed",
                        r.Error ?? "Could not check for updates.", ToastLevel.Error);
                    break;
            }
        }, TaskScheduler.FromCurrentSynchronizationContext());
    }

    // Records a pending update, persists it (so it survives a restart and suppresses re-notifying), and
    // lights up every surface. The toast fires only on the first surfacing.
    private void MarkAvailable(string version, bool notify)
    {
        _settings.PendingUpdateVersion = version;
        _settings.Save();
        AvailabilityChanged?.Invoke(true, version);
        if (notify)
            _notifications.ShowInfo("Perch — Update available",
                $"Version {version} is ready to install. Use the update button to update.", ToastLevel.Info);
    }

    // Clears a pending update and returns every surface to default. Used after applying and to self-heal
    // when a check finds the pending update is no longer actually available.
    private void ClearPending()
    {
        _settings.PendingUpdateVersion = null;
        _settings.Save();
        AvailabilityChanged?.Invoke(false, null);
    }

    /// <summary>Downloads and applies the pending update, then restarts. <paramref name="closeWindows"/>
    /// is invoked up front to tear down open windows — the closing windows are the visible signal the
    /// update is under way. On drift (already latest / release pulled) it self-heals the UI instead.</summary>
    public async void PerformUpdate(Action closeWindows)
    {
        if (_inProgress) return;
        _inProgress = true;
        try
        {
            _notifications.ShowInfo("Perch — Updating", "Preparing update…", ToastLevel.Info);

            var mgr = new UpdateManager(new GithubSource(AppInfo.RepoUrl, null, false));
            var update = await mgr.CheckForUpdatesAsync();
            if (update == null)
            {
                ClearPending();
                _notifications.ShowInfo("Perch", "You're already on the latest version.", ToastLevel.Info);
                return;
            }

            _notifications.ShowInfo("Perch — Updating",
                $"Downloading v{update.TargetFullRelease.Version}…", ToastLevel.Info);

            // Clear the persisted record up front: the process is about to be replaced, and we want a
            // clean slate so the next startup re-detects any version drift from scratch.
            _settings.PendingUpdateVersion = null;
            _settings.Save();

            closeWindows();

            await mgr.DownloadUpdatesAsync(update);
            mgr.ApplyUpdatesAndRestart(update); // exits the process
        }
        catch (Exception ex)
        {
            _notifications.ShowInfo("Perch — Update Failed", ex.Message, ToastLevel.Error);
        }
        finally
        {
            // ApplyUpdatesAndRestart exits the process, so this only runs on the drift/failure paths —
            // both of which should allow another attempt later.
            _inProgress = false;
        }
    }
}
