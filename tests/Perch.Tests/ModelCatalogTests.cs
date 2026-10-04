using Perch.Data;
using Xunit;

namespace Perch.Tests;

public class ModelCatalogTests
{
    [Theory]
    [InlineData("claude-opus-5-5", "Opus 5.5")]
    [InlineData("claude-opus-5", "Opus 5")]
    [InlineData("claude-opus-4-8", "Opus 4.8")]
    [InlineData("claude-opus-5-5[1m]", "Opus 5.5")]          // the 1M variant marker names the same release
    [InlineData("claude-haiku-4-5-20251001", "Haiku 4.5")]   // a dated snapshot id
    [InlineData("claude-fable-5-1", "Fable 5.1")]
    [InlineData("CLAUDE-SONNET-5-5", "Sonnet 5.5")]
    public void DisplayName_KnownId_ShowsFamilyAndVersion(string model, string expected) =>
        Assert.Equal(expected, ModelCatalog.DisplayName(model));

    [Theory]
    [InlineData("opus", "Opus 5.5")]
    [InlineData("sonnet", "Sonnet 5.5")]
    [InlineData("haiku", "Haiku 4.5")]
    [InlineData("fable", "Fable 5.1")]
    [InlineData("opus[1m]", "Opus 5.5")]
    public void DisplayName_Alias_ResolvesToTheFamilysLatest(string alias, string expected) =>
        Assert.Equal(expected, ModelCatalog.DisplayName(alias));

    [Theory]
    [InlineData("claude_opus_5_7")]
    [InlineData("claude-opus-5-7")]
    [InlineData("opusplan")]
    public void DisplayName_UnknownModel_IsShownAsGiven(string model)
    {
        Assert.Null(ModelCatalog.Find(model));
        Assert.Equal(model, ModelCatalog.DisplayName(model));
    }

    [Fact]
    public void Families_ListVersionsOldestFirst_LatestIsTheLast()
    {
        var opus = ModelCatalog.Families.Single(f => f.Alias == "opus");
        Assert.Equal("claude-opus-5-5", opus.Latest.Id);
        Assert.Equal(["4.6", "4.7", "4.8", "5", "5.5"], opus.Versions.Select(v => v.Version));
    }

    [Fact]
    public void Families_EveryIdRoundTripsThroughFind()
    {
        foreach (var version in ModelCatalog.Families.SelectMany(f => f.Versions))
            Assert.Same(version, ModelCatalog.Find(version.Id));
    }
}
