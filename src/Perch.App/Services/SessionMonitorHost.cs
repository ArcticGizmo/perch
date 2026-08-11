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
    // Safety-net rescan cadence (matches the WinForms ReconcileIntervalMs), catching anything a file
    // event or the deadline timer misses. The deadline timer below is what makes "done" fire on time.
    private static readonly TimeSpan ReconcileInterval = TimeSpan.FromSeconds(30);

    private readonly SessionMonitor _monitor;
    private readonly Action<IReadOnlyList<ClaudeSession>> _onSessions;

    // One-shot timer armed to the monitor's next deferred-completion deadline (a busy->idle settle or a
    // sub-agent grace). Without this the "done" badge only appears on the next incidental file event, so
    // a finished session lingers as Running/Idle far too long — the Avalonia port of the WinForms
    // _deadlineTimer + ArmDeadlineTimer. Ticks on the UI thread, so the Scan it drives is UI-thread-safe.
    private readonly DispatcherTimer _deadlineTimer;
    private readonly DispatcherTimer _reconcileTimer;

    /// <summary>Raised when a session newly needs attention (finished) — the app flashes the overlay.</summary>
    public event Action<ClaudeSession>? NeedsAttention;

    /// <summary>Raised when a session newly blocks awaiting input — the app flashes the overlay.</summary>
    public event Action<ClaudeSession>? AwaitingInput;

    /// <summary>Raised when a session's last request to the API failed (e.g. 529) — the app flashes the
    /// overlay and raises the API-error notification.</summary>
    public event Action<ClaudeSession>? ApiError;

    /// <summary>Raised when a pull request tracked for a session's directory was merged or closed — the app
    /// raises the "PR finished" notification.</summary>
    public event Action<ClaudeSession>? PrFinished;

    /// <summary>Raised when a new review is added to a tracked PR — the app raises the "PR reviewed" alert.</summary>
    public event Action<ClaudeSession>? PrReviewed;

    /// <summary>Raised when a tracked PR is approved — the app raises the "PR approved" alert.</summary>
    public event Action<ClaudeSession>? PrApproved;

    /// <summary>Raised when the plugin asks to open the history viewer on a session (carries its id).</summary>
    public event Action<string>? OpenHistoryRequested;

    /// <param name="processProbe">How pid liveness is tested. Defaults to the real OS probe; a replay
    /// passes one backed by the projector so recorded (dead) pids read as alive within their window.</param>
    public SessionMonitorHost(
        Action<IReadOnlyList<ClaudeSession>> onSessions, Perch.Platform.IProcessProbe? processProbe = null)
    {
        _monitor = new SessionMonitor(processProbe);
        _onSessions = onSessions;
        _monitor.SessionsChanged += OnSessionsChanged;
        // These fire from within Scan, which only runs on the UI thread (see ChangeDetected below and
        // Start), so forwarding them straight through keeps every consumer on the UI thread.
        _monitor.NeedsAttention += s => NeedsAttention?.Invoke(s);
        _monitor.AwaitingInput += s => AwaitingInput?.Invoke(s);
        _monitor.ApiError += s => ApiError?.Invoke(s);
        _monitor.PrFinished += s => PrFinished?.Invoke(s);
        _monitor.PrReviewed += s => PrReviewed?.Invoke(s);
        _monitor.PrApproved += s => PrApproved?.Invoke(s);
        _monitor.OpenHistoryRequested += id => OpenHistoryRequested?.Invoke(id);
        // FileSystemWatcher/debounce fire on background threads; hop to the UI thread and re-scan there
        // (matches the WinForms BeginInvoke(Scan) pattern) so the callback only runs on the UI thread.
        _monitor.ChangeDetected += () => Dispatcher.UIThread.Post(() => _monitor.Scan());

        _deadlineTimer = new DispatcherTimer();
        _deadlineTimer.Tick += (_, _) => { _deadlineTimer.Stop(); _monitor.Scan(); };
        _reconcileTimer = new DispatcherTimer { Interval = ReconcileInterval };
        _reconcileTimer.Tick += (_, _) => _monitor.Scan();
    }

    /// <summary>Whether the monitor flags stuck sessions (feeds the overlay's warning glyph). Off by
    /// default in the monitor; the app sets it from settings.</summary>
    public bool StuckDetectionEnabled { set => _monitor.StuckDetectionEnabled = value; }

    /// <summary>Whether the monitor fetches unstaged git line-churn (feeds the overlay's git chip). Off
    /// by default in the monitor; the app sets it from settings.</summary>
    public bool GitStatsEnabled { set => _monitor.GitStatsEnabled = value; }

    /// <summary>Turns the GitHub pull-request lookup on/off in the data layer (off ⇒ gh is never run).</summary>
    public bool PrEnabled { set => _monitor.PrEnabled = value; }

    /// <summary>How often (minutes) each working directory's PR is re-checked with gh.</summary>
    public int PrIntervalMinutes { set => _monitor.PrIntervalMinutes = value; }

    /// <summary>Turns the per-session Jira ticket glyph on/off in the data layer (off ⇒ no branch is read).</summary>
    public bool JiraEnabled { set => _monitor.JiraEnabled = value; }

    /// <summary>The Jira site branch tickets deep-link into (bare sub-domain or full host).</summary>
    public string? JiraSubdomain { set => _monitor.JiraSubdomain = value; }

    /// <summary>Optional comma-separated Jira project keys a branch key must match; blank matches any.</summary>
    public string? JiraProjectFilter { set => _monitor.JiraProjectFilter = value; }

    /// <summary>Reads the initial session state and starts the safety-net reconcile timer. Call on the
    /// UI thread (Scan raises SessionsChanged).</summary>
    public void Start()
    {
        _monitor.Scan();
        _reconcileTimer.Start();
    }

    /// <summary>Forces an immediate rescan on the UI thread. Replay calls this right after each
    /// projection so scrubbing reflects the new state without waiting out the watcher debounce /
    /// reconcile cadence.</summary>
    public void Reconcile() => _monitor.Scan();

    /// <summary>Flips a session's external-notify opt-in (writes/deletes its marker file) and rescans so
    /// the overlay's mail glyph + menu wording refresh. Call on the UI thread.</summary>
    public void ToggleExternalNotify(string sessionId)
    {
        _monitor.ToggleExternalNotify(sessionId);
        _monitor.Scan();
    }

    /// <summary>Sets or clears a session's pinned note (writes/deletes its <c>.note</c> sidecar) and
    /// rescans so the overlay's note glyph + second line refresh. A null/blank text clears it. Call on
    /// the UI thread.</summary>
    public void SetNote(string sessionId, string? text)
    {
        _monitor.SetNote(sessionId, text);
        _monitor.Scan();
    }

    /// <summary>Sets or clears the project note shared by every session under <paramref name="cwd"/>
    /// (writes/deletes the project's <c>project.note</c> sidecar) and rescans. A null/blank text clears
    /// it. Call on the UI thread.</summary>
    public void SetProjectNote(string cwd, string? text)
    {
        _monitor.SetProjectNote(cwd, text);
        _monitor.Scan();
    }

    /// <summary>Reads the project note shared by sessions under <paramref name="cwd"/>, or null when
    /// unset.</summary>
    public string? ReadProjectNote(string cwd) => SessionMonitor.ReadProjectNote(cwd);

    /// <summary>Forces an immediate rescan. For actions that change the live session set from *outside* the
    /// sessions directory — terminating a session kills the process but leaves its <c>{pid}.json</c> behind,
    /// so no file event fires and the row would linger until the reconcile poll. Call on the UI thread.
    /// </summary>
    public void Rescan() => _monitor.Scan();

    /// <summary>Clears a completed session's "done" badge — the user focused/clicked it — and rescans so
    /// the overlay drops the NeedsAttention state back to Idle. Harmless for a session that isn't done.
    /// Call on the UI thread.</summary>
    public void Acknowledge(string pid)
    {
        _monitor.Acknowledge(pid);
        _monitor.Scan();
    }

    // Runs after every scan (SessionsChanged fires from within Scan): pump the result to the UI, then
    // (re)arm the one-shot deadline timer so the next deferred completion fires on time.
    private void OnSessionsChanged(IReadOnlyList<ClaudeSession> sessions)
    {
        _onSessions(sessions);
        ArmDeadlineTimer();
    }

    // Arms the one-shot timer for the monitor's next needs-attention deadline (or leaves it stopped when
    // none is pending). Mirrors the WinForms ArmDeadlineTimer; fires on the next tick if already due.
    private void ArmDeadlineTimer()
    {
        _deadlineTimer.Stop();
        if (_monitor.NextNeedsAttentionDeadline is not { } deadline) return;
        var ms = (deadline - DateTime.Now).TotalMilliseconds;
        _deadlineTimer.Interval = TimeSpan.FromMilliseconds(Math.Clamp(ms, 1, int.MaxValue));
        _deadlineTimer.Start();
    }

    public void Dispose()
    {
        _deadlineTimer.Stop();
        _reconcileTimer.Stop();
        _monitor.SessionsChanged -= OnSessionsChanged;
        _monitor.Dispose();
    }
}
