using Perch.Data.Control;
using Xunit;

namespace Perch.Tests;

/// <summary>
/// The message/turn model behind the rich session UI (docs/session-ui-plan.md, Phase 1): how the flat
/// stream-json events group into user / assistant / permission items, how streaming text finalises, and
/// the turn/queue/permission bookkeeping the header shows.
/// </summary>
public class SessionConversationTests
{
    private static (SessionConversation Conv, List<(ConversationItem Item, ConversationChange Change)> Log) Make()
    {
        var conv = new SessionConversation();
        var log = new List<(ConversationItem, ConversationChange)>();
        conv.Changed += (item, change) => log.Add((item, change));
        return (conv, log);
    }

    [Fact]
    public void Init_SeedsIdentity()
    {
        var (conv, _) = Make();
        conv.Apply(new SessionInitEvent("sid-1", "claude-opus-5", "acceptEdits", 16, ["/model", "/compact"]));
        Assert.Equal("sid-1", conv.SessionId);
        Assert.Equal("claude-opus-5", conv.Model);
        Assert.Equal("acceptEdits", conv.PermissionMode);
        Assert.Equal(16, conv.ToolCount);
        Assert.Equal(new[] { "/model", "/compact" }, conv.SlashCommands);
        Assert.Empty(conv.Items);
    }

    [Fact]
    public void SecondInitWithNewId_ClearsConversation()
    {
        var (conv, _) = Make();
        // A live conversation under the first id: a turn with prose, plus a result seeding context/cost.
        conv.Apply(new SessionInitEvent("sid-1", "claude-opus-5", "default", 16));
        conv.AddUserPrompt("remember BANANA");
        conv.Apply(new AssistantTextEvent("OK"));
        conv.Apply(new TurnResultEvent(false, "success", 0.12, InputTokens: 5000, OutputTokens: 20, DurationMs: 900));
        Assert.NotEmpty(conv.Items);
        Assert.True(conv.ContextTokens > 0);

        bool reset = false;
        conv.Reset += () => reset = true;

        // `/clear` re-bases the session onto a new id with wiped context (a fresh init).
        conv.Apply(new SessionInitEvent("sid-2", "claude-opus-5", "default", 16));

        Assert.True(reset);
        Assert.Equal("sid-2", conv.SessionId);
        var note = Assert.IsType<NoteItem>(Assert.Single(conv.Items));   // only the marker remains
        Assert.Equal("conversation cleared", note.Text);
        Assert.Equal(0, conv.ContextTokens);
        Assert.False(conv.TurnActive);
        Assert.Null(conv.LastTurn);
        Assert.Equal(0.12, conv.TotalCostUsd);   // cumulative process spend is kept
    }

    [Fact]
    public void FirstInit_MatchingSeededId_DoesNotClear()
    {
        var (conv, _) = Make();
        conv.SetSessionId("sid-1");            // seeded at launch, before init
        conv.AddUserPrompt("hi");
        conv.Apply(new SessionInitEvent("sid-1", "claude-opus-5", "default", 16));   // same id → not a clear
        Assert.IsType<UserMessageItem>(Assert.Single(conv.Items));   // the user prompt survives
    }

    [Fact]
    public void Compact_ShowsAProgressRowThenFinalises()
    {
        var (conv, _) = Make();
        // Seed some context so the freed-tokens figure is meaningful.
        conv.Apply(new TurnResultEvent(false, "success", 0.05, InputTokens: 60_000, OutputTokens: 10, DurationMs: 900));

        conv.AddUserPrompt("/compact keep the failing test details");
        Assert.Equal("/compact keep the failing test details", Assert.IsType<UserMessageItem>(conv.Items[^2]).Text);
        var progress = Assert.IsType<CompactionItem>(conv.Items[^1]);
        Assert.Equal("keep the failing test details", progress.Instructions);
        Assert.Null(progress.Percent);       // indeterminate until the CLI reports one
        Assert.False(progress.IsDone);

        // A status record advances the meter.
        conv.Apply(new StatusEvent(42, "Compacting conversation…"));
        Assert.Equal(42, progress.Percent);

        // The compaction turn's result finalises the row and reports what it reclaimed.
        conv.Apply(new TurnResultEvent(false, "success", 0.06, InputTokens: 8_000, OutputTokens: 5, DurationMs: 1200));
        Assert.True(progress.IsDone);
        Assert.Equal(100, progress.Percent);
        Assert.Equal(52_000, progress.FreedTokens);   // 60k before − 8k after
        Assert.Equal(8_000, conv.ContextTokens);
        Assert.False(conv.TurnActive);
    }

    [Fact]
    public void Compact_CompactBoundary_FinalisesAsSuccessWithExactFreedTokens()
    {
        var (conv, _) = Make();
        conv.Apply(new TurnResultEvent(false, "success", 0.05, InputTokens: 8_281, OutputTokens: 10, DurationMs: 900));
        conv.AddUserPrompt("/compact");
        var progress = Assert.IsType<CompactionItem>(conv.Items[^1]);

        // The authoritative success signal (only emitted on a real compaction) carries pre/post tokens.
        conv.Apply(new CompactionCompletedEvent(PreTokens: 8_281, PostTokens: 5_350, Trigger: "manual"));

        Assert.True(progress.IsDone);
        Assert.False(progress.Failed);                 // a real success, not the false failure we shipped
        Assert.Equal(100, progress.Percent);
        Assert.Equal(2_931, progress.FreedTokens);      // 8281 − 5350
        Assert.Equal(5_350, conv.ContextTokens);        // context corrected to the post size
        Assert.False(conv.TurnActive);                  // the boundary settles the turn (no phantom "working")

        // The compact turn's own result then lands: it must NOT re-open/re-fail the settled row, must keep the
        // turn settled, and its (large summarisation) input must NOT clobber the corrected occupancy.
        conv.Apply(new TurnResultEvent(false, "success", 0.06, InputTokens: 90_000, OutputTokens: 3, DurationMs: 100));
        Assert.False(progress.Failed);
        Assert.False(conv.TurnActive);
        Assert.Equal(5_350, conv.ContextTokens);        // suppressed — still the post size, not 90k
    }

    [Fact]
    public void Compact_AfterBoundary_NextPromptRunsInsteadOfQueuing()
    {
        var (conv, _) = Make();
        conv.Apply(new TurnResultEvent(false, "success", 0.05, InputTokens: 8_281, OutputTokens: 10, DurationMs: 900));
        conv.AddUserPrompt("/compact");
        conv.Apply(new CompactionCompletedEvent(PreTokens: 8_281, PostTokens: 5_350, Trigger: "manual"));

        // With the turn settled at the boundary, a follow-up prompt starts its own turn (not queued behind a
        // phantom "working" compaction) — so its result will refresh the composer's context/token pills.
        conv.AddUserPrompt("now what?");
        Assert.True(conv.TurnActive);
        Assert.Equal(0, conv.QueuedPrompts);
    }

    [Fact]
    public void Compact_Interrupted_SettlesToCanceledNotSuccess()
    {
        var (conv, _) = Make();
        conv.Apply(new TurnResultEvent(false, "success", 0.05, InputTokens: 60_000, OutputTokens: 10, DurationMs: 900));
        conv.AddUserPrompt("/compact");
        var progress = Assert.IsType<CompactionItem>(conv.Items[^1]);

        // The user interrupts; the aborted turn returns with the context unchanged (nothing reclaimed).
        conv.NoteInterrupt();
        conv.Apply(new TurnResultEvent(false, "success", 0.05, InputTokens: 60_000, OutputTokens: 2, DurationMs: 500));

        Assert.True(progress.IsDone);
        Assert.True(progress.Failed);          // canceled, not a green success
        Assert.NotEqual(100, progress.Percent ?? 0);
        Assert.Equal(0, progress.FreedTokens);
    }

    [Fact]
    public void Status_WithoutAnActiveCompaction_IsIgnored()
    {
        var (conv, _) = Make();
        conv.Apply(new StatusEvent(30, "some status"));
        Assert.Empty(conv.Items);   // a stray status record adds nothing
    }

    [Fact]
    public void OrdinaryPrompt_DropsNoCompactionMarker()
    {
        var (conv, _) = Make();
        conv.AddUserPrompt("please compact the layout");   // not a /compact command
        Assert.IsType<UserMessageItem>(Assert.Single(conv.Items));
    }

    [Fact]
    public void StreamingDeltas_AccumulateThenFinalise()
    {
        var (conv, log) = Make();
        conv.AddUserPrompt("hi");
        conv.Apply(new TextDeltaEvent("Hel"));
        conv.Apply(new TextDeltaEvent("lo"));

        var assistant = Assert.IsType<AssistantMessageItem>(conv.Items[1]);
        var text = Assert.IsType<TextPart>(Assert.Single(assistant.Parts));
        Assert.Equal("Hello", text.Text);
        Assert.True(text.IsStreaming);

        conv.Apply(new AssistantTextEvent("Hello **world**"));
        Assert.Single(assistant.Parts);   // finalised in place, not appended
        Assert.Equal("Hello **world**", text.Text);
        Assert.False(text.IsStreaming);

        // One Added for the assistant item, then Updated per delta/final.
        Assert.Equal(ConversationChange.Added, log.First(e => ReferenceEquals(e.Item, assistant)).Change);
        Assert.Equal(3, log.Count(e => ReferenceEquals(e.Item, assistant) && e.Change == ConversationChange.Updated));
    }

    [Fact]
    public void ThinkingToolsAndText_StayOrderedInOneAssistantItem()
    {
        var (conv, _) = Make();
        conv.AddUserPrompt("fix it");
        conv.Apply(new AssistantThinkingEvent("thinking…"));
        conv.Apply(new ToolUseEvent("t1", "Read", "Reading Foo.cs", "{\"file_path\":\"Foo.cs\"}"));
        conv.Apply(new ToolResultEvent("t1", "class Foo {}", false));
        conv.Apply(new AssistantTextEvent("Done."));
        conv.Apply(new TurnResultEvent(false, "success", 0.02, 10, 20, 900));

        Assert.Equal(2, conv.Items.Count);
        var a = Assert.IsType<AssistantMessageItem>(conv.Items[1]);
        Assert.Collection(a.Parts,
            p => Assert.Equal("thinking…", Assert.IsType<ThinkingPart>(p).Text),
            p =>
            {
                var tool = Assert.IsType<ToolCallPart>(p);
                Assert.Equal("Read", tool.ToolName);
                Assert.Equal(ToolCallStatus.Done, tool.Status);
                Assert.Equal("class Foo {}", tool.ResultPreview);
                Assert.Contains("Foo.cs", tool.InputJson);
            },
            p => Assert.Equal("Done.", Assert.IsType<TextPart>(p).Text));
        Assert.True(a.IsComplete);
        Assert.Equal(0.02, a.Result!.CostUsd);
        Assert.Equal(0.02, conv.TotalCostUsd);
        Assert.False(conv.TurnActive);
    }

    [Fact]
    public void ToolError_MarksFailed()
    {
        var (conv, _) = Make();
        conv.Apply(new ToolUseEvent("t1", "Bash", "Running: false", "{}"));
        conv.Apply(new ToolResultEvent("t1", "exit 1", true));
        var tool = Assert.IsType<ToolCallPart>(Assert.IsType<AssistantMessageItem>(conv.Items[0]).Parts[0]);
        Assert.Equal(ToolCallStatus.Failed, tool.Status);
        Assert.Equal("exit 1", tool.ResultPreview);
    }

    [Fact]
    public void Permission_IsItsOwnItem_AndSplitsTheAssistantTurn()
    {
        var (conv, _) = Make();
        conv.AddUserPrompt("commit");
        conv.Apply(new ToolUseEvent("t1", "Bash", "Running: git commit", "{}"));
        var request = new PermissionRequestEvent("req1", "Bash", "git commit -am x", "{\"command\":\"git commit\"}", "acceptEdits");
        conv.Apply(request);

        var perm = Assert.IsType<PermissionItem>(conv.Items[2]);
        Assert.Same(perm, conv.PendingPermission);
        Assert.Equal(PermissionResolution.Pending, perm.Resolution);

        conv.ResolvePermission(perm, allowed: true, switchedMode: "acceptEdits");
        Assert.Null(conv.PendingPermission);
        Assert.Equal(PermissionResolution.Allowed, perm.Resolution);
        Assert.Equal("acceptEdits", perm.SwitchedMode);

        // The tool result still lands on the ORIGINAL tool card, even though later prose opens a new item.
        conv.Apply(new ToolResultEvent("t1", "[main abc123] x", false));
        conv.Apply(new AssistantTextEvent("Committed."));
        var first = Assert.IsType<AssistantMessageItem>(conv.Items[1]);
        Assert.Equal(ToolCallStatus.Done, Assert.IsType<ToolCallPart>(first.Parts[0]).Status);
        var second = Assert.IsType<AssistantMessageItem>(conv.Items[3]);
        Assert.Equal("Committed.", Assert.IsType<TextPart>(second.Parts[0]).Text);

        // The turn result closes both open assistant items.
        conv.Apply(new TurnResultEvent(false, "success", 0.05, 1, 2, 3));
        Assert.True(first.IsComplete);
        Assert.True(second.IsComplete);
    }

    [Fact]
    public void QueuedPrompts_DrainOnePerTurnResult()
    {
        var (conv, _) = Make();
        conv.AddUserPrompt("one");
        conv.AddUserPrompt("two");
        conv.AddUserPrompt("three");
        Assert.True(conv.TurnActive);
        Assert.Equal(2, conv.QueuedPrompts);

        conv.Apply(new TurnResultEvent(false, "success", 0.01, 0, 0, 0));
        Assert.True(conv.TurnActive);
        Assert.Equal(1, conv.QueuedPrompts);
        conv.Apply(new TurnResultEvent(false, "success", 0.02, 0, 0, 0));
        conv.Apply(new TurnResultEvent(false, "success", 0.03, 0, 0, 0));
        Assert.False(conv.TurnActive);
        Assert.Equal(0, conv.QueuedPrompts);
        Assert.Equal(0.03, conv.TotalCostUsd);
    }

    [Fact]
    public void ModeChange_NotesOnlyRealChanges()
    {
        var (conv, _) = Make();
        conv.Apply(new SessionInitEvent("s", "m", "default", 1));
        conv.Apply(new ModeChangedEvent("default"));
        Assert.Empty(conv.Items);
        conv.Apply(new ModeChangedEvent("plan"));
        var note = Assert.IsType<NoteItem>(Assert.Single(conv.Items));
        Assert.Contains("plan", note.Text);
        Assert.Equal("plan", conv.PermissionMode);
    }

    [Fact]
    public void LoadHistory_ReplaysTranscriptInFrontOfLiveItems()
    {
        var (conv, _) = Make();
        int resets = 0;
        conv.Reset += () => resets++;
        conv.AddNote("resuming session abc…");   // the live item that exists before history lands

        var lines = new[]
        {
            """{"type":"user","message":{"role":"user","content":"<command-name>/clear</command-name>"}}""",
            """{"type":"user","message":{"role":"user","content":"fix the rounding bug"},"timestamp":"2026-09-01T10:00:00Z"}""",
            """{"type":"assistant","message":{"role":"assistant","content":[{"type":"thinking","thinking":"hmm"},{"type":"tool_use","id":"t1","name":"Read","input":{"file_path":"a.cs"}}]}}""",
            """{"type":"user","message":{"role":"user","content":[{"type":"tool_result","tool_use_id":"t1","content":"class A {}"}]}}""",
            """{"type":"assistant","message":{"role":"assistant","content":[{"type":"text","text":"Fixed."}]}}""",
            """{"type":"user","isSidechain":true,"message":{"role":"user","content":"sub-agent chatter"}}""",
            """{"type":"user","message":{"role":"user","content":[{"type":"text","text":"thanks"}]}}""",
            "not json",
        };
        Assert.Equal(3, conv.LoadHistory(lines));
        Assert.Equal(1, resets);

        Assert.Equal(4, conv.Items.Count);
        Assert.Equal("fix the rounding bug", Assert.IsType<UserMessageItem>(conv.Items[0]).Text);
        var a = Assert.IsType<AssistantMessageItem>(conv.Items[1]);
        Assert.True(a.IsComplete);
        Assert.Collection(a.Parts,
            p => Assert.IsType<ThinkingPart>(p),
            p =>
            {
                var t = Assert.IsType<ToolCallPart>(p);
                Assert.Equal(ToolCallStatus.Done, t.Status);
                Assert.Equal("class A {}", t.ResultPreview);
            },
            p => Assert.Equal("Fixed.", Assert.IsType<TextPart>(p).Text));
        Assert.Equal("thanks", Assert.IsType<UserMessageItem>(conv.Items[2]).Text);
        Assert.IsType<NoteItem>(conv.Items[3]);   // the live note stays after the history
        Assert.False(conv.TurnActive);            // history never counts as a running turn
    }

    [Fact]
    public void SessionEnded_SettlesEverythingInFlight()
    {
        var (conv, _) = Make();
        conv.AddUserPrompt("go");
        conv.Apply(new TextDeltaEvent("partial"));
        conv.Apply(new ToolUseEvent("t1", "Bash", "Running: sleep", "{}"));
        conv.Apply(new PermissionRequestEvent("r", "Bash", "sleep", "{}", null));
        conv.SessionEnded(1, "boom");

        var a = Assert.IsType<AssistantMessageItem>(conv.Items[1]);
        Assert.True(a.IsComplete);
        Assert.False(Assert.IsType<TextPart>(a.Parts[0]).IsStreaming);
        Assert.Equal(ToolCallStatus.Failed, Assert.IsType<ToolCallPart>(a.Parts[1]).Status);
        Assert.Equal(PermissionResolution.Expired, Assert.IsType<PermissionItem>(conv.Items[2]).Resolution);
        Assert.Null(conv.PendingPermission);
        Assert.False(conv.TurnActive);
        var note = Assert.IsType<NoteItem>(conv.Items[^1]);
        Assert.Equal(NoteKind.Error, note.Kind);
        Assert.Contains("boom", note.Text);
    }
}
