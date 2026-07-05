using Avalonia.Threading;
using Perch.Data;

namespace Perch.Avalonia.Services;

/// <summary>
/// Owns the Perch.Core <see cref="SessionMonitor"/> for the Avalonia app and pumps its results to a
/// callback (the overlay canvas's <c>Update</c>). Mirrors the WinForms context's contract: every
/// file-change trigger is marshalled onto the UI thread before <see cref="SessionMonitor.Scan"/> runs,
/// so <see cref="SessionMonitor.SessionsChanged"/> — and the UI update it drives — always fire on the UI
/// thread. This is the pipeline the whole Avalonia UI hangs off.
/// </summary>
internal sealed class SessionMonitorHost : IDisposable
{
    private readonly SessionMonitor _monitor = new();
    private readonly Action<IReadOnlyList<ClaudeSession>> _onSessions;

    public SessionMonitorHost(Action<IReadOnlyList<ClaudeSession>> onSessions)
    {
        _onSessions = onSessions;
        _monitor.SessionsChanged += OnSessionsChanged;
        // FileSystemWatcher/debounce fire on background threads; hop to the UI thread and re-scan there
        // (matches the WinForms BeginInvoke(Scan) pattern) so the callback only runs on the UI thread.
        _monitor.ChangeDetected += () => Dispatcher.UIThread.Post(() => _monitor.Scan());
    }

    /// <summary>Reads the initial session state. Call on the UI thread (Scan raises SessionsChanged).</summary>
    public void Start() => _monitor.Scan();

    private void OnSessionsChanged(IReadOnlyList<ClaudeSession> sessions) => _onSessions(sessions);

    public void Dispose()
    {
        _monitor.SessionsChanged -= OnSessionsChanged;
        _monitor.Dispose();
    }
}
