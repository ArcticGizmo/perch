using Perch.Data;
using Perch.Platform;
using Xunit;

namespace Perch.Tests;

/// <summary>
/// End-to-end coverage of the "done" alert around <em>async</em> (background) sub-agents, through a real
/// <see cref="SessionMonitor.Scan"/>.
///
/// The scenario: a session launches an async agent. While it runs, and in the gap after it finishes but
/// before Claude Code injects its <c>&lt;task-notification&gt;</c> result, the session reads idle — looking
/// exactly like a completion. But the notification re-wakes the parent, so that idle is not the session's
/// end; the real completion comes later, when the parent has processed the result. Perch used to alert on
/// the first idle (the user saw "done", then the session immediately resumed).
///
/// The monitor now holds the "done" while an async agent is <em>launched-but-not-yet-notified</em>
/// (<see cref="TranscriptReader.HasOutstandingAsyncAgent"/>, matched by tool-use-id in the parent
/// transcript — a durable signal, unlike the parent's live status which the sessions-dir watcher can miss).
/// Both idle paths are gated: the busy-&gt;idle completion-settle and the sub-agent-completion grace.
/// </summary>
public class SubAgentCompletionTests : IDisposable
{
    private const string DeadPid = "2147483645";
    private const string SessionId = "sessAsyncSubCompletion";
    // The async launch's tool_use id — the same id the delivered task-notification names, linking the two.
    private const string LaunchToolUseId = "ag1";

    // The scan reads Clock.Now for the settle/grace math and Clock.UtcNow for sub-agent staleness. A
    // mutable clock lets a test advance past the windows without sleeping, while staying near the files'
    // real mtimes so nothing trips the 90s staleness guard.
    private sealed class MutableClock : IClockProvider
    {
        public DateTime UtcNow { get; set; } = DateTime.UtcNow;
        public DateTime Now => UtcNow.ToLocalTime();
        public void Advance(TimeSpan by) => UtcNow += by;
    }

    private sealed class AlwaysAlive : IProcessProbe
    {
        public bool IsAlive(int pid) => true;
    }

    private readonly string _sessionFile = Path.Combine(ClaudePaths.SessionsDir, $"{DeadPid}.json");
    private readonly string _projectDir = Path.Combine(ClaudePaths.ProjectsDir, "C--fixtures-proj");
    private readonly string _parentTranscript;
    private readonly string _subagentsDir;
    private readonly string _agentTranscript;
    private readonly string _agentMeta;
    private readonly MutableClock _clock = new();

    public SubAgentCompletionTests()
    {
        _parentTranscript = Path.Combine(_projectDir, $"{SessionId}.jsonl");
        _subagentsDir = Path.Combine(_projectDir, SessionId, "subagents");
        _agentTranscript = Path.Combine(_subagentsDir, "agent-async1.jsonl");
        _agentMeta = Path.Combine(_subagentsDir, "agent-async1.meta.json");

        Directory.CreateDirectory(ClaudePaths.SessionsDir);
        Directory.CreateDirectory(_subagentsDir);

        File.WriteAllText(_agentMeta,
            """{"agentType":"general-purpose","description":"explore code","toolUseId":"ag1","spawnDepth":1}""");

        Clock.SetProvider(_clock);
    }

    private void WriteSessionFile(string status) =>
        File.WriteAllText(_sessionFile, $$"""
            { "pid": {{DeadPid}}, "sessionId": "{{SessionId}}",
              "status": "{{status}}", "cwd": "C:\\fixtures\\proj",
              "updatedAt": {{new DateTimeOffset(_clock.UtcNow).ToUnixTimeMilliseconds()}} }
            """);

    // Parent transcript through the launch of an async agent, ending on the parent's own completed turn.
    // The launch tool_result carries toolUseResult.isAsync == true with tool_use_id == LaunchToolUseId —
    // so the agent is "launched but not yet notified" (outstanding) until AppendResultDelivered runs.
    private void WriteParentThroughAsyncLaunch()
    {
        File.WriteAllText(_parentTranscript, string.Join("\n",
            """{"type":"user","timestamp":"2026-08-24T00:45:00Z","cwd":"C:\\fixtures\\proj","message":{"role":"user","content":"Launch a background agent"}}""",
            """{"type":"assistant","timestamp":"2026-08-24T00:46:00Z","cwd":"C:\\fixtures\\proj","message":{"role":"assistant","model":"claude-opus-4-8","usage":{"input_tokens":100,"output_tokens":20},"content":[{"type":"tool_use","id":"ag1","name":"Agent","input":{"description":"explore","subagent_type":"general-purpose"}}]}}""",
            """{"type":"user","timestamp":"2026-08-24T00:46:14Z","cwd":"C:\\fixtures\\proj","toolUseResult":{"isAsync":true,"status":"async_launched","agentId":"async1"},"message":{"role":"user","content":[{"type":"tool_result","tool_use_id":"ag1","content":[{"type":"text","text":"Async agent launched successfully."}]}]}}""",
            """{"type":"assistant","timestamp":"2026-08-24T00:46:20Z","cwd":"C:\\fixtures\\proj","message":{"role":"assistant","model":"claude-opus-4-8","usage":{"input_tokens":100,"output_tokens":20},"content":[{"type":"text","text":"Agent started; I'll report when it finishes."}]}}""",
            """{"type":"file-history-snapshot","timestamp":"2026-08-24T00:46:21Z","cwd":"C:\\fixtures\\proj","messageId":"x"}"""));
    }

    // A non-async (inline) sub-agent transcript: same shape but no isAsync launch marker.
    private void WriteParentThroughSyncReturn()
    {
        File.WriteAllText(_parentTranscript, string.Join("\n",
            """{"type":"user","timestamp":"2026-08-24T00:45:00Z","cwd":"C:\\fixtures\\proj","message":{"role":"user","content":"Do the thing"}}""",
            """{"type":"assistant","timestamp":"2026-08-24T00:46:00Z","cwd":"C:\\fixtures\\proj","message":{"role":"assistant","model":"claude-opus-4-8","usage":{"input_tokens":100,"output_tokens":20},"content":[{"type":"tool_use","id":"ag1","name":"Task","input":{"description":"explore","subagent_type":"Explore"}}]}}""",
            """{"type":"user","timestamp":"2026-08-24T00:46:14Z","cwd":"C:\\fixtures\\proj","message":{"role":"user","content":[{"type":"tool_result","tool_use_id":"ag1","content":"The sub-agent found the root cause in Foo.cs"}]}}""",
            """{"type":"assistant","timestamp":"2026-08-24T00:46:20Z","cwd":"C:\\fixtures\\proj","message":{"role":"assistant","model":"claude-opus-4-8","usage":{"input_tokens":100,"output_tokens":20},"content":[{"type":"text","text":"Done — the root cause was in Foo.cs."}]}}""",
            """{"type":"file-history-snapshot","timestamp":"2026-08-24T00:46:21Z","cwd":"C:\\fixtures\\proj","messageId":"x"}"""));
    }

    // The agent's result finally reaches the parent as a task-notification (naming LaunchToolUseId) that
    // the parent then answers — so the async agent is no longer outstanding.
    private void AppendResultDelivered()
    {
        File.AppendAllText(_parentTranscript, "\n" + string.Join("\n",
            """{"type":"user","timestamp":"2026-08-24T00:47:20Z","cwd":"C:\\fixtures\\proj","message":{"role":"user","content":"<task-notification> <tool-use-id>ag1</tool-use-id> <status>completed</status> </task-notification>"}}""",
            """{"type":"assistant","timestamp":"2026-08-24T00:47:30Z","cwd":"C:\\fixtures\\proj","message":{"role":"assistant","model":"claude-opus-4-8","usage":{"input_tokens":200,"output_tokens":40},"content":[{"type":"text","text":"Here is the summary of recent changes."}]}}""",
            """{"type":"file-history-snapshot","timestamp":"2026-08-24T00:47:31Z","cwd":"C:\\fixtures\\proj","messageId":"y"}"""));
    }

    // Sub-agent transcript whose tail is an unfinished tool_use -> classified "working".
    private void WriteAgentWorking(DateTime mtimeUtc)
    {
        File.WriteAllText(_agentTranscript,
            """
            {"type":"user","timestamp":"2026-08-24T00:46:15Z","message":{"role":"user","content":"go explore"}}
            {"type":"assistant","timestamp":"2026-08-24T00:46:20Z","message":{"role":"assistant","content":[{"type":"tool_use","id":"t1","name":"Grep","input":{"pattern":"x"}}]}}
            """);
        File.SetLastWriteTimeUtc(_agentTranscript, mtimeUtc);
    }

    // Sub-agent transcript whose tail is a completed assistant turn -> classified "idle" (finished).
    private void WriteAgentFinished(DateTime mtimeUtc)
    {
        File.WriteAllText(_agentTranscript,
            """
            {"type":"user","timestamp":"2026-08-24T00:46:15Z","message":{"role":"user","content":"go explore"}}
            {"type":"assistant","timestamp":"2026-08-24T00:46:20Z","message":{"role":"assistant","content":[{"type":"tool_use","id":"t1","name":"Grep","input":{"pattern":"x"}}]}}
            {"type":"user","timestamp":"2026-08-24T00:47:15Z","message":{"role":"user","content":[{"type":"tool_result","tool_use_id":"t1","content":"found it"}]}}
            {"type":"assistant","timestamp":"2026-08-24T00:47:18Z","message":{"role":"assistant","content":[{"type":"text","text":"Here is my full report."}]}}
            """);
        File.SetLastWriteTimeUtc(_agentTranscript, mtimeUtc);
    }

    [Fact]
    public void AsyncAgent_SessionBusyThenIdleOnAgentFinish_DoesNotFireDone()
    {
        // The reported bug's shape: Claude Code keeps the session busy while the background agent runs,
        // then flips it busy->idle when the agent finishes — before the task-notification lands. That
        // busy->idle opens a completion-settle window whose "done" must be suppressed.
        var start = _clock.UtcNow;
        WriteParentThroughAsyncLaunch();
        using var monitor = new SessionMonitor(new AlwaysAlive());
        var doneCount = 0;
        monitor.NeedsAttention += s => { if (s.SessionId == SessionId) doneCount++; };

        // Scan 1: session busy, agent working.
        WriteSessionFile("busy");
        WriteAgentWorking(start);
        Assert.Equal(SessionStatus.Running, Single(monitor).Status);

        // Scan 2: agent finished, session flips idle (notification not delivered yet) -> settle armed.
        WriteAgentFinished(start.AddSeconds(1));
        WriteSessionFile("idle");
        Assert.Equal(SessionStatus.Idle, Single(monitor).Status);
        Assert.Equal(0, doneCount);

        // Scan 3: settle window elapsed — but an async agent is still outstanding, so no "done".
        _clock.Advance(TimeSpan.FromSeconds(2)); // > CompletionSettleMs (1s)
        Assert.Equal(SessionStatus.Idle, Single(monitor).Status);
        Assert.Equal(0, doneCount);
    }

    [Fact]
    public void AsyncAgent_ReturnWhileParentIdle_DoesNotFireDone()
    {
        // Variant: the parent had already gone idle while the agent ran (the sub-agent-completion grace
        // path rather than completion-settle). Also suppressed while the agent is outstanding.
        var start = _clock.UtcNow;
        WriteParentThroughAsyncLaunch();
        using var monitor = new SessionMonitor(new AlwaysAlive());
        var doneCount = 0;
        monitor.NeedsAttention += s => { if (s.SessionId == SessionId) doneCount++; };

        WriteSessionFile("idle");
        WriteAgentWorking(start);
        Assert.Equal(SessionStatus.Running, Single(monitor).Status); // masked as Running by the live sub

        WriteAgentFinished(start.AddSeconds(1));
        Assert.Equal(SessionStatus.Idle, Single(monitor).Status);
        Assert.Equal(0, doneCount);

        _clock.Advance(TimeSpan.FromSeconds(5)); // past the sub-completion grace window
        Assert.Equal(SessionStatus.Idle, Single(monitor).Status);
        Assert.Equal(0, doneCount);
    }

    [Fact]
    public void AsyncAgent_AfterResultDeliveredAndParentCompletes_FiresExactlyOneDone()
    {
        var start = _clock.UtcNow;
        WriteParentThroughAsyncLaunch();
        using var monitor = new SessionMonitor(new AlwaysAlive());
        var doneCount = 0;
        monitor.NeedsAttention += s => { if (s.SessionId == SessionId) doneCount++; };

        // Agent runs and finishes while the session is busy — held silent (as above).
        WriteSessionFile("busy");
        WriteAgentWorking(start);
        monitor.Scan();
        WriteAgentFinished(start.AddSeconds(1));
        WriteSessionFile("idle");
        monitor.Scan();
        _clock.Advance(TimeSpan.FromSeconds(2));
        monitor.Scan();
        Assert.Equal(0, doneCount);

        // The task-notification now reaches the parent, which processes it and produces its real final
        // turn: the agent is no longer outstanding, so the parent's own busy->idle must alert once.
        AppendResultDelivered();
        _clock.Advance(TimeSpan.FromSeconds(1));
        WriteSessionFile("busy");
        Assert.Equal(SessionStatus.Running, Single(monitor).Status);

        _clock.Advance(TimeSpan.FromSeconds(1));
        WriteSessionFile("idle");
        monitor.Scan();                          // opens the completion-settle window
        _clock.Advance(TimeSpan.FromSeconds(2)); // > CompletionSettleMs (1s)
        Assert.Equal(SessionStatus.NeedsAttention, Single(monitor).Status);
        Assert.Equal(1, doneCount);
    }

    [Fact]
    public void NonAsyncSubAgentReturn_StillFiresDoneAfterGrace()
    {
        // Regression guard: without the async launch marker, a sub-agent that finishes while the parent is
        // idle must still fire the grace-window "done" — the suppression keys on an outstanding async
        // agent, not on disabling the sub-completion path outright.
        var start = _clock.UtcNow;
        WriteParentThroughSyncReturn();
        using var monitor = new SessionMonitor(new AlwaysAlive());
        var doneCount = 0;
        monitor.NeedsAttention += s => { if (s.SessionId == SessionId) doneCount++; };

        WriteSessionFile("idle");
        WriteAgentWorking(start);
        Assert.Equal(SessionStatus.Running, Single(monitor).Status);

        WriteAgentFinished(start.AddSeconds(1));
        Assert.Equal(SessionStatus.Idle, Single(monitor).Status);
        Assert.Equal(0, doneCount); // held during the grace window

        _clock.Advance(TimeSpan.FromSeconds(5));
        Assert.Equal(SessionStatus.NeedsAttention, Single(monitor).Status);
        Assert.Equal(1, doneCount);
    }

    private ClaudeSession Single(SessionMonitor monitor) =>
        Assert.Single(monitor.Scan(), s => s.SessionId == SessionId);

    public void Dispose()
    {
        Clock.Reset();
        try { File.Delete(_sessionFile); } catch { }
        try { Directory.Delete(Path.Combine(_projectDir, SessionId), recursive: true); } catch { }
        try { File.Delete(_parentTranscript); } catch { }
    }
}
