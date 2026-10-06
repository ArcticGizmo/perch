# Session recovery — design & checkpoint plan

Status: **R0–R8 built** (2026-10-02), on branch `session-recovery` (cut from `main`), one commit per checkpoint.
Everything is unit-tested and render-verified; the live checks listed under each checkpoint are still owed. The
spike that grounds it (what Claude Code leaves on disk for each way a session can end) is written up below.

## The ask

1. A **Recent** list of sessions that have ended, which **specially marks the ones that closed abruptly**.
2. **Perch-controlled sessions that closed abruptly**, and the ones **still running when Perch itself closed**
   (an update, Exit, a crash, a restart), come back **at once** in the floating overlay and the Roost.
3. **Terminal sessions** don't come back by themselves. They're listed, and the user can **recover** them.
4. A session that ended **within 10 minutes of a device shutdown/restart** is marked **"just before shutdown"**.
   This covers the "close everything at the end of the day, then restart" habit, where every session ends
   gracefully but is still work in progress.
5. **Dormant sessions, everywhere.** Every session Perch opens shows its conversation straight away but **only
   binds to `claude` when the user sends something new**. That also makes the resume token/cost warning far
   less critical: nothing is spent by just opening a session to read it.
6. Prefer **what Claude Code already records** over new Perch marker files.

## What Claude Code records (the spike)

Tested 2026-10-02 against Claude Code 2.1.287 on Windows 11, with a throwaway session per way of ending it.

| How the session ended | Transcript tail | `SessionEnd` hook ran | `.claude.json` `lastSessionId` | `history.jsonl` |
|---|---|---|---|---|
| `/exit` | `cost-state` | yes | — | **`/exit` entry** (with `sessionId`, `project`, `timestamp`) |
| Ctrl+C ×2 | `cost-state` | yes | — | — |
| Ctrl+D | `cost-state` | yes | — | — |
| Close the Windows Terminal **tab** | `cost-state` | yes | updated | — |
| Close the Windows Terminal **window** | `cost-state` | yes | updated | — |
| **Restart Windows** (Fast Startup on) | **no `cost-state`** — last record is the turn's `turn_duration` | **no** (sidecars left behind) | not updated | — |
| Perch-controlled session (`entrypoint: "sdk-cli"`) after Perch went away | **no `cost-state`** | **no** | — | — |

What that means:

- **`cost-state` is Claude's "I exited cleanly" record.** It is appended when the process shuts down gracefully
  and never during a live session (a long live transcript has none). It carries `startTime` and `totalDuration`
  (ms), so **`startTime + totalDuration` is the exact end time**. A `cost-state` in the *middle* of a file just
  means the session exited and was later resumed; only the tail matters. *(Correction from R1: `startTime +
  totalDuration` is only right for a session that was never resumed, since a resume keeps the original
  `startTime` and adds to the duration. The file's last-write time is the exact end instead; see D7.)*
- **Abrupt = the process is gone and the transcript's last record is not `cost-state`.** No marker file needed.
- **Only `/exit` is distinguishable** among the graceful endings (via `history.jsonl`). Ctrl+C, Ctrl+D, closing
  the tab and closing the window all look identical on disk, which is fine: they're all "closed", not "abrupt".
- **A Windows restart is abrupt.** Claude is killed before it can write `cost-state` or run `SessionEnd`.
- **`sessions/{pid}.json` is no evidence.** Claude Code sweeps stale ones itself (it records `procStart` and
  `pidDomain` for exactly this), so an abruptly-ended session leaves no session file behind for long.
- **Perch's own `{sid}.configdir` / `.mode` sidecars surviving** also means "`SessionEnd` never ran". That's
  corroboration only; the transcript tail is the rule.

### The boot-time gotcha

After the restart test, Windows still reported `LastBootUpTime` as **11 days earlier**, and no classic shutdown
events (1074 / 6006) were logged. The boot type was `0x1`: **Fast Startup**, where the kernel hibernates instead of
shutting down, so boot time and uptime never reset. Only `Kernel-Power` 42 ("entering sleep") / 107 ("resumed")
and `Kernel-Boot` 27 ("boot type") were logged. **Boot time can't be used to find the shutdown.** Perch has to
record it itself (D5).

### Gotcha for anyone repeating the spike

`~/.claude/.gitignore` is `*`, so a ripgrep/Grep over a **directory** under `~/.claude` silently skips every
file in it. Search individual files.

## Decisions

| # | Question | Decision | Why |
|---|----------|----------|-----|
| D1 | What does "abrupt" mean? | **Process gone + transcript tail isn't `cost-state`.** For Perch-controlled sessions, Perch's own ledger decides instead (D3). | The spike: every graceful ending writes `cost-state`; a restart and a killed process don't. It's Claude's own record, so no new marker files (ask 6). |
| D2 | Kinds of ended session | `Exited` (graceful + `/exit` in `history.jsonl` after the session's last activity), `Closed` (graceful, no `/exit`), `Abrupt`. Orthogonal flag: `JustBeforeShutdown`. | `/exit` is the only deliberate "I'm done" signal Claude records. The shutdown flag applies to any kind (ask 4). A `/exit` older than a later resume doesn't count. |
| D3 | Which sessions come back **automatically**? | **Perch-controlled sessions that were still open when Perch closed** (any reason), or that ended abruptly. They come back **dormant** (D4), in their old overlay row and Roost slot. Sessions the user explicitly **ended** in Perch don't. | Ask 2. Perch knows exactly which of its sessions were open, so it doesn't need the transcript rule for its own sessions. |
| D4 | What is "dormant"? | A session shown with its **conversation loaded and a live composer, but no `claude` process.** The **first send** starts `claude --resume <id>` and delivers the message once it's up. Applies to **every** session Perch opens (launcher recents, Recent list, CLI `perch --resume`, restored sessions). | Ask 5. Opening to read costs nothing, so the cost estimate becomes an inline note at the point of sending instead of a gate before opening. |
| D5 | Shutdown time — where from? | In order: **(1)** Perch's own stamp when the OS says it's shutting down / logging off; **(2)** the OS power history (Windows event log, `Kernel-Power` 42 / `Kernel-Boot` 27) after the previous run was last alive; **(3)** Perch's last heartbeat, only on a head with **no** power history and only when Perch didn't exit cleanly. | Fast Startup makes boot time useless (spike). The stamp is exact. **Refined in R2:** a missing stamp plus a stale heartbeat can't tell a power-off from a Perch *crash*, so the power history outranks the heartbeat wherever it exists (Windows); the heartbeat stays the fallback where it doesn't (the macOS stub). |
| D6 | "Just before shutdown" window | **10 minutes, a constant** (`SessionRecovery.ShutdownWindow`). It becomes a setting only if it ever feels wrong. | The user's call. One constant is easier to tune than a setting nobody touches. |
| D7 | End time of an ended session | Graceful: the transcript's **last-write time** (the exit flush is its last write). Abrupt: the last transcript record's `timestamp`. | **Corrected in R1:** `cost-state`'s `startTime + totalDuration` only works for a session that was never resumed. A resume restores the original `startTime` and keeps adding to `totalDuration`, so a resumed session's figure lands hours off (one real session: ~16h before its file's last write). In the restart test the last record was 11s before the power event. |
| D8 | Terminal sessions | **Listed, never auto-resumed.** Each row offers **Resume in Perch** (opens dormant), **Resume in terminal** (`ISessionLauncher.Reopen`, which already exists and honours the config dir) and **Dismiss**. | Ask 3. The user keeps their terminal habit; Perch just remembers. |
| D9 | Perch's own exits end their sessions **gracefully** | Every exit path (Exit menu, update, OS shutdown) calls `PerchSession.End()` (stdin close, kill after 3s), not just the update path. | Today only the update path ends controlled sessions; plain Exit doesn't. A graceful end lets the user's own `SessionEnd` hooks run (and `cost-state` get written — R0 confirms). |
| D10 | Where the record lives | A small **`session-ledger.json`** in Perch's profile folder (beside `settings.json`), written atomically (`AtomicFile`) and debounced. **Not** `AppSettings`. | It changes every heartbeat and on every live-set change; churning `settings.json` for it would be wrong. |

### Follow-up decisions (user review 2026-10-02, all confirmed)

| # | Question | Decision |
|---|----------|----------|
| Q1 | How far back does Recent go, and how many rows? | **3 days, 5 rows.** Abrupt + "just before shutdown" sort first; "More…" opens the launcher's full recent list. The Roost rail shows the same 5. |
| Q2 | Is Dismiss remembered? | **Yes**, as a list of session ids (`AppSettings.RecentDismissed`, a `NotSettings` UI state), pruned to the 3-day window. A dismissed session that is resumed and **ends again reappears** (it's a new ending). |
| Q3 | Restored Perch sessions that weren't in any Roost tab | **Rail only** (plus their overlay row). Nothing is pushed into Focus or a tab: placement stays the user's (Roost tabs D2). Sessions that *were* in a tab go back to their slot. |
| Q4 | Which ended sessions are in Recent? | **All three kinds**, with different treatment: `Abrupt` and "just before shutdown" badged and first, `Closed` unbadged, `Exited` de-emphasised. Recent reads as "what you were doing lately". |
| Q5 | Should a dormant session start `claude` before the first send? | **No, not in v1.** It starts on send only. R4 measures the first-send delay; an early start (e.g. on first keystroke) is added later only if the delay is annoying. |

## Building blocks

### Core (`Perch.Core`, UI-free, tested)

- **`SessionEndReader`** — reads a transcript's tail (reusing `SessionHistory`'s existing tail read) and returns
  `(EndKind Kind, DateTime? EndedAt)` from the D1/D7 rules: tail `cost-state` → graceful, end =
  `startTime + totalDuration`; otherwise the last record's `timestamp`. Tolerates a partial trailing line (the
  usual "parse defensively, never throw" rule). *(As built, the end time follows the corrected D7.)*
- **`ExitCommandIndex`** — the `/exit` entries from `history.jsonl` (`sessionId → last /exit timestamp`), read
  **incrementally** (it keeps its byte offset; the file is large and append-only) and per config dir.
- **`SessionLedger`** — Perch's own record (D10): Perch's start time, heartbeat (`LastAlive`), clean-exit stamp,
  shutdown stamp, and for each **Perch-controlled** session: id, cwd, config dir, title, open/ended-by-user,
  Roost placement token. Pure state + a serializer; the App owns the file IO and the timer.
- **`ShutdownClock`** — resolves "when did the device last shut down?" from D5's three sources (the third via a
  platform interface). Pure, with a fake clock in tests.
- **`RecentSessions`** — joins `SessionHistory.ListAll`, `SessionEndReader`, `ExitCommandIndex`, the ledger and
  `ShutdownClock` into the ranked Recent list (Q1), applying dismissals (Q2). One place both the overlay and the
  Roost read.

### Platform interfaces

- **`IShutdownSignal`** — raises an event when the OS is shutting down / logging off, early enough to stamp the
  ledger. Windows: `WM_QUERYENDSESSION` / `WM_ENDSESSION` (or `Microsoft.Win32.SystemEvents.SessionEnding`; R0
  checks whether Avalonia's `ShutdownRequested` already covers it). macOS: `NSWorkspaceWillPowerOffNotification`
  (stub first).
- **`IPowerHistory`** — the D5 fallback: the most recent shutdown before Perch's start. Windows: the System event
  log (`Kernel-Power` 42, `Kernel-Boot` 27), readable without admin. macOS: stub returning null.
- **`ISessionLauncher.Reopen`** — **already exists** (`HandBackToTerminal` uses it). "Resume in terminal" reuses it.

### App

- **Dormant session window** — `SessionWindow` gains a dormant mode (D4): thread rendered from the transcript
  (the same `SessionThreadView` path the history viewer uses), composer live, no `PerchSession`. First send →
  the existing `StartSession` resume path (trust check and collision defences included), then the queued message.
  The resume estimate (`ResumeEstimate`) moves from the launcher gate into a one-line note above the composer.
- **Overlay "Recent" section** — a new movable section (`OverlaySection.Recent`), following the standard
  collapsible-section pattern (chevron header, persisted expand bool) and the section-order rules in `CLAUDE.md`.
  **Superseded (2026-10-05):** a section was too noisy for anyone running many sessions at once, so it became a
  **Recent button** (a clock glyph) on the "+ New session" row, beside the Roost button
  (`OverlayCanvas.Recent.cs`). It opens a flyout (`Views/RecentListView`) with filter chips: **All**,
  **Interrupted** (`SessionEndKind.Abrupt`, plus "was open" — Perch closing ended those, not the user) and
  **Before shutdown** (`JustBeforeShutdown`; a restart's victims are under both). It lists every line, not the
  top 5; "Show all…" opens the launcher. A warning-hue dot on the glyph marks flagged lines not yet seen in the
  flyout (runtime only). `OverlaySection.Recent` and `AppSettings.RecentExpanded` are gone; `ShowRecentSessions`
  now gates the button.
- **Roost** — dormant panes in their old slots, and a "Recent" group in the rail (R6). **Superseded (2026-10-05):**
  the group took a lot of the rail, so the dormant panes moved behind a **"◷ Recent" row in the rail footer**
  (`RoostWindow.Recent.cs`), above "N hidden", that opens the same `RecentListView` flyout (Roost-styled via
  `RecentListLook.For(SessionPalette)`, no "Show all…"). A click focuses the pane (its tab, or Focus); `>_` and `×`
  are the pane's Resume in terminal / Dismiss. Only a dormant pane some tab holds (Focus included) stays in the rail,
  under **NOT RUNNING**. `RoostDormant.JustBeforeShutdown` carries the shutdown flag past an `Interrupted` kind, so a
  restart's victims show under both filters. The dormant set itself (restorable → dormant windows → tab-held →
  Recent top 5) is unchanged.

## Checkpoints

Each checkpoint is one commit with a green gate: both heads build, `dotnet test` passes, and the headless render
is checked for anything owner-drawn.

### R0 — probes

Probes 1 and 2 were run with a script that drives `claude` exactly like `ClaudeSessionController` (same flags,
`--model haiku`, one "hi", wait for `result`). Probe 3 is half done: the source answers it, a live restart
confirms it.

1. ✅ **A Perch-controlled (stream-json) session ended gracefully writes `cost-state`.** Closing stdin
   (`ClaudeSessionController.Stop`) → `claude` exited with code 0 after **2.5s**, transcript tail `cost-state`,
   `SessionEnd` ran (sidecars cleaned). So D1's transcript rule works for controlled sessions too; the ledger is
   still what says "this was open when Perch closed".
2. ✅ **Perch exiting without ending its sessions is also graceful.** The parent exited with the child's pipes
   open; the OS closed them, `claude` saw EOF and shut down cleanly (`cost-state`, `SessionEnd` ran). No job
   object ties `claude` to Perch. The `sdk-cli` sessions found without a clean exit date from 17 Sep, before
   the last full boot: they don't reflect today's code.
   - **Risk found:** `Stop()` kills the process tree after **3s**, and a bare Haiku session already took 2.5s to
     exit. With MCP servers or slow `SessionEnd` hooks it can pass 3s and get killed (→ abrupt). R5 raises the
     timeout (~10s), since `Stop` runs off the UI thread anyway.
3. 🟡 **Avalonia raises `ShutdownRequested` on an OS shutdown/logoff**, from source (Avalonia 12.0.5):
   `Win32Platform` handles `WM_QUERYENDSESSION` on its hidden top-level window (not message-only, so it does get
   the broadcast) and the lifetime raises `ShutdownRequested`, closes windows, then `Exit`.
   `ShutdownRequestedEventArgs.IsOSShutdown` is `internal`, but it isn't needed: **Perch's own
   `desktop.Shutdown()` is forced and skips `ShutdownRequested` entirely** (straight to `Exit`). So in Perch,
   `ShutdownRequested` ⇔ OS shutdown/logoff, which makes `IShutdownSignal` on Windows a thin wrapper over it.
   - **Existing bug found:** because `Shutdown()` skips `ShutdownRequested`, the cleanup Perch hangs on it
     (disposing the monitor hosts, `ReleaseDocked`, `DisposeDense`, the replay sandbox) **never runs on a normal
     Exit**, only on an OS shutdown. It belongs on `desktop.Exit`, which fires on both paths. Fixed in R5
     (it's the same exit-path work as D9).
   - **Live check owed:** a probe in `App` logs `shutdown requested (OS shutdown/logoff)` and `lifetime exit` to
     `launch.log`. Run the **dev** build, restart Windows, and compare the timestamps with the power events
     (Kernel-Power 42) and with a terminal session's last record: that says how much time there is to stamp.
4. ⏳ **Restart with Fast Startup off** (a full boot): does anything change (1074/6006 logged, Claude given more
   time)? Only affects the event-log fallback. Owed by the user.

**Event-log fallback, observed:** every Fast Startup shutdown in the last three days logged the same pattern —
`Kernel-Power` 42 "entering sleep", 107 "resumed" seconds later, then `Kernel-Boot` 27 "boot type 0x1" at the next
power-on. So "the last 42 before the latest 27" is the shutdown time when Perch wasn't running.

### R1 — reading Claude's records (Core)

`SessionEndReader` + `ExitCommandIndex`, with fixtures under `tests/Perch.Tests/fixtures/claude/`: a transcript
ending in `cost-state`; one with a mid-file `cost-state` and later turns (resumed, then killed); one ending
mid-turn; one with a torn last line; a `history.jsonl` with an `/exit` before and after a resume. The
`history.jsonl` reader is tested for incremental reads (append, re-read only the new bytes).

**As built (R1):**
- `Perch.Core/Data/SessionEnd.cs`: `SessionEndKind` (`Unknown`/`Closed`/`Exited`/`Abrupt`), `SessionEnd(Kind,
  EndedAt)`, and `SessionEndReader`. `Read(path, lastExitCommand)` scans a 64KB tail, growing ×4 up to 1MB only
  when no record parses (one huge last record). The rule: a `cost-state` after the last **timestamped** record is a
  clean exit; untimestamped bookkeeping (`last-prompt`, `mode`, `permission-mode`) is ignored either way; a torn
  last line is abrupt. An `/exit` counts when it's at or after the last activity, so one from before a resume
  doesn't. `Scan` and `Classify` are exposed internally for tests.
- `Perch.Core/Data/ExitCommandIndex.cs`: one per config dir (`ClaudeConfigDir.HistoryFile` added). `Refresh()`
  reads from its byte offset to the **last complete line** only, restarts from 0 when the file shrank, pre-filters
  lines on `/exit`/`/quit` before parsing, matches the whole `display` (so "don't /exit yet" and `/exit-plan`
  don't count), skips entries without a `sessionId`, keeps the newest per session. The first read of a large
  history loads it once; later reads only the appended bytes.
- Fixtures in `tests/Perch.Tests/fixtures/session-end/` (outside the `claude/` tree so `SessionHistory.ListAll`
  tests don't see them): `graceful`, `killed-mid-turn`, `resumed-then-killed`, `torn-tail`. Tests:
  `SessionEndReaderTests` (9), `ExitCommandIndexTests` (6).

### R2 — Perch's ledger and the shutdown clock (Core)

`SessionLedger`, `ShutdownClock`, `RecentSessions`, and the `SessionRecovery.ShutdownWindow` constant. Tests: the
D5 fallback order, the 10-minute boundary, a hard power-off (heartbeat only), "Perch wasn't running", dismissals,
the 3-day window.

**As built (R2):**
- `SessionRecovery` (constants): `ShutdownWindow` 10 min, `ShutdownGrace` 2 min (a session's exit flush can land
  just *after* Perch's stamp, since the OS closes apps in no fixed order), `RecentWindow` 3 days, `RecentRows` 5,
  `HeartbeatInterval` 60s. `JustBeforeShutdown(endedAt, shutdowns)` checks against **every** recent shutdown, not
  just the last, so an older evening's sweep stays flagged for the whole window.
- `Perch.Platform.IPowerHistory` (`IsSupported`, `LastShutdownBetween(after, before)`): the Core interface R3
  implements. Defined now so `ShutdownClock` can be tested against a fake.
- `ShutdownClock.Resolve(previousRun, power, now)`: the refined D5 order. The power history is asked for a
  shutdown after the previous run was last alive, minus one heartbeat of slack (heartbeats can still land while
  the OS shuts down). No previous run (first launch) → the latest shutdown in the Recent window.
- `SessionLedger` (`session-ledger.json` beside `settings.json`, `DefaultPath`): `Run` (`StartedAt`, `LastAlive`,
  `CleanExitAt`, `ShutdownAt`), `Shutdowns` (deduped within a heartbeat, pruned to 3 days), `Sessions`
  (`LedgerSession(SessionId, Cwd, ConfigDir, Title)`). `BeginRun(now, power)` resolves and records the shutdown
  that ended the previous run, then resets the stamps; held sessions carry over (they come back dormant).
  `Heartbeat`/`MarkCleanExit`/`MarkShutdown`, `Track` (upsert), `Forget` (user ended it), `Rebind` (`/clear`).
  `Load` never throws (missing/corrupt → empty); `Save` is atomic. **The App must not save it from the headless
  render or tests** (same rule as `AppSettings.DisablePersistence`): R3 wires that.
  Roost placement isn't in `LedgerSession` yet: R6 adds it with the dormant pane token.
- `RecentSessions.Build(entries, heldByPerch, dismissed, shutdowns, now)` → `RecentSession(Entry, End,
  JustBeforeShutdown)` with `EndedAt` and `IsFlagged` (abrupt or just before shutdown). Flagged first, then newest.
  Leaves out running sessions, Perch's held ones, endings older than 3 days, and dismissed endings (`dismissed` maps
  an id to the end time it was dismissed at, so a later ending reappears — Q2). One `ExitCommandIndex` per config
  dir, found from the transcript path (`{root}/projects/{enc}/{id}.jsonl` → `{root}/history.jsonl`), refreshed
  once per build. `AppSettings.RecentDismissed` (the persisted dismissals) lands with the UI in R7.
- Tests: `ShutdownClockTests` (11, incl. the window boundaries), `SessionLedgerTests` (6), `RecentSessionsTests`
  (5, over a throwaway config dir built from the R1 fixtures).

**Real-data check (R2, read-only, not committed):** run over a real `~/.claude` (591 transcripts, 64 ended in the
last 3 days), every R0 spike session classified as observed (the restart → `Abrupt` + just before shutdown;
`/exit` → `Exited`; Ctrl+C / Ctrl+D / tab / window / both stream-json probes → `Closed`). Fed the last three days'
power events (`Kernel-Power` 42) as shutdowns, it flagged **15 sessions across four evenings**: each an end-of-day
burst of `/exit`s 1–9 minutes before the machine went down. In practice the habit the feature targets ends with
`/exit` more than with closing windows, and the shutdown flag is what brings those sessions back.

### R3 — platform signals

`IShutdownSignal` + `IPowerHistory`, Windows implementations, macOS stubs, `PlatformServices` wiring. The App
stamps the ledger on the signal, runs the heartbeat (every 60s plus on every live-set change), and writes a
clean-exit stamp on a normal Exit.

**As built (R3):**
- **No `IShutdownSignal` interface.** R0 showed Avalonia's own `ShutdownRequested` *is* the OS-shutdown signal on
  Windows (Perch's `desktop.Shutdown()` never raises it), so the App stamps the ledger there and a wrapper would add
  nothing. It's **gated to Windows**: on macOS Avalonia raises the same event for an ordinary Cmd+Q / Dock "Quit"
  (`applicationShouldTerminate`), so there a quit stays a plain exit until the port adds
  `NSWorkspaceWillPowerOffNotification` (behind an interface, when it does).
- `Perch.Core/Data/PowerTimeline.cs`: the pure "events → shutdowns" rule, in Core so it's tested on both CI hosts.
  A shutdown is a `SleepEntered` (Kernel-Power 42, how a Fast Startup shutdown logs) or `ShutdownStarted` (User32
  1074 / EventLog 6006) that a `Boot` (Kernel-Boot 27) follows. A `Resumed` (Kernel-Power 107) more than 2 minutes
  after a 42 makes it an ordinary sleep; a Fast Startup shutdown resumes within seconds. When a 1074/6006 and a 42
  precede one boot, the earlier 1074 (when the shutdown began) wins.
- `Perch.Platform.Windows/PowerHistory.cs`: `EventLogReader` over the System log with an XPath filter on those ids
  and the time window (read runs to now, so the boot after a shutdown is seen), matching **provider as well as id**
  (a network driver also logs id 27 at every power transition). Needs the `System.Diagnostics.EventLog` package
  (Microsoft, same 10.0.9 family as the project's other compat packages). `Perch.Platform.Mac/PowerHistory.cs`: stub,
  `IsSupported = false`. Exposed as `PlatformServices.PowerHistory`.
- `Perch.App/App.Recovery.cs`: `StartSessionLedger` (startup, off the UI thread: load, `BeginRun` with the power
  history, save, log the shutdown it found to `launch.log`), then a 60s heartbeat (save off the UI thread).
  `StampLedgerShutdown` on `ShutdownRequested` and `StampLedgerExit` on `Exit` both save **synchronously** (the
  process may not get another chance); a shutdown-stamped run doesn't also get a clean-exit stamp. Skipped entirely
  when `AppSettings.PersistenceDisabled` (render, tests) or under replay. The heartbeat on live-set changes waits for
  R5, when the ledger starts holding sessions.
- Tests: `PowerTimelineTests` (6), over the real event sequences from R0.
- **Real-log check (read-only):** a scratch file-based program calling `PowerHistory` against this machine's System
  log found exactly the seven shutdowns of the last four days that the R0 event dump showed, at the same times.

### R4 — dormant sessions everywhere

`SessionWindow` dormant mode; every "open a session" path (launcher recents, CLI `--resume`, the overlay, the
Roost) opens dormant. The first send starts the process and delivers the message. The estimate becomes the
composer note. **Measure** the first-send delay (Q5).

**As built (R4):**
- **Dormant is a state of `PerchSession`, not a second type.** `PerchSession.Dormant(options)` loads the transcript
  history (the existing `LoadHistoryAsync`) with no controller; `IsDormant` is true (not running, not ended).
  `Wake(options)` spawns `claude --resume <its id>` with the model/mode/effort/account chosen since it opened,
  keeps the already-loaded conversation, adds the "resumed session" note and raises `Woke`. `Start` now shares the
  spawn code (`Launch`). One object means the window, the app's session list and (later) the Roost all keep
  dealing with one session.
- **`SessionWindow`:** `ResumeSession` (CLI / elevate), `ResumeReplace` (/resume "resume here") and the launcher's
  recents all go through `OpenDormant`, which calls the app's new `DormantRequested` factory and attaches the
  result (no factory → starts at once, the old way). The first send sets `_sendAfterWake` and runs
  `StartSession`, so **the trust check, the live-in-a-terminal refusal and the `.perch-lock` check all run at wake,
  exactly as for any resume**; its dormant branch calls `Wake` and then re-sends. The text stays in the composer
  until it actually goes, and `LaunchFail` drops the pending send, so a refused wake loses nothing. Native
  commands (`/usage`, `/theme`, …) still run without waking. `CanCompose` (live **or** dormant) replaces the
  `IsRunning` checks that gated the composer, focus, the command palette and `@`-mentions.
- **The estimate moved** from a "Resume anyway?" dialog at open (deleted, with `HeavyResumeThresholdPercent`) to a
  one-line note at the top of the composer: "Claude starts when you send · ↩ ≈142k in · cache cold · ≈2.8% of 5h
  · ≈$0.89", amber when the cache is cold, with the full tooltip. Computed off the UI thread
  (`ShowDormantEstimate`, generation-guarded).
- **App:** `OpenDormantPerchSession` registers dormant sessions in `_perchSessions`, returning the existing one for
  an id already open, so a second open can't create a twin that would wake into a second writer. When a session
  window closes, dormant sessions no window views any more are dropped (swept, since the window has already
  detached by `Closed`); a woken one outlives its windows as before. `PerchSessionFor` (Roost pane bindings and
  actions) and overlay-row focus **skip dormant sessions**: if the same id is live in a terminal, that live
  session is what they mean.
- **Kept as-is:** a brand-new session (launcher "Start", `perch [dir]`) still starts at once, since there's no
  conversation to show. The ended-session **Resume** button still starts at once (an ended session already closes
  its window on End, so that path is rare). An abrupt session's unfinished tool shows as done, not running:
  `LoadHistory` already settles it.
- **Render:** `session_dormant_1x.png` / `session_dormant_light_1x.png` (`FeedDormantSampleForRender`).

**Owed live checks (R4):**
- Open a recent session from the launcher: the conversation shows, no `claude` process starts (Task Manager), the
  note shows the estimate. Send: claude starts, the message goes, the reply streams in. Time the gap (Q5).
- Same from the CLI (`perch --resume <id>`) and from the /resume overlay ("resume here").
- Wake refusals: a session live in a terminal; an untrusted folder (decline the dialog) — the text stays in the
  composer each time.
- Open the same id twice (launcher, then CLI): one window, not two.
- Close a dormant window, then open the same id again: it reloads cleanly.
- Elevate a terminal session to Perch: it opens dormant now, so the Roost pane only follows once you send (R6
  gives it a dormant pane).

### R5 — Perch's own exits

D9: every exit path ends controlled sessions gracefully and records them as "open at exit" in the ledger. Move
Perch's exit cleanup from `ShutdownRequested` to `desktop.Exit` (R0.3: it never runs on a normal Exit today), and
raise `ClaudeSessionController.Stop`'s kill timeout from 3s to ~10s (R0.2).

**User decision (2026-10-02):** restored Perch sessions appear at the **top of the overlay's Recent section** with a
"was open in Perch" marker (R7), not as dimmed rows in the Sessions section. No synthetic row kind in the Sessions
section, the dense strip or the settings preview.

**As built (R5):**
- **Teardown moved to `desktop.Exit`.** `ShutdownRequested` now only logs and stamps the OS shutdown; everything it
  used to dispose (monitor hosts, hotkeys, session lock, dense strip, replay sandbox) runs on `Exit`, which fires on
  both the forced `Shutdown()` (Exit menu, auto-close) and an OS shutdown. **This fixes a live bug**: a normal Exit
  skipped all of it. The docked edge (an AppBar, removed by window handle) is released on the overlay window's
  `Closing`, since the lifetime closes every window before `Exit`.
- **Every exit ends Perch's sessions cleanly**: `Exit` calls `End()` on each (stdin close; each `claude` writes its
  exit flush and runs `SessionEnd`, the user's hooks included). `ClaudeSessionController.Stop`'s kill grace is now
  10s (`StopGrace`), up from 3s.
- **Holding sessions** (`App.Recovery.cs`): a session is held when it starts (`StartPerchSession`) or a dormant one
  wakes. Held = `SessionLedger.Track` (re-tracked on a `/rename`), `Rebind` when `/clear` gives the process a new
  id, and `Forget` only on **`PerchSession.Released`**: the user's confirmed "End session" (`EndByUser`) or "Hand
  back to a terminal". Perch exiting, an update, a shutdown or a `claude` crash don't release, so those come back.
  A dormant session that's opened but never woken isn't held (nothing was running).
- **Restoring**: once the ledger has begun, the previous run's held sessions (minus any this run already holds)
  become `RestorableSessions`. A startup toast says how many, and a tray item "Reopen Perch sessions (N)" (hidden
  when there are none) opens each one dormant in its own window. Reopened-but-ignored ones stay in the ledger and
  come back next time; R7's Dismiss is what lets one go. R6 adds them to the Roost; R7 to the top of Recent.
- **A cancelled shutdown un-stamps itself**: the heartbeat keeps running after `ShutdownRequested`, and a tick that
  finds a shutdown stamp means Perch is still alive, so it calls `SessionLedger.CancelShutdown()` (an unsaved note,
  another app or the user can cancel a Windows shutdown after Perch has been asked).
- **Ledger saves are serialised** (a gate around serialise-and-write), so a later save always writes the later
  state; each runs off the UI thread except the two exit stamps, which must land synchronously.
- Tests: `SessionLedgerTests.ACancelledShutdown_IsNotRecordedNextRun`.

**Owed live checks (R5):**
- Start two Perch sessions, Exit Perch from the tray: both `claude` processes exit within seconds (no kill), their
  transcripts end in `cost-state`. Start Perch: the toast, then "Reopen Perch sessions (2)" opens both dormant.
- End one with "End session" before exiting: only the other comes back.
- Docked mode, then Exit: the reserved edge is released (maximised windows reclaim it) — the fixed bug.
- Update Perch with a session open: it comes back after the restart.
- Restart Windows with a Perch session open: it comes back, and the restart's sessions are flagged in R7. On the
next start they come back dormant: overlay rows (with a dormant style) and their old Roost slots. Ended-by-user
sessions don't come back.

### R6 — Roost

A dormant pane token (`RoostToken` gains a pid-less form) so a tab cell can hold a session with no process.
`RoostRoster` surfaces dormant entries; `SessionPane` gets a dormant mode (thread + composer + "Resume in
terminal"). When the same session id goes live from **anywhere** (Perch or a terminal), the existing
`RoostRoster.AdoptContinuations` moves it into the slot. The rail gets a "Recent" group fed by `RecentSessions`.
This also delivers the "Relaunching" future feature in `docs/roost-tabs-plan.md`.

**As built (R6):**
- **Dormant panes are supplied, not stored.** The app hands `RoostRoster.Update` a third list, `RoostDormant` (session id,
  cwd, title, last activity, a `RoostDormantKind` — was open in Perch / interrupted / before shutdown / ended / exited /
  not running — and whether it's Perch's). Each becomes a pane keyed `RoostToken.DormantKey` (`~sessionId`, which no
  pid can equal), `IsDormant`, idle, in a new rail group **`RoostGroup.Recent`** in the app's order. One the app stops
  naming goes at once (no linger); one whose session is live is never shown. `RoostPane.IsLive` (= not ended, not
  dormant) replaces `!Ended` wherever the Roost means "running" (take over, the "not in a tab" pill, the first-run tab,
  "+ New session" admission).
- **Adoption both ways** (`RoostRoster.Adopt`): a live process takes over an ended **or dormant** pane of the same
  conversation, and a dormant pane takes over an ended one. Tabs follow `Adopted`, so a region keeps its session from
  live → dormant (the process went) → live (the first send, or a `--resume` anywhere). A dormant pane can't be closed:
  its "Close"/"Dismiss" dismisses it.
- **Tabs survive a restart.** A dormant cell persists as the pid-less token `~/sessionId`. A persisted cell now places
  the pane whose pid **and** session id match; failing that (every pid is new after a reboot) the pane showing the same
  conversation, live under another process, else dormant. A recycled pid alone still places nothing. Cells that match
  nothing yet **wait** (`Sync(settleSeeds: false)`) until the app has supplied the dormant set, and
  `RoostTabSet.SessionIds()` names them so it can.
- **The dormant set** (`App.RoostDormant.cs`, `RoostDormantSessions`, recomposed on every fold from cached parts, in
  this order, one per session, never live, never a dismissed ending): (1) the restorable sessions Perch had open when it
  closed; (2) Perch sessions open dormant in a window, and woken ones the scan hasn't seen yet (so the dormant pane holds
  the place until the live one takes it); (3) every conversation a tab holds that isn't running, looked up in the Recent
  list, else the pane that just ended, else the dormant pane already showing it, else its transcript; (4) the Recent
  list's top 5. The Recent list (`RecentSessions.Build` + `SessionHistory.ListAll`) is built off the UI thread after the
  ledger begins, when the Roost opens, and 5s after a session leaves the scan (its exit flush may still be landing).
  Folding waits for the monitor's first scan, and persisted cells settle only after the first Recent build.
- **Waking from a pane** (`WakeFromRoostAsync`): the same gates as a session window, now shared in
  **`Services/ResumeGate`** (`Refusal`: live in a terminal / held by another Perch; `ConfirmTrustAsync`: the folder-trust
  question, here modal over the Roost), then `OpenDormantPerchSession` (the window's own dormant session if one is open,
  so there's one writer), `Wake` with the user's default permission mode, and the send. A refusal toasts why and the pane
  keeps the text; while it runs the composer is read-only ("Starting Claude…").
- **Pane & rail:** the pill says why it's here ("Was open in Perch" in the brand hue; "Interrupted 15h ago" / "Before
  shutdown · 15h ago" in the attention hue; "Ended 1m ago"), the footer has the composer ("Message to resume…"), **Resume
  in terminal** (`ReopenSession`) and **Open window** (dormant session window); the menu swaps "Close pane" for
  **Dismiss**. Rail rows under RECENT carry a short note (was open / interrupted / shutdown / age) and a dim dot (amber
  when flagged).
- **Dismiss** records `AppSettings.RecentDismissed` (id → the ending's time, pruned to 3 days; a `NotSettings` UI
  state), forgets a restorable session in the ledger (it stops coming back), and takes the pane out of its tab.
- **Render:** `roost_dormant_1x.png` / `roost_dormant_light_1x.png` / `roost_dormant_focus_1x.png`
  (`RenderRoostDormant`). Tests: `RoostDormantTests` (11).

**Owed live checks (R6):**
- Two Perch sessions in a tab, Exit Perch, start it: both come back dormant in their regions (and at the top of RECENT,
  "Was open in Perch"). Send in one: the trust/live/lock checks, then it wakes in place and the live pane keeps the
  region (no flicker to empty).
- Same with a terminal session in a tab across a reboot: it's dormant in its region; "Resume in terminal" opens it, and
  the live terminal pane takes the region.
- A terminal session in a tab ends: its pane turns dormant in place (no "Ended" linger, no emptied region).
- Dismiss a dormant pane: it leaves the tab and RECENT; resume and end it again, and it's back.
- A session interrupted by a restart: RECENT shows it first, amber.
- Open the Roost with many transcripts: the Recent build doesn't stall the UI.

### R7 — overlay Recent section

`OverlaySection.Recent` + `OverlaySectionOrder.Default` + the `SectionVisible`/`SectionHeight`/`PaintSectionCore`
arms + collapsible header (`RecentExpanded` in `AppSettings`, listed in `SettingsRegistryTests.NotSettings`).
Rows: title, folder (path rules: keep the name, elide the directory), age, badge (**abrupt**,
**just before shutdown**, de-emphasised for `Exited`), and Resume in Perch / Resume in terminal / Dismiss. A
startup toast when abrupt sessions are found ("3 sessions were interrupted by a restart"). Paint stays
allocation-free: the list is computed off the UI thread and cached, never read in `Render`.

**As built (R7):**
- **`OverlaySection.Recent`**, right after Sessions in `OverlaySectionOrder.Default`, with its arms in `SectionVisible`/`SectionHeight`/`PaintSectionCore` and a
  `_sectionTop`-backed `RecentTop`. `OverlayCanvas.Recent.cs` follows the collapsible pattern: chevron + "Recent"
  header (collapsed, it says "2 interrupted" in the warning hue, else the count), expand state persisted as
  `AppSettings.RecentExpanded` (`NotSettings`). Visible only while there's something recent (always in Rearrange
  mode, where an empty one reads "nothing recent"); `SetShowRecent` is the gate R8's setting will drive.
- **`OverlaySectionOrder.Normalize` splices a missing section after its *nearest* default predecessor** that's present,
  not the furthest one. With the old rule a user's custom order would have put Recent at the very bottom (after
  whichever early section they'd dragged last); now it follows the session rows wherever they are. Saved sections
  keep their relative order either way.
- **Lines** (`RecentLine`, precomputed by the app — paint formats nothing): what Perch had open first ("was open", brand
  hue), then the Recent list as built (flagged first: "interrupted · 14h" / "before shutdown · 14h" in the warning hue;
  then "2h ago"; an `/exit` de-emphasised), capped at 5 with "show +N more" (→ the launcher's full list). The title is
  the `/rename` title, with the folder's name beside it in the faint hue, else the folder name alone. A session live
  again drops out on the next fold; ages move on a 1-minute timer. `App.PushRecentLines` runs on every fold, after each
  Recent build and when the restorable list changes; `SetRecent` is a no-op for unchanged lines.
- **Actions:** click a line → Resume in Perch (a dormant window, `OpenSessionResume`); right-click → Resume in Perch /
  Resume in terminal / Dismiss; hover swaps the note for a "×" that dismisses. Dismiss is now one
  `App.DismissRecent(sessionId)` shared with the Roost's dormant panes (dismissal map stamped "now", ledger forget for a
  restorable one, out of its Roost tab).
- **Startup toast**: R5's "Pick up where you left off" now waits for the first Recent build and is merged with the
  restart count — "2 Perch sessions were open when Perch closed, and 3 sessions were interrupted by a restart. They're
  under Recent on the overlay." Interrupted = `Abrupt` + just-before-shutdown rows within the window of the shutdown
  that ended the previous run (`BeginRun`'s answer, kept as `_previousShutdown`).
- **Render:** `overlay_recent_1x.png` / `_1.5x` / `overlay_recent_collapsed_1x.png`, and the section in
  `overlay_sections_default/reordered_1x.png`. `SampleData.RecentLines()` is ready for R8's preview.

**Owed live checks (R7):**
- After a restart with sessions open in terminals: the toast counts them, Recent lists them first in the warning hue.
- Click a line: a dormant session window opens; send, and the line leaves Recent as the session goes live.
- "×" and Dismiss: the line goes and stays gone across a restart; resume it in a terminal and end it, and it's back.
- Collapse the section, restart Perch: it stays collapsed.
- An older `settings.json` with a custom `SectionOrder`: Recent appears right after Sessions.

### R8 — settings + polish

A `SettingDescriptor` for showing the Recent section (and anything else user-facing that R1–R7 added), a
`PreviewTarget` + `SampleData` rows so the Settings preview shows it, headless render at 1× and 1.5×, the
changelog's Unreleased section.

**As built (R8):**
- `AppSettings.ShowRecentSessions` (default on; the section still only appears while there's something recent) with a
  `recent-sessions` descriptor on the Session row surface ("Recent sessions"; keywords recent / ended / interrupted /
  restart / shutdown / resume / recover …), `PreviewTarget.RecentSessions` (catalogue chip "interrupted · 2h"), and a
  line in `OverlaySettingsGates.Apply` → `SetShowRecent`, so the live overlay and the preview follow it through the
  idempotent `DisplayChanged` path. It gates the overlay section only; the Roost's RECENT group is unaffected.
- The Settings preview (`PreviewPane`) seeds `SampleData.RecentLines()`, so the section shows there and Rearrange
  can place it (dimmed when the setting is off).
- Nothing else user-facing needed a setting: `RecentExpanded`, `RecentDismissed` and the ledger are UI state; the
  shutdown window, Recent window and row cap stay constants (D6, Q1).
- `CHANGELOG.md` Unreleased: the whole feature (R1–R8) plus the Exit-cleanup fix from R5.
- Render: `preview_pane_1x.png`, `settings_catalog_1x.png`.

### R9 — Perch sessions keep their place (supersedes the "was open" restore)

User decision (2026-10-06): a Perch session lives until the user ends it. Whatever stops its process (Exit, an update,
a crash, an OS restart), it comes back **where it was** (an overlay row in Sessions, a Roost pane in its rail group and
tab) with no `claude` behind it, and the first reply wakes it. This replaces R5/R7's "was open in Perch" entries at
the top of Recent, the startup toast's Perch half, and the tray's "Reopen Perch sessions" item. Windows don't reopen
(Perch often starts at login); clicking the row opens one. No expiry. Exit and Update both confirm when a session is
mid-turn (a running turn or a pending permission/question card).

**As built (R9):**
- `LedgerSession` also keeps `Model`/`PermissionMode`/`Effort` (optional, so older ledgers read as defaults);
  `TrackHeld` rewrites an entry only when it changes (it now also runs on `StateChanged`, which covers a mode switch).
  `OpenDormantPerchSession` seeds a held session's dormant `PerchSession` with them, so a window's wake uses them, and
  the Roost wake reads them too.
- `App._parked` (was `_restorable`): the previous run's held sessions, plus this run's whose process ended while
  Perch runs if Perch stopped it (`PerchSession.StoppedByPerch`, e.g. the update path) or it exited non-zero (a crash).
  A clean exit on its own (`/exit`) is the user ending it, so it's forgotten. `_exiting` keeps Exit's own stops out.
- Overlay: `App.WithDormantRows` appends a `ClaudeSession { IsDormant = true }` (Pid = `~sid`, Idle, `PerchControlled`,
  the stored mode) per parked session to each scan. The row draws at `DormantRowOpacity` with "not running", is left
  out of `_countedSessions` (header tally, dense strip), and has its own menu (Open / View history / Resume in terminal
  / End session / Copy id). A click goes to `OpenSessionResume` (dormant window).
- Roost: `RoostDormant.IsHeld` (`WasOpenInPerch`) puts the pane in the rail's Quiet group (and A–Z among the live ones),
  not Recent, and keeps it in the rail without a tab. Its pill reads "Not running · reply to resume". "Dismiss" reads
  "End session"; closing the pane only takes it out of the tab. A waking held pane keeps that kind until the scan
  sees the process.
- Recent (overlay button + Roost footer) no longer lists held sessions; the startup toast only counts terminal
  sessions a restart interrupted.
- `App.DormantRows.cs`: the rows, `EndDormant`, `ResumeDormantInTerminal` (lets go first, like hand-back), and
  `ConfirmStopTurnsAsync` behind `StartUpdate` / `RequestExit`.
- A dormant session window shows "End session" (confirm, then `PerchSession.EndByUser` → the app's dormant `Released`
  handler → `EndDormant`, which forgets it and closes every window showing it dormant).
- The launcher's "Resume recent" rows and the in-window `/resume` overlay no longer show the resume estimate (opening is
  free); it stays only in the dormant note above the composer (`ShowDormantEstimate`, now always computed there).
- Tests: `SessionLedgerTests.LaunchSettings_RoundTrip_…`, `RoostDormantTests.APerchSessionPerchStillHolds_…`. Render:
  `overlay_1x.png` (the "Invoice export" sample row), `roost_dormant_1x.png`.

**Live checks owed (R9):** start two Perch sessions (one in plan mode), Exit Perch, start it: both rows are back faded
and "not running", nothing is spawned (Task Manager), the Roost has them in Quiet / their tab. Click one, reply: it
resumes in plan mode and the row goes live. "End session" on the other: gone, and stays gone after a restart. Exit
with a turn running: the prompt appears. Kill a session's `claude` process: its row turns "not running".

## Risks

- **Claude's on-disk format can change.** `cost-state` and `history.jsonl` are undocumented. Everything degrades
  to "unknown" rather than "abrupt" when a record is missing, so a format change shows fewer badges instead of
  false alarms. R1's fixtures make a change easy to spot.
- **Two writers on one transcript.** A dormant session never starts if its id is live elsewhere: the existing
  `StartSession` collision defences (live-in-terminal check, `.perch-lock`) run on the first send.
- **`/clear` starts a new session id under the same process.** The ledger follows the process to its newest id.
- **Cost of the scan.** `RecentSessions` reuses `SessionHistory.ListAll` (already parallel, tail-only reads) and
  reads `history.jsonl` incrementally, all off the UI thread.

## Owed live checks

To fill in as checkpoints land.
