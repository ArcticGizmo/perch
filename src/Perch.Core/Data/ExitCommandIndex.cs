using System.Text.Json.Nodes;

namespace Perch.Data;

/// <summary>
/// The last time the user typed <c>/exit</c> (or its alias <c>/quit</c>) in each session, from one config dir's
/// <c>history.jsonl</c> — Claude Code's prompt history, where every submitted prompt is appended as
/// <c>{"display": "...", "timestamp": &lt;ms&gt;, "project": "...", "sessionId": "..."}</c>. It's the one record Claude
/// keeps of a deliberate exit (docs/session-recovery-plan.md, D2); <see cref="SessionEndReader"/> weighs it against
/// the transcript.
/// <para>The file is large and append-only, so <see cref="Refresh"/> folds it through a <see cref="TranscriptFold"/>:
/// only the bytes added since the last call are read (in chunks, a line still being written left for next time), an
/// unchanged file costs a stat, and a shrunk or replaced one is read again from the start. Entries without a
/// <c>sessionId</c> (older Claude Code versions) are skipped. Thread-safe; never throws.</para>
/// </summary>
internal sealed class ExitCommandIndex
{
    // A cheap pre-filter: only lines mentioning one of these are parsed at all.
    private static readonly string[] Commands = ["/exit", "/quit"];

    private static readonly LineFolder<Dictionary<string, DateTime>> Exits =
        new(() => new Dictionary<string, DateTime>(StringComparer.Ordinal), Consider);

    private readonly string _path;
    private readonly Lock _gate = new();
    private readonly TranscriptFold _fold = new(Exits);
    private IReadOnlyDictionary<string, DateTime> _lastExit = new Dictionary<string, DateTime>();

    public ExitCommandIndex(string historyFile) => _path = historyFile;

    /// <summary>The time (local) of the last <c>/exit</c> typed in <paramref name="sessionId"/>, or null.</summary>
    public DateTime? LastExit(string sessionId)
    {
        lock (_gate)
            return _lastExit.TryGetValue(sessionId, out var t) ? t : null;
    }

    /// <summary>Reads whatever was appended since the last call. An unreadable history keeps what was indexed.</summary>
    public void Refresh()
    {
        lock (_gate)
            _lastExit = _fold.Get(_path, Exits, exits => exits, _lastExit);
    }

    private static void Consider(Dictionary<string, DateTime> lastExit, string line)
    {
        if (!MentionsCommand(line)) return;
        if (JsonNode.Parse(line) is not JsonObject entry) return;   // a malformed line throws; the fold skips it
        var display = TranscriptJson.AsString(entry["display"])?.Trim();
        if (display is null || !Commands.Contains(display, StringComparer.OrdinalIgnoreCase)) return;
        var sessionId = TranscriptJson.AsString(entry["sessionId"]);
        long ms = TranscriptJson.AsLong(entry["timestamp"]);
        if (string.IsNullOrEmpty(sessionId) || ms <= 0) return;

        var at = DateTimeOffset.FromUnixTimeMilliseconds(ms).LocalDateTime;
        if (!lastExit.TryGetValue(sessionId, out var prev) || at > prev) lastExit[sessionId] = at;
    }

    private static bool MentionsCommand(string line)
    {
        foreach (var c in Commands)
            if (line.Contains(c, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }
}
