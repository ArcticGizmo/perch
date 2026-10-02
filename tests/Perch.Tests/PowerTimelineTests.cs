using Perch.Data;
using Xunit;
using static Perch.Data.PowerEventKind;

namespace Perch.Tests;

/// <summary>Reading shutdowns off OS power events (docs/session-recovery-plan.md, R3). The sequences mirror the
/// System event log seen in R0: a Fast Startup shutdown logs "entering sleep", "resumed" seconds later, then a boot at
/// the next power-on; a plain sleep logs "entering sleep" and a "resumed" whenever it wakes, with no boot.</summary>
public class PowerTimelineTests
{
    private static readonly DateTime Day = new(2026, 10, 1, 0, 0, 0, DateTimeKind.Local);
    private static DateTime At(int hour, int minute, int second = 0) => Day.AddHours(hour).AddMinutes(minute).AddSeconds(second);
    private static readonly DateTime Before = Day.AddDays(2);

    private static DateTime? Last(DateTime after, params PowerEvent[] events) => PowerTimeline.LastShutdown(events, after, Before);

    [Fact]
    public void FastStartupShutdown_IsTheSleepEnteredBeforeTheBoot()
    {
        Assert.Equal(At(17, 43, 16), Last(Day,
            new(SleepEntered, At(17, 43, 16)), new(Resumed, At(17, 43, 23)), new(Boot, At(17, 47, 54))));
    }

    [Fact]
    public void APlainSleepThatWakes_IsNotAShutdown()
    {
        Assert.Null(Last(Day,
            new(SleepEntered, At(12, 0)), new(Resumed, At(13, 30))));
        // …even when a boot comes much later, after a power loss with nothing logged in between.
        Assert.Null(Last(Day,
            new(SleepEntered, At(12, 0)), new(Resumed, At(13, 30)), new(Boot, At(20, 0))));
    }

    [Fact]
    public void ACandidateWithoutABootAfterIt_IsNotAShutdownYet()
    {
        Assert.Null(Last(Day, new(SleepEntered, At(17, 0)), new(Resumed, At(17, 0, 6))));
    }

    [Fact]
    public void FullShutdownOrRestart_UsesWhenItStarted()
    {
        // A restart logs 1074 (initiated) and 6006 (log stopped); the 1074 is the moment it began.
        Assert.Equal(At(9, 0), Last(Day,
            new(ShutdownStarted, At(9, 0)), new(ShutdownStarted, At(9, 0, 20)), new(SleepEntered, At(9, 0, 25)),
            new(Boot, At(9, 2))));
    }

    [Fact]
    public void ReturnsTheNewestShutdown_InsideTheWindow()
    {
        PowerEvent[] week =
        [
            new(SleepEntered, At(9, 29, 32)), new(Resumed, At(9, 29, 38)), new(Boot, At(23, 59, 29)),
            new(SleepEntered, At(33, 17, 42)), new(Resumed, At(33, 17, 52)), new(Boot, At(37, 4, 18)),
        ];
        Assert.Equal(At(33, 17, 42), PowerTimeline.LastShutdown(week, Day, Before));
        // Only shutdowns after `after` count: from the first boot on, it's the second one; from after both, none.
        Assert.Equal(At(33, 17, 42), PowerTimeline.LastShutdown(week, At(24, 0), Before));
        Assert.Null(PowerTimeline.LastShutdown(week, At(34, 0), Before));
        // And before `before`: the first one only.
        Assert.Equal(At(9, 29, 32), PowerTimeline.LastShutdown(week, Day, At(20, 0)));
    }

    [Fact]
    public void EventsOutOfOrder_AreSortedFirst()
    {
        Assert.Equal(At(17, 0), Last(Day,
            new(Boot, At(17, 5)), new(Resumed, At(17, 0, 6)), new(SleepEntered, At(17, 0))));
    }
}
