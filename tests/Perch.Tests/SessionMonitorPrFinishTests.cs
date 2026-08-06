using Perch.Data;
using Xunit;

namespace Perch.Tests;

/// <summary>
/// Covers <see cref="SessionMonitor.IsPrFinishTransition"/> — the decision behind the "PR finished"
/// notification. It must fire exactly once, only on the open/draft → merged/closed edge of the <em>same</em>
/// PR we last saw open, so a PR already finalised when first observed (Perch started after the merge) or a
/// branch switch onto a different, already-landed PR never raises a spurious alert.
/// </summary>
public class SessionMonitorPrFinishTests
{
    private static PullRequestInfo Pr(int number, PrState state) => new(number, "u", "t", state);

    [Theory]
    [InlineData(PrState.Open, PrState.Merged)]
    [InlineData(PrState.Open, PrState.Closed)]
    [InlineData(PrState.Draft, PrState.Merged)]
    [InlineData(PrState.Draft, PrState.Closed)]
    public void FiresOnOpenOrDraftToFinalisedForSamePr(PrState from, PrState to)
    {
        Assert.True(SessionMonitor.IsPrFinishTransition(Pr(1, from), Pr(1, to)));
    }

    [Fact]
    public void DoesNotFireWhenNoPreviousStateWasSeen()
    {
        // First observation is already finalised (Perch started after the merge) — record it, don't alert.
        Assert.False(SessionMonitor.IsPrFinishTransition(null, Pr(1, PrState.Merged)));
    }

    [Theory]
    [InlineData(PrState.Open)]
    [InlineData(PrState.Draft)]
    public void DoesNotFireWhileStillOpen(PrState to)
    {
        Assert.False(SessionMonitor.IsPrFinishTransition(Pr(1, PrState.Open), Pr(1, to)));
    }

    [Fact]
    public void DoesNotReFireWhenAlreadyFinalised()
    {
        // Once merged, subsequent scans still read merged — the previous state is no longer open, so no repeat.
        Assert.False(SessionMonitor.IsPrFinishTransition(Pr(1, PrState.Merged), Pr(1, PrState.Merged)));
    }

    [Fact]
    public void DoesNotFireOnBranchSwitchToADifferentAlreadyFinalisedPr()
    {
        // cwd switched from PR #1 (open) to PR #2 (already merged): different number, so it's not our edge.
        Assert.False(SessionMonitor.IsPrFinishTransition(Pr(1, PrState.Open), Pr(2, PrState.Merged)));
    }

    // ── Review-added detection (NewestNewReview) ───────────────────────────────
    private static PrReview R(string author, PrReviewState state, int minute) =>
        new(author, state, new DateTime(2026, 1, 1, 9, minute, 0, DateTimeKind.Utc));

    [Fact]
    public void NewestNewReview_ReturnsTheFreshReview()
    {
        var prev = new[] { R("octocat", PrReviewState.Commented, 0) };
        var cur = new[] { R("octocat", PrReviewState.Commented, 0), R("hubot", PrReviewState.Approved, 5) };
        var added = SessionMonitor.NewestNewReview(prev, cur);
        Assert.Equal("hubot", added!.Value.Author);
        Assert.Equal(PrReviewState.Approved, added.Value.State);
    }

    [Fact]
    public void NewestNewReview_PicksTheNewestWhenSeveralAreNew()
    {
        var cur = new[] { R("a", PrReviewState.Commented, 1), R("b", PrReviewState.ChangesRequested, 9) };
        var added = SessionMonitor.NewestNewReview(Array.Empty<PrReview>(), cur);
        Assert.Equal("b", added!.Value.Author);   // minute 9 beats minute 1
    }

    [Fact]
    public void NewestNewReview_NullWhenNothingChanged()
    {
        var same = new[] { R("octocat", PrReviewState.Approved, 0) };
        Assert.Null(SessionMonitor.NewestNewReview(same, same));
    }

    [Fact]
    public void NewestNewReview_ResubmissionBySameAuthorCountsAsNew()
    {
        // A reviewer re-reviews: same author, newer submit time → a fresh review to alert on.
        var prev = new[] { R("octocat", PrReviewState.ChangesRequested, 0) };
        var cur = new[] { R("octocat", PrReviewState.Approved, 5) };
        var added = SessionMonitor.NewestNewReview(prev, cur);
        Assert.Equal(PrReviewState.Approved, added!.Value.State);
    }
}
