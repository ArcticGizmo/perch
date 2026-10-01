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

    [Fact]
    public void Measures_only_logarithmically_many_prefixes()
    {
        int calls = 0;
        var text = new string('a', 1000);
        TextFit.Truncate(text, 10, s => { calls++; return s.Length; });
        Assert.InRange(calls, 1, 15);                      // 1 full measure + ~log2(1000) probes
    }
}
