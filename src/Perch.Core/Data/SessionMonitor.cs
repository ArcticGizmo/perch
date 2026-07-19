using System.Diagnostics;
using System.Text.Json.Nodes;
using Perch.Data;

namespace Perch.Data;

internal sealed class SessionMonitor : IDisposable
{
    private const int DebounceMs = 150;

    // Grace window after a session's sub-agents finish (while the parent still reads as idle) before
    // we raise a synthetic "done". Long enough for the parent to flip to busy as it resumes to process
    // the sub-agent's result — that transition cancels the synthetic done so the parent's own
    // busy->idle fires the single notification — yet short enough that a session that genuinely stops
    // on the sub-agent's return still alerts promptly. See _subsFinishedIdleAt.
    private const int SubsCompletionGraceMs = 3000;

    // Settle window applied to a plain busy->idle "done" before it's raised. A user cancelling the turn
    // (Esc/Ctrl+C) flips the session busy->idle exactly like a real completion; the only on-disk tell
    // apart is the "[Request interrupted by user]" marker Claude Code appends to the transcript — and
    // the transcript is NOT watched, so that marker often lands a beat after the session-status flip
    // that triggers the scan. Committing to "done" on that single edge scan therefore races the marker
    // and fires a false alert on a deliberate cancel. Instead we hold plain idle for this window, then
    // re-read the transcript: a marker that has since landed suppresses the alert; otherwise the
    // completion fires (a real completion never grows a marker, so it just costs this small delay).
    // See _completionSettleAt.
    private const int CompletionSettleMs = 1000;

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

    /// <summary>Master switch for the per-session unstaged git line-churn chip (+added / -deleted). Off
    /// by construction; the owning context sets it from settings and re-scans when the user toggles it.
    /// While off no git process is ever launched — the whole point is that it costs nothing when off.</summary>
    public bool GitStatsEnabled
    {
        get => _gitStats.Enabled;
        set => _gitStats.Enabled = value;
    }

    private readonly string _sessionsDir = ClaudePaths.SessionsDir;

    private readonly Dictionary<string, string> _lastRawStatus = new();
    private readonly Dictionary<string, DateTime> _idleSince = new();
    // When each PID last entered a continuous Running stretch, so we can show elapsed run time.
    private readonly Dictionary<string, DateTime> _runningSince = new();
    // When each PID last entered a continuous AwaitingInput stretch, so we can show a "waiting on you"
    // timer. Distinct from _awaitingInputPids, which is the one-shot notification-dedup set.
    private readonly Dictionary<string, DateTime> _awaitingSince = new();
    private readonly HashSet<string> _awaitingInputPids = new();
    // PIDs that had at least one running sub-agent on the previous scan, so we can detect the
    // moment they all finish and treat it like a busy->idle completion.
    private readonly HashSet<string> _hadRunningSubs = new();
    // When a session's sub-agents all finished while the parent was still reported idle, keyed by PID.
    // A sub-agent returning control is not the session completing — the parent almost always resumes to
    // analyse the result (flipping to busy, which clears this entry) and then raises its own busy->idle
    // "done". Firing here as well is a double-notification, so we defer: the synthetic "done" is raised
    // only if the parent is still idle once SubsCompletionGraceMs elapses, i.e. it picked nothing up.
    private readonly Dictionary<string, DateTime> _subsFinishedIdleAt = new();
    // When a plain busy->idle "done" was seen but is being held for CompletionSettleMs, keyed by PID,
    // so a not-yet-flushed cancel marker can still appear before we commit to the alert. Cleared when
    // the marker lands (suppress for good), the window elapses (fire the deferred "done"), or the
    // session resumes work / disappears. See CompletionSettleMs.
    private readonly Dictionary<string, DateTime> _completionSettleAt = new();

    private readonly SubAgentReader _subAgents = new();
    private readonly TranscriptReader _transcripts = new();
    private readonly GitStatsService _gitStats = new();

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
    /// The earliest instant at which a deferred completion window elapses and must be re-scanned — either
    /// a sub-agent-completion grace or a busy->idle completion settle — so the "done" fires on time
    /// without a later file event. Null when none is pending. The "done" /
    /// <see cref="SessionStatus.NeedsAttention"/> badge itself no longer lapses on a timer, so it never
    /// contributes a deadline. Recomputed at the end of every <see cref="Scan"/>.
    /// </summary>
    public DateTime? NextNeedsAttentionDeadline { get; private set; }

    public SessionMonitor()
    {
        _debounceTimer = new System.Threading.Timer(_ => ChangeDetected?.Invoke());
        // A background git refresh landing with new numbers should repaint the overlay — treat it like
        // any other change trigger. The rescan re-reads the (now-fresh) cache, so it settles at once.
        _gitStats.StatsUpdated += () => ChangeDetected?.Invoke();
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
            _awaitingSince.Remove(key);
            _awaitingInputPids.Remove(key);
            _hadRunningSubs.Remove(key);
            _subsFinishedIdleAt.Remove(key);
            _completionSettleAt.Remove(key);
        }

        SyncProcessSubscriptions(activePids);
        NextNeedsAttentionDeadline = ComputeNextDeadline();

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

    /// <summary>
    /// Sets or clears a session's pinned note by writing or deleting its <c>{sessionId}.note</c> sidecar.
    /// A null/blank <paramref name="text"/> removes the note; otherwise it's stored as a small JSON payload
    /// (<c>{ "text": …, "updatedAt": … }</c>) so pin/colour can be layered on later without a format change.
    /// Best-effort — never throws. Call <see cref="Scan"/> afterwards to refresh the session list and glyphs.
    /// The sidecar is deliberately left on disk when the session ends (unlike <c>.mode</c>/<c>.notify</c>),
    /// so a note rides along into the history viewer.
    /// </summary>
    public void SetNote(string sessionId, string? text)
    {
        if (string.IsNullOrEmpty(sessionId))
            return;
        var path = Path.Combine(_sessionsDir, $"{sessionId}.note");
        try
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                if (File.Exists(path))
                    File.Delete(path);
                return;
            }
            Directory.CreateDirectory(_sessionsDir);
            var payload = new JsonObject
            {
                ["text"] = text.Trim(),
                ["updatedAt"] = DateTime.UtcNow.ToString("o"),
            };
            File.WriteAllText(path, payload.ToJsonString());
        }
        catch
        {
            // Best-effort: a failed write leaves whatever was there; the next scan reflects reality.
        }
    }

    private DateTime? ComputeNextDeadline()
    {
        DateTime? earliest = null;

        // Wake at the end of any pending sub-agent-completion grace window, so a session that
        // stopped on its sub-agents' return (with no later file event to trigger a scan) still fires
        // its deferred "done" on time instead of waiting out the 30s reconcile poll.
        foreach (var finishedAt in _subsFinishedIdleAt.Values)
        {
            var deadline = finishedAt.AddMilliseconds(SubsCompletionGraceMs);
            if (earliest == null || deadline < earliest)
                earliest = deadline;
        }
        // Likewise wake at the end of any pending busy->idle completion settle, so the deferred "done"
        // fires on time (and a landed cancel marker is re-checked) without waiting out the 30s reconcile.
        foreach (var settleAt in _completionSettleAt.Values)
        {
            var deadline = settleAt.AddMilliseconds(CompletionSettleMs);
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

            // How the session was launched: "cli" for an interactive terminal, "sdk-ts"/"sdk-py" for a
            // background / SDK-driven run. The only on-disk tell that a session has no human at the
            // keyboard, so the overlay can mark it distinctly. See ClaudeSession.IsBackground.
            var entrypoint = node["entrypoint"]?.GetValue<string>();

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

            // Same lazy, cached-by-mtime read as IsBareCommand: only consulted at the busy->idle edge
            // where we'd otherwise raise "done". A user cancelling the turn (Esc/Ctrl+C) flips the
            // session busy->idle exactly like a normal completion — the only on-disk tell apart is the
            // interrupt marker Claude Code leaves in the transcript — so ask for it there and stay idle.
            bool? interrupted = null;
            bool IsInterrupted() =>
                (interrupted ??= _transcripts.LastTurnWasInterrupted(sessionId, cwd)) == true;

            // Same lazy, cached-by-mtime read, consulted only at the sub-agent-completion grace expiry
            // below. True when the transcript tail shows the parent still owes an assistant reply (a
            // sub-agent's result handed back but not yet answered), i.e. the session is mid-turn — just
            // slow to produce its first follow-up token — not actually done. See LastTurnAwaitingAssistant.
            bool? parentMidTurn = null;
            bool IsParentMidTurn() =>
                (parentMidTurn ??= _transcripts.LastTurnAwaitingAssistant(sessionId, cwd)) == true;

            SessionStatus status;
            // Set when a deferred busy->idle completion settle elapses this scan (below), so the "done"
            // notification is raised even though this is no longer a busy->idle edge.
            bool fireCompletionSettled = false;
            bool awaitingInput = IsAwaitingInput(rawStatus, waitingFor);
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
                _completionSettleAt.Remove(pid);
                status = SessionStatus.AwaitingInput;
            }
            else if (rawStatus == "busy")
            {
                _idleSince.Remove(pid);
                _completionSettleAt.Remove(pid);
                status = SessionStatus.Running;
                _awaitingInputPids.Remove(pid);
            }
            else
            {
                _awaitingInputPids.Remove(pid);
                if (prevRaw == "busy" && (IsBareCommand() || IsInterrupted()))
                {
                    // Not a real completion, so it shouldn't raise "done": either a fast built-in
                    // (e.g. /clear, /doctor) just finished without the model doing any work, or the user
                    // cancelled the turn (Esc/Ctrl+C) with the interrupt marker already on disk. Drop the
                    // idle timestamp set above and stay plain idle — no NeedsAttention glyph, no "done"
                    // alert. Removing _idleSince also makes the suppression stick: the next scan sees
                    // prevRaw=="idle" and no badge to restore.
                    _idleSince.Remove(pid);
                    _completionSettleAt.Remove(pid);
                    status = SessionStatus.Idle;
                }
                else if (prevRaw == "busy")
                {
                    // A busy->idle edge that looks like a real completion — but a user cancel flips
                    // busy->idle the same way, and Claude Code may not have flushed its interrupt marker to
                    // the (unwatched) transcript yet, so we can't tell them apart on this single edge scan.
                    // Don't commit to "done": hold plain idle and open a settle window. When it elapses we
                    // re-read the transcript below — a marker that has since landed suppresses the alert,
                    // otherwise the deferred completion fires. Drop _idleSince (set above) so no badge or
                    // glow shows while we wait.
                    _idleSince.Remove(pid);
                    _completionSettleAt[pid] = now;
                    status = SessionStatus.Idle;
                }
                else if (_completionSettleAt.TryGetValue(pid, out var settleAt))
                {
                    // Mid-settle from a prior scan (prevRaw is now "idle", still idle). Re-evaluate.
                    if (IsInterrupted() || IsBareCommand())
                    {
                        // The cancel marker landed inside the window — it was a deliberate stop, so
                        // suppress the "done" for good.
                        _completionSettleAt.Remove(pid);
                        _idleSince.Remove(pid);
                        status = SessionStatus.Idle;
                    }
                    else if ((now - settleAt).TotalMilliseconds >= CompletionSettleMs)
                    {
                        // Window elapsed with no marker: it really was a completion. Raise it now, and
                        // flag it so the notification below fires even though this isn't a busy->idle edge.
                        _completionSettleAt.Remove(pid);
                        _idleSince[pid] = now;
                        status = SessionStatus.NeedsAttention;
                        fireCompletionSettled = true;
                    }
                    else
                        // Still within the window — keep waiting silently.
                        status = SessionStatus.Idle;
                }
                else if (_idleSince.ContainsKey(pid))
                    // A completed session keeps its "done" badge until the user acknowledges it (focuses
                    // the terminal / clicks the alert, via Acknowledge) or it resumes work (busy clears
                    // _idleSince above). It is never cleared on a timer.
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
            // The gates reason over the whole flock (a working agent can be nested under another), so
            // flatten the tree; the tree itself is what the session below carries for the overlay.
            var allSubs = subAgents.SelectMany(s => s.SelfAndDescendants()).ToList();
            bool hasRunningSubs = allSubs.Any(s => !s.IsIdle);
            // A teammate frozen mid-turn by an interrupt eventually goes stale and flips to idle, which
            // is what drops hasRunningSubs here. That's a deliberate stop, not a completion — so when a
            // stale teammate is present we let the parent fall back to plain Idle without the "done" alert.
            bool subsWentStale = allSubs.Any(s => s.IsStale);
            bool hadRunningSubs = _hadRunningSubs.Contains(pid);
            bool subsJustFinished = hadRunningSubs && !hasRunningSubs;
            // Set when the deferred sub-agent completion below actually fires this scan, so the
            // notification at the end is raised even though this is not a parent busy->idle edge.
            bool fireSubsCompletion = false;

            if (hasRunningSubs)
            {
                _hadRunningSubs.Add(pid);
                // Fresh sub-agent activity supersedes any pending completion watch from a prior batch.
                _subsFinishedIdleAt.Remove(pid);
                // A live sub-agent means the session is working, not completing, so any pending busy->idle
                // settle from this scan (or an earlier one) is void.
                _completionSettleAt.Remove(pid);
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

                // Sub-agents finished and the parent reads as idle. Do NOT fire "done" here yet: a
                // sub-agent returning control is not the session completing — the parent almost always
                // resumes to analyse the result (flipping to busy) and then raises its own busy->idle
                // "done". Firing now as well is the double-notification users see. Instead arm a short
                // grace window — unless the subs went stale (the team was interrupted on purpose), in
                // which case there's no completion to report.
                // Guard against a pending busy->idle completion settle owning the same edge: let that one
                // path raise the single "done" rather than arming a second (overlapping) window here.
                if (subsJustFinished && !subsWentStale && status == SessionStatus.Idle
                    && !_subsFinishedIdleAt.ContainsKey(pid) && !_completionSettleAt.ContainsKey(pid))
                    _subsFinishedIdleAt[pid] = now;

                // Still idle once the grace window has elapsed: the parent never picked the work back
                // up (a busy transition would have cleared the entry below), so the sub-agents' return
                // really was the end of the session's work. Surface it like a busy->idle completion —
                // unless the transcript tail shows the parent still mid-turn (a sub-agent's result handed
                // back but not yet answered). Reading a large result back to the model can take far longer
                // than the grace window while the session file still reads idle; firing here is the
                // premature "done". In that case drop the watch and stay silent — the parent will resume
                // and its own busy->idle raises the single completion. If instead the tail is a finished
                // assistant turn, the session genuinely stopped on the sub-agents' return, so fire.
                else if (status == SessionStatus.Idle
                    && _subsFinishedIdleAt.TryGetValue(pid, out var finishedAt)
                    && (now - finishedAt).TotalMilliseconds >= SubsCompletionGraceMs)
                {
                    _subsFinishedIdleAt.Remove(pid);
                    if (!IsParentMidTurn())
                    {
                        _idleSince[pid] = now;
                        status = SessionStatus.NeedsAttention;
                        fireSubsCompletion = true;
                    }
                }
            }

            // The parent picked the work back up (resumed to busy, or hit a permission prompt): the
            // forthcoming busy->idle / awaiting-input alert is now its to raise, so drop the deferred
            // completion watch to avoid an extra "done".
            if (status is SessionStatus.Running or SessionStatus.AwaitingInput)
            {
                _subsFinishedIdleAt.Remove(pid);
                _completionSettleAt.Remove(pid);
            }

            var projectName = string.IsNullOrEmpty(cwd)
                ? sessionId[..Math.Min(8, sessionId.Length)]
                : PathLeaf.Of(cwd);

            var mode = ReadPermissionMode(Path.Combine(_sessionsDir, $"{sessionId}.mode"));

            // External-notification opt-in: the presence of a {sessionId}.notify marker is the signal.
            // Written/removed by both the overlay's right-click toggle and the plugin's /afk command.
            var externalNotify = !string.IsNullOrEmpty(sessionId)
                && File.Exists(Path.Combine(_sessionsDir, $"{sessionId}.notify"));

            // Pinned note: a short human annotation stored in a sibling {sessionId}.note sidecar,
            // written/removed by the overlay's right-click menu + note editor. Null when unset.
            var note = string.IsNullOrEmpty(sessionId)
                ? null
                : ReadNote(Path.Combine(_sessionsDir, $"{sessionId}.note"));

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

            // Likewise track the start of the current continuous AwaitingInput stretch, so the overlay
            // can show how long the session has been blocked on the user; reset once it stops waiting.
            DateTime? awaitingSince;
            if (status == SessionStatus.AwaitingInput)
            {
                if (!_awaitingSince.TryGetValue(pid, out var since))
                {
                    since = now;
                    _awaitingSince[pid] = since;
                }
                awaitingSince = since;
            }
            else
            {
                _awaitingSince.Remove(pid);
                awaitingSince = null;
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

            // Live token burn rate (tokens/min): only meaningful while the session is actively working,
            // where recent assistant turns give a current pace. Cached by mtime like the other readers.
            var burnRate = status == SessionStatus.Running
                ? _transcripts.GetBurnRate(sessionId, cwd)
                : null;

            // Web Artifacts published to claude.ai over the session's lifetime. Read from the transcript
            // and cached by mtime, so an unchanged transcript costs a stat, not a parse.
            var artifacts = _transcripts.GetArtifacts(sessionId, cwd);

            // The native task checklist Claude works through (TaskCreate/TaskUpdate), reconstructed from
            // the transcript and cached by mtime. Surfaced as a progress count + hover list in the overlay.
            var tasks = _transcripts.GetTasks(sessionId, cwd);

            // Unstaged git line churn (+added / -deleted) for this session's working tree. Fully gated
            // inside the service: while the experimental toggle is off this returns null without ever
            // launching git. Non-blocking — a stale/missing value schedules a background refresh that
            // triggers a repaint when it lands.
            var gitStats = _gitStats.Get(cwd);

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
                tasks,
                burnRate,
                awaitingSince,
                gitStats,
                entrypoint,
                note
            );

            if (status == SessionStatus.NeedsAttention
                && (fireSubsCompletion || fireCompletionSettled))
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

    // Reads a session's pinned note from its {sessionId}.note sidecar. The canonical format is a small
    // JSON object with a "text" field (see SetNote); a hand-edited plain-text file is tolerated as a
    // fallback so a note dropped in by hand still shows. Missing/blank/unparseable → null. Never throws.
    // Internal (not private) so the note round-trip can be unit-tested without a full scan.
    internal static string? ReadNote(string path)
    {
        try
        {
            if (!File.Exists(path))
                return null;
            var raw = File.ReadAllText(path);
            if (string.IsNullOrWhiteSpace(raw))
                return null;

            JsonNode? node;
            try { node = JsonNode.Parse(raw); }
            catch { node = null; } // not JSON at all — treated as plain text below

            // A JSON object is the canonical payload: trust its "text" field (a missing/blank one means no
            // note). Anything that isn't a JSON object — a hand-edited plain-text file — is used verbatim.
            string? text = node switch
            {
                JsonObject obj => obj["text"]?.GetValue<string>(),
                null           => raw,
                _              => null,
            };

            return string.IsNullOrWhiteSpace(text) ? null : text.Trim();
        }
        catch
        {
            return null;
        }
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

    /// <summary>
    /// Whether a raw session status of <paramref name="rawStatus"/> with hint
    /// <paramref name="waitingFor"/> means the model is genuinely blocked on user input (a
    /// permission prompt and the like). Claude Code reports a dedicated "waiting" status (with a
    /// "waitingFor" hint such as "permission prompt") while blocked; some flows also surface a
    /// non-empty waitingFor without flipping the status, so either normally counts.
    /// <para>
    /// The exception is <c>"dialog open"</c>: that is a passive client-side overlay (e.g.
    /// <c>/workflows</c>, <c>/model</c>, <c>/config</c>) — the user opened a CLI menu and is already
    /// at the keyboard, not being asked to respond to the model. Claude Code still reports status
    /// "waiting" the whole time it's open, so treating it as awaiting-input pegs the session as
    /// "awaiting input" for as long as the menu is up (notably while watching <c>/workflows</c>
    /// during a live run). It is not a prompt, so it must not count.
    /// </para>
    /// </summary>
    internal static bool IsAwaitingInput(string rawStatus, string? waitingFor)
    {
        if (string.Equals(waitingFor, "dialog open", StringComparison.OrdinalIgnoreCase))
            return false;
        return rawStatus == "waiting" || !string.IsNullOrWhiteSpace(waitingFor);
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
        _gitStats.Dispose();

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
