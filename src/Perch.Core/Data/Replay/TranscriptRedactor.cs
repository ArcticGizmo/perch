using System.Text.Json.Nodes;

namespace Perch.Data.Replay;

/// <summary>
/// Scrubs the <em>content</em> of a recording while preserving its <em>structure</em>, so a redacted
/// recording still drives the exact same parsers and state machine as the raw one — stats, burn-rate,
/// model mix, and the busy/idle/waiting transitions all stay realistic — but no message text, tool
/// argument, file path, title, or branch survives.
///
/// <para>What is kept (structural, not PII, and load-bearing for detection): record/block
/// <c>type</c>, <c>role</c>, <c>model</c>, the whole <c>usage</c> token-count object, tool
/// <c>name</c>s, the <c>id</c>/<c>tool_use_id</c> pairing, <c>timestamp</c>s, sidechain/agent
/// discriminators, and the control markers the <see cref="TranscriptReader"/> keys off — the
/// <c>[Request interrupted by user]</c> cancel marker, the <c>&lt;command-name&gt;</c> slash-command
/// prefix, and the <c>Set model to …</c> switch line. Everything else that is a string is replaced with
/// <see cref="ReplayFormat.RedactionToken"/>; numbers (the token counts) pass through untouched.</para>
///
/// Pure and defensive: a line that can't be parsed is returned verbatim (transcripts carry the odd
/// malformed trailing line, and the projector must ship it unchanged so the same defensive readers see it).
/// </summary>
internal static class TranscriptRedactor
{
    private const string Token = ReplayFormat.RedactionToken;

    // Keys whose (string) values are structural discriminators or non-PII signals the readers depend on,
    // so they pass through verbatim. Numbers (usage counts) are never touched regardless of key.
    private static readonly HashSet<string> TranscriptPreserveKeys = new(StringComparer.Ordinal)
    {
        "type", "role", "model", "usage", "id", "name", "tool_use_id", "toolUseId",
        "timestamp", "isSidechain", "isMeta", "isCompactSummary", "isApiErrorMessage",
        "taskKind", "agentType", "subagent_type", "spawnDepth", "stop_reason", "stop_sequence",
        "media_type", "uuid", "parentUuid",
    };

    // The subset of meta-sidecar keys that are safe to keep (an agent's human name / team name /
    // free-text description are redacted; its type, kind, colour, depth, model and spawning tool_use id
    // are not PII and drive the roster + tree reconstruction).
    private static readonly HashSet<string> MetaPreserveKeys = new(StringComparer.Ordinal)
    {
        "agentType", "taskKind", "color", "toolUseId", "spawnDepth", "permissionMode", "model",
    };

    /// <summary>Redacts one transcript JSONL line, rewriting any <c>cwd</c>/<c>gitBranch</c> onto the
    /// placeholder. Returns the line unchanged if it isn't parseable JSON.</summary>
    public static string RedactLine(string line, string placeholderCwd)
    {
        if (string.IsNullOrWhiteSpace(line))
            return line;
        JsonNode? node;
        try { node = JsonNode.Parse(line); }
        catch { return line; } // malformed/partial trailing line — ship verbatim
        if (node == null)
            return line;
        return (Redact(node, TranscriptPreserveKeys, placeholderCwd) ?? node).ToJsonString();
    }

    /// <summary>Redacts an <c>agent-*.meta.json</c> sidecar (description/name/teamName scrubbed, type /
    /// kind / colour / depth / model / spawn-id kept). Returns the input verbatim if unparseable.</summary>
    public static string RedactMeta(string json, string placeholderCwd)
    {
        JsonNode? node;
        try { node = JsonNode.Parse(json); }
        catch { return json; }
        if (node == null)
            return json;
        return (Redact(node, MetaPreserveKeys, placeholderCwd) ?? node).ToJsonString();
    }

    /// <summary>Redacts a <c>.note</c> sidecar's free text while keeping its JSON shape (see
    /// <see cref="SessionMonitor.SetNote"/>): the <c>text</c> is scrubbed, the <c>updatedAt</c> stamp kept.</summary>
    public static string RedactNote(string json)
    {
        try
        {
            if (JsonNode.Parse(json) is JsonObject obj)
            {
                if (obj.ContainsKey("text"))
                    obj["text"] = Token;
                return obj.ToJsonString();
            }
        }
        catch { /* not JSON — a hand-edited plain-text note */ }
        return Token; // plain-text note: replace wholesale
    }

    // Recursively rebuilds a node: preserve-keyed values pass through, cwd/gitBranch become placeholders,
    // every other string leaf collapses to the token (with the control markers below spared), and numbers
    // /bools survive so the token-count maths stays real.
    private static JsonNode? Redact(JsonNode? node, HashSet<string> preserve, string placeholderCwd)
    {
        switch (node)
        {
            case JsonObject obj:
                var result = new JsonObject();
                foreach (var (key, value) in obj)
                {
                    if (key is "cwd" or "originalCwd")
                        result[key] = placeholderCwd;
                    else if (key is "gitBranch")
                        result[key] = "main";
                    else if (preserve.Contains(key))
                        result[key] = value?.DeepClone();
                    else
                        result[key] = Redact(value, preserve, placeholderCwd);
                }
                return result;

            case JsonArray arr:
                var copy = new JsonArray();
                foreach (var item in arr)
                    copy.Add(Redact(item, preserve, placeholderCwd));
                return copy;

            case JsonValue value:
                // Only strings carry content; numbers/bools (notably usage token counts) pass through.
                if (value.TryGetValue<string>(out var s))
                    return JsonValue.Create(RedactText(s));
                return value.DeepClone();

            default:
                return node?.DeepClone();
        }
    }

    // Content-string redaction that spares the three control markers the state machine reads. The marker
    // text itself carries no user content, so keeping it leaks nothing while preserving replay fidelity.
    private static string RedactText(string s)
    {
        // Turn cancellation: preserve the canonical marker (and its tool-use variant) so a deliberate
        // cancel still suppresses the "done" alert during replay. See TranscriptReader interrupt logic.
        if (s.Contains("[Request interrupted by user", StringComparison.Ordinal))
            return s.Contains("for tool use", StringComparison.Ordinal)
                ? "[Request interrupted by user for tool use]"
                : "[Request interrupted by user]";

        // Slash-command echo: keep only the leading <command-name> prefix (the bare-command
        // discriminator); the command and its args are dropped.
        if (s.StartsWith("<command-name>", StringComparison.Ordinal))
            return $"<command-name>{Token}</command-name>";

        // Model-switch confirmation: the "Set model to …" line is what model-switch detection reads, and
        // model display names aren't PII, so keep it. Other command stdout is scrubbed.
        if (s.StartsWith("<local-command-stdout>", StringComparison.Ordinal))
            return s.Contains("Set model to", StringComparison.Ordinal)
                ? s
                : $"<local-command-stdout>{Token}</local-command-stdout>";

        return Token;
    }
}
