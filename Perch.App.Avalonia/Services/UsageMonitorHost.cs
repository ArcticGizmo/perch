using Avalonia.Threading;
using Perch.Data;

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

    private readonly UsageMonitor _monitor = new();
    private readonly Action<UsageInfo> _onUsage;
    private readonly DispatcherTimer _timer;

    public UsageMonitorHost(Action<UsageInfo> onUsage)
    {
        _onUsage = onUsage;
        _timer = new DispatcherTimer { Interval = Interval };
        _timer.Tick += (_, _) => _ = Poll();
    }

    /// <summary>Starts the timer and kicks off the first fetch. Call on the UI thread.</summary>
    public void Start()
    {
        _timer.Start();
        _ = Poll();
    }

    // Ticks on the UI thread; FetchAsync runs its IO off it and the continuation resumes here, so the
    // callback (and the repaint it drives) stays on the UI thread.
    private async Task Poll()
    {
        var info = await _monitor.FetchAsync();
        _onUsage(info);
    }

    public void Dispose() => _timer.Stop();
}
