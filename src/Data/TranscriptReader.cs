using System.Text.Json;
using System.Text.Json.Nodes;
using Perch.Data;

namespace Perch.Data;

/// <summary>
/// Reads a session's most recent tool call from its transcript and turns it into a short,
/// human-friendly phrase ("Reading Foo.cs", "Running: npm test", "Editing Bar.cs").
///
/// The transcript lives at <c>~/.claude/projects/{enc-cwd}/{sessionId}.jsonl</c>. It is appended
/// live and can grow large, so we never read the whole file — only the <see cref="TailBytes"/>
/// tail is seeked and scanned. The most recent <c>tool_use</c> block in that tail wins.
///
/// Each derived value is memoised per transcript by (length, last-write) via <see cref="MtimeCache{T}"/>,
/// so a scan while a session is busy — during which the transcript doesn't change between consecutive
/// scans — costs a stat, not a parse. Every failure path returns the empty value; this is best-effort
/// and must never throw.
/// </summary>
internal sealed class TranscriptReader
{
    private const int TailBytes = 32 * 1024;

    private readonly MtimeCache<string?> _activity = new();
    private readonly MtimeCache<string?> _title = new();
    private readonly MtimeCache<(float? Fill, int Window)> _contextFill = new();
    private readonly MtimeCache<bool> _bareCommand = new();
    private readonly MtimeCache<IReadOnlyList<Artifact>> _artifacts = new();
    private readonly MtimeCache<StuckMetrics> _stuck = new();

    // How many of the most recent tool calls the failing-loop heuristic looks across. Tuned against
    // real transcripts (see the throwaway analysis behind this feature): with proper per-command
    // fingerprinting a window of 10 cleanly separates a genuine retry-the-same-failing-thing loop
    // from healthy iterative work (e.g. ten different edits to the same file).
    private const int LoopWindow = 10;

    /// <summary>
    /// Returns a friendly phrase describing the latest tool call in the session's transcript,
    /// or <c>null</c> if the transcript can't be located/read or holds no tool call.
    /// </summary>
    public string? GetActivity(string sessionId, string cwd)
    {
        if (string.IsNullOrEmpty(sessionId))
            return null;
        var path = TranscriptLocator.Resolve(sessionId, cwd);
        return path == null ? null : _activity.GetOrCompute(path, Parse, null);
    }

    /// <summary>
    /// True when the most recent meaningful turn in the session's transcript is a built-in slash
    /// command (e.g. <c>/clear</c>, <c>/model</c>, <c>/doctor</c>) that completed without the model
    /// doing any work — i.e. no assistant message has been appended since the command was invoked.
    ///
    /// This is the discriminator for the fast/interactive built-ins the user never wants flagged as
    /// "running/done": those commands flip the session busy->idle (or busy->waiting for the ones with
    /// a picker) for a fraction of a second but produce no assistant turn, whereas a real prompt — or
    /// a command that actually drives the model, like <c>/init</c> or a custom command — always leaves
    /// assistant records behind. Metadata trailers (<c>last-prompt</c>, <c>mode</c>, <c>ai-title</c>, …)
    /// are ignored; only <c>assistant</c> records and command invocations are weighed.
    ///
    /// Cached by (length, last-write) like the other readers, so a scan that doesn't change the
    /// transcript costs a stat, not a parse. Best-effort; never throws (returns false on any failure).
    /// </summary>
    public bool LastTurnWasBareCommand(string sessionId, string cwd)
    {
        if (string.IsNullOrEmpty(sessionId))
            return false;
        var path = TranscriptLocator.Resolve(sessionId, cwd);
        return path != null && _bareCommand.GetOrCompute(path, ParseBareCommand, false);
    }

    /// <summary>
    /// Returns the session's explicit name as set by Claude Code's built-in <c>/rename</c> command —
    /// a <c>custom-title</c> transcript record. Null when the transcript can't be located/read or was
    /// never renamed. The auto-generated <c>ai-title</c> is deliberately ignored.
    ///
    /// A <c>/rename</c> title may have been set once early on, so — like Claude Code itself — we look
    /// in the tail first (where a later rename lands) and fall back to the head.
    /// </summary>
    public string? GetTitle(string sessionId, string cwd)
    {
        if (string.IsNullOrEmpty(sessionId))
            return null;
        var path = TranscriptLocator.Resolve(sessionId, cwd);
        return path == null ? null : _title.GetOrCompute(path, ParseTitle, null);
    }

    /// <summary>
    /// Returns the session's context-window fill (0–1) and the resolved window size in tokens.
    /// Fill is null when no usage data is available. The window defaults to
    /// <see cref="ModelContext.DefaultWindow"/> when no <c>/model</c> command was found.
    /// Best-effort; never throws.
    /// </summary>
    public (float? Fill, int Window) GetContextFill(string sessionId, string cwd)
    {
        if (string.IsNullOrEmpty(sessionId))
            return (null, ModelContext.DefaultWindow);
        var path = TranscriptLocator.Resolve(sessionId, cwd);
        if (path == null)
            return (null, ModelContext.DefaultWindow);
        return _contextFill.GetOrCompute(path, p => ParseContextFill(p, cwd), (null, ModelContext.DefaultWindow));
    }

    /// <summary>
    /// Returns the web Artifacts this session has published to claude.ai, de-duplicated by URL and in
    /// the order they were first published. Empty when the transcript can't be located/read or holds
    /// no artifacts. Best-effort; never throws.
    /// </summary>
    public IReadOnlyList<Artifact> GetArtifacts(string sessionId, string cwd)
    {
        if (string.IsNullOrEmpty(sessionId))
            return [];
        var path = TranscriptLocator.Resolve(sessionId, cwd);
        return path == null ? [] : _artifacts.GetOrCompute(path, ParseArtifacts, []);
    }

    /// <summary>
    /// Reads raw "is this session spinning?" measurements from the transcript tail: the current
    /// trailing run of failed tool calls, and how repetitive (and how failing) the last
    /// <see cref="LoopWindow"/> calls are. Threshold-free — the caller decides what counts as stuck —
    /// so this stays memoised by (length, last-write) while sensitivity settings change freely.
    /// Returns <c>default</c> (all zeroes) when the transcript can't be located/read. Never throws.
    /// </summary>
    public StuckMetrics GetStuckMetrics(string sessionId, string cwd)
    {
        if (string.IsNullOrEmpty(sessionId))
            return default;
        var path = TranscriptLocator.Resolve(sessionId, cwd);
        return path == null ? default : _stuck.GetOrCompute(path, ParseStuck, default);
    }

    private static IReadOnlyList<Artifact> ParseArtifacts(string path)
    {
        // An Artifact can be published anywhere in the session, so we read the whole file — but it's
        // cheap: a substring pre-filter skips almost every line, and the result is cached by
        // length+mtime so this only re-runs when the transcript actually changed. Each publish leaves
        // one record whose toolUseResult.url is the hosted page; re-publishing reuses the URL, so we
        // de-dupe by URL (last title wins) while preserving first-seen order.
        var order = new List<string>();
        var titles = new Dictionary<string, string>();

        foreach (var line in TranscriptScan.ReadLines(path))
        {
            // Cheap pre-filter: only the publish result records carry the artifact URL stem.
            if (!line.Contains("code/artifact"))
                continue;

            try
            {
                var result = JsonNode.Parse(line)?["toolUseResult"];
                var url = result?["url"]?.GetValue<string>();
                if (string.IsNullOrEmpty(url) || !url.Contains("/code/artifact/"))
                    continue;

                var title = result?["title"]?.GetValue<string>();
                if (string.IsNullOrWhiteSpace(title))
                    title = "Untitled artifact";

                if (!titles.ContainsKey(url))
                    order.Add(url);
                titles[url] = title.Trim();
            }
            catch
            {
                // Malformed/partial line (transcripts are appended live) — skip it.
            }
        }

        if (order.Count == 0)
            return [];

        return order.Select(u => new Artifact(u, titles[u])).ToList();
    }

    private static (float? fill, int window) ParseContextFill(string path, string cwd)
    {
        // A /model switch can land anywhere in the transcript, and the most recent one wins — so unlike
        // the activity/title tail-scans we must read the whole file. It's cheap: a substring pre-filter
        // skips almost every line untouched (model records are rare, usage records parse only near the
        // end's worth of assistant turns), and the result is cached by length+mtime so this full pass
        // only re-runs when the transcript actually changed.
        long latestUsed = 0;
        string? latestDisplayName = null;

        foreach (var line in TranscriptScan.ReadLines(path))
        {
            if (line.Length == 0)
                continue;

            // /model confirmation: a user-type record whose content is the terminal output string
            // wrapped in <local-command-stdout>. The wrapper is the key discriminator — and it must be
            // at the *start* of the content: user messages that quote or mention a "Set model to" line
            // in their body carry the wrapper mid-string and must not be mistaken for a real switch.
            if (line.Contains("local-command-stdout") && line.Contains("Set model to"))
            {
                try
                {
                    var node = JsonNode.Parse(line);
                    if (node?["type"]?.GetValue<string>() == "user")
                    {
                        var raw = node["message"]?["content"]?.GetValue<string>();
                        if (raw != null && raw.StartsWith("<local-command-stdout>"))
                        {
                            var dn = ModelContext.ParseDisplayName(raw);
                            if (dn != null)
                                latestDisplayName = dn;
                        }
                    }
                }
                catch { }
            }

            if (line.Contains("\"usage\""))
            {
                try
                {
                    var usage = JsonNode.Parse(line)?["message"]?["usage"];
                    if (usage != null)
                    {
                        long total = TranscriptJson.AsLong(usage["input_tokens"]) + TranscriptJson.AsLong(usage["cache_read_input_tokens"]);
                        if (total > 0)
                            latestUsed = total;
                    }
                }
                catch { }
            }
        }

        // A /model confirmation in the transcript is authoritative (the user explicitly switched, and
        // the most recent one wins). Lacking one, the session is running the configured default model —
        // whose transcript message.model field can't reveal whether it's the 200k or 1M variant — so we
        // fall back to the model id in settings.json, where the "[1m]" suffix makes that distinction.
        int window = latestDisplayName != null
            ? ModelContext.WindowFor(latestDisplayName)
            : ModelContext.WindowForConfiguredModel(ReadConfiguredModel(cwd));

        if (latestUsed == 0)
            return (null, window);

        return (Math.Clamp((float)latestUsed / window, 0f, 1f), window);
    }

    private static readonly JsonDocumentOptions JsonLeniency = new()
    {
        AllowTrailingCommas = true,
        CommentHandling = JsonCommentHandling.Skip,
    };

    /// <summary>
    /// Reads the configured default <c>model</c> from Claude Code's settings, in the same precedence
    /// Claude Code applies: project-local (<c>.claude/settings.local.json</c>) over project
    /// (<c>.claude/settings.json</c>) over user (<c>~/.claude/settings.json</c>). The first file that
    /// carries a non-blank <c>model</c> wins. Returns null when none do (e.g. the model is inherited
    /// from a managed/enterprise layer we don't read), which the caller maps to the default window.
    /// Best-effort; never throws.
    /// </summary>
    private static string? ReadConfiguredModel(string cwd)
    {
        var candidates = new List<string>();
        if (!string.IsNullOrEmpty(cwd))
        {
            candidates.Add(Path.Combine(cwd, ".claude", "settings.local.json"));
            candidates.Add(Path.Combine(cwd, ".claude", "settings.json"));
        }
        candidates.Add(ClaudePaths.UserSettingsFile);

        foreach (var path in candidates)
        {
            var model = ReadModelField(path);
            if (model != null)
                return model;
        }

        return null;
    }

    // Reads the top-level "model" string from one settings file, or null if absent/blank/unreadable.
    private static string? ReadModelField(string path)
    {
        try
        {
            if (!File.Exists(path))
                return null;

            using var doc = JsonDocument.Parse(File.ReadAllText(path), JsonLeniency);
            if (doc.RootElement.TryGetProperty("model", out var m)
                && m.ValueKind == JsonValueKind.String)
            {
                var s = m.GetString();
                return string.IsNullOrWhiteSpace(s) ? null : s.Trim();
            }
        }
        catch { }

        return null;
    }

    private static string? Parse(string path)
    {
        // Lines are chronological, so the last tool_use we see is the most recent. Track only the
        // friendly phrase, overwriting as newer tool calls appear.
        string? latest = null;

        foreach (var line in TranscriptScan.ReadTailLines(path, TailBytes))
        {
            // Cheap pre-filter: only assistant lines carrying a tool_use are worth parsing.
            if (!line.Contains("tool_use"))
                continue;

            try
            {
                if (TranscriptJson.ContentArray(JsonNode.Parse(line)) is not { } content)
                    continue;

                foreach (var block in content)
                {
                    if (TranscriptJson.BlockType(block) != "tool_use")
                        continue;
                    var name = block!["name"]?.GetValue<string>();
                    if (string.IsNullOrEmpty(name))
                        continue;
                    latest = ToolSummary.Describe(name, block["input"]);
                }
            }
            catch
            {
                // Malformed/partial line (transcripts are appended live) — skip it.
            }
        }

        return latest;
    }

    private static StuckMetrics ParseStuck(string path)
    {
        // Walk the tail collecting every tool_use (id + a fingerprint + a friendly label) and every
        // tool_result's pass/fail keyed by tool_use_id. Records are chronological, and tool_use lives
        // in assistant records while tool_result lives in the following user records, so the two
        // streams interleave naturally as we go.
        var uses = new List<(string Id, string Fingerprint, string Label)>();
        var errorById = new Dictionary<string, bool>();
        int trailingErrors = 0;   // consecutive failed results ending at the latest result

        foreach (var line in TranscriptScan.ReadTailLines(path, TailBytes))
        {
            // Cheap pre-filter: only lines that could carry a tool call or its result are worth parsing.
            if (!line.Contains("tool_use") && !line.Contains("tool_result"))
                continue;

            try
            {
                if (TranscriptJson.ContentArray(JsonNode.Parse(line)) is not { } content)
                    continue;

                foreach (var block in content)
                {
                    var type = TranscriptJson.BlockType(block);
                    if (type == "tool_use")
                    {
                        var name = block!["name"]?.GetValue<string>();
                        var id = block["id"]?.GetValue<string>();
                        if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(id))
                            continue;
                        var input = block["input"];
                        uses.Add((id, Fingerprint(name, input), ToolSummary.Describe(name, input)));
                    }
                    else if (type == "tool_result")
                    {
                        bool isError = block!["is_error"]?.GetValue<bool>() == true;
                        var rid = block["tool_use_id"]?.GetValue<string>();
                        if (rid != null)
                            errorById[rid] = isError;
                        // A success anywhere breaks the trailing streak; a failure extends it.
                        trailingErrors = isError ? trailingErrors + 1 : 0;
                    }
                }
            }
            catch
            {
                // Malformed/partial line (transcripts are appended live) — skip it.
            }
        }

        // Loop signal: over the last LoopWindow calls, find the single most-repeated fingerprint and
        // count how many of its occurrences errored. "Same thing, over and over, and failing" scores
        // high on both; healthy iterative work (distinct edits to one file) does not, because each
        // edit's content makes a distinct fingerprint.
        int loopRepeat = 0, loopErrors = 0;
        string? loopLabel = null;
        if (uses.Count > 0)
        {
            int from = Math.Max(0, uses.Count - LoopWindow);
            var best = uses.GetRange(from, uses.Count - from)
                .GroupBy(u => u.Fingerprint)
                .OrderByDescending(g => g.Count())
                .First();
            loopRepeat = best.Count();
            loopErrors = best.Count(u => errorById.TryGetValue(u.Id, out var e) && e);
            loopLabel = best.First().Label;
        }

        return new StuckMetrics(trailingErrors, loopRepeat, loopErrors, loopLabel);
    }

    // A coarse fingerprint of a tool call for loop detection: the same fingerprint twice means "the
    // same action". The discriminator is per-tool — for shell commands it's the command text; for
    // edits/writes it's the file plus the actual change (so re-applying the identical edit looks like
    // a loop but editing the same file differently does not); for reads/searches it's the target.
    private static string Fingerprint(string name, JsonNode? input)
    {
        string? Str(string key)
        {
            try { return input?[key]?.GetValue<string>(); }
            catch { return null; }
        }

        switch (name)
        {
            case "Bash":
            case "PowerShell":
                return $"{name}|{Str("command")}";
            case "Edit":
            case "MultiEdit":
                return $"{name}|{Str("file_path")}|{Str("old_string")}";
            case "Write":
                return $"Write|{Str("file_path")}|{Str("content")}";
            case "Read":
                return $"Read|{Str("file_path")}";
            case "Grep":
            case "Glob":
                return $"{name}|{Str("pattern")}";
            case "WebFetch":
                return $"WebFetch|{Str("url")}";
            case "WebSearch":
                return $"WebSearch|{Str("query")}";
            default:
                // Anything else (Task/Agent, MCP tools, …): the whole input distinguishes calls.
                return $"{name}|{input?.ToJsonString()}";
        }
    }

    private static bool ParseBareCommand(string path)
    {
        // Walk chronologically and remember only which of the two meaningful kinds came last: an
        // assistant turn (the model did work) or a command invocation (the user ran a slash command).
        // Everything else — plain user prompts, command stdout, mode/title/snapshot metadata — is
        // skipped so the trailing metadata records every transcript ends with don't muddy the verdict.
        // (If the tail spans a full window there is necessarily an assistant turn within it, so a bare
        // command older than the window can't be the latest meaningful turn anyway.)
        bool lastWasCommand = false;

        foreach (var line in TranscriptScan.ReadTailLines(path, TailBytes))
        {
            bool maybeAssistant = line.Contains("\"type\":\"assistant\"");
            bool maybeCommand = line.Contains("command-name");
            if (!maybeAssistant && !maybeCommand)
                continue;

            try
            {
                var node = JsonNode.Parse(line);
                if (node?["type"]?.GetValue<string>() == "assistant")
                {
                    lastWasCommand = false;
                    continue;
                }

                // A command invocation is recorded either as a system/local_command record (content
                // is the command string) or as a user record whose message content is that string.
                // Either way the content starts with "<command-name>"; requiring the prefix at the
                // very start keeps a user message that merely quotes the tag from being mistaken for
                // a real invocation.
                if (ContentString(node)?.StartsWith("<command-name>") == true)
                    lastWasCommand = true;
            }
            catch
            {
                // Malformed/partial line (transcripts are appended live) — skip it.
            }
        }

        return lastWasCommand;
    }

    // Pulls a record's textual content out of either shape we see for command invocations:
    // a top-level "content" string (system/local_command) or "message.content" string (user).
    // Returns null when content is absent or an array (e.g. an assistant block list).
    private static string? ContentString(JsonNode? node)
    {
        if (node?["content"] is JsonValue direct)
        {
            try { return direct.GetValue<string>(); } catch { }
        }
        if (node?["message"]?["content"] is JsonValue msg)
        {
            try { return msg.GetValue<string>(); } catch { }
        }
        return null;
    }

    private static string? ParseTitle(string path)
    {
        // Scan the tail first — a later /rename lands here. If none and the file spans more than one
        // window, a title set once early may be in the head, so look there before giving up.
        long len = new FileInfo(path).Length;
        return ScanWindowForTitle(TranscriptScan.ReadLinesFrom(path, Math.Max(0, len - TailBytes)))
            ?? (len > TailBytes ? ScanWindowForTitle(TranscriptScan.ReadLinesFrom(path, 0)) : null);
    }

    // Returns the last custom-title (the /rename name) record in the given lines, or null.
    private static string? ScanWindowForTitle(IEnumerable<string> lines)
    {
        string? custom = null;

        foreach (var line in lines)
        {
            // Cheap pre-filter: the /rename record type is "custom-title".
            if (!line.Contains("custom-title"))
                continue;

            try
            {
                var node = JsonNode.Parse(line);
                if (node?["type"]?.GetValue<string>() == "custom-title")
                    custom = node["customTitle"]?.GetValue<string>() ?? custom;
            }
            catch
            {
                // Malformed/partial line — skip it.
            }
        }

        return custom;
    }
}
