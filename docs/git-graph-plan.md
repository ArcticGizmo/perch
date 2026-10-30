# Git graph: milestone & checkpoint plan

Grows the **View tree** window (`GitTreeWindow`) into a first-class git graph that replaces it: navigate the whole graph, double-click to check out, drag to merge/rebase/cherry-pick/create worktrees, and worktrees shown on the graph with their Claude sessions.

Design artefact: [`git-graph-design.html`](git-graph-design.html) (published: https://claude.ai/artifact/1jcGkR1LQWVVPRmsjg7EVK). Where the two disagree, this doc wins.

Branch: `git-graph` (from `main` at 9083893).

## Decisions (2026-10-30)

| Question | Decision |
|---|---|
| Q1: new window or grow View tree? | **Grow `GitTreeWindow` so it replaces View tree.** Today's branch-scoped view becomes the "This branch" scope. |
| Q4: guard when a session is working in the target worktree | **Warn** (with an "Ask the session" option where it fits). No hard block. |
| Q5: three-way merge tool | **Deferred (D1)**, wanted eventually. Until then: conflict list + "Open in editor" + "Ask the session". |
| Q6: interactive rebase (drag to reorder/squash) | **Wanted, deferred (D2).** Must beat GitKraken's performance: design it on the same virtualised canvas and lane model, never a per-commit control tree. |
| Big repos | The user has large repos to check out; every milestone that touches loading or painting is measured against them (`perch bench-graph`). |
| Q2, Q3, Q7–Q10 | Take the artefact's recommendations unless the user says otherwise: session row + Roost + `perch tree` entry points; ask on dirty checkout; sibling `..\repo--branch` worktrees; fetch on click/open only; keep the per-window light toggle with lane hues as theme roles; commit↔session attribution deferred (D3). |

## What a checkpoint is

A small, independently committable slice that clears the **green gate**:

1. `dotnet build perch.slnx` clean on Windows (both heads; warnings are errors).
2. `dotnet test tests/Perch.Tests/Perch.Tests.csproj` green.
3. Owner-drawn changes: `dotnet run --project src/Perch.App -f net10.0-windows10.0.19041.0 -- render captures/<stamp>-git-graph` and the PNGs eyeballed.
4. User-visible changes: a section in `docs/git-graph-testing.md` with before/after manual checks.
5. This doc's checkpoint gets an **As built** note; anything that needs a real desktop goes on **Owed live checks**.

No mandatory user pause between checkpoints; the end of each milestone is a natural point to dogfood.

## Architecture

```
Perch.Core/Data/GitGraph/            (pure, UI-free, unit-tested)
  GraphModels      GraphCommit, GraphRef(+Kind), GraphEdge, GraphRow
  GraphLogReader   git log / for-each-ref parsers + the loader (via GitRunner)
  LaneAllocator    incremental, resumable lane assignment (paging), stable across refreshes
  GraphView        rows for a scope: WIP rows per dirty worktree, worktree HEAD marks, ref decorations
  GitOps           (M4+) the write side: switch, stash, merge, rebase, cherry-pick, worktree add,
                   routed to the worktree that owns the branch; one queue per repo; lock-aware
Perch.App
  Views/GraphCanvas        owner-drawn, row-virtualised; one measure-or-paint routine; OverlayDraw pens/brushes
  Windows/GitTreeWindow    rail (worktrees / local / remotes / stashes / tags) | graph | detail (files, DiffView, composer)
```

**Lane model.** Commits arrive in `--topo-order` (children first). Each lane slot waits for one commit hash. A commit takes the leftmost slot waiting for it (other waiters converge into it in the band just above the node), its first parent continues in its lane, and extra parents join a lane already waiting for them or open a new one in the band just below. Lanes are never compacted, so a branch keeps a straight line; freed slots are reused. The primary branch's first-parent chain (the default branch, or HEAD) is pinned to lane 0. Each row stores its node lane and its **up** edges (the band from the previous row), so a virtualised painter draws any window of rows from those rows alone, and the allocator can resume when the next page arrives.

**Stability.** A refresh lays out again with the previous layout as a hint: a lane starting at a commit (or a commit a few steps down its first-parent chain) that had a lane before takes the same lane if it's free; a brand-new lane goes after every hinted lane, so it can't push an existing branch sideways. A fresh open gets a compact layout.

**Performance budget** (checked with `perch bench-graph <repo>` on the user's big repos):

| Stage | Budget |
|---|---|
| First paint (first page of 2,000 commits) | < 300 ms after the window opens |
| Lane layout | < 150 ms per 100k commits |
| Paint | < 4 ms per frame, no heavy allocations (cached `FormattedText`, `OverlayDraw.Pen`/`Brush`) |
| Memory | < 40 MB for 100k commits |

## Milestones

### M1: Graph read model (Core)

- **CP1: models + log/ref reader.** `GraphCommit`/`GraphRef`/`GraphEdge`/`GraphRow`. `GraphLogReader.ParseLog` over `git log --topo-order -z --format=%H%x1f%P%x1f%an%x1f%ae%x1f%at%x1f%s`, `ParseRefs` over `for-each-ref` (local, remote, tags with annotated tags peeled, `HEAD`), and the loader through `GitRunner` (Automatic trust). Unit tests on canned output.
- **CP2: lane allocator.** Incremental `LaneAllocator` (add commits page by page), primary lane 0, convergence/merge/octopus/root/missing-parent handling, hint-driven stability. Unit tests covering linear, branch, merge, octopus, criss-cross, paging equivalence (one batch vs many pages give the same rows), stability (new commit on a side branch, new tip on top, keep every existing lane), and a 100k-commit synthetic run for correctness.
- **CP3: graph view + bench.** `GraphView` builds the row list for a scope (All / Worktrees / This branch), adds a WIP row per dirty worktree tethered to its HEAD, marks worktree HEADs, and attaches refs. `perch bench-graph <repo> [count]` prints load, parse, layout and memory. Streaming log read (`GitRunner` streaming variant) if the bench shows the one-shot read is the bottleneck on the big repos.

### M2: Graph canvas in the Tree window

- **CP4: `GraphCanvas`.** Owner-drawn, row-virtualised: lanes, nodes (merge, worktree-HEAD ring, WIP dashed node), ref pills, subject, author/when columns, selection, hover, keyboard (↑ ↓ PgUp PgDn Home End). `HeadlessRenderer` entry with a synthetic repo. Lane colours via `Palette` (temporary fixed set; real roles in CP6).
- **CP5: replace the node list.** `GitTreeWindow`'s left pane becomes the canvas; scope segment; selecting a row drives the existing files + `DiffView` + WIP composer. "This branch" reproduces today's view. Retire `NodeRow`/the node `ListBox`.
- **CP6: navigation + theming.** Rail (worktrees, local, remotes, stashes, tags) with click-to-jump and flash; hover a worktree/branch to highlight its lane; Ctrl+G go-to ref/hash; "Load older" paging. Lane hues get a contrast test. They live in the Tree window's own palette (`GraphCanvas.Colors`, built on `AuroraPalette` like `TreePalette`), **not** in app theme roles: the Tree is a reading window that deliberately keeps its own light/dark, independent of the app theme (see `AuroraPalette`).

### M3: Worktrees live

- **CP7: worktree status + sessions.** Per-worktree status off the UI thread; worktree cards with branch, dirty count, ahead/behind and the session working there (from the `SessionMonitor` snapshot, matched by cwd and `SessionWorktree`). WIP rows show the session pill.
- **CP8: live refresh.** Watch the common dir (`refs`, `packed-refs`, `HEAD`, `worktrees/*/HEAD`) and each worktree's index, debounced through `RepoWatchFilter`; re-layout with hints; keep selection and scroll. Watch only while the window is open.
- **CP9: entry points + retarget.** Session row (existing), Roost pane header, `perch tree [path]` CLI (`CliUsage`), and retargeting the window to another worktree (double-click a card or WIP row).

### M4: Checkout

- **CP10: `GitOps` write side.** `switch`, `switch --detach`, `switch --merge`, `stash push/pop`, run with UserAction trust in a named worktree; one op queue per repo; `index.lock` detection with a readable retry message; reflog-based undo for the last op. `GitRepoServiceIntegrationTests`-style round-trips against a throwaway repo.
- **CP11: double-click.** Branch → check out here; branch held by another worktree → popover (open that worktree / focus its session / detached copy); commit → detached or "create branch here"; dirty tree → stash / bring along / cancel; working session → warning. Toast with Undo.

### M5: Drag

- **CP12: drag mechanics.** Pointer-capture drag inside the canvas and rail: ghost, hint text, drop-target highlight, auto-scroll near edges, Esc cancels. Sources: ref pills, rail branches, commit rows. Targets: pills, rail branches, worktree cards, WIP rows, the New worktree zone.
- **CP13: drop menu + operations.** Merge (in the target's worktree), rebase (in the source's), fast-forward (only when possible), cherry-pick (in the branch's worktree), open a PR (`gh`). Session warning + "Ask the session". In-progress banner (merge/rebase/cherry-pick) with continue/abort/skip and the conflict list ("Open in editor", "Ask the session").
- **CP14: worktrees from drag.** Drop on New worktree → `git worktree add` with `WorktreeLayout` naming, new branch when the branch is held elsewhere, optional "Start a Claude session there". Remove/prune from the card menu (refuses a dirty tree without confirmation).

### M6: Remotes & PRs

- **CP15: fetch / pull / push.** On click and on open, off the UI thread; ahead/behind on pills and cards; create a tracking branch by double-clicking a remote branch; push never forces from a gesture.
- **CP16: PR badges.** `PrStatusService` state on branch pills (open/draft/approved/CI).
- **CP17: right-click menus.** Rename, delete, reset (soft/mixed/hard with confirmation), revert, tag, copy hash/name.

### Deferred

- **D1: three-way merge tool.** Its own design pass after M5 dogfooding.
- **D2: interactive rebase.** Reorder/squash/fixup/drop by dragging rows on the same canvas; plan computed in Core and applied with `GIT_SEQUENCE_EDITOR`; budgets as above at 1,000+ commits. Before building, measure where GitKraken's version slows down on the user's repos.
- **D3: commit ↔ session attribution** ("Made in" column).
- **D4: drag a branch onto a Roost pane** (real `DragDrop` with a custom format).

## Status

| CP | State |
|---|---|
| CP1 | ✅ built, tests green |
| CP2 | ✅ built, tests green |
| CP3 | ✅ built, tests green; big-repo bench numbers owed |
| CP4 | ✅ built, render-verified |
| CP5 | ✅ built, render-verified; never run live |
| CP6 | ✅ built; live window rendered headless on this repo |
| CP7 | ✅ built, render-verified |
| CP8 | ✅ built, tests green |
| CP9 | partly: retarget by double-click done; `perch tree` + Roost entry moved after M5 |
| CP10 | ✅ built, integration-tested |
| CP11 | ✅ built, integration-tested, render-verified (toast + branch bar); never run live |
| CP12 | ✅ built; a synthetic pointer drag runs in `render` (ghost, highlight, New worktree zone, drop menu) |
| CP13 | ✅ built, integration-tested (merge, rebase, fast-forward + Undo); conflict banner render-verified; PR from drag not built |
| CP14 | ✅ built, integration-tested (worktree add from branch / new branch + Undo, remove refuses dirty) |
| **Ready for user testing** | CP11–CP14 checks in `docs/git-graph-testing.md` |

## As built

- **CP1** (`Perch.Core/Data/GitGraph/GraphModels.cs`, `GraphLogReader.cs`). `GraphCommit` is a class (shared by reference across 100k rows); parent hash strings are interned to the parent's own `Hash` while parsing. `LoadLog(cwd, maxCount, skip)` walks `--branches --remotes --tags HEAD` in `--topo-order`; paging is `--skip`, which re-walks from the tips each page, so CP3's bench decides whether a streaming read replaces it. `LoadRefs` drops `origin/HEAD` and peels annotated tags. Other worktrees' detached HEADs aren't in the walk yet (CP3). Tests: `GitGraphTests` (parsers), `GitGraphIntegrationTests` (real throwaway repo: topo order, tag peeling, paging).
- **CP2** (`LaneAllocator.cs`). Incremental and resumable; rows store their node lane plus the edges in the band above. Primary chain pinned to lane 0 by following first parents from `PrimaryTip` as commits arrive. Hints: `HintsFrom(previous)` gives each commit's lane; a new tip looks up to 64 first-parent steps down the current page for a hinted commit; unhinted lanes open after the highest hinted lane. Paging gives identical rows without hints; with hints the lookahead only sees the current page. A 100k-commit synthetic history lays out inside the test run's time (whole graph suite < 1 s); the real budget is measured in CP3.
- **CP3** (`GraphSnapshot.cs`, `GraphLoader.cs`, `Perch.App/Rendering/GraphBench.cs`). Each dirty worktree becomes a **WIP pseudo-commit** (`wip:<path>`, parent = its HEAD) fed to the allocator ahead of the log, so its tether owns a lane down to its HEAD and can't overlap another branch's line; `GraphSnapshot.IsTether(row, edge)` tells the painter which segments to dash. The primary branch's chain starts from its worktree's WIP row when that's dirty. Snapshots are immutable for the UI; `Append` hands the allocator to a new snapshot (once). Scopes: **All** = `--branches --remotes --tags HEAD` + detached worktree heads; **Worktrees** = each worktree's `refs/heads/<branch>` (or detached hash) + `--branches=<trunk>` patterns; **Branch** = `HEAD` + best base ref `--not <merge-base>^@` (just `HEAD` on a trunk). Opening runs refs, every worktree's `git status`, and the log **in parallel** (the query names branches, not hashes, so nothing waits): on this repo open went 490 → 323 ms and a refresh 323 → 148 ms. Process start (~100 ms each here) dominates, not parse/layout. `perch bench-graph [repo] [count]` reports open, refresh (and how many commits changed lane), full history (git / parse / layout / memory) and `--skip` paging depth cost.
- **CP4** (`Perch.App/Views/GraphCanvas.cs`). Self-scrolling (own offset, wheel, keys; `GraphPane` adds a synced `ScrollBar`), paints only visible rows; curves are 8 short line runs (no geometry objects), pens/brushes built once per `Colors`, text via `OverlayDraw`'s cache. Lane colours: 8 per polarity on top of `AuroraPalette`, colour = lane index (a reused lane reuses its colour, as GitKraken does). Pills right-aligned against the graph: current worktree's branch solid, other locals tinted, remote-only dashed, "+N" overflow; a branch checked out in any worktree gets a drawn folder mark, an upstream at the same commit a small circle (Inter lacks ▣/⇅/◇, so marks are drawn, not glyphs). Below 760 px the author column is dropped. Pill hit-rects are recorded for CP11/CP12. Found and fixed while rendering: a merge whose second parent is on the primary chain now opens lane 0 rather than joining another branch's lane that was also waiting for it (`Merge_FromThePrimaryBranchHeadsForLaneZero`).
- **CP5** (`GitTreeWindow`). The node `ListBox`, `NodeRow`/`Knot`/`LaneSegment`, the terminal base node and the ghosted "Expand to full tree" button are gone; `TreeNode` is now just (Kind, IsHead, Commit, ChangeCount). The graph row drives the existing files/diff/composer code: this worktree's WIP row → `TreeNode.Wip`; a commit row → `TreeNode.Commit`; another worktree's WIP row → a hint, and **double-click retargets the window to that worktree** (an early piece of CP9). Refresh, the staging fast path and the watcher's auto-refresh all go through `GraphLoader.Load` (with the previous snapshot as lane hints); the auto-refresh compares rows/refs/worktrees (`SameGraph`) and keeps the shown diff when nothing moved. The loader now returns a `ScopeContext` (current worktree's full status, base ref and candidates) so the window doesn't run status twice. Scope persisted as `AppSettings.GitTreeScope` (in `SettingsRegistryTests.NotSettings`, like the window's other toggles). Graph pane is now the widest column (1.4*, min 420).
- **CP6** (`Views/GraphRail.cs`, `GraphLanes.cs`, `GraphSnapshot.Find`/`LaneOf`). The rail sits inside the graph card (210 px): worktree cards (this window's first, tinted in its lane colour; branch, change count, and a session line once CP7 feeds it), local branches, remotes grouped by remote (open), tags (collapsed, ordered by graph position), stashes. Click jumps the graph (`GraphCanvas.Flash`: select, centre, a 0.9 s wash, skipped under reduced motion); hover fades every other lane (`HighlightLane`); double-click a card retargets the window, double-click a branch raises `BranchActivated` for CP11. Every card/row carries a `RailItem` tag for CP12's drop hit-testing. **Ctrl+G** opens a go-to bar: exact ref name, then hash prefix (4+ hex), then subject/author text, Enter walks matches. Lane colours moved to Core (`GraphLanes`) and `GraphLaneContrastTests` holds them to 3:1 on the Aurora pane in both polarities (all pass) and distinct. The render harness now also opens the **real Tree window on the repo it runs in** and captures it after its background loads (`git_tree_live_1x.png`): this exercises the CP5/CP6 load and selection path end to end without a desktop (no pointer input).
- **CP7.** The App passes each scan's sessions to the window (`SetSessions`, also on creation). `GraphSnapshot.WorktreeContaining(dir)` places a session by its resolved worktree (`ClaudeSession.Worktree`, else cwd): the deepest worktree folder containing it, string-compared, no IO. Per worktree the most urgent session wins (Needs you > API error > Running > Done > Idle). Shown on the rail card (dot, name, state) and as a pill on the WIP row. The commit guard (`_isActive`) is now "opened from a working session OR a working session is placed in this worktree", so it survives a retarget.
- **CP8.** A second watcher on the repo's shared git dir (`RepoWatchFilter.IsRelevantInGitDir`: HEAD, index, packed-refs, refs/, each linked worktree's folder/HEAD/index, merge/rebase/cherry-pick markers) so another worktree's commits, branch moves and staging refresh the graph. Unstaged edits in other worktrees aren't watched (that would mean a recursive watcher per worktree); instead, while a session is **Running** in another worktree, a 15 s poll refreshes. The tests caught a real bug in the first cut: `Split('/', '\\', options)` binds to the `(char, int count, options)` overload, so backslash paths never split.
- **CP10** (`GitGraph/GitOps.cs`). `Switch`, `SwitchMerge`, `SwitchDetach`, `SwitchTrack`, `CreateBranch`, `StashPush` (`-u`, labelled), `Merge` (`--no-edit`), `FastForward` (`--ff-only`), `Rebase`, `CherryPick` (`-n` optional), `Continue`/`Abort` (with `-c core.editor=true` so continue never opens an editor), `WorktreeAdd` (existing branch or `-b new start`), `Head`/`Restore` (for Undo), `InProgress` (marker files in the worktree's own git dir via `GraphLoader.GitDirOf`). All return `GitOpResult(Ok, Message, Command)` + `Conflicted`; never throw. UserAction trust; one `SemaphoreSlim` per common git dir; a held `index.lock` gets one 700 ms retry, then "Another git process is using <worktree>…". Values starting with `-` are refused before git runs (`Refuse`). `FirstLine` drops `hint:` lines and `error:`/`fatal:` prefixes. 9 new integration tests (switch/restore, branch held by another worktree, stash, merge conflict → InProgress → abort, cherry-pick + ff-only both ways, worktree add on a new branch, option refusal).
- **CP11** (`GitGraph/GraphCheckout.cs`, `GitOps.Checkout`/`Undo`, `Windows/GitTreeWindow.Checkout.cs`, the window is now `partial`). What a double-click means is decided in Core and unit-tested: `GraphCheckout.ForRef` (local → Switch / AlreadyHere / HeldElsewhere; remote → Track, or the same-named local if one exists; tag → Detach; stash → nothing) and `ForCommit` (this worktree's branch at that commit, else one no other worktree holds, else a remote to track, else the bare commit). `GitOps.Checkout(worktree, action, target, DirtyChoice, newBranch)` records HEAD, stashes when asked (remembering that entry's commit via `StashTop`), switches (`--merge` to carry, `--track`, `--detach`, or `switch -c` for a new branch) and **pops the stash straight back if the switch fails**; `GitOps.Undo` restores HEAD (carrying changes back when they were carried) then pops **that exact stash by commit**, wherever it now sits in the list (`StashPop(worktree, hash)`). Canvas: a double-click on a pill raises `RefActivated` instead of `RowActivated`. Window: a free branch on a clean tree with no working session checks out immediately; held-elsewhere and detach plans ask first (menu at the pointer with an italic note line saying why); a dirty tree asks stash / bring along / cancel; a working session adds a warning line (warn, never block). New branch → an inline bar under the graph title (like Ctrl+G's). Outcomes go to a toast over the bottom of the graph (message, the git command in mono, Undo, ✕; 6 s, 10 s and red for errors, held open under the pointer; a busy toast while git runs). Render: `git_tree_checkout_1x.png` shows the toast and branch bar on the live window. 4 new integration tests (stash+switch+undo with another stash pushed on top, failed switch puts the stash back, carry there and back, detach and new branch), 4 planner unit tests. Not done: "focus its session" for a held branch (needs the session's window; with Roost entry points after M5).
- **CP12** (`GraphCanvas` press/threshold/`DropTargetAt`/`SetDropHighlight`/`AutoScroll`, `GraphRail` `TrackPress`/`Draggable`/`HighlightDrop`/`SetDragging`, `Windows/GitTreeWindow.Drag.cs`). Canvas and rail each detect press + 6 px and raise `DragRequested(DragSource, IPointer)`; the window then **captures the pointer on the canvas** for the rest of the gesture (the rail rebuilds itself to show the zone, which would drop a row's capture, and captured input keeps rail hover handlers from fighting the highlight) and follows it with window-level **tunnel** handlers (`PointerMoved`/`PointerReleased`, `handledEventsToo`), so it sees every move wherever the pointer is. Targets: the canvas answers for itself (pill, else WIP row → worktree, else the row's local branch, else the worktree whose HEAD it is); elsewhere `InputHitTest` + walk up to a control tagged `RailItem` (cards, branch rows, the "+ New worktree" zone shown only while dragging). The ghost is a `Border` on a hit-test-invisible `Canvas` layer over the whole graph card; its hint is the first option of the drop menu, recomputed only when the target changes. Auto-scroll on a 30 ms timer near the canvas edges. Esc / release over nothing / button already up cancels. Render: `render` now drives a **real drag with synthetic pointer input** (`HeadlessWindowExtensions.MouseDown/Move/Up`) on the live window and prints the hints: `git_tree_drag_pill_1x.png`, `git_tree_drag_newworktree_1x.png`, `git_tree_drop_menu_1x.png`. So pointer input *can* be exercised headlessly after all.
- **CP13** (`GitGraph/GraphDrop.cs`, `GitOps.RunDrop`/`Skip`). `GraphDrop.Menu(snap, source, target, layout, mainRoot)` is pure and unit-tested: branch→local branch = fast-forward when the target is behind (`IsAncestor` over the loaded rows; topo order means only the rows between the two need walking, and nothing past the loaded prefix can lead back in), "already contains" when it's ahead, else merge (in the target's worktree) + rebase (local sources only, in the source's worktree); local→remote = rebase onto / fast-forward to; commit→branch = cherry-pick; anything→tag/stash = a note. An op on a branch no worktree has runs in this worktree with `SwitchFirst` (greyed out while this tree is dirty); a fast-forward of such a branch is `git fetch . rev:branch` instead (atomic, refuses non-ff). `RunDrop` records the branch's old hash and returns an **undo closure**: `reset --keep <old>` in that worktree (refuses rather than lose changes; checks the branch is still checked out there), then switch back if it switched first; `update-ref <branch> <old> <new>` for the fetch fast-forward. A failure that didn't conflict and had switched first switches back. The menu: one item per option with the git command in mono under it (long ones trimmed from the left), a warning line per working session where an option runs. Conflicts: `ScopeContext.InProgress` (from `GitOps.InProgress`, loaded with the snapshot so both refresh paths carry it) drives an orange banner under the graph title with the conflicted-file count and Continue / Skip commit / Abort; a conflict in another worktree gives a toast with "Open <worktree>". The toast grew a second action button. Not built: **open a PR from a drag** (`gh`); CP16/M6 is the natural home.
- **CP14** (`GraphDrop` new-worktree arm, `GitOps.RunDrop` → `AddWorktree`, `WorktreeRemove`/`WorktreePrune`, `PrWorktree.EnsureExcluded` extracted). Path: the repo's **Detected** layout (worktrees the user already made), else **`{parent}/{repo}--{name}`** beside the main worktree (Q9); an ignore-file hint doesn't count, since Claude Code writes `.claude/worktrees` itself. Rendered against the **main** worktree (`PrWorktree.Inspect` → `Set.Root`, loaded per repo off-thread), not the snapshot's root, which is the window's own and may be linked. Free local branch → `worktree add path branch`; remote with no local → `--track -b name`; held branch or commit → the branch bar asks for a name. Undo removes the worktree and deletes a branch it created with `update-ref -d` against the hash it was made at (so a branch that has moved since survives). Toast offers "Start a session there" (`StartSessionRequested` → App opens a `SessionWindow` and `StartNew(path)`). Card right-click: open here, start a session, remove (asks when dirty or a session works there, forces only then; hidden for this window's worktree and the main one), prune. Integration tests: worktree for a branch + undo, worktree on a named branch from a commit + undo deletes the branch, remove refuses a dirty tree unless forced.

## Feedback round 1 (2026-10-30)

The user tried CP11–CP14 and asked for four things; all four are built (427ff68 for the first three, the commit after it for hiding):

- **Diff over the graph** (GitKraken's layout). The window is graph | files; `_diffOverlay` covers the graph card when a file is clicked (`OpensDiff`: `Tapped`/Enter on the file lists, ignoring the row's stage button), with a bar (`abc1234 · path`) and ✕; Esc closes it. Selecting a commit only fills the file list. A session's "View diff" (`Retarget` with a focus path) opens straight onto that file via `_openDiffWhenShown`, since a commit's files load asynchronously. The "Stage lines" button moved into the diff bar.
- **Steady header.** The scope segment and base picker live in the graph card's title row; the header text fills and trims (branch/status keep their start, the path keeps its folder name).
- **Pills and columns.** The first pill always gets one, cut to the room there is (the bug: the checked-out branch's folder mark pushed a 140 px name past the column and it fell into "+N"). Names are cut in the middle by `TextFit.ElideMiddle` (first path segment + … + as much of the end as fits; tested), with the full name as a tooltip. The pills|lanes and lanes|subject edges drag (↔ cursor, a line while hovered), double-click resets, widths persist (`GitTreeRefsWidth`/`GitTreeLanesWidth`, window prefs in `NotSettings`).
- **Hiding branches** (`GitGraph/BranchVisibility.cs`, `Windows/GitTreeWindow.Visibility.cs`). Per repo in `AppSettings.GitTreeRepos` keyed by the shared git dir (`GraphRepoPrefs`: `Hidden` full ref names, `Seen` remote refs). The loader turns `Hidden` into `--exclude=<name>` before the matching `--branches`/`--remotes` (All scope only), and the snapshot drops hidden refs from `RefsAt` (no pills) while `Refs` still lists them for the rail. Rail: an eye on hover (struck and always shown when hidden), "N · k hidden" in the header, a ⋯ menu with the auto-hide toggle and "Show all hidden". Pill right-click: check out / hide. Trunk and checked-out branches can't be hidden. **Auto-hide** (`GitTreeAutoHideOthers`): after each load, remote branches not in `Seen` are checked with **one** `git log --format=%H␟%P␟%ae␟%an <new branches> --not <trunk refs>`; a branch is the user's if a walk from its tip through those commits meets one authored with `user.email` or `user.name` (no identity → nothing hidden). Turning it on (or its first look at a repo) seeds `Seen` with what's there, so existing branches never vanish. Hidden ones get a toast with Undo. Tests: query building, `Unseen`/`SeedSeen`/`TrunkRefs`, `MineFrom`, and a real-repo walk + identity check.

## Feedback round 2 (2026-10-30)

- **Placement** (`PlacementMath.BesideOverlay`, tested; `App.PlaceBesideOverlay`): the overlay's monitor, live OS work area, the wider strip beside the overlay when it's at least the window's min width, shrunk to fit and centred.
- **Remote marks**: `for-each-ref` now also reads `%(upstream:short)` and `%(upstream:track,nobracket)` (same call); `GraphRef.Upstream/Ahead/Behind/UpstreamGone/InSync`. Pill trail: ● / ↑n ↓m / ⊘ / nothing; `GraphCanvas.Describe` is the tooltip for pills and rail rows. A remote pill is suppressed when a local on the same row tracks it.
- **Find** (`GraphSnapshot.FindAll`, tested; `GitTreeWindow.Find.cs`): Ctrl+F routes to the diff when it's open, else the commit search (the old Ctrl+G bar). Matches washed via `GraphCanvas.SetMatches`; "Search older history" loops `GraphLoader.LoadMore` until a match.
- **Menus** (`GitTreeWindow.Menus.cs`): right-click commit row / pill / rail branch. Row double-click no longer checks out (detail view deferred). `GitOps.DeleteBranch` + `IsUnmerged`, tested; Undo = `branch <name> <hash>`. "Create a worktree" reuses the New-worktree drop path.
- **Column header** in the canvas (`_headerH`; rows offset by it through `RowTop`/`RowAt`/`Viewport`), grips on both draggable edges.
- **Busy cover** (`SetGraphBusy` / `ClearBusyOnNextLoad` / `OnGraphLoaded`, 20 s fallback) over the graph card for checkouts, drops and their undo.
- **WIP rows** lose their pill; **files pane** collapses to a strip (`GitTreeFilesCollapsed`); **naming**: "Git graph…" first in the row menu group, window title "Git graph".

## Feedback round 3 (2026-10-30)

- **File menu** (`GitTreeWindow.Files.cs`): right-click a WIP row (`ContextRequested` on the row). Changes: stage, discard (`DiscardFile`, the diff button's path), delete. Staged: unstage, discard both sides (`GitRepoService.DiscardFileEntirely`: `restore --source=HEAD --staged --worktree`, with the rename's old path so it comes back), delete. Delete = `GitRepoService.DeleteFile`: `git rm -f -r` for anything the index has (a staged deletion), a plain disk delete for untracked files/folders, refusing a path that resolves outside the worktree. Open / reveal / copy path. Both destructive ones confirm; neither has Undo.
- **Toolbar** (`GitTreeWindow.Actions.cs`), right of the scope segment: Pull ▾ / Push / Branch / Stash / Pop, as one `Segment`. `GitOps.Pull(worktree, PullMode)` (FetchAll / FastForwardOnly / Merge / Rebase; the last choice is `AppSettings.GitTreePullMode`, in `NotSettings`) returns an undo (`reset --keep` to the old tip) when the branch moved. `GitOps.Push` pushes to the upstream, else `push -u origin|<only remote> <branch>`; never forces. Network ops go through `RunNetwork`, which redirects and closes stdin, so a terminal credential prompt fails instead of hanging. Stash = `StashPush` + Undo by commit; Pop = `StashPopTop`. Branch = the branch bar → `RunCheckout` with a new branch at `HEAD` (no dirty-tree question: it changes no files). Counts (↑/↓, stash count) and enabled state come from `_lastStatus` and `_snap` in `UpdateActionBar`, run from `ShowSnapshot`.
- **Menu overflow**: Fluent caps a menu flyout at 456 px and a `MenuItem` header doesn't wrap, so `Item`/`Note` (and the drop menu's label) now use `MenuText`: a wrapping `TextBlock` capped at `MenuTextWidth` (400). The busy card's text wraps too.
- Tests: discard-entirely (modified both sides, added, renamed), delete (tracked → staged `D`, untracked folder, outside path refused), stash + pop, and push/pull against a bare remote (ff pull + undo, fetch, publish with `-u`, then plain push).

## Handover (2026-10-30, end of session 2)

Branch `git-graph`, **not pushed**. Session 2 added CP11 (8abd0c5) and CP12–CP14 (the commit after it). The agreed stopping point (double-click checkout + drag working, then a test script) is reached: **the user now tests `docs/git-graph-testing.md` CP11 and CP12–CP14 sections** in a throwaway clone. Don't start M6 before their feedback unless asked.

**State.** Everything through CP14 is built. The Core decisions (`GraphCheckout`, `GraphDrop`) are unit-tested and every git operation with its Undo is integration-tested against a throwaway repo. `render` drives a real synthetic-pointer drag on the live Tree window, so pointer paths *can* be checked headlessly (`HeadlessWindowExtensions.MouseDown/MouseMove/MouseUp` with `RawInputModifiers.LeftMouseButton` held on the moves). Nothing has run on a real desktop.

**Next, after the user's feedback.** Fix what they find; then CP9's leftovers (`perch tree [path]`, Roost pane entry), "focus its session" for a held branch, open a PR from a drag (`gh pr create --web`, see the gh child-process gotchas in memory), then M6 (fetch/pull/push, PR badges, right-click menus).

**Gotchas learned in session 2.**
- `GraphSnapshot.Root` is the **window's own worktree** root, not the main one. Anything that needs the main worktree (new worktree paths) uses `PrWorktree.Inspect(...).Set.Root`.
- A rail rebuild mid-gesture drops the pressed row's pointer capture; the window re-captures on the canvas at drag start.
- Stash round trips under `core.autocrlf` rewrite line endings, so tests compare trimmed text.
- An ancestor walk over the loaded rows is exact: they're a prefix of a topological order, so nothing unloaded leads back into them.

## Session 1 handover (kept for the CP11–CP14 design notes)

**CP11: double-click to check out.** Everything it needs exists:
- Canvas: `RowActivated(row)` fires on double-click/Enter. Add a `RefActivated(GraphRef, row)` raised from `OnPointerPressed` when `ClickCount == 2 && RefAt(pos) is { } r` (pill hit-rects are already recorded in `_pillHits`), and handle that before `RowActivated`.
- Rail: `BranchActivated(GraphRef)` is already raised on double-click; the window doesn't subscribe yet.
- Window (`GitTreeWindow.OnGraphActivated`, currently only retargets another worktree's WIP row): branch → if `_snap.Worktrees` has another worktree on that branch, a `MenuFlyout` at the pointer offering *Open that worktree here* (`Retarget`), *Check out a detached copy here* (`GitOps.SwitchDetach`); else if this worktree is dirty (`_lastStatus`), offer *Stash N changes and switch* (`StashPush` then `Switch`), *Bring changes along* (`SwitchMerge`), *Cancel*; if a session is working here (`_isActive`), say so in the menu (the agreed guard is a warning, not a block). Remote branch → `SwitchTrack`. Commit row with no local branch → *Check out detached* / *Create branch here…* (a small name box). Run ops with `Task.Run`, then `RefreshStatus(preserveSelection: false)`.
- Needs a **toast** in the window (bottom of the graph card): message + the command in mono + **Undo** (`GitOps.Head` before the op, `GitOps.Restore` on Undo), auto-hide ~6 s. CP13 reuses it.
- Add CP11 checks to `docs/git-graph-testing.md`.

**CP12–CP14 (then): drag.** Use hand-rolled pointer capture as Roost does (no OS DragDrop): press on a pill or row in the canvas, move > 6 px → drag; the canvas draws its own ghost and highlights the pill/row under the pointer; for targets outside the canvas, the window hit-tests (`InputHitTest`) and walks up to a control whose `Tag` is a `GraphRail.RailItem` (worktree card or branch row; the rail already tags them). Add a "New worktree" drop zone at the end of the rail's worktree list. Drop opens a `MenuFlyout` per the design's table: branch→branch merge (in the target's worktree) / rebase (in the source's) / fast-forward (only when `isAncestor`; compute from `_snap` parents) ; commit→branch cherry-pick in the branch's worktree; branch→card switch that worktree; →New worktree `GitOps.WorktreeAdd` with `WorktreeLayout` naming (look at `PrSetup`/`PrWorktree` for how Perch already creates `..\repo--branch`). Every menu names the worktree and command first. After an op that stops on conflicts, show an in-progress banner from `GitOps.InProgress` with Continue/Abort.

**Gotchas learned this session.**
- Avalonia **12**. Templated controls (ScrollViewer etc.) render blank through `RenderControl`; put them in a shown headless window and `CaptureRenderedFrame` (see the live Tree window block in `HeadlessRenderer`).
- Inter has no ▣ ⇅ ◇, so owner-drawn marks are drawn shapes.
- `string.Split(char, char, options)` silently binds to the `(char, int, options)` overload. Use `Split(['/', '\\'], …)`.
- Lane colours live in Core `GraphLanes` (tested), not app theme roles: the Tree window keeps its own Aurora palette.
- Commit messages: write to a file and `git commit -F`.
- `perch bench-graph` output: run the built exe with stdout redirected to a file.

**Owed from the user** (not blocking): bench numbers from their big repos; the CP4–CP8 desktop checks; now the CP11–CP14 checks.

## Owed live checks

(Things only a real desktop or the user's big repos can confirm.)

- [ ] **CP3:** `perch bench-graph` on each of the user's big repos (see `docs/git-graph-testing.md`). Decides whether `--skip` paging is replaced by a streaming read, and whether the open budget holds.
- [ ] **CP4–CP5:** the Tree window on a real desktop, checks 1–8 in `docs/git-graph-testing.md`, including on a big repo (scroll feel, paging, refresh while a session commits).
- [ ] **CP11–CP14:** double-click checkout and drag on a real desktop, `docs/git-graph-testing.md` CP11 (1–10) and CP12–CP14 (1–16), in a throwaway clone: drag feel, menu placement at the pointer, toast/Undo, conflict banner, worktree add/remove, "Start a session there".
- [ ] Pre-existing, noticed in passing: the commit files list trims paths with a trailing ellipsis (`CommitFileTemplate`), against the path-trimming convention. Fix alongside CP6's rail.
