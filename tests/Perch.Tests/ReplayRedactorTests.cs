using System.Text.Json.Nodes;
using Perch.Data;
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
    public void SetModelLine_KeepsOnlyTheModelName()
    {
        var line = """
            {"type":"user","message":{"role":"user","content":"<local-command-stdout>Set model to Opus 4.8</local-command-stdout>"}}
            """.ReplaceLineEndings("");
        var content = Redacted(line)["message"]!["content"]!.GetValue<string>();
        Assert.Equal("<local-command-stdout>Set model to \u001b[1mOpus 4.8\u001b[22m</local-command-stdout>", content);
        Assert.Equal("Opus 4.8", ModelContext.ParseDisplayName(content));
    }

    [Theory]
    // CP15: the whole stdout used to survive whenever it mentioned "Set model to".
    [InlineData("Set model to \u001b[1mOpus 4.8 (1M context)\u001b[22m and saved to C:\\\\Users\\\\secret\\\\settings.json",
                "Set model to \u001b[1mOpus 4.8 (1M context)\u001b[22m")]
    [InlineData("Kept model as \u001b[1mSonnet 5\u001b[22m for this session · secret",
                "Kept model as \u001b[1mSonnet 5\u001b[22m")]
    [InlineData("secret output; Set model to x; rm secret <b>", "[redacted]")]
    public void ModelLineStdout_IsRebuiltAroundTheName_OrScrubbed(string stdout, string expectedInner)
    {
        var line = new JsonObject
        {
            ["type"] = "system", ["subtype"] = "local_command",
            ["content"] = $"<local-command-stdout>{stdout}</local-command-stdout>",
        }.ToJsonString();
        var redacted = TranscriptRedactor.RedactLine(line, Cwd);

        Assert.Equal($"<local-command-stdout>{expectedInner}</local-command-stdout>",
            JsonNode.Parse(redacted)!["content"]!.GetValue<string>());
        Assert.DoesNotContain("secret", redacted);
    }

    [Fact]
    public void MalformedLine_BecomesTheRedactionToken()
    {
        // CP15: the partially written trailing line of a live transcript used to ship verbatim. The token is
        // just as malformed to the readers, so they skip it the same way.
        const string partial = """{"type":"user","message":{"content":"my secret prom""";
        Assert.Equal("[redacted]", TranscriptRedactor.RedactLine(partial, Cwd));
        Assert.Equal("[redacted]", TranscriptRedactor.RedactMeta("""{"description":"secret""", Cwd));
    }

    [Fact]
    public void PreserveKeys_InsideToolPayloads_AreScrubbed()
    {
        // CP15: preserve-listed keys (name/id/type/model) used to survive at any depth, so a tool input or
        // result carrying {"name":"Customer A"} leaked it.
        var use = """
            {"type":"assistant","message":{"role":"assistant","content":[
              {"type":"tool_use","id":"tk1","name":"Task","input":{"subagent_type":"Explore",
               "name":"Customer A","id":"acct-secret","type":"secret-type","model":"secret-model",
               "nested":{"name":"Customer A"}}}]}}
            """.ReplaceLineEndings("");
        var res = """
            {"type":"user","toolUseResult":{"name":"Customer A","type":"secret","isAsync":true,
               "usage":{"output_tokens":7}},
             "message":{"role":"user","content":[
              {"type":"tool_result","tool_use_id":"tk1","content":[{"type":"resource_link","name":"Customer A"}]}]}}
            """.ReplaceLineEndings("");

        var useOut = TranscriptRedactor.RedactLine(use, Cwd);
        var block = JsonNode.Parse(useOut)!["message"]!["content"]![0]!;
        Assert.Equal("Task", block["name"]!.GetValue<string>());          // structural: the tool's name
        Assert.Equal("tk1", block["id"]!.GetValue<string>());
        Assert.Equal("Explore", block["input"]!["subagent_type"]!.GetValue<string>());
        Assert.Equal("[redacted]", block["input"]!["name"]!.GetValue<string>());
        Assert.Equal("[redacted]", block["input"]!["model"]!.GetValue<string>());
        Assert.DoesNotContain("Customer A", useOut);
        Assert.DoesNotContain("secret", useOut);

        var resOut = TranscriptRedactor.RedactLine(res, Cwd);
        var r = JsonNode.Parse(resOut)!;
        Assert.True(r["toolUseResult"]!["isAsync"]!.GetValue<bool>());       // bools and numbers still pass
        Assert.Equal(7, r["toolUseResult"]!["usage"]!["output_tokens"]!.GetValue<int>());
        Assert.Equal("tk1", r["message"]!["content"]![0]!["tool_use_id"]!.GetValue<string>());
        Assert.DoesNotContain("Customer A", resOut);
        Assert.DoesNotContain("secret", resOut);
    }

    [Fact]
    public void PreserveKeys_KeepOnlyScalars_AndImageSourceShape()
    {
        var line = """
            {"type":"user","model":{"k":"secret"},"message":{"role":"user","content":[
              {"type":"image","source":{"type":"base64","media_type":"image/png","data":"c2VjcmV0"}}]}}
            """.ReplaceLineEndings("");
        var outLine = TranscriptRedactor.RedactLine(line, Cwd);
        var n = JsonNode.Parse(outLine)!;

        Assert.Equal("[redacted]", n["model"]!["k"]!.GetValue<string>());   // an object is recursed, not cloned
        var source = n["message"]!["content"]![0]!["source"]!;
        Assert.Equal("base64", source["type"]!.GetValue<string>());
        Assert.Equal("image/png", source["media_type"]!.GetValue<string>());
        Assert.Equal("[redacted]", source["data"]!.GetValue<string>());
        Assert.DoesNotContain("secret", outLine);
    }

    [Fact]
    public void Meta_PreserveKeys_ApplyAtTheTopLevelOnly()
    {
        const string meta = """{"agentType":"Explore","extra":{"agentType":"Customer A","color":"Customer A"}}""";
        var outMeta = TranscriptRedactor.RedactMeta(meta, Cwd);
        Assert.Equal("Explore", JsonNode.Parse(outMeta)!["agentType"]!.GetValue<string>());
        Assert.DoesNotContain("Customer A", outMeta);
    }

    [Fact]
    public void RedactedSnapshot_IsAnAllowlist()
    {
        // CP15: the /rename title, waitingFor and the real Remote Control session id used to survive.
        const string snapshot = """
            {"pid":4242,"sessionId":"orig","cwd":"C:\\secret\\proj","status":"waiting","name":"Customer A rollout",
             "title":"Customer A rollout","waitingFor":"approve secret deploy","entrypoint":"cli",
             "bridgeSessionId":"session_01SECRETREALID","futureField":"secret"}
            """;
        var outSnap = RecordingExporter.RedactSnapshot(snapshot, Cwd, 900_001, "sid-1");
        var n = JsonNode.Parse(outSnap)!.AsObject();

        Assert.Equal(["pid", "sessionId", "cwd", "entrypoint", "bridgeSessionId"], n.Select(p => p.Key));
        Assert.Equal(900_001, n["pid"]!.GetValue<int>());
        Assert.Equal("sid-1", n["sessionId"]!.GetValue<string>());
        Assert.Equal(Cwd, n["cwd"]!.GetValue<string>());
        Assert.Equal("cli", n["entrypoint"]!.GetValue<string>());
        Assert.Equal(RecordingExporter.RedactedBridgeSessionId, n["bridgeSessionId"]!.GetValue<string>());
        Assert.DoesNotContain("Customer A", outSnap);
        Assert.DoesNotContain("SECRET", outSnap, StringComparison.OrdinalIgnoreCase);

        // No Remote Control → no bridge marker; an odd entrypoint is dropped; unparseable → identity only.
        var plain = JsonNode.Parse(RecordingExporter.RedactSnapshot("""{"entrypoint":"cli; secret"}""", Cwd, 1, "s"))!.AsObject();
        Assert.Equal(["pid", "sessionId", "cwd"], plain.Select(p => p.Key));
        Assert.Equal(["pid", "sessionId", "cwd"],
            JsonNode.Parse(RecordingExporter.RedactSnapshot("{not json", Cwd, 1, "s"))!.AsObject().Select(p => p.Key));
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
