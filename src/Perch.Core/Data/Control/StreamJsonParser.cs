using System.Text.Json.Nodes;

namespace Perch.Data.Control;

/// <summary>
/// Decodes one line of the Claude Code CLI's <c>--output-format stream-json</c> stream into zero or more
/// <see cref="SessionEvent"/>s. Pure and stateless so it can be unit-tested against captured lines; the
/// process plumbing lives in <see cref="ClaudeSessionController"/>. Like every transcript reader, it
/// parses defensively — an unknown or malformed line yields no events, never a throw. The shapes were
/// captured live from claude 2.1.247 (see <c>docs/session-control-poc.md</c> for samples).
/// </summary>
internal static class StreamJsonParser
{
    public static IReadOnlyList<SessionEvent> Parse(string line)
    {
        if (string.IsNullOrWhiteSpace(line)) return [];
        JsonNode? root;
        try { root = JsonNode.Parse(line); }
        catch { return []; }
        if (root is null) return [];

        try
        {
            return TranscriptJson.AsString(root["type"]) switch
            {
                "system"           => ParseSystem(root),
                "assistant"        => ParseAssistant(root),
                "user"             => ParseToolResults(root),
                "stream_event"     => ParseStreamEvent(root),
                "control_request"  => ParseControlRequest(root),
                "control_response" => ParseControlResponse(root),
                "result"           => ParseResult(root),
                _                  => [],
            };
        }
        catch
        {
            return [];
        }
    }

    private static IReadOnlyList<SessionEvent> ParseSystem(JsonNode root)
    {
        var subtype = TranscriptJson.AsString(root["subtype"]);
        if (subtype == "status") return ParseStatus(root);
        if (subtype != "init") return [];
        var commands = (root["slash_commands"] as JsonArray)?
            .Select(c => TranscriptJson.AsString(c))
            .Where(c => !string.IsNullOrEmpty(c))
            .Select(c => c!)
            .ToList();
        var mcp = (root["mcp_servers"] as JsonArray)?
            .Select(s => new McpServerInfo(
                TranscriptJson.AsString(s?["name"]) ?? "",
                TranscriptJson.AsString(s?["status"]) ?? ""))
            .Where(s => s.Name.Length > 0)
            .ToList();
        return
        [
            new SessionInitEvent(
                TranscriptJson.AsString(root["session_id"]) ?? "",
                TranscriptJson.AsString(root["model"]) ?? "",
                TranscriptJson.AsString(root["permissionMode"]) ?? "",
                (root["tools"] as JsonArray)?.Count ?? 0,
                commands ?? [],
                mcp ?? []),
        ];
    }

    // A progress record (the CLI emits these during /compact). Its exact shape isn't pinned down, so read a
    // percentage defensively and structurally: recursively scan the record for a "percent"/"progress"/"pct"
    // (or "ratio") numeric field wherever it sits, and, failing that, the first "NN%" in any status string.
    // A null percent is fine — the UI shows an indeterminate bar and its own elapsed timer.
    private static IReadOnlyList<SessionEvent> ParseStatus(JsonNode root)
    {
        var message = TranscriptJson.AsString(root["message"])
                   ?? TranscriptJson.AsString(root["status"])
                   ?? TranscriptJson.AsString(root["text"]);
        int? percent = FindPercent(root);
        return [new StatusEvent(percent, message)];
    }

    // Walk the record for a completion percentage: a numeric field whose name reads like one (a >1 value is
    // taken as a percent, a 0..1 value as a ratio ×100), else a "NN%" embedded in any string value. Depth is
    // trivial for a status record, so the recursion cost is negligible.
    private static int? FindPercent(JsonNode? node)
    {
        switch (node)
        {
            case JsonObject obj:
                foreach (var (key, value) in obj)
                {
                    var k = key.ToLowerInvariant();
                    if ((k.Contains("percent") || k.Contains("progress") || k == "pct" || k.Contains("ratio"))
                        && AsNumber(value) is { } n)
                        return n <= 1 ? Math.Clamp((int)Math.Round(n * 100), 0, 100)
                                      : Math.Clamp((int)Math.Round(n), 0, 100);
                }
                foreach (var (_, value) in obj)
                    if (FindPercent(value) is { } p) return p;
                return null;
            case JsonArray arr:
                foreach (var value in arr)
                    if (FindPercent(value) is { } p) return p;
                return null;
            case JsonValue v when v.TryGetValue<string>(out var s):
                var m = System.Text.RegularExpressions.Regex.Match(s, @"(\d{1,3})\s*%");
                return m.Success && int.TryParse(m.Groups[1].Value, out var pv) ? Math.Clamp(pv, 0, 100) : null;
            default:
                return null;
        }
    }

    private static double? AsNumber(JsonNode? n)
    {
        if (n is null) return null;
        try { return n.GetValue<double>(); }
        catch { try { return n.GetValue<long>(); } catch { return null; } }
    }

    private static IReadOnlyList<SessionEvent> ParseAssistant(JsonNode root)
    {
        var blocks = TranscriptJson.ContentArray(root);
        if (blocks is null) return [];
        var events = new List<SessionEvent>();
        foreach (var block in blocks)
        {
            switch (TranscriptJson.BlockType(block))
            {
                case "text" when TranscriptJson.AsString(block?["text"]) is { Length: > 0 } text:
                    events.Add(new AssistantTextEvent(text));
                    break;
                case "thinking" when TranscriptJson.AsString(block?["thinking"]) is { Length: > 0 } thought:
                    events.Add(new AssistantThinkingEvent(thought));
                    break;
                case "tool_use":
                    var name = TranscriptJson.AsString(block?["name"]) ?? "tool";
                    events.Add(new ToolUseEvent(
                        TranscriptJson.AsString(block?["id"]) ?? "",
                        name,
                        ToolSummary.Describe(name, block?["input"]),
                        block?["input"]?.ToJsonString() ?? "{}"));
                    break;
            }
        }
        return events;
    }

    private static IReadOnlyList<SessionEvent> ParseToolResults(JsonNode root)
    {
        var blocks = TranscriptJson.ContentArray(root);
        if (blocks is null) return [];
        var events = new List<SessionEvent>();
        foreach (var block in blocks)
        {
            if (TranscriptJson.BlockType(block) != "tool_result") continue;
            events.Add(new ToolResultEvent(
                TranscriptJson.AsString(block?["tool_use_id"]) ?? "",
                PreviewOf(block?["content"]),
                block?["is_error"]?.GetValue<bool>() ?? false));
        }
        return events;
    }

    // tool_result content is a plain string in some records and a block array in others.
    private static string PreviewOf(JsonNode? content)
    {
        var text = TranscriptJson.AsString(content);
        if (text is null && content is JsonArray blocks)
            text = string.Join(" ", blocks
                .Where(b => TranscriptJson.BlockType(b) == "text")
                .Select(b => TranscriptJson.AsString(b?["text"]))
                .Where(t => !string.IsNullOrEmpty(t)));
        return ToolSummary.Clip(text ?? "");
    }

    private static IReadOnlyList<SessionEvent> ParseStreamEvent(JsonNode root)
    {
        var ev = root["event"];
        if (TranscriptJson.AsString(ev?["type"]) != "content_block_delta") return [];
        var delta = ev?["delta"];
        if (TranscriptJson.AsString(delta?["type"]) != "text_delta") return [];
        var text = TranscriptJson.AsString(delta?["text"]);
        return text is { Length: > 0 } ? [new TextDeltaEvent(text)] : [];
    }

    private static IReadOnlyList<SessionEvent> ParseControlRequest(JsonNode root)
    {
        var request = root["request"];
        if (TranscriptJson.AsString(request?["subtype"]) != "can_use_tool") return [];
        string? suggestedMode = null;
        if (request?["permission_suggestions"] is JsonArray suggestions)
            suggestedMode = suggestions
                .Where(s => TranscriptJson.AsString(s?["type"]) == "setMode")
                .Select(s => TranscriptJson.AsString(s?["mode"]))
                .FirstOrDefault(m => !string.IsNullOrEmpty(m));
        return
        [
            new PermissionRequestEvent(
                TranscriptJson.AsString(root["request_id"]) ?? "",
                TranscriptJson.AsString(request?["tool_name"]) ?? "tool",
                TranscriptJson.AsString(request?["description"]) ?? "",
                request?["input"]?.ToJsonString() ?? "{}",
                suggestedMode),
        ];
    }

    private static IReadOnlyList<SessionEvent> ParseControlResponse(JsonNode root)
    {
        // Only the set_permission_mode ack carries information the UI shows; other acks are ignored.
        var mode = TranscriptJson.AsString(root["response"]?["response"]?["mode"]);
        return mode is { Length: > 0 } ? [new ModeChangedEvent(mode)] : [];
    }

    private static IReadOnlyList<SessionEvent> ParseResult(JsonNode root)
    {
        var usage = root["usage"];
        return
        [
            new TurnResultEvent(
                root["is_error"]?.GetValue<bool>() ?? false,
                TranscriptJson.AsString(root["subtype"]) ?? "",
                AsDouble(root["total_cost_usd"]),
                TranscriptJson.AsLong(usage?["input_tokens"]),
                TranscriptJson.AsLong(usage?["output_tokens"]),
                TranscriptJson.AsLong(root["duration_ms"]),
                TranscriptJson.AsLong(usage?["cache_read_input_tokens"]),
                TranscriptJson.AsLong(usage?["cache_creation_input_tokens"])),
        ];
    }

    // Cost is a double but tolerate an integer serialisation, mirroring TranscriptJson.AsLong.
    private static double AsDouble(JsonNode? n)
    {
        if (n is null) return 0;
        try { return n.GetValue<double>(); }
        catch { try { return n.GetValue<long>(); } catch { return 0; } }
    }
}
