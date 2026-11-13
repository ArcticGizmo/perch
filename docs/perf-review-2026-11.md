# Performance review — November 2026

A second performance pass over `main` at `fea89f2` (v0.6.10), six weeks after the adversarial review whose fixes are tracked as CP20–CP24 in `docs/review-fixes-plan.md`. All of those fixes are on `main`, along with the session-thread virtualization (#47), and this pass deliberately skipped them unless something had regressed. Nothing had.

Five read-only reviewers each covered one area:
- steady-state background work;
- overlay and owner-drawn paint;
- the session window;
- the Core data layer, startup and `perch-hook`;
- the features that landed in October and November.

Their top claims were then re-checked against the code. The **✔** column marks a finding that was hand-verified at the cited lines; the others are reviewer-reported with file:line evidence but not re-read.

## Baseline (headless `PERCH_BENCH=1 … render`, Release, this machine)

| Bench | Result |
|---|---|
| Overlay paint (sample overlay + attention chase) | mean 11.6 ms/frame (p95 19.4), draw calls 1.72 ms, **292 KB allocated/frame** |
| Streaming a 43 KB reply | mean 5.3 ms/frame, p99 26 ms, max 43 ms |
| Long thread (240 items), virtualization on/off | 5–10 items built, heap −3 MB vs **98.9 MB** with it off |
| Delta flush in a 200-tool turn | mean 0.016 ms |
| Window leak check | 1/10 reachable (the known flaky baseline) |

These match the post-CP22/CP23 numbers, so those fixes have held.

## Summary

The September work fixed the hot paths it targeted. The remaining cost falls into four groups:

1. **Things that keep running when they don't need to.** Hidden spinners tick at 25 Hz. Git line stats spawn `git diff` for idle sessions. The IDE detector snapshots every process on each scan. Several pollers keep going while the desktop is locked. Every scan pushes a full overlay update even when nothing changed.
2. **The git graph window**, new since the review, reloads everything on every file edit and rebuilds its rail on every session scan.
3. **Resume and Ctrl+F in the session window.** These are the remaining UI-thread freezes: the full window builds every item at once, and find rebuilds everything on every keystroke.
4. **Duplicated transcript reading.** Four separate `TranscriptFold`s read the same file, and their pre-filters are looser than intended.

Most of the first group are **S** changes.

## Tier 1 — quick wins (S each)

| ID | ✔ | Finding | Where | Fix |
|---|---|---|---|---|
| PF1 | ✔ | **Hidden `LoadingSpinner`s tick at 25 Hz for the whole life of every open session window.** The timer runs while the spinner is *attached*, not while it's *visible*. Each window keeps three of them under hidden parents: the launcher, the resume overlay and the agent footer. That's about 75 UI wakeups and invalidates a second per window. `Render` also creates a `new Pen` and a new `StreamGeometry` every frame. | `Views/LoadingSpinner.cs:30-46,56-58`; hosts at `SessionWindow.cs:832,2558`, `SessionWindow.Agents.cs:144` | Start and stop the timer from `IsEffectivelyVisible` (override `OnPropertyChanged` for `IsVisibleProperty` and walk up the tree, or have the hosts toggle the spinner itself). Use `OverlayDraw.Pen`. |
| PF2 | ✔ | **Fold pre-filters are too loose.** `Contains("is_error")` also matches `"is_error":false`: in a real 2.4 MB transcript that is 48 of 129 tool_result lines, of which only 5 are `true`. The legacy sub-agent fold parses *every* `tool_result` line, and that fold runs for most sessions (those with no `subagents/` dir). Tool results are most of a transcript's bytes, so the first scan after launch builds a full JsonNode DOM for tens of MB per fold. | `MarkdownFilesReader.cs:81`, `SessionWorktree.cs:179`, `SubAgentReader.cs:489` | Match `"is_error":true`. In `StepLegacy`, parse a tool_result only while an Agent/Task id is pending. Add a fold-equivalence test for each. |
| PF3 | ✔ | **Git line stats spawn `git diff` for every session, including idle ones**, once the 3 s TTL lapses. The 30 s reconcile means about 2 spawns a minute per cwd, all day and while locked; fast-poll gives about 15 a minute. Each result that changes raises `StatsUpdated`, which triggers another scan. (Opt-in: `ShowGitStats`.) | `GitStatsService.cs:30,67-78`; `SessionMonitor.cs:939` | Refresh only when the session is Running or its transcript grew since the last fetch. Raise the TTL to about 15–30 s. Skip while locked. |
| PF4 | ✔ | **The IDE-host detector takes a full Toolhelp snapshot on most scans.** Its 1.5 s TTL is shorter than any scan interval, so the whole process list is marshalled about 30 times a minute in fast-poll. IDE-hosted sessions also make an `OpenProcess` call each scan. A live pid's ancestry never changes. | `Perch.Platform.Windows/IdeHostDetector.cs:20,68-104`; `SessionMonitor.cs:964` | Cache `Detect` per (pid, start time) for the process's lifetime, and snapshot only when an unseen pid appears. |
| PF5 | ✔ | **The git graph rail is rebuilt on every session scan.** `ApplySessions` hands a fresh map to `GraphRail.SetSessions`, which clears and rebuilds every worktree card and every local, remote and stash row with no change check. That's every 2 s while a session works; with 1,000 remote branches, about 4k controls each time. | `GitTreeWindow.cs:1074-1100`; `Views/GraphRail.cs:85-96,168-223` | Return early when `shown` equals the previous map. On a sessions-only change, rebuild only the worktree cards. Later, virtualise or cap the ref lists. |
| PF6 | | **Pollers that don't stop while the desktop is locked or nothing is shown:** <br>• the social feed: about 8–12 HTTPS requests every 60 s, even with Realtime connected (`SocialFeedMonitorHost.cs:79-166`); <br>• `gh pr view` on merged or closed PRs and long-idle sessions (`PrStatusService.cs:167,200`); <br>• the mic monitor: COM endpoint activation plus a ConsentStore registry walk every 2 s (`MicrophoneMonitor.cs:39,64,184-318`); <br>• the usage and status polls (`UsageMonitorHost.cs:144`, `StatusMonitorHost.cs:54`); <br>• the overlay's 1 s tick, which has no `OnScreen()` check and also repaints in compact-rows mode where no elapsed label is drawn (`OverlayCanvas.cs:5812-5831`). | as listed | Gate each on `ISessionLock` as Feeds and GitHub alerts already do. Back the social poll off to about 5 min while Realtime is up. Give finalised PRs a TTL of 30 min or more. Poll the mic's WASAPI side only while something holds the mic. Give the 1 s tick `OnScreen()` and a check that a live label is actually painted. |
| PF7 | | **Paint-convention leftovers.** `UsageBarRenderer` creates 3–4 `new SolidColorBrush` and a `NewText` caption per bar per paint. The dense strip creates a `new SolidColorBrush` per status per paint, plus LINQ counts. A `new Cursor(Hand)` is created per pointer move over the docked column. | `Rendering/UsageBarRenderer.cs:38,50,67,86,90`; `DenseMode.cs:488-515`; `OverlayCanvas.cs:4102` | `OverlayDraw.Brush` / `OverlayDraw.Text(Truncate(…))`, and the existing `HandCursor`. |
| PF8 | ✔ | **Ctrl+F searches on every keystroke with no debounce.** Each search calls `HydrateAll()` plus `UpdateLayout()`, rebuilding every dehydrated item. It then walks the visual tree and allocates a `Border` and 2 brushes per match, twice. A one-letter query is thousands of matches. | `ThreadFindBar.cs:37`; `SessionThreadView.cs:756-858` | First step: debounce about 150 ms and require 2+ characters. The proper fix is PF19. |
| PF9 | | **`AppSettings.Save` (serialize plus fsync, on the UI thread) runs on every keystroke** in the ntfy and Jira text boxes. | `SettingsCatalogView.cs:315-323`, `SettingEditorWindow.cs:85-88` | Coalesce, and save off the UI thread. |
| PF10 | | **`perch-hook`'s `IsPerchRunning` uses `Process.GetProcessesByName`**, a whole-machine snapshot taking 5–20 ms, on SessionStart when auto-start is on. | `Perch.Hook/Program.cs:432-439` | `Mutex.TryOpenExisting("Local\\Perch_SingleInstance")`, plus the `_Dev` name. |
| PF11 | ✔ | **The release publish compresses the single file and doesn't use ReadyToRun.** Compressed bundles inflate every assembly into private memory at load. Avalonia, Markdig and Perch's own code all JIT at startup. Velopack's package is already a zip, so the compression saves little download. | `publish.bat:18-20`, `release.yml:59-61` | Drop `EnableCompressionInSingleFile` and add `PublishReadyToRun=true`. **Measure** cold start and working set before and after: the package gets bigger. |

## Tier 2 — bigger wins (M)

| ID | ✔ | Finding | Where | Fix |
|---|---|---|---|---|
| PF12 | ✔ | **Resuming a session freezes the UI.** `LoadHistory` decodes up to 4,000 lines on the UI thread. Each non-user line is parsed twice (`JsonNode` then `StreamJsonParser`), and image recovery does disk IO. Then the full window builds **every** item at bind, because the newest-first backlog path is `Compact`-only. Each item costs a Markdig parse with file-ref probes; each tool card two input parses; each Edit card a full LCS diff plus about 800 controls. The same full build happens on `/clear`, rewind and every theme change. Estimated 1–4 s for a long session. | `PerchSession.cs:250-258`; `SessionConversation.cs:540-547,636-647`; `SessionThreadView.cs:283` | (a) Parse on the worker: `LoadHistory(IEnumerable<ParsedTranscriptLine>)`, since CP24 already split the parse from the apply. (b) Let `StreamJsonParser` take a parsed `JsonNode`. (c) Use the backlog path (or placeholders that `Realize` hydrates) for the full window too. **Add a resume bench to `render` first.** |
| PF13 | ✔ | **The git graph reloads the whole graph on every file edit, and reloads can overlap.** Any settled watcher event, and the 15 s busy poll, runs the full `GraphLoader.Load`: `git log` for 2,000 commits, refs, stashes, one `git status` per worktree, plus 2 whole-tree diffs for the invisible-change check. `_gen` only discards stale results, so the git work still piles up in GitRunner's queue (cap 4, shared with everything else). It keeps running while minimised, and a refresh throws away loaded history pages. With 20 worktrees that's about 26 git spawns per edit burst. | `GitTreeWindow.cs:1838-1997,2045-2072,2095`; `GraphLoader.cs:34-140`; `GitRepoService.cs:127-135` | Classify watcher events: worktree files get a status-only refresh; `refs/`, `HEAD`, `packed-refs` and `worktrees/` get a full load. Single-flight with a rerun flag. Skip while minimised and catch up on restore. Scope the invisible-change diff to the candidate paths. Reload with `pageSize = max(PageSize, loaded)`. |
| PF14 | ✔ | **Every scan posts a full UI update even when nothing changed.** `Canvas.Update` re-sorts and rebuilds rows, and `RemeasurePanel` runs an extra `Draw(null)` measure, a monitor query and a window resize. There's also the Roost roster/tab sync and per-window activity refresh. That's about 30 times a minute while anything works, an estimated 2–8 ms of UI work each. | `SessionMonitorHost.cs:203-213`; `App.axaml.cs:355-371`; `OverlayCanvas.cs:713-719,1745-1795` | On the worker, fingerprint what the UI reads and skip the post when it's unchanged (still re-arm the deadline and fast-poll timers). In `Update`, skip the resize when the body height is unchanged. |
| PF15 | | **Transcripts are read several times per scan.** Four `TranscriptFold` instances (TranscriptReader, MarkdownFilesReader, SessionWorktree, the legacy SubAgentReader) each read and UTF-8-decode the same appended bytes. The tail readers (activity, burn rate, stuck) each re-read their own 32 KB tail. About 12 `TranscriptLocator.Resolve` calls per session per scan each do a `Regex.Replace` plus `File.Exists`. `ReadConfiguredModel` re-reads up to 3 `settings.json` files on every growth. | `TranscriptReader.cs:26-52,943,1037-1076`; `MarkdownFilesReader.cs:40`; `SessionWorktree.cs:35`; `SubAgentReader.cs:48`; `TranscriptLocator.cs:58-66` | One fold per transcript with all the `LineFolder`s registered (the fold already supports several). One shared tail window per (path, length, mtime). Resolve the path once per session per scan. Cache the settings model by mtime. The equivalence tests from CP20 cover this. |
| PF16 | | **Resource monitor: the whole analysis is rebuilt on the UI thread every 5 s.** `Runaways` is O(processes × ticks), so the cost grows quadratically over a 4 h recording (estimated 50–150 ms per tick late on). Every 30 s it also re-reads each session's whole transcript from byte 0, JSON-parsing lines before the window check. Its own CPU lands in the recording it is analysing. | `ResourceMonitorWindow.cs:187-300`; `ResourceAnalysis.cs:365-378,513-527,611`; `SessionActivity.cs:27-262` | Build off-thread, single-flight. Throttle live rebuilds. Walk each process's own series. Check the timestamp before parsing, or fold incrementally with `TranscriptFold`. |
| PF17 | | **Edit, Write and tool-fold cards are eager.** The LCS diff (up to a 1.6 MB `int[,]`) and the parsed input are recomputed on every hydrate, bind and theme change. The diff renders as about 800 controls. Read/Grep/Glob cards are fully built inside folds that start collapsed. | `SessionThreadView.cs:1113-1131,1512-1517,1932-1992`; `EditDiff.cs:84-97` | Cache the parse and the diff per `ToolCallPart`. Build fold cards on first expand. Render the diff as Runs with painted bands, or show 40 rows plus "show more". |
| PF18 | | **The overlay paint has two remaining hot spots.** The attention chase rebuilds its perimeter samples and clip every frame and makes about 4 × N `DrawLine` calls (roughly 2,500 for a tall panel) at 30 fps for 10 s per alert. Every row's glyphs (IDE, PR, Jira, mail, note, markdown, warn, mode, GitHub, todos) each create a `new StreamGeometry` per paint. | `OverlayCanvas.cs:3432-3938,5574-5578,5952-6040`; `OverlayCanvas.GitHub.cs:187-254` | Cache the samples and clip on (rect, radius, docked). Stroke the base outline once and loop only over the lit tail. Make glyphs `static readonly` geometry drawn under `PushTransform`, as `OverlayCanvas.Dnd.cs` already does. Re-run the overlay bench: the target is well under the current 292 KB/frame. |
| PF19 | | **Find (the proper fix).** Pick candidate items from the model text (`TextPart.Text`, tool results) and hydrate only those. Paint highlights as one viewport-clipped owner-drawn layer with cached brushes. While streaming, re-measure only the changed item's matches. | `SessionThreadView.cs:676,756-858` | — |

## Tier 3 — worth doing when nearby

- **Transcript search rescans the whole corpus on every query** (`TranscriptSearch.cs:58-64`, `TranscriptSearchWindow.cs:204-211`): about 2 GB of strings per query for a 1 GB history. When the new query extends the last one, search only the files that matched before. Search the UTF-8 bytes before decoding. Reuse `ListAll` for the window's lifetime.
- **`FlightPathService.ForDay`** (`:117-155`) fully parses every transcript touched since the chosen day, with no cancellation, and each Left-arrow press starts another scan. Prefilter on creation time and the `"timestamp"` substring, pass a `CancellationToken`, or derive it from `SessionStatsCache`.
- **Cold stats rebuild** (`SessionStatsService.cs:362-369`, `TeamReader.cs:128-135`): an estimated 8–15 s for 1 GB, run on a fresh install and after every fold-version bump. Add a fast path for tool_result-only user lines, and parallelise per file.
- **Startup before `_overlay.Show()`** (`App.axaml.cs:229,487,525-536`):
  - reflection `JsonSerializer` first-use costs for `AppSettings` and four stores;
  - `GitKrakenLauncher.CliPath` probes PATH × PATHEXT (about 480 `File.Exists`), which can hang on an offline network drive.
  
  Move the PATH probe to `Task.Run`, load the stores after Show, and consider a `JsonSerializerContext`.
- **Unbounded per-file caches.** `TranscriptFold._entries`, the `MtimeCache`s, `SessionMonitor._lastPr`, and the PR and git-stats caches grow all day. Evict entries for paths not seen in N minutes.
- **File-ref probes** (`MarkdownView.cs:756-772`, `FileRefResolver.cs:19-20,68`): every inline path does a `File.Exists` on the UI thread. The 5 s TTL and the clear at 1,024 entries make re-hydration re-probe. Use LRU eviction and a longer TTL for settled content.
- **Session window, smaller items:**
  - `BlockSelection.Nearest` walks every block on each pointer move (cache the rects and binary-search);
  - the branch/changes timers restart on every streamed delta (trigger on file-writing tool results only, `SessionWindow.Changes.cs:81-91`);
  - palette and @-mention rows are rebuilt on hover (`SessionWindow.cs:3000,3178`).
- **Diagnostic windows.** `HookActivityView:293-297` and `ResourceTimelineView:680-684` repaint on every mouse pixel. Invalidate only when the hovered target changes.
- **Git graph, smaller items:**
  - `LaneAllocator.cs:69-74` does a `List.Contains` per edge (O(P²) per row);
  - Branch scope runs a serial extra `for-each-ref` (`GraphLoader.cs:102-112`).

## Checked and fine

- **Overlay idle path.** It repaints at most about once a second. Every animation timer stops itself, and the mismatch pulse is `OnScreen`-gated. There's no file IO in any `Render`, and pointer-move invalidations are change-guarded.
- **Owner-drawn views.** `GraphCanvas` and the session list paint only visible rows. `StatsDashboard`, `FlightPathTimeline` and `AchievementsDashboard` repaint only on data or size changes. Roost timers stop on minimise.
- **Background services.**
  - GitHub alerts, Feeds, Todos, Sign-in refresh, Updates, and the Hypertree and Daemon hosts are all throttled, single-flight and, where it matters, lock-gated.
  - The PR glyph caches per (git dir, branch), with at most 3 `gh` runs at once.
  - The resource recorder runs only between explicit Start and Stop. It makes one `NtQuerySystemInformation` call per tick, with no WMI.
- **Live streaming.** Parsing happens on the controller's reader thread and the view batches the updates. The CP22 policies are intact.
- **Session window.** No event or timer leaks were found on close. The @-mention scan runs off-thread and is capped. The transcript search window is debounced and cancellable. The Markdig pipeline and regexes are static or compiled.
- **perch-hook.** It's NativeAOT and uses `Utf8JsonReader`. It handles only 3 events and makes no pipe connects.
- **Build settings.** TieredPGO is on by default. `InvariantGlobalization` and trimming aren't viable: culture-formatted dates, COM interop and the reflection-based `JsonSerializer` and `SalvageMerge` rule them out.

## Progress (branch `perf/review-2026-11`)

| ID | Commit | Notes |
|---|---|---|
| PF1 | 7927c76 | `render` prints a spinner check: 2 hidden spinners animating before, 0 after. |
| PF2 | 7908bc3 | |
| PF3 | 9e93572 | |
| PF4 | dc20ca2 | |
| PF5 | b29a4f3 | |
| PF7 | 65162e5 | Overlay bench now allocates 254 KB/frame (292 KB at the baseline). |
| PF8 | c799c5e, 3e95abe | Debounce only; PF19 is the proper fix. |
| PF9 | a67d6ae | |
| PF10 | cd2675b | Mutex probe 3.4 ms vs 13 ms by name. |
| PF6 | ab3a544 | See below. |
| PF11 | 44ae7b3 | Compression dropped; ReadyToRun rejected. See below. |
| PF12 | 8fa903e | Resume bench added; UI-thread block 5,200 → 21 ms. See below. |
| PF17 | 67ce045 | Cached parse and diff; diff as one text block. Lazy folds not done. See below. |
| PF13 | 9ed40ae | Status-only refresh for edits, single-flight, paused while minimised, scoped diff, pages kept. |
| PF14 | (this commit) | A scan that changed nothing isn't posted to the UI. Resize part already guarded. |

PF6 as built:
- **Usage, service status and the social feed** wait out a locked desktop. A new `Services/LockWatch` samples `ISessionLock` every 5 s and runs each skipped poller's catch-up at unlock. Only the cadence tick is held: a manual refresh, a settings change or a realtime nudge still polls.
- **Merged and closed PRs** are re-checked every 30 minutes, or at the user's interval if that's longer (`PrStatusService.FinalisedTtl`). Open PRs keep polling while locked, because those polls drive the away-from-desk merge and review pushes.
- **The mic monitor** reads WASAPI only while the privacy ledger names a holder (or the last snapshot had one), plus every 5th poll as a backstop. The ledger walk still runs every 2 s.
- **The overlay's 1 s tick** stops when the overlay can't be seen (`Render` restarts it) and doesn't run with compact rows.

Left out on purpose:
- **Backing the social poll off while Realtime is up.** The socket only carries post inserts, so reactions and game state would lag by up to 5 minutes, and the client doesn't expose whether the socket is joined.
- **Lock-gating the mic.** Its `Stop` resets the call-window tracker, so a lock would forget which window holds the call.
- **Pausing PR polls for long-idle sessions.** A review on an idle session's open PR is exactly what the alert is for.

PF11 as measured (2026-11-13, this machine, with CrowdStrike and Defender scanning every launch). Four Release single-file publishes of the same commit were compared:
- **A:** today's compressed bundle;
- **B:** uncompressed;
- **C:** uncompressed with ReadyToRun;
- **D:** compressed with ReadyToRun.

The tray itself wasn't launched: a dev-profile launch rewrites the dev hook marker and reconciles hooks into the declared config dirs. The probes were `--version` (7 runs), `render` (3 runs) and `bench-roost 1` (5 runs), all of which exit before any profile or hook work.

| Variant | Exe | Zipped (≈ download) | `bench-roost` private / WS | `render` private / WS | `--version` median |
|---|---|---|---|---|---|
| A compressed (was shipped) | 50 MB | 44 MB | 107 / 186 MB | 687 / 837 MB | 322 ms |
| **B uncompressed (now shipped)** | 115 MB | **46 MB** | **84 / 145 MB** | **599 / 722 MB** | 610 ms |
| C ReadyToRun | 219 MB | 84 MB | 94 / 156 MB | crashed | 910 ms |
| D ReadyToRun + compressed | 85 MB | 79 MB | 219 / 299 MB | crashed | 785 ms |

- **Compression** only saves 2 MB of download, because Velopack's package is a zip anyway. In exchange it holds every loaded assembly decompressed in private memory for the life of the tray: 23 MB more in the Roost probe and 88 MB more in the render probe. It's now off in `publish.bat` and `release.yml`; the macOS publish never had it.
- **ReadyToRun** is rejected. It nearly doubles the download (+38 MB), and no startup win showed: launch time on this machine tracks file size, because EDR scans the whole exe on each start (see the `--version` medians), and `bench-roost` timing was within its own ±1 s noise. In addition, every `render` run of both R2R builds died with an access violation in Skia's PNG encoder, reached through `CaptureRenderedFrame().Save` in `HeadlessRenderer` (0 of 6 non-R2R runs did). That suggests a lifetime bug in the headless capture path that R2R's different GC and JIT timing exposes. It's test-harness only, and worth a look if the render harness ever crashes on CI.
- The real tray number now lands in `launch.log` on every start (`startup: overlay open N ms after process start`). Compare it across the next release to confirm the cold start didn't regress.

PF12 as built. A resume bench (`Rendering/ResumeBench.cs`, part of `PERCH_BENCH=1 … render`) loads a transcript into a conversation already bound to a full-size thread, as a resumed session window does. It uses a synthetic 400-turn transcript and, if set, the file named by `PERCH_RESUME_TRANSCRIPT`.

| Transcript | Lines → items | UI-thread block before | After | Longest background slice after |
|---|---|---|---|---|
| Synthetic, 400 turns | 4,800 → 800 | 5,200 ms + 46 ms parse | **21 ms** | 41 ms |
| A real 14 MB transcript | 1,710 → 24 | 1,679 ms + 49 ms parse | **187 ms** | 132 ms (one 100-tool turn is one item) |

- **Almost all of the freeze was building every item at bind, not parsing.** Full windows now use the newest-first backlog the Roost's panes already had: the newest 6 items are built at once, and older ones are prepended in ~6 ms background slices that input runs between. This covers resume, `/clear`, rewind and every theme change, because they all rebind. Everything that walks the whole thread (find, jump to top or to the previous prompt, revealing a background task, rewind selection) already flushed the backlog first.
- **The parse moved to the worker** (`SessionConversation.ParseHistory`, then `LoadHistory(parsed)` on the UI thread), and `StreamJsonParser.Parse(JsonNode)` reuses the parse that classified the line, so each line is parsed once. Image recovery runs on the worker too.
- **Still open: the background build costs the full price.** The bench's back-to-back drain is about 7.3 s for the synthetic transcript: older items are built, measured and then mostly dehydrated again by virtualization. Prepending them as estimated-height placeholders that `Realize` hydrates on approach would skip most of that work. `Realize` already corrects the offset when a placeholder above the viewport comes back a different height, but the scrollbar and prompt-jump targets would be estimates until visited. PF17 (cached diffs, lazy fold cards) makes each build cheaper, so do that first and re-measure.

PF17 as built. `perch bench-resume [transcript.jsonl]` runs the resume bench headless. It loads the synthetic transcript whole and then without each kind of tool card, and it times a rebind (what a theme change, `/clear` or rewind does) after each load. Rebind on the synthetic transcript, warmed:

| Variant | Before | After |
|---|---|---|
| Whole | 8.3 s | 5.2 s |
| Without edits | 3.9 s | 3.6 s |
| Without folds | 6.9 s | 4.5 s |
| Real 14 MB transcript: rebind / deferred build / longest slice | 1,084 / 807 / 96 ms | 551 / 504 / 31 ms |

- **Edit cards were the cost**: about 11 ms each for a 50-line diff, against under 2 ms for a fold card. The LCS wasn't the problem; the panel was. Each diff row was a Border, a Grid, a marker TextBlock and a SelectableTextBlock, so a 50-line edit made about 200 controls. `Views/DiffText` is now one `SelectableTextBlock` of coloured runs, with the +/- markers and change bands painted in a gutter and behind it, measured once per text layout with `HitTestTextRange`. An Edit card now costs about 4 ms. Copying works across diff lines and leaves out the markers. `render` gained `session_diff_wrap(_light)`, which covers wrapped and blank changed lines and the MultiEdit separator.
- **The parsed input and the diff are cached on the `ToolCallPart`** (`Input`, `Diff`), so a rebuild after hydrate, rebind or theme change re-derives neither. This matters most for big edits, whose LCS table can be up to 1.6 MB per build. The file-path lookup for a file tool's header reads the cached input too, instead of parsing the JSON again.
- **Fold cards are still built eagerly.** The bench puts their whole share at about 0.7 s across 800 cards, under 1 ms each. Building them on first expand would mean teaching find (which matches hidden text in collapsed folds), expand-all and sticky-header ordering to build on demand. That's not worth it at that price.
- What's left of the resume cost is ordinary text layout, about 2.5 s for 800 prose-only items, and only the placeholder idea above avoids it.

PF13 as built (git graph window):
- **Watcher events are classified** (`RepoWatchFilter.ChangesGraph`). A working-file edit, or a staging write to `index`, can't move commits or refs, so it gets a status-only refresh. That's one `git status`, plus the invisible-change check scoped to the candidate files and a repaint of the shown file if its content changed. If the change set itself moved (a file appeared, left, or moved between staged and unstaged), it falls back to the full reload, which is the only path that rebuilds the WIP row and the worktree counts. HEAD, refs, packed-refs, worktree add/remove and merge/rebase markers reload in full, as before. For scale, on this repo with 1 worktree a warm full reload's git stage takes about 227 ms, plus two whole-tree diffs; each extra worktree adds a `git status`.
- **Single-flight.** One automatic refresh runs at a time. Requests arriving meanwhile collapse into one rerun of the strongest kind asked for, so a burst of edits can't queue git work in GitRunner (which is shared with everything else).
- **Paused while minimised.** Watcher and busy-poll refreshes are owed while the window is minimised and run once on restore.
- **The invisible-change check diffs only its candidate paths** (`GetWorkingTreeDiff(cwd, staged, paths)` with `:(top,literal)` pathspecs, so it works from a subfolder and a file name like `[x].txt` matches only itself). A path list too long for a command line falls back to the whole tree.
- **A reload keeps the pages already loaded** (`pageSize = max(PageSize, loaded)`), so scrolled-in history no longer vanishes on a refresh.

PF14 as built:
- **`SessionListComparer` compares scans on the scan worker.** It uses record equality with the four list fields swapped for a shared empty array, then compares the lists element by element, recursing into sub-agent children. When a scan matches the last one posted, only the deadline and fast-poll timers are re-armed on the UI thread. The overlay update, Roost sync, session-window activity mirroring and the rest are skipped.
- **Explicit requests always post.** That covers `Rescan`, `Reconcile`, and the acknowledge, notify and note changes queued through `OnWorker`. Some of them change what the App adds to the list (a dormant row ending) and rely on the callback to redraw. The force flag is consumed when a scan starts, so the scan that runs after the request is the one that posts.
- **Checked against live sessions:** two scans of this machine's real `~/.claude` 4 s apart (6 sessions, 4 running) came back equal for 5 of them. The sixth genuinely differed: its sub-agent's activity had moved on. A throwaway verb printed the per-field differences; it was not committed.
- **The resize half needed nothing.** `SizeFloatingToContent` already writes the window height only when it differs. The measure pass stays, because a posted update by definition changed something.

## Suggested order

1. **Batch A (one sitting, all S):** PF1, PF2, PF3, PF4, PF5, PF7, PF8 (the debounce), PF9, PF10. Verify with the existing render bench, the fold equivalence tests and a fresh PERCH_BENCH run.
2. **Batch B (lock and visibility gating):** PF6.
3. **Measure, then PF11:** compare cold start and working set of a Release publish with and without compression and ReadyToRun.
4. **PF12 resume**, behind a new resume bench in `render`, then PF17 (cached diffs, lazy fold cards).
5. **PF13 git graph** refresh split, then PF14 (skip no-op scan posts) and PF15 (a single fold).
6. Then PF16, PF18 and PF19, and Tier 3 as each area is touched.
