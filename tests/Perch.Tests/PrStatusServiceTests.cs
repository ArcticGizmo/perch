using Perch.Data;
using Xunit;

namespace Perch.Tests;

/// <summary>
/// Covers <see cref="PrStatusService.ParsePrJson"/> — the one piece of branching logic in the PR feature:
/// folding <c>gh pr view --json …</c> output into a <see cref="PullRequestInfo"/>, and mapping
/// <c>state</c> + <c>isDraft</c> onto <see cref="PrState"/>. The gh spawning, TTL caching and concurrency
/// gate around it can only be exercised against a real repo + gh install, so they're left to manual
/// testing; this pins the parsing that would otherwise silently drop or mis-colour a PR.
/// </summary>
public class PrStatusServiceTests
{
    [Fact]
    public void ParsesOpenPr()
    {
        var pr = PrStatusService.ParsePrJson(
            """{"number":1135,"url":"https://github.com/o/r/pull/1135","title":"Add PR glyph","state":"OPEN","isDraft":false}""");

        Assert.NotNull(pr);
        Assert.Equal(1135, pr!.Value.Number);
        Assert.Equal("https://github.com/o/r/pull/1135", pr.Value.Url);
        Assert.Equal("Add PR glyph", pr.Value.Title);
        Assert.Equal(PrState.Open, pr.Value.State);
    }

    [Fact]
    public void DraftIsItsOwnStateEvenThoughGhReportsOpen()
    {
        // gh reports a draft as state=OPEN with isDraft=true; we surface it as Draft so the overlay dims it.
        var pr = PrStatusService.ParsePrJson(
            """{"number":7,"url":"u","title":"WIP","state":"OPEN","isDraft":true}""");

        Assert.Equal(PrState.Draft, pr!.Value.State);
    }

    [Theory]
    [InlineData("MERGED", PrState.Merged)]
    [InlineData("merged", PrState.Merged)]   // case-insensitive
    [InlineData("CLOSED", PrState.Closed)]
    public void MapsMergedAndClosedStates(string state, PrState expected)
    {
        var pr = PrStatusService.ParsePrJson(
            $$"""{"number":1,"url":"u","title":"t","state":"{{state}}","isDraft":false}""");

        Assert.Equal(expected, pr!.Value.State);
    }

    [Fact]
    public void MissingNumberYieldsNull()
    {
        Assert.Null(PrStatusService.ParsePrJson("""{"url":"u","title":"t","state":"OPEN"}"""));
    }

    [Fact]
    public void EmptyOrMalformedJsonYieldsNullNotThrow()
    {
        Assert.Null(PrStatusService.ParsePrJson(""));
        Assert.Null(PrStatusService.ParsePrJson("not json"));
        Assert.Null(PrStatusService.ParsePrJson("[]")); // an array, not the expected object
    }

    [Fact]
    public void ToleratesMissingOptionalFields()
    {
        // Only number is required; absent url/title/isDraft default to empty/false, state absent -> Open.
        var pr = PrStatusService.ParsePrJson("""{"number":42}""");

        Assert.NotNull(pr);
        Assert.Equal(42, pr!.Value.Number);
        Assert.Equal("", pr.Value.Url);
        Assert.Equal("", pr.Value.Title);
        Assert.Equal(PrState.Open, pr.Value.State);
    }
}
