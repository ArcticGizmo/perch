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

## PF17 (cheaper Edit cards)

| Check | Do this | Before | After |
|---|---|---|---|
| Diff looks the same | Open a session with Edit, MultiEdit and Write calls, in dark and light themes. | One row per diff line | Same look: markers in the gutter, red and green bands, `+N -M` header, "… N more lines" past 200. |
| Wrapped lines | Narrow the window until a long changed line wraps. | Marker on the first row, band on every row | The same. |
| Copy | Drag-select across several diff lines and copy. | Selection stopped at each line | It spans lines, and the copied text has no `+`/`-` markers. |
| Find in a diff | Ctrl+F for a word inside an open diff. | Highlighted | Still highlighted on the right glyphs. |
| Scrolling a long session | Scroll quickly through a session with many edits. | Brief hitches as edit cards rebuild | Smoother. Each card builds in about a third of the time and never re-diffs. |
| Bench | `perch bench-resume [path.jsonl]` | — | Per-variant timings; "without edits" now close to "whole" |

## PF13 (git graph refresh)

Open *View tree* on a repo with a session working in it. To count git runs, use Process Monitor with a Process Create filter on `git.exe`.

| Check | Do this | Before | After |
|---|---|---|---|
| Editing a file that's already modified | Let Claude edit an already-changed file, or save one yourself. | `git log`, `for-each-ref`, `stash list`, a `git status` per worktree and 2 whole-tree diffs | One `git status` plus scoped diffs. The shown diff updates if it's that file. |
| A new file appears | Create an untracked file. | Full reload | Full reload (the WIP count changes). |
| Commit, checkout, branch | Commit or switch branch in a terminal. | Full reload | Full reload. |
| Burst of edits | Have Claude make a dozen quick edits. | Overlapping reloads pile up | At most one running, plus one rerun afterwards. |
| Minimised | Minimise the window while a session works, then restore it after a minute. | Kept reloading | No git runs while minimised; one refresh on restore. |
| Scrolled-in history | Scroll past 2,000 commits (Load more), then edit a file. | The extra pages were dropped | They stay. |
| Subfolder | Open View tree from a session whose cwd is a subfolder of the repo, and modify a file elsewhere in the repo. | — | The "≈ no net change" marks are still right. |

## PF14 (no-op scans)

| Check | Do this | Before | After |
|---|---|---|---|
| Idle CPU | With a few idle sessions and the overlay open, watch `perch.exe` CPU for a minute. | A UI update every scan | Scans that find nothing new don't touch the UI. |
| Rows still update | Start a turn, let it finish, answer a permission prompt, rename a session (`/rename`). | Updates | Each still updates promptly. |
| Explicit actions | Acknowledge a done row, toggle external notify, add a project note, end a dormant Perch session. | Immediate | Still immediate (these always post). |
| Sub-agents | Run a task that spawns a sub-agent. | Its row and activity follow | The same. |

## PF15 (one read per transcript)

| Check | Do this | Before | After |
|---|---|---|---|
| Row details still right | With several sessions, check titles (`/rename`), task checklists, artifacts, the Markdown glyph, the PR/Jira glyph following a worktree, permission-mode badges, sub-agent rows and the context bar. | Correct | Identical. |
| Activity and stuck | Watch a running row's activity line, the burn readout and a stuck warning (if enabled). | Correct | Identical. |
| CPU while a long session works | Compare `perch.exe` CPU while a session with a multi-MB transcript runs a long turn. | Higher | Lower: its new bytes are read once per scan, not four times, plus one tail read instead of seven. |

## PF16 (resource monitor)

| Check | Do this | Before | After |
|---|---|---|---|
| Long recording stays responsive | Overlay right-click → Analyse resource usage → record for 30+ minutes while you work, and keep interacting with the window. | Clicks and scrolling hitched on every 5 s tick, worse as it grew | Smooth. Findings update a moment after each tick. |
| Findings unchanged | Open an old recording (Load). | — | The same findings, including "busy for …" runaways. |
| Activity lane | Record while a session works. | Tool calls appear | Still appear (the window's last few minutes are parsed as before). |

## PF18 and PF19

| Check | Do this | Before | After |
|---|---|---|---|
| Glyphs | Look over rows with IDE icons, PR (incl. approved tick), Jira, mail, note, Markdown, warning, mode chevrons; the GitHub and To-do strips; at 100% and 150% scaling. | — | Identical. |
| Attention chase | Let a session finish to trigger the chase. | Neon comet around the panel | The same. |
| Find a phrase in a long session | Ctrl+F, type a phrase from an early turn of a long session. | A pause before results | Near-instant. The highlights and next/previous work as before. |
| Find while streaming | Search for a common word, then send a prompt and watch while it streams. | Stutter as every highlight was rebuilt per frame | Smooth. |
| Find text inside an edit | Search for a line from an Edit diff far up the thread. | Found | Found. |

## Tier 3

| Check | Do this | Before | After |
|---|---|---|---|
| Transcript search speed | Overlay → Search transcripts, type a word you've used in a few sessions. Note the time in the status line. | Several seconds | Noticeably faster for a big history. |
| Refining a search | Type `capac`, let it finish, then go on to `capacitor`. | Every transcript searched again | "Searching… N of M" counts only the sessions that matched `capac` (plus any written since), and it finishes almost at once. |
| Refining picks up new text | With a search finished, say the new word in a live session, then extend the query to it. | — | The live session shows up. |
| Flight path stepping | Open the flight path, then hold ← for a couple of seconds and release. | CPU stays busy for a while as each day's scan runs in turn | The day you stop on loads; the skipped days' scans stop at once. |
| Flight path unchanged | Look at today, yesterday and a day a few weeks back, including a session that ran across midnight. | — | The same lanes, colours, project and branch names. |
| Cold stats rebuild | Quit Perch, delete `stats-cache.bin` beside `settings.json`, start it, and open Stats → All time. | About 10 s of "loading" on a big history | A few seconds, with every core busy briefly. |
| Stats unchanged | Compare the All time, Last 7 days and Today figures, and the Achievements window, with the release build. | — | The same numbers. |
| Open in GitKraken | With GitKraken's CLI on PATH, right-click a git session row a few seconds after launch. | "Open in GitKraken" | Still there. Without the CLI, still absent. |
| Long uptime | Leave Perch running for a working day with sessions starting and ending, and note `perch.exe`'s private memory morning and evening. | Creeps up with every session | Levels off. Rows, task lists and sub-agents of running sessions are unaffected. |
| Resume after a break | End a session, wait over 40 minutes, resume it. | Its row details return | The same (its transcript is folded again from the start). |
| File links | In a session, ask Claude to create a new file and name it in its reply; then scroll a long thread up and down. | Links appear | The new file's link still appears (within half a minute if it was named before it was written). Paths that exist stay clickable. |
| Drag-select across blocks | In a long session window, drag from a heading down through several paragraphs and a code block, including past the bottom edge so it auto-scrolls. Copy. | Works, but heavier on a long thread | The same selection and copied Markdown; smoother on a long thread. |
| Branch chip | Ask Claude to `git checkout -b` a new branch, and separately to just explain some code. | The chip updates after the checkout | The same; the explanation alone no longer schedules branch reads. |
| Changed files panel | Open the panel, have Claude edit two files. | The list refreshes | The same, about a second after each edit lands. |
| Palette and @ hover | Type `/` and move the mouse down the list; then type `@` and do the same. Use ↑/↓ too. | The highlight follows | The same. The list no longer jumps while hovering; arrow keys keep the highlight in view. |
| Hook activity hover | Open the hook activity view, move the mouse over card headers and event rows, and off them. | Rows light up | The same; the highlight follows from row to row and clears off them. |
| Git graph, Branch scope | From a feature branch, open View tree and switch to the Branch scope; pick another base from its menu. | Loads | Loads a little sooner, with the same rows, lanes and base candidates. |
| Search results unchanged | Search a Windows path (`C:\src`), a quoted phrase, an accented word (`café`), and with "Tools & thinking" on and off. | — | The same sessions and hits as before. |

Not reproducible on demand: the mic backstop (an app streaming without a privacy-ledger entry), and the Realtime-off social poll.
