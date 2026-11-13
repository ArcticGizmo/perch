using System.Buffers;
using System.Collections.Concurrent;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Text;
using System.Text.Json.Nodes;
using Perch.Data.Control;

namespace Perch.Data;

/// <summary>Where in a transcript a search hit landed: a typed prompt, Claude's reply, its thinking, or a tool
/// call / tool output. Thinking and tools are only searched when asked (they're most of a transcript's bulk).</summary>
internal enum TranscriptHitKind { Prompt, Reply, Thinking, Tool, Report }

/// <summary>A one-line excerpt around a hit: whitespace collapsed, "…" where it was cut, and the match's
/// position within <see cref="Text"/> so the UI can highlight it.</summary>
internal sealed record TranscriptSnippet(string Text, int MatchStart, int MatchLength);

/// <summary>One hit: the record it's in (<see cref="Uuid"/>, which the viewer uses to land on it), what kind of
/// text it matched, when it was written, and an excerpt.</summary>
internal sealed record TranscriptHit(string? Uuid, TranscriptHitKind Kind, DateTime? Timestamp, TranscriptSnippet Snippet);

/// <summary>A transcript's matches: how many pieces of text matched (capped at <see cref="TranscriptSearch.CountCap"/>,
/// with <see cref="Capped"/> set when it stopped counting) and the first few hits, in file order.</summary>
internal sealed record TranscriptMatches(int Count, bool Capped, IReadOnlyList<TranscriptHit> Hits);

/// <summary>
/// Full-text search across session transcripts — "where did I discuss X?". No index: each search streams the
/// <c>.jsonl</c> files. A line is first matched as raw text against the JSON-escaped query (cheap, and it rules
/// out almost every line); only the lines that pass are parsed, and the hit is confirmed against the text the
/// history viewer would show — the prompt (<see cref="SessionConversation.GenuineUserPrompt"/>) or the events
/// <see cref="StreamJsonParser"/> decodes — so a match in a JSON key, a sub-agent's line or a slash-command echo
/// never surfaces. Matching is a case-insensitive substring, the same rule as the thread's Ctrl+F find, so the
/// viewer can light the same hit up. Like every reader here it's best-effort: an unreadable file or a malformed
/// line is skipped, never thrown.
/// </summary>
internal static class TranscriptSearch
{
    /// <summary>Shorter queries match nearly every line, so the UI waits for this many characters.</summary>
    public const int MinQueryLength = 2;

    /// <summary>A transcript stops counting its matches here; the UI shows "99+".</summary>
    public const int CountCap = 99;

    /// <summary>Characters of context kept either side of a match in a <see cref="TranscriptSnippet"/>.</summary>
    public const int SnippetRadius = 60;

    /// <summary>Searches one transcript, returning null when nothing matched (or the file couldn't be read).
    /// <paramref name="includeTools"/> widens the search from prompts and replies to thinking, tool calls and tool
    /// output. At most <paramref name="maxHits"/> hits are kept; the count runs on to <see cref="CountCap"/>.</summary>
    public static TranscriptMatches? SearchFile(string path, string query, bool includeTools, int maxHits,
        CancellationToken ct = default)
    {
        query = query.Trim();
        if (query.Length < MinQueryLength) return null;
        var needle = RawNeedle(query);
        int count = 0;
        var hits = new List<TranscriptHit>();

        // False once the cap is reached, which ends the scan.
        bool Take(string line)
        {
            foreach (var hit in MatchLine(line, query, includeTools))
            {
                if (hits.Count < maxHits) hits.Add(hit);
                if (++count >= CountCap) return false;
            }
            return true;
        }

        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete,
                1 << 16, FileOptions.SequentialScan);
            if (Ascii.IsValid(needle)) ScanBytes(fs, needle, includeTools, Take, ct);
            else ScanChars(fs, needle, includeTools, Take, ct);
        }
        catch (OperationCanceledException) { throw; }
        catch { /* unreadable right now — report what was found, if anything */ }
        return count == 0 ? null : new TranscriptMatches(count, count >= CountCap, hits);
    }

    private static ReadOnlySpan<byte> ToolResultTag => "\"type\":\"tool_result\""u8;

    // A transcript is mostly lines that don't match, and decoding them is most of a search's cost (about 2 bytes
    // of string per byte of file). An ASCII needle can be found in the raw UTF-8, so only the lines it lands in
    // are decoded. The one difference from a case-insensitive string search: a non-ASCII letter whose upper
    // case is ASCII (the dotless ı, the long ſ) no longer stands in for its ASCII look-alike.
    private static void ScanBytes(FileStream fs, string needle, bool includeTools, Func<string, bool> take,
        CancellationToken ct)
    {
        var pattern = Encoding.ASCII.GetBytes(needle);
        var buffer = ArrayPool<byte>.Shared.Rent(1 << 20);
        try
        {
            int filled = 0;
            bool eof = false, atStart = true;
            while (!eof)
            {
                ct.ThrowIfCancellationRequested();
                if (filled == buffer.Length)
                {
                    // One line longer than the buffer (a pasted image, a huge tool result): grow to hold it.
                    var bigger = ArrayPool<byte>.Shared.Rent(buffer.Length * 2);
                    buffer.AsSpan(0, filled).CopyTo(bigger);
                    ArrayPool<byte>.Shared.Return(buffer);
                    buffer = bigger;
                }
                int read = fs.Read(buffer, filled, buffer.Length - filled);
                eof = read == 0;
                filled += read;
                int skip = 0;
                if (atStart && (filled >= 3 || eof))
                {
                    if (buffer.AsSpan(0, filled).StartsWith("﻿"u8)) skip = 3;
                    atStart = false;
                }

                // The whole lines held so far; at the end of the file, the unterminated last line too.
                var data = buffer.AsSpan(skip, filled - skip);
                int complete = eof ? data.Length : data.LastIndexOf((byte)'\n') + 1;
                var lines = data[..complete];
                int pos = 0;
                while (pos < lines.Length)
                {
                    int at = IndexOfIgnoreAsciiCase(lines[pos..], pattern);
                    if (at < 0) break;
                    at += pos;
                    int start = lines[..at].LastIndexOf((byte)'\n') + 1;
                    int nl = lines[at..].IndexOf((byte)'\n');
                    int end = nl < 0 ? lines.Length : at + nl;
                    pos = end + 1;
                    var line = lines[start..end];
                    if (line.Length > 0 && line[^1] == (byte)'\r') line = line[..^1];
                    // Tool output is the bulk of a transcript; skip it without a parse when it isn't being searched.
                    if (!includeTools && line.IndexOf(ToolResultTag) >= 0) continue;
                    if (!take(Encoding.UTF8.GetString(line))) return;
                }

                int keep = data.Length - complete;
                data[complete..].CopyTo(buffer);
                filled = keep;
            }
        }
        finally { ArrayPool<byte>.Shared.Return(buffer); }
    }

    private static void ScanChars(FileStream fs, string needle, bool includeTools, Func<string, bool> take,
        CancellationToken ct)
    {
        using var reader = new StreamReader(fs);
        while (reader.ReadLine() is { } line)
        {
            ct.ThrowIfCancellationRequested();
            if (!line.Contains(needle, StringComparison.OrdinalIgnoreCase)) continue;
            if (!includeTools && line.Contains("\"type\":\"tool_result\"", StringComparison.Ordinal)) continue;
            if (!take(line)) return;
        }
    }

    /// <summary>Where the ASCII <paramref name="pattern"/> (at least 2 bytes) first occurs in
    /// <paramref name="haystack"/>, ignoring ASCII case; -1 when it doesn't.</summary>
    internal static int IndexOfIgnoreAsciiCase(ReadOnlySpan<byte> haystack, ReadOnlySpan<byte> pattern)
    {
        int n = pattern.Length;
        int lastStart = haystack.Length - n;
        if (lastStart < 0) return -1;
        byte fLo = AsciiLower(pattern[0]), fUp = AsciiUpper(pattern[0]);
        int i = 0;
        if (Vector128.IsHardwareAccelerated)
        {
            // A position is a candidate only when both its first and last bytes fit, which in prose and JSON
            // is rare even for common letters, so the full compare seldom runs.
            var firstLo = Vector128.Create(fLo);
            var firstUp = Vector128.Create(fUp);
            var lastLo = Vector128.Create(AsciiLower(pattern[n - 1]));
            var lastUp = Vector128.Create(AsciiUpper(pattern[n - 1]));
            ref byte h = ref MemoryMarshal.GetReference(haystack);
            for (; i + Vector128<byte>.Count - 1 <= lastStart; i += Vector128<byte>.Count)
            {
                var a = Vector128.LoadUnsafe(ref h, (nuint)i);
                var b = Vector128.LoadUnsafe(ref h, (nuint)(i + n - 1));
                var hit = (Vector128.Equals(a, firstLo) | Vector128.Equals(a, firstUp))
                    & (Vector128.Equals(b, lastLo) | Vector128.Equals(b, lastUp));
                for (uint bits = hit.ExtractMostSignificantBits(); bits != 0; bits &= bits - 1)
                {
                    int at = i + BitOperations.TrailingZeroCount(bits);
                    if (Ascii.EqualsIgnoreCase(haystack.Slice(at, n), pattern)) return at;
                }
            }
        }
        while (i <= lastStart)
        {
            int next = haystack[i..].IndexOfAny(fLo, fUp);
            if (next < 0) return -1;
            i += next;
            if (i > lastStart) return -1;
            if (Ascii.EqualsIgnoreCase(haystack.Slice(i, n), pattern)) return i;
            i++;
        }
        return -1;
    }

    private static byte AsciiLower(byte b) => b is >= (byte)'A' and <= (byte)'Z' ? (byte)(b | 0x20) : b;
    private static byte AsciiUpper(byte b) => b is >= (byte)'a' and <= (byte)'z' ? (byte)(b & ~0x20) : b;

    /// <summary>Searches many transcripts in parallel, calling <paramref name="onMatch"/> (from a worker thread)
    /// with each matching file's index into <paramref name="paths"/>, and <paramref name="onScanned"/> after each
    /// file. Files are handed out roughly in list order, so a newest-first list surfaces recent sessions first.
    /// Throws <see cref="OperationCanceledException"/> when cancelled.</summary>
    public static void SearchMany(IReadOnlyList<string> paths, string query, bool includeTools, int maxHitsPerFile,
        Action<int, TranscriptMatches> onMatch, Action? onScanned, CancellationToken ct)
    {
        var options = new ParallelOptions
        {
            CancellationToken = ct,
            MaxDegreeOfParallelism = Math.Clamp(Environment.ProcessorCount / 2, 2, 8),
        };
        Parallel.ForEach(Partitioner.Create(0, paths.Count, 1), options, range =>
        {
            for (int i = range.Item1; i < range.Item2; i++)
            {
                if (SearchFile(paths[i], query, includeTools, maxHitsPerFile, ct) is { } m) onMatch(i, m);
                onScanned?.Invoke();
            }
        });
    }

    /// <summary>Remembers the last finished search so that one which extends it ("capac" → "capacitor") reads
    /// only the transcripts that can still match. Text holding the longer query holds the shorter one too, so
    /// a transcript that didn't match before can't match now unless it has been written since.</summary>
    internal sealed class Narrowing
    {
        private readonly object _lock = new();
        private string? _query;
        private bool _includeTools;
        private Dictionary<string, (DateTime LastWrite, long Size)> _seen = new(StringComparer.OrdinalIgnoreCase);
        private HashSet<string> _matched = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>The indexes into <paramref name="entries"/> worth searching for <paramref name="query"/>: all
        /// of them, unless it extends the last finished search with the same scope, in which case only those
        /// that matched then, plus any written or created since.</summary>
        public List<int> Candidates(IReadOnlyList<HistoryEntry> entries, string query, bool includeTools)
        {
            lock (_lock)
            {
                bool narrows = _query is not null && includeTools == _includeTools
                    && query.Contains(_query, StringComparison.OrdinalIgnoreCase);
                var picked = new List<int>(narrows ? _matched.Count + 8 : entries.Count);
                for (int i = 0; i < entries.Count; i++)
                {
                    var e = entries[i];
                    if (!narrows || _matched.Contains(e.Path)
                        || !_seen.TryGetValue(e.Path, out var was) || was != (e.LastUpdated, e.SizeBytes))
                        picked.Add(i);
                }
                return picked;
            }
        }

        /// <summary>Records a search that ran to the end over <paramref name="entries"/> (every one of them either
        /// searched or ruled out), and the transcripts it matched.</summary>
        public void Completed(IReadOnlyList<HistoryEntry> entries, string query, bool includeTools,
            IEnumerable<string> matchedPaths)
        {
            var seen = new Dictionary<string, (DateTime, long)>(entries.Count, StringComparer.OrdinalIgnoreCase);
            foreach (var e in entries) seen[e.Path] = (e.LastUpdated, e.SizeBytes);
            var matched = new HashSet<string>(matchedPaths, StringComparer.OrdinalIgnoreCase);
            lock (_lock)
            {
                _query = query;
                _includeTools = includeTools;
                _seen = seen;
                _matched = matched;
            }
        }
    }

    /// <summary>The hits in one transcript line, confirmed against the text the viewer shows. Sub-agent lines
    /// and slash-command echoes yield nothing; a malformed line yields nothing.</summary>
    internal static IEnumerable<TranscriptHit> MatchLine(string line, string query, bool includeTools)
    {
        var hits = new List<TranscriptHit>();
        try
        {
            if (JsonNode.Parse(line) is not { } root) return hits;
            if (root["isSidechain"]?.GetValue<bool>() == true) return hits;
            var uuid = TranscriptJson.AsString(root["uuid"]);
            var at = TranscriptJson.ParseTimestamp(TranscriptJson.AsString(root["timestamp"]));

            void Try(TranscriptHitKind kind, string? text)
            {
                if (string.IsNullOrEmpty(text)) return;
                int i = text.IndexOf(query, StringComparison.OrdinalIgnoreCase);
                if (i >= 0) hits.Add(new TranscriptHit(uuid, kind, at, Snippet(text, i, query.Length)));
            }

            if (TranscriptJson.AsString(root["type"]) == "user" && SessionConversation.GenuineUserPrompt(root) is { } prompt)
            {
                Try(TranscriptHitKind.Prompt, prompt);
                return hits;
            }
            foreach (var ev in StreamJsonParser.Parse(line))
            {
                if (ev.FromSubagent) continue;
                switch (ev)
                {
                    case AssistantTextEvent t: Try(TranscriptHitKind.Reply, t.Text); break;
                    case AssistantThinkingEvent th when includeTools: Try(TranscriptHitKind.Thinking, th.Text); break;
                    case ToolUseEvent u when includeTools: Try(TranscriptHitKind.Tool, ToolUseText(u)); break;
                    case ToolResultEvent r when includeTools: Try(TranscriptHitKind.Tool, r.Text); break;
                    // A background agent's report, as its notice card shows it: prose like a reply, so always searched.
                    case AgentReportEvent rep: Try(TranscriptHitKind.Report, rep.Report); break;
                    case TaskNotificationEvent { Notification.Result: var result }:
                        Try(TranscriptHitKind.Report, AgentReport.FromNotificationResult(result));
                        break;
                }
            }
        }
        catch { /* a malformed line is skipped */ }
        return hits;
    }

    // A tool call reads as its name plus its input's string values ("Bash · npm test · Run the tests"), not its
    // raw JSON — so a hit's excerpt is readable and a path or command matches without its JSON escaping.
    private static string ToolUseText(ToolUseEvent u)
    {
        var sb = new StringBuilder(u.ToolName);
        try { AppendStrings(JsonNode.Parse(u.InputJson), sb); } catch { /* name only */ }
        return sb.ToString();
    }

    private static void AppendStrings(JsonNode? node, StringBuilder sb)
    {
        switch (node)
        {
            case JsonObject o: foreach (var (_, v) in o) AppendStrings(v, sb); break;
            case JsonArray a: foreach (var v in a) AppendStrings(v, sb); break;
            case JsonValue when TranscriptJson.AsString(node) is { Length: > 0 } s: sb.Append(" · ").Append(s); break;
        }
    }

    /// <summary>The query as it appears inside a JSON string on disk: backslashes, quotes and control characters
    /// escaped the way the CLI's JSON writer does — so a Windows path like <c>C:\src</c> still passes the raw
    /// pre-filter, where it's stored as <c>C:\\src</c>.</summary>
    internal static string RawNeedle(string query)
    {
        var sb = new StringBuilder(query.Length + 8);
        foreach (var c in query)
        {
            switch (c)
            {
                case '\\': sb.Append("\\\\"); break;
                case '"': sb.Append("\\\""); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                case < ' ': sb.Append("\\u").Append(((int)c).ToString("x4")); break;
                default: sb.Append(c); break;
            }
        }
        return sb.ToString();
    }

    /// <summary>A one-line excerpt of <paramref name="text"/> around the match at
    /// [<paramref name="index"/>, +<paramref name="length"/>): up to <see cref="SnippetRadius"/> characters either
    /// side, runs of whitespace (newlines included) collapsed to one space, and "…" where the text was cut.</summary>
    internal static TranscriptSnippet Snippet(string text, int index, int length, int radius = SnippetRadius)
    {
        int from = Math.Max(0, index - radius);
        int to = Math.Min(text.Length, index + length + radius);
        var sb = new StringBuilder(to - from + 2);
        if (from > 0) sb.Append('…');
        int start = -1, end = -1;
        bool lastSpace = true;   // drops leading whitespace
        for (int i = from; i < to; i++)
        {
            if (i == index) start = sb.Length;
            char c = text[i];
            if (char.IsWhiteSpace(c))
            {
                if (!lastSpace) sb.Append(' ');
                lastSpace = true;
            }
            else
            {
                sb.Append(c);
                lastSpace = false;
            }
            if (i == index + length - 1) end = sb.Length;
        }
        // Trailing whitespace goes, but never into the match.
        int keep = sb.Length;
        while (keep > Math.Max(end, 0) && sb[keep - 1] == ' ') keep--;
        sb.Length = keep;
        if (to < text.Length) sb.Append('…');
        if (start < 0) start = 0;
        if (end < start) end = start;
        return new TranscriptSnippet(sb.ToString(), start, end - start);
    }
}
