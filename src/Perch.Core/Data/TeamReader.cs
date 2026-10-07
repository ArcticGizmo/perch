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

    /// <summary>The agent transcripts under a session's <c>subagents/</c> directory whose meta sidecar marks them
    /// a teammate — <paramref name="isTeammate"/> answers that per meta path (the cache memoises it). Empty when
    /// the session ran no team or the directory can't be read.</summary>
    internal static IEnumerable<string> TeammateTranscripts(string sessionFile, Func<string, bool> isTeammate)
    {
        var dir = SubagentsDir(sessionFile);
        if (dir == null)
            return [];
        try
        {
            return Directory.EnumerateFiles(dir, "agent-*.jsonl")
                .Where(f => isTeammate(MetaPathFor(f)))
                .ToList();
        }
        catch { return []; }
    }

    internal static string MetaPathFor(string agentFile) => Path.ChangeExtension(agentFile, null) + ".meta.json";

    /// <summary>True when the meta sidecar at <paramref name="metaPath"/> marks its agent a teammate.</summary>
    internal static bool IsTeammateMeta(string metaPath) => ReadTeammateMeta(metaPath) is not null;

    /// <summary>
    /// The incremental reducer for one teammate transcript: per local day, its token usage and the file
    /// position of its first record that day. That position is what lets a day-filtered report find the
    /// teammate's first in-range record (the day it's counted on) without re-reading the file. With
    /// <see cref="From"/>/<see cref="To"/> set only records in that time range are kept (the one-shot
    /// <see cref="ParseContributions"/>); the cache leaves them null.
    /// </summary>
    internal sealed class TeamFoldState
    {
        public DateTime? From;
        public DateTime? To;
        public long Lines;   // lines stepped so far — the position stamp for FirstLine
        public readonly Dictionary<DateOnly, TeamDay> Days = new();
    }

    internal sealed class TeamDay
    {
        public long FirstLine;                  // position of the first record on this day
        public TokenTotals Tokens = TokenTotals.Zero;
    }

    // Bump when StepTeam's rules change: the persisted cache folded old transcripts under the old rules.
    internal const int TeamFoldVersion = 2;   // 2: counts 1-hour cache writes (TokenTotals.CacheWrite1h)

    internal static readonly LineFolder<TeamFoldState> TeamFolder = new(() => new TeamFoldState(), StepTeam);

    private static void StepTeam(TeamFoldState state, string line)
    {
        long position = state.Lines++;
        if (string.IsNullOrWhiteSpace(line))
            return;
        JsonNode? node;
        try { node = JsonNode.Parse(line); }
        catch { return; }
        if (node == null)
            return;

        if (TranscriptJson.ParseTimestamp(node["timestamp"]?.GetValue<string>()) is not { } t)
            return;
        if ((state.From is { } from && t < from) || (state.To is { } to && t >= to))
            return;

        var day = DateOnly.FromDateTime(t);
        if (!state.Days.TryGetValue(day, out var data))
            state.Days[day] = data = new TeamDay { FirstLine = position };

        if (node["message"]?["usage"] is { } usage)
            data.Tokens += TokenTotals.FromUsage(usage);
    }

    /// <summary>One teammate's contribution to the days <paramref name="inRange"/> accepts: its tokens per day,
    /// and a count of one on the day of its first in-range record (its spawn, within the range).</summary>
    internal static Dictionary<DateOnly, TeamDayData> Contributions(TeamFoldState team, Func<DateOnly, bool> inRange)
    {
        var perDay = new Dictionary<DateOnly, TeamDayData>();
        DateOnly? firstDay = null;
        long firstLine = long.MaxValue;
        foreach (var (day, td) in team.Days)
        {
            if (!inRange(day)) continue;
            perDay[day] = new TeamDayData { Tokens = td.Tokens };
            if (td.FirstLine < firstLine) { firstLine = td.FirstLine; firstDay = day; }
        }
        if (firstDay is { } fd)
            perDay[fd].Teammates++;   // count the teammate once, on its first in-range day
        return perDay;
    }

    /// <summary>
    /// Rolls each teammate transcript's token usage and a per-teammate count into day buckets, using the
    /// teammate records' own timestamps. The count lands on the day of a teammate's first in-range record
    /// (its spawn); tokens land on the day of each usage record — mirroring how the parent session is
    /// bucketed in <see cref="SessionStatsService"/>, so a teammate spanning midnight attributes correctly.
    /// The one-shot form of what the stats cache folds incrementally (same step, same result).
    /// </summary>
    public static Dictionary<DateOnly, TeamDayData> ParseContributions(string sessionFile, DateTime? from, DateTime to)
    {
        var perDay = new Dictionary<DateOnly, TeamDayData>();
        foreach (var agentFile in TeammateTranscripts(sessionFile, IsTeammateMeta))
        {
            var state = new TeamFoldState { From = from, To = to };
            try
            {
                foreach (var line in TranscriptScan.ReadLines(agentFile))
                {
                    try { StepTeam(state, line); }
                    catch { /* one bad line doesn't cost the rest of the file */ }
                }
            }
            catch { /* partial/locked transcript — keep whatever days we already bucketed */ }

            foreach (var (day, td) in Contributions(state, _ => true))
            {
                var into = perDay.TryGetValue(day, out var d) ? d : (perDay[day] = new TeamDayData());
                into.Teammates += td.Teammates;
                into.Tokens += td.Tokens;
            }
        }
        return perDay;
    }
}
