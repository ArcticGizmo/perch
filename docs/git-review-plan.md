# Session Change Review — phased plan

> Status: proposal / experiment. Author: design pass, 2026-08-05.
> Working title for the feature: **Session Change Review** (not "git tree").

## What was asked

A GitKraken-style git view launched from a right-click on a session: see history, stage
unstaged changes, author a commit, click back through commits to see their diffs. Explicitly
experimental — "just something to try in the real world," not every bell and whistle.

## The reframe (why this plan doesn't build a git client)

There are already excellent, free git clients everybody has open (GitKraken, Fork, VS Code,
lazygit, `gh`). Rebuilding a general history browser in owner-drawn Avalonia is a lot of work
to arrive at a worse version of those. The **only** thing that justifies putting git inside
Perch is the thing Perch uniquely knows: *which Claude session touched which repo, when, and
what it was doing.*

So the feature is reframed from "view the git tree" to **"review what this Claude session just
changed, and decide whether to keep it."** Session-scoped **audit**, not a git client. That is
both more valuable and much smaller.

Consequences baked into the phasing below:

- **Read-only audit first; commit later and optional.** Viewing diffs/history is safe and is
  the real experiment. Staging/committing is consequential (wrong files, bad messages in real
  repos) — a separate milestone you can cut entirely after using the audit view.
- **Committing while a session is Active is a hazard.** Claude Code edits — and sometimes runs
  git — in the same tree. `GitStatsService` already tiptoes around `index.lock` with
  `--no-optional-locks` for this reason. Commit must warn/refuse on an Active session. Auditing
  concurrently is fine.
- **No native git dependency.** No LibGit2Sharp (it complicates the macOS port and the NativeAOT
  hook). Reuse the proven shell-out idiom.
- **The real cost is owner-drawn rendering.** A lane-routed graph + syntax-highlighted diff is a
  large owner-drawn surface, every pixel bound by the render-mode/DPI/line-height rules. Phase 1
  is a **linear** history list and a **plain** unified diff. The lane graph is last and optional.
- **Non-modal, reused window** (like History/Stats), not modal.

## What already exists to build on

- `Perch.Core/Data/GitStatsService.cs` — the shell-out pattern to copy exactly: opt-in master
  switch, TTL cache, background refresh off the hot path, `--no-optional-locks`, best-effort
  (null on any failure), timeout ceiling, `StatsUpdated` nudge. `GitRepoService` is a sibling.
- `Perch.Core/Data/PrStatusService.cs` — same idiom over `gh`; the `.git` walk (`HasGitRepo`),
  the concurrency gate, and `DescribeGh()`-style capability probing are all reusable. The header
  can reuse `PullRequestInfo`.
- `ClaudeSession.Cwd` — every session already carries its working directory (and `GitStats` /
  `PullRequest`). Entry point has everything it needs.
- Per-session action surface already exists (Add note…, toggle notify, Hypertree jump in
  `App.axaml.cs`) — "Review changes…" slots in beside them.
- `Windows/WindowHost.ShowOrFocus` — the single-reused-window idiom; wire the new window in and
  into `CloseAuxWindows`.
- `Rendering/HeadlessRenderer` + `render <outDir>` mode — the standing way to eyeball the new
  owner-drawn surfaces at 1×/1.5×.
- `SettingsRegistry` — the feature ships behind an experimental `SettingDescriptor`, like
  git-stats and the PR integration (a coverage test enforces the descriptor exists).

## Milestones

### Milestone 0 — Git data layer + tests (de-risk, no UI)

Prove the read side cheaply and durably before any pixels.

- `Perch.Core/Data/GitRepoService.cs`, sibling to `GitStatsService`: opt-in, cached, background,
  best-effort, `--no-optional-locks`, timeout ceiling. Commands (all read-only, machine-readable):
  - `status --porcelain=v2 --branch` → working-tree + staged file list, branch, ahead/behind.
  - `log --max-count=N --format=…` → linear recent history (hash, author, date, subject).
  - `diff` / `diff --cached` / `show <hash>` → unified diff text for a file or a commit.
- Models: `GitFileChange` (path, status XY, staged/unstaged), `GitCommit`, `GitDiff` (parsed into
  hunks + typed lines: context/add/remove/header — enough to colour without a highlighter).
- Parsers are `internal static` + pure, unit-tested against **canned command output** (no live
  repo needed for parse tests) plus a small fixture repo for a couple of integration checks.
  Follows the existing `ParseNumstat` / `ParsePrJson` testing style in `Perch.Tests`.
- **Exit criteria:** `dotnet test` green; parsers handle rename/binary/CRLF/partial output;
  service never throws out of a scan; zero git processes while disabled.

### Milestone 1 — Read-only Change Review window (the experiment)

The actual thing to try in the real world. Read-only.

- Entry: per-session menu item **"Review changes…"** → `WindowHost.ShowOrFocus`, non-modal,
  positioned on the session/overlay's screen. Wire into `CloseAuxWindows`.
- Layout (single owner-drawn measure-or-paint routine, `Draw(DrawingContext?, width)` per the
  dashboard convention):
  - **Header:** repo name, branch, ahead/behind, PR chip (reuse `PullRequestInfo`), Active-session
    badge.
  - **Left / top:** changed files (working tree + staged), grouped, with per-file +/- counts.
  - **Right / main:** **plain** unified diff of the selected file — add/remove/context colouring
    from `Theming.Palette`, **no** syntax highlighting. Text sized from measured line height
    (CLAUDE.md gotcha), verified via `render` mode.
  - **Below:** **linear** recent-commit list; selecting one shows that commit's diff (`show`).
- Refresh follows the off-UI-thread → `Dispatcher.UIThread.Post` idiom; guard against a window
  closed mid-flight; subscribe to the service's update event for live refresh.
- Ships behind an experimental `SettingDescriptor` (feature off by default).
- **Exit criteria:** open on a live session, browse working-tree diff + last N commits, no jank,
  no git spawned when the feature/window is off; `render` output reviewed.
- **Decision gate:** *use this for a week.* Only proceed to M2 if authoring a commit here (vs.
  alt-tabbing) actually feels worth it. It's fine to stop here.

### Milestone 2 — Commit authoring (opt-in, guarded) — only if M1 earns it

The consequential milestone. Do not start until M1 has been lived with.

- Stage/unstage at **file** granularity first (`git add` / `git restore --staged`); hunk-level
  staging is a later nicety, not part of this milestone.
- Commit message editor (reuse the `StickyNoteWindow`-style multi-line editor); `git commit`.
- **Guards:** if the session is Active, warn and require explicit confirmation (or refuse) —
  this is where races with Claude's own git live. `ConfirmDialog` before the commit. Surface
  command failures instead of swallowing them (unlike the read path).
- Optional: amend last commit, discard a file's changes (destructive → confirm hard).
- **Exit criteria:** a commit authored from Perch lands correctly; Active-session guard works;
  failures are reported, not silent.

### Milestone 3 — Graph & polish (optional, "earn it")

The GitKraken-looking bells and whistles, each independently cuttable:

- Lane-routed commit graph (the expensive owner-drawn piece — colour lanes, merge routing).
- Syntax highlighting in the diff.
- Branch/remote awareness, checkout/switch, stash.
- Search/filter of history.

## Risks / open questions

- **Owner-drawn diff + graph cost** is the dominant risk — mitigated by keeping M1 linear+plain.
- **Concurrency with a live session** — read path uses `--no-optional-locks`; write path (M2)
  guards on Active.
- **Large repos / huge diffs** — cap `log` count, timeout ceiling, and cap/scroll giant diffs.
- **macOS parity** — pure shell-out keeps it portable; no new platform interface needed unless a
  future step wants native diff tooling.
- **Scope honesty:** even trimmed, M1 is bigger than the existing aux windows. M0+M1 is the
  experiment; M2/M3 are opt-in follow-ups.
