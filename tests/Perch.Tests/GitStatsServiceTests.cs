using Perch.Data;
using Xunit;

namespace Perch.Tests;

/// <summary>
/// Covers <see cref="GitStatsService.ParseNumstat"/> (folding <c>git diff --numstat</c> output into one total)
/// and the refresh cadence, through the git and clock seams.
/// </summary>
public class GitStatsServiceTests
{
    private sealed class Harness
    {
        public DateTime Now = new(2026, 11, 12, 9, 0, 0, DateTimeKind.Utc);
        public int Runs;
        public readonly GitStatsService Service;

        public Harness()
        {
            Service = new GitStatsService(_ => new GitLineStats(Interlocked.Increment(ref Runs), 0), () => Now)
            {
                Enabled = true,
            };
        }

        // One scan's lookup, then waits for any refresh it scheduled to land.
        public void Get(bool active)
        {
            Service.Get("C:\\repo", active);
            Assert.True(SpinWait.SpinUntil(() => !Service.IsRefreshing, TimeSpan.FromSeconds(5)));
        }
    }

    [Fact]
    public void ActiveSession_RefreshesAfterTheShortTtl()
    {
        var h = new Harness();
        h.Get(active: true);
        Assert.Equal(1, h.Runs);

        h.Now += GitStatsService.ActiveTtl - TimeSpan.FromMilliseconds(1);
        h.Get(active: true);
        Assert.Equal(1, h.Runs);

        h.Now += TimeSpan.FromMilliseconds(1);
        h.Get(active: true);
        Assert.Equal(2, h.Runs);
    }

    [Fact]
    public void IdleSession_WaitsForTheLongTtl()
    {
        var h = new Harness();
        h.Get(active: false);
        Assert.Equal(1, h.Runs);

        h.Now += TimeSpan.FromSeconds(30);
        h.Get(active: false);
        h.Now += TimeSpan.FromSeconds(30);
        h.Get(active: false);
        Assert.Equal(1, h.Runs);

        h.Now += GitStatsService.IdleTtl;
        h.Get(active: false);
        Assert.Equal(2, h.Runs);
    }

    [Fact]
    public void SessionThatStopsWorking_RefreshesOnceToCatchItsLastEdits()
    {
        var h = new Harness();
        h.Get(active: true);
        Assert.Equal(1, h.Runs);

        h.Now += TimeSpan.FromSeconds(1);
        h.Get(active: false);   // stopped inside the active TTL: still refreshes once
        Assert.Equal(2, h.Runs);

        h.Now += TimeSpan.FromSeconds(30);
        h.Get(active: false);
        Assert.Equal(2, h.Runs);
    }

    [Fact]
    public void SumsAddedAndDeletedAcrossFiles()
    {
        var stats = GitStatsService.ParseNumstat(
            "12\t3\tsrc/Foo.cs\n" +
            "0\t7\tsrc/Bar.cs\n" +
            "5\t0\treadme.md\n");

        Assert.Equal(17, stats.Added);
        Assert.Equal(10, stats.Deleted);
        Assert.False(stats.IsEmpty);
    }

    [Fact]
    public void SkipsBinaryFilesReportedAsDashes()
    {
        // git reports "-\t-\t<path>" for binary files; those columns don't parse and must not throw or
        // count. Only the real numeric row here should contribute.
        var stats = GitStatsService.ParseNumstat(
            "-\t-\tassets/logo.png\n" +
            "4\t2\tsrc/Baz.cs\n");

        Assert.Equal(4, stats.Added);
        Assert.Equal(2, stats.Deleted);
    }

    [Fact]
    public void ToleratesCrlfAndBlankAndMalformedLines()
    {
        var stats = GitStatsService.ParseNumstat(
            "8\t1\tsrc/A.cs\r\n" +   // CRLF line endings
            "\r\n" +                  // blank
            "garbage-with-no-tabs\n" + // malformed → skipped
            "\t\t\n" +                // empty columns → parse fails, no throw
            "2\t2\tsrc/B.cs");        // no trailing newline

        Assert.Equal(10, stats.Added);
        Assert.Equal(3, stats.Deleted);
    }

    [Fact]
    public void EmptyOutputIsCleanTree()
    {
        var stats = GitStatsService.ParseNumstat("");

        Assert.Equal(0, stats.Added);
        Assert.Equal(0, stats.Deleted);
        Assert.True(stats.IsEmpty);
    }
}
