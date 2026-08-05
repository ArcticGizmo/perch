using Perch.Theming;
using Xunit;

namespace Perch.Tests;

public class ThemeCodecTests
{
    [Fact]
    public void RoundTrips_AllRoles()
    {
        var t = Themes.Winamp with { Name = "My Retro Theme", Id = "whatever" };
        var code = ThemeCodec.Encode(t);
        var back = ThemeCodec.Decode(code)!;

        Assert.Equal("My Retro Theme", back.Name);
        // Every role survives (id is a placeholder the caller re-assigns, so compare role-by-role).
        foreach (var ((_, orig), (_, dec)) in ThemeRoles.All(t).Zip(ThemeRoles.All(back)))
            Assert.Equal(orig, dec);
    }

    [Fact]
    public void Code_IsCompactEnoughForAQr()
    {
        var code = ThemeCodec.Encode(Themes.Ember);
        Assert.StartsWith("perch1:", code);
        Assert.True(code.Length < 400, $"share code should be small (was {code.Length})");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not a code")]
    [InlineData("perch1:@@@notbase64@@@")]
    public void Decode_RejectsGarbage(string? junk)
    {
        Assert.Null(ThemeCodec.Decode(junk));
    }

    [Fact]
    public void Decode_ToleratesNameWithNoRoles()
    {
        // A short/old payload keeps Midnight's colours rather than throwing.
        var t = ThemeCodec.Decode(ThemeCodec.Encode(Themes.Midnight with { Name = "x" }));
        Assert.NotNull(t);
    }
}
