using Perch.Games;
using Xunit;

namespace Perch.Tests;

public class DrawWordsTests
{
    [Fact]
    public void OfferIsDeterministicForASeed()
    {
        var a = DrawWords.OfferWords(1234);
        var b = DrawWords.OfferWords(1234);
        Assert.Equal(a, b);
    }

    [Fact]
    public void OfferGivesOneWordPerTierFromTheRightBank()
    {
        var o = DrawWords.OfferWords(7);
        Assert.Contains(o.Easy, DrawWords.Words(DrawDifficulty.Easy));
        Assert.Contains(o.Medium, DrawWords.Words(DrawDifficulty.Medium));
        Assert.Contains(o.Hard, DrawWords.Words(DrawDifficulty.Hard));
        Assert.Equal(o.Easy, o.For(DrawDifficulty.Easy));
        Assert.Equal(o.Hard, o.For(DrawDifficulty.Hard));
    }

    [Fact]
    public void EveryWordIsNonEmpty()
    {
        foreach (var d in new[] { DrawDifficulty.Easy, DrawDifficulty.Medium, DrawDifficulty.Hard })
            Assert.All(DrawWords.Words(d), w => Assert.False(string.IsNullOrWhiteSpace(w)));
    }

    [Fact]
    public void EveryWordAndItsHintFitTheServersLengthLimit()
    {
        foreach (var d in new[] { DrawDifficulty.Easy, DrawDifficulty.Medium, DrawDifficulty.Hard })
            Assert.All(DrawWords.Words(d), w =>
            {
                Assert.InRange(w.Length, 1, DrawWords.MaxWordLength);
                Assert.InRange(DrawGuessing.LetterHint(w).Length, 1, DrawWords.MaxWordLength);
            });
    }
}

public class DrawGuessingTests
{
    [Theory]
    [InlineData("ice cream", "icecream")]
    [InlineData("Ice-Cream!", "icecream")]
    [InlineData("  T-Rex  ", "trex")]
    public void NormalizeStripsCaseSpacingAndPunctuation(string input, string expected) =>
        Assert.Equal(expected, DrawGuessing.Normalize(input));

    [Theory]
    [InlineData("ice cream", "Ice-Cream")]
    [InlineData("icecream", "ice cream")]
    [InlineData("  CAT ", "cat")]
    public void IsCorrectIgnoresCaseSpacingAndPunctuation(string guess, string word) =>
        Assert.True(DrawGuessing.IsCorrect(guess, word));

    [Theory]
    [InlineData("dog", "cat")]
    [InlineData("", "cat")]
    [InlineData("   ", "cat")]
    [InlineData("cat", "")]
    public void IsCorrectRejectsMismatchesAndBlanks(string guess, string word) =>
        Assert.False(DrawGuessing.IsCorrect(guess, word));

    [Theory]
    [InlineData("cat", "3")]
    [InlineData("ice cream", "3 5")]
    [InlineData("t-rex", "4")]
    [InlineData("once in a blue moon", "4 2 1 4 4")]
    public void LetterHintCountsLettersPerWord(string word, string expected) =>
        Assert.Equal(expected, DrawGuessing.LetterHint(word));
}

public class DrawScoringTests
{
    [Theory]
    [InlineData(DrawDifficulty.Easy, 10)]
    [InlineData(DrawDifficulty.Medium, 20)]
    [InlineData(DrawDifficulty.Hard, 30)]
    public void BaseByDifficulty(DrawDifficulty d, int expected) => Assert.Equal(expected, DrawScoring.Base(d));

    [Fact]
    public void FirstGuessEarnsFullBaseAndDrawerTwoThirds()
    {
        var (guesser, drawer) = DrawScoring.Points(DrawDifficulty.Hard, attempts: 1);
        Assert.Equal(30, guesser);
        Assert.Equal(20, drawer);
    }

    [Fact]
    public void ExtraGuessesReduceTheGuesserScoreByTwoEach()
    {
        Assert.Equal(28, DrawScoring.Points(DrawDifficulty.Hard, 2).Guesser);
        Assert.Equal(26, DrawScoring.Points(DrawDifficulty.Hard, 3).Guesser);
    }

    [Fact]
    public void GuesserScoreNeverFallsBelowHalfTheBase()
    {
        // Hard base 30 → floor 15; a huge attempt count still floors at 15.
        Assert.Equal(15, DrawScoring.Points(DrawDifficulty.Hard, 100).Guesser);
        // Easy base 10 → floor 5, drawer round(6.67) = 7.
        var (g, d) = DrawScoring.Points(DrawDifficulty.Easy, 50);
        Assert.Equal(5, g);
        Assert.Equal(7, d);
    }
}

public class DrawStrokeCodecTests
{
    private static DrawStroke Stroke(byte c, byte z, params (short, short)[] pts) =>
        new(c, z, pts.Select(p => new DrawPoint(p.Item1, p.Item2)).ToList());

    [Fact]
    public void RoundTripsStrokes()
    {
        var strokes = new[]
        {
            Stroke(0, 1, (10, 20), (30, 40), (50, 60)),
            Stroke(3, 2, (100, 100), (200, 250)),
        };
        var back = DrawStrokeCodec.Decode(DrawStrokeCodec.Encode(strokes));
        Assert.Equal(2, back.Count);
        Assert.Equal(0, back[0].Color);
        Assert.Equal(1, back[0].Size);
        Assert.Equal(3, back[0].Points.Count);
        Assert.Equal(new DrawPoint(30, 40), back[0].Points[1]);
        Assert.Equal(new DrawPoint(200, 250), back[1].Points[1]);
    }

    [Fact]
    public void EmptyOrNullEncodesAndDecodesToNothing()
    {
        Assert.Empty(DrawStrokeCodec.Decode(DrawStrokeCodec.Encode(null)));
        Assert.Empty(DrawStrokeCodec.Decode(DrawStrokeCodec.Encode(new List<DrawStroke>())));
    }

    [Fact]
    public void GarbageDecodesToEmptyRatherThanThrowing()
    {
        Assert.Empty(DrawStrokeCodec.Decode("not json"));
        Assert.Empty(DrawStrokeCodec.Decode("{}"));
        Assert.Empty(DrawStrokeCodec.Decode(""));
    }

    [Fact]
    public void CoordinatesAreClampedToTheCanvas()
    {
        var strokes = new[] { Stroke(0, 0, (-50, 5000)) };
        var back = DrawStrokeCodec.Decode(DrawStrokeCodec.Encode(strokes));
        Assert.Equal(new DrawPoint(0, DrawStrokeCodec.CanvasSize), back[0].Points[0]);
    }

    [Fact]
    public void OutOfRangeColorAndSizeIndicesAreClampedToZero()
    {
        var strokes = new[] { Stroke(200, 200, (10, 10)) };
        var back = DrawStrokeCodec.Decode(DrawStrokeCodec.Encode(strokes));
        Assert.Equal(0, back[0].Color);
        Assert.Equal(0, back[0].Size);
    }

    // The busiest drawing Encode can emit: every stroke slot used, the widest colour index, and every coordinate
    // four digits. Two layouts, since strokes past the point cap are still written (with no points).
    public static TheoryData<int, int> WorstCaseLayouts => new()
    {
        { DrawStrokeCodec.MaxStrokes, DrawStrokeCodec.MaxTotalPoints / DrawStrokeCodec.MaxStrokes },
        { DrawStrokeCodec.MaxStrokes, DrawStrokeCodec.MaxPointsPerStroke },
    };

    [Theory]
    [MemberData(nameof(WorstCaseLayouts))]
    public void TheLargestPossibleDrawingFitsTheServersSizeLimit(int strokes, int pointsPerStroke)
    {
        var busy = Enumerable.Range(0, strokes)
            .Select(_ => new DrawStroke((byte)(DrawPalette.Colors.Count - 1), (byte)(DrawPalette.Sizes.Count - 1),
                Enumerable.Repeat(new DrawPoint(1000, 1000), pointsPerStroke).ToList()))
            .ToList();
        int bytes = System.Text.Encoding.UTF8.GetByteCount(DrawStrokeCodec.Encode(busy));

        Assert.True(bytes <= DrawStrokeCodec.MaxEncodedBytes, $"{bytes} bytes");
        // The 64 KB first planned for the server's CHECK would have refused a legitimate drawing.
        Assert.True(bytes > 128 * 1024, $"{bytes} bytes");
    }

    [Fact]
    public void APayloadOverTheSizeLimitIsNotParsed()
    {
        const string drawing = "{\"v\":1,\"s\":[{\"c\":0,\"z\":0,\"p\":[1,2]}]}";
        string atCap = drawing + new string(' ', DrawStrokeCodec.MaxEncodedBytes - drawing.Length);

        Assert.Single(DrawStrokeCodec.Decode(atCap));
        Assert.Empty(DrawStrokeCodec.Decode(atCap + " "));
    }
}
