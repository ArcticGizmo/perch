using Perch.Data;
using Xunit;

namespace Perch.Tests;

/// <summary>
/// Guards <see cref="OverlaySectionOrder"/>: the default covers every section exactly once, and
/// <see cref="OverlaySectionOrder.Normalize"/> reconciles a saved order (partial, stale, or dirty) back to a
/// complete, de-duplicated list without ever losing or inventing a section.
/// </summary>
public class OverlaySectionOrderTests
{
    [Fact]
    public void DefaultCoversEverySectionExactlyOnce()
    {
        var all = Enum.GetValues<OverlaySection>();
        Assert.Equal(all.Length, OverlaySectionOrder.Default.Count);
        Assert.Equal(all.OrderBy(x => x), OverlaySectionOrder.Default.OrderBy(x => x));
        Assert.Equal(OverlaySectionOrder.Default.Count, OverlaySectionOrder.Default.Distinct().Count());
    }

    [Fact]
    public void NullOrEmptyYieldsDefault()
    {
        Assert.Equal(OverlaySectionOrder.Default, OverlaySectionOrder.Normalize(null));
        Assert.Equal(OverlaySectionOrder.Default, OverlaySectionOrder.Normalize([]));
        Assert.True(OverlaySectionOrder.IsDefault(null));
    }

    [Fact]
    public void RoundTripsAValidFullOrderUnchanged()
    {
        // A full reversal of the default is a valid order and must survive verbatim.
        var reversed = OverlaySectionOrder.Default.Reverse().ToList();
        Assert.Equal(reversed, OverlaySectionOrder.Normalize(reversed));
        Assert.False(OverlaySectionOrder.IsDefault(reversed));
    }

    [Fact]
    public void DropsDuplicatesKeepingFirstOccurrence()
    {
        var input = new[]
        {
            OverlaySection.Sessions, OverlaySection.Sessions, OverlaySection.Friends, OverlaySection.Friends,
        };

        var result = OverlaySectionOrder.Normalize(input).ToList();

        // Dupes dropped, every section present exactly once, and the saved pair keeps its relative order.
        Assert.Equal(result.Count, result.Distinct().Count());
        Assert.Equal(Enum.GetValues<OverlaySection>().Length, result.Count);
        Assert.True(result.IndexOf(OverlaySection.Sessions) < result.IndexOf(OverlaySection.Friends));
    }

    [Fact]
    public void SplicesMissingSectionsAtNaturalPositions()
    {
        // Saved order only knows a few sections (as if the file predates several). The rest must reappear near
        // their default neighbours, and every section must be present exactly once.
        var partial = new[] { OverlaySection.Sessions, OverlaySection.QuickLinks };

        var result = OverlaySectionOrder.Normalize(partial).ToList();

        Assert.Equal(Enum.GetValues<OverlaySection>().Length, result.Count);
        Assert.Equal(result.Count, result.Distinct().Count());
        Assert.Contains(OverlaySection.Sessions, result);
        Assert.Contains(OverlaySection.QuickLinks, result);
        // SystemInfo/ClaudeMetrics default before QuickLinks, so they land ahead of it.
        Assert.True(result.IndexOf(OverlaySection.SystemInfo) < result.IndexOf(OverlaySection.QuickLinks));
        Assert.True(result.IndexOf(OverlaySection.ClaudeMetrics) < result.IndexOf(OverlaySection.QuickLinks));
        // Media/Call default after Sessions, so they land behind it.
        Assert.True(result.IndexOf(OverlaySection.Media) > result.IndexOf(OverlaySection.Sessions));
        Assert.True(result.IndexOf(OverlaySection.Call) > result.IndexOf(OverlaySection.Sessions));
    }

    [Fact]
    public void IgnoresUnknownEnumValues()
    {
        // A value outside the defined members (e.g. written by a newer version) is dropped, not carried.
        var input = new[] { OverlaySection.Media, (OverlaySection)999, OverlaySection.SystemInfo };

        var result = OverlaySectionOrder.Normalize(input);

        Assert.DoesNotContain((OverlaySection)999, result);
        Assert.Equal(OverlaySection.Media, result[0]);
        Assert.Equal(OverlaySection.SystemInfo, result[1]);
        Assert.Equal(Enum.GetValues<OverlaySection>().Length, result.Count);
    }
}
