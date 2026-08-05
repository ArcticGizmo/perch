using System.Text.Json.Nodes;

namespace Perch.Data;

/// <summary>What a session was doing across a slice of the day, as drawn on the flight-path timeline.</summary>
internal enum FlightState
{
    /// <summary>Engaged — records flowing within the idle threshold (Claude working, or a live exchange).</summary>
    Active,
    /// <summary>A gap where the run was left mid-task with an outstanding tool call — the session was
    /// blocked, waiting on <em>you</em> (a permission/approval prompt). See the caveat on
    /// <see cref="FlightPathService"/>: a single tool call that legitimately ran longer than the idle
    /// threshold is indistinguishable from a permission block in the transcript and lands here too.</summary>
    AwaitingInput,
    /// <summary>A gap where the run had finished cleanly (a completed assistant turn) and the session then
    /// sat idle — task done, waiting for you to come back.</summary>
    Idle,
    /// <summary>The tail of an engaged run whose tool results kept erroring — a possible spin/stuck stretch.</summary>
    Stuck,
}

/// <summary>A contiguous slice of one session's day in a single <see cref="FlightState"/>. Half-open
/// [<see cref="Start"/>, <see cref="End"/>); segments never overlap and are ordered in time.</summary>
internal sealed record FlightSegment(DateTime Start, DateTime End, FlightState State)
{
    public TimeSpan Duration => End - Start;
}

/// <summary>A point-in-time API failure on a lane — a synthetic <c>isApiErrorMessage</c> record Claude
/// Code wrote (e.g. a 529 Overloaded). Overlaid as a marker on the track, not a state segment.</summary>
internal readonly record struct ApiErrorMark(DateTime Time, int Status);

/// <summary>One session's lane on the flight path: who it is, its span across the day, the coloured
/// segments in between, and any API-error marks overlaid on top.</summary>
internal sealed record FlightLane(
    string SessionId,
    string Project,
    string Branch,
    DateTime FirstActivity,
    DateTime LastActivity,
    TimeSpan ActiveTime,          // engaged time (Active + Stuck segments) — the same quantity Stats calls "active"
    TimeSpan AwaitingInputTime,   // time the session sat blocked on a prompt (AwaitingInput segments)
    TimeSpan IdleTime,            // time the session sat done-and-idle (Idle segments)
    IReadOnlyList<FlightSegment> Segments,
    IReadOnlyList<ApiErrorMark> ApiErrors);

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
/// engaged <see cref="FlightState.Active"/> block, and a longer gap is classified by the state the run
/// was left in when it went quiet:
/// <list type="bullet">
/// <item>the run finished cleanly (its last turn was a completed reply, no tool call outstanding) — the
/// session sat done-and-idle → <see cref="FlightState.Idle"/>;</item>
/// <item>the run was left mid-task with an outstanding tool call — the session was blocked waiting on you
/// → <see cref="FlightState.AwaitingInput"/>;</item>
/// <item>the run's tool results kept erroring at its tail → <see cref="FlightState.Stuck"/>.</item>
/// </list>
/// For <em>today</em>, the trailing time after the last run (up to now) is painted with the same
/// idle-vs-awaiting rule, so "finished and walked away" and "still blocked right now" are visible; on a
/// past day there is no such open tail.
///
/// <para><strong>Heuristic caveat.</strong> The live "waitingFor" permission signal lives only in the
/// transient <c>{pid}.json</c> session file, never in the transcript, so this scan can only <em>infer</em>
/// a block from transcript shape. An outstanding tool call across a gap is treated as
/// <see cref="FlightState.AwaitingInput"/> — but a single tool call that genuinely ran longer than the
/// idle threshold (a long build/test) produces the identical shape and is misclassified as awaiting
/// input. The <see cref="FlightState.Idle"/> side (a run that ended with a completed turn) is reliable.</para>
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

        // "now" only matters when the requested day is today — that's when a session can still be sitting
        // open on an idle/blocked tail. Read it through Clock so replay + tests can pin it.
        var now = Clock.Now;
        DateTime? asOf = day == DateOnly.FromDateTime(now) ? Min(now, dayEnd) : null;

        var lanes = new List<FlightLane>();
        foreach (var file in TranscriptLocator.EnumerateTranscripts())
        {
            // A transcript untouched since before the day can't hold one of its records — skip it without
            // opening it, the same mtime prefilter the stats scan uses to keep a day scan cheap.
            try { if (File.GetLastWriteTime(file) < dayStart) continue; }
            catch { continue; }

            if (BuildLane(file, dayStart, dayEnd, asOf) is { } lane)
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

    // Parses one transcript's records within [dayStart, dayEnd) and segments them into a lane. asOf is
    // "now" clamped to the day when the day is today (else null); it extends the trailing segment of a
    // still-open session up to the present. Returns null when the session had no records that day.
    private static FlightLane? BuildLane(string file, DateTime dayStart, DateTime dayEnd, DateTime? asOf)
    {
        var recs = new List<Rec>();
        var apiErrors = new List<ApiErrorMark>();
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
                        project = PathLeaf.Of(cwd!);
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

                // A synthetic API-failure record (isApiErrorMessage) is a point event Claude Code wrote
                // for a failed request — overlay it as a marker. Same field shape TranscriptReader reads;
                // here we keep every occurrence in the day, not just a trailing one.
                if (node["isApiErrorMessage"]?.GetValue<bool>() == true)
                    apiErrors.Add(new ApiErrorMark(t, (int)TranscriptJson.AsLong(node["apiErrorStatus"])));

                var content = node["message"]?["content"];
                recs.Add(ParseRec(t, content));
            }
        }
        catch { }

        if (recs.Count == 0)
            return null;

        recs.Sort((a, b) => a.Time.CompareTo(b.Time));

        var segments = new List<FlightSegment>();
        var active = TimeSpan.Zero;
        var awaiting = TimeSpan.Zero;
        var idle = TimeSpan.Zero;

        var runStart = recs[0].Time;
        var prev = recs[0].Time;
        int streak = 0;                 // consecutive errored results ending at the latest result in this run
        DateTime? streakStart = null;   // time of the first record of the current trailing error streak

        // Tool calls issued but not yet answered within the current run. Non-empty when a gap opens means
        // the run was left mid-task — the session was blocked (see the class-level heuristic caveat).
        // Cleared at every run boundary so it only ever reflects the run that just ended.
        var openTools = new HashSet<string>();

        // Folds one record into the run-local tracking: opens/closes outstanding tool calls and advances
        // the trailing-error streak.
        void Ingest(Rec r)
        {
            foreach (var id in r.Closes) openTools.Remove(id);
            foreach (var id in r.Opens) openTools.Add(id);
            ApplyResult(r, ref streak, ref streakStart);
        }

        // Extends the tail-corrected end of a run's segments, splitting off a Stuck tail when the run
        // ended mid-error-streak. Returns the tail-extended end so the caller can start a gap after it
        // without overlap.
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

        // Adds the gap between an engaged run's (tail-corrected) end and the next mark, coloured by the
        // state the run was left in: an outstanding tool call → blocked/AwaitingInput, else done/Idle.
        void AddGap(DateTime from, DateTime to, bool blocked)
        {
            if (to <= from)
                return;
            if (blocked)
            {
                segments.Add(new FlightSegment(from, to, FlightState.AwaitingInput));
                awaiting += to - from;
            }
            else
            {
                segments.Add(new FlightSegment(from, to, FlightState.Idle));
                idle += to - from;
            }
        }

        Ingest(recs[0]);
        var lastEnd = prev;
        for (int i = 1; i < recs.Count; i++)
        {
            var gap = recs[i].Time - prev;
            if (gap <= SessionStatsService.IdleThreshold)
            {
                Ingest(recs[i]);
                prev = recs[i].Time;
                continue;
            }

            // A real gap: close the current engaged run, classify the empty space by how the run was left,
            // then begin a fresh run at the resuming record.
            bool blocked = openTools.Count > 0;
            var activeEnd = CloseRun(runStart, prev, streak, streakStart);
            AddGap(activeEnd, recs[i].Time, blocked);

            runStart = recs[i].Time;
            prev = recs[i].Time;
            streak = 0;
            streakStart = null;
            openTools.Clear();
            Ingest(recs[i]);
        }
        lastEnd = CloseRun(runStart, prev, streak, streakStart);

        apiErrors.Sort((a, b) => a.Time.CompareTo(b.Time));

        // Today only: a session whose last run has gone quiet is still open — paint the trailing time up
        // to now, done-idle or still-blocked to match how the last run was left.
        if (asOf is { } nowClamp && nowClamp > lastEnd)
        {
            AddGap(lastEnd, nowClamp, openTools.Count > 0);
            lastEnd = nowClamp;
        }

        return new FlightLane(
            Path.GetFileNameWithoutExtension(file),
            project.Length > 0 ? project : "session",
            branch,
            recs[0].Time,
            lastEnd,
            active,
            awaiting,
            idle,
            segments,
            apiErrors);
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

    // Reduces one in-range transcript record to what segmentation needs: the tool_use ids it opens (an
    // assistant turn's tool calls), the tool_use ids it closes (a user turn's tool_results), and the net
    // result outcome of any tool_result it carries.
    private static Rec ParseRec(DateTime time, JsonNode? content)
    {
        var opens = new List<string>();
        var closes = new List<string>();
        var outcome = ResultOutcome.None;

        if (content is JsonArray arr)
        {
            foreach (var b in arr)
            {
                switch (TranscriptJson.BlockType(b))
                {
                    case "tool_use":
                        if (b!["id"]?.GetValue<string>() is { Length: > 0 } id)
                            opens.Add(id);
                        break;
                    case "tool_result":
                        if (b!["tool_use_id"]?.GetValue<string>() is { Length: > 0 } tid)
                            closes.Add(tid);
                        outcome = b["is_error"]?.GetValue<bool>() == true ? ResultOutcome.Error : ResultOutcome.Ok;
                        break;
                }
            }
        }

        return new Rec(time, opens, closes, outcome);
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
    private readonly record struct Rec(
        DateTime Time,
        IReadOnlyList<string> Opens,
        IReadOnlyList<string> Closes,
        ResultOutcome Result);
}
