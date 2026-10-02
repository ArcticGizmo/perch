# Session recovery — design & checkpoint plan

Status: **plan only, decisions confirmed** (2026-10-02), on branch `session-recovery` (cut from `main`).
Nothing is built yet. The
spike that grounds it (what Claude Code leaves on disk for each way a session can end) is done and written up
below; four small probes (R0) are still owed before the code that depends on them.

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
  means the session exited and was later resumed; only the tail matters.
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
| D5 | Shutdown time — where from? | In order: **(1)** Perch's own stamp when the OS says it's shutting down / logging off; **(2)** Perch's last heartbeat, when it's older than Perch's own start and there's no clean-exit stamp; **(3)** Windows event log fallback (`Kernel-Power` 42 / `Kernel-Boot` 27), for when Perch wasn't running. | Fast Startup makes boot time useless (spike). The stamp is exact; the heartbeat covers a hard power-off; the event log covers "Perch wasn't running". |
| D6 | "Just before shutdown" window | **10 minutes, a constant** (`SessionRecovery.ShutdownWindow`). It becomes a setting only if it ever feels wrong. | The user's call. One constant is easier to tune than a setting nobody touches. |
| D7 | End time of an ended session | Graceful: `cost-state` `startTime + totalDuration`. Abrupt: the last transcript record's `timestamp`. | Both are Claude's own data. In the restart test the last record was 11s before the power event. |
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
  usual "parse defensively, never throw" rule).
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
- **Roost** — dormant panes in their old slots, and a "Recent" group in the rail (R6).

## Checkpoints

Each checkpoint is one commit with a green gate: both heads build, `dotnet test` passes, and the headless render
is checked for anything owner-drawn.

### R0 — probes (owed, mostly manual)

1. **Does a Perch-controlled (stream-json) session write `cost-state` when it's ended gracefully?** End one from
   the session window and read its tail. If it never does, D1 for controlled sessions relies on the ledger alone
   (already the plan) and nothing else changes.
2. **What happens to controlled sessions on the overlay's plain Exit today?** (Expected: the pipe closes and
   `claude` dies without `SessionEnd`, which matches the `sdk-cli` leftovers.)
3. **Does Avalonia raise `ShutdownRequested` on an OS shutdown/logoff on Windows**, and how much time is there to
   write a small file? Decides how `IShutdownSignal` is built.
4. **Restart with Fast Startup off** (a full boot): does anything change (e.g. 1074/6006 logged, Claude gets more
   time)? Only affects the event-log fallback.

### R1 — reading Claude's records (Core)

`SessionEndReader` + `ExitCommandIndex`, with fixtures under `tests/Perch.Tests/fixtures/claude/`: a transcript
ending in `cost-state`; one with a mid-file `cost-state` and later turns (resumed, then killed); one ending
mid-turn; one with a torn last line; a `history.jsonl` with an `/exit` before and after a resume. The
`history.jsonl` reader is tested for incremental reads (append, re-read only the new bytes).

### R2 — Perch's ledger and the shutdown clock (Core)

`SessionLedger`, `ShutdownClock`, `RecentSessions`, and the `SessionRecovery.ShutdownWindow` constant. Tests: the
D5 fallback order, the 10-minute boundary, a hard power-off (heartbeat only), "Perch wasn't running", dismissals,
the 3-day window.

### R3 — platform signals

`IShutdownSignal` + `IPowerHistory`, Windows implementations, macOS stubs, `PlatformServices` wiring. The App
stamps the ledger on the signal, runs the heartbeat (every 60s plus on every live-set change), and writes a
clean-exit stamp on a normal Exit.

### R4 — dormant sessions everywhere

`SessionWindow` dormant mode; every "open a session" path (launcher recents, CLI `--resume`, the overlay, the
Roost) opens dormant. The first send starts the process and delivers the message. The estimate becomes the
composer note. **Measure** the first-send delay (Q5).

### R5 — Perch's own exits

D9: every exit path ends controlled sessions gracefully and records them as "open at exit" in the ledger. On the
next start they come back dormant: overlay rows (with a dormant style) and their old Roost slots. Ended-by-user
sessions don't come back.

### R6 — Roost

A dormant pane token (`RoostToken` gains a pid-less form) so a tab cell can hold a session with no process.
`RoostRoster` surfaces dormant entries; `SessionPane` gets a dormant mode (thread + composer + "Resume in
terminal"). When the same session id goes live from **anywhere** (Perch or a terminal), the existing
`RoostRoster.AdoptContinuations` moves it into the slot. The rail gets a "Recent" group fed by `RecentSessions`.
This also delivers the "Relaunching" future feature in `docs/roost-tabs-plan.md`.

### R7 — overlay Recent section

`OverlaySection.Recent` + `OverlaySectionOrder.Default` + the `SectionVisible`/`SectionHeight`/`PaintSectionCore`
arms + collapsible header (`RecentExpanded` in `AppSettings`, listed in `SettingsRegistryTests.NotSettings`).
Rows: title, folder (path rules: keep the name, elide the directory), age, badge (**abrupt**,
**just before shutdown**, de-emphasised for `Exited`), and Resume in Perch / Resume in terminal / Dismiss. A
startup toast when abrupt sessions are found ("3 sessions were interrupted by a restart"). Paint stays
allocation-free: the list is computed off the UI thread and cached, never read in `Render`.

### R8 — settings + polish

A `SettingDescriptor` for showing the Recent section (and anything else user-facing that R1–R7 added), a
`PreviewTarget` + `SampleData` rows so the Settings preview shows it, headless render at 1× and 1.5×, the
changelog's Unreleased section.

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
