using System.Text.Json.Nodes;
using Perch.Data;
using Perch.Data.Control;
using Xunit;

namespace Perch.Tests;

/// <summary>
/// Background tasks (docs/background-tasks-plan.md): <see cref="TaskNotification"/> parsing, and
/// <see cref="BackgroundTaskTracker"/> fed through <see cref="SessionConversation"/> from real captures. The
/// <c>stream-json/bg-*.jsonl</c> fixtures are claude 2.1.292 stdout (scrubbed); <c>transcripts/bg-*.jsonl</c> are the
/// same sessions' on-disk transcripts, which is what history and the read-only tails read.
/// </summary>
public class BackgroundTaskTests
{
    [Fact]
    public void Parse_ShellFailure_ReadsHeaderAndExitCode()
    {
        var n = TaskNotification.Parse(
            "<task-notification>\n<task-id>bbm65f6ep</task-id>\n<tool-use-id>toolu_1</tool-use-id>\n" +
            "<output-file>C:\\t\\bbm65f6ep.output</output-file>\n<status>failed</status>\n" +
            "<summary>Background command \"Delay\" failed with exit code 3</summary>\n</task-notification>");
        Assert.NotNull(n);
        Assert.Equal("bbm65f6ep", n.TaskId);
        Assert.Equal("toolu_1", n.ToolUseId);
        Assert.Equal("failed", n.Status);
        Assert.Equal("C:\\t\\bbm65f6ep.output", n.OutputFile);
        Assert.False(n.IsEvent);
        Assert.Equal(3, TaskNotification.ExitCodeOf(n.Summary));
    }

    [Fact]
    public void Parse_MonitorEvent_HasEventAndNoStatus()
    {
        var n = TaskNotification.Parse(
            "<task-notification>\n<task-id>bcbddz0ke</task-id>\n<summary>Monitor event: \"tick monitor\"</summary>\n" +
            "<event>event 1</event>\nIf this event is something the user would act on now, send a PushNotification.\n" +
            "</task-notification>");
        Assert.NotNull(n);
        Assert.True(n.IsEvent);
        Assert.Equal("event 1", n.EventText);
        Assert.Null(n.ToolUseId);
    }

    [Fact]
    public void Parse_AgentResult_TagsInsideResultAreIgnored()
    {
        var n = TaskNotification.Parse(
            "<task-notification>\n<task-id>a1</task-id>\n<tool-use-id>toolu_a</tool-use-id>\n<status>completed</status>\n" +
            "<summary>Agent \"x\" finished</summary>\n<result>quoted: <status>failed</status></result>\n</task-notification>");
        Assert.Equal("completed", n!.Status);
    }

    [Theory]
    [InlineData("Background command \"x\" completed (exit code 0)", 0)]
    [InlineData("Background command \"x\" failed with exit code 127", 127)]
    [InlineData("Monitor \"x\" stream ended", null)]
    public void ExitCodeOf_ReadsSummary(string summary, int? expected) =>
        Assert.Equal(expected, TaskNotification.ExitCodeOf(summary));

    [Fact]
    public void Parse_NotANotification_IsNull() => Assert.Null(TaskNotification.Parse("hello <task-id>x</task-id>"));

    [Fact]
    public void Stream_BackgroundShells_TrackRunEndAndExitCodes()
    {
        var (conv, maxRunning) = Replay("bg-shells.jsonl");
        Assert.Equal(2, maxRunning);
        Assert.Equal(0, conv.BackgroundTasks.RunningCount);

        var loop = conv.BackgroundTasks.Get("b7imn475i")!;
        Assert.Equal(BackgroundTaskKind.Shell, loop.Kind);
        Assert.Equal(BackgroundTaskStatus.Completed, loop.Status);
        Assert.Equal(0, loop.ExitCode);
        Assert.Equal("Loop counter with delays", loop.Description);
        Assert.EndsWith("b7imn475i.output", loop.OutputFile);

        var boom = conv.BackgroundTasks.Get("bbm65f6ep")!;
        Assert.Equal(BackgroundTaskStatus.Failed, boom.Status);
        Assert.Equal(3, boom.ExitCode);

        // Each launch card is linked to its task, and each end is one notice (never a user bubble).
        var launches = ToolCalls(conv).Where(p => p.BackgroundTaskId is not null).Select(p => p.BackgroundTaskId).ToList();
        Assert.Equal(["b7imn475i", "bbm65f6ep"], launches.Order());
        Assert.Equal(["bbm65f6ep", "b7imn475i"], conv.Items.OfType<TaskNoticeItem>().Select(n => n.Task.TaskId));
    }

    [Fact]
    public void Stream_Monitor_IsAMonitorNotAShell()
    {
        var (conv, maxRunning) = Replay("bg-monitor.jsonl");
        Assert.Equal(1, maxRunning);
        var monitor = Assert.Single(conv.BackgroundTasks.All);
        Assert.Equal(BackgroundTaskKind.Monitor, monitor.Kind);
        Assert.Equal(BackgroundTaskStatus.Completed, monitor.Status);
        Assert.False(monitor.Persistent);
        Assert.Equal("Monitor \"tick monitor\" stream ended", monitor.Summary);
        Assert.Equal(0, monitor.EventCount);   // stream-json doesn't echo the event records
    }

    [Theory]
    [InlineData("bg-taskstop.jsonl")]           // Claude called TaskStop
    [InlineData("bg-stop-task-control.jsonl")]  // Perch sent the stop_task control request
    public void Stream_StoppedShell_ReadsStopped(string fixture)
    {
        var (conv, _) = Replay(fixture);
        var task = Assert.Single(conv.BackgroundTasks.All);
        Assert.Equal(BackgroundTaskStatus.Stopped, task.Status);
        Assert.NotNull(task.EndedUtc);
        Assert.Single(conv.Items.OfType<TaskNoticeItem>());
    }

    [Fact]
    public void Stream_SubagentForegroundShell_IsNotBackgroundWork()
    {
        var (conv, _) = Replay("background-agent-after-result.jsonl");
        var agent = Assert.Single(conv.BackgroundTasks.All);   // not the sub-agent's foreground `sleep 15`
        Assert.Equal(BackgroundTaskKind.Agent, agent.Kind);
        Assert.Equal(BackgroundTaskStatus.Completed, agent.Status);
        Assert.Equal("a6aab7e6716844abb", Assert.Single(conv.Items.OfType<TaskNoticeItem>()).Task.TaskId);
    }

    [Fact]
    public void History_TaskNotifications_AreNoticesNotUserBubbles()
    {
        var conv = new SessionConversation();
        conv.LoadHistory(File.ReadLines(Fixture("transcripts", "bg-shells.jsonl")));

        Assert.DoesNotContain(conv.Items.OfType<UserMessageItem>(), u => u.Text.Contains("<task-notification>"));
        var notices = conv.Items.OfType<TaskNoticeItem>().ToList();
        Assert.Equal(2, notices.Count);
        Assert.Equal(3, conv.BackgroundTasks.Get("bbm65f6ep")!.ExitCode);
        Assert.Equal(BackgroundTaskStatus.Failed, conv.BackgroundTasks.Get("bbm65f6ep")!.Status);
        Assert.Equal(BackgroundTaskStatus.Completed, conv.BackgroundTasks.Get("b7imn475i")!.Status);
        Assert.All(ToolCalls(conv).Where(p => p.ToolName == "Bash"), p => Assert.NotNull(p.BackgroundTaskId));
    }

    [Fact]
    public void History_MonitorEvents_CountOnTheMonitor()
    {
        var conv = new SessionConversation();
        conv.LoadHistory(File.ReadLines(Fixture("transcripts", "bg-monitor.jsonl")));

        Assert.DoesNotContain(conv.Items.OfType<UserMessageItem>(), u => u.Text.Contains("<task-notification>"));
        var monitor = Assert.Single(conv.BackgroundTasks.All);
        Assert.Equal(BackgroundTaskKind.Monitor, monitor.Kind);
        Assert.Equal(3, monitor.EventCount);
        Assert.Equal("event 3", monitor.LastEvent);
        Assert.False(monitor.IsRunning);   // history: nothing from an earlier process still runs
        Assert.Equal(3, conv.Items.OfType<TaskNoticeItem>().Count(n => n.IsEvent));
    }

    [Fact]
    public void History_UnfinishedTask_IsStoppedWithTheSession()
    {
        var conv = new SessionConversation();
        conv.LoadHistory(File.ReadLines(Fixture("transcripts", "bg-stop-task.jsonl")));
        var task = Assert.Single(conv.BackgroundTasks.All);   // its notification was never delivered
        Assert.Equal(BackgroundTaskStatus.Stopped, task.Status);
    }

    [Fact]
    public void Tracker_RepeatNotification_NoticesOnce()
    {
        var tracker = new BackgroundTaskTracker();
        tracker.NoteToolUse("toolu_1", "Bash", "build");
        tracker.Launched("toolu_1", "b1", BackgroundTaskKind.Shell);
        var n = new TaskNotification("b1", "toolu_1", "completed", null, "Background command \"build\" completed (exit code 0)");
        Assert.NotNull(tracker.Notified(n));
        Assert.Null(tracker.Notified(n, delivered: true));   // the same end seen again (other feed)
        Assert.Equal("build", tracker.Get("b1")!.Description);
    }

    [Fact]
    public void Tracker_SessionEnded_StopsEverythingRunning()
    {
        var tracker = new BackgroundTaskTracker();
        tracker.Started("b1", "toolu_1", "local_bash", "dev server", isBackgrounded: true, ownedBySubagent: false);
        tracker.SessionEnded();
        Assert.Equal(BackgroundTaskStatus.Stopped, tracker.Get("b1")!.Status);
        Assert.Equal(0, tracker.RunningCount);
    }

    [Fact]
    public void Fold_RunningBackgroundTasks_FollowTheTranscriptAsItGrows()
    {
        var dir = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "perch-bg-" + Guid.NewGuid().ToString("N"))).FullName;
        try
        {
            var path = Path.Combine(dir, "t.jsonl");
            var lines = File.ReadAllLines(Fixture("transcripts", "bg-shells.jsonl"));
            int launched = Array.FindLastIndex(lines, l => l.Contains("bbm65f6ep") && l.Contains("backgroundTaskId"));
            int firstEnd = Array.FindIndex(lines, l => l.StartsWith("{\"type\":\"user\"") && l.Contains("<status>failed"));
            Assert.True(launched >= 0 && firstEnd > launched);

            var reader = new TranscriptReader();
            File.WriteAllLines(path, lines[..(launched + 1)]);
            var running = reader.RunningBackgroundTasksAt(path);
            Assert.Equal(["b7imn475i", "bbm65f6ep"], running.Select(t => t.TaskId).Order());
            Assert.All(running, t => Assert.Equal(BackgroundTaskKind.Shell, t.Kind));
            Assert.EndsWith("bbm65f6ep.output", running.Single(t => t.TaskId == "bbm65f6ep").OutputFile);

            File.AppendAllLines(path, lines[(launched + 1)..(firstEnd + 1)]);
            Assert.Equal("b7imn475i", Assert.Single(reader.RunningBackgroundTasksAt(path)).TaskId);

            File.AppendAllLines(path, lines[(firstEnd + 1)..]);
            Assert.Empty(reader.RunningBackgroundTasksAt(path));
        }
        finally { try { Directory.Delete(dir, recursive: true); } catch { } }
    }

    [Fact]
    public void Fold_MonitorEvents_DontEndTheMonitor_AndCleanExitClearsAll()
    {
        var dir = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "perch-bg-" + Guid.NewGuid().ToString("N"))).FullName;
        try
        {
            var path = Path.Combine(dir, "t.jsonl");
            var lines = File.ReadAllLines(Fixture("transcripts", "bg-monitor.jsonl"));
            int lastEvent = Array.FindLastIndex(lines, l => l.Contains("<event>event 3"));
            var reader = new TranscriptReader();
            File.WriteAllLines(path, lines[..(lastEvent + 1)]);
            var monitor = Assert.Single(reader.RunningBackgroundTasksAt(path));
            Assert.Equal(BackgroundTaskKind.Monitor, monitor.Kind);

            File.WriteAllLines(path, lines[..(lastEvent + 1)].Append("{\"type\":\"cost-state\",\"startTime\":0}"));
            Assert.Empty(reader.RunningBackgroundTasksAt(path));
        }
        finally { try { Directory.Delete(dir, recursive: true); } catch { } }
    }

    [Fact]
    public void Stream_ShellLaunch_KnowsItsOutputFileBeforeItEnds()
    {
        var conv = new SessionConversation();
        foreach (var line in File.ReadLines(Fixture("stream-json", "bg-stop-task-control.jsonl")))
        {
            if (line.Contains("\"perch\"")) continue;
            foreach (var ev in StreamJsonParser.Parse(line)) conv.Apply(ev);
            if (conv.BackgroundTasks.Get("bupmsikht") is { IsRunning: true, OutputFile: { } f })
            {
                Assert.Equal(@"C:\fixtures\tmp\tasks\bupmsikht.output", f);
                return;
            }
        }
        Assert.Fail("the running shell never learned its output file");
    }

    [Fact]
    public void Output_ReadTail_StripsTrailerAndReadsExitCode()
    {
        var path = Path.Combine(Path.GetTempPath(), "perch-bgout-" + Guid.NewGuid().ToString("N") + ".output");
        try
        {
            File.WriteAllText(path, "tick 1\r\ntick 2\n");
            Assert.Equal(("tick 1\ntick 2", (int?)null), BackgroundTaskOutput.ReadTail(path));
            File.AppendAllText(path, "\n[exited with code 3]\n");
            Assert.Equal(("tick 1\ntick 2", (int?)3), BackgroundTaskOutput.ReadTail(path));
            // A long file is cut at a line start, never mid-line.
            File.WriteAllText(path, string.Concat(Enumerable.Range(0, 2000).Select(i => $"line {i}\n")));
            var (text, _) = BackgroundTaskOutput.ReadTail(path, 100);
            Assert.StartsWith("line ", text);
            Assert.EndsWith("line 1999", text);
        }
        finally { File.Delete(path); }
        Assert.Equal(("", (int?)null), BackgroundTaskOutput.ReadTail(path));   // gone: no throw
    }

    [Fact]
    public void Output_PathFor_PrefersNamedThenSiblingDirectory()
    {
        var named = new BackgroundTask("b1") { OutputFile = @"C:\t\tasks\b1.output" };
        var monitor = new BackgroundTask("b2");
        Assert.Equal(@"C:\t\tasks\b1.output", BackgroundTaskOutput.PathFor(named, [], null, null));
        Assert.Equal(Path.Combine(@"C:\t\tasks", "b2.output"), BackgroundTaskOutput.PathFor(monitor, [named], null, null));
        Assert.EndsWith(Path.Combine("claude", "C--p", "sid", "tasks", "b2.output"),
            BackgroundTaskOutput.PathFor(monitor, [], @"C:\p", "sid"));
    }

    [Fact]
    public void Text_StatusAndNotice_ReadNaturally()
    {
        var start = new DateTime(2026, 10, 7, 4, 0, 0, DateTimeKind.Utc);
        var shell = new BackgroundTask("b1") { Description = "dev server", StartedUtc = start };
        Assert.Equal("background · 2m 14s", BackgroundTaskText.Status(shell, start.AddSeconds(134)));
        var monitor = new BackgroundTask("b2") { Kind = BackgroundTaskKind.Monitor, StartedUtc = start, EventCount = 3 };
        Assert.Equal("watching · 3 events · 9s", BackgroundTaskText.Status(monitor, start.AddSeconds(9)));

        shell.Status = BackgroundTaskStatus.Failed;
        shell.ExitCode = 3;
        Assert.Equal("exit 3", BackgroundTaskText.Status(shell, start));
        Assert.Equal("Background command \"dev server\" failed", BackgroundTaskText.Notice(shell, null));
        shell.Summary = "Background command \"dev server\" failed with exit code 3";
        Assert.Equal(shell.Summary, BackgroundTaskText.Notice(shell, null));
        monitor.Description = "tick";
        Assert.Equal("Monitor \"tick\": event 1 …", BackgroundTaskText.Notice(monitor, "event 1\nevent 2"));
        Assert.Equal("1h 05m", BackgroundTaskText.Elapsed(TimeSpan.FromMinutes(65)));
    }

    private static string Fixture(string dir, string name) =>
        Path.Combine(AppContext.BaseDirectory, "fixtures", dir, name);

    private static IEnumerable<ToolCallPart> ToolCalls(SessionConversation conv) =>
        conv.Items.OfType<AssistantMessageItem>().SelectMany(a => a.Parts.OfType<ToolCallPart>());

    // Replays a stream-json capture the way the controller's pump would, tracking the peak running count.
    private static (SessionConversation Conv, int MaxRunning) Replay(string fixture)
    {
        var conv = new SessionConversation();
        int max = 0;
        foreach (var line in File.ReadLines(Fixture("stream-json", fixture)))
        {
            var node = JsonNode.Parse(line)!;
            if (node["perch"] is { } marker)
            {
                if (marker.GetValue<string>() == "prompt") conv.AddUserPrompt(node["text"]!.GetValue<string>());
                continue;
            }
            foreach (var ev in StreamJsonParser.Parse(line)) conv.Apply(ev);
            max = Math.Max(max, conv.BackgroundTasks.RunningCount);
        }
        return (conv, max);
    }
}
