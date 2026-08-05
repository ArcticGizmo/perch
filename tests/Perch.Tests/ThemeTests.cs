using Perch.Theming;
using Xunit;

namespace Perch.Tests;

public class ThemeTests
{
    [Fact]
    public void Midnight_HasNoUnseededRole()
    {
        // A role left at default(Rgb) = black is almost always a forgotten seed in a `with`-derived preset.
        // Midnight is the fully-specified base, so none of its roles may be pure black.
        var black = new Rgb(0, 0, 0);
        foreach (var (name, value) in ThemeRoles.All(Themes.Midnight))
            Assert.True(value != black, $"Midnight role '{name}' is unseeded (black).");
    }

    [Fact]
    public void Midnight_TextRolesPassAA()
    {
        var t = Themes.Midnight;
        Assert.True(Contrast.PassesAA(t.TextPrimary, t.Surface));
        Assert.True(Contrast.PassesAA(t.TextTitle, t.Surface));
        Assert.True(Contrast.PassesAA(t.TextMuted, t.Surface));
        // Overlay body text on the darker overlay surface.
        Assert.True(Contrast.PassesAA(t.TextPrimary, t.OverlaySurface));
        Assert.True(Contrast.PassesAA(t.TextMuted, t.OverlaySurface));
    }

    [Fact]
    public void ById_FindsBuiltIn_AndNullsUnknown()
    {
        Assert.Same(Themes.Midnight, Themes.ById("midnight"));
        Assert.Null(Themes.ById("does-not-exist"));
        Assert.Null(Themes.ById(null));
    }
}
