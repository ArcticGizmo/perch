using Perch.Data;
using Xunit;
using Xunit.Abstractions;

namespace Perch.Tests;

public class SwearFilterTests
{
    private readonly ITestOutputHelper _out;
    public SwearFilterTests(ITestOutputHelper output) => _out = output;

    // The stem corpus, ROT13'd so the plaintext never lands in a grep of the repo. This is the single place
    // the word list lives; SwearFilter itself holds only the masked Encode() of each. Regenerate the table
    // by running GenerateStemTable and pasting its output.
    private static readonly string[] Rot13Stems =
    [
        "shpx", "fuvg", "nff", "ovgpu", "qvpx", "cvff", "pbpx", "cevpx", "gjng", "jnax", "jnaxre",
        "ohttre", "nefr", "phag", "penc", "qnza", "onfgneq", "obyybpx", "obyybpxf", "nefrubyr",
        "nffubyr", "qhzonff", "wnpxnff", "ohyyfuvg", "tbqqnza", "qbhpur", "fyhg", "juber", "fuvgr",
    ];

    [Fact(Skip = "generator — run manually and paste the output into SwearFilter.Stems")]
    public void GenerateStemTable()
    {
        foreach (var w in Rot13Stems)
            _out.WriteLine($"        0x{SwearFilter.Encode(TestText.Rot13(w)):X16}UL,");
    }

    [Fact]
    public void Table_MatchesEncodeOfEveryStem()
    {
        // Cross-checks that SwearFilter's committed constants are exactly Encode() of the corpus — if the
        // packing ever drifts from whatever generated the table, every stem here still resolves.
        foreach (var w in Rot13Stems)
            Assert.Equal(1, SwearFilter.Count(TestText.Rot13(w)));
    }

    // Inputs are ROT13'd (see TestText) so the plaintext corpus never sits in the repo — the same opacity
    // the filter itself keeps. The comment on each row says what it decodes to and why the count is what it is.
    [Theory]
    [InlineData("shpx guvf shpxvat fuvg", 3)]          // "fuck this fucking shit" — base word, an -ing form, one more
    [InlineData("qnza penc cvff", 3)]                  // "damn crap piss" — a spread of common stems
    [InlineData("ovgpurf fuvggl cvffrq jnaxref", 4)]   // "bitches shitty pissed wankers" — plural/-y/-ed/-ers inflections
    [InlineData("SHPX", 1)]                            // "FUCK" — upper-case still counts
    public void Count_TalliesProfanityAndInflections(string rot13, int expected) =>
        Assert.Equal(expected, SwearFilter.Count(TestText.Rot13(rot13)));

    [Theory]
    [InlineData("pynff cnff nffhzr tenff onff znff")]  // "class pass assume grass bass mass" — the Scunthorpe trap
    [InlineData("pyrna uryyb jbeyq pbqr")]             // "clean hello world code" — ordinary prose
    public void Count_DoesNotFalseMatchCleanText(string rot13) =>
        Assert.Equal(0, SwearFilter.Count(TestText.Rot13(rot13)));

    [Fact]
    public void Count_EmptyAndNullAreZero()
    {
        Assert.Equal(0, SwearFilter.Count(null));
        Assert.Equal(0, SwearFilter.Count(""));
        Assert.Equal(0, SwearFilter.Count("   "));
    }

    [Fact]
    public void Encode_IsMaskedNotBarePacking()
    {
        // The stored form must differ from a plain little-endian pack, or the mask would be a no-op and the
        // table would be trivially readable.
        var word = TestText.Rot13("nff");   // "ass" — a short stem
        ulong bare = 0;
        for (int i = 0; i < word.Length; i++)
            bare |= (ulong)(byte)word[i] << (i * 8);
        Assert.NotEqual(bare, SwearFilter.Encode(word));
    }
}
