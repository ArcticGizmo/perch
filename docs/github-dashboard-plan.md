# GitHub dashboard plan

Grows the GitHub alerts feature (`docs/github-alerts.md`) into a small PR dashboard. Users asked for three things:

1. **Dismiss** a PR so it stops showing until its status changes again ("this one is out of my hands now").
2. **More ways to slice the list**: group by repo / reason / role / nothing, sort by urgency or time, and a
   maximum age for recency.
3. **Start a session from a PR** with a quick prompt, so Claude starts working on it in the background.

Parts 1 and 2 are small and build on what already exists. Part 3 is larger and gets its own phases. Branch:
`github-dashboard`.

## Where it starts from

- `GitHubAlertsClient` (Core) fetches open PRs that involve you through `gh` (one GraphQL request, three
  searches, first 50 each). `GitHubAlertsClassifier` (pure) turns each one into "needs you" reasons.
- `GitHubAlertsSeenStore` keeps "last opened from Perch" per PR URL in `github-alerts-seen.json`, pruned to the
  open set after each successful poll.
- `GitHubAlertsSnapshot.ByRepo` is the only grouping. The window has two tabs (Needs you / All open), a search
  box, and one action per row (Open in GitHub).

## Part 1: dismiss until the status changes

### Behaviour

- Each row gets a **Dismiss** button. The PR leaves Needs you and All open, stops counting on the overlay strip,
  and appears under a third tab, **Dismissed**, where **Restore** brings it back.
- It returns on its own when its **state fingerprint** changes. Merged or closed PRs drop out of the store
  with the open-set prune, the same way seen markers do.
- **Include dismissed** (a non-persisted toolbar toggle) ignores dismissals in Needs you / All open, so you can
  find a PR you hid. A search that only dismissed PRs match offers a one-click "Include them" link.
- Once the fingerprint has changed, the dismissal is deleted, not just ignored. So a PR whose checks go red and
  then green again doesn't quietly disappear a second time.

### The fingerprint (`GitHubAlertsClassifier.Fingerprint`, pure)

It covers what "the status changed" means to someone who has handed a PR off:

| Included | Why |
|---|---|
| Your relationship to it (author / review requested / assignee flags) | A re-requested review is a new ask |
| Draft flag | Marked ready for review |
| Review decision | Approved / changes requested |
| Checks **failing or not** | A red build is news. Pending → green after a push is not |
| Conflicting **or not** | Conflicts appearing or clearing |
| Head commit date, **only on someone else's PR** | They pushed, so there's something new to review. On your own PR your push is your own move |
| Time of the newest non-bot event by someone else | New comments or reviews |

Deliberately left out: `mergeStateStatus` (it flips to BEHIND whenever the base branch moves), `mergeable ==
UNKNOWN` (GitHub computes it lazily), your own comments, reviews and pushes, bot activity, and seen markers.

### Storage

`GitHubAlertsDismissStore` (Core) is a sibling of the seen store: `github-alerts-dismissed.json` maps
`url → { fingerprint, atUtc }`. It uses the same best-effort IO and `AtomicFile`, and `InMemory()` for render and
tests. After each successful poll, `Reconcile(currentFingerprints)` drops PRs that have closed and dismissals
whose fingerprint no longer matches.

## Part 2: grouping, sorting, recency

### Options (`GhListOptions`, Core)

- **Group by:** Repo (default; repos needing you first, then A–Z) · Reason (by the headline reason, in priority
  order, "Nothing to do" last) · Role (Yours · Review requested · Assigned) · None (one flat list, no headers).
- **Sort within a group:** Urgency (default: needs you first, then headline reason priority, then most recently
  updated) · Recently updated · Oldest first.
- **Updated within:** Any (default) · 1 day · 7 days · 30 days · 90 days. PRs not updated in that window drop out
  of every tab **and** of the overlay strip's counts, so the strip and the window always agree. When the filter
  hides something, the empty or footer text says how many it hid.

The window's toolbar sets these. They persist as UI state on `AppSettings` (`GitHubDashboardGroupBy`,
`GitHubDashboardSortBy`, `GitHubDashboardMaxAgeDays`), listed in `SettingsRegistryTests.NotSettings` like the
Roost's rail sort. Unknown enum values from a newer build fall back to the defaults.

### Shape

`GitHubAlertsClassifier.Build(fetch, seen, dismissals, updatedSinceUtc)` stays pure (no clock read) and marks
each `GhPrItem` as `Dismissed` / `TooOld`. `NeedsYouCount` and `KindCounts` count only items that are shown.
`GitHubAlertsSnapshot.Grouped(view, options, query)` replaces `ByRepo`, and `Filter(view, query)` drives the tab
counts. `GhView` = NeedsYou / All / Dismissed replaces the `needsYouOnly` bool. The host owns the current options
(the strip needs the age cutoff) and raises `OptionsChanged`, which the App persists.

### Not doing (yet)

- Pushing the age window into the search query (`updated:>=…`). Each search returns at most 50 PRs, so a
  server-side filter would help heavy users. It's a one-line query change, but it means refetching when the
  option changes, so it waits until someone hits the 50 cap.
- Desktop notifications.

## Part 3: start a session from a PR

### Which Claude product

Perch doesn't try to detect one. The default is a **Perch-controlled session** (the `claude` CLI over
stream-json). It's the only host where Perch can start Claude, give it a prompt, keep it running with no window
open, and show its status (running / waiting / done) in the overlay. Alternatives:

- **Terminal:** `ISessionLauncher.RunClaudeCommand(cwd, "\"<prompt>\"", …)`. It works, but the session isn't
  backgrounded and is only tracked like any other CLI session. Offered as a setting for terminal-first users.
- **Claude Desktop's Code tab:** not possible. Perch can only open or focus the app (`OpenClaudeDesktop`), not
  give it a prompt.
- **Claude Code on the web:** a good match for PRs because it clones the repo itself, removing steps 1 and 2
  below. Its launch surface needs investigating before we commit to it.

Optional heuristic: default to the host of your most recent session in that repo (Perch already records each
session's host). One setting, "Start PR sessions in: Perch (default) / Terminal", is enough to start with.

### Status (2026-10-08)

S1–S4 are built on `github-dashboard` and S5 isn't started. The real app has never run any of it. Covered by unit
tests (`GitHubCheckoutsTests`, `PrSessionPromptsTests`) and the render (`pr_session_*.png`). Differences from the
phases below, settled while building:

- **S1** reads remotes straight from `.git/config` (`GitCheckoutScanner`, following a linked worktree's
  `commondir`) instead of running `git remote -v` per folder. Remembered folders are stored in
  `AppSettings.GitHubRepoCheckouts`.
- **S2** uses git only, not `gh pr checkout`: `git fetch <remote> pull/<n>/head:perch/pr-<n>` from whichever remote
  names the PR's repo (works for PRs from forks), then `git worktree add`, both through `GitRunner` as a
  `UserAction`. A plain fetch refuses to move a branch that has local commits, so an earlier session's work is
  never discarded. An existing worktree is reused as it is. The branch doesn't track the PR head, so a user who
  wants to push does it by hand (`git push <remote> HEAD:<head-branch>`). The worktree stays when the PR closes;
  cleaning it up is still to do.
- **S3**: each template is a short **task** ("Find out why CI is failing and fix it", with the `gh` commands),
  editable in the dialog. `PrSessionPrompts.Compose` wraps it with the PR reference and branch, and the rules (no
  push or post, read-only in plan mode, the untrusted-text guard). The dialog previews the full prompt on request.
  Persisted custom templates are later. Conflicts are resolved by **merging** the base branch, not rebasing,
  so nothing needs a force-push.
- **S4** is `PrSessionWindow`, opened from **Start session…** on each dashboard row. It asks three things: *what*
  (a radio list of quick prompts, the ones fitting the PR first, with only "Read-only" badged), *where* (the
  checkout with its name pinned, then "New worktree for this PR" or "Your checkout as it is", which names the
  branch the checkout is on), and the *account* on machines with a choice. Account selection reuses
  `SessionAccountChoice` (guardrails included) and is resolved again for the worktree folder at launch. Folder
  trust is asked for the worktree itself, because its code is the PR's.
- **Not in the background after all** (user feedback): Start opens an ordinary Perch-controlled **session window**,
  named after the PR with `/rename PR #<n> · <title>` (sent before the prompt; the CLI queues both), so the name
  is kept in the transcript and survives a resume.

### Phases

- **S1, repo → local checkout.** A PR names `owner/repo`, not a folder. Build a map from the folders Perch has
  seen Claude sessions in (`~/.claude/projects` cwds) to their `origin` remote (via `GitRunner`), matched
  case-insensitively over https and ssh URL forms. If nothing matches, or several folders do, ask once ("Where is
  acme/web checked out?") and remember the answer. Pure URL normalisation plus tests.
- **S2, branch isolation.** Never check out the PR branch in the user's working copy. Default to one
  `git worktree` per PR under a Perch-owned folder (e.g. `<repo>/../.perch-worktrees/<repo>-pr-<n>`), created with
  `gh pr checkout <n>` inside the worktree. Reuse it if it already exists, and offer a cleanup when the PR closes.
  The directory-trust check (`DirectoryTrust`) applies to the new folder.
- **S3, quick prompts.** Editable templates with `{pr}` (`owner/repo#n`), `{url}`, `{title}`. Each is offered only
  when its reason applies:
  - Address review comments (changes requested / new activity on yours)
  - Fix failing checks (checks failing)
  - Rebase and resolve conflicts (conflicts)
  - Review this PR and draft comments, don't post them (review requested)
  - Free text
- **S4, background launch.** `StartNew(cwd, model, mode)` gains an initial prompt and a **start without a window**
  flag. The session is registered like any controlled one, so it shows on the overlay and in the Roost, and
  clicking it opens its window. Account (config dir) choice uses the existing session account selector. Permission
  mode is chosen per template, never bypass by default. A session blocked on a permission shows as waiting, which
  is the expected behaviour unattended.
- **S5, link it back.** The dashboard row shows a glyph for a PR that has a live Perch session (matched by
  worktree folder or branch) and clicking it opens the session, so the same PR can't be launched twice by
  accident.

### Safety

- **Prompt injection.** PR titles, bodies and comments are written by other people. The prompt Perch builds
  contains only Perch-generated text: the PR reference and URL, the reason it needs you, and the user's template.
  Claude reads the PR itself with `gh`, so the untrusted text arrives as tool output, not as user instructions.
  The title appears only in the template the user picked and can see.
- **Unattended edits** happen in a worktree, so the worst case is a branch the user can inspect or delete, never
  a modified main checkout.
- **No auto-push.** Templates say "commit locally, don't push" unless the user edits that out.

## Part 4: session modes, worktree detection, templates

Agreed 2026-10-09. A PR session has two independent choices, **where** the code lives and **what** Claude does:

| Where (`PrWorkspace`) | What it is | Default when |
|---|---|---|
| `ExistingWorktree` | A worktree the user already has with the PR's head branch checked out | The PR is yours (not from a fork) and such a worktree exists |
| `Worktree` | A new worktree, laid out the way the user's existing ones are | A checkout is found, same-repo PR |
| `Clone` | A separate clone (`--filter=blob:none`) in Perch's data folder. Nothing shared with the user's repo | PR from a fork, or no checkout found |
| `DiffOnly` | An empty Perch scratch folder; Claude reads the PR via `gh` | Never the default; picked for quick reviews |
| `Checkout` | The user's checkout as it is (today's second option) | Never the default |

**What** (`PrIntent`): `Review` (plan mode, read-only) · `Fix` (accept edits) · `ReviewThenFix` (plan mode; the review
ends with a proposed fix plan, and approving it on Perch's existing plan-approval card switches the same session to
editing) · `Custom`. `DiffOnly` + any editing intent offers to upgrade to a worktree.

The per-PR worktree moves out of `.perch-worktrees`. Its location follows what the user already does, and falls back
to Claude Code's own `.claude/worktrees/pr-{number}`.

### Checkpoints

Each is a commit, with tests passing and render probes updated where there's UI.

- **W1, PR head info.** Add `headRefName`, `baseRefName`, `headRepository { nameWithOwner }`, `isCrossRepository`
  to the GraphQL fragment. `GhPullRequest` gets `HeadBranch`, `BaseBranch`, `HeadRepo`, `IsCrossRepository`.
  Parser tests. (The query still hasn't been run against live GitHub; these are standard fields.)
- **W2, list a repo's worktrees.** `GitWorktreeScanner` (Core, no git process): from a checkout, find the shared
  git dir, read `worktrees/*/gitdir` (each names a worktree's `.git` file) and each worktree's `HEAD`, and return
  the main root plus `(path, branch)` for every worktree. `RepoCheckoutResolver` folds linked worktrees into their
  main checkout, so a repo with five worktrees is one candidate, not a five-way choice. Temp-dir tests with plain
  files.
- **W3, infer the layout.** `WorktreeLayout` (pure): from the existing worktrees' paths and branches, infer a path
  template: `{parent}/{repo}-{branch}`, `{parent}/{repo}.{branch}`, `{root}/.worktrees/{branch}`,
  `{root}/.claude/worktrees/{branch}`, bare-repo `{root}/{branch}`, or anything else expressible relative to the
  root or its parent. Also infer how a `/` in a branch name maps to a folder (`-`, or nested). The most common
  pattern wins. With no worktrees, `.gitignore` entries (`.worktrees/`, `.claude/worktrees`) are hints, and the
  final fallback is `{root}/.claude/worktrees/{name}`. A PR's worktree name is `pr-{number}`.
- **W4, use it.** `PrWorktree` places new worktrees by the inferred layout. A layout nested inside the checkout gets
  an entry in the repo's **`.git/info/exclude`**, not `.gitignore`. It's local and never committed, so the worktree
  doesn't show as untracked and nothing in the repo changes. The dialog offers `ExistingWorktree` when W2 finds the
  PR's head branch checked out somewhere (same-repo PRs only; a fork's branch name means nothing locally).
- **W5, Clone and DiffOnly.** `PrClone` (Core): `git clone --filter=blob:none <repo url>` into
  `%LocalAppData%/<profile>/pr-clones/<owner>-<repo>-pr-<n>`, then fetch `pull/<n>/head` onto `perch/pr-<n>`. The
  URL is built from `owner/repo` on github.com, never taken from PR text. Reused if present. `DiffOnly`: an empty
  `pr-scratch/<owner>-<repo>-pr-<n>` folder. Perch made it and it's empty, so it's granted folder trust directly
  (clones and worktrees still ask). The dialog's *Where* becomes these five options, only showing the ones that
  apply, with the default from the table above.
- **W6, intents and variables.** `PrPromptTemplate` gains `Intent`, a default `Where`, and an optional `Model`.
  `ReviewThenFix` is a built-in. New variables are split by trust:
  - Trusted: `{owner}` `{name}` `{base}` `{reasons}` (the PR's reason texts), `{branch}` (Perch's local branch),
    `{worktree}`.
  - Untrusted: `{author}` and `{head}` join `{title}`, flattened to one line and length-capped. A branch name is
    chosen by the PR author.
  - A task that starts with `/` (a slash command or skill) is sent as written, with no wrapper, because anything
    before it would stop it running as a command. The editor says so.
- **W7, saved templates and editor.** `AppSettings.PrSessionTemplates` (null = the built-ins), listed in
  `SettingsRegistryTests.NotSettings` like `CustomThemes`. A Settings page, "PR session templates": list, add,
  duplicate, delete, reorder; fields for name, intent, default Where, model, reasons it fits, and task; a live
  preview of the composed prompt against a sample PR. The dialog's list comes from here.
- **W8, slash commands and skills in templates.** The editor's task box offers `/` completion from Perch's
  `SlashCommandCatalog` (built-ins, user and plugin commands, skills), so a template can be `/code-review {url}`.
  Repo-level shared templates (read from the main checkout's default branch, never the PR's code) stay out until
  asked for.
- **W9, per-repo memory.** Remember the last template and *Where* per repo, next to the remembered checkout, and
  preselect them.
- **W10, link back (was S5).** A dashboard row shows when a Perch session is running for that PR (matched by its
  folder: worktree, clone, scratch or checkout) and opens it, instead of offering a second launch.

## Testing

- Core (xUnit, `GitHubAlertsTests`): fingerprint stability (each excluded field changing leaves it unchanged,
  each included field changes it), dismiss/restore/reconcile round trip, `Build` with dismissals and an age
  cutoff, strip counts excluding hidden items, each grouping and sort order, tab filters.
- Render: `github_alerts_*` gains a grouped-by-reason view, a Dismissed tab, and an age-filtered empty state.
- Live: the feature has still never been run against real GitHub (see `docs/github-alerts.md`). The first live
  run covers the query as well as these additions.
