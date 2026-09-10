using Avalonia.Threading;
using Perch.Data;
using Perch.Platform;

namespace Perch.Avalonia.Services;

/// <summary>
/// Polls the rate-limit usage on a fixed cadence and pumps the readings to a callback (the overlay
/// canvas's <c>UpdateUsage</c>). A <see cref="DispatcherTimer"/> ticks on the UI thread every five
/// minutes, each fetch runs off it (<see cref="UsageMonitor.FetchAsync"/> never throws), and the
/// results are applied back on the UI thread so feeding the owner-drawn canvas is UI-thread-safe. A
/// failed fetch still yields that account's last-good reading tagged <c>Ok=false</c>, which the strip
/// renders dimmed.
///
/// <para>There can be several readings: the endpoint is authenticated with a config dir's own token
/// and config dirs are signed in separately. One monitor per distinct account+organization, since
/// dirs sharing both return the same numbers. A single config dir gives one monitor and one
/// reading.</para>
/// </summary>
internal sealed class UsageMonitorHost : IDisposable
{
    // Matches the WinForms UsageIntervalMs (300s) — the endpoint's data only moves on that scale.
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(5);

    private readonly IClaudeCredentials _credentials;
    private readonly Action<IReadOnlyList<AccountUsage>> _onUsage;
    private readonly DispatcherTimer _timer;

    // Re-derived at the start of every poll rather than on a config-set event, so it cannot race one.
    // A surviving group keeps its monitor, and so its last-good reading.
    private List<Group> _groups = [];

    private sealed record Group(string Key, string Label, UsageMonitor Monitor);

    /// <summary>The most recent readings, so a surface opened mid-run can seed itself without waiting
    /// for the next poll.</summary>
    public IReadOnlyList<AccountUsage> Last { get; private set; } = AccountUsage.None;

    /// <summary>Raised on the UI thread after every fetch (poll or manual refresh), so additional
    /// listeners — the Settings usage bars — track the same readings the overlay does.</summary>
    public event Action<IReadOnlyList<AccountUsage>>? Updated;

    public UsageMonitorHost(Action<IReadOnlyList<AccountUsage>> onUsage, IClaudeCredentials credentials)
    {
        _onUsage = onUsage;
        _credentials = credentials;
        _timer = new DispatcherTimer { Interval = Interval };
        _timer.Tick += (_, _) => _ = Poll();
    }

    /// <summary>Starts the timer and kicks off the first fetch. Call on the UI thread.</summary>
    public void Start()
    {
        _timer.Start();
        _ = Poll();
    }

    /// <summary>Stops polling (usage tracking turned off). The last readings are retained.</summary>
    public void Stop() => _timer.Stop();

    /// <summary>Fetches once immediately and returns the fresh readings (for the Settings "Refresh"
    /// button). Pumps the results to every listener just like a poll.</summary>
    public async Task<IReadOnlyList<AccountUsage>> RefreshAsync()
    {
        await Poll();
        return Last;
    }

    // Ticks on the UI thread; each FetchAsync runs its IO off it and resumes here, so the callback
    // (and the repaint it drives) stays on the UI thread.
    private async Task Poll()
    {
        var groups = ResolveGroups();
        if (groups.Count == 0)
        {
            Publish(AccountUsage.None);
            return;
        }

        var readings = new List<AccountUsage>(groups.Count);
        foreach (var group in groups)
        {
            var info = await group.Monitor.FetchAsync();
            // A lone reading needs no label, and renders as it did before there were groups.
            readings.Add(new AccountUsage(groups.Count > 1 ? group.Label : "", info));
        }

        Publish(readings);
    }

    private void Publish(IReadOnlyList<AccountUsage> readings)
    {
        Last = readings;
        _onUsage(readings);
        Updated?.Invoke(readings);
    }

    /// <summary>Config dirs holding credentials, bucketed by <see cref="ClaudeConfigDir.UsageKey"/>.
    /// Dirs without credentials are skipped rather than reported as errors — an environment nobody has
    /// signed into is not a failure.</summary>
    private List<Group> ResolveGroups()
    {
        var buckets = new Dictionary<string, List<ClaudeConfigDir>>(StringComparer.Ordinal);
        var order = new List<string>();
        foreach (var dir in ClaudeConfigSet.All)
        {
            if (!dir.HasCredentials) continue;
            var key = dir.UsageKey;
            if (!buckets.TryGetValue(key, out var list))
            {
                buckets[key] = list = [];
                order.Add(key);
            }
            list.Add(dir);
        }

        var existing = _groups.ToDictionary(g => g.Key, StringComparer.Ordinal);
        var groups = new List<Group>(order.Count);
        foreach (var key in order)
        {
            var dirs = buckets[key];
            var label = AccountUsage.LabelFor(dirs);
            groups.Add(existing.TryGetValue(key, out var kept)
                ? kept with { Label = label }
                : new Group(key, label, new UsageMonitor(_credentials, dirs[0])));
        }

        _groups = groups;
        return groups;
    }

    public void Dispose() => _timer.Stop();
}
