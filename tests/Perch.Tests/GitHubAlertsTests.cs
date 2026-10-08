using Perch.Data;
using Xunit;

namespace Perch.Tests;

/// <summary>
/// Guards the GitHub alerts data layer without launching gh: parsing <c>gh api user</c> and the GraphQL search
/// response (<see cref="GitHubAlertsClient"/>), the "whose move is it" rules in <see cref="GitHubAlertsClassifier"/>,
/// the window's repo grouping and the strip's summary, and the seen-marker store.
/// </summary>
public class GitHubAlertsTests
{
    private const string Me = "me";
    private static readonly DateTime T0 = new(2026, 10, 1, 12, 0, 0, DateTimeKind.Utc);

    private static GhPullRequest Pr(GhPrRelation rel, string author = Me, params GhEvent[] events) => new()
    {
        Repo = "acme/web", Number = 1, Title = "A change", Url = "https://github.com/acme/web/pull/1",
        Author = author, Relation = rel, UpdatedUtc = T0, LastCommitUtc = T0,
        Mergeable = GhMergeable.Mergeable, MergeState = "BLOCKED", Checks = GhChecks.Passing,
        ReviewDecision = "REVIEW_REQUIRED", Events = events,
    };

    private static GhEvent Ev(string who, GhEventKind kind, int minutesAfterT0, bool bot = false) =>
        new(who, bot, kind, T0.AddMinutes(minutesAfterT0));

    private static List<GhAlertKind> Kinds(GhPullRequest pr, DateTime? seen = null) =>
        GitHubAlertsClassifier.Classify(pr, Me, seen).Select(r => r.Kind).ToList();

    // ── Classifier: your own PRs ──

    [Fact]
    public void ChangesRequestedAfterYourLastPushNeedsYou()
    {
        var pr = Pr(GhPrRelation.Author, Me, Ev("alice", GhEventKind.ChangesRequested, 10));
        var reasons = GitHubAlertsClassifier.Classify(pr, Me, null);
        Assert.Equal(GhAlertKind.ChangesRequested, Assert.Single(reasons).Kind);
        Assert.Equal("Changes requested by alice", reasons[0].Text);
    }

    [Fact]
    public void ChangesRequestedBeforeYourLastPushIsAnswered()
    {
        // You pushed after the review — the ball is back in the reviewer's court.
        var pr = Pr(GhPrRelation.Author, Me, Ev("alice", GhEventKind.ChangesRequested, -10))
            with { ReviewDecision = "CHANGES_REQUESTED" };
        Assert.Empty(Kinds(pr));
    }

    [Fact]
    public void YourOwnReplyClearsEarlierComments()
    {
        var pr = Pr(GhPrRelation.Author, Me,
            Ev("alice", GhEventKind.Comment, 5), Ev(Me, GhEventKind.Comment, 6));
        Assert.Empty(Kinds(pr));
    }

    [Fact]
    public void CommentsSinceYouLastLookedNeedYouUntilSeen()
    {
        var pr = Pr(GhPrRelation.Author, Me,
            Ev("alice", GhEventKind.Comment, 5), Ev("bob", GhEventKind.Commented, 7));
        var reasons = GitHubAlertsClassifier.Classify(pr, Me, null);
        Assert.Equal(GhAlertKind.NewActivity, Assert.Single(reasons).Kind);
        Assert.Equal("2 new comments · bob, alice", reasons[0].Text);

        Assert.Empty(Kinds(pr, seen: T0.AddMinutes(8)));                        // opened after both
        Assert.Single(Kinds(pr, seen: T0.AddMinutes(6)));                       // bob's came after
    }

    [Fact]
    public void BotActivityIsIgnored()
    {
        var pr = Pr(GhPrRelation.Author, Me,
            Ev("ci", GhEventKind.Comment, 5, bot: true), Ev("linter", GhEventKind.ChangesRequested, 6, bot: true));
        Assert.Empty(Kinds(pr));
    }

    [Fact]
    public void ApprovalIsAnnouncedAndReadyToMergeWhenClean()
    {
        var pr = Pr(GhPrRelation.Author, Me, Ev("carol", GhEventKind.Approved, 5))
            with { ReviewDecision = "APPROVED", HasApproval = true, MergeState = "CLEAN" };
        var reasons = GitHubAlertsClassifier.Classify(pr, Me, null);
        Assert.Equal([GhAlertKind.NewActivity, GhAlertKind.ReadyToMerge], reasons.Select(r => r.Kind));
        Assert.Equal("Approved by carol", reasons[0].Text);
        // Once seen, it stays as "Ready to merge" — a state that needs an action.
        Assert.Equal([GhAlertKind.ReadyToMerge], Kinds(pr, seen: T0.AddHours(1)));
    }

    [Theory]
    [InlineData(GhChecks.Pending, "CLEAN", false, false)]
    [InlineData(GhChecks.Failing, "CLEAN", false, false)]
    [InlineData(GhChecks.Passing, "BLOCKED", false, false)]
    [InlineData(GhChecks.Passing, "BEHIND", false, false)]
    [InlineData(GhChecks.Passing, "CLEAN", true, false)]
    [InlineData(GhChecks.None, "CLEAN", false, true)]
    [InlineData(GhChecks.Passing, "UNSTABLE", false, true)]
    public void ReadyToMergeNeedsApprovalGreenAndUnblocked(GhChecks checks, string mergeState, bool draft, bool ready)
    {
        var pr = Pr(GhPrRelation.Author) with
        {
            ReviewDecision = "APPROVED", Checks = checks, MergeState = mergeState, IsDraft = draft,
        };
        Assert.Equal(ready, GitHubAlertsClassifier.IsReadyToMerge(pr));
    }

    [Fact]
    public void NoRequiredReviewStillNeedsAnApprovalToBeReady()
    {
        var pr = Pr(GhPrRelation.Author) with { ReviewDecision = "", MergeState = "CLEAN" };
        Assert.False(GitHubAlertsClassifier.IsReadyToMerge(pr));
        Assert.True(GitHubAlertsClassifier.IsReadyToMerge(pr with { HasApproval = true }));
    }

    [Fact]
    public void FailingChecksAndConflictsStayUntilFixed()
    {
        var pr = Pr(GhPrRelation.Author) with { Checks = GhChecks.Failing, Mergeable = GhMergeable.Conflicting };
        Assert.Equal([GhAlertKind.ChecksFailing, GhAlertKind.Conflicts], Kinds(pr, seen: T0.AddDays(1)));
    }

    // ── Classifier: other people's PRs ──

    [Fact]
    public void ReviewRequestedNeedsYouEvenOnceSeen()
    {
        var pr = Pr(GhPrRelation.ReviewRequested, "bob");
        var reasons = GitHubAlertsClassifier.Classify(pr, Me, T0.AddDays(1));
        Assert.Equal(GhAlertKind.ReviewRequested, Assert.Single(reasons).Kind);
        Assert.Equal("Review requested · bob", reasons[0].Text);
    }

    [Fact]
    public void SomeoneElsesStateDoesNotRaiseYourAlerts()
    {
        // Their failing checks / readiness are theirs to act on.
        var pr = Pr(GhPrRelation.ReviewRequested, "bob") with { Checks = GhChecks.Failing, Mergeable = GhMergeable.Conflicting };
        Assert.Equal([GhAlertKind.ReviewRequested], Kinds(pr));
    }

    [Fact]
    public void AssignedShowsUntilOpenedThenOnlyOnNewActivity()
    {
        var pr = Pr(GhPrRelation.Assignee, "frank");
        Assert.Equal([GhAlertKind.Assigned], Kinds(pr));
        Assert.Empty(Kinds(pr, seen: T0));

        var later = pr with { Events = [Ev("frank", GhEventKind.Comment, 30)] };
        Assert.Equal([GhAlertKind.NewActivity], Kinds(later, seen: T0));
    }

    [Fact]
    public void YourPrAssignedToYouIsTreatedAsYours()
    {
        var pr = Pr(GhPrRelation.Author | GhPrRelation.Assignee);
        Assert.Empty(Kinds(pr));   // no "Assigned to you" for your own PR
    }

    // ── Snapshot: grouping + summary ──

    [Fact]
    public void ByRepoPutsReposNeedingYouFirstAndFiltersTheRest()
    {
        var quiet = Pr(GhPrRelation.Author) with { Repo = "aaa/quiet", Url = "u1" };
        var review = Pr(GhPrRelation.ReviewRequested, "bob") with { Repo = "zzz/busy", Url = "u2" };
        var snap = GitHubAlertsClassifier.Build(
            new GitHubFetchResult(Me, [quiet, review], null, T0), new Dictionary<string, DateTime>());

        Assert.Equal(1, snap.NeedsYouCount);
        Assert.Equal(["zzz/busy", "aaa/quiet"], snap.Grouped(GhView.All, new()).Select(g => g.Title));
        Assert.Equal(["zzz/busy"], snap.Grouped(GhView.NeedsYou, new()).Select(g => g.Title));
    }

    [Fact]
    public void KindCountsCountEveryReasonInPriorityOrder()
    {
        var a = Pr(GhPrRelation.ReviewRequested, "bob") with { Url = "a" };
        var b = Pr(GhPrRelation.ReviewRequested, "bob") with { Url = "b" };
        // Two reasons on one PR: it counts under both.
        var c = Pr(GhPrRelation.Author) with { Url = "c", Checks = GhChecks.Failing, Mergeable = GhMergeable.Conflicting };
        var quiet = Pr(GhPrRelation.Author) with { Url = "d" };
        var snap = GitHubAlertsClassifier.Build(
            new GitHubFetchResult(Me, [c, a, quiet, b], null, T0), new Dictionary<string, DateTime>());

        Assert.Equal(3, snap.NeedsYouCount);
        Assert.Equal(
            [(GhAlertKind.ReviewRequested, 2), (GhAlertKind.ChecksFailing, 1), (GhAlertKind.Conflicts, 1)],
            snap.KindCounts());
        Assert.Equal("2 reviews requested", GitHubAlertsSnapshot.Describe(GhAlertKind.ReviewRequested, 2));
        Assert.Equal("1 ready to merge", GitHubAlertsSnapshot.Describe(GhAlertKind.ReadyToMerge, 1));
    }

    // ── Search ──

    [Theory]
    [InlineData(null, true)]
    [InlineData("   ", true)]
    [InlineData("checkout", true)]          // title
    [InlineData("CHECKOUT", true)]          // case-insensitive
    [InlineData("acme/web", true)]          // repo
    [InlineData("bob", true)]               // author
    [InlineData("#412", true)]              // number with #
    [InlineData("41", true)]                // number substring
    [InlineData("requested", true)]         // reason text
    [InlineData("web review", true)]        // every word matches somewhere
    [InlineData("web api", false)]          // "api" matches nothing
    [InlineData("#", false)]                // a lone # is a literal, matching nothing here
    public void SearchMatchesEveryWordAcrossTheRowsFields(string? query, bool expected)
    {
        var pr = Pr(GhPrRelation.ReviewRequested, "bob") with
        {
            Number = 412, Title = "Move the checkout form", Url = "https://github.com/acme/web/pull/412",
        };
        var item = new GhPrItem(pr, GitHubAlertsClassifier.Classify(pr, Me, null));
        Assert.Equal(expected, GitHubAlertsSnapshot.Matches(item, query));
    }

    [Fact]
    public void FilterAndByRepoApplyTheSearchWithinTheTab()
    {
        var review = Pr(GhPrRelation.ReviewRequested, "bob") with { Repo = "acme/web", Url = "a", Title = "Charts" };
        var quiet = Pr(GhPrRelation.Author) with { Repo = "acme/api", Url = "b", Title = "Charts too" };
        var snap = GitHubAlertsClassifier.Build(new GitHubFetchResult(Me, [review, quiet], null, T0), new Dictionary<string, DateTime>());

        Assert.Equal(2, snap.Filter(GhView.All, "charts").Count());
        Assert.Single(snap.Filter(GhView.NeedsYou, "charts"));
        Assert.Equal(["acme/api"], snap.Grouped(GhView.All, new(), "api").Select(g => g.Title));
        Assert.Empty(snap.Grouped(GhView.NeedsYou, new(), "api"));
    }

    // ── Dashboard: grouping, sorting, age window ──

    // Three PRs across two repos: a review request (bob, acme/web, updated an hour ago), your PR with failing checks
    // (acme/api, two days ago), and your quiet PR (acme/web, ten days ago).
    private static GitHubAlertsSnapshot Dashboard(IReadOnlyDictionary<string, string>? dismissed = null, DateTime? since = null)
    {
        var review = Pr(GhPrRelation.ReviewRequested, "bob") with { Repo = "acme/web", Number = 10, Url = "review", UpdatedUtc = T0.AddHours(-1) };
        var failing = Pr(GhPrRelation.Author) with { Repo = "acme/api", Number = 20, Url = "failing", UpdatedUtc = T0.AddDays(-2), Checks = GhChecks.Failing };
        var quiet = Pr(GhPrRelation.Author) with { Repo = "acme/web", Number = 30, Url = "quiet", UpdatedUtc = T0.AddDays(-10) };
        return GitHubAlertsClassifier.Build(new GitHubFetchResult(Me, [quiet, failing, review], null, T0),
            new Dictionary<string, DateTime>(), dismissed, since);
    }

    private static List<string> Urls(IEnumerable<GhPrItem> items) => items.Select(i => i.Pr.Url).ToList();

    [Fact]
    public void GroupByReasonOrdersByHeadlineThenNothingToDo()
    {
        var groups = Dashboard().Grouped(GhView.All, new(GhGroupBy.Reason));
        Assert.Equal(["Review requested", "Checks failing", "Nothing to do"], groups.Select(g => g.Title));
        Assert.Equal(["quiet"], Urls(groups[2].Items));
    }

    [Fact]
    public void GroupByRolePutsYoursFirst()
    {
        var groups = Dashboard().Grouped(GhView.All, new(GhGroupBy.Role));
        Assert.Equal(["Yours", "Review requested"], groups.Select(g => g.Title));
        Assert.Equal(["failing", "quiet"], Urls(groups[0].Items));      // urgency: needing you first
    }

    [Theory]
    [InlineData(GhSortBy.Urgency, new[] { "review", "failing", "quiet" })]   // headline priority: review before checks
    [InlineData(GhSortBy.Updated, new[] { "review", "failing", "quiet" })]
    [InlineData(GhSortBy.Oldest, new[] { "quiet", "failing", "review" })]
    public void GroupByNoneIsOneUntitledListInTheChosenOrder(GhSortBy sort, string[] expected)
    {
        var groups = Dashboard().Grouped(GhView.All, new(GhGroupBy.None, sort));
        var (title, items) = Assert.Single(groups);
        Assert.Equal("", title);
        Assert.Equal(expected, Urls(items));
    }

    [Fact]
    public void UrgencyRanksByHeadlineReasonNotJustRecency()
    {
        // The failing PR is the most recently updated, but a review request outranks failing checks.
        var review = Pr(GhPrRelation.ReviewRequested, "bob") with { Url = "review", UpdatedUtc = T0.AddDays(-3) };
        var failing = Pr(GhPrRelation.Author) with { Url = "failing", UpdatedUtc = T0, Checks = GhChecks.Failing };
        var snap = GitHubAlertsClassifier.Build(new GitHubFetchResult(Me, [failing, review], null, T0), new Dictionary<string, DateTime>());
        Assert.Equal(["review", "failing"], Urls(Assert.Single(snap.Grouped(GhView.All, new(GhGroupBy.None))).Items));
    }

    [Fact]
    public void AgeWindowHidesOldPrsFromEveryTabAndTheCounts()
    {
        var snap = Dashboard(since: T0.AddDays(-7));
        Assert.Equal(1, snap.TooOldCount);
        Assert.DoesNotContain("quiet", Urls(snap.Filter(GhView.All, null)));
        Assert.Equal(2, snap.NeedsYouCount);

        var tighter = Dashboard(since: T0.AddDays(-1));      // the failing PR (2 days old) goes too
        Assert.Equal(1, tighter.NeedsYouCount);
        Assert.Equal([(GhAlertKind.ReviewRequested, 1)], tighter.KindCounts());
    }

    [Fact]
    public void AnUnreadableUpdateTimeIsNeverTooOld()
    {
        var pr = Pr(GhPrRelation.Author) with { UpdatedUtc = DateTime.MinValue };
        var snap = GitHubAlertsClassifier.Build(new GitHubFetchResult(Me, [pr], null, T0),
            new Dictionary<string, DateTime>(), updatedSinceUtc: T0);
        Assert.False(Assert.Single(snap.Items).TooOld);
    }

    [Theory]
    [InlineData((GhGroupBy)99, (GhSortBy)99, -5, GhGroupBy.Repo, GhSortBy.Urgency, 0)]
    [InlineData(GhGroupBy.Role, GhSortBy.Oldest, 30, GhGroupBy.Role, GhSortBy.Oldest, 30)]
    public void OptionsNormalizeUnknownValues(GhGroupBy g, GhSortBy s, int age, GhGroupBy eg, GhSortBy es, int eage) =>
        Assert.Equal(new GhListOptions(eg, es, eage), new GhListOptions(g, s, age).Normalize());

    [Fact]
    public void UpdatedSinceIsNullForAnyAge()
    {
        Assert.Null(new GhListOptions().UpdatedSince(T0));
        Assert.Equal(T0.AddDays(-7), new GhListOptions(MaxAgeDays: 7).UpdatedSince(T0));
    }

    // ── Dismiss ──

    [Fact]
    public void ADismissedPrLeavesTheTabsAndTheStripUntilItsFingerprintChanges()
    {
        var failing = Dashboard().Items.Single(i => i.Pr.Url == "failing").Pr;
        var dismissed = new Dictionary<string, string> { ["failing"] = GitHubAlertsClassifier.Fingerprint(failing, Me) };
        var snap = Dashboard(dismissed);

        Assert.Equal(["review"], Urls(snap.Filter(GhView.NeedsYou, null)));
        Assert.DoesNotContain("failing", Urls(snap.Filter(GhView.All, null)));
        Assert.Equal(["failing"], Urls(snap.Filter(GhView.Dismissed, null)));
        Assert.Equal(1, snap.NeedsYouCount);
        Assert.Equal([(GhAlertKind.ReviewRequested, 1)], snap.KindCounts());

        // A stale fingerprint no longer hides it.
        var stale = Dashboard(new Dictionary<string, string> { ["failing"] = "something else" });
        Assert.Empty(stale.Filter(GhView.Dismissed, null));
        Assert.Equal(2, stale.NeedsYouCount);
    }

    [Fact]
    public void IncludeDismissedLetsDismissedPrsBackIntoTheTabsForASearch()
    {
        var failing = Dashboard().Items.Single(i => i.Pr.Url == "failing").Pr;
        var snap = Dashboard(new Dictionary<string, string> { ["failing"] = GitHubAlertsClassifier.Fingerprint(failing, Me) });

        Assert.Equal(["failing", "review"], Urls(snap.Filter(GhView.NeedsYou, null, includeDismissed: true)));   // fetch order
        Assert.Contains("failing", Urls(snap.Filter(GhView.All, null, includeDismissed: true)));
        Assert.Equal(["failing"], Urls(snap.Filter(GhView.Dismissed, null, includeDismissed: true)));   // unchanged
        Assert.Equal(["failing"], Urls(Assert.Single(snap.Grouped(GhView.All, new(), "api", includeDismissed: true)).Items));
        // The strip still ignores it: including dismissed is a window filter, not a restore.
        Assert.Equal(1, snap.NeedsYouCount);

        // The hint: only for a search, only where the view hides them.
        Assert.Equal(1, snap.HiddenDismissedMatches(GhView.All, "api", includeDismissed: false));
        Assert.Equal(1, snap.HiddenDismissedMatches(GhView.NeedsYou, "api", includeDismissed: false));
        Assert.Equal(0, snap.HiddenDismissedMatches(GhView.All, null, includeDismissed: false));
        Assert.Equal(0, snap.HiddenDismissedMatches(GhView.All, "api", includeDismissed: true));
        Assert.Equal(0, snap.HiddenDismissedMatches(GhView.Dismissed, "api", includeDismissed: false));
        Assert.Equal(0, snap.HiddenDismissedMatches(GhView.All, "web", includeDismissed: false));   // no dismissed match
    }

    public static TheoryData<string, Func<GhPullRequest, GhPullRequest>> FingerprintChanges => new()
    {
        { "new comment by someone else", p => p with { Events = [.. p.Events, Ev("alice", GhEventKind.Comment, 60)] } },
        { "a review", p => p with { Events = [.. p.Events, Ev("alice", GhEventKind.Approved, 60)] } },
        { "review decision", p => p with { ReviewDecision = "APPROVED" } },
        { "checks start failing", p => p with { Checks = GhChecks.Failing } },
        { "conflicts appear", p => p with { Mergeable = GhMergeable.Conflicting } },
        { "draft marked ready", p => p with { IsDraft = !p.IsDraft } },
        { "review re-requested", p => p with { Relation = p.Relation | GhPrRelation.ReviewRequested } },
    };

    [Theory]
    [MemberData(nameof(FingerprintChanges))]
    public void FingerprintChangesWhenTheStatusDoes(string why, Func<GhPullRequest, GhPullRequest> change)
    {
        var pr = Pr(GhPrRelation.Author, Me, Ev("alice", GhEventKind.Comment, 5));
        Assert.True(GitHubAlertsClassifier.Fingerprint(pr, Me) != GitHubAlertsClassifier.Fingerprint(change(pr), Me), why);
    }

    public static TheoryData<string, Func<GhPullRequest, GhPullRequest>> FingerprintNoise => new()
    {
        { "your own comment", p => p with { Events = [.. p.Events, Ev(Me, GhEventKind.Comment, 60)] } },
        { "a bot comment", p => p with { Events = [.. p.Events, Ev("ci", GhEventKind.Comment, 60, bot: true)] } },
        { "a dismissed review", p => p with { Events = [.. p.Events, Ev("alice", GhEventKind.Dismissed, 60)] } },
        { "base moved (BEHIND)", p => p with { MergeState = "BEHIND" } },
        { "mergeable recomputing", p => p with { Mergeable = GhMergeable.Unknown } },
        { "checks pending → passing", p => p with { Checks = GhChecks.Pending } },
        { "your own push", p => p with { LastCommitUtc = T0.AddHours(3) } },
        { "updated time alone", p => p with { UpdatedUtc = T0.AddHours(3) } },
    };

    [Theory]
    [MemberData(nameof(FingerprintNoise))]
    public void FingerprintIgnoresNoiseAndYourOwnMoves(string why, Func<GhPullRequest, GhPullRequest> change)
    {
        var pr = Pr(GhPrRelation.Author, Me, Ev("alice", GhEventKind.Comment, 5));
        Assert.True(GitHubAlertsClassifier.Fingerprint(pr, Me) == GitHubAlertsClassifier.Fingerprint(change(pr), Me), why);
    }

    [Fact]
    public void APushToSomeoneElsesPrChangesItsFingerprint()
    {
        var pr = Pr(GhPrRelation.ReviewRequested, "bob");
        Assert.NotEqual(GitHubAlertsClassifier.Fingerprint(pr, Me),
            GitHubAlertsClassifier.Fingerprint(pr with { LastCommitUtc = T0.AddHours(3) }, Me));
    }

    [Fact]
    public void DismissStoreRoundTripsAndReconciles()
    {
        var dir = Path.Combine(Path.GetTempPath(), "perch-tests", Guid.NewGuid().ToString("N"));
        var path = Path.Combine(dir, "github-alerts-dismissed.json");
        try
        {
            var store = GitHubAlertsDismissStore.LoadFrom(path);
            store.Dismiss("https://github.com/a/b/pull/1", "fp1", T0);
            store.Dismiss("https://github.com/a/b/pull/2", "fp2", T0);
            store.Dismiss("https://github.com/a/b/pull/3", "fp3", T0);
            store.Save();

            var reloaded = GitHubAlertsDismissStore.LoadFrom(path);
            Assert.Equal(new GhDismissal("fp1", T0), reloaded.All["https://github.com/a/b/pull/1"]);
            Assert.Equal(DateTimeKind.Utc, reloaded.All["https://github.com/a/b/pull/1"].AtUtc.Kind);
            Assert.Equal("fp2", reloaded.Fingerprints["HTTPS://github.com/a/b/pull/2"]);   // URL case-insensitive

            // #1 unchanged → kept; #2 moved on → dropped; #3 closed (absent) → dropped.
            var now = new Dictionary<string, string>
            {
                ["https://github.com/a/b/pull/1"] = "fp1",
                ["https://github.com/a/b/pull/2"] = "fp2-changed",
            };
            Assert.True(reloaded.Reconcile(now));
            Assert.False(reloaded.Reconcile(now));
            Assert.Equal(["https://github.com/a/b/pull/1"], reloaded.All.Keys);

            Assert.True(reloaded.Restore("https://github.com/a/b/pull/1"));
            Assert.False(reloaded.Restore("https://github.com/a/b/pull/1"));
            Assert.Empty(reloaded.All);
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }

    [Fact]
    public void DismissStoreToleratesAGarbledFile()
    {
        var dir = Path.Combine(Path.GetTempPath(), "perch-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "github-alerts-dismissed.json");
        try
        {
            File.WriteAllText(path, """{ "u": { "Fingerprint": "" , "AtUtc": "2026-10-01T00:00:00Z" }, "v": null }""");
            Assert.Empty(GitHubAlertsDismissStore.LoadFrom(path).All);     // empty fingerprint and null are dropped
            File.WriteAllText(path, "{ not json");
            Assert.Empty(GitHubAlertsDismissStore.LoadFrom(path).All);
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }

    // ── Parsing ──

    [Theory]
    [InlineData("""{"login":"octo-cat","id":1}""", "octo-cat")]
    [InlineData("""{"login":"a b"}""", null)]
    [InlineData("""{"login":"x review-requested:other"}""", null)]
    [InlineData("""{"id":1}""", null)]
    [InlineData("not json", null)]
    public void ParseLoginOnlyAcceptsLoginShapedValues(string json, string? expected) =>
        Assert.Equal(expected, GitHubAlertsClient.ParseLogin(json));

    private const string SearchJson = """
        {
          "data": {
            "authored": { "nodes": [
              {
                "number": 7, "title": "Mine", "url": "https://github.com/acme/web/pull/7", "isDraft": false,
                "updatedAt": "2026-10-01T12:00:00Z",
                "repository": { "nameWithOwner": "acme/web" },
                "author": { "login": "me" },
                "reviewDecision": "APPROVED", "mergeable": "CONFLICTING", "mergeStateStatus": "dirty",
                "commits": { "nodes": [ { "commit": { "committedDate": "2026-10-01T10:00:00Z",
                                                      "statusCheckRollup": { "state": "FAILURE" } } } ] },
                "latestReviews": { "nodes": [ { "state": "APPROVED" } ] },
                "reviews": { "nodes": [
                  { "author": { "__typename": "User", "login": "alice" }, "state": "APPROVED", "submittedAt": "2026-10-01T11:00:00Z" },
                  { "author": { "__typename": "User", "login": "me" }, "state": "PENDING", "submittedAt": null }
                ] },
                "comments": { "nodes": [
                  { "author": { "__typename": "Bot", "login": "ci" }, "createdAt": "2026-10-01T10:30:00Z" },
                  { "author": null, "createdAt": "2026-10-01T10:45:00Z" }
                ] }
              }
            ] },
            "review": { "nodes": [
              { "number": 9, "title": "Theirs", "url": "https://github.com/acme/api/pull/9",
                "repository": { "nameWithOwner": "acme/api" }, "author": { "login": "bob" },
                "commits": { "nodes": [ { "commit": { "committedDate": "2026-10-01T09:00:00Z", "statusCheckRollup": null } } ] } }
            ] },
            "assigned": { "nodes": [
              { "number": 7, "title": "Mine", "url": "https://github.com/acme/web/pull/7",
                "repository": { "nameWithOwner": "acme/web" }, "author": { "login": "me" } },
              {}
            ] }
          },
          "errors": [ { "message": "partial" } ]
        }
        """;

    [Fact]
    public void ParseSearchMergesTheThreeSearchesByUrl()
    {
        var prs = GitHubAlertsClient.ParseSearch(SearchJson)!;
        Assert.Equal(2, prs.Count);

        var mine = prs[0];
        Assert.Equal(GhPrRelation.Author | GhPrRelation.Assignee, mine.Relation);
        Assert.Equal("acme/web", mine.Repo);
        Assert.Equal(GhChecks.Failing, mine.Checks);
        Assert.Equal(GhMergeable.Conflicting, mine.Mergeable);
        Assert.Equal("DIRTY", mine.MergeState);
        Assert.Equal("APPROVED", mine.ReviewDecision);
        Assert.True(mine.HasApproval);
        Assert.Equal(new DateTime(2026, 10, 1, 10, 0, 0, DateTimeKind.Utc), mine.LastCommitUtc);

        // The pending draft review is dropped; events come oldest first; the bot and the ghost author are kept
        // (the classifier ignores bots; a ghost has no login).
        Assert.Equal(3, mine.Events.Count);
        Assert.True(mine.Events[0] is { Actor: "ci", IsBot: true, Kind: GhEventKind.Comment });
        Assert.True(mine.Events[1] is { Actor: "", IsBot: false });
        Assert.True(mine.Events[2] is { Actor: "alice", Kind: GhEventKind.Approved });

        var theirs = prs[1];
        Assert.Equal(GhPrRelation.ReviewRequested, theirs.Relation);
        Assert.Equal(GhChecks.None, theirs.Checks);
        Assert.Equal(GhMergeable.Unknown, theirs.Mergeable);
    }

    [Theory]
    [InlineData("""{"errors":[{"message":"bad"}]}""")]
    [InlineData("""{"data":null}""")]
    [InlineData("[]")]
    [InlineData("<html>")]
    public void ParseSearchRejectsResponsesWithoutData(string json) =>
        Assert.Null(GitHubAlertsClient.ParseSearch(json));

    // ── Seen store ──

    [Fact]
    public void SeenStoreRoundTripsAndPrunes()
    {
        var dir = Path.Combine(Path.GetTempPath(), "perch-tests", Guid.NewGuid().ToString("N"));
        var path = Path.Combine(dir, "github-alerts-seen.json");
        try
        {
            var store = GitHubAlertsSeenStore.LoadFrom(path);
            store.MarkSeen("https://github.com/a/b/pull/1", T0);
            store.MarkSeen("https://github.com/a/b/pull/2", T0.AddHours(1));
            store.Save();

            var reloaded = GitHubAlertsSeenStore.LoadFrom(path);
            Assert.Equal(T0, reloaded.All["https://github.com/a/b/pull/1"]);
            Assert.Equal(DateTimeKind.Utc, reloaded.All["https://github.com/a/b/pull/1"].Kind);

            Assert.True(reloaded.Prune(["https://github.com/a/b/pull/2"]));
            Assert.False(reloaded.Prune(["https://github.com/a/b/pull/2"]));
            Assert.Equal(["https://github.com/a/b/pull/2"], reloaded.All.Keys);
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }

    [Fact]
    public void SeenStoreToleratesAGarbledFile()
    {
        var dir = Path.Combine(Path.GetTempPath(), "perch-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "github-alerts-seen.json");
        try
        {
            File.WriteAllText(path, "{ not json");
            Assert.Empty(GitHubAlertsSeenStore.LoadFrom(path).All);
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }
}
