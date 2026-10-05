namespace Perch.Data;

/// <summary>How the signed-in user relates to a pull request. Flags, because one PR can be several at once
/// (your own PR that you also assigned to yourself, say). Set from which of the three searches returned it.</summary>
[Flags]
public enum GhPrRelation
{
    None            = 0,
    Author          = 1,
    ReviewRequested = 2,
    Assignee        = 4,
}

/// <summary>The head commit's combined CI status (GitHub's <c>statusCheckRollup.state</c>), folded to the
/// buckets the alerts care about. <see cref="None"/> = the commit reports no checks at all.</summary>
public enum GhChecks { None, Pending, Passing, Failing }

/// <summary>GitHub's <c>mergeable</c>: whether the PR merges cleanly into its base. <see cref="Unknown"/>
/// while GitHub is still computing it (it does so lazily, so a freshly-pushed PR often reads Unknown).</summary>
public enum GhMergeable { Unknown, Mergeable, Conflicting }

/// <summary>What one piece of PR activity was: a plain conversation comment, or a submitted review in one
/// of its states (a reply to a review thread arrives as a <see cref="Commented"/> review).</summary>
public enum GhEventKind { Comment, Commented, Approved, ChangesRequested, Dismissed }

/// <summary>One piece of activity on a PR — who did it, whether they're a bot, what it was and when.</summary>
public readonly record struct GhEvent(string Actor, bool IsBot, GhEventKind Kind, DateTime AtUtc);

/// <summary>
/// An open pull request as read from GitHub's GraphQL search: the identity and state the alerts window shows,
/// plus the raw ingredients (<see cref="Events"/>, <see cref="LastCommitUtc"/>, review decision, mergeability,
/// checks) the <see cref="GitHubAlertsClassifier"/> turns into "needs you" reasons. UI-free.
/// </summary>
public sealed record GhPullRequest
{
    public required string Repo { get; init; }      // owner/name
    public required int Number { get; init; }
    public required string Title { get; init; }
    public required string Url { get; init; }
    public string Author { get; init; } = "";
    public bool IsDraft { get; init; }
    public DateTime UpdatedUtc { get; init; }
    /// <summary>The head commit's committed date — the best GraphQL offers for "when the author last
    /// pushed". Null when unreadable.</summary>
    public DateTime? LastCommitUtc { get; init; }
    /// <summary>GitHub's <c>reviewDecision</c>: <c>APPROVED</c>, <c>CHANGES_REQUESTED</c>,
    /// <c>REVIEW_REQUIRED</c>, or empty when the repo requires no review.</summary>
    public string ReviewDecision { get; init; } = "";
    public GhMergeable Mergeable { get; init; }
    /// <summary>GitHub's <c>mergeStateStatus</c> (<c>CLEAN</c>, <c>BLOCKED</c>, <c>BEHIND</c>, <c>DIRTY</c>, …),
    /// upper-case; empty when absent.</summary>
    public string MergeState { get; init; } = "";
    public GhChecks Checks { get; init; }
    /// <summary>True when any reviewer's latest review is an approval — stands in for the review decision on
    /// a repo that requires no reviews (where <see cref="ReviewDecision"/> is empty).</summary>
    public bool HasApproval { get; init; }
    /// <summary>Recent comments and reviews, oldest first.</summary>
    public IReadOnlyList<GhEvent> Events { get; init; } = [];
    public GhPrRelation Relation { get; init; }
}

/// <summary>Why a PR needs you. Order is priority: the first reason on a PR is its headline.</summary>
public enum GhAlertKind
{
    /// <summary>Someone asked you to review it.</summary>
    ReviewRequested,
    /// <summary>Your PR: a reviewer requested changes since you last acted.</summary>
    ChangesRequested,
    /// <summary>New comments / reviews / an approval since you last acted or looked.</summary>
    NewActivity,
    /// <summary>Your PR: the head commit's checks are failing.</summary>
    ChecksFailing,
    /// <summary>Your PR: it no longer merges cleanly.</summary>
    Conflicts,
    /// <summary>Your PR: approved, green and mergeable.</summary>
    ReadyToMerge,
    /// <summary>Someone else's PR assigned to you that you haven't opened from Perch yet.</summary>
    Assigned,
}

/// <summary>One reason a PR needs you, with its one-line human text ("Changes requested by alice").</summary>
public readonly record struct GhAlertReason(GhAlertKind Kind, string Text);

/// <summary>A PR plus the reasons (possibly none) it needs you.</summary>
public sealed record GhPrItem(GhPullRequest Pr, IReadOnlyList<GhAlertReason> Reasons)
{
    public bool NeedsYou => Reasons.Count > 0;
}

/// <summary>The raw result of one poll: who you are, the open PRs that involve you, and an error line when
/// the fetch failed (in which case <see cref="Prs"/> is empty).</summary>
public sealed record GitHubFetchResult(string? Login, IReadOnlyList<GhPullRequest> Prs, string? Error, DateTime FetchedUtc);

/// <summary>What the overlay strip and the alerts window render: every PR with its reasons, most urgent
/// first, plus the fetch's time and any error.</summary>
public sealed record GitHubAlertsSnapshot(string? Login, IReadOnlyList<GhPrItem> Items, string? Error, DateTime FetchedUtc)
{
    public int NeedsYouCount => Items.Count(i => i.NeedsYou);

    /// <summary>The PRs grouped by repository for the window: repos with something needing you first, then
    /// alphabetical; within a repo, PRs needing you first, then most recently updated. When
    /// <paramref name="needsYouOnly"/> is set, PRs (and so repos) with nothing to do are dropped; a non-empty
    /// <paramref name="query"/> keeps only the PRs it <see cref="Matches"/>.</summary>
    public IReadOnlyList<(string Repo, IReadOnlyList<GhPrItem> Items)> ByRepo(bool needsYouOnly, string? query = null) =>
        Filter(needsYouOnly, query)
             .GroupBy(i => i.Pr.Repo, StringComparer.OrdinalIgnoreCase)
             .Select(g => (Repo: g.First().Pr.Repo, Items: (IReadOnlyList<GhPrItem>)g
                 .OrderByDescending(i => i.NeedsYou)
                 .ThenByDescending(i => i.Pr.UpdatedUtc)
                 .ToList()))
             .OrderByDescending(g => g.Items.Any(i => i.NeedsYou))
             .ThenBy(g => g.Repo, StringComparer.OrdinalIgnoreCase)
             .ToList();

    /// <summary>The items the window would show for a tab + search, ungrouped — its tab counts read this.</summary>
    public IEnumerable<GhPrItem> Filter(bool needsYouOnly, string? query) =>
        Items.Where(i => (!needsYouOnly || i.NeedsYou) && Matches(i, query));

    /// <summary>
    /// Whether <paramref name="item"/> matches a search: every whitespace-separated word must appear (ignoring
    /// case) somewhere in the PR's title, repo, author, number (with or without a leading <c>#</c>) or reason
    /// texts — so "web review" finds review requests in acme/web, and "#412" finds that PR. An empty query
    /// matches everything.
    /// </summary>
    public static bool Matches(GhPrItem item, string? query)
    {
        if (string.IsNullOrWhiteSpace(query)) return true;
        var pr = item.Pr;
        foreach (var word in query.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
        {
            var w = word.Length > 1 && word[0] == '#' ? word[1..] : word;
            bool hit = Has(pr.Title, w) || Has(pr.Repo, w) || Has(pr.Author, w)
                || Has(pr.Number.ToString(System.Globalization.CultureInfo.InvariantCulture), w)
                || item.Reasons.Any(r => Has(r.Text, w));
            if (!hit) return false;
        }
        return true;

        static bool Has(string haystack, string needle) => haystack.Contains(needle, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>How many PRs carry each kind of reason, in priority order, omitting kinds with none — the overlay
    /// strip's symbol + count chips. A PR with two reasons (changes requested <em>and</em> failing checks) counts
    /// under both, so each chip reads as "PRs in this state"; the chips needn't sum to <see cref="NeedsYouCount"/>.</summary>
    public IReadOnlyList<(GhAlertKind Kind, int Count)> KindCounts() =>
        Items.SelectMany(i => i.Reasons.Select(r => r.Kind).Distinct())
             .GroupBy(k => k)
             .OrderBy(g => g.Key)
             .Select(g => (g.Key, g.Count()))
             .ToList();

    /// <summary>The words a symbol stands for, with its count ("2 reviews requested") — the strip's tooltip.</summary>
    public static string Describe(GhAlertKind kind, int n) => kind switch
    {
        GhAlertKind.ReviewRequested  => n == 1 ? "1 review requested" : $"{n} reviews requested",
        GhAlertKind.ChangesRequested => n == 1 ? "1 with changes requested" : $"{n} with changes requested",
        GhAlertKind.NewActivity      => n == 1 ? "1 with new comments or reviews" : $"{n} with new comments or reviews",
        GhAlertKind.ChecksFailing    => n == 1 ? "1 with failing checks" : $"{n} with failing checks",
        GhAlertKind.Conflicts        => n == 1 ? "1 with merge conflicts" : $"{n} with merge conflicts",
        GhAlertKind.ReadyToMerge     => n == 1 ? "1 ready to merge" : $"{n} ready to merge",
        GhAlertKind.Assigned         => n == 1 ? "1 assigned to you" : $"{n} assigned to you",
        _                            => $"{n}",
    };
}

/// <summary>
/// Decides why each PR needs you. Pure (no IO, no clock) so every rule is unit-tested.
///
/// <para>The core idea is "whose move is it": activity by someone else only counts when it is newer than both
/// your own last move on the PR (a comment, a review, or — on your PR — your latest commit) and the moment you
/// last opened it from Perch (<paramref name="seenUtc"/>). So a review you've answered, or a thread you've read,
/// stops nagging without Perch needing any write access to GitHub. Bots are ignored throughout.</para>
///
/// <para>State-based reasons — review requested, checks failing, conflicts, ready to merge — stay for as long
/// as the state holds, since each needs an action rather than a read. "Assigned" is the exception: it shows
/// until you first open the PR, then only new activity brings it back.</para>
/// </summary>
public static class GitHubAlertsClassifier
{
    // A merge state that means "not mergeable yet" even when approval and checks look fine.
    private static readonly HashSet<string> NotReadyMergeStates =
        new(StringComparer.OrdinalIgnoreCase) { "BLOCKED", "BEHIND", "DIRTY", "DRAFT" };

    public static IReadOnlyList<GhAlertReason> Classify(GhPullRequest pr, string login, DateTime? seenUtc)
    {
        var reasons = new List<GhAlertReason>();
        bool mine = pr.Relation.HasFlag(GhPrRelation.Author) || SameUser(pr.Author, login);

        // Your own last move: your comments and reviews, and on your own PR your latest commit.
        DateTime floor = seenUtc ?? DateTime.MinValue;
        if (mine && pr.LastCommitUtc is { } pushed && pushed > floor) floor = pushed;
        foreach (var e in pr.Events)
            if (SameUser(e.Actor, login) && e.AtUtc > floor) floor = e.AtUtc;

        var fresh = pr.Events
            .Where(e => !e.IsBot && !SameUser(e.Actor, login) && e.Kind != GhEventKind.Dismissed && e.AtUtc > floor)
            .ToList();

        if (mine)
        {
            int cr = fresh.FindLastIndex(e => e.Kind == GhEventKind.ChangesRequested);
            int ap = fresh.FindLastIndex(e => e.Kind == GhEventKind.Approved);
            if (cr >= 0)
                reasons.Add(new(GhAlertKind.ChangesRequested, ByLine("Changes requested", fresh[cr].Actor)));
            else if (ap >= 0)
                reasons.Add(new(GhAlertKind.NewActivity, ByLine("Approved", fresh[ap].Actor)));
            else if (fresh.Count > 0)
                reasons.Add(new(GhAlertKind.NewActivity, ActivityText(fresh)));

            if (pr.Checks == GhChecks.Failing)
                reasons.Add(new(GhAlertKind.ChecksFailing, "Checks failing"));
            if (pr.Mergeable == GhMergeable.Conflicting)
                reasons.Add(new(GhAlertKind.Conflicts, "Merge conflicts"));
            if (IsReadyToMerge(pr))
                reasons.Add(new(GhAlertKind.ReadyToMerge, "Ready to merge"));
        }
        else
        {
            if (pr.Relation.HasFlag(GhPrRelation.ReviewRequested))
                reasons.Add(new(GhAlertKind.ReviewRequested,
                    pr.Author.Length > 0 ? $"Review requested · {pr.Author}" : "Review requested"));
            else if (pr.Relation.HasFlag(GhPrRelation.Assignee))
            {
                if (seenUtc is null)
                    reasons.Add(new(GhAlertKind.Assigned, "Assigned to you"));
                else if (fresh.Count > 0)
                    reasons.Add(new(GhAlertKind.NewActivity, ActivityText(fresh)));
            }
        }

        reasons.Sort((a, b) => a.Kind.CompareTo(b.Kind));
        return reasons;
    }

    /// <summary>Approved (by decision, or by an approving review on a repo that requires none), green or
    /// check-less, mergeable, not a draft, and not held back by branch protection.</summary>
    internal static bool IsReadyToMerge(GhPullRequest pr)
    {
        if (pr.IsDraft || pr.Mergeable != GhMergeable.Mergeable) return false;
        if (pr.Checks is GhChecks.Failing or GhChecks.Pending) return false;
        if (NotReadyMergeStates.Contains(pr.MergeState)) return false;
        return pr.ReviewDecision.Equals("APPROVED", StringComparison.OrdinalIgnoreCase)
            || (pr.ReviewDecision.Length == 0 && pr.HasApproval);
    }

    /// <summary>Classifies every PR in a fetch against the seen markers into the rendered snapshot.</summary>
    public static GitHubAlertsSnapshot Build(GitHubFetchResult fetch, IReadOnlyDictionary<string, DateTime> seen)
    {
        var login = fetch.Login ?? "";
        var items = fetch.Prs
            .Select(pr => new GhPrItem(pr, Classify(pr, login, seen.TryGetValue(pr.Url, out var s) ? s : null)))
            .ToList();
        return new GitHubAlertsSnapshot(fetch.Login, items, fetch.Error, fetch.FetchedUtc);
    }

    // "3 new comments · alice, bob" — counted and attributed, newest actor first, at most two names.
    private static string ActivityText(List<GhEvent> fresh)
    {
        var who = fresh.AsEnumerable().Reverse().Select(e => e.Actor).Where(a => a.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        string count = fresh.Count == 1 ? "New comment" : $"{fresh.Count} new comments";
        if (who.Count == 0) return count;
        string names = who.Count <= 2 ? string.Join(", ", who) : $"{who[0]}, {who[1]} +{who.Count - 2}";
        return $"{count} · {names}";
    }

    private static string ByLine(string what, string actor) => actor.Length > 0 ? $"{what} by {actor}" : what;

    private static bool SameUser(string a, string b) =>
        a.Length > 0 && string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
}
