using Perch.Theming;
using Xunit;

namespace Perch.Tests;

public class ColorMathTests
{
    [Theory]
    [InlineData(255, 0, 0)]
    [InlineData(24, 24, 32)]
    [InlineData(140, 140, 160)]
    [InlineData(255, 255, 255)]
    [InlineData(0, 0, 0)]
    public void HslRoundTrips(byte r, byte g, byte b)
    {
        var c = new Rgb(r, g, b);
        var (h, s, l) = ColorMath.ToHsl(c);
        var back = ColorMath.FromHsl(h, s, l);
        // Allow ±1 per channel for rounding.
        Assert.InRange(back.R, r - 1, r + 1);
        Assert.InRange(back.G, g - 1, g + 1);
        Assert.InRange(back.B, b - 1, b + 1);
    }

    [Fact]
    public void Retint_KeepsLightness_ChangesHue()
    {
        var grey = new Rgb(60, 60, 66);
        var warm = ColorMath.Retint(grey, 15, 0.15);
        Assert.InRange(ColorMath.Lightness(warm), ColorMath.Lightness(grey) - 0.02, ColorMath.Lightness(grey) + 0.02);
        Assert.True(warm.R >= warm.B, "a ~15° tint should bias red over blue (warm)");
    }

    [Fact]
    public void Retint_ZeroChroma_IsNeutralGrey()
    {
        var c = ColorMath.Retint(new Rgb(80, 40, 40), 15, 0);
        Assert.Equal(c.R, c.G);
        Assert.Equal(c.G, c.B);
    }

    [Fact]
    public void NudgeToContrast_ReachesAA_OnDarkBackground()
    {
        var bg = new Rgb(15, 15, 20);
        var weak = new Rgb(110, 110, 130); // ~3.8:1 — under AA
        Assert.False(Contrast.PassesAA(weak, bg));

        var fixedFg = ColorMath.NudgeToContrast(weak, bg, Contrast.AaText);
        Assert.True(Contrast.PassesAA(fixedFg, bg),
            $"nudged colour is {Contrast.Ratio(fixedFg, bg):0.00}:1");
    }

    [Fact]
    public void NudgeToContrast_LeavesPassingColourUntouched()
    {
        var bg = new Rgb(24, 24, 32);
        var ok = new Rgb(225, 225, 235);
        Assert.Equal(ok, ColorMath.NudgeToContrast(ok, bg, Contrast.AaText));
    }
}
