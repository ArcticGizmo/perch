using System.Text.Json.Nodes;
using Perch.Platform;

namespace Perch.Data;

/// <summary>
/// A headless worker session hosted by the Claude Code background daemon (<c>claude daemon run</c>).
/// These run with a named-pipe PTY instead of a terminal, so there is no host window anywhere in their
/// process ancestry — a focus click can never succeed on one. The overlay therefore surfaces them in
/// their own "daemon" section, where a click offers session actions (history, copy id/resume) instead
/// of a doomed focus attempt. Read from <c>~/.claude/daemon/roster.json</c> by
/// <see cref="DaemonRosterReader"/>.
/// </summary>
public record DaemonWorker(
    string ShortId,      // the roster key — the session id's first 8 hex chars
    string SessionId,
    int Pid,
    string Cwd,
    string ProjectName,  // leaf of Cwd, "" when the roster carries no cwd
    string Source,       // how it was dispatched: "slash", "spare", … ("" when absent)
    string? Name,        // the dispatch seed's name (or trimmed intent); null for an unnamed worker
    DateTime StartedAt
)
{
    /// <summary>True for a pre-warmed standby worker the daemon keeps idling so the next background
    /// dispatch starts instantly — it has no task of its own yet.</summary>
    public bool IsSpare => string.Equals(Source, "spare", StringComparison.OrdinalIgnoreCase);

    /// <summary>The label to show the user: the dispatch's task name when it has one, otherwise the
    /// project it runs in, otherwise the session id prefix.</summary>
    public string DisplayName =>
        Name ?? (ProjectName.Length > 0 ? ProjectName : ShortId);
}

/// <summary>
/// Reads the daemon supervisor's worker roster (<c>~/.claude/daemon/roster.json</c>), best-effort.
/// The file is rewritten live by the daemon, so it's opened shared and any parse hiccup yields an
/// empty list rather than an exception. Workers whose pid is no longer alive are dropped — a killed
/// daemon leaves the roster behind untouched, and the pid probe is the only tell.
/// </summary>
public static class DaemonRosterReader
{
    /// <summary>The directory the roster lives in (<c>~/.claude/daemon</c>) — what the app watches.</summary>
    public static string Directory => ClaudePaths.DaemonDir;

    public static IReadOnlyList<DaemonWorker> Read(IProcessProbe? probe = null)
    {
        probe ??= SystemProcessProbe.Instance;
        try
        {
            var path = ClaudePaths.DaemonRosterFile;
            if (!File.Exists(path)) return [];

            string json;
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            using (var reader = new StreamReader(fs))
                json = reader.ReadToEnd();

            return Parse(json, probe);
        }
        catch
        {
            return [];
        }
    }

    /// <summary>The live workers in a roster file's text. Never throws.</summary>
    internal static IReadOnlyList<DaemonWorker> Parse(string json, IProcessProbe probe)
    {
        try
        {
            if (JsonNode.Parse(json) is not JsonObject root
                || root["workers"] is not JsonObject workers)
                return [];

            var result = new List<DaemonWorker>();
            foreach (var (shortId, node) in workers)
            {
                // One worker at a time (review fixes CP25): a field of an unexpected type skips at most that
                // worker, never the whole roster. Optional fields read leniently (a mistyped one is just absent);
                // only a missing or unusable pid/sessionId disqualifies a worker.
                try
                {
                    if (ReadWorker(shortId, node, probe) is { } worker) result.Add(worker);
                }
                catch { /* this worker only */ }
            }

            // Stable order: oldest worker first, then by key, so the strip doesn't shuffle between reads.
            return result
                .OrderBy(r => r.StartedAt)
                .ThenBy(r => r.ShortId, StringComparer.Ordinal)
                .ToList();
        }
        catch
        {
            return [];
        }
    }

    private static DaemonWorker? ReadWorker(string shortId, JsonNode? node, IProcessProbe probe)
    {
        if (node is not JsonObject w) return null;

        long pidValue = TranscriptJson.AsLong(w["pid"]);
        if (pidValue <= 0 || pidValue > int.MaxValue) return null;
        int pid = (int)pidValue;

        var sessionId = TranscriptJson.AsString(w["sessionId"]) ?? "";
        if (sessionId.Length == 0) return null;

        var startedAtMs = TranscriptJson.AsLong(w["startedAt"]);
        DateTime? recordedStart = startedAtMs > 0
            ? DateTimeOffset.FromUnixTimeMilliseconds(startedAtMs).LocalDateTime
            : null;
        // A killed daemon leaves its roster behind, so the pid is the only tell. With a start time, a pid some
        // newer process has since inherited doesn't count either.
        if (!probe.IsAlive(pid, recordedStart)) return null;

        var cwd = TranscriptJson.AsString(w["cwd"]) ?? "";
        var dispatch = w["dispatch"] as JsonObject;
        var source = TranscriptJson.AsString(dispatch?["source"]) ?? "";

        // The human label: the dispatch seed's explicit name when Claude Code assigned one,
        // else the seed intent (the prompt that launched the worker). Blank → null, so the
        // display falls back to the project name.
        var seed = dispatch?["seed"] as JsonObject;
        var name = TranscriptJson.AsString(seed?["name"]);
        if (string.IsNullOrWhiteSpace(name)) name = TranscriptJson.AsString(seed?["intent"]);
        name = string.IsNullOrWhiteSpace(name) ? null : name.Trim();

        return new DaemonWorker(
            shortId, sessionId, pid, cwd,
            string.IsNullOrEmpty(cwd) ? "" : PathLeaf.Of(cwd),
            source, name, recordedStart ?? DateTime.MinValue);
    }
}
