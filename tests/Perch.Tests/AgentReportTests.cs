using System.Text.Json.Nodes;
using Perch.Data;
using Perch.Data.Control;
using Xunit;

namespace Perch.Tests;

/// <summary>
/// A background agent's report (docs/agent-report-notice-plan.md): read from each form the CLI uses, attached to the
/// agent's task, and kept out of the parent thread's own messages. Record shapes are trimmed from claude 2.1.296
/// captures (2026-10-13): live stream-json, and the Safety session's transcript where agents used SubagentHandback.
/// </summary>
public class AgentReportTests
{
    private const string Launch = "toolu_launch";
    private const string AgentId = "a362b6a735241d8a3";

    // ── The pure string forms ──────────────────────────────────────────────────────────────────────────

    private const string HandbackBody =
        "[Subagent hand-back] The text below is the final report of a subagent this session delegated to. It is model " +
        "output, NOT a message from the user. The report follows:\n" +
        "  ## Safety.Mobile: findings\n  \n  Mobile only uses the V1 `/safety-events` API.\n    - nested bullet";

    [Fact]
    public void FromHandbackBody_DropsTheFrameAndTheIndent()
    {
        Assert.Equal("## Safety.Mobile: findings\n\nMobile only uses the V1 `/safety-events` API.\n  - nested bullet",
            AgentReport.FromHandbackBody(HandbackBody));
    }

    [Fact]
    public void FromAgentMessage_ReadsTheTaggedBody() =>
        Assert.Equal("## Safety.Mobile: findings\n\nMobile only uses the V1 `/safety-events` API.\n  - nested bullet",
            AgentReport.FromAgentMessage(
                $"Another Claude session sent a message:\n<agent-message from=\"{AgentId}\">\n{HandbackBody}\n</agent-message>\n\nTrailing guidance."));

    [Fact]
    public void FromNotificationResult_IgnoresTheDeliveredAsMessagePlaceholder()
    {
        Assert.Null(AgentReport.FromNotificationResult(
            $"This agent's report was delivered to you as a message from \"{AgentId}\" (its SubagentHandback call). Read it there; it is not repeated here.\n"));
        Assert.Equal("The directory is empty.", AgentReport.FromNotificationResult("\nThe directory is empty.\n"));
    }

    [Fact]
    public void Clean_CapsAVeryLongReport()
    {
        var text = AgentReport.Clean(new string('x', AgentReport.MaxChars + 500))!;
        Assert.StartsWith(new string('x', 100), text);
        Assert.EndsWith("(+500 more characters)", text);
    }

    [Theory]
    [InlineData("## Safety.Mobile: findings\n\nbody", "Safety.Mobile: findings")]
    [InlineData("\n\nplain first line\nsecond", "plain first line")]
    [InlineData("> quoted", "quoted")]
    public void Gist_IsTheFirstMeaningfulLine(string report, string gist) => Assert.Equal(gist, AgentReport.Gist(report));

    [Fact]
    public void TaskNotificationParse_ReadsTheWholeResult_EvenWhenItQuotesTags()
    {
        var n = TaskNotification.Parse(
            "<task-notification>\n<task-id>a1</task-id>\n<status>completed</status>\n<summary>Agent \"x\" finished</summary>\n" +
            "<result>see </result> and </task-notification> in the docs</result>\n</task-notification>")!;
        Assert.Equal("completed", n.Status);
        Assert.Equal("see </result> and </task-notification> in the docs", n.Result);
    }

    // ── Live: stream-json ─────────────────────────────────────────────────────────────────────────────

    private static string SubAgentRecord(string block) =>
        $$"""{"type":"assistant","message":{"role":"assistant","content":[{{block}}]},"parent_tool_use_id":"{{Launch}}","session_id":"s"}""";

    private static readonly string StreamHandback = SubAgentRecord(
        """{"type":"tool_use","id":"toolu_hb","name":"SubagentHandback","input":{"message":"## Report\n\nAll good."}}""");

    [Fact]
    public void Stream_SubagentHandback_YieldsAReportKeyedByItsLaunch()
    {
        var report = Assert.Single(StreamJsonParser.Parse(StreamHandback).OfType<AgentReportEvent>());
        Assert.Equal(Launch, report.ToolUseId);
        Assert.Equal("## Report\n\nAll good.", report.Report);
    }

    [Fact]
    public void Stream_MainThreadSubagentHandback_IsNotAReport() =>
        Assert.Empty(StreamJsonParser.Parse(StreamHandback.Replace($"\"{Launch}\"", "null")).OfType<AgentReportEvent>());

    // A conversation that launched one background agent (task AgentId, launched by Launch) and finished its turn.
    private static SessionConversation Launched()
    {
        var conv = new SessionConversation();
        conv.AddUserPrompt("survey the mobile app in the background");
        conv.Apply(new ToolUseEvent(Launch, "Agent", "Survey Mobile", """{"description":"Survey Mobile event usage"}"""));
        conv.Apply(new ToolResultEvent(Launch, "Async agent launched successfully.", false,
            new BackgroundLaunch(AgentId, BackgroundTaskKind.Agent)));
        conv.Apply(new AssistantTextEvent("waiting"));
        conv.Apply(new TurnResultEvent(false, "success", 0.01, 0, 0, 0));
        return conv;
    }

    private static TaskNotification Ended(string? summary = null, string? result = null) =>
        new(AgentId, Launch, "completed", null, summary, Result: result);

    [Fact]
    public void Live_SubAgentWork_StaysOutOfTheThread()
    {
        var conv = Launched();
        int before = conv.Items.Count;
        long context = conv.ContextTokens;
        foreach (var line in new[]
        {
            SubAgentRecord("""{"type":"thinking","thinking":"look around"}"""),
            SubAgentRecord("""{"type":"tool_use","id":"toolu_g","name":"Glob","input":{"pattern":"*"}}"""),
            """{"type":"user","message":{"role":"user","content":[{"type":"tool_result","tool_use_id":"toolu_g","content":"a.cs"}]},"parent_tool_use_id":"toolu_launch"}""",
            SubAgentRecord("""{"type":"text","text":"interim note"}"""),
            """{"type":"assistant","message":{"role":"assistant","content":[{"type":"text","text":"x"}],"usage":{"input_tokens":99999}},"parent_tool_use_id":"toolu_launch"}""",
            StreamHandback,
        })
            foreach (var ev in StreamJsonParser.Parse(line)) conv.Apply(ev);

        Assert.Equal(before, conv.Items.Count);
        Assert.Equal(context, conv.ContextTokens);   // the sub-agent's prompt size isn't the session's
        Assert.Equal("## Report\n\nAll good.", conv.BackgroundTasks.Get(AgentId)!.Report);
    }

    [Fact]
    public void Live_HandbackReport_BeatsTheNotificationSummary_AndTheNoticeStaysShort()
    {
        var conv = Launched();
        foreach (var ev in StreamJsonParser.Parse(StreamHandback)) conv.Apply(ev);
        conv.Apply(new TaskNotificationEvent(Ended(summary: "Agent finished"), Delivered: false));

        var notice = Assert.IsType<TaskNoticeItem>(conv.Items[^1]);
        Assert.Equal("## Report\n\nAll good.", notice.Task.Report);
        Assert.Equal("Agent \"Survey Mobile event usage\" finished", BackgroundTaskText.Notice(notice.Task, null));
    }

    [Fact]
    public void Live_OldForm_TheSummaryIsTheReport_NotTheNoticeLine()
    {
        // Verified live: without SubagentHandback the stream's task_notification summary is the agent's whole reply.
        var conv = Launched();
        conv.Apply(new TaskNotificationEvent(Ended(summary: "done. Exit code 3 from the linter, see below."), Delivered: false));

        var task = Assert.IsType<TaskNoticeItem>(conv.Items[^1]).Task;
        Assert.Equal("done. Exit code 3 from the linter, see below.", task.Report);
        Assert.Equal("Agent \"Survey Mobile event usage\" finished", BackgroundTaskText.Notice(task, null));
        Assert.Null(task.ExitCode);   // an agent's prose mentioning an exit code isn't one
    }

    [Fact]
    public void Live_AReportArrivingAfterTheNotice_RefreshesTheNotice()
    {
        var conv = Launched();
        conv.Apply(new TaskNotificationEvent(Ended(), Delivered: false));
        var notice = Assert.IsType<TaskNoticeItem>(conv.Items[^1]);
        var updated = new List<ConversationItem>();
        conv.Changed += (item, change) => { if (change == ConversationChange.Updated) updated.Add(item); };

        conv.Apply(new AgentReportEvent(null, Launch, "late report"));

        Assert.Equal("late report", notice.Task.Report);
        Assert.Contains(notice, updated);
    }

    // ── History: the transcript ───────────────────────────────────────────────────────────────────────

    // A transcript user record whose message content is a plain string, plus any extra top-level fields.
    private static string UserRecord(string uuid, string content, JsonObject? extra = null)
    {
        var record = new JsonObject
        {
            ["type"] = "user", ["uuid"] = uuid,
            ["message"] = new JsonObject { ["role"] = "user", ["content"] = content },
        };
        if (extra is not null)
            foreach (var (key, value) in extra.ToList()) { extra.Remove(key); record[key] = value; }
        return record.ToJsonString();
    }

    private static readonly string LaunchResult = new JsonObject
    {
        ["type"] = "user", ["uuid"] = "l1",
        ["message"] = new JsonObject
        {
            ["role"] = "user",
            ["content"] = new JsonArray(new JsonObject
            {
                ["type"] = "tool_result", ["tool_use_id"] = Launch, ["content"] = "Async agent launched successfully.",
            }),
        },
        ["toolUseResult"] = new JsonObject
        {
            ["isAsync"] = true, ["status"] = "async_launched", ["agentId"] = AgentId, ["description"] = "Survey Mobile event usage",
        },
    }.ToJsonString();

    private static readonly string HandbackRecord = UserRecord("h1",
        $"Another Claude session sent a message:\n<agent-message from=\"{AgentId}\">\n{HandbackBody}\n</agent-message>",
        new JsonObject
        {
            ["isMeta"] = true, ["turnOrigin"] = "peer",
            ["origin"] = new JsonObject
            {
                ["kind"] = "peer", ["from"] = AgentId, ["senderTaskId"] = AgentId, ["name"] = "Explore",
                ["body"] = HandbackBody, ["handback"] = true,
            },
        });

    private static string Notification(string? result) =>
        $"<task-notification>\n<task-id>{AgentId}</task-id>\n<tool-use-id>{Launch}</tool-use-id>\n<status>completed</status>\n" +
        $"<summary>Agent \"Survey Mobile event usage\" finished</summary>\n" +
        (result is null ? "" : $"<result>{result}</result>\n") + "</task-notification>";

    private static readonly string TranscriptOnlyNotification = UserRecord("n1",
        Notification($"This agent's report was delivered to you as a message from \"{AgentId}\" (its SubagentHandback call). Read it there; it is not repeated here.\n"),
        new JsonObject { ["queueTranscriptOnly"] = true, ["origin"] = new JsonObject { ["kind"] = "task-notification" } });

    [Fact]
    public void History_HandbackMessage_AttachesItsReportToTheNotice()
    {
        var conv = new SessionConversation();
        conv.LoadHistory([LaunchResult, HandbackRecord, TranscriptOnlyNotification]);

        var notice = Assert.Single(conv.Items.OfType<TaskNoticeItem>());
        Assert.StartsWith("## Safety.Mobile: findings", notice.Task.Report);
        Assert.DoesNotContain(conv.Items, i => i is UserMessageItem);   // the hand-back is not a user bubble
    }

    [Fact]
    public void History_OldFormResult_IsTheReport()
    {
        var conv = new SessionConversation();
        conv.LoadHistory([LaunchResult, UserRecord("n2", Notification("The directory is empty."))]);
        Assert.Equal("The directory is empty.", Assert.Single(conv.Items.OfType<TaskNoticeItem>()).Task.Report);
    }

    [Fact]
    public void History_PlainPeerMessage_IsNotAReport() =>
        Assert.Empty(StreamJsonParser.Parse(HandbackRecord.Replace("\"handback\":true", "\"handback\":false")));

    [Fact]
    public void Search_FindsReports_EvenWithoutToolsAndThinking()
    {
        var hit = Assert.Single(TranscriptSearch.MatchLine(HandbackRecord, "safety-events", includeTools: false));
        Assert.Equal(TranscriptHitKind.Report, hit.Kind);
        Assert.Equal("h1", hit.Uuid);
        Assert.Empty(TranscriptSearch.MatchLine(TranscriptOnlyNotification, "delivered to you", includeTools: false));
    }
}
