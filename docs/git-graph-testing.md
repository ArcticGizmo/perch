# Git graph: manual testing

Checks for the git graph work (`docs/git-graph-plan.md`), one section per checkpoint that changes something you can see or run. Each lists what to do, what you should see now, and what you'd have seen before.

## CP3: `perch bench-graph`

Measures the graph's read and layout on a real repository. Run it on the big repos you have checked out.

```
perch bench-graph <path-to-repo> [count]
```

`count` caps the full-history read (default 100,000). From a dev build: `src\Perch.App\bin\Debug\net10.0-windows10.0.19041.0\perch.exe bench-graph <repo>`. In PowerShell, pipe it (`| Out-String`) or redirect it (`> bench.txt`) if the output lands after the prompt.

**Expect** four sections:

1. **Open**: what the Tree window will pay on open: `scope`, then `git: refs, status, log` (run in parallel), `parse`, `layout`, `total`, then rows / lanes / worktrees (and how many are dirty) / refs.
2. **Refresh**: the same with lane hints from the open, and `0 commits changed lane` unless the repo changed between the two reads.
3. **Full history**: `git log`, MB of output, `parse`, `layout`, then commits, lanes, edges and the memory held, then `layout with hints`.
4. **Paging** (only past 2,000 commits): the cost of reading a page at three depths with `--skip`.

**Please send back** the output for each big repo. The budgets it's measured against are in the plan (open < 300 ms, layout < 150 ms per 100k commits, < 40 MB per 100k). If the paging lines grow with depth, CP3's follow-up is a streaming log read instead of `--skip`.

**Before:** `perch bench-graph` didn't exist (`perch --help` now lists it under Diagnostics).

## CP4–CP5: the graph in the Tree window

Open the Tree window as before: right-click a session row on the overlay → **View tree…**. Best tried in a repo with a few branches and at least one extra worktree (`git worktree add ..\<repo>--try <branch>`) with an uncommitted change in it.

1. **Graph instead of a list.** The left pane is titled **Graph · all branches** and shows every branch as coloured lanes with merge curves, ref pills right-aligned against the graph (the branch checked out in this window's worktree is a solid pill; branches checked out in any worktree carry a small folder mark; a small circle at the end means the remote branch is at the same commit), tags at the right of the subject, author and age. *Before:* a single-lane list of this branch's commits with a terminal base marker.
2. **WIP rows per worktree.** Each worktree with uncommitted changes has a row at the top ("N uncommitted changes", dashed node, a dashed pill with the worktree's folder name) and a dashed line down its own lane to the commit it's on. A clean worktree adds no row; its HEAD commit has a ring (thicker for this window's worktree). *Before:* only this worktree's WIP node.
3. **Opening lands where you are.** The window selects this worktree's WIP row if it's dirty (Changes / Staged groups and the composer appear as before), otherwise its HEAD commit.
4. **Selecting rows.** Click or ↑/↓/PgUp/PgDn/Home/End: a commit shows its files and diff, as before; this worktree's WIP row shows staging and the composer. Another worktree's WIP row shows "N uncommitted in <name>. Double-click to open that worktree here." **Double-click it** and the window re-points at that worktree (title changes; its WIP row now drives the staging panes).
5. **Scope segment** in the header: **All / Worktrees / This branch**. *Worktrees* shows only the main branch and what each worktree has checked out. *This branch* is the old view (the base picker "since ⑂ origin/main" appears only in this scope). The choice is remembered next time the window opens.
6. **Long history.** In a repo with more than 2,000 commits, scroll to the bottom: "Loading older commits…" shows and the next page appends without the lanes above moving.
7. **Live refresh.** With the window open, commit in another terminal (or have a session commit). Within a second the graph updates, the selection stays on the same commit (or WIP row), and the diff you were reading doesn't jump.
8. **Light/dark** (☀/☾ in the header) recolours the graph with its own lane palette.

Report anything that looks off with a screenshot. These checks have only been verified through the headless renderer (`git_graph_*.png`, `git_tree_*.png`), not on a real desktop.
