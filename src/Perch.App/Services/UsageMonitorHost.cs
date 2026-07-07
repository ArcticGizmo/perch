using Avalonia.Threading;
using Perch.Data;
using Perch.Platform;

namespace Perch.Avalonia.Services;

/// <summary>
/// Polls the account-wide rate-limit usage on a fixed cadence and pumps each reading to a callback (the
/// overlay canvas's <c>UpdateUsage</c>). The Avalonia counterpart of the WinForms context's usage timer:
/// a <see cref="DispatcherTimer"/> ticks on the UI thread every five minutes, the fetch runs off it
/// (<see cref="UsageMonitor.FetchAsync"/> never throws), and the result is applied back on the UI thread
/// so feeding the owner-drawn canvas is UI-thread-safe. A failed fetch still yields the last-good reading
/// tagged <c>Ok=false</c>, which the strip renders dimmed.
/// </summary>
internal sealed class UsageMonitorHost : IDisposable
{
    // Matches the WinForms UsageIntervalMs (300s) — the endpoint's data only moves on that scale.
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(5);

    private readonly UsageMonitor _monitor;
    private readonly Action<UsageInfo> _onUsage;
    private readonly DispatcherTimer _timer;

    /// <summary>The most recent reading, cached so a surface opened mid-run (e.g. the Settings usage
    /// bars) can seed itself without waiting for the next poll. <see cref="UsageInfo.Empty"/> until the
    /// first fetch completes.</summary>
    public UsageInfo Last { get; private set; } = UsageInfo.Empty;

    /// <summary>Raised on the UI thread after every fetch (poll or manual refresh), so additional
    /// listeners — the Settings usage bars — track the same readings the overlay does.</summary>
    public event Action<UsageInfo>? Updated;

    public UsageMonitorHost(Action<UsageInfo> onUsage, IClaudeCredentials credentials)
    {
        _onUsage = onUsage;
        _monitor = new UsageMonitor(credentials);
        _timer = new DispatcherTimer { Interval = Interval };
        _timer.Tick += (_, _) => _ = Poll();
    }

    /// <summary>Starts the timer and kicks off the first fetch. Call on the UI thread.</summary>
    public void Start()
    {
        _timer.Start();
        _ = Poll();
    }

    /// <summary>Stops polling (usage tracking turned off). The last reading is retained.</summary>
    public void Stop() => _timer.Stop();

    /// <summary>Fetches once immediately and returns the fresh reading (for the Settings "Refresh"
    /// button). Pumps the result to every listener just like a poll.</summary>
    public async Task<UsageInfo> RefreshAsync()
    {
        await Poll();
        return Last;
    }

    // Ticks on the UI thread; FetchAsync runs its IO off it and the continuation resumes here, so the
    // callback (and the repaint it drives) stays on the UI thread.
    private async Task Poll()
    {
        var info = await _monitor.FetchAsync();
        Last = info;
        _onUsage(info);
        Updated?.Invoke(info);
    }

    public void Dispose() => _timer.Stop();
}
