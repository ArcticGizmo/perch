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
    /// <summary>The PR's head branch name (<c>headRefName</c>). Chosen by the PR's author, so untrusted text.</summary>
    public string HeadBranch { get; init; } = "";
    /// <summary>The branch it merges into (<c>baseRefName</c>).</summary>
    public string BaseBranch { get; init; } = "";
    /// <summary>owner/name of the repo the head branch lives in; empty when that fork has been deleted.</summary>
    public string HeadRepo { get; init; } = "";
    /// <summary>The head comes from another repository (a fork), so its branch name means nothing in a local
    /// checkout of <see cref="Repo"/>.</summary>
    public bool IsCrossRepository { get; init; }
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

/// <summary>A PR plus the reasons (possibly none) it needs you, and whether the user's view hides it: dismissed until
/// its state changes, or not updated within the dashboard's age window.</summary>
public sealed record GhPrItem(GhPullRequest Pr, IReadOnlyList<GhAlertReason> Reasons)
{
    public bool NeedsYou => Reasons.Count > 0;

    /// <summary>Dismissed, and its state fingerprint still matches the one it was dismissed at.</summary>
    public bool Dismissed { get; init; }

    /// <summary>Not updated within the "Updated within" window — hidden from every tab and the strip.</summary>
    public bool TooOld { get; init; }

    /// <summary>Counts on the strip and shows under Needs you / All open.</summary>
    public bool Shown => !Dismissed && !TooOld;
}

/// <summary>The window's tabs: what needs you, every open PR, or the ones you dismissed. Too-old PRs are in none.</summary>
public enum GhView { NeedsYou, All, Dismissed }

/// <summary>How the window groups its rows. <see cref="None"/> is one flat list with no headers.</summary>
public enum GhGroupBy { Repo, Reason, Role, None }

/// <summary>How rows are ordered within a group: most urgent first, most recently updated first, or stalest first.</summary>
public enum GhSortBy { Urgency, Updated, Oldest }

/// <summary>
/// The dashboard's view options, persisted as UI state on <see cref="AppSettings"/>. <see cref="MaxAgeDays"/> of 0
/// means any age; otherwise PRs not updated in that many days are hidden everywhere, strip included.
/// </summary>
public sealed record GhListOptions(GhGroupBy GroupBy = GhGroupBy.Repo, GhSortBy SortBy = GhSortBy.Urgency, int MaxAgeDays = 0)
{
    /// <summary>The choices the window's "Updated within" picker offers (0 = any).</summary>
    public static readonly IReadOnlyList<int> AgeChoices = [0, 1, 7, 30, 90];

    /// <summary>Values read back from a settings file as real ones: an enum value no build defines falls back to the
    /// default, and an age that isn't positive means "any".</summary>
    public GhListOptions Normalize() => new(
        Enum.IsDefined(GroupBy) ? GroupBy : GhGroupBy.Repo,
        Enum.IsDefined(SortBy) ? SortBy : GhSortBy.Urgency,
        Math.Max(0, MaxAgeDays));

    /// <summary>The oldest "updated" time still shown, or null when any age is.</summary>
    public DateTime? UpdatedSince(DateTime nowUtc) => MaxAgeDays > 0 ? nowUtc.AddDays(-MaxAgeDays) : null;
}

/// <summary>The raw result of one poll: who you are, the open PRs that involve you, and an error line when
/// the fetch failed (in which case <see cref="Prs"/> is empty).</summary>
public sealed record GitHubFetchResult(string? Login, IReadOnlyList<GhPullRequest> Prs, string? Error, DateTime FetchedUtc);

/// <summary>What the overlay strip and the alerts window render: every PR with its reasons, most urgent
/// first, plus the fetch's time and any error.</summary>
public sealed record GitHubAlertsSnapshot(string? Login, IReadOnlyList<GhPrItem> Items, string? Error, DateTime FetchedUtc)
{
    /// <summary>PRs needing you that aren't dismissed or too old — the strip's headline count.</summary>
    public int NeedsYouCount => Items.Count(i => i.Shown && i.NeedsYou);

    /// <summary>How many PRs the "Updated within" window hides, across every tab.</summary>
    public int TooOldCount => Items.Count(i => i.TooOld);

    /// <summary>The items the window would show for a tab + search, ungrouped — its tab counts read this. Too-old PRs
    /// are in no tab; dismissed ones only in <see cref="GhView.Dismissed"/>, unless
    /// <paramref name="includeDismissed"/> (the window's "Include dismissed", for hunting a PR down) lets them back
    /// into Needs you / All open as well.</summary>
    public IEnumerable<GhPrItem> Filter(GhView view, string? query, bool includeDismissed = false) =>
        Items.Where(i => !i.TooOld && view switch
        {
            GhView.NeedsYou  => (includeDismissed || !i.Dismissed) && i.NeedsYou,
            GhView.All       => includeDismissed || !i.Dismissed,
            GhView.Dismissed => i.Dismissed,
            _                => false,
        } && Matches(i, query));

    /// <summary>Dismissed PRs a search turns up that <paramref name="view"/> is hiding — the window's "N dismissed
    /// PRs also match" hint. Zero with no search, or when the view already shows dismissed ones.</summary>
    public int HiddenDismissedMatches(GhView view, string? query, bool includeDismissed) =>
        string.IsNullOrWhiteSpace(query) || includeDismissed || view == GhView.Dismissed
            ? 0
            : Filter(view, query, includeDismissed: true).Count(i => i.Dismissed);

    /// <summary>
    /// The window's rows for a tab + search, grouped and sorted per <paramref name="options"/>. Group order:
    /// by repo, repos with something needing you first then A–Z; by reason, the headline reason in priority order
    /// with "Nothing to do" last; by role, yours → review requested → assigned. <see cref="GhGroupBy.None"/> yields
    /// one group with an empty title (the window draws no header for it). Within a group rows follow
    /// <see cref="GhListOptions.SortBy"/>; ties fall back to most recently updated, then PR number.
    /// </summary>
    public IReadOnlyList<(string Title, IReadOnlyList<GhPrItem> Items)> Grouped(GhView view, GhListOptions options,
        string? query = null, bool includeDismissed = false)
    {
        var rows = Filter(view, query, includeDismissed).ToList();
        if (rows.Count == 0) return [];

        IReadOnlyList<GhPrItem> Sort(IEnumerable<GhPrItem> items) => (options.SortBy switch
        {
            GhSortBy.Updated => items.OrderByDescending(i => i.Pr.UpdatedUtc),
            GhSortBy.Oldest  => items.OrderBy(i => i.Pr.UpdatedUtc),
            _                => items.OrderByDescending(i => i.NeedsYou)
                                     .ThenBy(i => i.NeedsYou ? (int)i.Reasons[0].Kind : int.MaxValue)
                                     .ThenByDescending(i => i.Pr.UpdatedUtc),
        }).ThenByDescending(i => i.Pr.UpdatedUtc).ThenBy(i => i.Pr.Number).ToList();

        switch (options.GroupBy)
        {
            case GhGroupBy.None:
                return [("", Sort(rows))];

            case GhGroupBy.Reason:
                return rows.GroupBy(i => i.NeedsYou ? (int)i.Reasons[0].Kind : int.MaxValue)
                           .OrderBy(g => g.Key)
                           .Select(g => (g.Key == int.MaxValue ? "Nothing to do" : ReasonGroupTitle((GhAlertKind)g.Key), Sort(g)))
                           .ToList();

            case GhGroupBy.Role:
                return rows.GroupBy(i => RoleRank(i.Pr.Relation))
                           .OrderBy(g => g.Key)
                           .Select(g => (RoleGroupTitle(g.Key), Sort(g)))
                           .ToList();

            default:
                return rows.GroupBy(i => i.Pr.Repo, StringComparer.OrdinalIgnoreCase)
                           .Select(g => (Title: g.First().Pr.Repo, Items: Sort(g)))
                           .OrderByDescending(g => g.Items.Any(i => i.NeedsYou))
                           .ThenBy(g => g.Title, StringComparer.OrdinalIgnoreCase)
                           .ToList();
        }
    }

    // "Yours" wins over the other relations, as on the row's meta line.
    private static int RoleRank(GhPrRelation r) =>
        r.HasFlag(GhPrRelation.Author) ? 0
        : r.HasFlag(GhPrRelation.ReviewRequested) ? 1
        : r.HasFlag(GhPrRelation.Assignee) ? 2
        : 3;

    private static string RoleGroupTitle(int rank) => rank switch
    {
        0 => "Yours",
        1 => "Review requested",
        2 => "Assigned to you",
        _ => "Other",
    };

    private static string ReasonGroupTitle(GhAlertKind kind) => kind switch
    {
        GhAlertKind.ReviewRequested  => "Review requested",
        GhAlertKind.ChangesRequested => "Changes requested",
        GhAlertKind.NewActivity      => "New comments or reviews",
        GhAlertKind.ChecksFailing    => "Checks failing",
        GhAlertKind.Conflicts        => "Merge conflicts",
        GhAlertKind.ReadyToMerge     => "Ready to merge",
        GhAlertKind.Assigned         => "Assigned to you",
        _                            => kind.ToString(),
    };

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
    /// under both, so each chip reads as "PRs in this state"; the chips needn't sum to <see cref="NeedsYouCount"/>.
    /// Dismissed and too-old PRs don't count.</summary>
    public IReadOnlyList<(GhAlertKind Kind, int Count)> KindCounts() =>
        Items.Where(i => i.Shown)
             .SelectMany(i => i.Reasons.Select(r => r.Kind).Distinct())
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

    /// <summary>
    /// Classifies every PR in a fetch against the seen markers into the rendered snapshot. A PR is
    /// <see cref="GhPrItem.Dismissed"/> when <paramref name="dismissed"/> (URL → fingerprint) holds its current
    /// <see cref="Fingerprint"/>, and <see cref="GhPrItem.TooOld"/> when it was last updated before
    /// <paramref name="updatedSinceUtc"/> (a PR with no readable update time is never too old).
    /// </summary>
    public static GitHubAlertsSnapshot Build(GitHubFetchResult fetch, IReadOnlyDictionary<string, DateTime> seen,
        IReadOnlyDictionary<string, string>? dismissed = null, DateTime? updatedSinceUtc = null)
    {
        var login = fetch.Login ?? "";
        var items = fetch.Prs
            .Select(pr => new GhPrItem(pr, Classify(pr, login, seen.TryGetValue(pr.Url, out var s) ? s : null))
            {
                Dismissed = dismissed is not null && dismissed.TryGetValue(pr.Url, out var fp) && fp == Fingerprint(pr, login),
                TooOld = updatedSinceUtc is { } since && pr.UpdatedUtc > DateTime.MinValue && pr.UpdatedUtc < since,
            })
            .ToList();
        return new GitHubAlertsSnapshot(fetch.Login, items, fetch.Error, fetch.FetchedUtc);
    }

    /// <summary>
    /// A PR's state as it matters to someone who has handed it off — a dismissal holds while this is unchanged. In:
    /// your relation to it, draft, review decision, checks failing or not, conflicting or not, the head commit
    /// (only on someone else's PR — on yours a push is your own move) and the newest non-bot activity by someone
    /// else. Out, because they churn without news: merge state (BEHIND whenever the base moves), an unknown
    /// mergeable (GitHub computes it lazily), pending → green checks, your own activity, bots and seen markers.
    /// </summary>
    public static string Fingerprint(GhPullRequest pr, string login)
    {
        bool mine = pr.Relation.HasFlag(GhPrRelation.Author) || SameUser(pr.Author, login);
        var theirs = pr.Events
            .Where(e => !e.IsBot && !SameUser(e.Actor, login) && e.Kind != GhEventKind.Dismissed)
            .Select(e => e.AtUtc)
            .DefaultIfEmpty(DateTime.MinValue)
            .Max();
        long commit = !mine && pr.LastCommitUtc is { } c ? c.Ticks : 0;
        return string.Join('|',
            (int)pr.Relation,
            pr.IsDraft ? 1 : 0,
            pr.ReviewDecision,
            pr.Checks == GhChecks.Failing ? 1 : 0,
            pr.Mergeable == GhMergeable.Conflicting ? 1 : 0,
            commit,
            theirs.Ticks);
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
