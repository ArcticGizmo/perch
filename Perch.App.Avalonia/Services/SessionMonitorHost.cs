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

    /// <summary>Raised when a session newly needs attention (finished) — the app flashes the overlay.</summary>
    public event Action<ClaudeSession>? NeedsAttention;

    /// <summary>Raised when a session newly blocks awaiting input — the app flashes the overlay.</summary>
    public event Action<ClaudeSession>? AwaitingInput;

    /// <summary>Raised when the plugin asks to open the history viewer on a session (carries its id).</summary>
    public event Action<string>? OpenHistoryRequested;

    public SessionMonitorHost(Action<IReadOnlyList<ClaudeSession>> onSessions)
    {
        _onSessions = onSessions;
        _monitor.SessionsChanged += OnSessionsChanged;
        // These fire from within Scan, which only runs on the UI thread (see ChangeDetected below and
        // Start), so forwarding them straight through keeps every consumer on the UI thread.
        _monitor.NeedsAttention += s => NeedsAttention?.Invoke(s);
        _monitor.AwaitingInput += s => AwaitingInput?.Invoke(s);
        _monitor.OpenHistoryRequested += id => OpenHistoryRequested?.Invoke(id);
        // FileSystemWatcher/debounce fire on background threads; hop to the UI thread and re-scan there
        // (matches the WinForms BeginInvoke(Scan) pattern) so the callback only runs on the UI thread.
        _monitor.ChangeDetected += () => Dispatcher.UIThread.Post(() => _monitor.Scan());
    }

    /// <summary>Whether the monitor flags stuck sessions (feeds the overlay's warning glyph). Off by
    /// default in the monitor; the app sets it from settings.</summary>
    public bool StuckDetectionEnabled { set => _monitor.StuckDetectionEnabled = value; }

    /// <summary>Whether the monitor fetches unstaged git line-churn (feeds the overlay's git chip). Off
    /// by default in the monitor; the app sets it from settings.</summary>
    public bool GitStatsEnabled { set => _monitor.GitStatsEnabled = value; }

    /// <summary>Reads the initial session state. Call on the UI thread (Scan raises SessionsChanged).</summary>
    public void Start() => _monitor.Scan();

    /// <summary>Flips a session's external-notify opt-in (writes/deletes its marker file) and rescans so
    /// the overlay's mail glyph + menu wording refresh. Call on the UI thread.</summary>
    public void ToggleExternalNotify(string sessionId)
    {
        _monitor.ToggleExternalNotify(sessionId);
        _monitor.Scan();
    }

    /// <summary>Clears a completed session's "done" badge — the user focused/clicked it — and rescans so
    /// the overlay drops the NeedsAttention state back to Idle. Harmless for a session that isn't done.
    /// Call on the UI thread.</summary>
    public void Acknowledge(string pid)
    {
        _monitor.Acknowledge(pid);
        _monitor.Scan();
    }

    private void OnSessionsChanged(IReadOnlyList<ClaudeSession> sessions) => _onSessions(sessions);

    public void Dispose()
    {
        _monitor.SessionsChanged -= OnSessionsChanged;
        _monitor.Dispose();
    }
}
