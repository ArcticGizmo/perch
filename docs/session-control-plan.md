# Session control — milestone plan (prove e2e first, polish later)

> **Status: plan (2026-08-27, branch `session-control-poc`).** Follows the investigation in
> `docs/session-control-poc.md`, which verified the stream-json control protocol live (M0 below is
> already on this branch). Every milestone here ends in a **runnable end-to-end demonstration** of one
> risky mechanism; polish (theming depth, settings registry entries, markdown fidelity, macOS parity,
> overlay glyphs) is deliberately deferred to a single backlog behind a decision gate at M6.
>
> **POST-GATE PIVOT (2026-08-27, user feedback):** the stream-json chat *console* and the *permission
> valet* both proved awkward to dogfood — elevation killed the terminal but left the external window
> spewing teardown escapes, and the valet only makes sense for external terminals (can't be dogfooded
> purely inside Perch). Decision: build the **embedded ConPTY terminal** (previously the deferred M6
> option) as the primary surface — a real interactive `claude` TUI inside a Perch window, promptable
> from the terminal *or* a Perch input box. Chosen tech: `Iciclecreek.Avalonia.Terminal` +
> `Porta.Pty` (MIT, net10/Avalonia-12, XTerm.NET emulator over ConPTY). The **valet is parked** (code
> wired but no tray toggle, so dormant). `SessionTerminalWindow` is the new elevation destination and
> gets its own tray entry. Spike verified: package restores against Avalonia 12.0.5 and the integration
> compiles against the real `TerminalControl` API; the live TUI render is the user's dogfood pass. See
> the "Embedded terminal" note under M6.

The goal, restated from the investigation: users keep their own terminals if they want; Perch attaches
rich surfaces to *any* session (reading, permission responses, prompt nudges); Perch-owned sessions get
full control; an existing terminal session can be elevated into a Perch-owned one via `--resume`.

## Operating rules for the prove-out

- **Thin vertical slices.** Each milestone must be demoable against a *real* Claude Code session on this
  machine, not just unit tests. Ugly UI is fine; a `(PoC)` suffix on every new menu item/window title.
- **Capture-first protocol work.** Any new wire surface (hook JSON, inbox socket, resume) gets a raw
  capture saved under `tests/Perch.Tests/fixtures/` and a fixture test *before* the feature is called
  done — the stream-json parser tests set the pattern. Third-party docs have already been wrong once
  about exact shapes; captures are the source of truth.
- **No new persisted settings until the gate.** Nothing added to `AppSettings`/`SettingsRegistry` before
  M6 (avoids registry-coverage churn on code that may be cut). Hard-coded PoC defaults, marked `// PoC:`.
- **Degrade to today's behaviour.** Every attachment mechanism must fail *open*: valet timeout → normal
  terminal prompt; tray closed → hooks no-op fast; socket missing → feature hidden. A user who never
  opens Perch must notice nothing.
- **Cheap models for live tests.** All manual e2e runs use `--model haiku` in a scratch folder.

## What already exists (reuse, don't rebuild)

- **M0 (this branch):** `Perch.Core/Data/Control/` — `SessionEvents`, `StreamJsonParser`
  (fixture-tested), `ClaudeSessionController` (spawn, initialize, prompts, `can_use_tool` respond,
  `set_permission_mode`, interrupt [unexercised], stop) — and `SessionConsoleWindow` on the tray menu.
- **Live transcript tailing:** `TranscriptParser` (`SessionHistory.cs`) — incremental `Ingest()`,
  assistant text + tool_use/result stitching; `TranscriptLocator`, `TranscriptScan`, `ToolSummary`.
  The markdown viewer already runs a live transcript watcher for pane refresh — same trigger the
  mirror needs.
- **Hook plumbing:** `perch-hook` (NativeAOT, self-managed registration via
  `ClaudeUserSettings`/`HookInstaller`) already handles SessionStart/mode/cleanup events and knows how
  to find/launch the tray. Adding a hook event = a new dispatch arm + a registration entry.
- **Markdown rendering:** `Rendering/MarkdownRender.cs` (Markdig → Avalonia inlines), proven in
  `ChangelogWindow` and `MarkdownWindow`.
- **Process control:** `SessionTerminator` (safe tree-kill with pid-identity check),
  `SessionLauncher.Reopen` (opens a terminal running `claude --resume <id>`), `WindowActivator`.
- **Session identity:** `SessionMonitor` scan of `~/.claude/sessions/{pid}.json`; sidecars keyed by
  session id (`.mode`, `.notify`, `.history`) — the established Perch↔hook rendezvous channel.

## Milestones

### M0 — Ownership over stream-json ✅ (done, this branch)

Proved: spawn/drive a session bidirectionally on Windows redirected stdio; per-block rich output;
`can_use_tool` round-trip; `set_permission_mode`; normal transcripts + hooks fire. Remaining known
gaps: interrupt unexercised (picked up in M4), resume not wired (M4).

### M1 — Rich mirror pane: read any live session, including ones Perch didn't launch ✅ (code)

> **Landed 2026-08-27.** Discovery: `HistoryWindow` already *was* most of the mirror (live
> `FileSystemWatcher` tail + debounce, Follow, per-session open from the overlay row via
> `HistoryRequested`). What M1 added: prose now renders through the block-level `MarkdownView`
> (headings, syntax-highlighted code panels, tables, quotes) instead of flat inlines, and live tailing
> is **incremental** — new events append and a landed tool result patches its block in place
> (`ApplyIncremental`), which also fixed a real bug where a result arriving with no new events never
> re-rendered. Verified via a new `CaptureRenderedFrame` headless capture (`history_readable_1x.png`);
> live-watching a real session is still to be eyeballed interactively. No separate `SessionMirrorWindow`
> was built — reuse won.

The "stop reading giant markdown/diffs in a terminal" win, and the foundation every later surface
renders into. No control, no protocol risk — mostly assembly of existing parts.

- **Build:** `SessionMirrorWindow` (reused instance, retargetable like `MarkdownWindow.Retarget`):
  `TranscriptParser.Ingest()` on a file watcher + poll fallback, rendering user/assistant text
  (markdown via `MarkdownRender`), thinking (dimmed), tool chips with results. Open from a session
  row's right-click menu ("Mirror… (PoC)"). Works identically for Perch-owned and terminal sessions.
- **Proves:** the rich reading experience tracks a *live terminal session* with acceptable latency and
  survives transcript quirks (partial lines, truncation, image blocks) at real sizes.
- **Exit criteria:** watch a real terminal session doing a multi-tool task live in the mirror; open the
  mirror on a >5 MB transcript without UI stalls; `TranscriptParser`-level fixture test for anything
  the mirror needed that the parser didn't already surface.
- **Deferred polish:** diff-aware rendering of Edit/Write inputs, images, search, virtualisation
  beyond "cap rendered history to the last N events".

### M2 — Permission valet: answer any session's permission prompts from Perch ✅ (code + e2e)

> **Landed 2026-08-27.** `perch-hook valet` (PreToolUse) forwards the raw payload over a named pipe to
> the tray's `ValetServer` (Perch.Core/Data/Control) and relays an explicit allow/deny back as
> `hookSpecificOutput`; everything else is silent (never `"ask"` — that *forces* a prompt, so "no
> opinion" must be no output). The pipe name is baked into the hook registration per profile
> (`ClaudeUserSettings`), so dev/release trays never cross. Tray side: `DecideValet` passes instantly
> when disarmed / the session is Perch-owned (`ControlledSessions`) / the tool is read-only
> (`ValetProtocol.IsReadOnlyTool` — the PoC heuristic), else `ValetPromptWindow` cards with
> Allow/Deny/Ignore and an 18 s auto-pass; armed via the tray menu ("Permission valet (PoC)").
> **e2e verified against real claude 2.1.247** with a project-local registration + stand-in server:
> allow → Write ran with no denial; no server → instant fail-open, normal denial, no added latency.
> Unit-tested: pipe round-trip, pass-on-unparseable, fail-open-on-throw. Still needs a human pass:
> the armed prompt UI in a live interactive terminal (ignore → TUI prompt appears at ~18 s), and the
> known heuristic gap — an armed valet also prompts for allowlisted mutating tools (e.g. an allowlisted
> `git status` under Bash) because the hook can't see Claude Code's own permission evaluation.

The highest-value control feature and the riskiest UX loop — prove it before anything else builds on it.

- **Build:**
  - Tray side: a named-pipe server (`perch-valet` + profile suffix; single instance already guaranteed
    by `ISessionLock`), request/response JSON. A crude prompt window (or a bar inside the mirror/console)
    showing tool name + `ToolSummary` phrase + raw input, Allow / Deny / Ignore.
  - Hook side: `perch-hook pretool` — reads the PreToolUse stdin JSON, connects to the pipe with a
    short connect timeout (~200 ms; tray absent → emit `"ask"` immediately), waits a fixed PoC window
    (~15 s) for a decision, then emits
    `{"hookSpecificOutput":{"hookEventName":"PreToolUse","permissionDecision":"allow|deny|ask",…}}`.
    Registered via `HookInstaller` alongside the existing hooks.
- **Proves:** the full loop against a *real interactive terminal session* — decision honoured by the
  TUI; `"ask"` fallback lands the user in the normal terminal prompt; multiple concurrent sessions
  don't cross wires; the hook adds no perceptible latency when Perch is closed.
- **Exit criteria:** scripted demo: terminal session asks to Write → approve from Perch → file written,
  terminal never prompted. Second run: ignore Perch → terminal prompts normally after the window.
  Third: tray not running → no added delay. Captures of the PreToolUse stdin payload + fixture tests
  for the hook's decision serialisation. Also verified: behaviour when a tool is auto-allowed by
  allowlists (hook fires before permission checks — decide whether the valet should early-`ask`).
- **Open questions to answer here, not before:** the right defer timeout; whether the valet should show
  only would-have-prompted tools (needs a heuristic — the hook can't see whether a prompt would occur);
  interaction with the M0 console's stream-json `can_use_tool` path (valet must not double-prompt
  Perch-owned sessions — simplest: hook early-`ask`s when the session id is one Perch owns).
- **Deferred polish:** rich input rendering in the prompt (reuse mirror pieces), per-tool "always
  allow" rules, notification/toast integration, configurable timeout.

### M3 — Prompt injection into a live terminal session (inbox socket spike) ✅ (spike done — deferred, not built into Perch)

> **Spiked 2026-08-27; outcome per the cut rule: keep as "nudge", defer the build.** Full findings in
> `docs/session-control-poc.md` §"Sending prompts into a live terminal session". In short: harvesting
> `CLAUDE_CODE_MESSAGING_SOCKET`/`_TOKEN` via a SessionStart hook works; the **raw** pipe handshake as
> commonly documented did *not* (no ack, message never landed — the real wire format is an enveloped,
> `ackId`/`hop-chain`/`crossSessionInbound` protocol), but the **supported** cross-session `SendMessage`
> delivered instantly and Claude acted on it. It lands as a *teammate* message (never "the user typed
> this") with a built-in "a peer cannot grant escalation" guard — so it is genuinely a nudge/queue
> channel, as the plan predicted. **Decision:** the core ask (respond from Perch, read from Perch,
> elevate) is covered by M1/M2/M4 without this, and clean sending wants a session context or the
> enveloped protocol reverse-engineered — so no Perch code ships for M3 now. Revisit at M6 if a
> "queue a prompt to a terminal session" affordance proves wanted; the token's at-rest posture
> (secret) is settled up front. No PoC-suffixed Perch surface was added.

<details><summary>Original M3 plan (for reference)</summary>

The most uncertain mechanism — timebox it and let the findings decide its future. Treat as a spike
that may be cut at the gate.

- **Build:** perch-hook SessionStart harvests `CLAUDE_CODE_MESSAGING_SOCKET`/`_TOKEN` into a
  session-id-keyed sidecar (`{sid}.msg`) next to the existing `.mode`/`.notify` sidecars (cleaned up by
  the existing `cleanup` sweep). Tray side: minimal client (connect named pipe, `{"type":"auth",…}`,
  send text) behind a "Send to session… (PoC)" action on the mirror window.
- **Proves / documents:** how an externally-posted message actually lands in a terminal session — how
  it's labelled, whether `crossSessionInbound` approval interrupts, latency, and whether it reads as a
  usable "type a prompt from Perch" or only as a "nudge".
- **Exit criteria:** a message sent from Perch visibly reaches a live terminal session and Claude acts
  on it; a short findings section appended to `session-control-poc.md` (including the security note:
  the sidecar holds a capability token — record ACL posture and whether it must be DPAPI-protected or
  kept tray-memory-only before this ships).
- **Cut rule:** if messages land as awkwardly-labelled inter-session chatter that Claude treats as
  second-class, demote this to "nudge/queue" framing or drop it; M2+M4 already cover the core ask.

</details>

### M4 — Elevate & hand back: move a session between terminal and Perch ownership ✅ (code + resume e2e)

> **Landed 2026-08-27.** `ClaudeSessionController.Start` gained `resumeSessionId` (`--resume <id>`);
> `SessionConsoleWindow.ResumeSession` + a "Hand back to terminal" button; the overlay session-row menu
> gained "Elevate to Perch (PoC)…" (CLI sessions only, not ones Perch already owns) →
> `App.OnElevateToPerch` confirms, `SessionTerminator.Terminate`s the terminal process, and resumes the
> same id in the console. **Resume e2e verified against real claude 2.1.247**: seed a session by id →
> resume by id → it recalled the codeword, same session id, one transcript. **Interrupt: inconclusive,
> honestly** — the control request was sent cleanly, but a fast `haiku` turn finished before it landed,
> so cancellation itself isn't yet proven (needs a genuinely long turn / slower model, or the race is
> real and interrupt needs the queued-message path). Kill-then-resume worked with no lost/forked id in
> the spike. Still needs a human pass: the full overlay→elevate→console→hand-back round-trip in the
> live app, and a conclusive interrupt test.

- **Build:** `ClaudeSessionController.Start` gains `resumeSessionId` (`--resume <id>`);
  `SessionConsoleWindow` gains a "resume existing" path. Overlay session-row action "Elevate to
  Perch… (PoC)": confirm → `SessionTerminator.Terminate(pid)` → controller resume → console opens
  mid-conversation. Reverse action in the console: Stop → `SessionLauncher.Reopen(cwd, sessionId, …)`.
  Exercise **interrupt** here too (long turn in the console → interrupt → next prompt works).
- **Proves:** same session id and one continuous transcript across both hand-offs (mirror window keeps
  tracking it throughout — a nice compound demo of M1); interrupt actually cancels a turn over stdin.
- **Exit criteria:** scripted round-trip: start in terminal → elevate → continue in console → hand back
  → continue in terminal, one transcript, no duplicated/forked session ids; interrupt capture + parser
  fixture for its ack; kill-vs-graceful findings recorded (what state a terminated TUI leaves, whether
  `--resume` picks up cleanly after `Terminate`).
- **Risk to retire:** whether a TUI process must exit *cleanly* for resume to be safe — if tree-kill
  proves lossy, elevation needs a cooperative exit (e.g. inbox-socket nudge from M3, or user exits
  manually and Perch offers "continue here" on the now-dead session).

### M5 — Ownership console: interactive-parity essentials ✅ (partial — core done, two items deferred)

> **Landed 2026-08-27.** Done: assistant answers render through the block-level `MarkdownView`
> (code panels, tables, headings — verified via a new `session_console_1x.png` capture); overlay clicks
> on a Perch-owned session id route to the console instead of hunting for a terminal (`FocusSession`
> early-returns via `ControlledSessions.Owns`, killing the "No window to focus" toast); a queued-prompt
> indicator in the status line (stream-json queues prompts sent mid-turn). Already covered earlier:
> Perch-owned sessions skip the valet (`ControlledSessions`, M2). **Deferred within M5** (need a
> protocol spike, not just UI): `AskUserQuestion` as a first-class choice UI and plan-mode approval —
> how these arrive over stream-json (a tool_use that auto-runs? a control request?) isn't established
> yet, so building buttons would be guessing. Tracked into the M6 gate. Still needs a human pass:
> dogfooding a long real session end-to-end from the console.

Make the M0 console honest enough for daily dogfooding — still function over form.

- **Build:** handle `AskUserQuestion` as a first-class choice UI (arrives as a tool_use block);
  surface plan-mode approval; queued-prompt indicator (stream-json queues stdin messages);
  markdown-render assistant text via `MarkdownRender`; route overlay clicks for Perch-owned session ids
  to the console instead of `FocusTerminalForProcess` (kills the "No window to focus" toast); tag
  Perch-owned sessions so the M2 valet skips them.
- **Exit criteria:** run a real 20+ turn working session entirely from the console (dogfood: use it for
  a small Perch task) without needing a terminal escape hatch for anything except slash commands.
- **Deferred polish:** slash-command palette, image paste, transcript search, session picker.

### M6 — Decision gate ⏳ (reached — awaiting the human decisions below before polish begins)

> **Reached 2026-08-27.** M1–M5 landed as code on `session-control-poc` (M3 spiked and deliberately not
> built). Everything is behind `(PoC)`-suffixed, opt-in surfaces; no persisted settings, no changelog,
> nothing default-on — exactly the pre-gate state the plan called for. Per the plan, the gate is a
> **human checkpoint**: the questions below decide what (if anything) graduates out of PoC, and the
> polish backlog stays parked until then.
>
> **What shipped (all verified at least at the protocol/e2e level; interactive app passes still owed):**
> - M1 rich reading — history viewer renders live transcripts through `MarkdownView`, incremental tail.
> - M2 permission valet — answer any session's prompts from Perch; e2e-verified allow + fail-open.
> - M4 elevate/hand-back — `--resume` takeover both directions; resume e2e-verified (interrupt not).
> - M5 console parity — Markdown answers, owned-session focus routing, queued-prompt indicator.
>
> **Open items carried in:** interactive/dogfooding passes for every surface; the valet's read-only-tool
> heuristic (armed valet still prompts for allowlisted mutating tools); a conclusive interrupt test;
> `AskUserQuestion`/plan-mode over stream-json (needs a protocol spike).
>
> **Decisions needed from the user before polish** (see the chat summary):
> 1. Which surfaces graduate to default-on vs stay opt-in `(PoC)`? (Especially: should the valet ever be
>    on by default, given the heuristic gap and that it inserts a hook into *every* session?)
> 2. Invest in the macOS head now (named pipe → Unix socket behind a Core interface) or keep Windows-only
>    for the PoC?
> 3. Build the `AskUserQuestion`/plan-approval protocol spike now, or defer until the console is actually
>    dogfooded and the need is proven?
> 4. Pursue the ConPTY embedded-terminal tab at all, now that valet + elevation cover the ask without it?
>
> **User answers (2026-08-27):** (Q1) valet parked, embedded terminal is the surface; (Q4) **yes, build
> ConPTY** — done below. Q2 (macOS) and Q3 (`AskUserQuestion` spike) still open.

### Overlay "+ New session" launcher ✅ (code)

> **Landed 2026-08-27.** A "+ New session" row heads the overlay's Sessions section (above the session
> rows, present even with an empty roster), so a Perch-controlled session launches from the overlay
> rather than the tray menu. Owner-drawn in `OverlayCanvas.Sections.cs` (`DrawNewSessionRow`, hover
> wash + "+" glyph + accent caption), hit-tested via `_newSessionRect`, raises `NewSessionRequested` →
> `OpenSessionTerminal`. `RowsTop`/`SessionsSectionHeight` account for the row so row hit-testing stays
> aligned. Verified via `render` captures (populated + empty overlay).

### Embedded ConPTY terminal — refinements (2026-08-31, user feedback)

> Three fixes after the first dogfood: (1) **starts empty** — the cwd box is blank and "Start claude" is
> disabled until a real folder is chosen, so a session can't launch at the home dir / a filesystem root;
> (2) a **folder-picker button** (📁) opens the native OS folder dialog (`StorageProvider.OpenFolderPickerAsync`);
> (3) the **renderer artifact is fixed** — root cause was `TerminalControl` auto-launching its default
> `cmd.exe` on load (its `Process` defaults to `cmd.exe`, and `TerminalView.OnLoaded` launches it), so our
> explicit `LaunchProcess` was a *second* process → duplicated/reflowed frames. Setting `Process = ""`
> disables the auto-launch; we own every launch. A resumed session now launches from the terminal's
> `Loaded` (after layout, correct size), fresh sessions from the Start click. **Verified live** (UI-driven
> screenshots in captures/): empty state shows no phantom shell; picking a folder + Start launches a
> single clean claude TUI in the chosen dir; a prompt typed in the Perch box injected into the PTY and
> claude replied. 936 tests green.

### Embedded ConPTY terminal ✅ (code) — the post-gate pivot

> **Landed 2026-08-27.** `SessionTerminalWindow` hosts a real interactive `claude` TUI inside Perch via
> `Iciclecreek.Terminal.TerminalControl` (XTerm.NET over Porta.Pty/ConPTY). On Windows it launches
> `cmd.exe /c claude [--resume <id>]` in the chosen cwd; a Perch input box below the terminal writes
> prompts into the same PTY via `SendInputAsync(text + "\r")`, so a prompt can come from the terminal or
> from Perch. Wired as a tray entry ("Session terminal (PoC)…") and as the **new elevation destination**
> (overlay "Elevate to Perch" now resumes into this terminal, not the chat console). The valet's tray
> toggle was removed (parked; server/hook stay wired but dormant). Spike verification: the package
> restores against Avalonia 12.0.5 with no downgrade, and the integration compiles against the real
> `TerminalControl` API (`LaunchProcess`/`SendInputAsync`/`ProcessExited`/`IsLive`/`BufferSize`); all
> tests green. **Owed (user dogfood pass — needs a live desktop):** confirm the TUI renders and is
> interactive, prompt-from-Perch types correctly, and elevation resumes cleanly. Known residue:
> elevating an *external* session still kills its process, so that external terminal window shows
> teardown output (unavoidable when killing a TUI out from under another host) — launching sessions in
> Perch's terminal from the start avoids it entirely.

Once the remaining decisions are answered, the polish backlog is:

- Settings + registry descriptors (valet on/off + timeout, mirror preferences, elevation confirmations)
  — each with the required `SettingsRegistryTests` coverage and `PlatformFeature` gating where an OS
  head lacks support.
- macOS head: pipe/IPC implementations behind `Perch.Core` interfaces (named pipe → Unix socket), stubs
  first per the porting convention.
- Theming/contrast pass on the new windows, overlay glyph for "Perch-attached/owned" sessions,
  notifications integration, changelog entries, drop the `(PoC)` suffixes.
- Revisit the deferred ConPTY embedded-terminal tab with real usage data on whether anyone still wants
  the TUI *inside* Perch once elevation + valet exist.

## Sequencing & effort

M1 → M2 are the value core and independent enough to demo separately (M1 is low-risk assembly,
~days; M2 is the real e2e loop, ~days including hook registration and multi-session testing).
M3 is a timeboxed spike (1–2 days) that can run any time after M1. M4 depends on M0 only (~1–2 days).
M5 is the longest tail (up to a week of protocol odds and ends discovered while dogfooding). Gate at
M6. Everything stays on feature branches off `session-control-poc` (or a renamed `session-control`),
merged milestone-by-milestone so each demo is a commit.

## Standing risks

- **Protocol drift:** stream-json shapes, hook JSON, and the inbox socket are SDK-load-bearing but not
  versioned contracts. Mitigation: capture fixtures per surface, check the `init` record's
  `capabilities` array at runtime, and keep every mechanism fail-open.
- **Valet UX backfire:** a hook that delays every tool call by its defer window would make terminals
  *worse*. Mitigation: aggressive connect timeout, defer only while a Perch prompt surface is actually
  visible/focused, and measure added latency in M2's exit criteria.
- **Token handling (M3):** the messaging token is a capability; decide its at-rest posture at the gate
  before any default-on behaviour.
- **Kill-based elevation (M4):** may prove lossy; the fallback shapes are listed in M4.
