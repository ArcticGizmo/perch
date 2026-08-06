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

    [Fact]
    public void NoStatusCheckRollupYieldsNoChecks()
    {
        var pr = PrStatusService.ParsePrJson(
            """{"number":1,"url":"u","title":"t","state":"OPEN","isDraft":false}""");

        Assert.Empty(pr!.Value.Checks);
        Assert.Equal(PrChecksRollup.None, pr.Value.ChecksRollup);
    }

    [Fact]
    public void ParsesCheckRunNameStatusAndConclusion()
    {
        var pr = PrStatusService.ParsePrJson(
            """
            {"number":1,"url":"u","title":"t","state":"OPEN","isDraft":false,"statusCheckRollup":[
              {"__typename":"CheckRun","name":"build","status":"COMPLETED","conclusion":"SUCCESS"},
              {"__typename":"CheckRun","name":"tests","status":"COMPLETED","conclusion":"FAILURE"},
              {"__typename":"CheckRun","name":"lint","status":"IN_PROGRESS","conclusion":""}
            ]}
            """);

        var checks = pr!.Value.Checks;
        Assert.Equal(3, checks.Count);
        Assert.Equal(new PrCheck("build", PrCheckState.Success), checks[0]);
        Assert.Equal(new PrCheck("tests", PrCheckState.Failure), checks[1]);
        Assert.Equal(new PrCheck("lint", PrCheckState.Pending), checks[2]);
    }

    [Theory]
    // Completed conclusions that don't block are folded to Success; the rest to Failure.
    [InlineData("COMPLETED", "NEUTRAL", PrCheckState.Success)]
    [InlineData("COMPLETED", "SKIPPED", PrCheckState.Success)]
    [InlineData("COMPLETED", "success", PrCheckState.Success)]   // case-insensitive
    [InlineData("COMPLETED", "TIMED_OUT", PrCheckState.Failure)]
    [InlineData("COMPLETED", "CANCELLED", PrCheckState.Failure)]
    [InlineData("COMPLETED", "ACTION_REQUIRED", PrCheckState.Failure)]
    [InlineData("COMPLETED", "", PrCheckState.Pending)]          // decided but no conclusion yet
    [InlineData("QUEUED", "", PrCheckState.Pending)]
    public void MapsCheckRunConclusions(string status, string conclusion, PrCheckState expected)
    {
        var pr = PrStatusService.ParsePrJson(
            $$"""
            {"number":1,"state":"OPEN","statusCheckRollup":[
              {"__typename":"CheckRun","name":"c","status":"{{status}}","conclusion":"{{conclusion}}"}
            ]}
            """);

        Assert.Equal(expected, pr!.Value.Checks[0].State);
    }

    [Theory]
    // Legacy commit-status contexts carry a single `state` keyed by `context`.
    [InlineData("SUCCESS", PrCheckState.Success)]
    [InlineData("PENDING", PrCheckState.Pending)]
    [InlineData("EXPECTED", PrCheckState.Pending)]
    [InlineData("FAILURE", PrCheckState.Failure)]
    [InlineData("ERROR", PrCheckState.Failure)]
    public void MapsLegacyStatusContexts(string state, PrCheckState expected)
    {
        var pr = PrStatusService.ParsePrJson(
            $$"""
            {"number":1,"state":"OPEN","statusCheckRollup":[
              {"__typename":"StatusContext","context":"ci/circleci","state":"{{state}}"}
            ]}
            """);

        Assert.Equal(new PrCheck("ci/circleci", expected), pr!.Value.Checks[0]);
    }

    [Fact]
    public void ChecksRollupTakesTheWorstState()
    {
        // Any failure wins over pending/success.
        var failing = PrStatusService.ParsePrJson(
            """
            {"number":1,"state":"OPEN","statusCheckRollup":[
              {"__typename":"CheckRun","name":"a","status":"COMPLETED","conclusion":"SUCCESS"},
              {"__typename":"CheckRun","name":"b","status":"IN_PROGRESS","conclusion":""},
              {"__typename":"CheckRun","name":"c","status":"COMPLETED","conclusion":"FAILURE"}
            ]}
            """);
        Assert.Equal(PrChecksRollup.Failing, failing!.Value.ChecksRollup);

        // No failure but something still running ⇒ Pending.
        var pending = PrStatusService.ParsePrJson(
            """
            {"number":1,"state":"OPEN","statusCheckRollup":[
              {"__typename":"CheckRun","name":"a","status":"COMPLETED","conclusion":"SUCCESS"},
              {"__typename":"CheckRun","name":"b","status":"QUEUED","conclusion":""}
            ]}
            """);
        Assert.Equal(PrChecksRollup.Pending, pending!.Value.ChecksRollup);

        // All green ⇒ Passing.
        var passing = PrStatusService.ParsePrJson(
            """
            {"number":1,"state":"OPEN","statusCheckRollup":[
              {"__typename":"CheckRun","name":"a","status":"COMPLETED","conclusion":"SUCCESS"}
            ]}
            """);
        Assert.Equal(PrChecksRollup.Passing, passing!.Value.ChecksRollup);
    }

    [Fact]
    public void PullRequestInfoEqualityComparesChecksBySequence()
    {
        // The service's change gate leans on value equality; two identical fetches must compare equal even
        // though each ParseChecks builds a fresh list.
        const string json =
            """
            {"number":1,"state":"OPEN","statusCheckRollup":[
              {"__typename":"CheckRun","name":"build","status":"COMPLETED","conclusion":"SUCCESS"}
            ]}
            """;
        Assert.Equal(PrStatusService.ParsePrJson(json), PrStatusService.ParsePrJson(json));

        var a = PrStatusService.ParsePrJson(json);
        var b = PrStatusService.ParsePrJson(
            """
            {"number":1,"state":"OPEN","statusCheckRollup":[
              {"__typename":"CheckRun","name":"build","status":"COMPLETED","conclusion":"FAILURE"}
            ]}
            """);
        Assert.NotEqual(a, b);
    }

    [Fact]
    public void CapturesCheckDetailUrls()
    {
        // CheckRun links via detailsUrl; a legacy StatusContext via targetUrl. Both land on PrCheck.Url.
        var pr = PrStatusService.ParsePrJson(
            """
            {"number":1,"state":"OPEN","statusCheckRollup":[
              {"__typename":"CheckRun","name":"build","status":"COMPLETED","conclusion":"SUCCESS","detailsUrl":"https://gh/run/1"},
              {"__typename":"StatusContext","context":"ci/circleci","state":"SUCCESS","targetUrl":"https://ci/2"},
              {"__typename":"CheckRun","name":"lint","status":"COMPLETED","conclusion":"SUCCESS"}
            ]}
            """);

        Assert.Equal("https://gh/run/1", pr!.Value.Checks[0].Url);
        Assert.Equal("https://ci/2", pr.Value.Checks[1].Url);
        Assert.Equal("", pr.Value.Checks[2].Url); // no URL reported → empty
    }

    [Fact]
    public void SkipsNonObjectRollupEntriesWithoutThrowing()
    {
        var pr = PrStatusService.ParsePrJson(
            """
            {"number":1,"state":"OPEN","statusCheckRollup":[
              "junk", 42,
              {"__typename":"CheckRun","name":"build","status":"COMPLETED","conclusion":"SUCCESS"}
            ]}
            """);

        Assert.Single(pr!.Value.Checks);
        Assert.Equal("build", pr.Value.Checks[0].Name);
    }

    [Fact]
    public void ParsesLatestReviewsAuthorStateAndTime()
    {
        var pr = PrStatusService.ParsePrJson(
            """
            {"number":1,"state":"OPEN","latestReviews":[
              {"author":{"login":"octocat"},"state":"APPROVED","submittedAt":"2026-01-01T09:00:00Z"},
              {"author":{"login":"hubot"},"state":"CHANGES_REQUESTED","submittedAt":"2026-01-01T09:05:00Z"},
              {"author":{"login":"ghost"},"state":"COMMENTED","submittedAt":"2026-01-01T08:00:00Z"}
            ]}
            """);

        var reviews = pr!.Value.LatestReviews;
        Assert.Equal(3, reviews.Count);
        Assert.Equal("octocat", reviews[0].Author);
        Assert.Equal(PrReviewState.Approved, reviews[0].State);
        Assert.Equal(PrReviewState.ChangesRequested, reviews[1].State);
        Assert.Equal(PrReviewState.Commented, reviews[2].State);
    }

    [Fact]
    public void NewestReviewAndApprovalPickByTime()
    {
        var pr = PrStatusService.ParsePrJson(
            """
            {"number":1,"state":"OPEN","latestReviews":[
              {"author":{"login":"octocat"},"state":"APPROVED","submittedAt":"2026-01-01T09:00:00Z"},
              {"author":{"login":"hubot"},"state":"CHANGES_REQUESTED","submittedAt":"2026-01-01T09:05:00Z"}
            ]}
            """);

        // Newest overall is hubot (later time); newest approval is octocat (the only approver).
        Assert.Equal("hubot", pr!.Value.NewestReview!.Value.Author);
        Assert.Equal("octocat", pr.Value.NewestApproval!.Value.Author);
    }

    [Fact]
    public void NoLatestReviewsYieldsNoneAndDoesNotThrow()
    {
        var pr = PrStatusService.ParsePrJson("""{"number":1,"state":"OPEN"}""");
        Assert.Empty(pr!.Value.LatestReviews);
        Assert.Null(pr.Value.NewestReview);
        Assert.Null(pr.Value.NewestApproval);
    }

    [Fact]
    public void ReviewEqualityFoldsIntoPullRequestInfoEquality()
    {
        const string json =
            """
            {"number":1,"state":"OPEN","latestReviews":[
              {"author":{"login":"octocat"},"state":"APPROVED","submittedAt":"2026-01-01T09:00:00Z"}
            ]}
            """;
        Assert.Equal(PrStatusService.ParsePrJson(json), PrStatusService.ParsePrJson(json));

        var changed = PrStatusService.ParsePrJson(
            """
            {"number":1,"state":"OPEN","latestReviews":[
              {"author":{"login":"octocat"},"state":"CHANGES_REQUESTED","submittedAt":"2026-01-01T09:00:00Z"}
            ]}
            """);
        Assert.NotEqual(PrStatusService.ParsePrJson(json), changed);
    }
}
