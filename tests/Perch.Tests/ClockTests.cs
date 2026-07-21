using Perch.Data;
using Perch.Platform;
using Xunit;

namespace Perch.Tests;

/// <summary>
/// Covers the replay <see cref="Clock"/> seam and its default <see cref="SystemClock"/> /
/// <see cref="SystemProcessProbe"/> providers. Each test that swaps the ambient provider restores it in
/// a finally so the rest of the (serialised) suite sees the real wall clock again.
/// </summary>
public class ClockTests
{
    private sealed class FixedClock(DateTime now) : IClockProvider
    {
        public DateTime Now => now;
        public DateTime UtcNow => now.ToUniversalTime();
    }

    [Fact]
    public void DefaultProvider_IsSystemClock()
    {
        // Untouched, the ambient clock tracks the wall clock (within a generous slack).
        Assert.True((DateTime.Now - Clock.Now).Duration() < TimeSpan.FromSeconds(5));
    }

    [Fact]
    public void SetProvider_DrivesNow()
    {
        var fixed_ = new DateTime(2026, 7, 21, 9, 30, 0, DateTimeKind.Local);
        try
        {
            Clock.SetProvider(new FixedClock(fixed_));
            Assert.Equal(fixed_, Clock.Now);
        }
        finally
        {
            Clock.Reset();
        }
    }

    [Fact]
    public void ClaudeSession_ElapsedLabel_ReadsTheAmbientClock()
    {
        // Proves the ClaudeSession migration wired through: the "run time" label is measured against
        // Clock.Now, not DateTime.Now, so replay's virtual clock controls the overlay timers.
        var now = new DateTime(2026, 7, 21, 12, 0, 0, DateTimeKind.Local);
        try
        {
            Clock.SetProvider(new FixedClock(now));
            var session = new ClaudeSession("1234", "sess", SessionStatus.Running, "C:\\proj", "proj",
                now, RunningSince: now.AddMinutes(-7));
            Assert.Equal("7m", session.RunningElapsedLabel());
        }
        finally
        {
            Clock.Reset();
        }
    }

    [Fact]
    public void SystemProcessProbe_CurrentProcessIsAlive()
    {
        using var self = System.Diagnostics.Process.GetCurrentProcess();
        Assert.True(SystemProcessProbe.Instance.IsAlive(self.Id));
    }

    [Fact]
    public void SystemProcessProbe_UnusedPidIsNotAlive()
    {
        // No real process owns this id; the probe must report it dead rather than throw.
        Assert.False(SystemProcessProbe.Instance.IsAlive(int.MaxValue));
    }
}
