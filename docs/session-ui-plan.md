# Perch rich session UI — plan & handoff

**Status (2026-09-02):** Phases 1, 3 and 4 **shipped as code** (uncommitted, branch `session-control-poc`),
built to the Phase 0 mockup *without* the design sign-off having been given yet — every visual token lives in
one file (`Perch.App/Theming/SessionPalette.cs`) so a sign-off revision is a one-file edit. Phase 2 (slash
commands, `AskUserQuestion`, `@`-mentions) not started. **Nothing has been run live by a human** — see
"Owed" below. Render-verified: `captures/20260902-session-ui-render*/session_window_1x.png`,
`session_window_light_1x.png`, `session_launcher_1x.png`.

## What shipped (2026-09-02) — file map
- **Model (Core, tested):** `Perch.Core/Data/Control/SessionConversation.cs` — folds `SessionEvent`s into
  composed items (`UserMessageItem`, `AssistantMessageItem` with ordered `TextPart`/`ThinkingPart`/
  `ToolCallPart`, `PermissionItem`, `NoteItem`) + header state (id/model/mode/cost/turn/queue/pending
  permission). Streaming deltas finalise in place; a permission prompt splits the assistant turn; the turn
  result closes every open item of the turn. `tests/SessionConversationTests.cs`.
- **Controller:** `ClaudeSessionController.Start(..., resumeSessionId, newSessionId)` now pins
  `--session-id <fresh GUID>` for new sessions, so the id is known **before** `init`; registers in
  `ControlledSessions` + writes the lock at launch (released on exit / failed start); sets
  `PERCH_SESSION_OWNER=<tray pid>` in the child's environment. `ToolUseEvent.InputJson` and
  `SessionInitEvent.SlashCommands` added to the parser (fixture-tested).
- **Lock sidecar (4b):** `Perch.Core/Data/Control/SessionLock.cs` — `{sessionId}.perch-lock` in
  `~/.claude/sessions/` (JSON: sessionId, pid-as-string, cwd, profile, since). `Acquire` refuses a *live*
  other owner, overwrites stale; `HeldByOther`, `Release`, `SweepStale` (run at tray start). `tests/SessionLockTests.cs`.
- **Hook guard (4c):** `perch-hook start` reads the lock; if a normal `claude` opens a Perch-controlled id
  whose owner is alive → emits `{"systemMessage": "⚠ Perch: session … is currently controlled by …"}` (advisory,
  fail-open). Perch's own session is recognised by `PERCH_SESSION_OWNER`. `cleanup` deletes the lock only when
  it's ours or stale.
- **UI:** `Perch.App/Windows/SessionWindow.cs` (session bar · launcher · thread · composer · mode flyout ·
  ⋯ menu with hand-back-to-terminal / copy resume / end), `Views/SessionThreadView.cs` (item renderers,
  tool cards, permission card + receipt, stick-to-bottom), `Views/SessionButton.cs`, `Theming/SessionPalette.cs`
  (the design's tokens; dark/light follow the theme's side; brand + semantic hues from `Palette`).
  `MarkdownStyle` gained `BodySize`/`BlockGap`/`BodyFont`/`RootMargin` (additive). Keyboard: Enter sends;
  Enter with a pending permission and an empty composer allows; Esc denies a pending permission, else interrupts.
- **App wiring:** tray "New session…", overlay "+ New session", "Elevate to Perch", and overlay row focus all
  target `SessionWindow` (several may be open; `App._sessionWindows`). Refuse-if-live (4a) via
  `SessionWindow.LiveLookup` (monitor roster) + `SessionLock.HeldByOther`.
- **`perch` CLI (Phase 3):** `Program.Main` parses claude-shaped args (`SessionOpenIntent.FromArgs`: `--resume
  [id]`/`-r`, `--continue`/`-c`, `--session-id`, `--model`, `--permission-mode`, positional dir). Second launch →
  forwards over the `perch-control[-dev]` named pipe (`ControlProtocol`/`ControlServer`, modelled on the
  valet) and prints the reply; first launch stashes `Program.PendingSessionIntent` for the tray to consume.
  `tests/ControlProtocolTests.cs`.
- **Dogfood round 1 (2026-09-02, user feedback) →** (1) **sessions outlive their window**: `Perch.App/Services/
  PerchSession.cs` owns the controller + conversation; `SessionWindow` is a *view* (`Attach`/`Detach`), closing it
  hides the view, the overlay row reopens one (`App.ShowSessionView`); a persistent **End session** button (top
  right, `ConfirmDialog`) is the only way to stop the process — removed from the ⋯ menu. (2) **AskUserQuestion is
  a question card**, not a permission: `AskUserQuestionInput` (Core, tested) parses the questions and builds the
  allow reply's `updatedInput` (`answers: {question → label(s)}`, Agent-SDK convention — **verify live**);
  single-select picks submit immediately, multi-select toggles + Submit, Skip = deny. (3) **Perch sessions list
  with the normal sessions**: `ClaudeSession.PerchControlled` (from the live lock, read in `SessionMonitor`) makes
  `IsBackground` false and the overlay draws the brand mark in the status-dot slot (`PerchOriginKey`); the row
  click routes to `ShowSessionView`, whose `Activate()` follows the window to its virtual desktop. (4) Thread
  scrolling fixed: the `ScrollViewer` subclass needed `StyleKeyOverride` to get a template at all.
- **Dogfood round 2 (2026-09-02) →** session id sits under the project path (de-emphasised, click = copy);
  model / permission-mode / **effort** pills moved into the composer bar (they shape the next message) with
  upward flyouts — effort = `--effort` at launch, `/effort <level>` as a message mid-session (documented as
  working in -p), model mid-session = `set_model` control request (Agent-SDK subtype; ack not surfaced);
  **resume now shows history**: `--resume` replays nothing on stdout (docs confirm), so `PerchSession` reads the
  transcript via `TranscriptLocator.Resolve` off-thread and `SessionConversation.LoadHistory` (tested) inserts the
  past turns in front of the live items (`Reset` event → thread rebuilds; long transcripts show the last 4000 lines).
- **Dogfood round 3 (2026-09-02) →** pills show the *actual* starting state, never "default": model/mode/effort
  pre-launch come from `ClaudeUserSettings.ReadSessionDefaults()` (settings.json `model`, `permissions.defaultMode`,
  `effortLevel`), falling back to the CLI's built-in default model (`opus`, tooltip says so), then init's real
  model once running; the mode pill + its menu items wear the overlay's own badge (`Views/ModeGlyph` →
  `OverlayCanvas.DrawModeChevrons`, `Palette.ModeColor`); **Claude's turns are in a bubble** too (raised, tail
  toward the avatar; tool/thinking cards inset on the surface colour) — a deliberate departure from the mockup's
  open prose, for eye strain.
- **Dogfood round 4 (2026-09-02) →** accent is now the bird's body pink (sampled from icon.png: `#DD9E8E` dark /
  `#B9675A` light) — the brand red-orange read as "error" beside the status hues; cost shows as "≈ $x est." with a
  tooltip (API-equivalent gauge, not billed on a plan); permission modes are the CLI's real four — `ask` (the
  CLI's "default"), plan, accept edits, bypass — with "(default)" marking the settings.json one only in the menu,
  and plan pinned blue in the session UI (`SessionPalette.Plan`; the overlay still paints plan in the theme accent,
  which is only blue in Midnight — a theme-role follow-up if it should match everywhere); model + effort share one
  joined "model │ effort" pill; `fable` added to the model list.
- **Dual-surface terminal PoC — DELETED (2026-09-04).** The decision is locked: for one session a user gets
  the Perch rich UI **or** an external `claude`, never both surfaces at once. So `SessionTerminalWindow`,
  `SessionHost`, `SessionUiWindow`, `TranscriptReadableView` and the `Iciclecreek.Avalonia.Terminal` package
  (XTerm.NET / Porta.Pty ConPTY) are gone. `SessionConsoleWindow.cs` was already deleted (its logic lives in
  the model/window). The rich `SessionWindow` (stream-json ownership) is the only Perch-native surface; the
  external terminal stays the external terminal, bridged only by `--resume` hand-off, not by co-driving one
  live session.

## Owed / open
- **Design sign-off** on the mockup, then adjust `SessionPalette` (and the real web fonts: Bricolage Grotesque /
  Hanken Grotesk / JetBrains Mono are *named* in the font stacks but not bundled — they render only where
  installed; bundling as `AvaloniaResource` is the follow-up if the faces are approved).
- **Live dogfood** (never run by a human): launch from tray, full turn, a permission answered in-UI, mode
  switch, interrupt (still unproven at the protocol level), hand-back, `perch --resume <id>` from a terminal
  with the tray running and not running, the hook warning when a normal `claude --resume` opens a controlled id.
- Phase 2: verify which slash commands work as plain text over stream-json (init now exposes
  `SlashCommands`), command palette, `AskUserQuestion`/`ExitPlanMode` cards, `@`-file mentions, input history.
- A permission prompt in a background window has no attention cue yet (no flash/toast).

This doc is the single source of truth for a fresh session picking this up. Branch: `session-control-poc`.

## Read these first
- **The mockup:** `docs/session-ui-mockup.html` (published: https://claude.ai/code/artifact/6ac5ac71-ef65-42b2-8725-0b7ad5f86358).
  A design mockup of the target UI — open it, toggle Dark/Light. This is what Phase 1 builds to.
- **`docs/session-control-poc.md` §7–§8** — the investigations behind every decision below (multi-surface
  model, the live socket spike, the terminal-fidelity findings). Don't re-investigate; it's captured there.
- Memory: `session-ui-redesign` (and `session-control-poc`) in the auto-loaded memory index.

## Why we're here (the arc)
Perch started as a session *observer*. We explored *controlling* sessions and, dogfooding, converged on a
clear requirement: **a rich, Perch-native UI that does 100% of what the terminal can** — prompts,
**permission prompts**, slash commands, streaming, thinking, tools, mode, interrupt — a Claude-Desktop-class
client. Detours proven dead-ends for that goal:
- The **embedded ConPTY terminal** (`SessionTerminalWindow`) has an emulator-fidelity ceiling and can't
  render permission prompts/pickers richly → **dual-surface (terminal + rich UI) concept is dropped.**
- The **"attach to a normal terminal" nudge channel** works (live-spiked — see below) but is a
  teammate-framed nudge, not full control → not the path to 100% coverage.

## Decisions locked (by the user, this session)
1. **C# stream-json protocol**, not the TS Agent SDK. (There is **no .NET Agent SDK**; the SDK just wraps
   the CLI's `stream-json` protocol, which we already speak in C# via `ClaudeSessionController`.) Keep the
   ownership behind a clean boundary so a session-host *service* stays a later refactor, not a rewrite.
2. **Rich UI is the primary/only surface.** Forget dual-session. A genuine TUI is a later optional escape
   hatch via `--resume` handoff.
3. **Visual language = "Distinct Perch identity"** — warm, rounded, a little playful, on Perch's brand
   (`FixedColors.Brand` red-orange `#FF442D`); *not* a Claude clone. Completely rethink the current
   renderer's look (keep the plumbing).
4. **Mock the design first, sign off, then build.** (Phase 0 = the mockup above.)
5. **`perch` CLI mirrors `claude`:** `perch --resume [id]`, `perch [dir]`, etc. open the Perch rich window.
6. **Collision defenses** so a normal `claude` and a Perch-controlled session don't open the same id.

## What we learned this session (so you don't repeat it)
- **No .NET Agent SDK** exists (Python/TS only). We speak the `stream-json` wire protocol directly.
- **Socket injection into a *normal* terminal session — live-verified (Windows).** The `claude` binary
  (Bun-compiled, JS in plaintext) embeds the recipe: connect `CLAUDE_CODE_MESSAGING_SOCKET`, write
  `{"type":"auth","token":<CLAUDE_CODE_MESSAGING_TOKEN>}\n` then `{"type":"user","message":{"role":"user",
  "content":"…"}}\n`, close. The token is the **childToken**; on Windows presenting it = **own-child**, which
  **bypasses the peer approval gate**. A spike (`scratchpad/spike/`, now gone — it's in `docs/session-control-poc.md`
  §8a) proved delivery with no prompt — **but it's framed as a teammate message** ("Another Claude session
  sent a message… a peer cannot grant escalation"), never as the user, and can't answer TUI prompts or run
  slash commands. So it's a nudge channel only → **not** used for the 100%-coverage rich UI. (Kept as a
  possible future `AttachedSessionHost`.)
- **Terminal fidelity** (the emulator artifacts — dim-as-underline, dark logo, cursor jump): the "faint→
  underline" bug is **not** in our pinned `Iciclecreek.Avalonia.Terminal` 3.1.0 source (was a stale build);
  cursor/palette fixes are on `main` = `4.0.0-rc005`. **Already applied this session:** bumped to
  `4.0.0-rc005` + set `Options.TermName="xterm-256color"` + `COLORTERM=truecolor` in `SessionTerminalWindow`
  (builds clean; user dogfood owed). Since the terminal is being retired from the main path, this is a
  courtesy fix, not load-bearing.
- **Fix landed this session:** overlay row click on a Perch-owned embedded session now focuses the
  `SessionTerminalWindow` (`App.FocusSession` → `BringToFront`, matched on `Host.SessionId`).

## Design direction & open sign-off questions
Mockup = warm charcoal ground + brand red-orange accent; type = Bricolage Grotesque / Hanken Grotesk /
JetBrains Mono; turns as composed units (user bubble; Claude open prose under the bird mark; collapsible
thinking; tool-call cards; **permission prompt as a first-class card**). **Awaiting the user's read on:** the
red-orange-on-warm-charcoal identity strength, Bricolage as the display face, user-bubble-vs-open-assistant
asymmetry, and the tool/permission card treatment. **Revise the mockup to their feedback before Phase 1.**

---

## Plan (phased)

### Phase 0 — Design mockup ✅ (sign-off still owed)
`docs/session-ui-mockup.html`. Gate: user sign-off (and any revisions) — Phase 1 went ahead against the
mockup as drawn; revisions land in `SessionPalette`.

### Phase 1 — Rich stream-json client, redesigned (core coverage) ✅ shipped as code
A new `SessionWindow` binding to `ClaudeSessionController`, rendered in the signed-off visual language.
**Introduce a message/turn view-model** (roles, ordered blocks, tool-call cards with args+output+status) —
the current window has none, which is why grouping/cards have nothing to bind to. Preserve the reusable
*logic* from `SessionConsoleWindow` (event dispatch shape, streaming-delta→final-markdown swap, turn/queue
bookkeeping, mode-echo guard, permission-answer flow) and keep an equivalent **`FeedSampleForRender` headless
hook** so the UI is iterable via `-- render <dir>`.
- Conversation stream (user / assistant markdown / thinking / tools / cost), restyled.
- **Permission prompts as first-class UI** (`can_use_tool` → Allow / Allow+mode / Deny) — already proven in
  `SessionConsoleWindow`, re-skinned. This is the crown jewel.
- Composer (multi-line, Enter/Shift-Enter), live mode switcher, interrupt.
- **Session launcher**: New (default) or resume (reuse `SessionHistory.ListAll` picker), with the
  **refuse-if-live** collision guard (4a) before start.
- Controller change: pin `--session-id <fresh GUID>` for new sessions (id known before `init`, for the
  lock + defenses); `--resume <id>` for resume; `ControlledSessions.Register` + write the lock at launch.

### Phase 2 — 100% coverage (not started)
- **Slash commands:** verify which work over stream-json (init advertises `slash_commands`; spike — some are
  TUI-only); surface a command palette. **→ Broken out as a per-command checklist in
  `docs/session-slash-commands-plan.md`** (built-in commands only; skills excluded). Key finding: built-in
  commands **execute** when sent as plain text and return **markdown**, so most interop is palette + render;
  `/clear`, `/compact`, `/rename` mutate the session and need native reactions.
- **`AskUserQuestion` + plan approval (`ExitPlanMode`)**: arrive as `tool_use` blocks → first-class UI,
  answered via the permission/tool-result path.
- **Composer affordances:** `@`-file mentions + autocomplete, input history, model/mode/resume pickers.

### Phase 3 — `perch` CLI parity ✅ shipped as code
- Parse claude-like args at the top of `Program.Main` (before Velopack): `--resume [id]`, `--continue/-c`,
  `--session-id <id>`, positional `[dir]` → a normalized "open session" intent.
- At the single-instance mutex gate (`Program.cs`): if a tray is already running (`!createdNew`), **connect
  as a client to a new `perch-control[-dev]` named pipe** (mirror `ValetServer`/`ValetProtocol`), send the
  intent as newline JSON, print via `AttachParentConsole()`, `return 0`. If no tray yet, **stash the intent**
  (a static like `AutoStarted`) and consume it in the composition root after the overlay shows.
- Tray side: a control server modeled on `ValetServer`, started next to `_valetServer` in
  `App.OnFrameworkInitializationCompleted()`; marshals to `Dispatcher.UIThread`, opens the rich `SessionWindow`.
- PATH already handled by `IPathInstaller` (installed-dir exe named `perch`). No deep-link scheme exists.

### Phase 4 — Collision defenses ✅ shipped as code ((c) warns via systemMessage; no tray relay)
- **(a) Refuse-if-live:** before resuming/controlling id `X`, check `SessionMonitor.Scan()` live list; if `X`
  is running elsewhere, refuse (offer elevate/handoff). Covers the pre-`init` window during `--resume`.
- **(b) Ownership lock sidecar:** write `{sessionId}.perch-lock` beside the session JSON (`~/.claude/sessions/`)
  when Perch takes ownership; delete on exit; add to `perch-hook cleanup`'s sweep. Makes ownership
  cross-process visible (today `ControlledSessions` is in-process only).
- **(c) SessionStart guard:** `perch-hook start` (has the `session_id` at open time) checks the lock; if a
  *normal* `claude` opens a Perch-controlled id, warn / relay to the tray over the existing pipe — fail-open.
- **(d) Registry unification:** make `ControlledSessions` reflect every Perch-owned session so `Owns()`
  (valet skip, focus routing) is never wrong.

### Retiring the dual-surface
Remove `SessionTerminalWindow` + `SessionHost` + `SessionUiWindow` (the ConPTY dual-surface PoC) from the
primary flow; the rich `SessionWindow` is the destination for tray + `perch` CLI + elevate. Keep a clean
`ISessionHost`-style boundary around the stream-json ownership so it can become a session-host service later.

## Reuse / file map (verified this session)
- **Reuse verbatim** (all `internal`, in `Perch.Core/Data/Control/`, so the new UI stays in `Perch.App`):
  `ClaudeSessionController` (events `EventReceived`/`Exited`; `SendPrompt`/`RespondToPermission`/
  `SetPermissionMode`/`Interrupt`/`Stop`; registers id from `system/init`), `StreamJsonParser.Parse(line)`,
  `SessionEvents` (init/text-delta/assistant-text/thinking/tool-use/tool-result/permission-request/
  mode-changed/turn-result), `ToolSummary.Describe(name,input)`.
- **Render engine (keep, extend):** `Perch.App/Rendering/MarkdownView.cs` — `Build(md, MarkdownStyle)`; its
  type-scale/spacing/**body font** are private hard-coded constants → **extend `MarkdownStyle` additively**
  with size/spacing/body-font rather than fork.
- **Logic reference (replace pixels):** `Perch.App/Windows/SessionConsoleWindow.cs`. **No message model** —
  introduce one.
- **Theming:** `Perch.App/Theming/Palette.cs` (+ `Perch.Theming.Theme`/`FixedColors` in Core). Alias cached
  `Palette.*Brush` fields (never snapshot `Color`). Palette has **no chat-specific roles** — compose from
  `SurfaceRaised`/`OverlayRowHover`/`Track`/`Accent`/`FixedColors.Brand` at reduced alpha (`Rgb.ToColor(alpha)`),
  or add new `Theme` roles via the CLAUDE.md checklist. Brand hue anchors the identity. Real values:
  Midnight (default dark) Surface `#181820`, text `#E1E1EB`, muted `#8C8CA0`, Accent(blue) `#60A5FA`;
  Ember (warm dark) Surface `#181615`; Brand `#FF442D`/hover `#FF6854`.
- **CLI seam:** `Program.cs` arg if-ladder + single-instance mutex (`return 0` on 2nd launch = the
  forward-or-stash spot); `ValetServer`/`ValetProtocol` = copyable named-pipe pattern; `AttachParentConsole()`.
- **Collision primitives:** `SessionMonitor.Scan()` (live = `{pid}.json` with that `sessionId` + PID alive;
  key defenses on `sessionId`, liveness via PID probe), `ControlledSessions`, the `{sessionId}.mode/.notify/
  .history` sidecar pattern, `perch-hook` `start`/`cleanup` (`src/Perch.Hook/Program.cs`), `SessionTerminator`.
- **New files:** `Perch.App/Windows/SessionWindow.cs`, a `perch-control` pipe protocol/server, a
  `{sessionId}.perch-lock` helper (Core), CLI arg parser.

## Verification
- `dotnet build perch.slnx` (both heads) + `dotnet test tests/Perch.Tests/…` green; add `StreamJsonParser`
  fixtures for new shapes (slash commands, AskUserQuestion tool_use) and a `perch-control` protocol test.
- Live dogfood: launch from tray and from `perch --resume <id>`; drive a full turn incl. a permission prompt
  answered in-UI, a slash command, an `AskUserQuestion`; confirm streaming/thinking/tools render.
- Collision: session live in a real terminal → Perch refuses to control that id; Perch-owned session →
  `perch-hook start` warns when a normal `claude` opens it; verify `{sessionId}.perch-lock` lifecycle.
- Screenshots to `./captures/` (desktop app; on Windows use `-f net10.0-windows10.0.19041.0`).

## How to resume (checklist for the new session)
1. Read the "What shipped" and "Owed / open" sections above; `dotnet build perch.slnx` + `dotnet test` must be green.
2. Dogfood live (the list under "Owed"); fix what breaks. Screenshots → `captures/`.
3. Get the design sign-off; revise `SessionPalette` (and the mockup, re-published to the **same** artifact URL).
4. Delete the retired dual-surface files + the terminal package once agreed; then Phase 2.
