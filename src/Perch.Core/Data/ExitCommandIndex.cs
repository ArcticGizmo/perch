using System.Text;
using System.Text.Json.Nodes;

namespace Perch.Data;

/// <summary>
/// The last time the user typed <c>/exit</c> (or its alias <c>/quit</c>) in each session, from one config dir's
/// <c>history.jsonl</c> — Claude Code's prompt history, where every submitted prompt is appended as
/// <c>{"display": "...", "timestamp": &lt;ms&gt;, "project": "...", "sessionId": "..."}</c>. It's the one record Claude
/// keeps of a deliberate exit (docs/session-recovery-plan.md, D2); <see cref="SessionEndReader"/> weighs it against
/// the transcript.
/// <para>The file is large and append-only, so <see cref="Refresh"/> reads only the bytes added since the last call,
/// and only up to the last complete line (a line still being written is picked up next time). A file that shrank was
/// replaced, so it's read again from the start. Entries without a <c>sessionId</c> (older Claude Code versions) are
/// skipped. Thread-safe; never throws.</para>
/// </summary>
internal sealed class ExitCommandIndex
{
    private readonly string _path;
    private readonly Lock _gate = new();
    private readonly Dictionary<string, DateTime> _lastExit = new(StringComparer.Ordinal);
    private long _offset;

    // A cheap byte-level pre-filter: only lines mentioning one of these are parsed at all.
    private static readonly string[] Commands = ["/exit", "/quit"];

    public ExitCommandIndex(string historyFile) => _path = historyFile;

    /// <summary>The time (local) of the last <c>/exit</c> typed in <paramref name="sessionId"/>, or null.</summary>
    public DateTime? LastExit(string sessionId)
    {
        lock (_gate)
            return _lastExit.TryGetValue(sessionId, out var t) ? t : null;
    }

    /// <summary>Reads whatever was appended since the last call.</summary>
    public void Refresh()
    {
        lock (_gate)
        {
            try
            {
                if (!File.Exists(_path)) return;
                using var fs = TranscriptScan.OpenShared(_path);
                if (fs.Length < _offset)
                {
                    _offset = 0;
                    _lastExit.Clear();
                }
                if (fs.Length == _offset) return;

                fs.Seek(_offset, SeekOrigin.Begin);
                var bytes = new byte[fs.Length - _offset];
                int read = 0;
                while (read < bytes.Length && fs.Read(bytes, read, bytes.Length - read) is > 0 and var n) read += n;

                int end = read > 0 ? Array.LastIndexOf(bytes, (byte)'\n', read - 1) : -1;
                if (end < 0) return;   // no complete line yet
                foreach (var line in Encoding.UTF8.GetString(bytes, 0, end).Split('\n'))
                    Consider(line);
                _offset += end + 1;
            }
            catch
            {
                // Best-effort: an unreadable history leaves the index as it was; the next refresh tries again.
            }
        }
    }

    private void Consider(string line)
    {
        if (!Commands.Any(c => line.Contains(c, StringComparison.OrdinalIgnoreCase))) return;
        try
        {
            if (JsonNode.Parse(line) is not JsonObject entry) return;
            var display = TranscriptJson.AsString(entry["display"])?.Trim();
            if (display is null || !Commands.Contains(display, StringComparer.OrdinalIgnoreCase)) return;
            var sessionId = TranscriptJson.AsString(entry["sessionId"]);
            long ms = TranscriptJson.AsLong(entry["timestamp"]);
            if (string.IsNullOrEmpty(sessionId) || ms <= 0) return;

            var at = DateTimeOffset.FromUnixTimeMilliseconds(ms).LocalDateTime;
            if (!_lastExit.TryGetValue(sessionId, out var prev) || at > prev) _lastExit[sessionId] = at;
        }
        catch
        {
            // A malformed line is skipped.
        }
    }
}
