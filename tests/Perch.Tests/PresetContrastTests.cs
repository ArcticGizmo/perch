using Perch.Theming;
using Xunit;

namespace Perch.Tests;

/// <summary>
/// Contrast contract for every built-in theme: text roles must clear WCAG AA on the surface they're
/// painted on, and the key non-text glyphs must clear the 3:1 floor. A preset that violates its own
/// contract fails the build — this is what makes "accessible by construction" more than a slogan.
/// </summary>
public class PresetContrastTests
{
    public static TheoryData<string> AllThemes()
    {
        var data = new TheoryData<string>();
        foreach (var t in Themes.BuiltIn) data.Add(t.Id);
        return data;
    }

    [Theory]
    [MemberData(nameof(AllThemes))]
    public void TextRolesPassAA(string themeId)
    {
        var t = Themes.ById(themeId)!;

        // Body/title/muted on the window surface.
        AssertAA(t.TextPrimary, t.Surface, themeId, "TextPrimary/Surface");
        AssertAA(t.TextTitle, t.Surface, themeId, "TextTitle/Surface");
        AssertAA(t.TextMuted, t.Surface, themeId, "TextMuted/Surface");

        // Body/muted on the darker overlay surface.
        AssertAA(t.TextPrimary, t.OverlaySurface, themeId, "TextPrimary/OverlaySurface");
        AssertAA(t.TextMuted, t.OverlaySurface, themeId, "TextMuted/OverlaySurface");

        // Text on a raised surface (cards, buttons).
        AssertAA(t.TextPrimary, t.SurfaceRaised, themeId, "TextPrimary/SurfaceRaised");
    }

    [Theory]
    [MemberData(nameof(AllThemes))]
    public void PrimaryTextClearsAAA(string themeId)
    {
        var t = Themes.ById(themeId)!;
        Assert.True(Contrast.PassesAAA(t.TextPrimary, t.Surface),
            $"[{themeId}] primary text should clear AAA on the surface, was {Contrast.Ratio(t.TextPrimary, t.Surface):0.00}");
    }

    [Theory]
    [MemberData(nameof(AllThemes))]
    public void StatusGlyphsClearNonTextFloor(string themeId)
    {
        var t = Themes.ById(themeId)!;
        // Status dots/glyphs are non-text UI (WCAG 1.4.11, 3:1) on the overlay surface.
        AssertNonText(t.StatusRunning, t.OverlaySurface, themeId, "StatusRunning");
        AssertNonText(t.StatusAttention, t.OverlaySurface, themeId, "StatusAttention");
        AssertNonText(t.StatusAwaiting, t.OverlaySurface, themeId, "StatusAwaiting");
        AssertNonText(t.StatusError, t.OverlaySurface, themeId, "StatusError");
        AssertNonText(t.Accent, t.OverlaySurface, themeId, "Accent");
        AssertNonText(t.Jira, t.OverlaySurface, themeId, "Jira");
    }

    private static void AssertAA(Rgb fg, Rgb bg, string themeId, string pair) =>
        Assert.True(Contrast.PassesAA(fg, bg),
            $"[{themeId}] {pair} is {Contrast.Ratio(fg, bg):0.00}:1, under the AA {Contrast.AaText}:1 floor.");

    private static void AssertNonText(Rgb fg, Rgb bg, string themeId, string role) =>
        Assert.True(Contrast.PassesAaLarge(fg, bg),
            $"[{themeId}] {role} is {Contrast.Ratio(fg, bg):0.00}:1, under the non-text {Contrast.NonText}:1 floor.");
}
