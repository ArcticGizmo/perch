using System.Text.RegularExpressions;

namespace Perch.Data;

/// <summary>What launched a background task. A Monitor runs as a <c>local_bash</c> task too; only the launching
/// tool's name tells it apart. See <c>docs/background-tasks-plan.md</c>.</summary>
internal enum BackgroundTaskKind { Shell, Monitor, Agent }

internal enum BackgroundTaskStatus { Running, Completed, Failed, Stopped }

/// <summary>A terminal session's still-running background task, as its transcript tells it
/// (<see cref="TranscriptReader.GetRunningBackgroundTasks"/>): no live feed, just launch and not-yet-ended.
/// <paramref name="OutputFile"/> is a shell's output path when the launch result named it.</summary>
internal sealed record RunningBackgroundTask(string TaskId, BackgroundTaskKind Kind, DateTime? StartedUtc, string? OutputFile);

/// <summary>
/// One piece of background work a session started: a <c>run_in_background</c> shell, a <c>Monitor</c>, or an async
/// <c>Agent</c>. Built by <see cref="BackgroundTaskTracker"/> from the CLI's <c>task_*</c> stream-json records and/or
/// the transcript's launch result + <c>&lt;task-notification&gt;</c>. Mutated only by the tracker (single-threaded,
/// like the conversation that owns it).
/// </summary>
internal sealed class BackgroundTask(string taskId)
{
    public string TaskId { get; } = taskId;
    public string? ToolUseId { get; internal set; }
    public BackgroundTaskKind Kind { get; internal set; } = BackgroundTaskKind.Shell;
    public string Description { get; internal set; } = "";
    public BackgroundTaskStatus Status { get; internal set; } = BackgroundTaskStatus.Running;
    public DateTime StartedUtc { get; internal set; }
    public DateTime? EndedUtc { get; internal set; }
    /// <summary>A shell's exit code, read off the notification summary ("… failed with exit code 3").</summary>
    public int? ExitCode { get; internal set; }
    /// <summary>The task's live output (<c>%TEMP%\claude\…\tasks\{id}.output</c>), once a record has named it.</summary>
    public string? OutputFile { get; internal set; }
    /// <summary>The CLI's one-line summary of how it ended.</summary>
    public string? Summary { get; internal set; }
    /// <summary>A sub-agent launched it rather than the main thread.</summary>
    public bool OwnedBySubagent { get; internal set; }
    /// <summary>A persistent Monitor (no expiry). Null when unknown or not a Monitor.</summary>
    public bool? Persistent { get; internal set; }
    /// <summary>Monitor events delivered so far, and the newest one's text. Transcript feed only: stream-json doesn't
    /// echo the event records.</summary>
    public int EventCount { get; internal set; }
    public string? LastEvent { get; internal set; }
    /// <summary>An agent's latest progress line ("Running Sleep for 15 seconds").</summary>
    public string? Progress { get; internal set; }

    public bool IsRunning => Status == BackgroundTaskStatus.Running;
}

/// <summary>
/// A parsed <c>&lt;task-notification&gt;</c>, from a transcript user record or (field for field) the stream-json
/// <c>task_notification</c> record. A Monitor <em>event</em> carries only a task id, a summary and the event text: no
/// tool-use id and no status, because the task is still running.
/// </summary>
internal sealed record TaskNotification(
    string TaskId, string? ToolUseId, string? Status, string? OutputFile, string? Summary, string? EventText = null)
{
    public bool IsEvent => EventText is not null && Status is null;

    private static readonly Regex Tag = new(@"<(task-id|tool-use-id|output-file|status|summary|event)>(.*?)</\1>",
        RegexOptions.Compiled | RegexOptions.Singleline);

    /// <summary>Parses the notification at the start of <paramref name="text"/>, or null when it isn't one. Only the
    /// small header tags are read; an agent's <c>&lt;result&gt;</c> (which can be huge) is skipped.</summary>
    public static TaskNotification? Parse(string? text)
    {
        if (text is null) return null;
        var start = text.IndexOf("<task-notification>", StringComparison.Ordinal);
        if (start < 0) return null;
        var end = text.IndexOf("</task-notification>", start, StringComparison.Ordinal);
        // The tags sit before any <result>; stop there so a report quoting tags can't be read as the header.
        var resultAt = text.IndexOf("<result>", start, StringComparison.Ordinal);
        int stop = resultAt >= 0 ? resultAt : end >= 0 ? end : text.Length;
        var body = text[start..stop];
        string? id = null, toolUse = null, file = null, status = null, summary = null, ev = null;
        foreach (Match m in Tag.Matches(body))
        {
            var v = m.Groups[2].Value.Trim();
            switch (m.Groups[1].Value)
            {
                case "task-id": id ??= v; break;
                case "tool-use-id": toolUse ??= v; break;
                case "output-file": file ??= v; break;
                case "status": status ??= v; break;
                case "summary": summary ??= v; break;
                case "event": ev ??= m.Groups[2].Value.Trim('\n', '\r'); break;
            }
        }
        return id is { Length: > 0 } ? new TaskNotification(id, toolUse, status, file, summary, ev) : null;
    }

    private static readonly Regex ExitCodeRx = new(@"exit code (-?\d+)", RegexOptions.Compiled);

    /// <summary>The exit code a shell's summary reports ("failed with exit code 3", "completed (exit code 0)").</summary>
    public static int? ExitCodeOf(string? summary) =>
        summary is not null && ExitCodeRx.Match(summary) is { Success: true } m && int.TryParse(m.Groups[1].Value, out var c)
            ? c : null;

    /// <summary>The CLI's status word as a task status. <c>killed</c> (task_updated) and <c>stopped</c>
    /// (task_notification) are the same end. Null for an unknown or missing word.</summary>
    public static BackgroundTaskStatus? StatusOf(string? status) => status switch
    {
        "completed" => BackgroundTaskStatus.Completed,
        "failed" => BackgroundTaskStatus.Failed,
        "killed" or "stopped" => BackgroundTaskStatus.Stopped,
        "running" => BackgroundTaskStatus.Running,
        _ => null,
    };
}

/// <summary>
/// Folds the background-task signals of one session into <see cref="BackgroundTask"/>s. Two feeds, both optional and
/// idempotent so a notification seen twice (or from both) settles a task once:
/// <list type="bullet">
/// <item>stream-json (controlled sessions): <c>task_started</c> / <c>task_progress</c> / <c>task_updated</c> /
/// <c>task_notification</c> / <c>background_tasks_changed</c>;</item>
/// <item>transcript (history, tails): the launch tool_result's <c>toolUseResult</c> and the delivered
/// <c>&lt;task-notification&gt;</c>.</item>
/// </list>
/// The launching tool calls are noted first (<see cref="NoteToolUse"/>) so a Monitor can be told from a shell.
/// UI-free and single-threaded.
/// </summary>
internal sealed class BackgroundTaskTracker(Func<DateTime>? clock = null)
{
    private readonly Func<DateTime> _clock = clock ?? (() => DateTime.UtcNow);
    private readonly Dictionary<string, BackgroundTask> _byId = new(StringComparer.Ordinal);
    private readonly List<BackgroundTask> _order = new();
    private readonly Dictionary<string, (string Name, string? Description)> _tools = new(StringComparer.Ordinal);

    /// <summary>Every task seen, oldest first.</summary>
    public IReadOnlyList<BackgroundTask> All => _order;

    /// <summary>The tasks still running, oldest first.</summary>
    public IReadOnlyList<BackgroundTask> Running => _order.Where(t => t.IsRunning).ToList();

    public int RunningCount => _order.Count(t => t.IsRunning);

    /// <summary>A task changed (created, progressed or ended).</summary>
    public event Action<BackgroundTask>? Changed;

    public BackgroundTask? Get(string? taskId) => taskId is not null && _byId.TryGetValue(taskId, out var t) ? t : null;

    public BackgroundTask? ByToolUseId(string? toolUseId) =>
        toolUseId is null ? null : _order.FirstOrDefault(t => t.ToolUseId == toolUseId);

    /// <summary>A tool call was made. Remembered so a task launched by it gets the right kind and a description.</summary>
    public void NoteToolUse(string toolUseId, string toolName, string? description)
    {
        if (toolUseId.Length > 0) _tools[toolUseId] = (toolName, description);
    }

    /// <summary>A launch tool_result named its task (<c>backgroundTaskId</c>, a Monitor's <c>taskId</c>, or an async
    /// agent's <c>agentId</c>).</summary>
    public BackgroundTask Launched(string toolUseId, string taskId, BackgroundTaskKind kind, DateTime? at = null,
        bool? persistent = null)
    {
        var t = GetOrAdd(taskId, at);
        t.ToolUseId ??= toolUseId;
        t.Kind = KindFor(toolUseId, kind);
        t.Persistent ??= persistent;
        if (t.Description.Length == 0) t.Description = DescriptionFor(toolUseId) ?? "";
        Changed?.Invoke(t);
        return t;
    }

    /// <summary><c>task_started</c>. Foreground tasks (<paramref name="isBackgrounded"/> false: a plain shell call
    /// the CLI also tracks) are ignored, since they aren't background work.</summary>
    public BackgroundTask? Started(string taskId, string? toolUseId, string? taskType, string? description,
        bool isBackgrounded, bool ownedBySubagent, DateTime? at = null)
    {
        if (!isBackgrounded) return null;
        var t = GetOrAdd(taskId, at);
        t.ToolUseId ??= toolUseId;
        t.Kind = KindFor(toolUseId, taskType == "local_agent" ? BackgroundTaskKind.Agent : BackgroundTaskKind.Shell);
        if (description is { Length: > 0 }) t.Description = description;
        t.OwnedBySubagent |= ownedBySubagent;
        Changed?.Invoke(t);
        return t;
    }

    /// <summary><c>task_progress</c>: an agent's latest step. Unknown tasks are ignored.</summary>
    public void Progressed(string taskId, string? description)
    {
        if (Get(taskId) is not { IsRunning: true } t || description is not { Length: > 0 }) return;
        t.Progress = description;
        Changed?.Invoke(t);
    }

    /// <summary><c>task_updated</c> with a <c>patch.status</c>. Unknown tasks are ignored (only background tasks
    /// are tracked, and a foreground one's update arrives here too).</summary>
    public void Updated(string taskId, string? status, DateTime? endUtc)
    {
        if (Get(taskId) is not { } t || TaskNotification.StatusOf(status) is not { } s) return;
        if (Settle(t, s, endUtc)) Changed?.Invoke(t);
    }

    /// <summary>A task notification. Returns the task the first time a notification ends it (so the caller can post
    /// a notice exactly once), or, for a Monitor event, the task the event belongs to. Null when nothing new
    /// happened: a repeat, or a foreground task's notification. <paramref name="delivered"/> is true for the
    /// transcript's delivered <c>&lt;task-notification&gt;</c> record, which only background work produces, so an
    /// unseen task is created from it. On the stream a foreground shell notifies too, so an unseen id is ignored.</summary>
    public BackgroundTask? Notified(TaskNotification n, DateTime? at = null, bool delivered = false)
    {
        if (n.IsEvent)
        {
            var monitor = GetOrAdd(n.TaskId, at);
            monitor.Kind = BackgroundTaskKind.Monitor;
            if (monitor.Description.Length == 0 && MonitorName(n.Summary) is { } name) monitor.Description = name;
            monitor.EventCount++;
            monitor.LastEvent = n.EventText;
            Changed?.Invoke(monitor);
            return monitor;
        }

        var t = Get(n.TaskId) ?? ByToolUseId(n.ToolUseId);
        if (t is null)
        {
            if (!delivered) return null;
            t = GetOrAdd(n.TaskId, at);
            t.ToolUseId = n.ToolUseId;
            t.Kind = KindFor(n.ToolUseId, n.TaskId.StartsWith('a') ? BackgroundTaskKind.Agent : BackgroundTaskKind.Shell);
            t.Description = DescriptionFor(n.ToolUseId) ?? "";
        }
        t.OutputFile ??= n.OutputFile is { Length: > 0 } f ? f : null;
        if (n.Summary is { Length: > 0 }) t.Summary = n.Summary;
        t.ExitCode ??= TaskNotification.ExitCodeOf(n.Summary);
        // The notification is the final word. It can correct a task_updated: an update that said completed and a
        // notification that says failed reads failed. ("killed" then "stopped" is the same end.)
        var status = TaskNotification.StatusOf(n.Status) ?? BackgroundTaskStatus.Completed;
        if (status != BackgroundTaskStatus.Running) { t.Status = status; t.EndedUtc ??= at ?? _clock(); }
        Changed?.Invoke(t);
        return _noticed.Add(t.TaskId) ? t : null;
    }

    private readonly HashSet<string> _noticed = new(StringComparer.Ordinal);

    /// <summary><c>background_tasks_changed</c>: the CLI's whole current set. A tracked task missing from it has
    /// ended (its own update normally said so first). A task in it we haven't seen is added.</summary>
    public void Reconcile(IReadOnlyList<(string TaskId, string? TaskType, string? Description)> current, DateTime? at = null)
    {
        var live = new HashSet<string>(current.Select(c => c.TaskId), StringComparer.Ordinal);
        foreach (var (id, type, desc) in current)
        {
            if (_byId.ContainsKey(id)) continue;
            var t = GetOrAdd(id, at);
            t.Kind = type == "local_agent" ? BackgroundTaskKind.Agent : BackgroundTaskKind.Shell;
            t.Description = desc ?? "";
            Changed?.Invoke(t);
        }
        foreach (var t in _order)
            if (t.IsRunning && !live.Contains(t.TaskId) && Settle(t, BackgroundTaskStatus.Completed, at))
                Changed?.Invoke(t);
    }

    /// <summary>The session ended: nothing it started survives the CLI, so every running task stops.</summary>
    public void SessionEnded()
    {
        foreach (var t in _order)
            if (Settle(t, BackgroundTaskStatus.Stopped, null)) Changed?.Invoke(t);
    }

    /// <summary>History load: a transcript records no end for a task its session took down with it (the CLI exited
    /// before it finished), so anything still "running" at the end of a finished transcript is closed as stopped.</summary>
    public void FinalizeHistory()
    {
        foreach (var t in _order)
            if (Settle(t, BackgroundTaskStatus.Stopped, null)) Changed?.Invoke(t);
    }

    /// <summary>Takes over another tracker's tasks (a history load folded into a scratch conversation), in front of
    /// this one's. A task id already here wins. Raises nothing: the adopted tasks are history.</summary>
    public void Adopt(BackgroundTaskTracker other)
    {
        var adopted = other._order.Where(t => !_byId.ContainsKey(t.TaskId)).ToList();
        foreach (var t in adopted) _byId[t.TaskId] = t;
        _order.InsertRange(0, adopted);
        foreach (var id in other._noticed) _noticed.Add(id);
        foreach (var (id, tool) in other._tools) _tools.TryAdd(id, tool);
    }

    private bool Settle(BackgroundTask t, BackgroundTaskStatus status, DateTime? at)
    {
        if (!t.IsRunning || status == BackgroundTaskStatus.Running) return false;
        t.Status = status;
        t.EndedUtc ??= at ?? _clock();
        return true;
    }

    private BackgroundTask GetOrAdd(string taskId, DateTime? at)
    {
        if (_byId.TryGetValue(taskId, out var t)) return t;
        t = new BackgroundTask(taskId) { StartedUtc = at ?? _clock() };
        _byId[taskId] = t;
        _order.Add(t);
        return t;
    }

    private BackgroundTaskKind KindFor(string? toolUseId, BackgroundTaskKind fallback) =>
        toolUseId is not null && _tools.TryGetValue(toolUseId, out var tool)
            ? tool.Name switch
            {
                "Monitor" => BackgroundTaskKind.Monitor,
                "Agent" or "Task" => BackgroundTaskKind.Agent,
                "Bash" or "PowerShell" => BackgroundTaskKind.Shell,
                _ => fallback,
            }
            : fallback;

    private string? DescriptionFor(string? toolUseId) =>
        toolUseId is not null && _tools.TryGetValue(toolUseId, out var tool) ? tool.Description : null;

    private static readonly Regex MonitorNameRx = new("^Monitor event: \"(.*)\"$", RegexOptions.Compiled);

    private static string? MonitorName(string? summary) =>
        summary is not null && MonitorNameRx.Match(summary) is { Success: true } m ? m.Groups[1].Value : null;
}
