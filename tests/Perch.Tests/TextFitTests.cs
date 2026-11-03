using Perch.Data;
using Xunit;

namespace Perch.Tests;

/// <summary>
/// <see cref="TextFit"/> (review fixes CP23): the overlay's label truncation. The measure here is "one unit per
/// grapheme", so the width a test passes is the number of user-perceived characters that fit (the ellipsis
/// counts as one). The cut must never split a surrogate pair, a ZWJ emoji sequence or a base + combining mark,
/// which the old UTF-16 cut did.
/// </summary>
public sealed class TextFitTests
{
    private static double Graphemes(string s) => TextFit.GraphemeEnds(s).Length;

    [Theory]
    [InlineData("hello", 5, "hello")]      // fits: unchanged
    [InlineData("hello", 4, "hel…")]
    [InlineData("hello", 1, "…")]          // not even one letter fits beside the ellipsis
    [InlineData("hello", 0, "")]
    [InlineData("", 3, "")]
    public void Plain_text_truncates_with_a_trailing_ellipsis(string text, double width, string expected) =>
        Assert.Equal(expected, TextFit.Truncate(text, width, Graphemes));

    [Fact]
    public void A_surrogate_pair_is_never_split()
    {
        const string text = "ab😀cd";                     // 😀 is two UTF-16 code units
        Assert.Equal("ab😀…", TextFit.Truncate(text, 4, Graphemes));
        Assert.Equal("ab…", TextFit.Truncate(text, 3, Graphemes));
    }

    [Fact]
    public void A_zwj_emoji_sequence_is_kept_whole_or_dropped_whole()
    {
        const string family = "👨‍👩‍👧";                    // man ZWJ woman ZWJ girl: one grapheme, 8 code units
        string text = "x" + family + "yz";
        Assert.Equal(4, TextFit.GraphemeEnds(text).Length);
        Assert.Equal("x" + family + "…", TextFit.Truncate(text, 3, Graphemes));
        Assert.Equal("x…", TextFit.Truncate(text, 2, Graphemes));
    }

    [Fact]
    public void A_combining_mark_stays_with_its_base()
    {
        const string text = "café au lait";         // e + COMBINING ACUTE ACCENT
        Assert.Equal("café…", TextFit.Truncate(text, 5, Graphemes));
    }

    [Theory]
    [InlineData("main", 10, "main")]                                                  // fits: unchanged
    [InlineData("dependabot/npm_and_yarn/site/site-1e18", 20, "dependabot/…ite-1e18")] // first segment + the end
    [InlineData("dependabot/npm_and_yarn/site/site-1e18", 14, "…ite/site-1e18")]       // too little room after it: just the end
    [InlineData("feature", 4, "…ure")]                                                 // no segments: the end
    [InlineData("feature", 0, "")]
    public void Branch_names_lose_their_middle(string text, double width, string expected) =>
        Assert.Equal(expected, TextFit.ElideMiddle(text, width, Graphemes));

    private static string Upper(string s) => s.ToUpperInvariant();

    [Fact]
    public void Names_in_a_sentence_are_replaced_whole_and_the_words_kept()
    {
        const string a = "dependabot/github_actions/actions-c673", b = "dependabot/npm_and_yarn/site/site-1e18";
        string text = $"Merge {a} into {b} (checks out {b} in perch first)";
        Assert.Equal($"Merge {Upper(a)} into {Upper(b)} (checks out {Upper(b)} in perch first)",
            TextFit.ShortenNames(text, [a, b], Upper));
    }

    [Theory]
    [InlineData("Rebase main-old onto main", "Rebase main-old onto MAIN")]        // glued to more name: not a hit
    [InlineData("origin/main is behind main.", "origin/main is behind MAIN.")]   // a full stop ends the name
    [InlineData("main.x and main", "main.x and MAIN")]                            // a '.' mid-name doesn't
    public void Only_whole_names_are_replaced(string text, string expected) =>
        Assert.Equal(expected, TextFit.ShortenNames(text, ["main"], Upper));

    [Fact]
    public void The_longer_name_wins_and_replacements_are_not_rescanned()
    {
        // "origin/feat" contains "feat"; and a replacement containing a name isn't replaced again.
        Assert.Equal("Track [origin/feat] as [feat]",
            TextFit.ShortenNames("Track origin/feat as feat", ["feat", "origin/feat", null], n => $"[{n}]"));
    }

    [Fact]
    public void Measures_only_logarithmically_many_prefixes()
    {
        int calls = 0;
        var text = new string('a', 1000);
        TextFit.Truncate(text, 10, s => { calls++; return s.Length; });
        Assert.InRange(calls, 1, 15);                      // 1 full measure + ~log2(1000) probes
    }
}
