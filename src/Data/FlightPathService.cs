using System.Text.Json.Nodes;

namespace Perch.Data;

/// <summary>What a session was doing across a slice of the day, as drawn on the flight-path timeline.</summary>
internal enum FlightState
{
    /// <summary>Engaged — records flowing within the idle threshold (Claude working, or a live exchange).</summary>
    Active,
    /// <summary>A gap that ended when the human came back and typed — time the session waited on <em>you</em>.</summary>
    Waiting,
    /// <summary>The tail of an engaged run whose tool results kept erroring — a possible spin/stuck stretch.</summary>
    Stuck,
}

/// <summary>A contiguous slice of one session's day in a single <see cref="FlightState"/>. Half-open
/// [<see cref="Start"/>, <see cref="End"/>); segments never overlap and are ordered in time.</summary>
internal sealed record FlightSegment(DateTime Start, DateTime End, FlightState State)
{
    public TimeSpan Duration => End - Start;
}

/// <summary>One session's lane on the flight path: who it is, its span across the day, and the coloured
/// segments in between (the blank space between segments is idle / walked-away time).</summary>
internal sealed record FlightLane(
    string SessionId,
    string Project,
    string Branch,
    DateTime FirstActivity,
    DateTime LastActivity,
    TimeSpan ActiveTime,          // engaged time (Active + Stuck segments) — the same quantity Stats calls "active"
    TimeSpan WaitingTime,         // time this session spent waiting on the human
    IReadOnlyList<FlightSegment> Segments);

/// <summary>The whole day's flight path: every session lane, plus the hour-aligned time window the
/// timeline spans (framed to the day's first and last activity, not a fixed 24h).</summary>
internal sealed record FlightPathReport(
    DateOnly Day,
    DateTime WindowStart,   // hour-floored earliest activity (== WindowEnd only when empty)
    DateTime WindowEnd,     // hour-ceiled latest activity, clamped to the end of the day
    IReadOnlyList<FlightLane> Lanes)
{
    public bool IsEmpty => Lanes.Count == 0;

    public static FlightPathReport Empty(DateOnly day)
    {
        var start = day.ToDateTime(TimeOnly.MinValue);
        return new FlightPathReport(day, start, start, []);
    }
}

/// <summary>
/// Builds the daily "flight path" — a per-session Gantt of the day — from the same append-only
/// transcripts the <see cref="SessionStatsService"/> reads. Where the stats engine collapses a session's
/// records into a single active-time total, this keeps the <em>segments</em>: walking each session's
/// records in time order, a run of records within <see cref="SessionStatsService.IdleThreshold"/> is an
/// engaged <see cref="FlightState.Active"/> block; a longer gap that ends with the human typing a fresh
/// prompt is <see cref="FlightState.Waiting"/> (time the session waited on you); and the tail of a run
/// whose tool results kept erroring is <see cref="FlightState.Stuck"/>. Everything else — a long silent
/// gap with no human prompt to resume it — is left blank (walked away).
///
/// Retroactive and best-effort, exactly like the stats scan: unreadable files and malformed lines are
/// skipped, never thrown, and it works for sessions that ran long before this window existed. Heavier
/// than a headline scan (it reads every in-range record), so callers should invoke <see cref="ForDay"/>
/// off the UI thread.
/// </summary>
internal static class FlightPathService
{
    // A small tail credited after a run's last record, so a lone quick exchange still paints a visible
    // sliver rather than a zero-width bar. Matches SessionStatsService.SessionTail.
    private static readonly TimeSpan SessionTail = TimeSpan.FromSeconds(30);

    // Consecutive errored tool results at the tail of an engaged run that flip it (from the first of
    // that streak onward) to Stuck. Mirrors the meaning of StuckMetrics.TrailingErrorStreak; a modest
    // threshold so one or two recoverable errors don't paint a whole lane red.
    private const int StuckErrorStreak = 3;

    /// <summary>Builds the flight path for the given local day. Off-UI-thread work.</summary>
    public static FlightPathReport ForDay(DateOnly day)
    {
        var dayStart = day.ToDateTime(TimeOnly.MinValue);
        var dayEnd = dayStart.AddDays(1);

        var lanes = new List<FlightLane>();
        foreach (var file in TranscriptLocator.EnumerateTranscripts())
        {
            // A transcript untouched since before the day can't hold one of its records — skip it without
            // opening it, the same mtime prefilter the stats scan uses to keep a day scan cheap.
            try { if (File.GetLastWriteTime(file) < dayStart) continue; }
            catch { continue; }

            if (BuildLane(file, dayStart, dayEnd) is { } lane)
                lanes.Add(lane);
        }

        if (lanes.Count == 0)
            return FlightPathReport.Empty(day);

        // Earliest session on top — the lane list reads like the day's schedule, top to bottom.
        lanes.Sort((a, b) => a.FirstActivity.CompareTo(b.FirstActivity));

        var min = lanes.Min(l => l.FirstActivity);
        var max = lanes.Max(l => l.LastActivity);
        return new FlightPathReport(day, FloorHour(min), CeilHour(max, dayEnd), lanes);
    }

    // Parses one transcript's records within [dayStart, dayEnd) and segments them into a lane. Returns
    // null when the session had no records that day.
    private static FlightLane? BuildLane(string file, DateTime dayStart, DateTime dayEnd)
    {
        var recs = new List<Rec>();
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
                if (t < dayStart || t >= dayEnd)
                    continue;

                var type = node["type"]?.GetValue<string>();
                bool isMeta = node["isMeta"]?.GetValue<bool>() ?? false;
                var content = node["message"]?["content"];

                bool isPrompt = type == "user" && !isMeta && IsUserPrompt(content);
                recs.Add(new Rec(t, isPrompt, ResultOutcomeOf(content)));
            }
        }
        catch { }

        if (recs.Count == 0)
            return null;

        recs.Sort((a, b) => a.Time.CompareTo(b.Time));

        var segments = new List<FlightSegment>();
        var active = TimeSpan.Zero;
        var waiting = TimeSpan.Zero;

        var runStart = recs[0].Time;
        var prev = recs[0].Time;
        int streak = 0;                 // consecutive errored results ending at the latest result in this run
        DateTime? streakStart = null;   // time of the first record of the current trailing error streak

        // Extends the tail-corrected end of a run's segments, splitting off a Stuck tail when the run
        // ended mid-error-streak. Returns the tail-extended end so the caller can start a waiting gap
        // after it without overlap.
        DateTime CloseRun(DateTime rStart, DateTime rEnd, int strk, DateTime? sStart)
        {
            var end = Min(rEnd + SessionTail, dayEnd);
            if (strk >= StuckErrorStreak && sStart is { } ss)
            {
                var stuckStart = ss > rStart ? ss : rStart;
                if (stuckStart > rStart)
                {
                    segments.Add(new FlightSegment(rStart, stuckStart, FlightState.Active));
                    active += stuckStart - rStart;
                }
                segments.Add(new FlightSegment(stuckStart, end, FlightState.Stuck));
                active += end - stuckStart;
            }
            else
            {
                segments.Add(new FlightSegment(rStart, end, FlightState.Active));
                active += end - rStart;
            }
            return end;
        }

        ApplyResult(recs[0], ref streak, ref streakStart);
        var lastEnd = prev;
        for (int i = 1; i < recs.Count; i++)
        {
            var gap = recs[i].Time - prev;
            if (gap <= SessionStatsService.IdleThreshold)
            {
                ApplyResult(recs[i], ref streak, ref streakStart);
                prev = recs[i].Time;
                continue;
            }

            // A real gap: close the current engaged run, then classify the empty space by what resumed it.
            var activeEnd = CloseRun(runStart, prev, streak, streakStart);
            if (recs[i].IsPrompt && recs[i].Time > activeEnd)
            {
                segments.Add(new FlightSegment(activeEnd, recs[i].Time, FlightState.Waiting));
                waiting += recs[i].Time - activeEnd;
            }

            runStart = recs[i].Time;
            prev = recs[i].Time;
            streak = 0;
            streakStart = null;
            ApplyResult(recs[i], ref streak, ref streakStart);
        }
        lastEnd = CloseRun(runStart, prev, streak, streakStart);

        return new FlightLane(
            Path.GetFileNameWithoutExtension(file),
            project.Length > 0 ? project : "session",
            branch,
            recs[0].Time,
            lastEnd,
            active,
            waiting,
            segments);
    }

    // Folds a record's tool-result outcome into the trailing error streak: an error extends it (and, if
    // it starts a new streak, stamps where the streak began); a success anywhere resets it.
    private static void ApplyResult(Rec rec, ref int streak, ref DateTime? streakStart)
    {
        switch (rec.Result)
        {
            case ResultOutcome.Error:
                if (streak == 0)
                    streakStart = rec.Time;
                streak++;
                break;
            case ResultOutcome.Ok:
                streak = 0;
                streakStart = null;
                break;
            // None: no tool result on this record — leave the streak untouched.
        }
    }

    // A user record counts as a prompt when it carries author text — a plain-string body, or an array
    // with a text block. A tool-result-only turn (the common array shape) does not. Mirrors
    // SessionStatsService.IsUserPrompt so "waiting on the human" lines up with the prompt count.
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

    // The net tool-result outcome of a record: the is_error flag of its last tool_result block (results
    // almost always arrive one per record), or None when the record carries no result at all.
    private static ResultOutcome ResultOutcomeOf(JsonNode? content)
    {
        var outcome = ResultOutcome.None;
        if (content is JsonArray arr)
            foreach (var b in arr)
                if (TranscriptJson.BlockType(b) == "tool_result")
                    outcome = b!["is_error"]?.GetValue<bool>() == true ? ResultOutcome.Error : ResultOutcome.Ok;
        return outcome;
    }

    private static DateTime Min(DateTime a, DateTime b) => a < b ? a : b;
    private static DateTime FloorHour(DateTime t) => new(t.Year, t.Month, t.Day, t.Hour, 0, 0, t.Kind);
    private static DateTime CeilHour(DateTime t, DateTime cap)
    {
        var floor = FloorHour(t);
        var ceil = floor == t ? floor : floor.AddHours(1);
        return ceil > cap ? cap : ceil;
    }

    private enum ResultOutcome { None, Ok, Error }

    // One in-range transcript record, reduced to just what segmentation needs.
    private readonly record struct Rec(DateTime Time, bool IsPrompt, ResultOutcome Result);
}
