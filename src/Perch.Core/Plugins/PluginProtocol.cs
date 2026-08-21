namespace Perch.Plugins;

using System.Text.Json;
using System.Text.Json.Nodes;

/// <summary>
/// The newline-delimited-JSON wire format between Perch (host) and a plugin process. The host writes a
/// single <see cref="PluginRequest"/> line (one-shot) or an <c>init</c> then ticks (persistent); the
/// plugin writes zero or more response lines. Parsing plugin output is defensive — a blank or malformed
/// line is skipped, never fatal — because plugin stdout is untrusted third-party text.
/// </summary>
internal static class PluginProtocol
{
    private static readonly JsonSerializerOptions Json = new()
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>Serialises a host→plugin request to a single line (no embedded newlines).</summary>
    public static string Serialize(PluginRequest request)
    {
        var obj = new JsonObject
        {
            ["type"] = request.Type,
            ["perch"] = request.PerchVersion,
            ["grants"] = new JsonArray(request.Grants.Select(g => JsonValue.Create(g)).ToArray<JsonNode?>()),
        };
        if (request.Event != null) obj["event"] = request.Event;
        if (request.Context.Count > 0)
        {
            var ctx = new JsonObject();
            foreach (var kv in request.Context) ctx[kv.Key] = kv.Value;
            obj["context"] = ctx;
        }
        return obj.ToJsonString(Json);
    }

    /// <summary>Parses one line of plugin output into a message. Returns null for a blank line or any
    /// line that isn't a JSON object with a string <c>type</c> — the caller skips those.</summary>
    public static PluginMessage? ParseLine(string line)
    {
        if (string.IsNullOrWhiteSpace(line)) return null;

        JsonNode? node;
        try { node = JsonNode.Parse(line); }
        catch (JsonException) { return null; }

        if (node is not JsonObject obj) return null;
        if (obj["type"]?.GetValueKind() != JsonValueKind.String) return null;
        var type = obj["type"]!.GetValue<string>();

        return type switch
        {
            "ready" => new PluginReady(TryGlyph(obj["render"])),
            "render" => TryGlyph(obj["glyph"] ?? obj["render"]) is { } g ? new PluginRenderMessage(g) : null,
            "notify" => new PluginNotifyMessage(
                Str(obj["title"]) ?? "",
                Str(obj["body"]) ?? ""),
            "log" => new PluginLogMessage(Str(obj["level"]) ?? "info", Str(obj["message"]) ?? ""),
            _ => new PluginUnknownMessage(type),
        };
    }

    private static PluginGlyph? TryGlyph(JsonNode? node)
    {
        if (node is not JsonObject o) return null;
        var glyph = Str(o["glyph"]);
        var text = Str(o["text"]);
        var tooltip = Str(o["tooltip"]);
        // A render with neither a glyph nor text carries nothing paintable — treat as absent.
        if (string.IsNullOrEmpty(glyph) && string.IsNullOrEmpty(text)) return null;
        return new PluginGlyph(glyph ?? "", text ?? "", tooltip ?? "");
    }

    private static string? Str(JsonNode? n) =>
        n?.GetValueKind() == JsonValueKind.String ? n!.GetValue<string>() : null;
}

/// <summary>A host→plugin request. <see cref="Context"/> is already filtered to what the plugin's grants
/// allow (the host never puts <c>cwd</c> in here unless <c>read.cwd</c> was granted, etc.).</summary>
internal sealed record PluginRequest(
    string Type,
    string PerchVersion,
    IReadOnlyList<string> Grants,
    IReadOnlyDictionary<string, string> Context,
    string? Event = null)
{
    public const string PollType = "poll";
    public const string EventType = "event";
}

/// <summary>A paintable overlay contribution from a plugin: a short glyph (emoji/char), a short text label,
/// and a tooltip. The host clamps/sanitises these before rendering.</summary>
internal sealed record PluginGlyph(string Glyph, string Text, string Tooltip);

// ── plugin→host messages ────────────────────────────────────────────────────────────────
internal abstract record PluginMessage;
internal sealed record PluginReady(PluginGlyph? Render) : PluginMessage;
internal sealed record PluginRenderMessage(PluginGlyph Glyph) : PluginMessage;
internal sealed record PluginNotifyMessage(string Title, string Body) : PluginMessage;
internal sealed record PluginLogMessage(string Level, string Text) : PluginMessage;
internal sealed record PluginUnknownMessage(string Type) : PluginMessage;
