using Perch.Data;
using Xunit;

namespace Perch.Tests;

public class FlightPathServiceTests
{
    // The sessFlight fixtures live on distinctive days so ForDay's day filter isolates each from the
    // other fixtures. Their records sit in tight UTC windows, so they fall on one local day on any test
    // machine — derive each day from the fixture's own first timestamp rather than hard-coding.
    private static readonly DateOnly FlightDay =
        DateOnly.FromDateTime(DateTimeOffset.Parse("2025-06-15T12:00:00Z").LocalDateTime);
    private static readonly DateOnly BlockedDay =
        DateOnly.FromDateTime(DateTimeOffset.Parse("2025-06-16T12:00:00Z").LocalDateTime);
    private static readonly DateOnly TailDay =
        DateOnly.FromDateTime(DateTimeOffset.Parse("2025-06-17T09:00:00Z").LocalDateTime);
    private static readonly DateOnly ApiDay =
        DateOnly.FromDateTime(DateTimeOffset.Parse("2025-03-10T12:00:00Z").LocalDateTime);

    private sealed class FixedClock(DateTime now) : IClockProvider
    {
        public DateTime Now => now;
        public DateTime UtcNow => now.ToUniversalTime();
    }

    private static FlightLane SingleLane(DateOnly day, string sessionId)
    {
        SessionStatsService.IdleThreshold = TimeSpan.FromMinutes(5);
        var report = FlightPathService.ForDay(day);
        return Assert.Single(report.Lanes, l => l.SessionId == sessionId);
    }

    [Fact]
    public void ForDay_SegmentsEngagedIdleAndStuck()
    {
        var lane = SingleLane(FlightDay, "sessFlight");

        Assert.Equal("proj", lane.Project);
        Assert.Equal("flight", lane.Branch);

        // Four segments in time order: an engaged run, the long gap after a completed "done" turn (idle,
        // not a block — no tool call was outstanding), a short engaged stretch, then the trailing error
        // streak (stuck).
        Assert.Equal(
            new[] { FlightState.Active, FlightState.Idle, FlightState.Active, FlightState.Stuck },
            lane.Segments.Select(s => s.State).ToArray());

        // Segments tile forward in time and never overlap.
        for (int i = 1; i < lane.Segments.Count; i++)
            Assert.True(lane.Segments[i].Start >= lane.Segments[i - 1].End);
    }

    [Fact]
    public void ForDay_StuckTailBeginsAtFirstErrorNotRunStart()
    {
        var lane = SingleLane(FlightDay, "sessFlight");
        var stuck = Assert.Single(lane.Segments, s => s.State == FlightState.Stuck);

        // The second run starts at 12:20:00 with a prompt + one clean tool call, then three failures from
        // 12:21:00. Only the failing tail (from the first error, + the 30s tail = 2m30s) is red.
        Assert.Equal(TimeSpan.FromSeconds(150), stuck.Duration);
    }

    [Fact]
    public void ForDay_CompletedThenAwayIsIdleNotWaiting()
    {
        var lane = SingleLane(FlightDay, "sessFlight");

        // Run 1's last record is a completed "done" turn at 12:02:00; +30s tail ends the active bar at
        // 12:02:30. The human's next prompt lands at 12:20:00, so the idle gap is 17m30s — and because
        // the run finished cleanly (no tool call outstanding) it counts as idle, not awaiting input.
        Assert.Equal(TimeSpan.FromSeconds(1050), lane.IdleTime);
        Assert.Equal(TimeSpan.Zero, lane.AwaitingInputTime);

        // Active time (engaged + stuck) is the two runs' spans, tails included: 2m30s + 1m + 2m30s = 6m.
        Assert.Equal(TimeSpan.FromMinutes(6), lane.ActiveTime);
    }

    [Fact]
    public void ForDay_OutstandingToolCallAcrossAGapIsAwaitingInput()
    {
        var lane = SingleLane(BlockedDay, "sessFlightBlocked");

        // The run is left with an outstanding tool_use (b1) at 12:00:30; its result only arrives at
        // 12:30:00 — the session was blocked on you for that gap. Segments: engaged, awaiting input,
        // engaged (once the result lands).
        Assert.Equal(
            new[] { FlightState.Active, FlightState.AwaitingInput, FlightState.Active },
            lane.Segments.Select(s => s.State).ToArray());

        // Active bar ends 30s after 12:00:30 (12:01:00); the result lands at 12:30:00 → 29m blocked.
        Assert.Equal(TimeSpan.FromMinutes(29), lane.AwaitingInputTime);
        Assert.Equal(TimeSpan.Zero, lane.IdleTime);
    }

    [Fact]
    public void ForDay_TodayTrailingTailReflectsHowTheRunWasLeft()
    {
        // Pin "now" to late on the tail fixtures' own day so ForDay treats it as today and paints the
        // open tail up to now. 23:00 local on that day is safely after the 09:00-ish activity on any tz.
        var now = TailDay.ToDateTime(new TimeOnly(23, 0));
        try
        {
            Clock.SetProvider(new FixedClock(now));
            SessionStatsService.IdleThreshold = TimeSpan.FromMinutes(5);
            var report = FlightPathService.ForDay(TailDay);

            // A session that ended with a completed reply and then went quiet trails as idle (done).
            var done = Assert.Single(report.Lanes, l => l.SessionId == "sessFlightDone");
            Assert.Equal(FlightState.Idle, done.Segments[^1].State);
            Assert.True(done.IdleTime > TimeSpan.Zero);
            Assert.Equal(TimeSpan.Zero, done.AwaitingInputTime);
            Assert.Equal(now, done.Segments[^1].End);

            // A session left with an unanswered tool call trails as still-awaiting-input.
            var open = Assert.Single(report.Lanes, l => l.SessionId == "sessFlightOpen");
            Assert.Equal(FlightState.AwaitingInput, open.Segments[^1].State);
            Assert.True(open.AwaitingInputTime > TimeSpan.Zero);
            Assert.Equal(TimeSpan.Zero, open.IdleTime);
            Assert.Equal(now, open.Segments[^1].End);
        }
        finally
        {
            Clock.Reset();
        }
    }

    [Fact]
    public void ForDay_PastDayHasNoOpenTail()
    {
        // On a past day (the default real clock is years after the fixtures) the "done" session simply
        // ends after its completed turn — no trailing idle-to-now is invented.
        var lane = SingleLane(TailDay, "sessFlightDone");
        Assert.DoesNotContain(lane.Segments, s => s.State is FlightState.Idle or FlightState.AwaitingInput);
    }

    [Fact]
    public void ForDay_MarksEveryApiErrorRegardlessOfRecovery()
    {
        // The synthetic isApiErrorMessage record lands at 12:00:10Z with status 529.
        var errAt = DateTimeOffset.Parse("2025-03-10T12:00:10Z").LocalDateTime;

        // A session sitting on the error surfaces it.
        var errored = SingleLane(ApiDay, "sessApiError");
        var mark = Assert.Single(errored.ApiErrors);
        Assert.Equal(529, mark.Status);
        Assert.Equal(errAt, mark.Time);

        // A session that hit the same 529 but then recovered still gets the marker — the flight path
        // records that it happened, not just whether the session ended on it.
        var recovered = SingleLane(ApiDay, "sessApiErrorRecovered");
        Assert.Equal(529, Assert.Single(recovered.ApiErrors).Status);
    }

    [Fact]
    public void ForDay_EmptyDayHasNoLanes()
    {
        SessionStatsService.IdleThreshold = TimeSpan.FromMinutes(5);
        // A day far from any fixture's activity yields an empty, well-formed report.
        var report = FlightPathService.ForDay(new DateOnly(2000, 1, 1));
        Assert.True(report.IsEmpty);
        Assert.Equal(report.WindowStart, report.WindowEnd);
    }

    [Fact]
    public void BuildLane_SkipsOtherDays_ButKeepsTheProjectAndBranchFromThem()
    {
        SessionStatsService.IdleThreshold = TimeSpan.FromMinutes(5);
        var day = new DateTime(2025, 8, 20, 0, 0, 0, DateTimeKind.Unspecified);
        string Utc(DateTime local) => local.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ss.fffZ");
        var path = Path.Combine(Path.GetTempPath(), "perch-flight-" + Guid.NewGuid().ToString("N") + ".jsonl");
        try
        {
            File.WriteAllLines(path,
            [
                // The day before: the only record naming the project and branch.
                $$$"""{"type":"user","timestamp":"{{{Utc(day.AddHours(-3))}}}","cwd":"C:\\work\\lanes","gitBranch":"multi-day","message":{"role":"user","content":"start"}}""",
                $$$"""{"type":"assistant","timestamp":"{{{Utc(day.AddHours(-2))}}}","message":{"role":"assistant","content":[{"type":"text","text":"ok"}]}}""",
                // The day itself.
                $$$"""{"type":"user","timestamp":"{{{Utc(day.AddHours(10))}}}","message":{"role":"user","content":"again"}}""",
                $$$"""{"type":"assistant","timestamp":"{{{Utc(day.AddHours(10).AddMinutes(1))}}}","message":{"role":"assistant","content":[{"type":"text","text":"done"}]}}""",
                // The next day, and a malformed line from it.
                $$$"""{"type":"user","timestamp":"{{{Utc(day.AddDays(1).AddHours(1))}}}","message":{"role":"user","content":"later"}}""",
                $$$"""{"type":"user","timestamp":"{{{Utc(day.AddDays(1).AddHours(2))}}}","message":{""",
            ]);
            var lane = FlightPathService.BuildLane(path, day, day.AddDays(1), null, CancellationToken.None)!;
            Assert.Equal("lanes", lane.Project);
            Assert.Equal("multi-day", lane.Branch);
            Assert.Equal(day.AddHours(10), lane.FirstActivity);
            Assert.Equal(FlightState.Active, Assert.Single(lane.Segments).State);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void ForDay_Cancelled_Throws()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() => FlightPathService.ForDay(FlightDay, cts.Token));
    }
}
