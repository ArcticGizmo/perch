using Perch.Data.Replay;
using Xunit;

namespace Perch.Tests;

/// <summary>
/// Covers <see cref="SessionStatusSynthesiser"/> — the transcript-position → raw-status reconstruction
/// the projector writes into each synthesised session file.
/// </summary>
public class SessionStatusSynthesiserTests
{
    private static string Assistant(string body) =>
        "{\"type\":\"assistant\",\"message\":{\"role\":\"assistant\",\"content\":[{\"type\":\"text\",\"text\":\""
        + body + "\"}]}}";

    private static string AssistantToolUse(string id) =>
        "{\"type\":\"assistant\",\"message\":{\"role\":\"assistant\",\"content\":[{\"type\":\"tool_use\",\"id\":\""
        + id + "\",\"name\":\"Bash\",\"input\":{}}]}}";

    private static string ToolResult(string id) =>
        "{\"type\":\"user\",\"message\":{\"role\":\"user\",\"content\":[{\"type\":\"tool_result\",\"tool_use_id\":\""
        + id + "\",\"content\":\"ok\"}]}}";

    private static string UserPrompt(string text) =>
        "{\"type\":\"user\",\"message\":{\"role\":\"user\",\"content\":\"" + text + "\"}}";

    [Fact]
    public void Empty_IsIdle() => Assert.Equal("idle", SessionStatusSynthesiser.Synthesise([]));

    [Fact]
    public void CompletedAssistantTurn_IsIdle() =>
        Assert.Equal("idle", SessionStatusSynthesiser.Synthesise([UserPrompt("hi"), Assistant("done")]));

    [Fact]
    public void OpenToolUse_IsBusy() =>
        Assert.Equal("busy", SessionStatusSynthesiser.Synthesise([UserPrompt("hi"), AssistantToolUse("t1")]));

    [Fact]
    public void ToolResultThenNoReply_IsBusy() =>
        // The tool has returned but the model hasn't produced its follow-up turn yet → still working.
        Assert.Equal("busy", SessionStatusSynthesiser.Synthesise(
            [AssistantToolUse("t1"), ToolResult("t1")]));

    [Fact]
    public void MatchedToolUse_ThenAssistantReply_IsIdle() =>
        Assert.Equal("idle", SessionStatusSynthesiser.Synthesise(
            [AssistantToolUse("t1"), ToolResult("t1"), Assistant("all done")]));

    [Fact]
    public void GenuineUserPrompt_Last_IsBusy() =>
        Assert.Equal("busy", SessionStatusSynthesiser.Synthesise([UserPrompt("please do X")]));

    [Fact]
    public void InterruptMarker_Last_IsIdle() =>
        Assert.Equal("idle", SessionStatusSynthesiser.Synthesise(
            [AssistantToolUse("t1"), UserPrompt("[Request interrupted by user]")]));

    [Fact]
    public void BareCommand_Last_IsIdle() =>
        Assert.Equal("idle", SessionStatusSynthesiser.Synthesise(
            [Assistant("x"), UserPrompt("<command-name>/clear</command-name>")]));
}
