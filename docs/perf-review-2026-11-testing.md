# Perf review 2026-11: manual checks

Hand checks for the fixes in `docs/perf-review-2026-11.md` (branch `perf/review-2026-11`). Most of these fixes cut background cost rather than change what you see, so each check names the signal to watch. Unit tests and the `render` bench cover the rest.

## Running each build

| Build | How |
|---|---|
| Before | `git checkout main`, then `dotnet run --project src/Perch.App -f net10.0-windows10.0.19041.0` |
| After | `git checkout perf/review-2026-11`, then the same command |
| Bench (either) | `PERCH_BENCH=1 dotnet run --project src/Perch.App -c Release -f net10.0-windows10.0.19041.0 -- render captures/<dir>` |

A dev run writes its logs to `%APPDATA%\Perch (Dev)\logs\`. Quit the installed tray first, or the dev run hands off to it.

## Headless bench (no session needed)

| Line in the bench output | Before | After |
|---|---|---|
| `overlay paint bench … KB allocated/frame` | about 292 KB | about 254 KB (PF7) |
| `spinners: … animating while hidden` | 2 | 0 (PF1) |

## Batch A

| ID | Do this | Before | After |
|---|---|---|---|
| PF1 | Open a session window and leave it idle. Watch `perch.exe` CPU in Task Manager. | A steady trickle of CPU from the window repainting at 25 Hz | About idle. The launcher, resume and agent-footer spinners still spin while they're shown. |
| PF2 | Start Perch with several long transcripts live, and compare first-scan time or CPU. | Every `tool_result` line is parsed by up to three folds | Far fewer lines are parsed. Markdown-file, worktree and sub-agent glyphs are unchanged. |
| PF3 | With *Git line stats* on, leave a session idle for 5 minutes. Count `git.exe` starts (Process Monitor, filter Process Create). | About 2 `git diff` runs a minute per idle repo | About one every 2 minutes. A working session still refreshes every ~3 s, and the stats catch the last edit once it goes idle. |
| PF4 | Run a session inside VS Code or Cursor and keep the overlay open. | A process snapshot on most scans (not visible) | A snapshot only when a new session appears (not visible). Check the IDE icon still shows on the row. |
| PF5 | Open the git graph (*View tree*) while a session works, with many branches. Watch `perch.exe` CPU. | The whole rail rebuilds every ~2 s | The rail rebuilds only when a worktree's session changes. Check the worktree cards still show which session is working where. |
| PF7 | Overlay with usage bars expanded and an attention chase running. | — | It looks identical; only the bench number moves. |
| PF8 | Session window, Ctrl+F, type `session` quickly. | A search on every keystroke; a long thread stutters | One search ~150 ms after you stop. Type one letter: nothing until Enter. A history-search landing still highlights at once. |
| PF9 | Settings → ntfy topic (or a Jira field): type a long value quickly, then close Settings at once. | The whole settings file saved on every key | Saved once you pause or leave the box. Reopen Settings: the full value is there. |
| PF10 | Start a new `claude` session with auto-start on and the tray running. | — | The tray isn't launched twice. SessionStart is a few ms faster, which you won't notice. |

## PF6 (lock and visibility gating)

| Check | Do this | Before | After |
|---|---|---|---|
| Usage, status and social pause while locked | With usage on (and Social signed in, if you use it), lock the PC (Win+L) for more than 5 minutes, then unlock. Open `logs\lock.log`. | No log. Polls ran all through the lock. | A `locked` line, then `unlocked; catching up: UsageMonitorHost, StatusMonitorHost[, SocialFeedMonitorHost]`. The usage bars refresh within a few seconds of unlocking. |
| Short lock | Lock and unlock within 3 seconds. | — | `lock.log` may show nothing; no catch-up is needed. |
| Notifications still fire while locked | With *notify when locked* on, lock the PC while a session finishes or waits for input. | External push arrives | It still arrives: the session scan and PR polls are not gated. |
| Merged or closed PR | Keep a session on a branch whose PR is merged. Watch `gh.exe` starts in Process Monitor. | `gh pr view` every poll interval (5 min by default) | Once per 30 min (or your interval, if longer). An open PR's checks and reviews still update at your interval. |
| Mic monitor at rest | Mic strip on, nothing using the mic. Use Process Explorer to watch `perch.exe` CPU or audio-endpoint handle churn. | WASAPI enumerated every 2 s | Every 10 s. |
| Mic in a call | Join a Teams or Zoom call, or open Voice Recorder. | Strip appears within ~2 s | It still appears within ~2 s, names the app, and jumps to it on click. The device name shows in the tooltip. |
| Overlay 1 s tick, hidden | With a session running, hide the overlay however you normally do. | 1 Hz repaint and account lookup continue while hidden | The tick stops. Show the overlay again: the elapsed time resumes at once. |
| Overlay 1 s tick, compact rows | Switch to compact rows (the density toggle) with a session running. | 1 Hz repaints of a row with no elapsed label | No per-second repaints. Switch back to roomy: the elapsed time ticks again. |

## PF11 (publish without compression)

Compare the installed v0.6.10 (before) with the first release cut from this branch (after), or run `publish.bat` and install `releases\Perch-win-Setup.exe`.

| Check | Do this | Before | After |
|---|---|---|---|
| Tray memory | Leave the tray running for 10 minutes with your usual sessions. In Task Manager → Details, add the *Commit size* column and read `perch.exe`. | Higher by roughly 25–90 MB (assemblies held decompressed) | Lower by roughly that much. *Memory (active private working set)* should drop too. |
| Cold start | After a reboot (or with the tray quit for a while), start Perch and read the last `startup:` line in `%APPDATA%\Perch\logs\launch.log`. | No `startup:` line (v0.6.10 doesn't write it) | `startup: overlay open N ms after process start`. Note N across a few launches, so the next release can be compared. |
| Download and install size | Look at the release assets and `%LOCALAPPDATA%\Perch\current\perch.exe`. | Setup about 64 MB, perch.exe 50 MB | Setup about 2 MB bigger, perch.exe about 115 MB |

## PF12 (resume without freezing)

| Check | Do this | Before | After |
|---|---|---|---|
| Resume a long session | In the session launcher, resume your longest session (several MB of transcript). Try to scroll or type as soon as the window appears. | The window paints, then freezes for one to several seconds before anything responds | The newest turns show at once and respond. Older turns fill in above over the next seconds; the scrollbar grows as they arrive. |
| Scrolled up while it loads | Right after resuming, scroll up. | — | The view stays where you put it as older items are added above (no jumps). |
| Jump to top / previous prompt | Right after resuming, click jump-to-top, then the previous-prompt button a few times. | Works | Works the same: it builds anything not yet built first, so it may pause briefly the first time. |
| Ctrl+F straight after resume | Search for a word from the first prompt. | Finds it | Still finds it. |
| Theme change | Open a long session and switch theme in Settings → Appearance. | Each open session window stalls | Newest content re-tints at once; the rest follows. |
| `/clear` and rewind | Run `/clear` in a long session, or rewind to an early prompt. | A stall while the column rebuilds | No stall. |
| Bench | `PERCH_BENCH=1` render, optionally with `PERCH_RESUME_TRANSCRIPT=<path to a .jsonl>` | `resume bench` lines are new | UI block about 20 ms (synthetic) |

Not reproducible on demand: the mic backstop (an app streaming without a privacy-ledger entry), and the Realtime-off social poll.
