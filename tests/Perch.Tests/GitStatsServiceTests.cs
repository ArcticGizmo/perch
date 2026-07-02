using Perch.Data;
using Xunit;

namespace Perch.Tests;

/// <summary>
/// Covers <see cref="GitStatsService.ParseNumstat"/> — the one piece of branching logic in the git-stats
/// feature: folding <c>git diff --numstat</c> output into a single added/deleted total. The process
/// spawning and TTL caching around it can only be exercised against a real repo, so they're left to
/// manual testing; this pins the parsing that would otherwise silently miscount.
/// </summary>
public class GitStatsServiceTests
{
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
