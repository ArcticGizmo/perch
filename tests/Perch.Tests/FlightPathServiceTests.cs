using Perch.Data;
using Xunit;

namespace Perch.Tests;

public class FlightPathServiceTests
{
    // The sessFlight fixture lives on a distinctive day so ForDay's day filter isolates it from the
    // other fixtures. Its records sit in a tight midday-UTC window, so they fall on one local day on
    // any test machine — derive that day from the fixture's own first timestamp rather than hard-coding.
    private static readonly DateOnly FixtureDay =
        DateOnly.FromDateTime(DateTimeOffset.Parse("2025-06-15T12:00:00Z").LocalDateTime);

    private static FlightLane SingleLane()
    {
        SessionStatsService.IdleThreshold = TimeSpan.FromMinutes(5);
        var report = FlightPathService.ForDay(FixtureDay);
        var lane = Assert.Single(report.Lanes, l => l.SessionId == "sessFlight");
        return lane;
    }

    [Fact]
    public void ForDay_SegmentsEngagedWaitingAndStuck()
    {
        var lane = SingleLane();

        Assert.Equal("proj", lane.Project);
        Assert.Equal("flight", lane.Branch);

        // Four segments in time order: an engaged run, the long gap the human resumed (waiting), a short
        // engaged stretch, then the trailing error streak (stuck).
        Assert.Equal(
            new[] { FlightState.Active, FlightState.Waiting, FlightState.Active, FlightState.Stuck },
            lane.Segments.Select(s => s.State).ToArray());

        // Segments tile forward in time and never overlap.
        for (int i = 1; i < lane.Segments.Count; i++)
            Assert.True(lane.Segments[i].Start >= lane.Segments[i - 1].End);
    }

    [Fact]
    public void ForDay_StuckTailBeginsAtFirstErrorNotRunStart()
    {
        var lane = SingleLane();
        var stuck = Assert.Single(lane.Segments, s => s.State == FlightState.Stuck);

        // The second run starts at 12:20:00 with a prompt + one clean tool call, then three failures from
        // 12:21:00. Only the failing tail (from the first error, + the 30s tail = 2m30s) is red.
        Assert.Equal(TimeSpan.FromSeconds(150), stuck.Duration);
    }

    [Fact]
    public void ForDay_WaitingTimeIsTheHumanGap()
    {
        var lane = SingleLane();

        // Run 1's last record is 12:02:00; +30s tail ends the active bar at 12:02:30. The human's next
        // prompt lands at 12:20:00, so the waiting gap is 17m30s.
        Assert.Equal(TimeSpan.FromSeconds(1050), lane.WaitingTime);

        // Active time (engaged + stuck) is the two runs' spans, tails included: 2m30s + 1m + 2m30s = 6m.
        Assert.Equal(TimeSpan.FromMinutes(6), lane.ActiveTime);
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
}
