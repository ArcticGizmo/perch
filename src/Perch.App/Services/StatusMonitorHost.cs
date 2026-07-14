using Avalonia.Threading;
using Perch.Data;

namespace Perch.Avalonia.Services;

/// <summary>
/// Polls the public Claude service status on a fixed cadence and pumps each reading to a callback (the
/// overlay canvas's <c>UpdateStatus</c>). The sibling of <see cref="UsageMonitorHost"/>: a
/// <see cref="DispatcherTimer"/> ticks on the UI thread, the fetch runs off it
/// (<see cref="StatusMonitor.FetchAsync"/> never throws), and the result is applied back on the UI
/// thread so feeding the owner-drawn canvas is UI-thread-safe.
/// </summary>
internal sealed class StatusMonitorHost : IDisposable
{
    // Status pages move on the order of minutes; poll a little more eagerly than usage so an outage
    // surfaces reasonably promptly without hammering the endpoint.
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(2);

    private readonly StatusMonitor _monitor = new();
    private readonly Action<StatusInfo> _onStatus;
    private readonly DispatcherTimer _timer;

    /// <summary>The most recent reading. <see cref="StatusInfo.Healthy"/> until the first fetch completes.</summary>
    public StatusInfo Last { get; private set; } = StatusInfo.Healthy;

    public StatusMonitorHost(Action<StatusInfo> onStatus)
    {
        _onStatus = onStatus;
        _timer = new DispatcherTimer { Interval = Interval };
        _timer.Tick += (_, _) => _ = Poll();
    }

    /// <summary>Starts the timer and kicks off the first fetch. Call on the UI thread.</summary>
    public void Start()
    {
        _timer.Start();
        _ = Poll();
    }

    public void Stop() => _timer.Stop();

    // Ticks on the UI thread; FetchAsync runs its IO off it and the continuation resumes here, so the
    // callback (and the repaint it drives) stays on the UI thread.
    private async Task Poll()
    {
        var info = await _monitor.FetchAsync();
        Last = info;
        _onStatus(info);
    }

    public void Dispose() => _timer.Stop();
}
