using System.Text.Json.Nodes;
using Perch.Data.Control;
using Xunit;

namespace Perch.Tests;

/// <summary>
/// Replays real <c>claude -p --output-format stream-json</c> stdout (captured from 2.1.282, scrubbed) through
/// <see cref="StreamJsonParser"/> into <see cref="SessionConversation"/>, the path the controller's pump takes.
/// Hand-built events can't express wire details such as a sub-agent's <c>parent_tool_use_id</c>; these can.
/// Fixture lines are CLI records, plus <c>{"perch":"prompt"}</c> where Perch sent a prompt and
/// <c>{"perch":"expect"}</c> checkpoints on the turn state at that point in the stream.
/// </summary>
public class StreamJsonReplayTests
{
    [Theory]
    [InlineData("absorbed-mid-turn-prompt.jsonl")]
    [InlineData("queued-prompts-own-turn.jsonl")]
    [InlineData("background-agent-after-result.jsonl")]
    public void Replay_MatchesExpectedTurnState(string fixture)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "fixtures", "stream-json", fixture);
        var conv = new SessionConversation();
        int checkpoints = 0;
        int lineNo = 0;
        foreach (var line in File.ReadLines(path))
        {
            lineNo++;
            var node = JsonNode.Parse(line)!;
            switch (node["perch"]?.GetValue<string>())
            {
                case "prompt":
                    conv.AddUserPrompt(node["text"]!.GetValue<string>());
                    break;
                case "expect":
                    checkpoints++;
                    var why = $"{fixture}:{lineNo}: {node["why"]}";
                    Assert.True(node["turnActive"]!.GetValue<bool>() == conv.TurnActive,
                        $"{why} (TurnActive was {conv.TurnActive})");
                    if (node["settled"] is { } settled)
                        Assert.True(settled.GetValue<bool>() == conv.IsSettled, $"{why} (IsSettled was {conv.IsSettled})");
                    break;
                default:
                    foreach (var ev in StreamJsonParser.Parse(line)) conv.Apply(ev);
                    break;
            }
        }
        Assert.True(checkpoints > 0, $"{fixture} has no expect checkpoints");
    }
}
