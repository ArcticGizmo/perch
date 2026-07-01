using Perch.Data;
using Xunit;

namespace Perch.Tests;

public class ModelContextTests
{
    [Fact]
    public void ParseDisplayName_AnsiMarked_ReturnsNameBetweenMarkers()
    {
        // The real "/model" confirmation wraps the display name in ANSI bold markers (ESC[1m … ESC[22m).
        var content = "Set model to [1mSonnet 4.6 (1M context)[22m for this session only";
        Assert.Equal("Sonnet 4.6 (1M context)", ModelContext.ParseDisplayName(content));
    }

    [Fact]
    public void ParseDisplayName_PlainText_StripsTrailingClauses()
    {
        Assert.Equal("Opus 4.8", ModelContext.ParseDisplayName("Set model to Opus 4.8 for this session only"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("some unrelated terminal output")]
    public void ParseDisplayName_NotAModelLine_ReturnsNull(string? content)
    {
        Assert.Null(ModelContext.ParseDisplayName(content));
    }

    [Theory]
    [InlineData("Sonnet 4.6 (1M context)", ModelContext.ExtendedWindow)]
    [InlineData("Opus 4.8 (1M context)", ModelContext.ExtendedWindow)]
    [InlineData("Sonnet 5", ModelContext.ExtendedWindow)]        // 1M by default, no marker
    [InlineData("Sonnet 4.6", ModelContext.DefaultWindow)]
    [InlineData("Opus 4.8", ModelContext.DefaultWindow)]         // plain Opus is the 200k variant
    [InlineData("Haiku 4.5", ModelContext.DefaultWindow)]        // 200k-only
    [InlineData(null, ModelContext.DefaultWindow)]
    [InlineData("", ModelContext.DefaultWindow)]
    public void WindowFor_MapsDisplayNameToWindow(string? displayName, int expected)
    {
        Assert.Equal(expected, ModelContext.WindowFor(displayName));
    }

    [Theory]
    [InlineData("claude-opus-4-8[1m]", ModelContext.ExtendedWindow)]
    [InlineData("opus[1m]", ModelContext.ExtendedWindow)]
    [InlineData("claude-sonnet-5", ModelContext.ExtendedWindow)] // Sonnet 5 defaults to 1M
    [InlineData("sonnet", ModelContext.ExtendedWindow)]          // alias → current Sonnet (5)
    [InlineData("claude-opus-4-8", ModelContext.DefaultWindow)]
    [InlineData("claude-sonnet-4-6", ModelContext.DefaultWindow)]
    [InlineData(null, ModelContext.DefaultWindow)]
    public void WindowForConfiguredModel_OneMSuffixIsExtended(string? model, int expected)
    {
        Assert.Equal(expected, ModelContext.WindowForConfiguredModel(model));
    }
}
