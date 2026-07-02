namespace Perch.Data;

public enum SessionStatus
{
    Idle = 0,
    Running = 1,
    NeedsAttention = 2,
    AwaitingInput = 3,
}

/// <summary>The kinds of desktop notification Perch raises for a session, each with its
/// own settings toggle. "Done" = work finished; "WaitingForInput" = blocked on a prompt.</summary>
public enum NotificationKind
{
    Done = 0,
    WaitingForInput = 1,
}

public enum PermissionMode
{
    Normal = 0,
    AcceptEdits = 1,
    Plan = 2,
    Auto = 3,
    Bypass = 4,
}

/// <summary>
/// A Claude Code sub-agent running under a parent session — either an ordinary Task/Agent
/// invocation, or a persistent <em>teammate</em> (Agent Teams). Neither has a session file of its
/// own: they execute in the parent's process and are surfaced from their per-agent transcript and
/// <c>.meta.json</c> sidecar under <c>{sessionId}/subagents/</c> (see <see cref="SubAgentReader"/>).
///
/// Teammates differ from ordinary sub-agents in two ways that matter here: their meta carries
/// <c>taskKind == "in_process_teammate"</c> plus a human <see cref="Name"/>/<see cref="Color"/>, and
/// they are <em>persistent</em> — a teammate stays alive and idle (waiting for the lead to message it)
/// rather than disappearing when a task finishes. So a teammate is surfaced whenever its transcript
/// exists, with <see cref="IsIdle"/> telling working from waiting; an ordinary sub-agent is only ever
/// surfaced while it is actively working.
/// </summary>
public record SubAgent(
    string AgentId,             // tool_use id (ordinary) or agentId (teammate) — stable per invocation
    string Description,         // the Task's short description, used as the row label for sub-agents
    string AgentType,           // subagent_type, e.g. "general-purpose", "Explore"
    bool IsTeammate = false,    // true when meta.taskKind == "in_process_teammate"
    string? Name = null,        // teammate's name (the "@arch-explorer" label); null for plain sub-agents
    string? TeamName = null,    // the team this member belongs to, e.g. "session-a0a997f1"
    string? Color = null,       // Claude-assigned member colour ("green"/"yellow"/"blue"/…); null if none
    string? Activity = null,    // present-tense phrase for what it's doing now ("Reading Foo.cs"); null when idle
    bool IsIdle = false,        // teammate is alive but waiting (tail is a finished assistant turn)
    bool IsStale = false        // classified working but the transcript went silent (interrupted/frozen mid-turn)
);

/// <summary>
/// A web Artifact this session has published (via the Artifact tool) to claude.ai. Surfaced from the
/// transcript: each publish leaves a tool result whose <c>toolUseResult.url</c> is the hosted page.
/// Re-publishing reuses the same URL, so artifacts are de-duplicated by <see cref="Url"/>.
/// </summary>
public record Artifact(
    string Url,    // https://claude.ai/code/artifact/{id} — the page to open
    string Title   // the artifact's title, shown in the picker when a session has several
);

/// <summary>The lifecycle state of a task in a session's checklist. Mirrors the vocabulary of
/// Claude Code's <c>TaskUpdate</c> tool (<c>pending</c> / <c>in_progress</c> / <c>completed</c>).</summary>
public enum TaskState
{
    Pending = 0,
    InProgress = 1,
    Completed = 2,
}

/// <summary>
/// One entry in a session's task list — the native checklist Claude Code builds with the
/// <c>TaskCreate</c> tool and advances with <c>TaskUpdate</c>. Current Claude Code keeps no durable
/// on-disk task file (the <c>~/.claude/tasks/{id}/</c> dir holds only a lock + id counter), so the
/// list is reconstructed by replaying those tool calls from the transcript in creation order.
/// See <see cref="TranscriptReader.GetTasks"/>.
/// </summary>
public record TaskItem(
    string Subject,     // the task's headline, e.g. "Phase 1 — Slash commands for instance CRUD"
    string ActiveForm,  // present-tense label Claude shows while it's the active task ("Building slash commands…")
    TaskState State
);

public record ClaudeSession(
    string Pid,
    string SessionId,
    SessionStatus Status,
    string Cwd,
    string ProjectName,
    DateTime LastUpdated,
    PermissionMode Mode = PermissionMode.Normal,
    IReadOnlyList<SubAgent>? SubAgents = null,
    string? Activity = null,
    DateTime? RunningSince = null,
    string? BridgeSessionId = null,
    bool ExternalNotify = false,
    string? Title = null,
    float? ContextFill = null,
    int ContextWindow = ModelContext.DefaultWindow,
    IReadOnlyList<Artifact>? Artifacts = null,
    StuckSignal? Stuck = null,
    IReadOnlyList<TaskItem>? Tasks = null,
    double? BurnRate = null,
    DateTime? AwaitingSince = null
)
{
    /// <summary>Running sub-agents under this session; never null.</summary>
    public IReadOnlyList<SubAgent> SubAgents { get; init; } = SubAgents ?? [];

    /// <summary>Web Artifacts this session has published to claude.ai; never null. See
    /// <see cref="TranscriptReader.GetArtifacts"/>.</summary>
    public IReadOnlyList<Artifact> Artifacts { get; init; } = Artifacts ?? [];

    /// <summary>True when this session has at least one published Artifact to open.</summary>
    public bool HasArtifacts => Artifacts.Count > 0;

    /// <summary>The native task checklist Claude is working through (<c>TaskCreate</c>/<c>TaskUpdate</c>),
    /// in creation order; never null. See <see cref="TranscriptReader.GetTasks"/>.</summary>
    public IReadOnlyList<TaskItem> Tasks { get; init; } = Tasks ?? [];

    /// <summary>True when this session has a task checklist at all.</summary>
    public bool HasTasks => Tasks.Count > 0;

    /// <summary>
    /// The session's current token burn rate in tokens per minute, measured over the most recent
    /// continuous burst of assistant turns; null when it isn't running or there's too little recent
    /// activity to compute a rate. See <see cref="TranscriptReader.GetBurnRate"/>.
    /// </summary>
    public double? BurnRate { get; init; } = BurnRate;

    /// <summary>How many tasks in the checklist are completed.</summary>
    public int CompletedTaskCount => Tasks.Count(t => t.State == TaskState.Completed);

    /// <summary>The task currently being worked (first <see cref="TaskState.InProgress"/>), or null.
    /// Its <see cref="TaskItem.ActiveForm"/> is the "chipping away at the list" phrase the overlay
    /// shows on a running row.</summary>
    public TaskItem? CurrentTask => Tasks.FirstOrDefault(t => t.State == TaskState.InProgress);

    /// <summary>An advisory "this session may be stuck/spinning" signal (repeated failures or a
    /// failing loop) derived from the transcript tail, or null when nothing looks wrong. Only ever
    /// set while the session is Running and detection is enabled. See <see cref="StuckSignal"/>.</summary>
    public StuckSignal? Stuck { get; init; } = Stuck;

    /// <summary>True when this session has been flagged as possibly stuck.</summary>
    public bool IsStuck => Stuck != null;

    /// <summary>
    /// The explicit session name set by Claude Code's built-in <c>/rename</c> command (a
    /// <c>custom-title</c> transcript record). Null when the session was never renamed. The
    /// auto-generated <c>ai-title</c> is deliberately ignored. Normalised so blank is null.
    /// See <see cref="TranscriptReader.GetTitle"/>.
    /// </summary>
    public string? Title { get; init; } = string.IsNullOrWhiteSpace(Title) ? null : Title.Trim();

    /// <summary>
    /// The label to show the user for this session: the explicit <c>/rename</c> <see cref="Title"/>
    /// when one has been set, otherwise the <see cref="ProjectName"/> derived from the working
    /// directory (or, lacking that, the session id prefix). Internal logic that must match the real
    /// terminal window — e.g. focusing it by title — should keep using <see cref="ProjectName"/>.
    /// </summary>
    public string DisplayName => Title ?? ProjectName;

    /// <summary>
    /// True while this session is connected to the mobile app / claude.ai via /remote-control —
    /// i.e. its session file carries a <c>bridgeSessionId</c>. That id is also the deep-link target
    /// encoded into the QR code (https://claude.ai/code/{BridgeSessionId}).
    /// </summary>
    public bool RemoteControlled => !string.IsNullOrEmpty(BridgeSessionId);

    /// <summary>
    /// True when this session has opted in to external (ntfy) notifications — i.e. its session file
    /// has a sibling <c>{sessionId}.notify</c> marker. The marker is the single source of truth,
    /// written/removed both by the overlay's right-click toggle and the plugin's <c>/afk</c> command.
    /// </summary>
    public bool ExternalNotify { get; init; } = ExternalNotify;

    /// <summary>
    /// When this session most recently entered the current continuous <see cref="SessionStatus.AwaitingInput"/>
    /// stretch — i.e. when it started blocking on the user — so the overlay can show a "waiting on you"
    /// timer. Null unless the session is awaiting input.
    /// </summary>
    public DateTime? AwaitingSince { get; init; } = AwaitingSince;

    /// <summary>
    /// How long this session has been continuously running, as a compact label showing only the
    /// most significant unit ("8s", "3m", "2h"). Null when the session isn't running.
    /// </summary>
    public string? RunningElapsedLabel() =>
        RunningSince is { } start ? FormatElapsed(DateTime.Now - start) : null;

    /// <summary>How long this session has been blocked awaiting input, as the same compact label
    /// (<see cref="RunningElapsedLabel"/>). Null when the session isn't awaiting input.</summary>
    public string? AwaitingElapsedLabel() =>
        AwaitingSince is { } start ? FormatElapsed(DateTime.Now - start) : null;

    /// <summary>The continuous time this session has been blocked awaiting input, or null when it
    /// isn't. Lets the overlay warm the "waiting on you" timer from yellow toward red as it grows.</summary>
    public TimeSpan? AwaitingElapsed() =>
        AwaitingSince is { } start ? Max(DateTime.Now - start, TimeSpan.Zero) : null;

    // Compact "most significant unit" label ("8s", "3m", "2h"); clamps negatives to zero.
    private static string FormatElapsed(TimeSpan elapsed)
    {
        elapsed = Max(elapsed, TimeSpan.Zero);
        if (elapsed.TotalHours >= 1)
            return $"{(int)elapsed.TotalHours}h";
        if (elapsed.TotalMinutes >= 1)
            return $"{(int)elapsed.TotalMinutes}m";
        return $"{(int)elapsed.TotalSeconds}s";
    }

    private static TimeSpan Max(TimeSpan a, TimeSpan b) => a < b ? b : a;
}
