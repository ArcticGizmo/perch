using System.Diagnostics;
using System.Text.Json.Nodes;
using Perch.Data;

namespace Perch.Data;

internal sealed class SessionMonitor : IDisposable
{
    private const int NeedsAttentionMinutes = 5;
    private const int DebounceMs = 150;

    // Stuck/runaway detection thresholds. Tuned against a corpus of real transcripts so healthy work
    // almost never trips them: across ~114 transcripts a trailing error streak of 4+ flagged only a
    // handful, and a single command repeated 3+ times in the last 10 calls with 2+ failures was rarer
    // still. See StuckMetrics / TranscriptReader.GetStuckMetrics.
    private const int ErrorStreakThreshold = 4;
    private const int LoopRepeatThreshold  = 3;
    private const int LoopErrorThreshold   = 2;

    /// <summary>Master switch for stuck/runaway detection. Off by construction; the owning context
    /// sets it (and the two sub-switches) from settings and re-scans when the user toggles them.</summary>
    public bool StuckDetectionEnabled { get; set; }

    /// <summary>Flag a session when several tool calls in a row fail ("commands coming up empty").</summary>
    public bool DetectErrorStreaks { get; set; } = true;

    /// <summary>Flag a session when it keeps repeating the same action and it keeps failing.</summary>
    public bool DetectFailingLoops { get; set; } = true;

    private readonly string _sessionsDir = ClaudePaths.SessionsDir;

    private readonly Dictionary<string, string> _lastRawStatus = new();
    private readonly Dictionary<string, DateTime> _idleSince = new();
    // When each PID last entered a continuous Running stretch, so we can show elapsed run time.
    private readonly Dictionary<string, DateTime> _runningSince = new();
    private readonly HashSet<string> _awaitingInputPids = new();
    // PIDs that had at least one running sub-agent on the previous scan, so we can detect the
    // moment they all finish and treat it like a busy->idle completion.
    private readonly HashSet<string> _hadRunningSubs = new();

    private readonly SubAgentReader _subAgents = new();
    private readonly TranscriptReader _transcripts = new();

    // PIDs we have an exit subscription for, keyed by the same string PID used everywhere else.
    private readonly Dictionary<string, Process> _trackedProcesses = new();

    private FileSystemWatcher? _watcher;
    private readonly System.Threading.Timer _debounceTimer;
    private bool _disposed;

    public event Action<IReadOnlyList<ClaudeSession>>? SessionsChanged;
    public event Action<ClaudeSession>? NeedsAttention;
    public event Action<ClaudeSession>? AwaitingInput;

    /// <summary>
    /// Raised when a session asks (via the plugin's <c>/history</c> command, which drops a one-shot
    /// <c>{sessionId}.history</c> trigger file) to open the history viewer on that session. Fired
    /// from <see cref="Scan"/>, i.e. already on the UI thread.
    /// </summary>
    public event Action<string>? OpenHistoryRequested;

    /// <summary>
    /// Raised (on a thread-pool thread) whenever something happened that warrants a re-scan:
    /// a session file changed, a tracked process exited, or the watcher dropped events.
    /// The owner is responsible for marshaling <see cref="Scan"/> onto the UI thread.
    /// </summary>
    public event Action? ChangeDetected;

    /// <summary>
    /// The earliest instant at which a session currently in <see cref="SessionStatus.NeedsAttention"/>
    /// will lapse back to <see cref="SessionStatus.Idle"/>. Null when no session is in that window.
    /// Recomputed at the end of every <see cref="Scan"/>.
    /// </summary>
    public DateTime? NextNeedsAttentionDeadline { get; private set; }

    public SessionMonitor()
    {
        _debounceTimer = new System.Threading.Timer(_ => ChangeDetected?.Invoke());
        EnsureWatcher();
    }

    public IReadOnlyList<ClaudeSession> Scan()
    {
        // The sessions directory may be created after we start; (re)attach the watcher lazily.
        EnsureWatcher();

        if (!Directory.Exists(_sessionsDir))
        {
            NextNeedsAttentionDeadline = null;
            SyncProcessSubscriptions(new HashSet<string>());
            SessionsChanged?.Invoke([]);
            return [];
        }

        var sessions = new List<ClaudeSession>();
        var now = DateTime.Now;

        string[] files;
        try
        {
            files = Directory.GetFiles(_sessionsDir, "*.json");
        }
        catch
        {
            return [];
        }

        foreach (var file in files)
        {
            var session = ReadSession(file, now);
            if (session != null)
                sessions.Add(session);
        }

        var activePids = sessions.Select(s => s.Pid).ToHashSet();
        foreach (var key in _lastRawStatus.Keys.Where(k => !activePids.Contains(k)).ToList())
        {
            _lastRawStatus.Remove(key);
            _idleSince.Remove(key);
            _runningSince.Remove(key);
            _awaitingInputPids.Remove(key);
            _hadRunningSubs.Remove(key);
        }

        SyncProcessSubscriptions(activePids);
        NextNeedsAttentionDeadline = ComputeNextDeadline(sessions);

        SessionsChanged?.Invoke(sessions);

        // Consume any one-shot /history triggers after the subscribers have refreshed their view of
        // the live sessions, so a freshly-opened viewer lands on the right (now-current) session.
        ProcessHistoryRequests();
        return sessions;
    }

    /// <summary>
    /// Toggles a session's external-notification opt-in by writing or deleting its
    /// <c>{sessionId}.notify</c> marker — the single source of truth shared with the plugin's /afk
    /// command. Returns the resulting state (true = now on). Call <see cref="Scan"/> afterwards to
    /// refresh the session list and glyphs from the new on-disk state.
    /// </summary>
    public bool ToggleExternalNotify(string sessionId)
    {
        var marker = Path.Combine(_sessionsDir, $"{sessionId}.notify");
        try
        {
            if (File.Exists(marker))
            {
                File.Delete(marker);
                return false;
            }
            Directory.CreateDirectory(_sessionsDir);
            File.WriteAllText(marker, sessionId);
            return true;
        }
        catch
        {
            return File.Exists(marker);
        }
    }

    private DateTime? ComputeNextDeadline(IReadOnlyList<ClaudeSession> sessions)
    {
        DateTime? earliest = null;
        foreach (var session in sessions)
        {
            if (session.Status != SessionStatus.NeedsAttention)
                continue;
            if (!_idleSince.TryGetValue(session.Pid, out var idleAt))
                continue;

            var deadline = idleAt.AddMinutes(NeedsAttentionMinutes);
            if (earliest == null || deadline < earliest)
                earliest = deadline;
        }
        return earliest;
    }

    private ClaudeSession? ReadSession(string filePath, DateTime now)
    {
        try
        {
            string json;
            using (
                var fs = new FileStream(
                    filePath,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.ReadWrite
                )
            )
            using (var reader = new StreamReader(fs))
                json = reader.ReadToEnd();

            var node = JsonNode.Parse(json);
            if (node == null)
                return null;

            var pid =
                node["pid"]?.GetValue<long>().ToString()
                ?? Path.GetFileNameWithoutExtension(filePath);
            var sessionId = node["sessionId"]?.GetValue<string>() ?? "";
            var rawStatus = node["status"]?.GetValue<string>() ?? "idle";
            var waitingFor = node["waitingFor"]?.GetValue<string>();
            var cwd = node["cwd"]?.GetValue<string>() ?? "";
            var updatedAtMs = node["updatedAt"]?.GetValue<long>() ?? 0;

            // Remote Control marker: Claude Code adds a "bridgeSessionId" to the session file only
            // while the session is connected to claude.ai/the mobile app via /remote-control. There is
            // no other on-disk signal (the session URL/QR and client count never touch disk), so this
            // field is the one thing we can observe. Its presence == remote control is active, and the
            // value itself is the deep-link target we encode into the QR code.
            var bridgeSessionId = node["bridgeSessionId"]?.GetValue<string>();

            var updatedAt =
                updatedAtMs > 0
                    ? DateTimeOffset.FromUnixTimeMilliseconds(updatedAtMs).LocalDateTime
                    : now;

            if (!IsProcessRunning(pid))
                return null;

            var prevRaw = _lastRawStatus.TryGetValue(pid, out var p) ? p : null;
            if (rawStatus == "idle" && prevRaw == "busy")
                _idleSince[pid] = now;
            _lastRawStatus[pid] = rawStatus;

            // Fast/interactive built-ins (/clear, /model, /doctor, …) briefly flip the session
            // busy->idle (or busy->waiting) without the model doing any work, which would otherwise
            // raise a spurious "done"/"waiting" alert. Detect them lazily — only at the transition
            // where we'd actually notify — by asking whether the transcript's latest turn is one of
            // these bare commands. The read is cached by mtime, so this costs at most one stat/parse
            // per scan per session, and only when a transition is in play.
            bool? bareCommand = null;
            bool IsBareCommand() =>
                (bareCommand ??= _transcripts.LastTurnWasBareCommand(sessionId, cwd)) == true;

            SessionStatus status;
            // Claude Code reports a dedicated "waiting" status (with a "waitingFor" hint such as
            // "permission prompt") while it is blocked on user input. Some flows may also surface
            // a non-empty waitingFor without flipping the status, so treat either as awaiting input.
            bool awaitingInput =
                rawStatus == "waiting" || !string.IsNullOrWhiteSpace(waitingFor);
            if (awaitingInput && IsBareCommand())
            {
                // An interactive built-in (e.g. the /model picker) is open. The user typed the
                // command and is already at the keyboard, so treat it as idle rather than nagging
                // them with a "waiting for input" alert.
                _idleSince.Remove(pid);
                _awaitingInputPids.Remove(pid);
                status = SessionStatus.Idle;
            }
            else if (awaitingInput)
            {
                _idleSince.Remove(pid);
                status = SessionStatus.AwaitingInput;
            }
            else if (rawStatus == "busy")
            {
                _idleSince.Remove(pid);
                status = SessionStatus.Running;
                _awaitingInputPids.Remove(pid);
            }
            else
            {
                _awaitingInputPids.Remove(pid);
                if (prevRaw == "busy" && IsBareCommand())
                {
                    // A fast built-in (e.g. /clear, /doctor) just finished; no model work happened,
                    // so it shouldn't count as a completion. Drop the idle timestamp set above and
                    // stay plain idle — no NeedsAttention glyph, no "done" alert.
                    _idleSince.Remove(pid);
                    status = SessionStatus.Idle;
                }
                else if (
                    _idleSince.TryGetValue(pid, out var idleAt)
                    && (now - idleAt).TotalMinutes < NeedsAttentionMinutes
                )
                    status = SessionStatus.NeedsAttention;
                else
                    status = SessionStatus.Idle;
            }

            // Sub-agents (Task tool) and teammates (Agent Teams) run inside this session's process and
            // have no session file of their own; surface them from their transcripts and roll their
            // activity up. The full list (including idle teammates) is shown on the overlay, but only an
            // actively-working child means the parent loop is blocked — an idle teammate sitting waiting
            // for the lead must not keep the parent pegged as Running.
            var subAgents = _subAgents.GetRunning(sessionId, cwd);
            bool hasRunningSubs = subAgents.Any(s => !s.IsIdle);
            bool hadRunningSubs = _hadRunningSubs.Contains(pid);
            bool subsJustFinished = hadRunningSubs && !hasRunningSubs;

            if (hasRunningSubs)
            {
                _hadRunningSubs.Add(pid);
                // A live sub-agent means the session is working even when Claude Code reports the
                // parent as idle (the parent loop is simply blocked waiting on the child).
                if (status is SessionStatus.Idle or SessionStatus.NeedsAttention)
                {
                    status = SessionStatus.Running;
                    _idleSince.Remove(pid);
                }
            }
            else
            {
                _hadRunningSubs.Remove(pid);
                // Sub-agents finished and the parent picked nothing else up: surface it like any
                // other busy->idle completion so the "done" alert still fires.
                if (subsJustFinished && status == SessionStatus.Idle)
                {
                    _idleSince[pid] = now;
                    status = SessionStatus.NeedsAttention;
                }
            }

            var projectName = string.IsNullOrEmpty(cwd)
                ? sessionId[..Math.Min(8, sessionId.Length)]
                : Path.GetFileName(
                    cwd.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                );

            var mode = ReadPermissionMode(Path.Combine(_sessionsDir, $"{sessionId}.mode"));

            // External-notification opt-in: the presence of a {sessionId}.notify marker is the signal.
            // Written/removed by both the overlay's right-click toggle and the plugin's /afk command.
            var externalNotify = !string.IsNullOrEmpty(sessionId)
                && File.Exists(Path.Combine(_sessionsDir, $"{sessionId}.notify"));

            // The explicit name set by Claude Code's built-in /rename command (a custom-title record
            // in the transcript). Null when the session was never renamed, in which case the overlay
            // falls back to the project name from cwd. The auto-generated ai-title is ignored.
            var title = _transcripts.GetTitle(sessionId, cwd);

            // Track the start of the current continuous Running stretch so the overlay can show
            // elapsed run time; reset the moment the session stops running.
            DateTime? runningSince;
            if (status == SessionStatus.Running)
            {
                if (!_runningSince.TryGetValue(pid, out var since))
                {
                    since = now;
                    _runningSince[pid] = since;
                }
                runningSince = since;
            }
            else
            {
                _runningSince.Remove(pid);
                runningSince = null;
            }

            // Live activity: only worth reading the transcript tail while the session is working.
            var activity = status == SessionStatus.Running
                ? _transcripts.GetActivity(sessionId, cwd)
                : null;

            // Stuck/runaway: also only meaningful while the session is actively working. Gated by the
            // settings switches so a user drowning in false positives can turn it (or either half) off.
            var stuck = StuckDetectionEnabled && status == SessionStatus.Running
                ? DetectStuck(sessionId, cwd)
                : null;

            var (contextFill, contextWindow) = _transcripts.GetContextFill(sessionId, cwd);

            // Web Artifacts published to claude.ai over the session's lifetime. Read from the transcript
            // and cached by mtime, so an unchanged transcript costs a stat, not a parse.
            var artifacts = _transcripts.GetArtifacts(sessionId, cwd);

            // The native task checklist Claude works through (TaskCreate/TaskUpdate), reconstructed from
            // the transcript and cached by mtime. Surfaced as a progress count + hover list in the overlay.
            var tasks = _transcripts.GetTasks(sessionId, cwd);

            var session = new ClaudeSession(
                pid,
                sessionId,
                status,
                cwd,
                projectName,
                updatedAt,
                mode,
                subAgents,
                activity,
                runningSince,
                bridgeSessionId,
                externalNotify,
                title,
                contextFill,
                contextWindow,
                artifacts,
                stuck,
                tasks
            );

            if (status == SessionStatus.NeedsAttention && (prevRaw == "busy" || subsJustFinished))
                NeedsAttention?.Invoke(session);

            if (status == SessionStatus.AwaitingInput && _awaitingInputPids.Add(pid))
                AwaitingInput?.Invoke(session);

            return session;
        }
        catch
        {
            return null;
        }
    }

    // Consumes one-shot {sessionId}.history trigger files dropped by the plugin's /history command:
    // each is deleted (so it fires exactly once) and turned into an OpenHistoryRequested event.
    // Deleting the file re-fires the watcher, but the next Scan finds nothing to do, so it settles.
    private void ProcessHistoryRequests()
    {
        string[] files;
        try
        {
            files = Directory.GetFiles(_sessionsDir, "*.history");
        }
        catch
        {
            return;
        }

        foreach (var file in files)
        {
            var sessionId = Path.GetFileNameWithoutExtension(file);
            try { File.Delete(file); } catch { }
            if (!string.IsNullOrEmpty(sessionId))
                OpenHistoryRequested?.Invoke(sessionId);
        }
    }

    // Turns the transcript's raw stuck measurements into a signal, applying the sub-switches and
    // tuned thresholds. The error-streak check wins when both fire (it's the more direct "everything
    // is failing" signal). Returns null when nothing crosses a threshold — the common case.
    private StuckSignal? DetectStuck(string sessionId, string cwd)
    {
        var m = _transcripts.GetStuckMetrics(sessionId, cwd);

        if (DetectErrorStreaks && m.TrailingErrorStreak >= ErrorStreakThreshold)
            return new StuckSignal(StuckKind.ErrorStreak,
                $"{m.TrailingErrorStreak} tool calls in a row have failed — the session may be stuck.");

        if (DetectFailingLoops
            && m.LoopRepeat >= LoopRepeatThreshold
            && m.LoopErrors >= LoopErrorThreshold)
        {
            var what = string.IsNullOrEmpty(m.LoopLabel) ? "the same action" : m.LoopLabel;
            return new StuckSignal(StuckKind.FailingLoop,
                $"Repeating a failing action: {what} (×{m.LoopRepeat}, {m.LoopErrors} failed).");
        }

        return null;
    }

    private static PermissionMode ReadPermissionMode(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                return PermissionMode.Normal;
            }
            var text = File.ReadAllText(path).Trim().ToLowerInvariant();
            return text switch
            {
                "acceptedits" or "accept_edits" or "accept-edits" => PermissionMode.AcceptEdits,
                "plan" => PermissionMode.Plan,
                "auto" => PermissionMode.Auto,
                "bypass"
                or "bypassall"
                or "bypass_all"
                or "bypasspermissions"
                or "bypass_permissions" => PermissionMode.Bypass,
                _ => PermissionMode.Normal,
            };
        }
        catch
        {
            return PermissionMode.Normal;
        }
    }

    private static bool IsProcessRunning(string pid)
    {
        if (!int.TryParse(pid, out var id))
            return false;
        try
        {
            return !Process.GetProcessById(id).HasExited;
        }
        catch
        {
            return false;
        }
    }

    // ----- Event-driven trigger plumbing -------------------------------------------------

    private void EnsureWatcher()
    {
        if (_watcher != null || _disposed)
            return;
        if (!Directory.Exists(_sessionsDir))
            return;

        try
        {
            var watcher = new FileSystemWatcher(_sessionsDir)
            {
                // Watch every file in the directory: *.json session files and their sibling
                // *.mode files. Re-scanning is cheap and idempotent, so a slightly broad
                // trigger is harmless and simpler than running two watchers.
                NotifyFilter = NotifyFilters.FileName
                    | NotifyFilters.LastWrite
                    | NotifyFilters.Size,
                IncludeSubdirectories = false,
                InternalBufferSize = 64 * 1024,
            };

            watcher.Created += OnFileEvent;
            watcher.Changed += OnFileEvent;
            watcher.Deleted += OnFileEvent;
            watcher.Renamed += OnFileEvent;
            watcher.Error += OnWatcherError;
            watcher.EnableRaisingEvents = true;

            _watcher = watcher;
        }
        catch
        {
            // If the watcher can't be created the reconciliation poll still keeps state fresh.
            _watcher = null;
        }
    }

    private void OnFileEvent(object sender, FileSystemEventArgs e) => RequestScanDebounced();

    private void RequestScanDebounced()
    {
        if (_disposed)
            return;
        // (Re)arm the debounce: a single logical write often fires several events in a burst.
        _debounceTimer.Change(DebounceMs, Timeout.Infinite);
    }

    private void OnWatcherError(object sender, ErrorEventArgs e)
    {
        // The watcher buffer overflowed (or the dir went away). Tear it down so the next Scan
        // re-attaches a fresh one, and force an immediate reconciliation scan.
        try
        {
            if (_watcher != null)
            {
                _watcher.EnableRaisingEvents = false;
                _watcher.Created -= OnFileEvent;
                _watcher.Changed -= OnFileEvent;
                _watcher.Deleted -= OnFileEvent;
                _watcher.Renamed -= OnFileEvent;
                _watcher.Error -= OnWatcherError;
                _watcher.Dispose();
            }
        }
        catch { }
        _watcher = null;

        ChangeDetected?.Invoke();
    }

    private void SyncProcessSubscriptions(HashSet<string> activePids)
    {
        // Drop subscriptions for PIDs that are no longer active.
        foreach (var pid in _trackedProcesses.Keys.Where(k => !activePids.Contains(k)).ToList())
        {
            if (_trackedProcesses.Remove(pid, out var proc))
            {
                try { proc.Exited -= OnTrackedProcessExited; } catch { }
                proc.Dispose();
            }
        }

        // Add subscriptions for newly-seen PIDs so an unclean exit (which leaves a stale
        // session file and fires no filesystem event) still triggers a re-scan.
        foreach (var pid in activePids)
        {
            if (_trackedProcesses.ContainsKey(pid))
                continue;
            if (!int.TryParse(pid, out var id))
                continue;
            try
            {
                var proc = Process.GetProcessById(id);
                proc.EnableRaisingEvents = true;
                proc.Exited += OnTrackedProcessExited;
                if (proc.HasExited)
                {
                    // Exited between scan and subscribe; reconciliation/next scan will clean up.
                    proc.Exited -= OnTrackedProcessExited;
                    proc.Dispose();
                    continue;
                }
                _trackedProcesses[pid] = proc;
            }
            catch
            {
                // Process gone or inaccessible; the reconciliation poll covers it.
            }
        }
    }

    private void OnTrackedProcessExited(object? sender, EventArgs e) => ChangeDetected?.Invoke();

    public void Acknowledge(string pid) => _idleSince.Remove(pid);

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;

        _debounceTimer.Dispose();

        if (_watcher != null)
        {
            try
            {
                _watcher.EnableRaisingEvents = false;
                _watcher.Created -= OnFileEvent;
                _watcher.Changed -= OnFileEvent;
                _watcher.Deleted -= OnFileEvent;
                _watcher.Renamed -= OnFileEvent;
                _watcher.Error -= OnWatcherError;
                _watcher.Dispose();
            }
            catch { }
            _watcher = null;
        }

        foreach (var proc in _trackedProcesses.Values)
        {
            try { proc.Exited -= OnTrackedProcessExited; } catch { }
            proc.Dispose();
        }
        _trackedProcesses.Clear();
    }
}
