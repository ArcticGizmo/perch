using Avalonia.Threading;
using Perch.Data;
using Perch.Platform;

namespace Perch.Avalonia.Services;

/// <summary>
/// Owns the Perch.Core <see cref="MetricsMonitor"/> for the Avalonia app and pumps its samples to the
/// overlay. The Avalonia counterpart of the WinForms context's metrics wiring: the monitor samples on
/// its own timer thread, and each reading is marshalled onto the UI thread before it reaches the
/// owner-drawn canvas. The set of session pids to measure is refreshed from every session scan.
/// </summary>
internal sealed class MetricsMonitorHost : IDisposable
{
    private readonly MetricsMonitor _monitor;
    private readonly Action<SystemMetrics> _onSystem;
    private readonly Action<IReadOnlyDictionary<string, SessionMetrics>> _onSessions;

    public MetricsMonitorHost(
        ISystemMetrics platform,
        Action<SystemMetrics> onSystem,
        Action<IReadOnlyDictionary<string, SessionMetrics>> onSessions)
    {
        _monitor = new MetricsMonitor(platform);
        _onSystem = onSystem;
        _onSessions = onSessions;
        _monitor.Updated += OnUpdated;
    }

    /// <summary>Applies the three sampling switches and starts/stops the timer to match. Call on the UI
    /// thread. (4.17 will drive these from Settings; the app passes sensible port defaults for now.)</summary>
    public void Configure(bool system, bool perSession, bool subprocess)
        => _monitor.Configure(system, perSession, subprocess);

    /// <summary>Tells the sampler which session processes to measure. Called from each scan (UI thread);
    /// the pid array is swapped in atomically for the timer thread.</summary>
    public void SetSessionPids(IEnumerable<string> pids) => _monitor.SetSessionPids(pids);

    // Fires on the monitor's timer (thread-pool) thread; hop to the UI thread before touching the canvas.
    private void OnUpdated(SystemMetrics system, IReadOnlyDictionary<string, SessionMetrics> sessions)
        => Dispatcher.UIThread.Post(() => { _onSystem(system); _onSessions(sessions); });

    public void Dispose()
    {
        _monitor.Updated -= OnUpdated;
        _monitor.Dispose();
    }
}
