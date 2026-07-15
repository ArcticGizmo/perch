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
    private readonly StatusMonitor _monitor = new();
    private readonly Action<StatusInfo> _onStatus;
    private readonly DispatcherTimer _timer;

    /// <summary>The most recent reading. <see cref="StatusInfo.Healthy"/> until the first fetch completes.</summary>
    public StatusInfo Last { get; private set; } = StatusInfo.Healthy;

    public StatusMonitorHost(Action<StatusInfo> onStatus, int intervalMinutes)
    {
        _onStatus = onStatus;
        _timer = new DispatcherTimer { Interval = ClampInterval(intervalMinutes) };
        _timer.Tick += (_, _) => _ = Poll();
    }

    /// <summary>Starts the timer and kicks off the first fetch. Call on the UI thread.</summary>
    public void Start()
    {
        _timer.Start();
        _ = Poll();
    }

    public void Stop() => _timer.Stop();

    /// <summary>Changes the poll cadence live (from the Settings stepper). A change while running restarts
    /// the countdown so the new interval takes effect immediately.</summary>
    public void SetInterval(int intervalMinutes)
    {
        var interval = ClampInterval(intervalMinutes);
        if (_timer.Interval == interval) return;
        _timer.Interval = interval;
        if (_timer.IsEnabled) { _timer.Stop(); _timer.Start(); }
    }

    // Poll no more than once a minute and no less than hourly — status pages move on the order of minutes,
    // and conditional (304) polls are cheap, so the bound is really just sanity.
    private static TimeSpan ClampInterval(int minutes) => TimeSpan.FromMinutes(Math.Clamp(minutes, 1, 60));

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
