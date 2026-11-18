# Worktree glyph: show when a session is working in a git worktree

A session row now shows a small pine-tree glyph when the session is working in a **linked** git worktree rather than the repo's main checkout. It sits in the icon group left of the session name, just before the PR and Jira glyphs, because those are read from the same worktree's branch.

How Perch decides (`SessionWorktree.Locate`, on top of the existing `SessionWorktree.Resolve` from [pr-glyph-worktree-testing.md](pr-glyph-worktree-testing.md)):

1. **Pinned.** A worktree the PR/Jira lookups were pinned to from the row menu wins, so the tree always agrees with the PR beside it. The pin is Perch's own: Claude never sees it and it never moves the session, which is why the glyph itself offers no way to set one.
2. **Followed edits.** Otherwise, the worktree holding the session's newest successful Write/Edit, if that isn't the session's own folder.
3. **Started there.** Otherwise, the worktree holding the session's own folder: `claude --worktree`, a `.claude/worktrees/<name>` folder, or a session simply started in a sibling worktree.

The main checkout never gets the glyph. In a bare layout (`.bare` + worktrees) every checkout is a linked worktree, so every session there gets it. A submodule is never mistaken for a worktree. Worktree lists come from a few small reads under the repo's `.git`, cached for 30 s per repo, with no git process.

- **Hover:** `Worktree <folder>`, `Branch <name>` (or `Detached HEAD`), how Perch knows (`The session's own folder` / `Followed from the session's latest edits` / `PR pinned to this worktree`), and `Click to open the git graph`.
- **Click:** opens the git graph for the session.
- **Right-click:** the ordinary row menu. The worktree picker stays where it was, under **Pull request worktree** / **Jira ticket worktree**, only while one of those glyphs is on.
- **Setting:** Settings → Session row → **Worktree glyph** (search "worktree"). On by default.

Unit coverage: `SessionWorktreeTests` (`Classify` for each source, main checkout, bare layout, and `Locate` against an on-disk repo). The checks below are the live ones.

## Setup

- **Before:** the installed Perch (0.6.11).
- **After:** this branch, run with `dotnet run --project src/Perch.App -f net10.0-windows10.0.19041.0`. It uses its own settings (`%APPDATA%\Perch (Dev)`). The glyph is on by default; leave **GitHub pull requests** and **Jira** off for checks 1-3 so the glyph is tested on its own.

You need any git repo (call it `<repo>`) with no linked worktrees to start with.

## 1. A session started in a worktree

In a terminal in `<repo>`, run `claude --worktree glyph-test`, then in that session paste:

> Say hi and stop.

| | Before | After |
|---|---|---|
| Row | No worktree glyph | Pine glyph left of the name |
| Hover the glyph | — | `Worktree glyph-test`, `Branch <its branch>`, `The session's own folder` |
| Click the glyph | — | The git graph opens for the repo |

Also start a plain `claude` in `<repo>` itself.

| | After |
|---|---|
| The plain session's row | No glyph (main checkout) |

## 2. A session that makes its own worktree

In the plain session from check 1, paste:

> Create a git worktree as a sibling folder: `git worktree add ..\<repo>--wt -b wt-glyph-test`. Do not cd into it permanently. Then use the Edit tool on `..\<repo>--wt\README.md` (absolute path) to append one blank line, and stop. Don't commit.

| | Before | After |
|---|---|---|
| The session's row | No glyph | Pine glyph within a few seconds |
| Hover | — | `Worktree <repo>--wt`, `Branch wt-glyph-test`, `Followed from the session's latest edits` |

Then: *"Edit README.md in the current folder (not the worktree) to append a blank line."*

| | After |
|---|---|
| The session's row | Glyph gone (back in the main checkout) |

## 3. Right-click

Right-click the glyph on the `--worktree` session from check 1.

| | After |
|---|---|
| Menu | The ordinary row menu (no worktree picker while PR and Jira are off) |

## 4. The setting

Settings → search `worktree`.

| | After |
|---|---|
| Search result | **Worktree glyph** toggle, on |
| Turn it off | Every tree glyph disappears |
| Turn it back on | Glyphs return within a second or two |

## 5. Alongside the PR glyph

Turn on **GitHub pull requests** and repeat check 2 with a branch that has an open PR.

| | After |
|---|---|
| Row | Tree glyph, then the PR glyph |
| Row menu → **Pull request worktree** ▸ `<repo>--wt` on the plain session (after its edits moved back) | Tree glyph appears beside the worktree's PR; hover says `PR pinned to this worktree` |
| Same menu ▸ `<repo> · <branch>` on the `--worktree` session | Both the PR and the tree follow the main checkout, so the tree disappears |

## Clean up

Exit the sessions, `git worktree remove ..\<repo>--wt`, `git branch -D wt-glyph-test`, remove the `--worktree` session's worktree (`git worktree remove .claude\worktrees\glyph-test`), and revert the README edit.
