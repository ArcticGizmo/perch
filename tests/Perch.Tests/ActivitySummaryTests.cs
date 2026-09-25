using Perch.Data.Control;
using Perch.Data.Roost;
using Xunit;

namespace Perch.Tests;

/// <summary>
/// Guards <see cref="ActivitySummary"/>, the Roost mini card's "last few things it did" lines: tool calls with
/// their collapsed result, a pending permission / question / plan kept as the newest line, closing prose as
/// "Done: …" only once the turn is over, and the skips (thinking, info notes, resolved permissions).
/// </summary>
public class ActivitySummaryTests
{
    private static SessionConversation Conv(params SessionEvent[] events)
    {
        var conv = new SessionConversation();
        conv.AddUserPrompt("fix the flaky test");
        foreach (var e in events) conv.Apply(e);
        return conv;
    }

    private static readonly TurnResultEvent Result = new(false, "success", 0, 0, 0, 0);

    [Fact]
    public void EmptyConversationHasNoLines() =>
        Assert.Empty(ActivitySummary.Build(new SessionConversation(), sessionRunning: false));

    [Fact]
    public void ToolCallsShowTheirPhraseAndCollapsedResult()
    {
        var conv = Conv(
            new ToolUseEvent("t1", "Read", "Reading Routes.cs", """{"file_path":"Routes.cs"}"""),
            new ToolResultEvent("t1", "a\nb\nc", false),
            new ToolUseEvent("t2", "Bash", "Running dotnet test"));

        var lines = ActivitySummary.Build(conv, sessionRunning: true);
        Assert.Equal([
            new ActivityLine(ActivityKind.User, "You: fix the flaky test"),
            new ActivityLine(ActivityKind.Tool, "Reading Routes.cs · Read 3 lines"),
            new ActivityLine(ActivityKind.ToolRunning, "Running dotnet test (running…)"),
        ], lines);
    }

    [Fact]
    public void KeepsOnlyTheNewestLinesInOrder()
    {
        var conv = Conv(
            new ToolUseEvent("t1", "Bash", "Running a"), new ToolResultEvent("t1", "ok", false),
            new ToolUseEvent("t2", "Bash", "Running b"), new ToolResultEvent("t2", "ok", false),
            new ToolUseEvent("t3", "Bash", "Running c"), new ToolResultEvent("t3", "boom", true));

        Assert.Equal(["Running b", "Running c (failed)"], ActivitySummary.Build(conv, true, max: 2).Select(l => l.Text));
        Assert.Equal(ActivityKind.ToolFailed, ActivitySummary.Build(conv, true, max: 1)[0].Kind);
    }

    [Fact]
    public void ClosingProseReadsAsDoneOnceTheTurnIsOver()
    {
        var conv = Conv(new AssistantTextEvent("## Summary\nAdded the **chart** legend."), Result);
        var last = ActivitySummary.Build(conv, sessionRunning: false)[^1];
        Assert.Equal(new ActivityLine(ActivityKind.Done, "Done: Summary"), last);
    }

    [Theory]
    [InlineData("Done — merged the duplicates.", "Done — merged the duplicates.")]
    [InlineData("done. Tests pass.", "done. Tests pass.")]
    [InlineData("Doneness checks added.", "Done: Doneness checks added.")]
    public void ClosingProseThatAlreadySaysDoneIsNotPrefixedAgain(string prose, string expected)
    {
        var conv = Conv(new AssistantTextEvent(prose), Result);
        Assert.Equal(new ActivityLine(ActivityKind.Done, expected), ActivitySummary.Build(conv, sessionRunning: false)[^1]);
    }

    [Fact]
    public void TailedTranscriptProseIsOnlyDoneWhenTheSessionIsNotRunning()
    {
        // A tailed transcript never gets a result event, so the item stays incomplete: the monitor decides.
        var conv = new SessionConversation();
        conv.AppendTranscriptLine("""{"type":"assistant","message":{"role":"assistant","content":[{"type":"text","text":"Added the chart legend."}]}}""");

        Assert.Equal(ActivityKind.Prose, ActivitySummary.Build(conv, sessionRunning: true)[^1].Kind);
        Assert.Equal(new ActivityLine(ActivityKind.Done, "Done: Added the chart legend."),
            ActivitySummary.Build(conv, sessionRunning: false)[^1]);
    }

    [Fact]
    public void ProseFollowedByAToolIsNeverDone()
    {
        var conv = Conv(new AssistantTextEvent("Let me check the tests."), new ToolUseEvent("t1", "Bash", "Running dotnet test"));
        var lines = ActivitySummary.Build(conv, sessionRunning: false);
        Assert.Equal(new ActivityLine(ActivityKind.Prose, "Let me check the tests."), lines[^2]);
    }

    [Fact]
    public void PendingPermissionIsTheNewestLine()
    {
        var conv = Conv(
            new ToolUseEvent("t1", "Bash", "Running dotnet test"),
            new PermissionRequestEvent("r1", "Bash", "dotnet test", """{"command":"dotnet test"}""", null));

        var lines = ActivitySummary.Build(conv, sessionRunning: true);
        Assert.Equal(new ActivityLine(ActivityKind.Permission, "Allow Bash: dotnet test?"), lines[^1]);
    }

    [Fact]
    public void PendingQuestionShowsTheFirstQuestion()
    {
        var input = """{"questions":[{"question":"Which layout?","header":"Layout","options":[{"label":"A","description":""}],"multiSelect":false}]}""";
        var conv = Conv(new PermissionRequestEvent("r1", AskUserQuestionInput.ToolName, "", input, null));
        Assert.Equal(new ActivityLine(ActivityKind.Question, "Asks: Which layout?"), ActivitySummary.Build(conv, true)[^1]);
    }

    [Fact]
    public void PendingPlanAsksForApproval()
    {
        var conv = Conv(new PermissionRequestEvent("r1", PlanApprovalInput.ToolName, "", """{"plan":"do it"}""", null));
        Assert.Equal(ActivityKind.Plan, ActivitySummary.Build(conv, true)[^1].Kind);
    }

    [Fact]
    public void ResolvedPermissionsThinkingAndInfoNotesAreSkipped()
    {
        var conv = Conv(
            new AssistantThinkingEvent("hmm, let me think"),
            new PermissionRequestEvent("r1", "Bash", "ls", "{}", null));
        conv.ResolvePermission(conv.PendingPermission!, allowed: true);
        conv.AddNote("permission mode → plan");

        var lines = ActivitySummary.Build(conv, sessionRunning: true);
        Assert.Equal([new ActivityLine(ActivityKind.User, "You: fix the flaky test")], lines);
    }

    [Fact]
    public void AnErroredSessionsClosingTextIsAnErrorNotDone()
    {
        var conv = Conv(new AssistantTextEvent("API Error: 529 Overloaded."), Result);
        Assert.Equal(new ActivityLine(ActivityKind.Error, "API Error: 529 Overloaded."),
            ActivitySummary.Build(conv, sessionRunning: false, sessionErrored: true)[^1]);
    }

    [Fact]
    public void ErrorNotesShow()
    {
        var conv = Conv();
        conv.SessionEnded(1, "boom");
        Assert.Equal(ActivityKind.Error, ActivitySummary.Build(conv, false)[^1].Kind);
    }

    [Theory]
    [InlineData("## Heading\nbody", "Heading")]
    [InlineData("```csharp\nvar x = 1;\n```", "var x = 1;")]
    [InlineData("- **Fixed** the `Snap` bug", "Fixed the Snap bug")]
    [InlineData("\n\n  \n", "")]
    public void SnippetTakesTheFirstMeaningfulLine(string text, string expected) =>
        Assert.Equal(expected, ActivitySummary.Snippet(text));

    [Fact]
    public void LongLinesAreClipped()
    {
        var s = ActivitySummary.Snippet(new string('x', 300));
        Assert.True(s.Length <= 96);
        Assert.EndsWith("…", s);
    }
}
