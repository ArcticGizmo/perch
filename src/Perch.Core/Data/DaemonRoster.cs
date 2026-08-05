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

            if (JsonNode.Parse(json) is not JsonObject root
                || root["workers"] is not JsonObject workers)
                return [];

            var result = new List<DaemonWorker>();
            foreach (var (shortId, node) in workers)
            {
                if (node is not JsonObject w) continue;

                int pid = (int)(w["pid"]?.GetValue<long>() ?? 0);
                if (pid <= 0 || !probe.IsAlive(pid)) continue;

                var sessionId = w["sessionId"]?.GetValue<string>() ?? "";
                if (sessionId.Length == 0) continue;

                var cwd = w["cwd"]?.GetValue<string>() ?? "";
                var startedAtMs = w["startedAt"]?.GetValue<long>() ?? 0;
                var startedAt = startedAtMs > 0
                    ? DateTimeOffset.FromUnixTimeMilliseconds(startedAtMs).LocalDateTime
                    : DateTime.MinValue;

                var dispatch = w["dispatch"] as JsonObject;
                var source = dispatch?["source"]?.GetValue<string>() ?? "";

                // The human label: the dispatch seed's explicit name when Claude Code assigned one,
                // else the seed intent (the prompt that launched the worker). Blank → null, so the
                // display falls back to the project name.
                var seed = dispatch?["seed"] as JsonObject;
                var name = seed?["name"]?.GetValue<string>();
                if (string.IsNullOrWhiteSpace(name)) name = seed?["intent"]?.GetValue<string>();
                name = string.IsNullOrWhiteSpace(name) ? null : name.Trim();

                result.Add(new DaemonWorker(
                    shortId, sessionId, pid, cwd,
                    string.IsNullOrEmpty(cwd) ? "" : PathLeaf.Of(cwd),
                    source, name, startedAt));
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
}
