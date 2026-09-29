using System.Collections.Concurrent;
using System.Diagnostics;
using Avalonia.Threading;
using Perch.Data;

namespace Perch.Avalonia.Services;

/// <summary>
/// Owns the Perch.Core <see cref="SessionMonitor"/> for the Avalonia app and pumps its results to a
/// callback (the overlay canvas's <c>Update</c>). This is the pipeline the whole Avalonia UI hangs off.
///
/// <para><see cref="SessionMonitor.Scan"/> reads every session file and its transcript, so it runs on a
/// single-flight background worker (review fixes CP20), never the UI thread: one scan at a time, and any number of
/// requests during a scan collapse into one trailing scan. The results and every event the scan raises are posted
/// back to the UI thread in the order they were raised, so every consumer still runs on the UI thread. Anything the
/// UI asks of the monitor itself (acknowledge, notify toggle, project note) is queued and run on the worker just
/// before its next scan, so the monitor's state is only ever touched by one thread.</para>
/// </summary>
internal sealed class SessionMonitorHost : IDisposable
{
    // Safety-net rescan cadence (matches the WinForms ReconcileIntervalMs), catching anything a file
    // event or the deadline timer misses. The deadline timer below is what makes "done" fire on time.
    private static readonly TimeSpan ReconcileInterval = TimeSpan.FromSeconds(30);

    // Short-cadence poll that runs ONLY while a Perch-controlled session is actively working
    // (SessionMonitor.HasWorkingControlledSession). A controlled session doesn't heartbeat its session
    // file, so no file event fires while it runs a turn — and a background sub-agent it launches lives
    // and dies well inside the 30s reconcile, so the overlay never sampled it. Polling here catches the
    // sub-agent's working window. Off whenever no controlled session is working, so it costs nothing at rest.
    private static readonly TimeSpan ControlledPollInterval = TimeSpan.FromSeconds(2);

    // A scan slower than this is written to logs/scan.log, with the session count, to diagnose a slow machine.
    private const int SlowScanMs = 250;

    private readonly SessionMonitor _monitor;
    private readonly Action<IReadOnlyList<ClaudeSession>> _onSessions;
    private readonly CoalescingTrigger _scan;
    private readonly ConcurrentQueue<Action> _pending = new();
    private volatile bool _disposed;

    // One-shot timer armed to the monitor's next deferred-completion deadline (a busy->idle settle or a
    // sub-agent grace). Without this the "done" badge only appears on the next incidental file event, so
    // a finished session lingers as Running/Idle far too long — the Avalonia port of the WinForms
    // _deadlineTimer + ArmDeadlineTimer.
    private readonly DispatcherTimer _deadlineTimer;
    private readonly DispatcherTimer _reconcileTimer;
    private readonly DispatcherTimer _controlledPollTimer;

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
        Action<IReadOnlyList<ClaudeSession>> onSessions,
        Perch.Platform.IProcessProbe? processProbe = null,
        Perch.Platform.IIdeHostDetector? ideDetector = null)
    {
        _monitor = new SessionMonitor(processProbe, ideDetector);
        _onSessions = onSessions;
        _scan = new CoalescingTrigger(() => Task.Run(ScanOnce), TimeSpan.Zero);

        // Scan raises these on the worker; each is posted to the UI thread, in order, so consumers keep
        // running there (and a scan's alerts still land before the overlay update that follows them).
        _monitor.SessionsChanged += OnSessionsChanged;
        _monitor.NeedsAttention += s => Post(() => NeedsAttention?.Invoke(s));
        _monitor.AwaitingInput += s => Post(() => AwaitingInput?.Invoke(s));
        _monitor.ApiError += s => Post(() => ApiError?.Invoke(s));
        _monitor.PrFinished += s => Post(() => PrFinished?.Invoke(s));
        _monitor.PrReviewed += s => Post(() => PrReviewed?.Invoke(s));
        _monitor.PrApproved += s => Post(() => PrApproved?.Invoke(s));
        _monitor.OpenHistoryRequested += id => Post(() => OpenHistoryRequested?.Invoke(id));
        // File events, process exits and background refreshes arrive (debounced) on pool threads.
        _monitor.ChangeDetected += RequestScan;

        _deadlineTimer = new DispatcherTimer();
        _deadlineTimer.Tick += (_, _) => { _deadlineTimer.Stop(); RequestScan(); };
        _reconcileTimer = new DispatcherTimer { Interval = ReconcileInterval };
        _reconcileTimer.Tick += (_, _) => RequestScan();
        _controlledPollTimer = new DispatcherTimer { Interval = ControlledPollInterval };
        _controlledPollTimer.Tick += (_, _) => RequestScan();
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

    /// <summary>Starts the initial scan (on the worker — the overlay fills when it lands) and the safety-net
    /// reconcile timer. Call on the UI thread.</summary>
    public void Start()
    {
        RequestScan();
        _reconcileTimer.Start();
    }

    /// <summary>Requests an immediate rescan. Replay calls this right after each projection so scrubbing
    /// reflects the new state without waiting out the watcher debounce / reconcile cadence.</summary>
    public void Reconcile() => RequestScan();

    /// <summary>Flips a session's external-notify opt-in (writes/deletes its marker file) and rescans so
    /// the overlay's mail glyph + menu wording refresh.</summary>
    public void ToggleExternalNotify(string sessionId) => OnWorker(() => _monitor.ToggleExternalNotify(sessionId));

    /// <summary>Sets or clears the project note shared by every session under <paramref name="cwd"/>
    /// (writes/deletes the project's <c>project.note</c> sidecar) and rescans so the overlay's note glyph
    /// refreshes. A null/blank text clears it.</summary>
    public void SetProjectNote(string cwd, string? text) => OnWorker(() => _monitor.SetProjectNote(cwd, text));

    /// <summary>Reads the project note shared by sessions under <paramref name="cwd"/>, or null when
    /// unset.</summary>
    public string? ReadProjectNote(string cwd) => SessionMonitor.ReadProjectNote(cwd);

    /// <summary>Requests an immediate rescan. For actions that change the live session set from *outside* the
    /// sessions directory — terminating a session kills the process but leaves its <c>{pid}.json</c> behind,
    /// so no file event fires and the row would linger until the reconcile poll.</summary>
    public void Rescan() => RequestScan();

    /// <summary>Clears a completed session's "done" badge — the user focused/clicked it — and rescans so
    /// the overlay drops the NeedsAttention state back to Idle. Harmless for a session that isn't done.</summary>
    public void Acknowledge(string pid) => OnWorker(() => _monitor.Acknowledge(pid));

    private void RequestScan()
    {
        if (!_disposed) _scan.Request();
    }

    // Queues a change to the monitor's state for the worker (so it never races a scan), then scans.
    private void OnWorker(Action action)
    {
        _pending.Enqueue(action);
        RequestScan();
    }

    // The worker body: apply queued UI requests, then scan. Never runs concurrently with itself.
    private void ScanOnce()
    {
        if (_disposed) return;
        while (_pending.TryDequeue(out var action))
        {
            try { action(); } catch { /* best-effort, like the direct calls it replaces */ }
        }

        var sw = Stopwatch.StartNew();
        var sessions = _monitor.Scan();
        if (sw.ElapsedMilliseconds >= SlowScanMs)
            DiagnosticLog.Append("scan.log", $"Slow scan: {sw.ElapsedMilliseconds} ms for {sessions.Count} session(s)");
    }

    private void Post(Action onUi) => Dispatcher.UIThread.Post(() =>
    {
        if (!_disposed) onUi();
    });

    // Raised on the worker at the end of each scan: capture what the UI needs from the monitor now (the
    // worker owns it), then pump the result to the UI and (re)arm the deadline timer and controlled poll there.
    private void OnSessionsChanged(IReadOnlyList<ClaudeSession> sessions)
    {
        var deadline = _monitor.NextNeedsAttentionDeadline;
        var controlledWorking = _monitor.HasWorkingControlledSession;
        Post(() =>
        {
            _onSessions(sessions);
            ArmDeadlineTimer(deadline);
            UpdateControlledPoll(controlledWorking);
        });
    }

    // Runs the short-cadence poll only while a controlled session is working, so its background sub-agents
    // are sampled during their run; stops it the moment none is, keeping the overlay quiet at rest. See
    // ControlledPollInterval / SessionMonitor.HasWorkingControlledSession.
    private void UpdateControlledPoll(bool controlledWorking)
    {
        if (controlledWorking)
        {
            if (!_controlledPollTimer.IsEnabled) _controlledPollTimer.Start();
        }
        else if (_controlledPollTimer.IsEnabled)
        {
            _controlledPollTimer.Stop();
        }
    }

    // Arms the one-shot timer for the monitor's next needs-attention deadline (or leaves it stopped when
    // none is pending). Mirrors the WinForms ArmDeadlineTimer; fires on the next tick if already due.
    private void ArmDeadlineTimer(DateTime? deadline)
    {
        _deadlineTimer.Stop();
        if (deadline is not { } due) return;
        var ms = (due - DateTime.Now).TotalMilliseconds;
        _deadlineTimer.Interval = TimeSpan.FromMilliseconds(Math.Clamp(ms, 1, int.MaxValue));
        _deadlineTimer.Start();
    }

    public void Dispose()
    {
        _disposed = true;
        _deadlineTimer.Stop();
        _reconcileTimer.Stop();
        _controlledPollTimer.Stop();
        _monitor.SessionsChanged -= OnSessionsChanged;
        _monitor.ChangeDetected -= RequestScan;
        _monitor.Dispose();
    }
}
