# Statusline git counts: manual test prompts

Manual before/after checks for branch `statusline-git-perf`. A generated status line whose template uses `git.staged`, `git.unstaged`, `git.changes` or `git.dirty` now:

- counts both sides with **one** `git status --porcelain=v2` call instead of two `git diff --numstat` calls;
- calls Git for Windows' real `<root>\<msys>\bin\git.exe` instead of the `cmd\git.exe` launcher, which only starts that same binary as a second process;
- serves the counts **stale-while-revalidate** from `perch-statusline-cache\` beside the script. The last result is shown at once; once it's older than 2 s, a detached copy of the script recomputes it for the next refresh. Only a folder with no cached result yet runs git inline.

On a machine with endpoint security every process start costs ~100–150 ms, so the refresh was mostly process starts. Benchmarked on the "With Org" profile (25 interleaved runs, median): **597 ms before, 184 ms after**, against 172 ms for the same line with no git counts at all.

`StatuslineScriptTests` covers the counting (a staged rename, a file staged then edited again, untracked files ignored), the cache (fresh hit, stale-then-refreshed), and the existing hardening and repo-local git-lfs end-to-end checks. These prompts cover what only a live Claude Code session shows.

## Running before and after

The script is a snapshot compiled when a profile is applied, so switching builds means re-applying the profile.

- **Before:** your installed Perch. Statusline designer → pick a profile that shows git counts (e.g. "Rate-aware verbose") → **Set active** in the config dir you'll test in.
- **After:** quit the tray, `git checkout statusline-git-perf`, `dotnet run --project src/Perch.App -f net10.0-windows10.0.19041.0`, then **Set active** the same profile in the same dir. The dev build's line starts with an amber ` dev ` tag, so you can tell which script is live.

Start a fresh `claude` in a git repo with uncommitted changes, under that config dir.

## 1. The line keeps up while Claude streams

```text
Read three of the largest source files in this repository one at a time, and after each one write a two-sentence summary of it.
```

| | Before | After |
|---|---|---|
| Status line during the turn | Lags behind the conversation or visibly stalls between updates | Updates as quickly as a line with no git counts |
| CPU in Task Manager | A burst of `node` + 3–6 `git` processes on every update | One `node` per update, plus a background `node` + `git` at most every 2 s |

## 2. Counts catch up after an edit

```text
Append a line "perf check" to the end of README.md, then reply with just "done".
```

| | Before | After |
|---|---|---|
| Unstaged count after the edit | Goes up on the next refresh | Goes up one refresh later: the refresh that notices the stale entry still shows the old count and starts the background update |

Then run `git checkout -- README.md` in another terminal and send any message. After: the count drops back after at most two refreshes.

## 3. A folder Perch hasn't seen yet

`cd` to another repo that has changes and start `claude` there.

| | Before | After |
|---|---|---|
| First line in that folder | Shows counts | Shows counts (git runs inline because the folder has no cached result yet) |
| `perch-statusline-cache\` beside the script | n/a | Holds one `git-<hash>.json` per folder |

## 4. Timing it yourself

From the repo you test in, with `$cfg` set to the config dir:

```powershell
$cfg = "$HOME\.claude"   # or your test config dir
$payload = @{ cwd = (Get-Location).Path; workspace = @{ current_dir = (Get-Location).Path }; model = @{ display_name = 'Opus' } } | ConvertTo-Json
1..10 | ForEach-Object { (Measure-Command { $payload | node "$cfg\perch-statusline.mjs" | Out-Null }).TotalMilliseconds }
```

| | Before | After |
|---|---|---|
| Typical time per run | ~550–650 ms | ~170–200 ms (the first run in a new folder is ~450 ms) |

## 5. Not checkable on demand

- **A repo-local git-lfs with stat-dirty files.** The direct git binary gets the launcher's PATH order, so `git-lfs` and `sh` still resolve. The unit test covers a `git lfs install --local` repo. If you work in an LFS repo, check its counts don't jump after a checkout.
- **Git not installed under `<root>\cmd`.** Any other git on PATH (scoop, a portable copy, macOS/Linux) is called as before. It still gets the single status call and the cache.
