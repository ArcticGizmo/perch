using Avalonia.Threading;
using Perch.Data;
using Perch.Platform;

namespace Perch.Avalonia.Services;

/// <summary>
/// Polls the rate-limit usage for every org currently in use and pumps the set of readings to a callback
/// (the overlay canvas's <c>UpdateUsage</c>). One <see cref="UsageMonitor"/> per active config dir: the
/// primary reads its token through <see cref="IClaudeCredentials"/> (file on Windows/Linux, Keychain on
/// macOS); a non-primary dir reads its own <c>.credentials.json</c>. A <see cref="DispatcherTimer"/> ticks
/// on the UI thread every five minutes, the fetches run off it (<see cref="UsageMonitor.FetchAsync"/> never
/// throws) sequentially so the endpoint isn't hit in a burst, and the result is applied back on the UI
/// thread. Each monitor keeps its own last-good reading, so a failed org still renders dimmed.
/// </summary>
internal sealed class UsageMonitorHost : IDisposable
{
    // Matches the WinForms UsageIntervalMs (300s) — the endpoint's data only moves on that scale.
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(5);

    private readonly Action<IReadOnlyList<OrgUsage>> _onUsage;
    private readonly IClaudeCredentials _primaryCredentials;
    private readonly IOrgProvider _orgs = new OrgProvider();
    private readonly DispatcherTimer _timer;

    // How often the idle (known-but-not-in-use) accounts are refreshed, in poll ticks: every Nth poll they're
    // fetched, and reused from the last reading in between — active accounts refresh every tick, idle ones
    // ~every 15 min, so the throttled endpoint isn't hit for every idle account on every tick.
    private const int IdleEvery = 3;

    // One monitor per dir, cached by resolved real root so a dir that comes and goes keeps its last-good
    // reading (for dimming) across membership churn.
    private readonly Dictionary<string, UsageMonitor> _monitors = new(ClaudeConfigDir.PathComparer);
    // Active dirs (a live session's + the primary) and the full known set (adds idle-but-known accounts). The
    // strip shows both: active default to full bars, idle to compact chips.
    private IReadOnlyList<ClaudeConfigDir> _activeDirs = [];
    private IReadOnlyList<ClaudeConfigDir> _knownDirs = [];
    // Last produced reading per dir (by real root), so an idle account skipped on this tick still renders from
    // its previous value rather than blanking.
    private readonly Dictionary<string, OrgUsage> _lastByRoot = new(ClaudeConfigDir.PathComparer);

    private bool _started;
    private bool _polling;
    private int _pollCount;

    /// <summary>The most recent set of readings, cached so a surface opened mid-run (the Settings usage bars)
    /// can seed itself without waiting for the next poll. Empty until the first poll completes.</summary>
    public IReadOnlyList<OrgUsage> Last { get; private set; } = [];

    /// <summary>The primary (default) account's reading — the first entry, since the primary always leads the
    /// active set. For the single-account surfaces (a session window's <c>/usage</c>, the Settings preview)
    /// that show one account, not the per-org strip.</summary>
    public UsageInfo LastPrimaryUsage => Last.Count > 0 ? Last[0].Usage : UsageInfo.Empty;

    /// <summary>Raised on the UI thread after every poll, so additional listeners (the Settings usage bars)
    /// track the same readings the overlay does.</summary>
    public event Action<IReadOnlyList<OrgUsage>>? Updated;

    public UsageMonitorHost(Action<IReadOnlyList<OrgUsage>> onUsage, IClaudeCredentials primaryCredentials)
    {
        _onUsage = onUsage;
        _primaryCredentials = primaryCredentials;
        _timer = new DispatcherTimer { Interval = Interval };
        _timer.Tick += (_, _) => _ = Poll();
    }

    /// <summary>Sets which config dirs to poll: the <paramref name="active"/> ones (a live session's + the
    /// primary) and the full <paramref name="known"/> set (which adds idle-but-known accounts). Adds/prunes the
    /// per-dir monitors over the union and, when the membership actually changes, kicks an immediate poll so a
    /// newly-active or newly-discovered account appears promptly. Call on the UI thread.</summary>
    public void SetDirs(IReadOnlyList<ClaudeConfigDir> active, IReadOnlyList<ClaudeConfigDir> known)
    {
        bool changed = !SameSequence(active, _activeDirs) || !SameSequence(known, _knownDirs);
        _activeDirs = active;
        _knownDirs = known;

        // Monitors (and the reuse cache) span the union of active + known.
        var union = active.Concat(known).ToList();
        foreach (var d in union)
            if (!_monitors.ContainsKey(d.RealRoot))
                _monitors[d.RealRoot] = MonitorFor(d);

        var keep = new HashSet<string>(union.Select(d => d.RealRoot), ClaudeConfigDir.PathComparer);
        foreach (var stale in _monitors.Keys.Where(k => !keep.Contains(k)).ToList())
            _monitors.Remove(stale);
        foreach (var stale in _lastByRoot.Keys.Where(k => !keep.Contains(k)).ToList())
            _lastByRoot.Remove(stale);

        if (changed && _started)
            _ = Poll();
    }

    /// <summary>Starts the timer and kicks off the first poll. Call on the UI thread.</summary>
    public void Start()
    {
        _started = true;
        _timer.Start();
        _ = Poll();
    }

    /// <summary>Stops polling (usage tracking turned off). The last readings are retained.</summary>
    public void Stop()
    {
        _started = false;
        _timer.Stop();
    }

    /// <summary>Fetches once immediately and returns the fresh readings (for the Settings "Refresh" button).</summary>
    public async Task<IReadOnlyList<OrgUsage>> RefreshAsync()
    {
        await Poll();
        return Last;
    }

    private UsageMonitor MonitorFor(ClaudeConfigDir dir) =>
        dir.Equals(ClaudeConfigSet.Instance.Primary)
            ? UsageMonitor.ForPrimary(_primaryCredentials)
            : UsageMonitor.ForConfigDir(dir);

    // Ticks on the UI thread; each FetchAsync runs its IO off it and resumes here, so the callback (and the
    // repaint it drives) stays on the UI thread. Sequential over dirs = a natural stagger for the throttled
    // endpoint. A reentrancy guard skips a tick that lands while a poll is still in flight.
    private async Task Poll()
    {
        if (_polling)
            return;
        _polling = true;
        try
        {
            _pollCount++;
            var active = _activeDirs.Count > 0 ? _activeDirs : new[] { ClaudeConfigSet.Instance.Primary };
            var known = _knownDirs.Count > 0 ? _knownDirs : active;

            // Resolve every account (active first, then idle-but-known), collapsing dirs that share an account
            // so it's polled — and shown — once, and tagging each active/idle.
            var accounts = UsageDirSelection.Resolve(active, known, d => _orgs.GetLive(d));

            // Idle accounts refresh only every IdleEvery-th tick (incl. the first); in between they're reused
            // from their last reading, so we don't hit the throttled endpoint for every idle account every tick.
            bool pollIdle = (_pollCount % IdleEvery) == 1;

            var results = new List<OrgUsage>(accounts.Count);
            foreach (var (dir, org, isActive) in accounts)
            {
                OrgUsage row;
                if (isActive || pollIdle || !_lastByRoot.TryGetValue(dir.RealRoot, out var prev))
                {
                    if (!_monitors.TryGetValue(dir.RealRoot, out var mon))
                        _monitors[dir.RealRoot] = mon = MonitorFor(dir);
                    var info = await mon.FetchAsync();     // off-thread IO, never throws
                    row = new OrgUsage(dir, org, info, isActive);
                }
                else
                {
                    // Reuse the idle account's last reading (keep its org/activity current).
                    row = prev with { Org = org, Active = isActive };
                }
                _lastByRoot[dir.RealRoot] = row;
                results.Add(row);
            }
            Last = results;
            _onUsage(results);
            Updated?.Invoke(results);
        }
        finally
        {
            _polling = false;
        }
    }

    private static bool SameSequence(IReadOnlyList<ClaudeConfigDir> a, IReadOnlyList<ClaudeConfigDir> b)
    {
        if (a.Count != b.Count)
            return false;
        for (int i = 0; i < a.Count; i++)
            if (!a[i].Equals(b[i]))
                return false;
        return true;
    }

    public void Dispose()
    {
        _started = false;
        _timer.Stop();
    }
}
