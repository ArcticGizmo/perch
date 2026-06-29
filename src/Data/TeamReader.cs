using System.Text.Json.Nodes;

namespace Perch.Data;

/// <summary>
/// Reads the persistent <em>teammates</em> (Agent Teams) recorded under a session's
/// <c>{sessionId}/subagents/</c> directory, for the historical surfaces (stats and the history viewer) —
/// as opposed to <see cref="SubAgentReader"/>, which reports the live running/idle state of a session in
/// flight. A teammate is an agent whose <c>.meta.json</c> carries <c>taskKind == "in_process_teammate"</c>;
/// ordinary Task/Agent sub-agent runs (no such taskKind) are ignored here.
///
/// Best-effort and pure: a missing directory or an unreadable/partial file yields nothing, never throws.
/// </summary>
internal static class TeamReader
{
    /// <summary>A teammate that took part in a session: its display name and Claude-assigned colour.</summary>
    internal readonly record struct Teammate(string Name, string? Color);

    /// <summary>Per-day contribution rolled up from a session's teammate transcripts.</summary>
    internal sealed class TeamDayData
    {
        public int Teammates;                   // teammates whose first in-range record fell on this day
        public TokenTotals Tokens = TokenTotals.Zero;
    }

    // {dir}/{sessionId}/subagents for a transcript at {dir}/{sessionId}.jsonl, or null when absent.
    private static string? SubagentsDir(string sessionFile)
    {
        try
        {
            var dir = Path.GetDirectoryName(sessionFile);
            var sessionId = Path.GetFileNameWithoutExtension(sessionFile);
            if (dir == null || string.IsNullOrEmpty(sessionId))
                return null;
            var sub = Path.Combine(dir, sessionId, "subagents");
            return Directory.Exists(sub) ? sub : null;
        }
        catch { return null; }
    }

    // Reads name/colour from a meta sidecar, but only for a teammate; null for an ordinary sub-agent
    // (or a missing/bad sidecar). Mirrors the discriminator in SubAgentReader.ReadAgentMeta.
    private static Teammate? ReadTeammateMeta(string metaPath)
    {
        try
        {
            if (!File.Exists(metaPath))
                return null;
            var node = JsonNode.Parse(File.ReadAllText(metaPath));
            if (node?["taskKind"]?.GetValue<string>() != "in_process_teammate")
                return null;
            var name = node?["name"]?.GetValue<string>();
            return new Teammate(
                string.IsNullOrWhiteSpace(name) ? "teammate" : name!.Trim(),
                node?["color"]?.GetValue<string>());
        }
        catch { return null; }
    }

    /// <summary>The distinct teammates that took part in a session (deduped by name, ordered by name).
    /// Empty when the session ran no team.</summary>
    public static IReadOnlyList<Teammate> GetTeammates(string sessionFile)
    {
        var dir = SubagentsDir(sessionFile);
        if (dir == null)
            return [];

        var byName = new Dictionary<string, Teammate>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var meta in Directory.EnumerateFiles(dir, "agent-*.meta.json"))
                if (ReadTeammateMeta(meta) is { } tm)
                    byName.TryAdd(tm.Name, tm);
        }
        catch { /* directory vanished mid-scan — return what we have */ }

        return byName.Values.OrderBy(t => t.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>
    /// Rolls each teammate transcript's token usage and a per-teammate count into day buckets, using the
    /// teammate records' own timestamps. The count lands on the day of a teammate's first in-range record
    /// (its spawn); tokens land on the day of each usage record — mirroring how the parent session is
    /// bucketed in <see cref="SessionStatsService"/>, so a teammate spanning midnight attributes correctly.
    /// </summary>
    public static Dictionary<DateOnly, TeamDayData> ParseContributions(string sessionFile, DateTime? from, DateTime to)
    {
        var perDay = new Dictionary<DateOnly, TeamDayData>();
        var dir = SubagentsDir(sessionFile);
        if (dir == null)
            return perDay;

        IEnumerable<string> agentFiles;
        try { agentFiles = Directory.EnumerateFiles(dir, "agent-*.jsonl"); }
        catch { return perDay; }

        foreach (var agentFile in agentFiles)
        {
            // Only teammates count here; an ordinary sub-agent run has no in_process_teammate meta.
            if (ReadTeammateMeta(Path.ChangeExtension(agentFile, null) + ".meta.json") is null)
                continue;

            DateOnly? firstDay = null;
            try
            {
                foreach (var line in TranscriptScan.ReadLines(agentFile))
                {
                    if (string.IsNullOrWhiteSpace(line))
                        continue;
                    JsonNode? node;
                    try { node = JsonNode.Parse(line); }
                    catch { continue; }
                    if (node == null)
                        continue;

                    if (TranscriptJson.ParseTimestamp(node["timestamp"]?.GetValue<string>()) is not { } t)
                        continue;
                    if ((from != null && t < from) || t >= to)
                        continue;

                    var day = DateOnly.FromDateTime(t);
                    var data = perDay.TryGetValue(day, out var d) ? d : (perDay[day] = new TeamDayData());
                    firstDay ??= day;

                    if (node["message"]?["usage"] is { } usage)
                        data.Tokens += new TokenTotals(
                            TranscriptJson.AsLong(usage["input_tokens"]),
                            TranscriptJson.AsLong(usage["output_tokens"]),
                            TranscriptJson.AsLong(usage["cache_creation_input_tokens"]),
                            TranscriptJson.AsLong(usage["cache_read_input_tokens"]));
                }
            }
            catch { /* partial/locked transcript — keep whatever days we already bucketed */ }

            if (firstDay is { } fd)
                perDay[fd].Teammates++;   // count the teammate once, on its first in-range day
        }
        return perDay;
    }
}
