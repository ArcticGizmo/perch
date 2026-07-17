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
    private readonly MtimeCache<double?> _burnRate = new();
    private readonly MtimeCache<bool> _bareCommand = new();
    private readonly MtimeCache<bool> _interrupted = new();
    private readonly MtimeCache<bool> _awaitingAssistant = new();
    private readonly MtimeCache<IReadOnlyList<Artifact>> _artifacts = new();
    private readonly MtimeCache<IReadOnlyList<TaskItem>> _tasks = new();
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
    /// True when the session's latest meaningful turn ended because the user cancelled it (Esc /
    /// Ctrl+C) rather than because the model finished. Claude Code appends a synthetic user record —
    /// <c>[Request interrupted by user]</c> or <c>[Request interrupted by user for tool use]</c> — when
    /// a turn is interrupted; a normal turn ends with an assistant record instead. This is the
    /// discriminator that keeps a deliberate cancel from raising a "done" alert: on the busy->idle edge
    /// the caller stays plain idle when this is true.
    ///
    /// If the user resumed afterwards (any assistant record was appended after the marker) it is no
    /// longer the latest turn and this returns false. Cached by (length, last-write) like the other
    /// readers. Best-effort; never throws (returns false on any failure). The transcript format is
    /// undocumented and may change between Claude Code releases, so a miss simply falls back to the
    /// existing "done" behaviour.
    /// </summary>
    public bool LastTurnWasInterrupted(string sessionId, string cwd)
    {
        if (string.IsNullOrEmpty(sessionId))
            return false;
        var path = TranscriptLocator.Resolve(sessionId, cwd);
        return path != null && _interrupted.GetOrCompute(path, ParseInterrupted, false);
    }

    /// <summary>
    /// True when the session's transcript tail ends mid-turn — i.e. the model still owes an assistant
    /// reply. That is the case either when the last meaningful record is a user record (a prompt or a
    /// <c>tool_result</c> the model hasn't answered yet — notably a sub-agent's result just handed back
    /// to the parent) or an assistant record whose last block is a <c>tool_use</c> awaiting its result.
    /// A <em>completed</em> turn instead ends in an assistant record with no pending tool call.
    ///
    /// This is the discriminator that keeps a sub-agent's return from raising a premature "done": a
    /// sub-agent finishing hands its result back to the parent as a <c>tool_result</c>, and reading that
    /// (often large) result back to the model to produce the next turn can take far longer than the
    /// sub-agent-completion grace window — during which the session file still reads idle. If the tail
    /// shows the parent mid-turn, the session hasn't finished; it's just slow to produce its first token.
    ///
    /// Metadata trailers (<c>attachment</c>, <c>file-history</c>, <c>queue-operation</c>, <c>system</c>,
    /// <c>summary</c>) are ignored; only <c>assistant</c>/<c>user</c> records are weighed. Mirrors
    /// <see cref="SubAgentReader"/>'s own working/idle classification. Cached by (length, last-write)
    /// like the other readers. Best-effort; never throws (returns false on any failure).
    /// </summary>
    public bool LastTurnAwaitingAssistant(string sessionId, string cwd)
    {
        if (string.IsNullOrEmpty(sessionId))
            return false;
        var path = TranscriptLocator.Resolve(sessionId, cwd);
        return path != null && _awaitingAssistant.GetOrCompute(path, ParseAwaitingAssistant, false);
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
    /// Returns the session's current token burn rate in tokens per minute — measured over the most
    /// recent continuous burst of assistant turns in the transcript tail — or null when there isn't
    /// enough recent activity to compute one (fewer than two turns, or a long idle gap before the
    /// latest turn). Each turn counts only its fresh tokens (new input + freshly cached input +
    /// output); the cache re-read is excluded so a long-context session's rate isn't dominated by the
    /// whole context being re-billed each turn. Best-effort; never throws.
    /// </summary>
    public double? GetBurnRate(string sessionId, string cwd)
    {
        if (string.IsNullOrEmpty(sessionId))
            return null;
        var path = TranscriptLocator.Resolve(sessionId, cwd);
        return path == null ? null : _burnRate.GetOrCompute(path, ParseBurnRate, null);
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

    /// <summary>
    /// Returns the <em>freshest batch</em> of the session's native task checklist — the run of tasks
    /// Claude built with <c>TaskCreate</c>/<c>TaskUpdate</c> since the most recent user prompt — with
    /// each task's current status, in creation order. Returns empty when the transcript can't be
    /// located/read, the session created no tasks, or the list is stale: the user has prompted again
    /// since the last task touch, so the checklist belongs to a unit of work that's been superseded.
    /// Best-effort; never throws.
    /// </summary>
    public IReadOnlyList<TaskItem> GetTasks(string sessionId, string cwd)
    {
        if (string.IsNullOrEmpty(sessionId))
            return [];
        var path = TranscriptLocator.Resolve(sessionId, cwd);
        return path == null ? [] : _tasks.GetOrCompute(path, ParseTasks, []);
    }

    private static IReadOnlyList<TaskItem> ParseTasks(string path)
    {
        // Claude Code builds its checklist with the TaskCreate tool and advances it with TaskUpdate;
        // there is no durable task file on disk, so we reconstruct the list by replaying those calls.
        // Two refinements keep the overlay honest:
        //   • Freshest batch — a task list belongs to the prompt that spawned it. When a genuine user
        //     prompt is followed by a new TaskCreate, that starts a new batch and we drop the previous
        //     one, so a brand-new plan reads as a fresh list rather than appending to old, done work.
        //   • Staleness — if the user has prompted *since* the last task touch (with no new tasks), the
        //     checklist has been abandoned, and we surface nothing rather than a lingering "5/5".
        // Ids are session-monotonic: TaskCreate carries no id, but the k-th create has id k and that's
        // what TaskUpdate.taskId references, so we key by a running counter (which keeps advancing
        // across batches) rather than list position — otherwise a second batch's ids wouldn't resolve.
        // A task can be created early in a long session, so we read the whole file; it's cheap (the
        // substring pre-filter skips almost every line, and the result is cached by length+mtime).
        var byId = new Dictionary<int, (string Subject, string ActiveForm, TaskState State)>();
        var batchIds = new List<int>();         // ids of the current batch, in creation order
        int nextId = 1;                         // session-monotonic id counter (never reset)
        bool promptSinceTask = false;           // a genuine user prompt has arrived since the last create
        DateTime? lastTaskTs = null, lastPromptTs = null;

        foreach (var line in TranscriptScan.ReadLines(path))
        {
            // Cheap pre-filter: the task tool calls, plus genuine user prompts (a user record that
            // isn't a tool result or an assistant tool_use line) — those drive batching and staleness.
            bool maybeTask   = line.Contains("TaskCreate") || line.Contains("TaskUpdate");
            bool maybePrompt = line.Contains("\"type\":\"user\"") && !line.Contains("tool_result") && !line.Contains("tool_use");
            if (!maybeTask && !maybePrompt)
                continue;

            try
            {
                var node = JsonNode.Parse(line);
                DateTime? ts = null;
                try { ts = TranscriptJson.ParseTimestamp(node?["timestamp"]?.GetValue<string>()); }
                catch { }

                if (maybePrompt && IsGenuineUserPrompt(node))
                {
                    promptSinceTask = true;
                    if (ts is { } pt && (lastPromptTs is null || pt > lastPromptTs))
                        lastPromptTs = pt;
                    continue;
                }

                if (TranscriptJson.ContentArray(node) is not { } content)
                    continue;

                foreach (var block in content)
                {
                    if (TranscriptJson.BlockType(block) != "tool_use")
                        continue;
                    var name = block!["name"]?.GetValue<string>();
                    var input = block["input"];

                    if (name == "TaskCreate")
                    {
                        int id = nextId++;
                        // A create after a fresh prompt opens a new batch — drop the previous one.
                        if (promptSinceTask)
                        {
                            batchIds.Clear();
                            byId.Clear();
                            promptSinceTask = false;
                        }
                        var subject = input?["subject"]?.GetValue<string>()?.Trim() ?? "";
                        var activeForm = input?["activeForm"]?.GetValue<string>()?.Trim() ?? "";
                        byId[id] = (subject, activeForm, TaskState.Pending);
                        batchIds.Add(id);
                        if (ts is { } ct) lastTaskTs = ct;
                    }
                    else if (name == "TaskUpdate")
                    {
                        // taskId/status come through as JSON strings; ToString() is robust whether the
                        // id is encoded as a string ("1") or a bare number, where GetValue<string> throws.
                        var idStr = input?["taskId"]?.ToString();
                        var status = input?["status"]?.ToString();
                        // Only updates to a task in the current batch matter; ids from a dropped batch
                        // are absent from byId and harmlessly ignored.
                        if (int.TryParse(idStr, out int id) && byId.TryGetValue(id, out var cur))
                        {
                            TaskState? state = status switch
                            {
                                "in_progress" => TaskState.InProgress,
                                "completed"   => TaskState.Completed,
                                "pending"     => TaskState.Pending,
                                _             => null,
                            };
                            if (state is { } s)
                                byId[id] = (cur.Subject, cur.ActiveForm, s);
                        }
                        if (ts is { } ut) lastTaskTs = ut;
                    }
                }
            }
            catch
            {
                // Malformed/partial line (transcripts are appended live) — skip it.
            }
        }

        if (batchIds.Count == 0)
            return [];

        // Stale: the user moved on after the batch's last task touch, so the checklist is abandoned.
        if (lastPromptTs is { } p && (lastTaskTs is null || p > lastTaskTs))
            return [];

        return batchIds.Select(id => byId[id])
                       .Select(t => new TaskItem(t.Subject, t.ActiveForm, t.State))
                       .ToList();
    }

    // True for a real typed user prompt: a "user" record whose message content is plain text — not a
    // tool result, not a slash-command echo (<command-name>) or its stdout (<local-command-stdout>).
    // These mark the boundary between units of work (and thus task batches).
    private static bool IsGenuineUserPrompt(JsonNode? node)
    {
        if (node?["type"]?.GetValue<string>() != "user")
            return false;

        var content = node["message"]?["content"];
        if (content is JsonValue value)
        {
            try
            {
                var s = value.GetValue<string>();
                return !string.IsNullOrEmpty(s)
                    && !s.StartsWith("<command-name>")
                    && !s.StartsWith("<local-command-stdout>");
            }
            catch { return false; }
        }

        // Array content: a genuine prompt carries a text block; a tool-result turn carries tool_result.
        if (content is JsonArray arr)
        {
            bool hasText = false;
            foreach (var b in arr)
            {
                var bt = TranscriptJson.BlockType(b);
                if (bt == "tool_result")
                    return false;
                if (bt == "text")
                    hasText = true;
            }
            return hasText;
        }

        return false;
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
        string? latestModelId = null;

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
                    var message = JsonNode.Parse(line)?["message"];

                    // The running model id (e.g. "claude-opus-4-8"). Ambiguous about 200k vs 1M, but it's
                    // the only record of what model is actually answering when the session never ran
                    // /model and settings.json carries no "model" — the common case. Most recent wins.
                    var model = message?["model"]?.GetValue<string>();
                    if (!string.IsNullOrEmpty(model))
                        latestModelId = model;

                    var usage = message?["usage"];
                    if (usage != null)
                    {
                        // The prompt's true size is all three input buckets summed. Omitting
                        // cache_creation badly under-counts right after a /model switch (or any
                        // cache-invalidating event): the switch resets the prompt cache, so
                        // cache_read collapses to 0 and the whole live context lands in
                        // cache_creation. Steady-state turns keep cache_creation small, so this
                        // only mattered visibly once model switches entered the picture.
                        long total = TranscriptJson.AsLong(usage["input_tokens"])
                                   + TranscriptJson.AsLong(usage["cache_read_input_tokens"])
                                   + TranscriptJson.AsLong(usage["cache_creation_input_tokens"]);
                        if (total > 0)
                            latestUsed = total;
                    }
                }
                catch { }
            }
        }

        // A /model confirmation in the transcript is authoritative (the user explicitly switched, and the
        // most recent one wins). Lacking one, resolve from the model id: the transcript's running
        // message.model first (what's actually answering), then the settings.json default. message.model
        // can't reveal 200k vs 1M for models where that's an opt-in, so WindowForConfiguredModel applies
        // the "[1m]"/family assumptions (e.g. Opus 4.x is treated as 1M).
        int window = latestDisplayName != null
            ? ModelContext.WindowFor(latestDisplayName)
            : ModelContext.WindowForConfiguredModel(latestModelId ?? ReadConfiguredModel(cwd));

        if (latestUsed == 0)
            return (null, window);

        return (Math.Clamp((float)latestUsed / window, 0f, 1f), window);
    }

    // How far apart two assistant turns can be and still count as one continuous burst of work.
    // A larger gap (a pause, a permission wait, the user stepping away) starts a fresh burst, so the
    // rate reflects the session's *current* pace rather than being diluted by idle time between turns.
    private static readonly TimeSpan BurstGap = TimeSpan.FromMinutes(2);

    private static double? ParseBurnRate(string path)
    {
        // Each assistant turn's usage record carries the tokens that turn cost (all input buckets +
        // output) and a timestamp. Only recent activity is relevant, so we read the tail, collect
        // (timestamp, tokens) for every turn in it, then measure the rate over the most recent
        // continuous burst — the run of turns ending at the latest one with no BurstGap-sized gap.
        var turns = new List<(DateTime Ts, long Tokens)>();

        foreach (var line in TranscriptScan.ReadTailLines(path, TailBytes))
        {
            // Cheap pre-filter: only assistant usage records carry the per-turn token counts.
            if (!line.Contains("\"usage\""))
                continue;

            try
            {
                var node = JsonNode.Parse(line);
                var usage = node?["message"]?["usage"];
                if (usage == null)
                    continue;

                var ts = TranscriptJson.ParseTimestamp(node?["timestamp"]?.GetValue<string>());
                if (ts is not { } t)
                    continue;

                // Fresh tokens only — the cache re-read (cache_read_input_tokens) is deliberately
                // excluded: on a long-context session it dwarfs everything else and makes the rate
                // read absurdly high, so we count what the turn actually adds (new input, freshly
                // cached input, and generated output) rather than the whole context re-billed each turn.
                long tokens = TranscriptJson.AsLong(usage["input_tokens"])
                            + TranscriptJson.AsLong(usage["output_tokens"])
                            + TranscriptJson.AsLong(usage["cache_creation_input_tokens"]);
                if (tokens > 0)
                    turns.Add((t, tokens));
            }
            catch
            {
                // Malformed/partial line (transcripts are appended live) — skip it.
            }
        }

        if (turns.Count < 2)
            return null;

        // Records are chronological in the file, but don't rely on it — the rate maths needs order.
        turns.Sort((a, b) => a.Ts.CompareTo(b.Ts));

        // Walk back from the newest turn, extending the burst while consecutive turns stay close.
        int start = turns.Count - 1;
        while (start > 0 && turns[start].Ts - turns[start - 1].Ts <= BurstGap)
            start--;

        // The newest turn stands alone (a long gap precedes it): no live pace to report yet.
        if (start == turns.Count - 1)
            return null;

        double minutes = (turns[^1].Ts - turns[start].Ts).TotalMinutes;
        if (minutes <= 0)
            return null;

        // Tokens consumed across the burst's span: every turn after the first (each turn's cost is
        // attributed to the interval ending at its timestamp), divided by the elapsed minutes.
        long tokensInSpan = 0;
        for (int i = start + 1; i < turns.Count; i++)
            tokensInSpan += turns[i].Tokens;

        return tokensInSpan / minutes;
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

    // Marker text Claude Code writes into a synthetic user record when a turn is cancelled. Matches
    // both variants ("[Request interrupted by user]" and "…for tool use]") via the common prefix.
    private const string InterruptMarker = "[Request interrupted by user";

    private static bool ParseInterrupted(string path)
    {
        // Walk chronologically and remember only which of the two kinds came last: an assistant turn
        // (the model finished / did work) or the interrupt marker (the user cancelled). Everything
        // else — plain prompts, tool results, metadata trailers — is skipped. A normal completion ends
        // with an assistant turn; a cancelled one ends with the marker. If any assistant record follows
        // the marker the user resumed, so it's no longer the latest turn. (Same tail-window reasoning as
        // ParseBareCommand: a marker older than the window necessarily has an assistant turn after it.)
        bool lastWasInterrupt = false;

        foreach (var line in TranscriptScan.ReadTailLines(path, TailBytes))
        {
            bool maybeAssistant = line.Contains("\"type\":\"assistant\"");
            bool maybeInterrupt = line.Contains("Request interrupted by user");
            if (!maybeAssistant && !maybeInterrupt)
                continue;

            try
            {
                var node = JsonNode.Parse(line);
                var type = node?["type"]?.GetValue<string>();
                if (type == "assistant")
                {
                    lastWasInterrupt = false;
                    continue;
                }

                // Confirm the marker sits in a user record's message content — not, say, an assistant
                // block that merely quotes the phrase. Requiring type=="user" plus the marker text keeps
                // the cheap substring pre-filter from producing false positives.
                if (type == "user" && MessageTextContains(node, InterruptMarker))
                    lastWasInterrupt = true;
            }
            catch
            {
                // Malformed/partial line (transcripts are appended live) — skip it.
            }
        }

        return lastWasInterrupt;
    }

    private static bool ParseAwaitingAssistant(string path)
    {
        // Walk the tail chronologically; the verdict is decided by the last meaningful assistant/user
        // record. A user record (a prompt, or a tool_result the model hasn't answered — e.g. a sub-agent's
        // result just handed back) leaves the turn awaiting the model's reply. An assistant record ends
        // the turn only when it carries no pending tool_use; one that ends on a tool_use is still awaiting
        // that tool's result. Metadata trailers every transcript ends with are skipped so they don't muddy
        // the verdict. Same tail-window reasoning as ParseBareCommand/ParseInterrupted, and the same
        // working/idle definition SubAgentReader.Classify uses for a sub-agent's own transcript.
        bool sawTurn = false;
        bool awaiting = false;

        foreach (var line in TranscriptScan.ReadTailLines(path, TailBytes))
        {
            // Only assistant/user records carry the turn boundary; skip the rest without parsing.
            if (line.Length == 0 || (!line.Contains("assistant") && !line.Contains("user")))
                continue;

            JsonNode? node;
            try { node = JsonNode.Parse(line); }
            catch { continue; }

            var type = node?["type"]?.GetValue<string>();
            if (type == "user")
            {
                sawTurn = true;
                awaiting = true;
            }
            else if (type == "assistant")
            {
                sawTurn = true;
                bool hadToolUse = false;
                if (TranscriptJson.ContentArray(node) is { } content)
                    foreach (var block in content)
                        if (TranscriptJson.BlockType(block) == "tool_use")
                            hadToolUse = true;
                awaiting = hadToolUse;
            }
        }

        return sawTurn && awaiting;
    }

    // True when a user record's message content contains the given text, in either shape we see:
    // a plain string content, or an array of blocks whose "text" field carries it (the interrupt
    // marker lands in a text block). Best-effort; false on any absent/typed-wrong field.
    private static bool MessageTextContains(JsonNode? node, string needle)
    {
        var content = node?["message"]?["content"];
        if (content is JsonValue direct)
        {
            try { return direct.GetValue<string>().Contains(needle, StringComparison.Ordinal); }
            catch { return false; }
        }
        if (content is JsonArray blocks)
        {
            foreach (var block in blocks)
            {
                if (block?["text"] is JsonValue text)
                {
                    try
                    {
                        if (text.GetValue<string>().Contains(needle, StringComparison.Ordinal))
                            return true;
                    }
                    catch { }
                }
            }
        }
        return false;
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
