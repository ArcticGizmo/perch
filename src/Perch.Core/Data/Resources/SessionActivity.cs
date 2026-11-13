using System.Text.Json;

namespace Perch.Data.Resources;

/// <summary>One tool call from a transcript: when, by which agent (<c>main</c> or a sub-agent's id), and which tool.</summary>
public readonly record struct ToolCall(DateTime AtUtc, string Agent, string Tool);

/// <summary>A moment Claude Code fires hooks for, reconstructed from a transcript: the event (<c>PreToolUse</c>,
/// <c>PostToolUse</c>, <c>UserPromptSubmit</c>, <c>Stop</c>, …), when, by which agent, and for tool events which tool.
/// Claude Code doesn't log successful hook runs, so these are what the hooks page counts runs from.</summary>
public readonly record struct HookMoment(DateTime AtUtc, string Agent, string Event, string? Tool);

/// <summary>A hook run Claude Code did record: a failure (<c>hook_non_blocking_error</c> and kin) or a Stop hook's
/// duration from <c>stop_hook_summary</c>. <see cref="Command"/> has the profile folder masked.</summary>
public readonly record struct HookTrace(DateTime AtUtc, string Agent, string Event, string Command, int? DurationMs, string? Error, int? ExitCode);

/// <summary>What a session did during a recording, from its transcript and its sub-agents' transcripts.
/// <see cref="Cwd"/> is the working directory the transcript records, for finding the project's hook settings.</summary>
public sealed record SessionActivity(string SessionId, string? Cwd, IReadOnlyList<ToolCall> Calls)
{
    public IReadOnlyList<HookMoment> Moments { get; init; } = [];
    public IReadOnlyList<HookTrace> Traces { get; init; } = [];

    /// <summary>Reads the tool calls (and hook moments and traces) between <paramref name="fromUtc"/> and
    /// <paramref name="toUtc"/> from a session's transcript and the <c>{sessionId}/subagents/agent-*.jsonl</c> beside it.
    /// Best-effort: unreadable files and malformed lines are skipped, and it never throws.</summary>
    public static SessionActivity Read(string sessionId, string transcriptPath, DateTime fromUtc, DateTime toUtc)
    {
        var acc = new Accumulator(fromUtc, toUtc);
        ReadFile(transcriptPath, "main", acc, isMain: true);
        try
        {
            var dir = Path.Combine(Path.GetDirectoryName(transcriptPath) ?? "", sessionId, "subagents");
            if (Directory.Exists(dir))
            {
                foreach (var file in Directory.EnumerateFiles(dir, "agent-*.jsonl"))
                {
                    // A sub-agent last written before the window opened did nothing in it.
                    try { if (File.GetLastWriteTimeUtc(file) < fromUtc) continue; } catch { }
                    ReadFile(file, Path.GetFileNameWithoutExtension(file), acc, isMain: false);
                }
            }
        }
        catch { }
        acc.Calls.Sort((a, b) => a.AtUtc.CompareTo(b.AtUtc));
        acc.Moments.Sort((a, b) => a.AtUtc.CompareTo(b.AtUtc));
        acc.Traces.Sort((a, b) => a.AtUtc.CompareTo(b.AtUtc));
        return new SessionActivity(sessionId, acc.Cwd, acc.Calls) { Moments = acc.Moments, Traces = acc.Traces };
    }

    internal sealed class Accumulator(DateTime fromUtc, DateTime toUtc)
    {
        public readonly DateTime From = fromUtc, To = toUtc;
        public readonly List<ToolCall> Calls = new();
        public readonly List<HookMoment> Moments = new();
        public readonly List<HookTrace> Traces = new();
        public string? Cwd;
        public bool InWindow(DateTime at) => at >= From && at <= To;
    }

    private static void ReadFile(string path, string agent, Accumulator acc, bool isMain)
    {
        var toolNames = new Dictionary<string, string>(StringComparer.Ordinal);
        DateTime? first = null, last = null;
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);
            while (reader.ReadLine() is { } line)
                ParseLine(line, agent, isMain, acc, toolNames, ref first, ref last);
        }
        catch { }

        // A session's first record is its start; a sub-agent's first and last records are its start and stop. A
        // sub-agent still writing at the end of the window hasn't stopped yet.
        if (isMain)
        {
            if (first is { } start && acc.InWindow(start)) acc.Moments.Add(new HookMoment(start, agent, "SessionStart", null));
        }
        else
        {
            if (first is { } start && acc.InWindow(start)) acc.Moments.Add(new HookMoment(start, agent, "SubagentStart", null));
            if (last is { } stop && acc.InWindow(stop) && stop < acc.To.AddSeconds(-30))
                acc.Moments.Add(new HookMoment(stop, agent, "SubagentStop", null));
        }
    }

    private static readonly TimeSpan OutsideMargin = TimeSpan.FromMinutes(1);

    // Only the record kinds that mark a hook event, carry a tool call, or report a hook run are parsed.
    private static readonly string[] Interesting =
        ["\"tool_use\"", "\"tool_result\"", "\"type\":\"user\"", "stop_hook_summary", "turn_duration", "compact_boundary", "\"hook_"];

    internal static void ParseLine(string line, string agent, bool isMain, Accumulator acc, Dictionary<string, string> toolNames,
        ref DateTime? first, ref DateTime? last)
    {
        if (acc.Cwd is null && isMain && line.Contains("\"cwd\"", StringComparison.Ordinal)) acc.Cwd = Str(line, "cwd");
        bool timestamped = line.Contains("\"timestamp\"", StringComparison.Ordinal);
        if (!timestamped) return;
        bool interesting = Interesting.Any(k => line.Contains(k, StringComparison.Ordinal));
        if (!interesting && first is not null)
        {
            // Cheap path for everything else: only the time matters (for sub-agent start/stop).
            if (TimestampOf(line) is { } t) last = t;
            return;
        }
        // An interesting record well outside the window adds nothing but its time, and the window is usually the last
        // few minutes of a long transcript that's re-read every half minute while recording (perf review PF16). The
        // margin covers a nested "timestamp" key read ahead of the record's own.
        if (first is not null && TimestampOf(line) is { } when
            && (when < acc.From - OutsideMargin || when > acc.To + OutsideMargin))
        {
            last = when;
            return;
        }
        try
        {
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;
            if (!root.TryGetProperty("timestamp", out var ts) || ts.ValueKind != JsonValueKind.String || !ts.TryGetDateTime(out var at)) return;
            at = at.ToUniversalTime();
            first ??= at;
            last = at;
            if (!interesting || !acc.InWindow(at)) return;

            string type = root.TryGetProperty("type", out var tp) && tp.ValueKind == JsonValueKind.String ? tp.GetString() ?? "" : "";
            string subtype = root.TryGetProperty("subtype", out var st) && st.ValueKind == JsonValueKind.String ? st.GetString() ?? "" : "";

            if (type == "system")
            {
                if (subtype == "turn_duration" && isMain) acc.Moments.Add(new HookMoment(at, agent, "Stop", null));
                else if (subtype == "compact_boundary")
                {
                    acc.Moments.Add(new HookMoment(at, agent, "PreCompact", null));
                    acc.Moments.Add(new HookMoment(at, agent, "PostCompact", null));
                }
                else if (subtype == "stop_hook_summary" && root.TryGetProperty("hookInfos", out var infos) && infos.ValueKind == JsonValueKind.Array)
                {
                    foreach (var info in infos.EnumerateArray())
                    {
                        if (info.ValueKind != JsonValueKind.Object || Str(info, "command") is not { } cmd) continue;
                        int? ms = info.TryGetProperty("durationMs", out var d) && d.ValueKind == JsonValueKind.Number ? d.GetInt32() : null;
                        acc.Traces.Add(new HookTrace(at, agent, isMain ? "Stop" : "SubagentStop", CommandLineRedactor.Redact(cmd), ms, null, null));
                    }
                }
                return;
            }

            if (type == "attachment" && root.TryGetProperty("attachment", out var att) && att.ValueKind == JsonValueKind.Object)
            {
                string kind = Str(att, "type") ?? "";
                if (kind.StartsWith("hook_", StringComparison.Ordinal) && Str(att, "command") is { } cmd)
                {
                    string? error = kind.Contains("error", StringComparison.Ordinal) || kind.Contains("blocking", StringComparison.Ordinal)
                        ? FirstLine(Str(att, "stderr")) ?? FirstLine(Str(att, "content")) ?? kind
                        : null;
                    int? exit = att.TryGetProperty("exitCode", out var ec) && ec.ValueKind == JsonValueKind.Number ? ec.GetInt32() : null;
                    int? ms = att.TryGetProperty("durationMs", out var d) && d.ValueKind == JsonValueKind.Number ? d.GetInt32() : null;
                    acc.Traces.Add(new HookTrace(at, agent, Str(att, "hookEvent") ?? "?", CommandLineRedactor.Redact(cmd), ms,
                        error is null ? null : CommandLineRedactor.Redact(error), exit));
                }
                return;
            }

            if (!root.TryGetProperty("message", out var msg) || msg.ValueKind != JsonValueKind.Object) return;
            if (!msg.TryGetProperty("content", out var content)) return;

            if (type == "user" && isMain && content.ValueKind == JsonValueKind.String
                && !(root.TryGetProperty("isMeta", out var meta) && meta.ValueKind == JsonValueKind.True))
            {
                acc.Moments.Add(new HookMoment(at, agent, "UserPromptSubmit", null));
                return;
            }
            if (content.ValueKind != JsonValueKind.Array) return;
            foreach (var block in content.EnumerateArray())
            {
                if (block.ValueKind != JsonValueKind.Object) continue;
                string blockType = Str(block, "type") ?? "";
                if (blockType == "tool_use" && Str(block, "name") is { } name)
                {
                    if (Str(block, "id") is { } id) toolNames[id] = name;
                    acc.Calls.Add(new ToolCall(at, agent, name));
                    acc.Moments.Add(new HookMoment(at, agent, "PreToolUse", name));
                }
                else if (blockType == "tool_result" && Str(block, "tool_use_id") is { } useId)
                {
                    bool failed = block.TryGetProperty("is_error", out var e) && e.ValueKind == JsonValueKind.True;
                    acc.Moments.Add(new HookMoment(at, agent, failed ? "PostToolUseFailure" : "PostToolUse", toolNames.GetValueOrDefault(useId)));
                }
                else if (blockType == "text" && type == "user" && isMain
                         && !(root.TryGetProperty("isMeta", out var m2) && m2.ValueKind == JsonValueKind.True)
                         && !content.EnumerateArray().Any(b => b.ValueKind == JsonValueKind.Object && Str(b, "type") == "tool_result"))
                {
                    acc.Moments.Add(new HookMoment(at, agent, "UserPromptSubmit", null));
                    break;
                }
            }
        }
        catch { }
    }

    private static DateTime? TimestampOf(string line) => TranscriptJson.PeekTimestampUtc(line);

    private static string? FirstLine(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        var line = s.Trim().Split('\n')[0].Trim();
        return line.Length > 240 ? line[..240] + "…" : line;
    }

    private static string? Str(JsonElement o, string name) =>
        o.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static string? Str(string line, string name)
    {
        try
        {
            using var doc = JsonDocument.Parse(line);
            return Str(doc.RootElement, name);
        }
        catch
        {
            return null;
        }
    }
}

/// <summary>What the viewer adds to a recording from this machine: the hooks that could have run and the sessions'
/// tool calls. Both optional; without them the analysis still names launches by their command lines.</summary>
public sealed record ResourceContext(HookCatalog? Hooks, IReadOnlyDictionary<string, SessionActivity>? Activity)
{
    public static readonly ResourceContext None = new(null, null);

    /// <summary>Builds the context for <paramref name="recording"/> from the files on disk. Touches the disk: call it
    /// off the UI thread.</summary>
    public static ResourceContext Load(ResourceRecording recording) =>
        recording.Ticks.Count == 0
            ? None
            : Load(recording.Sessions.ToList(), recording.Hooks.ToList(), recording.Ticks[0].AtUtc, recording.Ticks[^1].AtUtc);

    /// <summary>As <see cref="Load(ResourceRecording)"/>, from copies taken on the thread that owns the recording, so a
    /// live recording can keep growing while this runs elsewhere.</summary>
    public static ResourceContext Load(IReadOnlyList<ResourceSession> sessions, IReadOnlyList<HookDef> recordedHooks,
        DateTime firstTickUtc, DateTime lastTickUtc)
    {
        var from = firstTickUtc.AddMinutes(-1);
        var to = lastTickUtc.AddSeconds(5);
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        var activity = new Dictionary<string, SessionActivity>();
        var projectDirs = new List<string>();
        string? configRoot = null;
        foreach (var s in sessions.DistinctBy(s => s.SessionId))
        {
            string cwd = s.Cwd is { Length: > 0 } c ? Expand(c, profile) : "";
            var transcript = TranscriptLocator.Resolve(s.SessionId, cwd);
            if (transcript is null) continue;
            configRoot ??= TranscriptLocator.OwningConfigDir(transcript)?.Root;
            var a = SessionActivity.Read(s.SessionId, transcript, from, to);
            activity[s.SessionId] = a;
            if (cwd.Length > 0) projectDirs.Add(cwd);
            else if (a.Cwd is { Length: > 0 } fromTranscript) projectDirs.Add(fromTranscript);
        }

        // What the recording saw comes first; this machine's current hooks fill in for recordings made before hooks were
        // recorded, at the risk of a configuration that has since changed.
        var disk = HookCatalog.Load(configRoot ?? ClaudePaths.ClaudeDir, projectDirs);
        var hooks = new HookCatalog(recordedHooks
            .Select(h => h with { File = Expand(h.File, profile) })
            .Concat(disk.Hooks)
            .DistinctBy(h => (h.Source, h.Event, h.Matcher, Normalized: ResourceAnalysis.Normalize(h.Command)))
            .ToList());
        return new ResourceContext(hooks, activity);
    }

    /// <summary>The hooks for a set of live sessions, grouped by their config dirs: what the recorder writes into a
    /// recording. Touches the disk.</summary>
    public static HookCatalog HooksFor(IReadOnlyList<SessionTag> sessions)
    {
        var hooks = new List<HookDef>();
        foreach (var byRoot in sessions.GroupBy(s => s.ConfigRoot ?? ClaudePaths.ClaudeDir, StringComparer.OrdinalIgnoreCase))
            hooks.AddRange(HookCatalog.Load(byRoot.Key, byRoot.Select(s => s.Cwd ?? "")).Hooks);
        if (sessions.Count == 0) hooks.AddRange(HookCatalog.Load(ClaudePaths.ClaudeDir, []).Hooks);
        return new HookCatalog(hooks.DistinctBy(h => (h.Source, h.Event, h.Matcher, h.Command)).ToList());
    }

    // A recorded path with the profile folder masked, back to the real path on this machine.
    private static string Expand(string masked, string profile) =>
        masked.StartsWith('~') ? profile + masked[1..] : masked;
}
