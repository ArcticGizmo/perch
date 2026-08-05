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

    [Fact]
    public void Theme_JsonRoundTrips()
    {
        var custom = Themes.Ember with { Id = "custom-1", Name = "My Theme", Accent = new Rgb(1, 2, 3) };
        var json = System.Text.Json.JsonSerializer.Serialize(custom);
        var back = System.Text.Json.JsonSerializer.Deserialize<Theme>(json)!;
        Assert.Equal(custom, back);                 // records compare by value across every role
        Assert.Equal(new Rgb(1, 2, 3), back.Accent);
    }

    [Fact]
    public void Catalog_ResolvesCustomThenFallsBack()
    {
        var custom = new List<Theme> { Themes.Midnight with { Id = "custom-1", Name = "Mine" } };
        Assert.Equal("custom-1", ThemeCatalog.Resolve("custom-1", custom).Id);
        Assert.Same(Themes.Midnight, ThemeCatalog.Resolve("nope", custom));
        Assert.False(ThemeCatalog.IsBuiltIn("custom-1"));
        Assert.True(ThemeCatalog.IsBuiltIn("ember"));
    }
}
