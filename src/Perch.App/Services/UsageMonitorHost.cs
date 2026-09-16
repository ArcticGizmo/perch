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

    // One monitor per active dir, cached by resolved real root so a dir that comes and goes keeps its
    // last-good reading (for dimming) across membership churn.
    private readonly Dictionary<string, UsageMonitor> _monitors = new(ClaudeConfigDir.PathComparer);
    private IReadOnlyList<ClaudeConfigDir> _activeDirs = [];

    private bool _started;
    private bool _polling;

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

    /// <summary>Sets which config dirs to poll (the orgs in use, primary always included). Adds/prunes the
    /// per-dir monitors and, when the membership actually changes, kicks an immediate poll so a newly-active
    /// org's bars appear promptly. Call on the UI thread.</summary>
    public void SetActiveDirs(IReadOnlyList<ClaudeConfigDir> dirs)
    {
        bool changed = !SameSequence(dirs, _activeDirs);
        _activeDirs = dirs;

        foreach (var d in dirs)
            if (!_monitors.ContainsKey(d.RealRoot))
                _monitors[d.RealRoot] = MonitorFor(d);

        var keep = new HashSet<string>(dirs.Select(d => d.RealRoot), ClaudeConfigDir.PathComparer);
        foreach (var stale in _monitors.Keys.Where(k => !keep.Contains(k)).ToList())
            _monitors.Remove(stale);

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
            var dirs = _activeDirs.Count > 0 ? _activeDirs : new[] { ClaudeConfigSet.Instance.Primary };

            // Resolve each dir's org first (cheap, cached), then collapse dirs that share an account so the same
            // account isn't polled — or shown — twice. Only the survivors hit the network.
            var resolved = dirs.Select(d => (Dir: d, Org: _orgs.GetLive(d)));
            var deduped = UsageDirSelection.DedupeAccounts(resolved);

            var results = new List<OrgUsage>(deduped.Count);
            foreach (var (dir, org) in deduped)
            {
                if (!_monitors.TryGetValue(dir.RealRoot, out var mon))
                    _monitors[dir.RealRoot] = mon = MonitorFor(dir);
                var info = await mon.FetchAsync();     // off-thread IO, never throws
                results.Add(new OrgUsage(dir, org, info));
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
