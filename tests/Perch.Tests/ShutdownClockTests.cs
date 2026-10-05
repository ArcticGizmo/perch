using Perch.Data;
using Perch.Platform;
using Xunit;

namespace Perch.Tests;

/// <summary>Finding the shutdown that ended Perch's previous run (docs/session-recovery-plan.md, D5), plus the
/// "just before shutdown" window it feeds.</summary>
public class ShutdownClockTests
{
    private static readonly DateTime Now = new(2026, 10, 2, 9, 0, 0, DateTimeKind.Local);

    /// <summary>A power history holding a fixed list of shutdowns; records the window it was last asked about.</summary>
    internal sealed class FakePower(bool supported, params DateTime[] shutdowns) : IPowerHistory
    {
        public (DateTime After, DateTime Before)? Asked { get; private set; }
        public bool IsSupported => supported;

        public DateTime? LastShutdownBetween(DateTime after, DateTime before)
        {
            Asked = (after, before);
            var hits = shutdowns.Where(s => s > after && s < before).ToList();
            return hits.Count == 0 ? null : hits.Max();
        }
    }

    private static LedgerRun Run(DateTime started, DateTime? alive = null, DateTime? clean = null, DateTime? stamped = null) =>
        new() { StartedAt = started, LastAlive = alive, CleanExitAt = clean, ShutdownAt = stamped };

    [Fact]
    public void PerchsOwnStamp_Wins()
    {
        var stamp = Now.AddHours(-12);
        var power = new FakePower(true, Now.AddHours(-11));

        Assert.Equal(stamp, ShutdownClock.Resolve(Run(Now.AddDays(-1), stamp, stamped: stamp), power, Now));
        Assert.Null(power.Asked);
    }

    [Fact]
    public void NoStamp_AsksThePowerHistory_SinceThePreviousRunWasLastAlive()
    {
        var alive = Now.AddHours(-12);
        var shutdown = alive.AddSeconds(-20);   // logged just before the last heartbeat landed: inside the slack
        var power = new FakePower(true, shutdown);

        Assert.Equal(shutdown, ShutdownClock.Resolve(Run(Now.AddDays(-1), alive), power, Now));
        Assert.Equal(alive - SessionRecovery.HeartbeatInterval, power.Asked!.Value.After);
    }

    [Fact]
    public void AShutdownBeforeThePreviousRun_DoesNotCount()
    {
        var alive = Now.AddHours(-12);
        var power = new FakePower(true, Now.AddDays(-2));

        Assert.Null(ShutdownClock.Resolve(Run(Now.AddDays(-1), alive), power, Now));
    }

    [Fact]
    public void PerchCrashed_WithPowerHistoryShowingNoShutdown_IsNoShutdown()
    {
        // The heartbeat alone would look like a power-off; the power history says otherwise.
        var power = new FakePower(true);
        Assert.Null(ShutdownClock.Resolve(Run(Now.AddDays(-1), Now.AddHours(-3)), power, Now));
    }

    [Fact]
    public void NoPowerHistory_FallsBackToTheLastHeartbeat_UnlessPerchExitedCleanly()
    {
        var alive = Now.AddHours(-3);
        var none = new FakePower(false);

        Assert.Equal(alive, ShutdownClock.Resolve(Run(Now.AddDays(-1), alive), none, Now));
        Assert.Null(ShutdownClock.Resolve(Run(Now.AddDays(-1), alive, clean: alive), none, Now));
        Assert.Equal(alive, ShutdownClock.Resolve(Run(Now.AddDays(-1), alive), null, Now));
    }

    [Fact]
    public void FirstRunEver_LooksBackOverTheRecentWindow()
    {
        var shutdown = Now.AddDays(-1);
        var power = new FakePower(true, shutdown, Now.AddDays(-5));

        Assert.Equal(shutdown, ShutdownClock.Resolve(null, power, Now));
        Assert.Equal(Now - SessionRecovery.RecentWindow, power.Asked!.Value.After);
        Assert.Null(ShutdownClock.Resolve(null, new FakePower(false), Now));
    }

    [Theory]
    [InlineData(-11, false)]   // too long before
    [InlineData(-10, true)]    // the window's edge
    [InlineData(-1, true)]
    [InlineData(2, true)]      // flushed just after the stamp (grace)
    [InlineData(3, false)]     // after the grace
    public void JustBeforeShutdown_IsTenMinutesBefore_PlusASmallGraceAfter(int minutesFromShutdown, bool expected)
    {
        var shutdown = Now.AddHours(-1);
        Assert.Equal(expected, SessionRecovery.JustBeforeShutdown(shutdown.AddMinutes(minutesFromShutdown), [shutdown]));
    }
}
