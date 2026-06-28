using Perch.Data;
using Xunit;

namespace Perch.Tests;

/// <summary>
/// Covers the raw stuck/runaway measurements read from the transcript tail. The thresholds that turn
/// these into a <see cref="StuckSignal"/> live in <c>SessionMonitor</c> and are trivial; the parsing
/// and fingerprinting tested here is the logic-heavy part, and the thresholds the fixtures are built
/// around match the ones the monitor applies (error streak ≥ 4; same call ≥ 3× with ≥ 2 failing).
/// </summary>
public class StuckMetricsTests
{
    private const string Cwd = TestEnvironment.FixtureCwd;

    [Fact]
    public void GetStuckMetrics_CountsTrailingErrorStreak()
    {
        var reader = new TranscriptReader();
        var m = reader.GetStuckMetrics("sessStuckErrors", Cwd);
        // Four failing Bash calls in a row, all at the tail.
        Assert.Equal(4, m.TrailingErrorStreak);
    }

    [Fact]
    public void GetStuckMetrics_DetectsFailingLoopEvenWhenErrorsNotConsecutive()
    {
        var reader = new TranscriptReader();
        var m = reader.GetStuckMetrics("sessStuckLoop", Cwd);
        // build/read/build/read/build — the successful reads break the consecutive streak…
        Assert.True(m.TrailingErrorStreak < 4);
        // …but the same failing command still repeats three times, all failing.
        Assert.Equal(3, m.LoopRepeat);
        Assert.Equal(3, m.LoopErrors);
        Assert.Contains("dotnet build", m.LoopLabel);
    }

    [Fact]
    public void GetStuckMetrics_DoesNotFlagRepeatedEditsToSameFile()
    {
        var reader = new TranscriptReader();
        var m = reader.GetStuckMetrics("sessIterating", Cwd);
        // Four *different* successful edits to one file is healthy iterative work, not a loop: each
        // edit's content makes a distinct fingerprint, so nothing repeats and nothing errors.
        Assert.Equal(0, m.TrailingErrorStreak);
        Assert.Equal(1, m.LoopRepeat);
        Assert.Equal(0, m.LoopErrors);
    }

    [Fact]
    public void GetStuckMetrics_BenignForHealthySession()
    {
        var reader = new TranscriptReader();
        var m = reader.GetStuckMetrics("sessA", Cwd);
        Assert.Equal(0, m.TrailingErrorStreak);
        Assert.True(m.LoopErrors == 0);
    }

    [Fact]
    public void GetStuckMetrics_DefaultWhenTranscriptMissing()
    {
        var reader = new TranscriptReader();
        var m = reader.GetStuckMetrics("does-not-exist", Cwd);
        Assert.Equal(default, m);
    }
}
