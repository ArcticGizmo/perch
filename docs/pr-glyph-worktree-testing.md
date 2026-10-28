# PR glyph: follow the worktree a session is working in

A session that makes its own worktree (`git worktree add ..\<repo>--feature`) and then edits there by absolute path never moves Claude Code's recorded `cwd`: the worktree sits outside the session's working directories, so each `cd` lasts one command and commits go through `git -C`. Perch read the PR (and Jira ticket) from `cwd`, so the row showed the main checkout's PR, the same as every other session in that checkout.

Two changes, both in `SessionWorktree`:

1. **Automatic.** The PR/Jira lookups follow the worktree holding the session's newest successful Write/Edit/NotebookEdit. A failed write and any write outside the repo (memory files, another repo) are ignored. Only worktrees git records for the `cwd`'s repo are candidates, and transcript paths are matched against them as plain strings, never probed on disk. A newest write back in the session's own checkout returns to `cwd`.
2. **Pinned.** Right-clicking the PR glyph opens the same choices directly, under a `PR from worktree` caption (debug builds list the PR alert test triggers in their own section beneath). If the repo has no linked worktrees, a release build shows the ordinary row menu there instead. The row's right-click menu also gains **Pull request worktree** (or **Jira ticket worktree** when only Jira is on). It only appears when the repo has linked worktrees. It offers **Automatic** plus one radio item per worktree (`folder · branch`). A pick is stored as `{sessionId}.perch-worktree` beside the session's transcript, so it survives a resume. A pin to a worktree that's since been removed is ignored.

When the PR comes from somewhere other than `cwd`, the PR tooltip adds a line: `From worktree <folder>`, with ` (pinned)` for a pin. PR finished/reviewed/approved alerts are now keyed by the directory the PR was read from, so two sessions in one checkout following different worktrees don't share alert memory.

Unit coverage: `SessionWorktreeTests` (the pick rule, the transcript fold, the worktree list). The checks below are the live ones.

## Setup

- **Before:** the installed Perch (0.5.45).
- **After:** this branch, run with `dotnet run --project src/Perch.App -f net10.0-windows10.0.19041.0`. It uses its own settings (`%APPDATA%\Perch (Dev)`), so turn on **GitHub pull requests** there too.

You need a GitHub repo checked out on a branch with an open PR (call it `<repo>`), and a second branch that also has an open PR (or create one during the test).

## 1. A session that makes its own worktree

Start two Claude Code sessions in `<repo>`. In the second, paste:

> Create a git worktree for branch `<branch-with-a-pr>` as a sibling folder: `git worktree add ..\<repo>--wt <branch-with-a-pr>`. Do not cd into it permanently. Then use the Edit tool on `..\<repo>--wt\README.md` (absolute path) to append one blank line, and stop. Don't commit.

| | Before | After |
|---|---|---|
| Session 1's PR glyph | The checkout's PR | Unchanged |
| Session 2's PR glyph | The checkout's PR (same as session 1) | `<branch-with-a-pr>`'s PR, within a few seconds |
| Session 2's PR tooltip | No worktree line | `From worktree <repo>--wt` |

Then in session 2: *"Edit README.md in the current folder (not the worktree) to append a blank line."*

| | After |
|---|---|
| Session 2's PR glyph | Back to the checkout's PR; tooltip line gone |

## 2. Pin from the row menu

Right-click session 1's row.

| | Before | After |
|---|---|---|
| Menu | No worktree entry | **Pull request worktree** ▸ `Automatic`, then `<repo> · <branch>`, `<repo>--wt · <branch-with-a-pr>` |
| Pick `<repo>--wt` | — | Session 1 shows the worktree's PR; tooltip says `(pinned)` |
| Reopen the submenu | — | The pinned item is ticked |
| Pick `Automatic` | — | Back to the checkout's PR |

Right-click session 2's PR glyph itself.

| | Before | After |
|---|---|---|
| Menu (release build) | The ordinary row menu | `PR from worktree` caption, `Automatic (last edits: <repo>--wt)`, then the worktrees |
| Menu (debug build, `dotnet run`) | Only the `Debug: test …` alert triggers | The worktree choices, a separator, then the `Debug: test …` triggers |
| Pick `<repo> · <branch>` | — | The glyph swaps to the checkout's PR and the tooltip says `(pinned)` |

In a repo with no linked worktrees, the row-menu entry doesn't appear at all, and the glyph's right-click shows only the debug triggers (debug) or the row menu (release).

## 3. Pin survives a resume and a removed worktree

1. Pin session 1 to `<repo>--wt`, exit it, and `claude --resume` the same session. The pin is still applied (the file sits beside the transcript as `<sessionId>.perch-worktree`).
2. `git worktree remove ..\<repo>--wt`. Within ~30 s the pin is ignored and the row falls back to Automatic.

## 4. GitHub dashboard: middle-click "Open in GitHub"

Open the GitHub dashboard (click the overlay's GitHub strip) and middle-click a card's **Open in GitHub**.

| | Before | After |
|---|---|---|
| Middle-click | Nothing happens | The PR opens in a new browser window; the card is marked seen |
| Left-click | Opens in the current browser window | Unchanged |
| Button tooltip | The PR URL | The PR URL, then `Middle-click for a new window` |

## Clean up

`git worktree remove ..\<repo>--wt` (if still present) and revert the README edit.
