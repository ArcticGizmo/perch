using Perch.Theming;
using Xunit;

namespace Perch.Tests;

public class ContrastTests
{
    [Fact]
    public void WhiteOnBlack_Is21()
    {
        Assert.Equal(21.0, Contrast.Ratio(new Rgb(255, 255, 255), new Rgb(0, 0, 0)), 3);
    }

    [Fact]
    public void Ratio_IsOrderIndependent()
    {
        var a = new Rgb(0x76, 0x76, 0x76);
        var b = new Rgb(255, 255, 255);
        Assert.Equal(Contrast.Ratio(a, b), Contrast.Ratio(b, a), 6);
    }

    [Fact]
    public void CanonicalThresholdGrey_IsAboutAA()
    {
        // #767676 on white is the well-known ~4.5:1 body-text threshold grey.
        double r = Contrast.Ratio(new Rgb(0x76, 0x76, 0x76), new Rgb(255, 255, 255));
        Assert.InRange(r, 4.48, 4.6);
        Assert.True(Contrast.PassesAA(new Rgb(0x76, 0x76, 0x76), new Rgb(255, 255, 255)));
    }

    [Fact]
    public void SameColour_IsOne()
    {
        Assert.Equal(1.0, Contrast.Ratio(new Rgb(40, 40, 40), new Rgb(40, 40, 40)), 6);
    }

    [Fact]
    public void BestForeground_PicksWhiteOnDark_BlackOnLight()
    {
        var white = new Rgb(255, 255, 255);
        var black = new Rgb(0, 0, 0);
        Assert.Equal(white, Contrast.BestForeground(new Rgb(20, 24, 40)));    // dark navy accent -> white text
        Assert.Equal(black, Contrast.BestForeground(new Rgb(250, 220, 90)));  // light yellow accent -> black text
    }

    [Fact]
    public void BestForeground_AlwaysBeatsTheOtherChoice()
    {
        // Whatever it returns must have at least the contrast of the alternative, for any background.
        foreach (var bg in new[] { new Rgb(0, 0, 0), new Rgb(255, 255, 255), new Rgb(128, 128, 128),
                                    new Rgb(255, 68, 45), new Rgb(56, 189, 248), new Rgb(120, 200, 120) })
        {
            var pick = Contrast.BestForeground(bg);
            var other = pick == new Rgb(255, 255, 255) ? new Rgb(0, 0, 0) : new Rgb(255, 255, 255);
            Assert.True(Contrast.Ratio(pick, bg) >= Contrast.Ratio(other, bg));
        }
    }

    [Fact]
    public void Hex_RoundTrips()
    {
        Assert.Equal(new Rgb(255, 68, 45), Rgb.FromHex("#ff442d"));
        Assert.Equal(new Rgb(255, 68, 45), Rgb.FromHex("FF442D"));
        Assert.Equal("#FF442D", new Rgb(255, 68, 45).ToHex());
        Assert.Equal(new Rgb(0xAA, 0xBB, 0xCC), Rgb.FromHex("#abc")); // 3-digit shorthand
    }
}
