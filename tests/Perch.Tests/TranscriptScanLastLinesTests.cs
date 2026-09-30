using Perch.Data;
using Xunit;

namespace Perch.Tests;

/// <summary>Review fixes CP24: a resumed session keeps only the tail of its transcript while reading it (a ring
/// buffer), instead of reading every line into a list and trimming it after.</summary>
public sealed class TranscriptScanLastLinesTests
{
    [Fact]
    public void Keeps_the_last_lines_in_order_and_says_when_it_clipped()
    {
        var (lines, clipped) = TranscriptScan.LastLines(Enumerable.Range(1, 10).Select(i => $"l{i}"), 4);
        Assert.Equal(["l7", "l8", "l9", "l10"], lines);
        Assert.True(clipped);
    }

    [Fact]
    public void A_short_file_is_whole_and_unclipped()
    {
        var (lines, clipped) = TranscriptScan.LastLines(["a", "b"], 4);
        Assert.Equal(["a", "b"], lines);
        Assert.False(clipped);

        (lines, clipped) = TranscriptScan.LastLines(["a", "b", "c", "d"], 4);
        Assert.Equal(4, lines.Count);
        Assert.False(clipped);
    }

    [Fact]
    public void A_long_stream_is_consumed_once_and_keeps_its_tail()
    {
        int produced = 0;
        IEnumerable<string> Source()
        {
            for (int i = 0; i < 100_000; i++) { produced++; yield return i.ToString(); }
        }
        var (lines, clipped) = TranscriptScan.LastLines(Source(), 4000);
        Assert.Equal(100_000, produced);
        Assert.Equal(4000, lines.Count);
        Assert.Equal("96000", lines[0]);
        Assert.Equal("99999", lines[^1]);
        Assert.True(clipped);
    }
}
