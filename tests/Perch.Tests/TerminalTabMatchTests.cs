using Perch.Data;
using Xunit;

namespace Perch.Tests;

/// <summary>
/// Covers <see cref="TerminalTabMatch"/>: finding the Windows Terminal tab that hosts a session from its
/// console title, ignoring Claude Code's animated status glyph, and refusing to guess when it is ambiguous.
/// </summary>
public class TerminalTabMatchTests
{
    private static readonly string[] Tabs =
    [
        "✳ Refactor payment service",
        "◑ Investigate slow dashboard",
        "✳ Update onboarding docs",
        "◐ Fix flaky login test",
    ];

    [Fact]
    public void Finds_the_tab_whose_title_matches()
        => Assert.Equal(2, TerminalTabMatch.FindTab(Tabs, "✳ Update onboarding docs"));

    [Fact]
    public void Ignores_the_status_glyph_which_animates_between_reads()
        => Assert.Equal(1, TerminalTabMatch.FindTab(Tabs, "◐ Investigate slow dashboard"));

    [Fact]
    public void Matches_a_title_without_any_glyph()
        => Assert.Equal(3, TerminalTabMatch.FindTab(Tabs, "Fix flaky login test"));

    [Fact]
    public void No_match_when_no_tab_carries_the_title()
        => Assert.Equal(-1, TerminalTabMatch.FindTab(Tabs, "✳ Something else"));

    [Fact]
    public void No_match_when_two_tabs_carry_the_same_title()
        => Assert.Equal(-1, TerminalTabMatch.FindTab(["Claude Code", "✳ Fix flaky login test", "Claude Code"], "Claude Code"));

    [Fact]
    public void A_title_is_not_matched_by_a_longer_one_that_starts_with_it()
        => Assert.Equal(-1, TerminalTabMatch.FindTab(["✳ Fix flaky login test (part 2)"], "✳ Fix flaky login test"));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("✳ ")]
    public void An_empty_or_glyph_only_title_never_matches(string? title)
        => Assert.Equal(-1, TerminalTabMatch.FindTab(["✳ ", ""], title));
}
