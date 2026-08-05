using Perch.Theming;
using Xunit;

namespace Perch.Tests;

/// <summary>
/// A contrast audit of Perch's shipped chrome colours. The values are mirrored here as <see cref="Rgb"/>
/// literals because the test project targets <c>Perch.Core</c> and can't reference the Avalonia head where
/// <c>Palette</c> / <c>OverlayCanvas</c> live — so this doubles as a regression anchor: if someone dims a
/// text role back under the WCAG AA floor, this fails. Once M1 lifts the palette into a Core-side
/// <c>Theme</c>, these literals get replaced by the real theme's roles.
/// </summary>
public class PaletteContrastTests
{
    // ── Settings surface (Palette.cs) ──
    private static readonly Rgb SettingsBg    = new(24, 24, 32);   // Palette.FormBg
    private static readonly Rgb SettingsFg    = new(225, 225, 235);// Palette.Fg
    private static readonly Rgb SettingsTitle = new(245, 245, 250);// Palette.Title
    private static readonly Rgb SettingsMuted = new(140, 140, 160);// Palette.Muted

    // ── Overlay surface (OverlayCanvas.cs) ──
    private static readonly Rgb OverlayBg    = new(15, 15, 20);    // OverlayCanvas.BgColor
    private static readonly Rgb OverlayFg    = new(225, 225, 235); // OverlayCanvas.FgColor
    private static readonly Rgb OverlayMuted = new(140, 140, 160); // OverlayCanvas.MutedColor (M0-lifted)

    [Fact]
    public void SettingsBodyText_PassesAA()
    {
        Assert.True(Contrast.PassesAA(SettingsFg, SettingsBg));
        Assert.True(Contrast.PassesAA(SettingsTitle, SettingsBg));
        Assert.True(Contrast.PassesAA(SettingsMuted, SettingsBg));
    }

    [Fact]
    public void OverlayBodyText_PassesAA()
    {
        Assert.True(Contrast.PassesAA(OverlayFg, OverlayBg));
        Assert.True(Contrast.PassesAA(OverlayMuted, OverlayBg));
    }

    [Fact]
    public void OverlayMuted_WasLiftedAboveAA()
    {
        // The M0 fix: (110,110,130) was ~3.8:1 on the overlay bg (under AA); (140,140,160) is ~5.8:1.
        double before = Contrast.Ratio(new Rgb(110, 110, 130), OverlayBg);
        double after = Contrast.Ratio(OverlayMuted, OverlayBg);
        Assert.True(before < Contrast.AaText, $"expected the old muted to fail AA, was {before:0.00}");
        Assert.True(after >= Contrast.AaText, $"expected the new muted to pass AA, was {after:0.00}");
    }

    [Fact]
    public void PrimaryText_ClearsAAA()
    {
        // Primary body text should be comfortably readable, not just scraping AA.
        Assert.True(Contrast.PassesAAA(SettingsFg, SettingsBg));
        Assert.True(Contrast.PassesAAA(OverlayFg, OverlayBg));
    }
}
