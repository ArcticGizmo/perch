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
| CP4 | next |

## As built

- **CP1** (`Perch.Core/Data/GitGraph/GraphModels.cs`, `GraphLogReader.cs`). `GraphCommit` is a class (shared by reference across 100k rows); parent hash strings are interned to the parent's own `Hash` while parsing. `LoadLog(cwd, maxCount, skip)` walks `--branches --remotes --tags HEAD` in `--topo-order`; paging is `--skip`, which re-walks from the tips each page, so CP3's bench decides whether a streaming read replaces it. `LoadRefs` drops `origin/HEAD` and peels annotated tags. Other worktrees' detached HEADs aren't in the walk yet (CP3). Tests: `GitGraphTests` (parsers), `GitGraphIntegrationTests` (real throwaway repo: topo order, tag peeling, paging).
- **CP2** (`LaneAllocator.cs`). Incremental and resumable; rows store their node lane plus the edges in the band above. Primary chain pinned to lane 0 by following first parents from `PrimaryTip` as commits arrive. Hints: `HintsFrom(previous)` gives each commit's lane; a new tip looks up to 64 first-parent steps down the current page for a hinted commit; unhinted lanes open after the highest hinted lane. Paging gives identical rows without hints; with hints the lookahead only sees the current page. A 100k-commit synthetic history lays out inside the test run's time (whole graph suite < 1 s); the real budget is measured in CP3.
- **CP3** (`GraphSnapshot.cs`, `GraphLoader.cs`, `Perch.App/Rendering/GraphBench.cs`). Each dirty worktree becomes a **WIP pseudo-commit** (`wip:<path>`, parent = its HEAD) fed to the allocator ahead of the log, so its tether owns a lane down to its HEAD and can't overlap another branch's line; `GraphSnapshot.IsTether(row, edge)` tells the painter which segments to dash. The primary branch's chain starts from its worktree's WIP row when that's dirty. Snapshots are immutable for the UI; `Append` hands the allocator to a new snapshot (once). Scopes: **All** = `--branches --remotes --tags HEAD` + detached worktree heads; **Worktrees** = each worktree's `refs/heads/<branch>` (or detached hash) + `--branches=<trunk>` patterns; **Branch** = `HEAD` + best base ref `--not <merge-base>^@` (just `HEAD` on a trunk). Opening runs refs, every worktree's `git status`, and the log **in parallel** (the query names branches, not hashes, so nothing waits): on this repo open went 490 → 323 ms and a refresh 323 → 148 ms. Process start (~100 ms each here) dominates, not parse/layout. `perch bench-graph [repo] [count]` reports open, refresh (and how many commits changed lane), full history (git / parse / layout / memory) and `--skip` paging depth cost.

## Owed live checks

(Things only a real desktop or the user's big repos can confirm.)

- [ ] **CP3:** `perch bench-graph` on each of the user's big repos (see `docs/git-graph-testing.md`). Decides whether `--skip` paging is replaced by a streaming read, and whether the open budget holds.
