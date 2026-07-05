using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Perch.Data;

namespace Perch.Data;

/// <summary>The kind of thing a <see cref="HistoryEvent"/> represents in a session transcript.</summary>
internal enum HistoryEventKind
{
    UserText,
    AssistantText,
    Thinking,
    ToolCall,
    Image, // a pasted/attached image (base64 inline data, or a url source)
    Meta,  // summaries / unknown record types — shown only in the raw view
}

/// <summary>
/// One rendered entry in a session's history. Tool calls are mutable: a <c>tool_result</c> arriving
/// later (it lives on a separate transcript line) is stitched onto the matching call via its id.
/// </summary>
internal sealed class HistoryEvent
{
    public HistoryEventKind Kind { get; init; }
    public DateTime? Timestamp { get; init; }
    public bool IsSidechain { get; init; }

    /// <summary>One-line summary (the role's text first line, or the tool phrase).</summary>
    public string Summary { get; set; } = "";

    /// <summary>Full detail: the message body, the tool input, and (once stitched) its result.</summary>
    public string Detail { get; set; } = "";

    /// <summary>Tool result text, attached out-of-band once the matching tool_result line is read.</summary>
    public string? Result { get; set; }

    /// <summary>Stable key for tracking expand/collapse state across re-renders.</summary>
    public string Key { get; init; } = "";

    // Image events only: the media type plus exactly one of a url source or inline base64 data.
    public string? ImageMedia { get; set; }
    public string? ImageUrl { get; set; }
    public string? ImageData { get; set; }
}

/// <summary>What a single <see cref="TranscriptParser.Ingest"/> pass changed, so the UI can render
/// incrementally: where the newly-appended events start, and which already-known events were mutated
/// (a tool result landed on an earlier call).</summary>
internal readonly record struct IngestResult(int FirstNewIndex, IReadOnlyList<int> MutatedIndices)
{
    public static readonly IngestResult None = new(-1, []);
    public bool HasNew => FirstNewIndex >= 0;
}

/// <summary>
/// Incrementally parses a session transcript (<c>~/.claude/projects/{enc-cwd}/{sessionId}.jsonl</c>)
/// into an ordered <see cref="HistoryEvent"/> list. Built for live tailing: <see cref="Ingest"/> reads
/// only the bytes appended since the previous call, so following an active session is cheap. The file
/// is appended-only; if it ever shrinks (replaced/truncated) the parser resets and re-reads from the top.
/// Best-effort throughout — malformed/partial trailing lines are skipped, never thrown.
/// </summary>
internal sealed class TranscriptParser
{
    private readonly string _path;
    private long _offset;

    // tool_use id -> the ToolCall event awaiting its result, so a later tool_result line can be stitched on.
    private readonly Dictionary<string, HistoryEvent> _pendingTools = new();
    // index of each tool event in Events, to report it as "mutated" when its result arrives.
    private readonly Dictionary<string, int> _toolIndex = new();

    public List<HistoryEvent> Events { get; } = new();

    public TranscriptParser(string path) => _path = path;

    /// <summary>Reads everything appended since the last call and folds it into <see cref="Events"/>.</summary>
    public IngestResult Ingest()
    {
        try
        {
            var fi = new FileInfo(_path);
            if (!fi.Exists)
                return IngestResult.None;

            if (fi.Length < _offset)
                Reset(); // file was replaced/truncated — start over

            if (fi.Length == _offset)
                return IngestResult.None; // nothing new

            byte[] bytes;
            using (var fs = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            {
                fs.Seek(_offset, SeekOrigin.Begin);
                long len = fs.Length - _offset;
                bytes = new byte[len];
                int read = 0;
                while (read < len)
                {
                    int n = fs.Read(bytes, read, (int)(len - read));
                    if (n <= 0) break;
                    read += n;
                }
                if (read != len)
                    bytes = bytes[..read];
            }

            var text = Encoding.UTF8.GetString(bytes);

            // Only advance past complete lines; a trailing partial line is left for the next Ingest
            // (the file is appended live, so the last record can be half-written).
            int lastNl = text.LastIndexOf('\n');
            if (lastNl < 0)
                return IngestResult.None;

            var consumed = text[..(lastNl + 1)];
            _offset += Encoding.UTF8.GetByteCount(consumed);

            int firstNew = Events.Count;
            var mutated = new List<int>();
            foreach (var line in consumed.Split('\n'))
            {
                if (!string.IsNullOrWhiteSpace(line))
                    ProcessLine(line, mutated);
            }

            bool hasNew = Events.Count > firstNew;
            return new IngestResult(hasNew ? firstNew : -1, mutated);
        }
        catch
        {
            return IngestResult.None;
        }
    }

    private void Reset()
    {
        _offset = 0;
        Events.Clear();
        _pendingTools.Clear();
        _toolIndex.Clear();
    }

    private void ProcessLine(string line, List<int> mutated)
    {
        JsonNode? node;
        try { node = JsonNode.Parse(line); }
        catch { return; }
        if (node == null) return;

        var type = node["type"]?.GetValue<string>();
        DateTime? ts = TranscriptJson.ParseTimestamp(node["timestamp"]?.GetValue<string>());
        bool sidechain = node["isSidechain"]?.GetValue<bool>() ?? false;
        bool isUser = type == "user";

        var content = node["message"]?["content"];

        // A plain-string message body is the common shape for a user prompt (and occasionally an
        // assistant reply). Array bodies carry the structured blocks handled below.
        if (content is JsonValue val && val.TryGetValue<string>(out var str))
        {
            if (!string.IsNullOrWhiteSpace(str))
                Add(new HistoryEvent
                {
                    Kind = isUser ? HistoryEventKind.UserText : HistoryEventKind.AssistantText,
                    Timestamp = ts,
                    IsSidechain = sidechain,
                    Summary = FirstLine(str),
                    Detail = str.Trim(),
                    Key = $"e{Events.Count}",
                });
            return;
        }

        if (content is not JsonArray blocks)
        {
            // Unknown record (summary, system, …) — keep it for the raw view only.
            if (type is "summary" or "system")
            {
                var s = node["summary"]?.GetValue<string>() ?? type;
                Add(new HistoryEvent
                {
                    Kind = HistoryEventKind.Meta,
                    Timestamp = ts,
                    Summary = $"[{type}] {FirstLine(s)}",
                    Detail = s,
                    Key = $"e{Events.Count}",
                });
            }
            return;
        }

        foreach (var block in blocks)
        {
            var btype = TranscriptJson.BlockType(block);
            switch (btype)
            {
                case "text":
                {
                    var t = block!["text"]?.GetValue<string>();
                    if (!string.IsNullOrWhiteSpace(t))
                        Add(new HistoryEvent
                        {
                            Kind = isUser ? HistoryEventKind.UserText : HistoryEventKind.AssistantText,
                            Timestamp = ts,
                            IsSidechain = sidechain,
                            Summary = FirstLine(t),
                            Detail = t.Trim(),
                            Key = $"e{Events.Count}",
                        });
                    break;
                }
                case "thinking":
                {
                    var t = block!["thinking"]?.GetValue<string>();
                    if (!string.IsNullOrWhiteSpace(t))
                        Add(new HistoryEvent
                        {
                            Kind = HistoryEventKind.Thinking,
                            Timestamp = ts,
                            IsSidechain = sidechain,
                            Summary = FirstLine(t),
                            Detail = t.Trim(),
                            Key = $"e{Events.Count}",
                        });
                    break;
                }
                case "tool_use":
                {
                    var name = block!["name"]?.GetValue<string>() ?? "tool";
                    var input = block["input"];
                    var id = block["id"]?.GetValue<string>();
                    var ev = new HistoryEvent
                    {
                        Kind = HistoryEventKind.ToolCall,
                        Timestamp = ts,
                        IsSidechain = sidechain,
                        Summary = ToolSummary.Describe(name, input),
                        Detail = PrettyJson(input),
                        Key = id ?? $"e{Events.Count}",
                    };
                    Add(ev);
                    if (id != null)
                    {
                        _pendingTools[id] = ev;
                        _toolIndex[id] = Events.Count - 1;
                    }
                    break;
                }
                case "image":
                {
                    var source = block!["source"];
                    var stype = source?["type"]?.GetValue<string>();
                    var media = source?["media_type"]?.GetValue<string>() ?? "image";
                    Add(new HistoryEvent
                    {
                        Kind = HistoryEventKind.Image,
                        Timestamp = ts,
                        IsSidechain = sidechain,
                        ImageMedia = media,
                        ImageUrl = stype == "url" ? source?["url"]?.GetValue<string>() : null,
                        ImageData = stype == "base64" ? source?["data"]?.GetValue<string>() : null,
                        Summary = $"image ({media})",
                        Key = $"e{Events.Count}",
                    });
                    break;
                }
                case "tool_result":
                {
                    var rid = block!["tool_use_id"]?.GetValue<string>();
                    if (rid != null && _pendingTools.TryGetValue(rid, out var call))
                    {
                        call.Result = ExtractResult(block["content"]);
                        _pendingTools.Remove(rid);
                        if (_toolIndex.TryGetValue(rid, out var idx))
                            mutated.Add(idx);
                    }
                    break;
                }
            }
        }
    }

    private void Add(HistoryEvent ev) => Events.Add(ev);

    private static string FirstLine(string s)
    {
        s = s.Trim();
        int nl = s.IndexOf('\n');
        return nl < 0 ? s : s[..nl];
    }

    // A tool_result's content is either a plain string or an array of {type:"text", text:…} blocks.
    private static string ExtractResult(JsonNode? content)
    {
        if (content is JsonValue v && v.TryGetValue<string>(out var s))
            return s.Trim();

        if (content is JsonArray arr)
        {
            var sb = new StringBuilder();
            foreach (var b in arr)
            {
                var t = b?["text"]?.GetValue<string>();
                if (t != null)
                    sb.AppendLine(t);
            }
            return sb.ToString().Trim();
        }

        return content?.ToJsonString() ?? "";
    }

    private static string PrettyJson(JsonNode? node)
    {
        if (node == null) return "";
        try { return node.ToJsonString(new JsonSerializerOptions { WriteIndented = true }); }
        catch { return node.ToString(); }
    }
}

/// <summary>One selectable session in the viewer's dropdown: a transcript file on disk, its project,
/// when it last changed, whether its process is currently alive, and how big it is (so the viewer can
/// warn before loading a multi-megabyte transcript).</summary>
internal sealed record HistoryEntry(
    string SessionId,
    string ProjectName,
    string Cwd,
    string Path,
    DateTime LastUpdated,
    bool IsActive,
    long SizeBytes = 0
)
{
    public string RelativeTime => SessionHistory.Relative(LastUpdated);

    /// <summary>Human-readable transcript size (e.g. "1.4 MB"), shown in the dropdown.</summary>
    public string SizeLabel => SessionHistory.FormatSize(SizeBytes);

    /// <summary>True for transcripts big enough to lag or risk exhausting memory; the viewer gates
    /// these behind an explicit "load anyway" confirmation rather than rendering them on selection.</summary>
    public bool IsLarge => SizeBytes >= SessionHistory.LargeTranscriptBytes;

    /// <summary>True for the synthetic "(none)" row that heads the dropdown — selecting it shows the
    /// empty placeholder rather than loading a transcript.</summary>
    public bool IsPlaceholder { get; init; }

    /// <summary>The synthetic "(none)" entry the viewer prepends to the dropdown so a user can open the
    /// window without loading anything and deliberately clear the current selection.</summary>
    public static HistoryEntry Placeholder { get; } =
        new("", "(none — select a session)", "", "", DateTime.MinValue, false) { IsPlaceholder = true };
}

/// <summary>
/// Static helpers for enumerating session transcripts on disk. Active sessions (their process still
/// running, per <see cref="SessionMonitor"/>) are listed first, then everything else newest-first.
/// </summary>
internal static class SessionHistory
{
    // Project name keyed by transcript path — the cwd inside a transcript never changes, so caching
    // avoids reopening every file on each (frequent) re-list. Listing runs on a background thread, so
    // guard the cache.
    private static readonly Dictionary<string, string> _projectNameCache = new();
    private static readonly object _cacheLock = new();

    /// <summary>Transcripts at or above this size are flagged "large": the viewer shows their size in an
    /// alert colour and asks before loading them, since parsing/rendering a multi-megabyte transcript can
    /// lag badly or run the UI out of memory.</summary>
    public const long LargeTranscriptBytes = 10L * 1024 * 1024; // 10 MB

    /// <summary>Lists every session transcript across all projects, active first, then newest-first.</summary>
    public static List<HistoryEntry> ListAll(IReadOnlySet<string> activeSessionIds)
    {
        var entries = new List<HistoryEntry>();
        foreach (var file in TranscriptLocator.EnumerateTranscripts())
        {
            try
            {
                var fi = new FileInfo(file);
                var sessionId = System.IO.Path.GetFileNameWithoutExtension(file);
                var (project, cwd) = ResolveProject(file, System.IO.Path.GetDirectoryName(file) ?? "");
                entries.Add(new HistoryEntry(
                    sessionId,
                    project,
                    cwd,
                    file,
                    fi.LastWriteTime,
                    activeSessionIds.Contains(sessionId),
                    fi.Length));
            }
            catch { }
        }

        return entries
            .OrderByDescending(e => e.IsActive)
            .ThenByDescending(e => e.LastUpdated)
            .ToList();
    }

    // Derives a friendly project name from the transcript's cwd (read once, cached), falling back to
    // the encoded directory name when no cwd can be recovered.
    private static (string project, string cwd) ResolveProject(string file, string dir)
    {
        lock (_cacheLock)
        {
            if (_projectNameCache.TryGetValue(file, out var cached))
                return (cached, "");
        }

        string cwd = "";
        try
        {
            using var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(fs);
            for (int i = 0; i < 8 && reader.ReadLine() is { } line; i++)
            {
                if (!line.Contains("\"cwd\"")) continue;
                try
                {
                    var c = JsonNode.Parse(line)?["cwd"]?.GetValue<string>();
                    if (!string.IsNullOrEmpty(c)) { cwd = c; break; }
                }
                catch { }
            }
        }
        catch { }

        string project = !string.IsNullOrEmpty(cwd)
            ? System.IO.Path.GetFileName(cwd.TrimEnd('/', '\\'))
            : System.IO.Path.GetFileName(dir);

        if (string.IsNullOrEmpty(project))
            project = "session";

        lock (_cacheLock)
            _projectNameCache[file] = project;
        return (project, cwd);
    }

    /// <summary>Formats a byte count as a short, human-readable size (e.g. "812 KB", "1.4 MB").</summary>
    public static string FormatSize(long bytes)
    {
        if (bytes < 1024) return $"{bytes} B";
        double kb = bytes / 1024.0;
        if (kb < 1024) return $"{kb:0} KB";
        double mb = kb / 1024.0;
        if (mb < 1024) return $"{mb:0.0} MB";
        return $"{mb / 1024.0:0.0} GB";
    }

    public static string Relative(DateTime t)
    {
        var d = DateTime.Now - t;
        if (d.TotalSeconds < 60) return "just now";
        if (d.TotalMinutes < 60) return $"{(int)d.TotalMinutes}m ago";
        if (d.TotalHours < 24) return $"{(int)d.TotalHours}h ago";
        if (d.TotalDays < 7) return $"{(int)d.TotalDays}d ago";
        return t.ToString("MMM d");
    }
}
