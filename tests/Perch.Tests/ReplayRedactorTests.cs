using System.Text.Json.Nodes;
using Perch.Data.Replay;
using Xunit;

namespace Perch.Tests;

/// <summary>
/// Covers <see cref="TranscriptRedactor"/>: the guarantee is that content is scrubbed while the
/// structure the state machine + stats read (record/tool kinds, token counts, model, id pairing,
/// timestamps, control markers) survives intact.
/// </summary>
public class ReplayRedactorTests
{
    private const string Cwd = @"C:\demo\project-a";

    private static JsonNode Redacted(string line) =>
        JsonNode.Parse(TranscriptRedactor.RedactLine(line, Cwd))!;

    [Fact]
    public void UserText_IsScrubbed_ButStructureKept()
    {
        var line = """
            {"type":"user","timestamp":"2025-03-08T12:00:00Z","cwd":"C:\\secret\\proj",
             "gitBranch":"feature/secret","message":{"role":"user","content":"my secret prompt"}}
            """.ReplaceLineEndings("");
        var n = Redacted(line);

        Assert.Equal("user", n["type"]!.GetValue<string>());
        Assert.Equal("2025-03-08T12:00:00Z", n["timestamp"]!.GetValue<string>());
        Assert.Equal("user", n["message"]!["role"]!.GetValue<string>());
        Assert.Equal("[redacted]", n["message"]!["content"]!.GetValue<string>());
        Assert.Equal(Cwd, n["cwd"]!.GetValue<string>());
        Assert.Equal("main", n["gitBranch"]!.GetValue<string>());
        Assert.DoesNotContain("secret", TranscriptRedactor.RedactLine(line, Cwd));
    }

    [Fact]
    public void AssistantUsageAndModel_Survive_TextAndThinkingScrubbed()
    {
        var line = """
            {"type":"assistant","timestamp":"2025-03-08T12:00:05Z","message":{"role":"assistant",
             "model":"claude-opus-4-8","usage":{"input_tokens":40000,"output_tokens":10000},
             "content":[{"type":"thinking","thinking":"secret reasoning"},
                        {"type":"text","text":"secret answer"}]}}
            """.ReplaceLineEndings("");
        var n = Redacted(line);
        var msg = n["message"]!;

        Assert.Equal("claude-opus-4-8", msg["model"]!.GetValue<string>());
        Assert.Equal(40000, msg["usage"]!["input_tokens"]!.GetValue<int>());
        Assert.Equal(10000, msg["usage"]!["output_tokens"]!.GetValue<int>());
        var content = msg["content"]!.AsArray();
        Assert.Equal("thinking", content[0]!["type"]!.GetValue<string>());
        Assert.Equal("[redacted]", content[0]!["thinking"]!.GetValue<string>());
        Assert.Equal("text", content[1]!["type"]!.GetValue<string>());
        Assert.Equal("[redacted]", content[1]!["text"]!.GetValue<string>());
        Assert.DoesNotContain("secret", TranscriptRedactor.RedactLine(line, Cwd));
    }

    [Fact]
    public void ToolUseAndResult_KeepNameAndPairing_InputScrubbed()
    {
        var use = """
            {"type":"assistant","message":{"role":"assistant","content":[
              {"type":"tool_use","id":"tk1","name":"Bash","input":{"command":"rm -rf /secret"}}]}}
            """.ReplaceLineEndings("");
        var res = """
            {"type":"user","message":{"role":"user","content":[
              {"type":"tool_result","tool_use_id":"tk1","content":"secret output"}]}}
            """.ReplaceLineEndings("");

        var u = Redacted(use)["message"]!["content"]!.AsArray()[0]!;
        Assert.Equal("tool_use", u["type"]!.GetValue<string>());
        Assert.Equal("tk1", u["id"]!.GetValue<string>());
        Assert.Equal("Bash", u["name"]!.GetValue<string>());
        Assert.Equal("[redacted]", u["input"]!["command"]!.GetValue<string>());

        var r = Redacted(res)["message"]!["content"]!.AsArray()[0]!;
        Assert.Equal("tool_result", r["type"]!.GetValue<string>());
        Assert.Equal("tk1", r["tool_use_id"]!.GetValue<string>()); // pairing preserved
        Assert.Equal("[redacted]", r["content"]!.GetValue<string>());
    }

    [Fact]
    public void InterruptMarker_IsPreserved()
    {
        var line = """
            {"type":"user","message":{"role":"user","content":"[Request interrupted by user]"}}
            """.ReplaceLineEndings("");
        Assert.Equal("[Request interrupted by user]",
            Redacted(line)["message"]!["content"]!.GetValue<string>());

        var toolVariant = """
            {"type":"user","message":{"role":"user","content":"[Request interrupted by user for tool use]"}}
            """.ReplaceLineEndings("");
        Assert.Equal("[Request interrupted by user for tool use]",
            Redacted(toolVariant)["message"]!["content"]!.GetValue<string>());
    }

    [Fact]
    public void CommandNamePrefix_IsPreserved()
    {
        var line = """
            {"type":"user","message":{"role":"user","content":"<command-name>/clear</command-name><command-args>secret</command-args>"}}
            """.ReplaceLineEndings("");
        var content = Redacted(line)["message"]!["content"]!.GetValue<string>();
        Assert.StartsWith("<command-name>", content); // bare-command detection still fires
        Assert.DoesNotContain("secret", content);
    }

    [Fact]
    public void SetModelLine_IsPreserved()
    {
        var line = """
            {"type":"user","message":{"role":"user","content":"<local-command-stdout>Set model to Opus 4.8</local-command-stdout>"}}
            """.ReplaceLineEndings("");
        Assert.Contains("Set model to Opus 4.8",
            Redacted(line)["message"]!["content"]!.GetValue<string>());
    }

    [Fact]
    public void MalformedLine_IsReturnedVerbatim()
    {
        // Transcripts carry the odd partial trailing line; it must pass through so the same defensive
        // readers see it during replay.
        const string partial = """{"type":"assistant","message":{"content":[{"type":"tool_use",""";
        Assert.Equal(partial, TranscriptRedactor.RedactLine(partial, Cwd));
    }

    [Fact]
    public void Meta_KeepsTypeSignals_ScrubsHumanFields()
    {
        const string meta = """
            {"agentType":"ux-explorer","description":"UX analysis of secret-app","name":"Ada",
             "taskKind":"in_process_teammate","teamName":"secret-team","color":"blue","toolUseId":"u1"}
            """;
        var n = JsonNode.Parse(TranscriptRedactor.RedactMeta(meta, Cwd))!;

        Assert.Equal("ux-explorer", n["agentType"]!.GetValue<string>());
        Assert.Equal("in_process_teammate", n["taskKind"]!.GetValue<string>());
        Assert.Equal("blue", n["color"]!.GetValue<string>());
        Assert.Equal("u1", n["toolUseId"]!.GetValue<string>());
        Assert.Equal("[redacted]", n["description"]!.GetValue<string>());
        Assert.Equal("[redacted]", n["name"]!.GetValue<string>());
        Assert.Equal("[redacted]", n["teamName"]!.GetValue<string>());
        Assert.DoesNotContain("secret", TranscriptRedactor.RedactMeta(meta, Cwd));
        Assert.DoesNotContain("Ada", TranscriptRedactor.RedactMeta(meta, Cwd));
    }
}
