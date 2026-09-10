using Perch.Data.Control;
using Xunit;

namespace Perch.Tests;

/// <summary>
/// The stream-json decoder behind the controlled-session PoC (docs/session-control-poc.md). The sample
/// lines are trimmed captures from a real <c>claude -p --output-format stream-json</c> run (2.1.247).
/// </summary>
public class StreamJsonParserTests
{
    [Fact]
    public void Init_YieldsSessionInit()
    {
        var line = """{"type":"system","subtype":"init","cwd":"C:\\proj","session_id":"5b4d131d-dfd4-4103-860c-f5c96094b598","tools":["Bash","Read","Write"],"model":"claude-haiku-4-5-20251001","permissionMode":"default","uuid":"u1"}""";
        var ev = Assert.IsType<SessionInitEvent>(Assert.Single(StreamJsonParser.Parse(line)));
        Assert.Equal("5b4d131d-dfd4-4103-860c-f5c96094b598", ev.SessionId);
        Assert.Equal("claude-haiku-4-5-20251001", ev.Model);
        Assert.Equal("default", ev.PermissionMode);
        Assert.Equal(3, ev.ToolCount);
        Assert.Empty(ev.SlashCommands!);
    }

    [Fact]
    public void Init_CapturesAdvertisedSlashCommands()
    {
        var line = """{"type":"system","subtype":"init","session_id":"s","tools":[],"model":"m","permissionMode":"default","slash_commands":["compact","model","review"]}""";
        var ev = Assert.IsType<SessionInitEvent>(Assert.Single(StreamJsonParser.Parse(line)));
        Assert.Equal(new[] { "compact", "model", "review" }, ev.SlashCommands);
    }

    [Fact]
    public void Init_CapturesMcpServers()
    {
        var line = """{"type":"system","subtype":"init","session_id":"s","tools":[],"model":"m","permissionMode":"default","mcp_servers":[{"name":"Atlassian Rovo","status":"needs-auth"},{"name":"Gmail","status":"connected"},{"name":"","status":"x"}]}""";
        var ev = Assert.IsType<SessionInitEvent>(Assert.Single(StreamJsonParser.Parse(line)));
        Assert.NotNull(ev.McpServers);
        Assert.Equal(2, ev.McpServers!.Count);   // the empty-named entry is dropped
        Assert.Equal("Atlassian Rovo", ev.McpServers[0].Name);
        Assert.Equal("needs-auth", ev.McpServers[0].Status);
        Assert.Equal("connected", ev.McpServers[1].Status);
    }

    [Fact]
    public void AssistantMessage_YieldsOneEventPerBlock()
    {
        var line = """{"type":"assistant","message":{"role":"assistant","content":[{"type":"thinking","thinking":"pondering"},{"type":"text","text":"hello"},{"type":"tool_use","id":"toolu_1","name":"Bash","input":{"command":"echo hi"}}]},"session_id":"s"}""";
        var events = StreamJsonParser.Parse(line);
        Assert.Equal(3, events.Count);
        Assert.Equal("pondering", Assert.IsType<AssistantThinkingEvent>(events[0]).Text);
        Assert.Equal("hello", Assert.IsType<AssistantTextEvent>(events[1]).Text);
        var tool = Assert.IsType<ToolUseEvent>(events[2]);
        Assert.Equal("toolu_1", tool.ToolUseId);
        Assert.Equal("Bash", tool.ToolName);
        Assert.Equal("Running: echo hi", tool.Summary);
        Assert.Equal("""{"command":"echo hi"}""", tool.InputJson);
    }

    [Fact]
    public void ToolResult_HandlesStringAndBlockContent()
    {
        var stringResult = """{"type":"user","message":{"role":"user","content":[{"type":"tool_result","tool_use_id":"toolu_1","content":"ok"}]}}""";
        var ev = Assert.IsType<ToolResultEvent>(Assert.Single(StreamJsonParser.Parse(stringResult)));
        Assert.Equal("toolu_1", ev.ToolUseId);
        Assert.Equal("ok", ev.Preview);
        Assert.False(ev.IsError);

        var blockResult = """{"type":"user","message":{"role":"user","content":[{"type":"tool_result","tool_use_id":"toolu_2","content":[{"type":"text","text":"boom"}],"is_error":true}]}}""";
        var err = Assert.IsType<ToolResultEvent>(Assert.Single(StreamJsonParser.Parse(blockResult)));
        Assert.Equal("boom", err.Preview);
        Assert.True(err.IsError);
    }

    [Fact]
    public void StreamEvent_TextDeltaOnly()
    {
        var delta = """{"type":"stream_event","event":{"type":"content_block_delta","index":0,"delta":{"type":"text_delta","text":"chunk"}}}""";
        Assert.Equal("chunk", Assert.IsType<TextDeltaEvent>(Assert.Single(StreamJsonParser.Parse(delta))).Text);

        var toolDelta = """{"type":"stream_event","event":{"type":"content_block_delta","index":1,"delta":{"type":"input_json_delta","partial_json":"{\"comm"}}}""";
        Assert.Empty(StreamJsonParser.Parse(toolDelta));
    }

    [Fact]
    public void CanUseTool_YieldsPermissionRequest()
    {
        var line = """{"type":"control_request","request_id":"be11bc84-3253-405d-aef7-1eaba4143976","request":{"subtype":"can_use_tool","tool_name":"Write","display_name":"Write","input":{"file_path":"C:\\t\\perm-test.txt","content":"hello"},"description":"perm-test.txt","permission_suggestions":[{"type":"setMode","mode":"acceptEdits","destination":"session"}],"tool_use_id":"toolu_x"}}""";
        var ev = Assert.IsType<PermissionRequestEvent>(Assert.Single(StreamJsonParser.Parse(line)));
        Assert.Equal("be11bc84-3253-405d-aef7-1eaba4143976", ev.RequestId);
        Assert.Equal("Write", ev.ToolName);
        Assert.Equal("perm-test.txt", ev.Description);
        Assert.Contains("perm-test.txt", ev.InputJson);
        Assert.Equal("acceptEdits", ev.SuggestedMode);
    }

    [Fact]
    public void ControlResponse_ModeAckYieldsModeChanged_OtherAcksIgnored()
    {
        var modeAck = """{"type":"control_response","response":{"subtype":"success","request_id":"req_mode_1","response":{"mode":"acceptEdits"}}}""";
        Assert.Equal("acceptEdits", Assert.IsType<ModeChangedEvent>(Assert.Single(StreamJsonParser.Parse(modeAck))).Mode);

        var initAck = """{"type":"control_response","response":{"subtype":"success","request_id":"req_init_1","response":{"commands":[]}}}""";
        Assert.Empty(StreamJsonParser.Parse(initAck));
    }

    [Fact]
    public void ControlResponse_RemoteControlAck_YieldsRemoteControlEvent()
    {
        // Enable ack (shape captured live from claude 2.1.263): the inner response carries the claude.ai
        // session URL and bridge id, matched by Perch's "perch-rc" request id.
        var enable = """{"type":"control_response","response":{"subtype":"success","request_id":"perch-rc","response":{"session_url":"https://claude.ai/code/session_017mAetP5THaUYGmHDFo7w6y","connect_url":"https://claude.ai/code?environment=","environment_id":"","bridge_epoch":1,"bridge_session_id":"cse_017mAetP5THaUYGmHDFo7w6y"}}}""";
        var on = Assert.IsType<RemoteControlEvent>(Assert.Single(StreamJsonParser.Parse(enable)));
        Assert.Equal("https://claude.ai/code/session_017mAetP5THaUYGmHDFo7w6y", on.SessionUrl);
        Assert.Equal("cse_017mAetP5THaUYGmHDFo7w6y", on.BridgeSessionId);
        Assert.Null(on.Error);

        // Disable ack: empty success → a RemoteControlEvent with no URL (off).
        var disable = """{"type":"control_response","response":{"subtype":"success","request_id":"perch-rc"}}""";
        var off = Assert.IsType<RemoteControlEvent>(Assert.Single(StreamJsonParser.Parse(disable)));
        Assert.Null(off.SessionUrl);

        // Error ack (e.g. remote control unavailable): surfaces the message.
        var err = """{"type":"control_response","response":{"subtype":"error","request_id":"perch-rc","error":"Remote control is not available"}}""";
        var failed = Assert.IsType<RemoteControlEvent>(Assert.Single(StreamJsonParser.Parse(err)));
        Assert.Null(failed.SessionUrl);
        Assert.Equal("Remote control is not available", failed.Error);
    }

    [Fact]
    public void Result_YieldsTurnResult()
    {
        var line = """{"type":"result","subtype":"success","is_error":false,"duration_ms":2318,"num_turns":1,"session_id":"s","total_cost_usd":0.062387,"usage":{"input_tokens":10,"output_tokens":47},"result":"hello"}""";
        var ev = Assert.IsType<TurnResultEvent>(Assert.Single(StreamJsonParser.Parse(line)));
        Assert.False(ev.IsError);
        Assert.Equal("success", ev.Subtype);
        Assert.Equal(0.062387, ev.CostUsd, precision: 6);
        Assert.Equal(10, ev.InputTokens);
        Assert.Equal(47, ev.OutputTokens);
        Assert.Equal(2318, ev.DurationMs);
    }

    [Fact]
    public void Result_CapturesCacheTokens_AndDerivesContext()
    {
        var line = """{"type":"result","subtype":"success","is_error":false,"duration_ms":900,"session_id":"s","total_cost_usd":0.01,"usage":{"input_tokens":1200,"output_tokens":800,"cache_read_input_tokens":45000,"cache_creation_input_tokens":3000}}""";
        var ev = Assert.IsType<TurnResultEvent>(Assert.Single(StreamJsonParser.Parse(line)));
        Assert.Equal(45000, ev.CacheReadTokens);
        Assert.Equal(3000, ev.CacheCreationTokens);
        // Context occupancy = every input bucket summed; fresh (billed) input excludes the cache re-read.
        Assert.Equal(49200, ev.ContextTokens);
        Assert.Equal(4200, ev.FreshInputTokens);
    }

    [Fact]
    public void CompactStatus_ParsesAPercentFromFieldOrText()
    {
        // A numeric progress field.
        var field = """{"type":"system","subtype":"status","message":"Compacting conversation…","progress":42}""";
        var a = Assert.IsType<StatusEvent>(Assert.Single(StreamJsonParser.Parse(field)));
        Assert.Equal(42, a.Percent);
        Assert.Equal("Compacting conversation…", a.Message);

        // No field, but a "NN%" embedded in the status text (the CLI's readout).
        var text = """{"type":"system","subtype":"status","message":"Compacting conversation… (18s)  18%"}""";
        Assert.Equal(18, Assert.IsType<StatusEvent>(Assert.Single(StreamJsonParser.Parse(text))).Percent);

        // Neither: still a StatusEvent (indeterminate — the UI drives its own timer), never a throw.
        var bare = """{"type":"system","subtype":"status","message":"Working…"}""";
        Assert.Null(Assert.IsType<StatusEvent>(Assert.Single(StreamJsonParser.Parse(bare))).Percent);
    }

    [Fact]
    public void CompactBoundary_ParsesPreAndPostTokens()
    {
        // The real shape captured live (claude 2.1.260): the authoritative "compaction succeeded" record.
        var line = """{"type":"system","subtype":"compact_boundary","content":"Conversation compacted","compactMetadata":{"trigger":"manual","preTokens":8281,"durationMs":81322,"postTokens":5350,"cumulativeDroppedTokens":401532}}""";
        var ev = Assert.IsType<CompactionCompletedEvent>(Assert.Single(StreamJsonParser.Parse(line)));
        Assert.Equal(8281, ev.PreTokens);
        Assert.Equal(5350, ev.PostTokens);
        Assert.Equal("manual", ev.Trigger);
    }

    [Theory]
    // A nested percent field is still found (the scan is structural, not tied to a fixed key path).
    [InlineData("""{"type":"system","subtype":"status","data":{"compaction":{"percent":63}}}""", 63)]
    // A 0..1 ratio is scaled to a percentage.
    [InlineData("""{"type":"system","subtype":"status","progress":0.25}""", 25)]
    // "pct" shorthand.
    [InlineData("""{"type":"system","subtype":"status","pct":7}""", 7)]
    public void CompactStatus_FindsAPercentWhereverItSits(string line, int expected) =>
        Assert.Equal(expected, Assert.IsType<StatusEvent>(Assert.Single(StreamJsonParser.Parse(line))).Percent);

    [Theory]
    [InlineData("")]
    [InlineData("not json at all")]
    [InlineData("{\"type\":\"rate_limit_event\",\"rate_limit_info\":{}}")]
    [InlineData("{\"type\":\"system\",\"subtype\":\"hook_started\",\"hook_name\":\"SessionStart\"}")]
    [InlineData("{\"truncated\":")]
    public void UnknownOrMalformedLines_YieldNothing(string line) =>
        Assert.Empty(StreamJsonParser.Parse(line));
}
