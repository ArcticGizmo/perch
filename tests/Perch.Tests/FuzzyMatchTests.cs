using Perch.Data;
using Xunit;

namespace Perch.Tests;

public class FuzzyMatchTests
{
    [Fact]
    public void BlankQuery_NeverMatches()
    {
        Assert.False(FuzzyMatch.TryMatch("", "docs/readme.md", out _));
        Assert.False(FuzzyMatch.TryMatch("   ", "docs/readme.md", out _));
    }

    [Fact]
    public void NonSubsequence_DoesNotMatch()
    {
        Assert.False(FuzzyMatch.TryMatch("xyz", "docs/readme.md", out _));
        // Right letters, wrong order — subsequence must be in order.
        Assert.False(FuzzyMatch.TryMatch("dcamr", "docs/markdown.md", out _));
    }

    [Fact]
    public void Subsequence_Matches_AcrossSegments()
    {
        // m-v-p spread across "markdown-viewer-plan".
        Assert.True(FuzzyMatch.TryMatch("mvp", "docs/markdown-viewer-plan.md", out var r));
        Assert.Equal(3, r.Positions.Count);
    }

    [Fact]
    public void MatchedPositions_AreCorrectIndices()
    {
        Assert.True(FuzzyMatch.TryMatch("read", "docs/readme.md", out var r));
        // "read" lands at the start of the file name "readme.md" → indices 5..8.
        Assert.Equal(new[] { 5, 6, 7, 8 }, r.Positions);
    }

    [Fact]
    public void FileNameMatch_OutranksDirectoryMatch()
    {
        // "plan" appears in both the directory and the file name; the file-name hit should win.
        var paths = new[] { "plan/notes.md", "docs/theming-plan.md" };
        var ranked = FuzzyMatch.Rank("plan", paths, 10);
        Assert.Equal("docs/theming-plan.md", ranked[0].Path);
    }

    [Fact]
    public void PathSegment_CanNarrowResults()
    {
        // Typing part of the directory filters to files under it.
        var paths = new[] { "docs/theming-plan.md", "src/app/readme.md", "docs/setup.md" };
        var ranked = FuzzyMatch.Rank("docs", paths, 10);
        Assert.Equal(2, ranked.Count);
        Assert.All(ranked, h => Assert.StartsWith("docs/", h.Path));
    }

    [Fact]
    public void Rank_RespectsLimit_AndOrdersBestFirst()
    {
        var paths = new[]
        {
            "readme.md", "docs/readme.md", "docs/guide/readme.md", "unrelated.md", "reference/notes.md",
        };
        var ranked = FuzzyMatch.Rank("read", paths, 2);
        Assert.Equal(2, ranked.Count);
        // The bare "readme.md" (file-name match, shortest, least run-up) should rank first.
        Assert.Equal("readme.md", ranked[0].Path);
        // Scores are non-increasing.
        Assert.True(ranked[0].Match.Score >= ranked[1].Match.Score);
    }

    [Fact]
    public void ConsecutiveRun_BeatsScatteredMatch()
    {
        Assert.True(FuzzyMatch.TryMatch("plan", "docs/plan.md", out var tight));
        Assert.True(FuzzyMatch.TryMatch("plan", "docs/pxlxaxn.md", out var loose));
        Assert.True(tight.Score > loose.Score);
    }
}
