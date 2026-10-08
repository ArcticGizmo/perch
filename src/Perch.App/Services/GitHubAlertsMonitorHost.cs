using Avalonia.Threading;
using Perch.Avalonia.Views;
using Perch.Data;
using Perch.Platform;

namespace Perch.Avalonia.Services;

/// <summary>
/// Polls GitHub for the open pull requests that involve you and keeps the overlay's GitHub strip and the alerts
/// window current. A <see cref="DispatcherTimer"/> ticks on the UI thread every few minutes; each tick runs
/// <see cref="GitHubAlertsClient.Fetch"/> (blocking gh calls) on the thread pool, then marshals the result back,
/// classifies it against the seen markers and pushes the strip + raises <see cref="Changed"/>.
///
/// <para>One fetch at a time; a tick that lands while one is in flight is skipped. Ticks are skipped while the
/// desktop is locked (nobody is looking, and it spares the rate limit); the first tick after unlocking catches
/// up. A failed poll keeps the last good list on screen, so a network blip doesn't blank the strip. Marking a PR
/// seen (opening it from the window), dismissing or restoring one, or changing the view options reclassifies the
/// cached fetch at once, with no extra gh call.</para>
///
/// <para>The host owns the dashboard's <see cref="Options"/> (not just the window) because the "Updated within"
/// window also trims the overlay strip's counts, so the strip and the window always agree.</para>
/// </summary>
internal sealed class GitHubAlertsMonitorHost : IDisposable
{
    private readonly GitHubAlertsSeenStore _seen;
    private readonly GitHubAlertsDismissStore _dismissed;
    private readonly Action<OverlayCanvas.GitHubStrip> _onStrip;
    private readonly ISessionLock? _lock;
    private readonly DispatcherTimer _timer;

    private GitHubFetchResult? _last;       // the latest fetch (on error, the last good list carrying the error)
    private bool _running, _inFlight;
    private int _generation;                // bumped by Stop, so a fetch from before it is dropped
    private GhListOptions _options = new();

    /// <summary>The latest classified snapshot, or null before the first answer / while stopped.</summary>
    public GitHubAlertsSnapshot? Current { get; private set; }

    /// <summary>Whether a fetch is running now (the window shows "checking…").</summary>
    public bool Busy => _inFlight;

    /// <summary>Raised on the UI thread whenever <see cref="Current"/> or <see cref="Busy"/> changes.</summary>
    public event Action? Changed;

    /// <summary>Raised when the window changes <see cref="Options"/>, so the App can persist them.</summary>
    public event Action<GhListOptions>? OptionsChanged;

    public GitHubAlertsMonitorHost(GitHubAlertsSeenStore seen, GitHubAlertsDismissStore dismissed,
        Action<OverlayCanvas.GitHubStrip> onStrip, ISessionLock? sessionLock)
    {
        _seen = seen;
        _dismissed = dismissed;
        _onStrip = onStrip;
        _lock = sessionLock;
        _timer = new DispatcherTimer { Interval = TimeSpan.FromMinutes(5) };
        _timer.Tick += (_, _) => Poll(force: false);
    }

    /// <summary>The dashboard's grouping, order and age window. Setting it reclassifies at once (the age window
    /// trims the strip) and, when it actually changed, raises <see cref="OptionsChanged"/>.</summary>
    public GhListOptions Options
    {
        get => _options;
        set
        {
            var next = value.Normalize();
            if (next == _options) return;
            _options = next;
            Publish();
            OptionsChanged?.Invoke(next);
        }
    }

    /// <summary>Adopts persisted options at startup without raising <see cref="OptionsChanged"/>.</summary>
    public void SeedOptions(GhListOptions options)
    {
        _options = options.Normalize();
        if (_running) Publish();
    }

    /// <summary>The poll interval, clamped to 2–60 minutes. Takes effect from the next tick.</summary>
    public int IntervalMinutes
    {
        set => _timer.Interval = TimeSpan.FromMinutes(Math.Clamp(value, 2, 60));
    }

    /// <summary>Starts polling and fetches now. Idempotent. Call on the UI thread.</summary>
    public void Start()
    {
        if (_running) return;
        _running = true;
        GitHubAlertsClient.ResetLogin();
        _timer.Start();
        Publish();
        Poll(force: true);
    }

    /// <summary>Stops polling and clears the strip (the feature was turned off). Idempotent.</summary>
    public void Stop()
    {
        if (!_running) return;
        _running = false;
        _timer.Stop();
        _generation++;
        _last = null;
        Publish();
    }

    /// <summary>Fetches now, even while locked (the window's Refresh). A no-op while one is in flight.</summary>
    public void RefreshNow() => Poll(force: true);

    /// <summary>Records that the PR was just opened, so activity up to now stops counting, and reclassifies.</summary>
    public void MarkSeen(string url)
    {
        _seen.MarkSeen(url, DateTime.UtcNow);
        _seen.Save();
        Publish();
    }

    /// <summary>Hides the PR until its state changes (see <see cref="GitHubAlertsClassifier.Fingerprint"/>). A no-op
    /// for a PR the latest fetch doesn't hold.</summary>
    public void Dismiss(string url)
    {
        if (_last?.Prs.FirstOrDefault(p => string.Equals(p.Url, url, StringComparison.OrdinalIgnoreCase)) is not { } pr) return;
        _dismissed.Dismiss(pr.Url, GitHubAlertsClassifier.Fingerprint(pr, _last.Login ?? ""), DateTime.UtcNow);
        _dismissed.Save();
        Publish();
    }

    /// <summary>Brings a dismissed PR back.</summary>
    public void Restore(string url)
    {
        if (!_dismissed.Restore(url)) return;
        _dismissed.Save();
        Publish();
    }

    /// <summary>Headless-render seam: adopt <paramref name="fetch"/> as the latest poll without a timer or gh.</summary>
    internal void SeedForRender(GitHubFetchResult fetch)
    {
        _running = true;
        _last = fetch;
        Publish();
    }

    private void Poll(bool force)
    {
        if (!_running || _inFlight) return;
        if (!force && _lock?.IsLocked == true) return;

        _inFlight = true;
        int generation = _generation;
        Changed?.Invoke();
        Task.Run(GitHubAlertsClient.Fetch).ContinueWith(t => Dispatcher.UIThread.Post(() =>
        {
            _inFlight = false;
            if (generation != _generation || !_running)
            {
                // Stopped (or stopped and restarted) mid-flight: drop this answer; a restart polls afresh.
                if (_running) Poll(force: true);
                return;
            }

            var result = t.IsCompletedSuccessfully ? t.Result : new GitHubFetchResult(null, [], "GitHub check failed", DateTime.UtcNow);
            if (result.Error is not null && _last is { Error: null } good)
                result = good with { Error = result.Error };
            else if (result.Error is null)
            {
                if (_seen.Prune(result.Prs.Select(p => p.Url))) _seen.Save();
                // Only a full, successful open set may expire dismissals: closed PRs and ones whose state moved on.
                var login = result.Login ?? "";
                var now = result.Prs
                    .GroupBy(p => p.Url, StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(g => g.Key, g => GitHubAlertsClassifier.Fingerprint(g.First(), login), StringComparer.OrdinalIgnoreCase);
                if (_dismissed.Reconcile(now)) _dismissed.Save();
            }
            _last = result;
            Publish();
        }));
    }

    // Reclassifies the cached fetch into Current, pushes the overlay strip, and tells the window.
    private void Publish()
    {
        Current = _running && _last is { } fetch
            ? GitHubAlertsClassifier.Build(fetch, _seen.All, _dismissed.Fingerprints, _options.UpdatedSince(DateTime.UtcNow))
            : null;
        _onStrip(ToStrip(Current));
        Changed?.Invoke();
    }

    /// <summary>Folds a snapshot into what the overlay strip paints. A failure with an earlier good list still
    /// shows the counts (the window carries the error); only a failure with nothing to show reads as one.</summary>
    internal static OverlayCanvas.GitHubStrip ToStrip(GitHubAlertsSnapshot? s)
    {
        if (s is null) return new(OverlayCanvas.GitHubStripStatus.Checking, 0, 0, "");
        if (s.Error is { } err && s.Items.Count == 0)
            return new(OverlayCanvas.GitHubStripStatus.Error, 0, 0, err);
        return new(OverlayCanvas.GitHubStripStatus.Ok, s.NeedsYouCount, s.Items.Count(i => i.Shown), "", s.KindCounts());
    }

    public void Dispose()
    {
        _timer.Stop();
        _running = false;
        _generation++;
    }
}
