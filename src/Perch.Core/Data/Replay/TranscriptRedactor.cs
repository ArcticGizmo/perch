using System.Text.Json.Nodes;

namespace Perch.Data.Replay;

/// <summary>
/// Scrubs the <em>content</em> of a recording while preserving its <em>structure</em>, so a redacted
/// recording still drives the exact same parsers and state machine as the raw one — stats, burn-rate,
/// model mix, and the busy/idle/waiting transitions all stay realistic — but no message text, tool
/// argument, file path, title, or branch survives.
///
/// <para>What is kept (structural, not PII, and load-bearing for detection): record/block
/// <c>type</c>, <c>role</c>, <c>model</c>, tool <c>name</c>s, the <c>id</c>/<c>tool_use_id</c> pairing,
/// <c>timestamp</c>s, sidechain/agent discriminators, a Task's <c>subagent_type</c>, and the control
/// markers the <see cref="TranscriptReader"/> keys off — the <c>[Request interrupted by user]</c> cancel
/// marker, the <c>&lt;command-name&gt;</c> slash-command prefix, and the model named by a <c>Set model to …</c>
/// / <c>Kept model as …</c> line (rebuilt around the extracted name; the rest of that stdout is dropped).
/// Everything else that is a string is replaced with <see cref="ReplayFormat.RedactionToken"/>; numbers
/// (the <c>usage</c> token counts) and bools pass through untouched.</para>
///
/// <para>The preserved keys are kept only where the readers look for them — the record, its
/// <c>message</c>, that message's content blocks and an image block's <c>source</c> — and only when the
/// value is a scalar (review fixes CP15). Tool inputs, tool results and <c>toolUseResult</c> are arbitrary
/// JSON, so a <c>{"name":"Customer A"}</c> in one is scrubbed like any other string.</para>
///
/// Pure and defensive: a line that can't be parsed (the partially written trailing line of a live
/// transcript) becomes the redaction token, which the readers skip as malformed just as they would the
/// original.
/// </summary>
internal static class TranscriptRedactor
{
    private const string Token = ReplayFormat.RedactionToken;

    // Keys whose scalar values are structural discriminators or non-PII signals the readers depend on,
    // so they pass through verbatim — at a structural level only (see Level).
    private static readonly HashSet<string> TranscriptPreserveKeys = new(StringComparer.Ordinal)
    {
        "type", "role", "model", "id", "name", "tool_use_id", "toolUseId",
        "timestamp", "isSidechain", "isMeta", "isCompactSummary", "isApiErrorMessage",
        "taskKind", "agentType", "spawnDepth", "stop_reason", "stop_sequence",
        "media_type", "uuid", "parentUuid",
    };

    // Inside a tool_use input only the Task's agent type survives (the roster reads it).
    private static readonly HashSet<string> ToolInputPreserveKeys = new(StringComparer.Ordinal) { "subagent_type" };

    // Where in a record a node sits. Preserve keys apply at every level but Payload; each level names the
    // one child that stays structural.
    private enum Level { Record, Message, Block, Source, ToolInput, Payload }

    // The subset of meta-sidecar keys that are safe to keep (an agent's human name / team name /
    // free-text description are redacted; its type, kind, colour, depth, model and spawning tool_use id
    // are not PII and drive the roster + tree reconstruction).
    private static readonly HashSet<string> MetaPreserveKeys = new(StringComparer.Ordinal)
    {
        "agentType", "taskKind", "color", "toolUseId", "spawnDepth", "permissionMode", "model",
    };

    /// <summary>Redacts one transcript JSONL line, rewriting any <c>cwd</c>/<c>gitBranch</c> onto the
    /// placeholder. A line that isn't parseable JSON becomes the redaction token.</summary>
    public static string RedactLine(string line, string placeholderCwd)
    {
        if (string.IsNullOrWhiteSpace(line))
            return line;
        JsonNode? node;
        try { node = JsonNode.Parse(line); }
        catch { return Token; } // malformed/partial trailing line — its text could be anything
        if (node == null)
            return line;
        return (Redact(node, Level.Record, TranscriptPreserveKeys, placeholderCwd) ?? node).ToJsonString();
    }

    /// <summary>Redacts an <c>agent-*.meta.json</c> sidecar (description/name/teamName scrubbed, type /
    /// kind / colour / depth / model / spawn-id kept at the top level). Unparseable → the redaction token.</summary>
    public static string RedactMeta(string json, string placeholderCwd)
    {
        JsonNode? node;
        try { node = JsonNode.Parse(json); }
        catch { return Token; }
        if (node == null)
            return json;
        return (Redact(node, Level.Record, MetaPreserveKeys, placeholderCwd) ?? node).ToJsonString();
    }

    /// <summary>Redacts a note sidecar's free text while keeping its JSON shape (see
    /// <see cref="SessionMonitor.SetProjectNote"/>): the <c>text</c> is scrubbed, the <c>updatedAt</c> stamp
    /// kept. Applies to the <c>project.note</c> sidecar and legacy <c>{sessionId}.note</c> files in old
    /// recordings.</summary>
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

    // Recursively rebuilds a node: preserve-keyed scalars at a structural level pass through, cwd/gitBranch
    // become placeholders, every other string leaf collapses to the token (with the control markers below
    // spared), and numbers/bools survive so the token-count maths stays real.
    private static JsonNode? Redact(JsonNode? node, Level level, HashSet<string> preserve, string placeholderCwd)
    {
        switch (node)
        {
            case JsonObject obj:
                var keep = level switch
                {
                    Level.Payload => null,
                    Level.ToolInput => ToolInputPreserveKeys,
                    _ => preserve,
                };
                var result = new JsonObject();
                foreach (var (key, value) in obj)
                {
                    if (key is "cwd" or "originalCwd")
                        result[key] = placeholderCwd;
                    else if (key is "gitBranch")
                        result[key] = "main";
                    else if (value is JsonValue && keep?.Contains(key) == true)
                        result[key] = value.DeepClone();
                    else
                        result[key] = Redact(value, ChildLevel(level, key), preserve, placeholderCwd);
                }
                return result;

            case JsonArray arr:
                // An array sits at its key's level: message.content's items are content blocks.
                var copy = new JsonArray();
                foreach (var item in arr)
                    copy.Add(Redact(item, level, preserve, placeholderCwd));
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

    // The structural path the readers use: record → message → content blocks → an image's source, plus a
    // tool_use block's input. Anything else (toolUseResult, a tool_result's content, a tool input's nested
    // values, progress data…) is payload.
    private static Level ChildLevel(Level parent, string key) => (parent, key) switch
    {
        (Level.Record, "message") => Level.Message,
        (Level.Message, "content") => Level.Block,
        (Level.Block, "source") => Level.Source,
        (Level.Block, "input") => Level.ToolInput,
        _ => Level.Payload,
    };

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

        // Model-switch confirmation: model detection reads the name out of a "Set model to …" / "Kept model
        // as …" stdout. Rebuild the line around just that name (model display names aren't PII); the rest of
        // the stdout, and any other command's, is scrubbed.
        if (s.StartsWith("<local-command-stdout>", StringComparison.Ordinal))
            return ModelLine(s) ?? $"<local-command-stdout>{Token}</local-command-stdout>";

        return Token;
    }

    // A display name as /model reports one ("Opus 4.8 (1M context)", "claude-opus-5[1m]"), nothing freer.
    private static readonly System.Text.RegularExpressions.Regex ModelNameShape =
        new(@"^[\p{L}\p{N} .,:()\[\]/_+·-]{1,64}$", System.Text.RegularExpressions.RegexOptions.Compiled);

    private static string? ModelLine(string s)
    {
        const string open = "<local-command-stdout>", close = "</local-command-stdout>";
        var inner = s[open.Length..];
        int end = inner.IndexOf(close, StringComparison.Ordinal);
        if (end >= 0) inner = inner[..end];
        if (!ModelContext.LooksLikeModelLine(inner) || ModelContext.ParseDisplayName(inner) is not { } name
            || !ModelNameShape.IsMatch(name))
            return null;
        var verb = inner.Contains("Set model to", StringComparison.Ordinal) ? "Set model to" : "Kept model as";
        return $"<local-command-stdout>{verb} \u001b[1m{name}\u001b[22m</local-command-stdout>";
    }
}
