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

## CP11: double-click to check out

Use a throwaway clone or a branch you don't mind moving, with at least one extra worktree (`git worktree add ..\<repo>--try <branch>`). Every successful checkout shows a toast at the bottom of the graph ("Checked out X", the git command underneath in mono, **Undo**, ✕); it hides after ~6 s unless the pointer is on it. Failures show git's own line in red for ~10 s, with no Undo. *Before (all of these):* double-clicking a pill, branch or commit did nothing; only another worktree's WIP row reacted.

1. **Clean tree, free branch.** With no uncommitted changes, double-click a local branch pill (or the branch in the rail's **Local** list). It checks out straight away, the solid pill and the ring move to it, and the window lands on its HEAD. Click **Undo**: back on the previous branch, toast "Back on <branch>".
2. **Double-click a commit row** (not on a pill). A commit with a local branch checks that branch out. A commit with only a remote branch creates a tracking branch ("Checked out x (tracking origin/x)"). A commit with neither opens a menu: "No branch points at abc1234" / **Check out abc1234 here, detached** / **Create a branch at abc1234…**.
3. **Create a branch.** From that menu pick **Create a branch at…**: a bar opens under the graph title ("New branch at abc1234", a name box, **Create and check out**, ✕). Type a name, Enter: the branch is created there and checked out. Esc closes the bar without doing anything. A bad name (e.g. with a space) shows git's error in the toast.
4. **Tag.** Double-click a tag in the rail's **Tags** list: the menu says "<tag> is a tag" and offers the detached checkout or a branch there.
5. **Remote branch.** Double-click a remote pill/rail row with no local branch of that name: a local tracking branch is created and checked out. If a local of that name exists, it's that local branch that gets checked out.
6. **Branch held by another worktree.** Double-click the branch your extra worktree has checked out: a menu says it's checked out in `<name>` and offers **Open <name> in this window** (the window re-points there) or **Check out abc1234 here, detached**.
7. **Dirty tree.** Make an uncommitted change (and an untracked file) and double-click another branch: a menu says "<worktree> has N changes not committed" and offers **Stash the N changes, then check out X**, **Bring the N changes to X**, **Cancel**.
   - *Stash:* the tree goes clean, a "Perch: before checking out X" stash appears in the rail's **Stashes**, toast "…; N changes stashed". **Undo** switches back *and* pops that exact stash (even if you stashed something else in between): your edits and the untracked file are back.
   - *Bring along:* the edits come with you (toast "…, changes brought along"); **Undo** carries them back.
   - If git refuses (e.g. a branch held elsewhere), a stash made for it is popped straight back: nothing is lost and no stash is left behind.
8. **Session guard.** With a Claude session running in this worktree, any checkout first shows a menu whose top line warns "A Claude session is working in this worktree…" with **Check out X anyway** / **Cancel** (or the stash/bring-along choices when dirty). It warns; it never blocks.
9. **Already there.** Double-click the branch this worktree is on: toast "<worktree> is already on <branch>." Nothing runs.
10. **Light/dark** recolours the toast and the branch bar.

## Feedback round 1: diff over the graph, steady header, resizable columns

1. **Diff over the graph.** The window is now graph (left) | files (right). Click a commit: its files fill the right pane, and the graph stays visible. Click a file (or select it and press Enter): its diff covers the graph, with a bar on top ("abc1234 · path", or "Working tree / Staged · path") and **✕** top right. ✕ or Esc goes back to the graph. While the diff is open, clicking other files switches the diff. The + / − stage buttons on file rows don't open it. "View diff" from a session opens straight onto that file's diff. *Before:* a third pane on the right always showed the diff, squeezing the graph.
2. **Steady header.** The All / Worktrees / This branch switch (and the "since ⑂ base" picker) now sit in the graph card's own title row, in a fixed place. The window header shows the branch, status and path, and trims instead of pushing anything (the path keeps its folder name). *Before:* the switch moved left and right as the branch name and status changed length.
3. **Branch pills.** The branch this worktree has checked out always gets its pill, even when the name is long. Long names lose their middle (`dependabot/…site-1e1827e218`) and hovering a pill shows the full name. *Before:* a long checked-out branch's pill vanished into "+1", and names were cut at the end.
4. **Resizable columns.** Hover the edge between the pills and the lanes, or between the lanes and the commit messages: the cursor becomes ↔ and a line shows. Drag to resize. Double-click an edge to put it back. The widths are remembered next time.
5. Light/dark recolours the diff bar.

## Feedback round 2

1. **Opens where there's room.** Open the graph from the overlay row's right-click → **Git graph…** (now the first item of that group, above Work items; the window is titled "Git graph"). It opens on the overlay's monitor, in the space beside the overlay, never underneath it (docked or floating), and shrinks to fit that space. Only this Perch's own overlay is known; a second Perch's floating overlay isn't (a docked one is, since it reserves the screen edge).
2. **Remote marks.** A local branch's pill (and its rail row) shows: nothing = local only, no remote branch; **●** = same commit as its upstream; **↑2 ↓1** = ahead/behind its upstream; **⊘** = its upstream is configured but gone. Hover for it in words ("2 commits ahead of origin/x", "Local only: no remote branch", …).
3. **Find commits.** With the graph showing, **Ctrl+F** opens "Find commits" (Ctrl+G too). As you type, matching rows (message, author, hash prefix, branch or tag name) are washed amber and the first is selected; "3 / 14" counts them, Enter / Shift+Enter or ‹ › walk them. Nothing in what's loaded → "No match in the newest 2,000 commits" and **Search older history**, which loads older pages until something matches. With a diff open, Ctrl+F still finds in the diff.
4. **Right-click, not double-click.** Right-click a commit row: Check out <its branches>, Check out abc1234 (detached), Create a branch at…, Create a worktree at…, Copy commit hash / message. Double-clicking a commit row now does nothing (a commit detail view is planned there); double-clicking a branch pill still checks it out.
5. **Columns.** A header row (Branch / tag · Graph · Commit message · Author · When) sits above the rows, with a ‖ grip on each draggable edge.
6. **Busy cover.** Checking out (double-click a pill, or any drop / Undo) greys the graph with a "Checking out X…" card until the refreshed graph is on screen.
7. **Delete a local branch.** Right-click a local branch (pill or rail) → **Delete <branch>…**. A merged branch just goes; one with unmerged commits asks first. The toast's Undo brings it back at the same commit (its upstream setting isn't restored). Not offered for a branch a worktree has checked out.
8. **Create a worktree from a menu.** Right-click a branch → **Create a worktree for <branch>** (a free branch goes straight in; one held elsewhere asks for a new branch name); right-click a tag or commit → **Create a worktree at…** (asks for a name).
9. **WIP rows.** No pill on a WIP row any more. This worktree's row says "N uncommitted changes"; another worktree's says "N uncommitted changes in <its folder>".
10. **Collapse the files pane.** The **›** at the top right of Files collapses it to a thin strip; the strip's **‹** brings it back at its old width. Remembered.

## Feedback round 3: file menu, toolbar, menu overflow

Use a throwaway clone that has a remote you can push to (a fork, or a bare repo: `git clone --bare . ../sandbox.git` then `git remote add origin ../sandbox.git`).

1. **Right-click a file in Changes.** Menu: **Stage <file>**, **Discard changes to <file>…** (tracked files only), **Delete <file>…**, then **Open file**, **Show in Explorer**, **Copy path**. Discard asks, then puts the file back to its staged (or committed) content. Delete asks, then removes it from disk: a tracked file shows up under Staged as a deletion (`D`), ready to commit; an untracked file or folder is just gone. *Before:* no right-click menu; discarding was only the diff's per-file button.
2. **Right-click a file in Staged.** Menu: **Unstage <file>**, **Discard all changes to <file>…** (staged and unstaged together, back to the last commit), **Delete <file>…**. For a file that's new in this commit, the dialog says discarding deletes it. For a rename (`git mv a b`), discarding brings `a` back and removes `b`. A file that's already deleted on disk has no Delete, Open or Show in Explorer.
3. **The toolbar.** Right of All / Worktrees / This branch in the graph card: **Pull ▾ · Push · Branch · Stash · Pop**. Hover each one to see what it will run.
   - **Pull** pulls this worktree's branch. It reads **Pull ↓3** when the remote branch is 3 ahead, and is greyed out on a detached HEAD or a branch with no upstream. **▾** offers **Fetch all**, **Pull (fast-forward only)**, **Pull (merge if diverged)** and **Pull (rebase)**. The one you pick runs and becomes what the button does (a ✓ marks it, and the button reads **Fetch** in that mode). A pull that moved the branch has **Undo** in its toast, which resets back. A pull that stops on conflicts shows the orange in-progress banner. *Before:* no pull or fetch in the window.
   - **Push** reads **Push ↑2** when 2 ahead. On a branch with no upstream it publishes to `origin` and sets tracking (`git push -u origin <branch>`; hover says so). It never forces: a rejected push shows git's reason in a red toast.
   - **Branch** opens the "New branch from <branch>" bar. Type a name and press Enter: the branch is created at HEAD and checked out, your changes stay as they are, and Undo switches back.
   - **Stash** (only when there are changes) stashes everything, untracked files included, as "WIP on <branch>"; Undo pops it back. **Pop** (only when there's a stash; it reads **Pop (3)** when there are 3) applies the newest stash and drops it. If it conflicts with your changes, a red toast says so and the stash stays in the list.
   - A credential prompt can't hang the window: if git would need to ask for a password in a terminal, the push or pull fails with git's message. A credential manager's own sign-in window still appears.
4. **Long branch names in menus.** Double-click a pill with a long name (a Dependabot branch) while this worktree has changes. The "Stash the 1 change, then check out …" and "Bring the 1 change to …" items wrap onto a second line inside the menu instead of running past its edge. The same goes for the session warning line, the drop menu and the right-click menus. *Before:* the text ran out past the menu's right edge.

## Feedback round 4: files pane clicks

1. **The heading toggles.** Click anywhere on the Files heading row (not only the **›**): the pane collapses to its strip. Click anywhere on the strip (not only the **‹**): it comes back at its old width. *Before:* only the chevrons did it.
2. **Empty space opens nothing.** With a few files in Changes (or a commit with a few files selected), click in the blank area below the last row. No diff opens. Clicking a row still opens its diff. *Before:* a click below the rows opened the diff of whichever file was selected, usually the first.

## Feedback round 1: hiding branches

1. **Hide one.** Hover a branch in the rail's Local or Remotes list: an eye appears at the right. Click it: the branch leaves the graph (its pill, and any commits only it reaches), and its rail row stays, dimmed and italic with the eye struck through. The header shows "N · 1 hidden". Click the struck eye to bring it back. Or right-click a pill in the graph → **Hide <branch> from the graph** (the toast offers Undo). Branches a worktree has checked out, and the main line (`main` / `origin/main`), can't be hidden; they get no eye, and the pill menu says why.
2. **Show everything again.** The **⋯** on the Local or Remotes header → **Show all N hidden branches**.
3. **Auto-hide.** **⋯** → **Hide new remote branches that aren't mine** (a ✓ shows when it's on). Turning it on hides nothing that's already there. Then make a remote branch that isn't yours appear: e.g. have someone push one, or run `git fetch` after Dependabot opens a PR. On the next refresh it's hidden, with a toast "Hid origin/… : a new branch that isn't yours" and Undo. A new remote branch with a commit of yours (one you pushed from another clone) stays visible. "Yours" = a commit that main doesn't have, made with your git `user.email` or `user.name`.
4. **Per repo, every worktree.** What's hidden is remembered per repository and is the same in every worktree of it, and after closing and reopening the window.
5. The scopes: hiding applies to **All**; **Worktrees** and **This branch** show what they always did.

## CP12–CP14: drag to merge, rebase, cherry-pick and make worktrees

Use a throwaway clone. Set it up with: two local branches that have diverged (`a` and `b`, each with a commit the other lacks), a branch `behind` that is an ancestor of `main` (`git branch behind main~2`), and one extra worktree (`git worktree add ..\<repo>--try b`). *Before (all of these):* nothing in the graph or rail could be dragged.

**The gesture (CP12)**

1. **Start a drag.** Press on a branch pill in the graph (or a branch in the rail's Local/Remotes lists, or a worktree card) and move a few pixels. A small card follows the pointer with the branch name, and a hint line under it. Pressing on a commit row (not a pill) and moving drags the commit (the card shows its short hash and subject). Tags, stashes and WIP rows don't drag. A plain click still just selects/jumps as before.
2. **Targets light up.** Over a branch pill: that row gets an outline and the pill a ring, and the hint says what a drop would do ("Merge a into b in <worktree>", "· 1 more on drop" when the menu has more). Over a row but not on a pill, the row's local branch is the target. Over a worktree's WIP row, or a worktree card in the rail, the target is that worktree. Over nothing useful: "Drop on a branch, a worktree or + New worktree. Esc cancels."
3. **New worktree zone.** While dragging, a "+ New worktree" box appears under the worktree cards in the rail; it disappears when the drag ends.
4. **Auto-scroll.** Drag towards the top or bottom edge of the graph: it scrolls, and the target under the pointer updates.
5. **Cancel.** Esc during a drag, or releasing over nothing, ends it with no menu and no change.

**The drop menu and operations (CP13)**

Every menu item names what happens and where (the worktree), with the git command underneath in mono. A session working in the worktree an option runs in adds an italic warning line at the top (warning only). Every success shows the toast with **Undo**.

6. **Diverged branches.** Drag `a` onto `b`: **Merge a into b in <worktree that has b>** and **Rebase a onto b** (if no worktree has `a`, the rebase says "(checks out a in <this worktree> first)"). Pick Merge: `b` gets a merge commit in the worktree that has it (that can be the extra worktree; this window's worktree isn't touched). **Undo** resets `b` back (`reset --keep`, so it refuses rather than lose changes).
7. **Rebase.** Drag `a` onto `b` again and pick Rebase: `a` is replayed on `b`. Undo puts `a` back where it was, and switches this worktree back if the rebase had to check `a` out.
8. **Fast-forward.** Drag `main` onto `behind`: one option, **Fast-forward behind to main** (`git fetch . main:behind` when no worktree has `behind`, `git merge --ff-only` in its worktree when one does). Undo moves it back. Dragging `behind` onto `main` instead says "main already contains behind." in a toast, with no menu.
9. **Local onto remote.** Drag a local branch onto its `origin/...` pill: rebase onto it when they've diverged, fast-forward when the local one is just behind.
10. **Cherry-pick.** Drag a commit row from `a` onto the `b` pill: **Cherry-pick abc1234 onto b in <worktree>**. Dropping a commit onto a branch that already contains it says so.
11. **Switch first needs a clean tree.** An option that would check a branch out in this worktree first is greyed out ("· commit or stash this worktree's changes first") while this worktree has changes.
12. **Conflicts.** Make `a` and `b` change the same line, drag `a` onto `b` and Merge in this window's worktree: the toast says it stopped on conflicts, and an orange banner appears under the graph title: "A merge is in progress in <worktree>: N conflicted files. Resolve and stage them in Changes, then Continue." with **Continue** and **Abort** (and **Skip commit** for a rebase or cherry-pick). The conflicted files show as `U` in Changes. Abort puts it back; resolving, staging and Continue finishes it (no editor opens). If the conflict happened in *another* worktree, the toast instead offers **Open <worktree>**, which re-points the window there (where the banner then shows).

**Worktrees from drag (CP14)**

13. **Onto a worktree.** Drag a free branch onto the extra worktree's card: **Check out <branch> in <worktree>**; it switches that worktree, not this one. If that worktree has changes, you're asked stash / bring along / cancel first. Dragging a branch that another worktree holds offers only a detached checkout. Dragging a commit onto a card offers cherry-pick onto that worktree's branch and a detached checkout.
14. **New worktree.** Drag a branch no worktree has onto **+ New worktree**: **New worktree for <branch>**, command `git worktree add ..\<repo>--<branch> <branch>` (or, in a repo whose existing worktrees follow another layout, that layout). The toast says "Made worktree <folder> on <branch>" with **Undo** (removes the worktree) and **Start a session there** (opens a Perch session window in it). The new worktree appears in the rail.
15. **New worktree, new branch.** Drag a remote-only branch onto the zone: a tracking branch of the same name in a new worktree. Drag a branch another worktree holds, or a commit: the branch bar opens ("New worktree on a new branch from …"); type a name and Enter. Undo removes the worktree and deletes that new branch (only if it hasn't moved).
16. **Card menu.** Right-click a worktree card: **Open <name> in this window**, **Start a Claude session in <name>**, **Remove <name>…** (not for this window's worktree or the main one), **Prune worktrees whose folders are gone**. Remove on a clean worktree just goes; on one with changes, or with a session working in it, it asks first (and only then forces). The branch stays.

These have been verified headlessly: a synthetic pointer drag in `render` (`git_tree_drag_pill_1x.png`, `git_tree_drag_newworktree_1x.png`, `git_tree_drop_menu_1x.png`), the banner in `git_tree_checkout_1x.png`, and every git operation and its Undo by integration tests against a throwaway repo. Not on a real desktop: the feel of the drag, menu placement at the pointer, and anything multi-monitor.
