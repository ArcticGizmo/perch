using Perch.Data;
using Xunit;

namespace Perch.Tests;

public class ChangelogParserTests
{
    private const string Sample = """
# Changelog

Some preamble.

---

## [Unreleased]

- work in progress

---

## [v0.2.11] - 2026-07-21

- Stopped the overlay hopping to the front.

---

## [v0.2.10] - 2026-07-20

- Gold unlocks now flip a card.
- Unlock toasts are a separate toggle.

---

## [v0.2.9] - 2026-07-19

- More trophies for the cabinet.

---
""";

    [Fact]
    public void Parse_ReturnsAVersionedSectionPerHeading_SkippingH1()
    {
        var sections = ChangelogParser.Parse(Sample);

        // Unreleased + three versioned sections (the "# Changelog" H1 is not a "## " section).
        Assert.Equal(4, sections.Count);
        Assert.Null(sections[0].Version);                 // Unreleased has no version
        Assert.Equal("v0.2.11", sections[1].Display);
        Assert.Equal(new Version(0, 2, 11), sections[1].Version);
    }

    [Fact]
    public void Parse_TrimsTrailingRuleAndBlankLinesFromBlock()
    {
        var section = ChangelogParser.Parse(Sample)[1]; // v0.2.11

        Assert.Equal("## [v0.2.11] - 2026-07-21", section.Block[0]);
        Assert.DoesNotContain("---", section.Block);
        Assert.NotEqual("", section.Block[^1].Trim());    // no dangling blank line
    }

    [Fact]
    public void UnseenSince_SingleStep_ReturnsOneEntry()
    {
        var unseen = ChangelogParser.UnseenSince(Sample, "v0.2.10", "v0.2.11");

        Assert.Single(unseen);
        Assert.Equal("v0.2.11", unseen[0].Display);
    }

    [Fact]
    public void UnseenSince_MultiStep_ReturnsAllInRange_NewestFirst_ExcludingUnreleased()
    {
        var unseen = ChangelogParser.UnseenSince(Sample, "v0.2.8", "v0.2.11");

        Assert.Equal(3, unseen.Count);
        Assert.Equal(new[] { "v0.2.11", "v0.2.10", "v0.2.9" }, unseen.Select(s => s.Display).ToArray());
        Assert.DoesNotContain(unseen, s => s.Version is null); // Unreleased never included
    }

    [Fact]
    public void UnseenSince_RespectsUpperBound()
    {
        // Current is 0.2.10, so 0.2.11 must not leak in even though it's in the file.
        var unseen = ChangelogParser.UnseenSince(Sample, "v0.2.8", "v0.2.10");

        Assert.Equal(new[] { "v0.2.10", "v0.2.9" }, unseen.Select(s => s.Display).ToArray());
    }

    [Fact]
    public void UnseenSince_SameVersion_ReturnsNothing()
    {
        Assert.Empty(ChangelogParser.UnseenSince(Sample, "v0.2.11", "v0.2.11"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not-a-version")]
    public void UnseenSince_NoUsableLastSeen_ReturnsNothing(string? lastSeen)
    {
        Assert.Empty(ChangelogParser.UnseenSince(Sample, lastSeen, "v0.2.11"));
    }

    [Fact]
    public void UnseenSince_ToleratesMissingLeadingV()
    {
        var unseen = ChangelogParser.UnseenSince(Sample, "0.2.10", "0.2.11");

        Assert.Single(unseen);
        Assert.Equal("v0.2.11", unseen[0].Display);
    }
}
