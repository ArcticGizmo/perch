# GitHub alerts

An opt-in overlay section that tells you which of your open pull requests need you, plus a list window.
Setting: **Settings → Integrations → GitHub alerts** (`AppSettings.ShowGitHubAlerts`, poll interval
`GitHubAlertsIntervalMinutes`, default 5, clamped 2–60). Off by default; while off, nothing runs `gh`.

## What you see

- **Overlay:** one line (`OverlaySection.GitHub`, default position under Todo, reorderable like the
  others). "3 PRs need you" with an attention badge on the merge glyph, then one owner-drawn symbol + count per
  reason kind on the right, in priority order: eye = review requested, ± = changes requested, speech bubble = new
  comments/reviews, ⊗ = checks failing, warning triangle = conflicts, tick = ready to merge, person = assigned.
  A PR with two reasons counts under both. Chips that don't fit the panel width are dropped behind a "…". Hovering
  shows a tooltip spelling each symbol out. Otherwise "No PRs need you · 5 open", "checking…", or a one-line error
  (gh not found / not signed in / GitHub didn't respond). Clicking anywhere on the line opens the window, whose
  reason pills use the same colours as the symbols.
- **Window** (`GitHubAlertsWindow`, titled "GitHub dashboard"): each row shows the title,
  `#number · author/yours · role · updated …` (with the repo in front when not grouping by repo), a pill per
  reason, **Dismiss** and **Open in GitHub**. Filter tabs: **Needs you** (default), **All open** and
  **Dismissed** (rows there offer **Restore**), beside a search box (Ctrl+F) that keeps PRs where every word
  appears in the title, repo, author, number (`#412` or `412`) or reason text (`GitHubAlertsSnapshot.Matches`).
  The tab counts follow the search. A toolbar picks **Group by** (Repo / Reason / Role / None), **Sort** (Most
  urgent / Recently updated / Oldest first) and **Updated** (Any time / last day / week / 30 / 90 days). Refresh
  button. Esc clears the search, then closes.

## Dismiss and view options

See `docs/github-dashboard-plan.md` (parts 1 and 2) for the design.

- **Dismiss** hides a PR until its `GitHubAlertsClassifier.Fingerprint` changes: new non-bot activity by someone
  else, review decision, checks starting or stopping failing, conflicts, draft flag, your relation to it, or a new
  push on someone else's PR. Your own moves, bots, merge-state churn and pending → green checks don't count. Stored
  in `%AppData%/<profile>/github-alerts-dismissed.json` (`GitHubAlertsDismissStore`). After each successful poll
  `Reconcile` deletes entries whose PR closed or whose fingerprint moved on, so a PR doesn't quietly vanish again
  if its state swings back.
- **Include dismissed** (a toggle in the toolbar, not persisted) lets dismissed PRs back into Needs you / All
  open, marked "dismissed" with Restore, for hunting down a PR you hid. The strip still ignores them. When a search
  matches dismissed PRs the current view hides, a "N dismissed PRs also match · Include them" link turns it on
  (`GitHubAlertsSnapshot.HiddenDismissedMatches`).
- **View options** (`GhListOptions`) live on the monitor host, because the age window also trims the overlay
  strip's counts, so the strip and the window always agree. They persist as UI state on `AppSettings`
  (`GitHubDashboardGroupBy` / `SortBy` / `MaxAgeDays`, in `SettingsRegistryTests.NotSettings`). The age window is
  applied on the client only, on top of the first 50 PRs per search.

## Where the data comes from

`GitHubAlertsClient` (Core) shells out to the GitHub CLI, so Perch holds no token:

1. `gh api user` once per process (cached; reset when the feature is switched on) to learn your login. The
   login is checked against GitHub's login shape before it goes into a query string.
2. One `gh api graphql --input -` per poll: three searches in one request (`author:`, `review-requested:`,
   `assignee:`, all `is:pr is:open archived:false`, first 50 each), merged by URL into `GhPullRequest`s carrying
   a `GhPrRelation` flag set. Per PR it reads the review decision, mergeable, merge state, the head commit's
   date and check rollup, latest reviews, and the newest 30 reviews and 30 comments.

`GitHubAlertsMonitorHost` (App) runs it on a `DispatcherTimer`, off the UI thread, one fetch at a time. It skips
ticks while the desktop is locked and keeps the last good list when a poll fails.

## The rules (`GitHubAlertsClassifier`, pure and unit-tested)

"Whose move is it": activity by someone else counts only if it is newer than **both** your own last move on the
PR (your comment or review, or on your own PR your latest commit) **and** the moment you last opened it from
Perch. Bots (`__typename == Bot` or a `[bot]` login) are ignored.

| Reason | When | Clears when |
|---|---|---|
| Review requested | Someone else's PR requests your review | GitHub drops the request (you reviewed) |
| Changes requested by X | Your PR, a changes-requested review after your last move | You push, reply, or open it |
| Approved by X / N new comments | Your PR, new activity after your last move | You reply, push, or open it |
| Checks failing | Your PR, head commit rollup FAILURE/ERROR | Checks go green |
| Merge conflicts | Your PR, `mergeable == CONFLICTING` | Conflicts resolved |
| Ready to merge | Your PR: not draft, mergeable, checks green or none, approved (decision, or an approving review where none is required), merge state not BLOCKED/BEHIND/DIRTY | Merged or the state changes |
| Assigned to you | Someone else's PR assigned to you that you've never opened from Perch | First open. After that only new activity brings it back |

The seen markers live in `%AppData%/<profile>/github-alerts-seen.json` (`GitHubAlertsSeenStore`), keyed by PR
URL and pruned to the open set after each successful poll.

## Known limits / not done

- **Not run against live GitHub yet.** The GraphQL query and the parser are covered by fixture tests only. The
  first real run should confirm the query is accepted as written (`mergeStateStatus`, `latestReviews`) and the
  counts look right.
- Only the newest 30 comments and 30 reviews per PR are read, and the first 50 PRs per search.
- `committedDate` stands in for "when you last pushed". A rebase that keeps old commit dates won't count as a
  move.
- No desktop notifications yet. Row actions are open and dismiss; starting a session from a PR is part 3 of
  `docs/github-dashboard-plan.md`. The session-row PR glyph
  (`ShowPullRequests`) is a separate feature and is unchanged.
- The window has no tests (UI). Check it with `render` (`overlay_github_*.png`, including the `_narrow` and
  `_two` chip probes, and `github_alerts_*.png`).
