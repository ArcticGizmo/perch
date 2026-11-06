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

    // The tab to select, or -1 for none, which is how a caller reads any non-Found match.
    private static int IndexOf(IReadOnlyList<string> tabs, string? title) => TerminalTabMatch.Match(tabs, title).Index;

    [Fact]
    public void Finds_the_tab_whose_title_matches()
        => Assert.Equal(2, IndexOf(Tabs, "✳ Update onboarding docs"));

    [Fact]
    public void Ignores_the_status_glyph_which_animates_between_reads()
        => Assert.Equal(1, IndexOf(Tabs, "◐ Investigate slow dashboard"));

    [Fact]
    public void Matches_a_title_without_any_glyph()
        => Assert.Equal(3, IndexOf(Tabs, "Fix flaky login test"));

    [Fact]
    public void No_match_when_no_tab_carries_the_title()
        => Assert.Equal(-1, IndexOf(Tabs, "✳ Something else"));

    [Fact]
    public void No_match_when_two_tabs_carry_the_same_title()
        => Assert.Equal(-1, IndexOf(["Claude Code", "✳ Fix flaky login test", "Claude Code"], "Claude Code"));

    [Fact]
    public void A_title_is_not_matched_by_a_longer_one_that_starts_with_it()
        => Assert.Equal(-1, IndexOf(["✳ Fix flaky login test (part 2)"], "✳ Fix flaky login test"));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("✳ ")]
    public void An_empty_or_glyph_only_title_never_matches(string? title)
        => Assert.Equal(-1, IndexOf(["✳ ", ""], title));

    [Fact]
    public void Match_reports_a_single_tab_as_found()
        => Assert.Equal(new TabMatch(TabMatchKind.Found, 2, 1), TerminalTabMatch.Match(Tabs, "◐ Update onboarding docs"));

    [Fact]
    public void Match_reports_look_alike_tabs_as_ambiguous_with_their_count()
        => Assert.Equal(
            new TabMatch(TabMatchKind.Ambiguous, -1, 3),
            TerminalTabMatch.Match(["✳ Claude Code", "✳ Fix login", "◐ Claude Code", "Claude Code"], "✳ Claude Code"));

    [Fact]
    public void Match_reports_no_tab_as_none()
        => Assert.Equal(new TabMatch(TabMatchKind.None, -1, 0), TerminalTabMatch.Match(Tabs, "✳ Renamed"));

    [Fact]
    public void A_mark_is_a_bird_and_a_token_after_the_title()
        => Assert.Equal("✳ Claude Code \U0001F4261a2b", TerminalTabMatch.Mark("✳ Claude Code", "1a2b"));

    [Fact]
    public void A_marked_title_is_found_by_its_marker_alone()
    {
        var marked = TerminalTabMatch.Mark("✳ Claude Code", "1a2b");
        // WT may already show a newer glyph by the time the marker arrives.
        string[] tabs = ["✳ Claude Code", TerminalTabMatch.Mark("◐ Claude Code", "1a2b"), "✳ Claude Code"];
        Assert.Equal(1, TerminalTabMatch.FindMarked(tabs, marked));
    }

    [Fact]
    public void A_marker_not_yet_shown_finds_nothing()
        => Assert.Equal(-1, TerminalTabMatch.FindMarked(
            ["✳ Claude Code", "✳ Claude Code"], TerminalTabMatch.Mark("✳ Claude Code", "1a2b")));

    [Fact]
    public void A_stranded_marker_is_not_mistaken_for_ours()
        => Assert.Equal(-1, TerminalTabMatch.FindMarked(
            [TerminalTabMatch.Mark("✳ Claude Code", "ffff")], TerminalTabMatch.Mark("✳ Claude Code", "1a2b")));

    [Fact]
    public void A_title_without_a_marker_never_finds_a_tab()
        => Assert.Equal(-1, TerminalTabMatch.FindMarked(["✳ Claude Code"], "✳ Claude Code"));
}
