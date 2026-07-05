using System.Text.Json.Nodes;
using Perch.Data;

namespace Perch.Data;

/// <summary>Aggregated statistics for a single day, derived from session transcripts.</summary>
/// <param name="Day">The local calendar day these figures cover.</param>
/// <param name="SessionCount">Distinct sessions (transcripts) with at least one record that day.</param>
/// <param name="ActiveTime">Estimated engagement time — see <see cref="SessionStatsService"/> for how
/// it is inferred from the gaps between transcript records.</param>
internal sealed record DayStats(DateOnly Day, int SessionCount, TimeSpan ActiveTime)
{
    public static DayStats Empty(DateOnly day) => new(day, 0, TimeSpan.Zero);

    /// <summary>One-line summary for the tray menu, e.g. "Today: 4 sessions · 3h 12m active".</summary>
    public string TraySummary()
    {
        if (SessionCount == 0)
            return "Today: no sessions yet";
        var sessions = SessionCount == 1 ? "1 session" : $"{SessionCount} sessions";
        return $"Today: {sessions} · {FormatActive(ActiveTime)} active";
    }

    private static string FormatActive(TimeSpan t) => StatsFormat.Duration(t);
}

/// <summary>Token counts split by billing class. Cache writes price at ~1.25× input, cache reads at
/// ~0.1× — kept separate so the cost estimate is honest rather than treating every token the same.</summary>
internal readonly record struct TokenTotals(long Input, long Output, long CacheWrite, long CacheRead)
{
    public long Total => Input + Output + CacheWrite + CacheRead;
    public static readonly TokenTotals Zero = default;
    public static TokenTotals operator +(TokenTotals a, TokenTotals b) =>
        new(a.Input + b.Input, a.Output + b.Output, a.CacheWrite + b.CacheWrite, a.CacheRead + b.CacheRead);
}

internal sealed record ToolStat(string Tool, int Count);
internal sealed record ProjectStat(string Project, int Sessions, TimeSpan ActiveTime, long Tokens);

/// <summary>Per-model token totals plus the equivalent pay-as-you-go API cost (null when the model's
/// price isn't known, so we never fabricate a number).</summary>
internal sealed record ModelStat(string Model, TokenTotals Tokens, decimal? Cost);

/// <summary>The full Tier 1 + 2 statistics for one day, as shown in the Stats window.</summary>
internal sealed record StatsReport(
    DateOnly Day,
    int SessionCount,
    TimeSpan ActiveTime,
    int Prompts,
    int ToolCalls,
    int SubAgents,
    int Teammates,                // distinct Agent-Teams members that ran in this window
    TokenTotals Tokens,
    TokenTotals TeammateTokens,   // tokens those teammates burned (reported separately, not in Tokens)
    decimal EstimatedCost,        // sum of the per-model costs we could price
    bool CostComplete,            // false when some tokens used a model we have no price for
    IReadOnlyList<ProjectStat> Projects,
    IReadOnlyList<ToolStat> Tools,
    IReadOnlyList<ModelStat> Models,
    IReadOnlyList<ProjectStat> Branches,   // git branch reuses ProjectStat (name in Project field)
    int[] HourlyActiveSeconds)    // 24 bins, local hour -> estimated active seconds
{
    public static StatsReport Empty(DateOnly day) => new(
        day, 0, TimeSpan.Zero, 0, 0, 0, 0, TokenTotals.Zero, TokenTotals.Zero, 0m, true,
        [], [], [], [], new int[24]);
}

/// <summary>One day's headline figures, used for the trend histogram.</summary>
internal sealed record DayPoint(DateOnly Day, int Sessions, TimeSpan Active, long Tokens);

/// <summary>Statistics over a span of days: the aggregate totals (as a <see cref="StatsReport"/>),
/// a contiguous zero-filled daily series for the trend bars, and the trend/streak/record extras.</summary>
internal sealed record RangeReport(
    string ScopeLabel,                       // "Last 7 days", "All time", …
    string TrendLabel,                       // header for the trend section
    StatsReport Totals,
    IReadOnlyList<DayPoint> Trend,           // oldest → newest, zero-filled
    int ActiveDays,                          // days in the scanned span with ≥1 session
    int? StreakDays,                         // consecutive days up to the end (all-time only; else null)
    DateOnly? BusiestDay,
    TimeSpan BusiestDayActive,
    TimeSpan LongestSession,
    DateOnly? FirstActiveDay);

/// <summary>Shared formatting for stat values, so the tray line and the Stats window read identically.</summary>
internal static class StatsFormat
{
    public static string Duration(TimeSpan t)
    {
        if (t.TotalHours >= 1) return $"{(int)t.TotalHours}h {t.Minutes}m";
        if (t.TotalMinutes >= 1) return $"{(int)t.TotalMinutes}m";
        return "<1m";
    }

    /// <summary>Compact token count: 12.3M / 45.6k / 789.</summary>
    public static string Tokens(long n)
    {
        if (n >= 1_000_000) return $"{n / 1_000_000.0:0.0}M";
        if (n >= 1_000) return $"{n / 1_000.0:0.0}k";
        return n.ToString();
    }

    public static string Cost(decimal usd) => usd >= 100m ? $"${usd:0}" : $"${usd:0.00}";
}

/// <summary>
/// Computes session statistics by scanning Claude Code transcripts on disk
/// (<c>~/.claude/projects/{enc-cwd}/{sessionId}.jsonl</c>). Transcript-derived and retroactive: it
/// records nothing of its own, just reads the append-only logs Claude Code already writes, so it works
/// for sessions that ran long before this feature existed — and survives the tray being closed.
///
/// Each transcript is one session; a record's <c>timestamp</c> places it on a calendar day. "Active
/// time" is inferred, not measured: walking a session's records in time order, each gap counts toward
/// active time, but a gap longer than <see cref="IdleThreshold"/> is capped (the user had walked away),
/// so the figure reflects engagement rather than wall-clock since the first message.
///
/// Best-effort throughout — unreadable files and malformed lines are skipped, never thrown. Reads are
/// off the caller's hot path; callers should invoke <see cref="ForDay"/> on a background thread.
///
/// Phase 1 surfaces only the daily headline (sessions + active time); the scan is structured so later
/// phases can layer tokens, cost, tool mix and trends onto the same pass.
/// </summary>
internal static class SessionStatsService
{
    // Gaps longer than this between a session's records are treated as "walked away" and capped, so
    // active time measures engagement. Defaults to SessionMonitor's NeedsAttention window (5 minutes);
    // configurable from settings (see AppSettings.StatsActiveIdleMinutes), applied at startup and when
    // the user changes it.
    public static TimeSpan IdleThreshold { get; set; } = TimeSpan.FromMinutes(5);

    // A small tail credited after a session's last record, so a single quick exchange (one or two
    // records, no gaps) isn't counted as zero active time.
    private static readonly TimeSpan SessionTail = TimeSpan.FromSeconds(30);

    /// <summary>Computes the headline statistics for the given local day.</summary>
    public static DayStats ForDay(DateOnly day)
    {
        var dayStart = day.ToDateTime(TimeOnly.MinValue);
        var dayEnd = dayStart.AddDays(1);

        int sessions = 0;
        var active = TimeSpan.Zero;

        foreach (var file in EnumerateCandidateTranscripts(dayStart))
        {
            var times = ReadTimestampsInRange(file, dayStart, dayEnd);
            if (times.Count == 0)
                continue;
            sessions++;
            active += ActiveSpan(times);
        }

        return new DayStats(day, sessions, active);
    }

    // Only transcripts last modified at or after the window start can contain a record inside it — a
    // file untouched since yesterday cannot hold one of today's records. This keeps a "today" scan to
    // the handful of files actually touched today rather than every transcript ever written. Pass null
    // (the all-time scope) to skip the filter and enumerate every transcript.
    private static IEnumerable<string> EnumerateCandidateTranscripts(DateTime? windowStart)
    {
        foreach (var file in TranscriptLocator.EnumerateTranscripts())
        {
            if (windowStart == null)
            {
                yield return file;
                continue;
            }
            DateTime mtime;
            try { mtime = File.GetLastWriteTime(file); }
            catch { continue; }
            if (mtime >= windowStart)
                yield return file;
        }
    }

    // Reads the record timestamps falling inside [from, to), sorted ascending. Opened shared so an
    // actively-appended transcript can still be read; malformed/partial lines are skipped.
    private static List<DateTime> ReadTimestampsInRange(string file, DateTime from, DateTime to)
    {
        var result = new List<DateTime>();
        try
        {
            foreach (var line in TranscriptScan.ReadLines(file))
            {
                if (line.Length == 0)
                    continue;
                // Cheap substring pre-filter before paying for a JSON parse — most lines are kept,
                // but skipping the rare timestamp-less record (e.g. file-history-snapshot) is free.
                if (!line.Contains("\"timestamp\""))
                    continue;

                try
                {
                    if (TranscriptJson.ParseTimestamp(JsonNode.Parse(line)?["timestamp"]?.GetValue<string>()) is { } t
                        && t >= from && t < to)
                        result.Add(t);
                }
                catch { }
            }
        }
        catch { }

        result.Sort();
        return result;
    }

    // Sums the gaps between consecutive records, capping any gap over the idle threshold, then adds a
    // small tail. times must be sorted ascending and non-empty. (internal for golden tests.)
    internal static TimeSpan ActiveSpan(List<DateTime> times)
    {
        var total = TimeSpan.Zero;
        for (int i = 1; i < times.Count; i++)
        {
            var gap = times[i] - times[i - 1];
            if (gap <= TimeSpan.Zero)
                continue;
            total += gap < IdleThreshold ? gap : IdleThreshold;
        }
        return total + SessionTail;
    }

    // ── Rich report (Tier 1 + 2) ─────────────────────────────────────────────────
    // Per-model equivalent API pricing, USD per million tokens (input, output). Keys are matched as a
    // prefix of the transcript's model id so dated snapshots (claude-haiku-4-5-20251001) resolve too.
    // Cache reads bill at ~0.1× input and cache writes at ~1.25× input — applied in CostOf.
    private static readonly (string key, decimal input, decimal output)[] Prices =
    [
        ("claude-fable-5",   10m, 50m),
        ("claude-opus-4",     5m, 25m),   // 4.5 / 4.6 / 4.7 / 4.8 all share Opus-tier pricing
        ("claude-sonnet-4",   3m, 15m),
        ("claude-haiku-4",    1m,  5m),
    ];

    /// <summary>The full report for a single day. Heavier than <see cref="ForDay"/> — call off the UI thread.</summary>
    public static StatsReport ReportForDay(DateOnly day) =>
        ComposeReport(day, Scan(day.ToDateTime(TimeOnly.MinValue), day.AddDays(1).ToDateTime(TimeOnly.MinValue)).Values);

    /// <summary>Statistics over the inclusive day range [from, to], with a trend series and records.
    /// <paramref name="scopeLabel"/> names the span (e.g. "Last 7 days").</summary>
    public static RangeReport ReportForRange(DateOnly from, DateOnly to, string scopeLabel)
    {
        var buckets = Scan(from.ToDateTime(TimeOnly.MinValue), to.AddDays(1).ToDateTime(TimeOnly.MinValue));
        return Assemble(buckets, scopeLabel, trendFrom: from, to: to, streakMeaningful: false);
    }

    /// <summary>Statistics over every transcript on disk: all-time totals, streak and records, with the
    /// trend showing the last 30 days. Scans the full project history, so it's the slowest path.</summary>
    public static RangeReport ReportAllTime(DateOnly today)
    {
        var buckets = Scan(null, today.AddDays(1).ToDateTime(TimeOnly.MinValue));
        return Assemble(buckets, "All time", trendFrom: today.AddDays(-29), to: today, streakMeaningful: true);
    }

    // Scans candidate transcripts and buckets every accepted record by its local day. Each transcript is
    // one session; a session active on N days contributes to N day-buckets. from==null means all-time
    // (no mtime filter and no lower time bound).
    private static Dictionary<DateOnly, DayBucket> Scan(DateTime? from, DateTime to)
    {
        var map = new Dictionary<DateOnly, DayBucket>();
        foreach (var file in EnumerateCandidateTranscripts(from))
        {
            var sessionId = Path.GetFileNameWithoutExtension(file);
            foreach (var (day, sdd) in ParseSession(file, from, to))
            {
                var bucket = map.TryGetValue(day, out var b) ? b : (map[day] = new DayBucket());
                FoldSession(bucket, sessionId, sdd);
            }

            // Agent-Teams teammates run in their own transcripts under {sessionId}/subagents/, which the
            // session scan above never sees. Roll their counts/tokens into the same day-buckets.
            foreach (var (day, td) in TeamReader.ParseContributions(file, from, to))
            {
                var bucket = map.TryGetValue(day, out var b) ? b : (map[day] = new DayBucket());
                bucket.Teammates += td.Teammates;
                bucket.TeammateTokens += td.Tokens;
            }
        }
        return map;
    }

    // Parses one transcript and buckets its in-range records by local day. The cwd and git branch are
    // session constants, captured once and stamped onto every day-bucket for the session.
    // (internal for golden tests — this is a primary target of the Phase 2 parsing consolidation.)
    internal static Dictionary<DateOnly, SessionDayData> ParseSession(string file, DateTime? from, DateTime to)
    {
        var perDay = new Dictionary<DateOnly, SessionDayData>();
        string project = "", branch = "";
        try
        {
            foreach (var line in TranscriptScan.ReadLines(file))
            {
                if (string.IsNullOrWhiteSpace(line))
                    continue;

                JsonNode? node;
                try { node = JsonNode.Parse(line); }
                catch { continue; }
                if (node == null)
                    continue;

                if (project.Length == 0)
                {
                    var cwd = node["cwd"]?.GetValue<string>();
                    if (!string.IsNullOrEmpty(cwd))
                        project = Path.GetFileName(cwd!.TrimEnd('/', '\\'));
                }
                if (branch.Length == 0)
                {
                    var gb = node["gitBranch"]?.GetValue<string>();
                    if (!string.IsNullOrEmpty(gb))
                        branch = gb!;
                }

                if (TranscriptJson.ParseTimestamp(node["timestamp"]?.GetValue<string>()) is not { } t)
                    continue;
                if ((from != null && t < from) || t >= to)
                    continue;

                var day = DateOnly.FromDateTime(t);
                var data = perDay.TryGetValue(day, out var d) ? d : (perDay[day] = new SessionDayData());
                data.Times.Add(t);

                var type = node["type"]?.GetValue<string>();
                bool isMeta = node["isMeta"]?.GetValue<bool>() ?? false;
                var message = node["message"];
                var content = message?["content"];

                if (type == "user" && !isMeta && IsUserPrompt(content))
                    data.Prompts++;

                // Token usage rides on assistant records; attribute it to the record's model.
                if (message?["usage"] is { } usage)
                {
                    var model = message["model"]?.GetValue<string>() ?? "unknown";
                    var tt = new TokenTotals(
                        TranscriptJson.AsLong(usage["input_tokens"]),
                        TranscriptJson.AsLong(usage["output_tokens"]),
                        TranscriptJson.AsLong(usage["cache_creation_input_tokens"]),
                        TranscriptJson.AsLong(usage["cache_read_input_tokens"]));
                    data.Tokens += tt;
                    data.Models[model] = data.Models.GetValueOrDefault(model, TokenTotals.Zero) + tt;
                }

                if (content is JsonArray blocks)
                {
                    foreach (var b in blocks)
                    {
                        if (TranscriptJson.BlockType(b) != "tool_use")
                            continue;
                        var name = b!["name"]?.GetValue<string>() ?? "tool";
                        data.ToolCalls++;
                        data.ToolCounts[name] = data.ToolCounts.GetValueOrDefault(name) + 1;
                        if (name == "Task")
                            data.SubAgents++;   // the Task tool is how a session spawns a sub-agent
                    }
                }
            }
        }
        catch { }

        foreach (var d in perDay.Values)
        {
            d.Project = project;
            d.Branch = branch;
        }
        return perDay;
    }

    // Folds one session's single-day data into that day's bucket. Active time is per (session, day) so a
    // session spanning midnight is attributed correctly; the longest such span feeds the records.
    private static void FoldSession(DayBucket bucket, string sessionId, SessionDayData s)
    {
        s.Times.Sort();
        var span = ActiveSpan(s.Times);
        bucket.Sessions.Add(sessionId);
        bucket.Active += span;
        if (span > bucket.LongestSession)
            bucket.LongestSession = span;
        bucket.Prompts += s.Prompts;
        bucket.ToolCalls += s.ToolCalls;
        bucket.SubAgents += s.SubAgents;
        bucket.Tokens += s.Tokens;
        AccumulateHourly(bucket.Hourly, s.Times);

        foreach (var (k, v) in s.ToolCounts)
            bucket.ToolCounts[k] = bucket.ToolCounts.GetValueOrDefault(k) + v;
        foreach (var (k, v) in s.Models)
            bucket.Models[k] = bucket.Models.GetValueOrDefault(k, TokenTotals.Zero) + v;
        AddGroup(bucket.Projects, s.Project.Length > 0 ? s.Project : "session", span, s.Tokens.Total);
        if (s.Branch.Length > 0)
            AddGroup(bucket.Branches, s.Branch, span, s.Tokens.Total);
    }

    private static void AddGroup(Dictionary<string, (int sessions, TimeSpan active, long tokens)> d,
        string key, TimeSpan active, long tokens)
    {
        var g = d.GetValueOrDefault(key);
        d[key] = (g.sessions + 1, g.active + active, g.tokens + tokens);
    }

    // A user record counts as a prompt when it carries author text — a plain-string body, or an array
    // with a text block. Records that are only tool_result blocks (the common array shape) don't count.
    private static bool IsUserPrompt(JsonNode? content)
    {
        if (content is JsonValue v && v.TryGetValue<string>(out var s))
            return !string.IsNullOrWhiteSpace(s);
        if (content is JsonArray arr)
            foreach (var b in arr)
                if (b?["type"]?.GetValue<string>() == "text")
                    return true;
        return false;
    }

    // Merges a set of day-buckets into one StatsReport. SessionCount is the distinct union of session ids
    // across the buckets, so a session resumed on several days counts once.
    private static StatsReport ComposeReport(DateOnly day, IEnumerable<DayBucket> buckets)
    {
        var sessions = new HashSet<string>();
        var active = TimeSpan.Zero;
        int prompts = 0, toolCalls = 0, subAgents = 0, teammates = 0;
        var tokens = TokenTotals.Zero;
        var teammateTokens = TokenTotals.Zero;
        var hourly = new int[24];
        var toolCounts = new Dictionary<string, int>();
        var modelTokens = new Dictionary<string, TokenTotals>();
        var projects = new Dictionary<string, (int sessions, TimeSpan active, long tokens)>();
        var branches = new Dictionary<string, (int sessions, TimeSpan active, long tokens)>();

        foreach (var bk in buckets)
        {
            sessions.UnionWith(bk.Sessions);
            active += bk.Active;
            prompts += bk.Prompts;
            toolCalls += bk.ToolCalls;
            subAgents += bk.SubAgents;
            teammates += bk.Teammates;
            tokens += bk.Tokens;
            teammateTokens += bk.TeammateTokens;
            for (int h = 0; h < 24; h++)
                hourly[h] += bk.Hourly[h];
            foreach (var (k, v) in bk.ToolCounts)
                toolCounts[k] = toolCounts.GetValueOrDefault(k) + v;
            foreach (var (k, v) in bk.Models)
                modelTokens[k] = modelTokens.GetValueOrDefault(k, TokenTotals.Zero) + v;
            MergeGroups(projects, bk.Projects);
            MergeGroups(branches, bk.Branches);
        }

        decimal totalCost = 0;
        bool costComplete = true;
        var models = new List<ModelStat>();
        foreach (var (model, tt) in modelTokens)
        {
            var cost = CostOf(model, tt);
            if (cost is { } c) totalCost += c;
            else if (tt.Total > 0) costComplete = false;
            models.Add(new ModelStat(model, tt, cost));
        }
        models.Sort((a, b) => b.Tokens.Total.CompareTo(a.Tokens.Total));

        var toolStats = toolCounts
            .Select(kv => new ToolStat(kv.Key, kv.Value))
            .OrderByDescending(t => t.Count)
            .ToList();

        return new StatsReport(day, sessions.Count, active, prompts, toolCalls, subAgents, teammates,
            tokens, teammateTokens, totalCost, costComplete, ToStats(projects), toolStats, models, ToStats(branches), hourly);
    }

    private static void MergeGroups(
        Dictionary<string, (int sessions, TimeSpan active, long tokens)> into,
        Dictionary<string, (int sessions, TimeSpan active, long tokens)> from)
    {
        foreach (var (k, v) in from)
        {
            var g = into.GetValueOrDefault(k);
            into[k] = (g.sessions + v.sessions, g.active + v.active, g.tokens + v.tokens);
        }
    }

    private static List<ProjectStat> ToStats(Dictionary<string, (int sessions, TimeSpan active, long tokens)> d) =>
        d.Select(kv => new ProjectStat(kv.Key, kv.Value.sessions, kv.Value.active, kv.Value.tokens))
         .OrderByDescending(p => p.ActiveTime)
         .ToList();

    // Builds the range report: aggregate totals, a contiguous zero-filled trend, active-day count, and
    // the records. Streak (consecutive days up to `to`) is only meaningful when the whole history was
    // scanned, so it's null for bounded ranges.
    private static RangeReport Assemble(Dictionary<DateOnly, DayBucket> map, string scopeLabel,
        DateOnly trendFrom, DateOnly to, bool streakMeaningful)
    {
        var totals = ComposeReport(to, map.Values);

        var trend = new List<DayPoint>();
        for (var d = trendFrom; d <= to; d = d.AddDays(1))
            trend.Add(map.TryGetValue(d, out var bk)
                ? new DayPoint(d, bk.Sessions.Count, bk.Active, bk.Tokens.Total)
                : new DayPoint(d, 0, TimeSpan.Zero, 0));

        int activeDays = map.Count(kv => kv.Value.Sessions.Count > 0);

        int? streak = null;
        if (streakMeaningful)
        {
            int n = 0;
            for (var d = to; map.TryGetValue(d, out var bk) && bk.Sessions.Count > 0; d = d.AddDays(-1))
                n++;
            streak = n;
        }

        DateOnly? busiest = null;
        var busiestActive = TimeSpan.Zero;
        var longest = TimeSpan.Zero;
        foreach (var (d, bk) in map)
        {
            if (bk.Active > busiestActive) { busiestActive = bk.Active; busiest = d; }
            if (bk.LongestSession > longest) longest = bk.LongestSession;
        }
        DateOnly? first = map.Count > 0 ? map.Keys.Min() : null;

        string trendLabel = streakMeaningful ? "Active per day (last 30 days)" : "Active per day";
        return new RangeReport(scopeLabel, trendLabel, totals, trend, activeDays, streak,
            busiest, busiestActive, longest, first);
    }

    // Attributes each capped inter-record gap to the hour the gap started in, so the histogram reflects
    // when the day's engagement actually happened. Same idle-threshold rule as ActiveSpan.
    private static void AccumulateHourly(int[] hourly, List<DateTime> times)
    {
        for (int i = 1; i < times.Count; i++)
        {
            var gap = times[i] - times[i - 1];
            if (gap <= TimeSpan.Zero)
                continue;
            var capped = gap < IdleThreshold ? gap : IdleThreshold;
            hourly[times[i - 1].Hour] += (int)capped.TotalSeconds;
        }
        if (times.Count > 0)
            hourly[times[^1].Hour] += (int)SessionTail.TotalSeconds;
    }

    // internal for golden tests — pins the per-model pricing the cost estimate depends on.
    internal static decimal? CostOf(string model, TokenTotals t)
    {
        foreach (var (key, input, output) in Prices)
        {
            if (!model.StartsWith(key, StringComparison.Ordinal))
                continue;
            return (t.Input * input
                  + t.CacheRead * input * 0.10m
                  + t.CacheWrite * input * 1.25m
                  + t.Output * output) / 1_000_000m;
        }
        return null;   // unknown model — surfaced as "—" rather than a fabricated figure
    }

    // Mutable per-(session, day) scratch used only while parsing one transcript.
    // (internal so golden tests can assert the parsed fields.)
    internal sealed class SessionDayData
    {
        public string Project = "";
        public string Branch = "";
        public readonly List<DateTime> Times = new();
        public int Prompts;
        public int ToolCalls;
        public int SubAgents;
        public TokenTotals Tokens = TokenTotals.Zero;
        public readonly Dictionary<string, int> ToolCounts = new();
        public readonly Dictionary<string, TokenTotals> Models = new();
    }

    // Aggregated stats for one calendar day, accumulated across every session active that day.
    private sealed class DayBucket
    {
        public readonly HashSet<string> Sessions = new();
        public TimeSpan Active;
        public int Prompts;
        public int ToolCalls;
        public int SubAgents;
        public int Teammates;                         // Agent-Teams members that ran this day
        public TokenTotals Tokens = TokenTotals.Zero;
        public TokenTotals TeammateTokens = TokenTotals.Zero;  // tokens those teammates burned (kept separate)
        public readonly Dictionary<string, int> ToolCounts = new();
        public readonly Dictionary<string, TokenTotals> Models = new();
        public readonly Dictionary<string, (int sessions, TimeSpan active, long tokens)> Projects = new();
        public readonly Dictionary<string, (int sessions, TimeSpan active, long tokens)> Branches = new();
        public readonly int[] Hourly = new int[24];
        public TimeSpan LongestSession;
    }
}
