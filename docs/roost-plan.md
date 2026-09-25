# Roost — design & checkpoint plan

**Status (2026-09-25):** design signed off, checkpoint plan below; nothing built yet. Branch `roost`.
Mockup: `docs/roost-mockup.html` (published: https://claude.ai/artifact/9JRcPWR2J7ZvtYr3Fgsrk8). The mockup
was signed off as-is, but it predates two decisions: the **collapse model** and the **"Roost"** name
(it still says "Command center"). Where the two disagree, this doc wins.

Formerly "Command center" (`docs/command-center-plan.md`). It was renamed to **Roost** (where the birds gather).

## Decisions (Q&A, 2026-09-25)
| Question | Decision |
|---|---|
| What a checkpoint is | A small, independently committable slice that clears the **green gate** (below). No mandatory user pause between checkpoints. |
| P0 mockup sign-off | Done. The mockup is accepted as-is, so P0 is dropped. |
| Ended sessions | Stay for **10 minutes, greyed out**, then drop (or sooner if closed). The History window covers older ones. |
| Past 4/6 panes | **Keep the 2×2 grid.** No chip strip. A **collapse** model (mini cards) shows what's happening cheaply, and panes expand when you're needed. |
| Collapsed pane shape | **Mini card:** header + the last 2–3 activity lines. |
| Pane needs you while collapsed | **Auto-expands, unless you're typing**: held (pulsing) until you pause or send. |
| Default collapse state | **By status:** Needs you / Done·review expanded; Working / Quiet / Ended collapsed. A manual toggle pins that pane's state. |
| Layouts | Keep all three (Tiled, Main + stack, Zoom). |
| Packing in Tiled | Mini cards **stack inside a grid cell**. An expanded pane takes a whole cell. First-seen order is preserved. |
| External sessions | Read-only + Focus terminal. **Take over in Perch** comes in P3 and must show a **confirm modal**. |
| Name | **Roost** |
| P1 data source for Perch panes | **In-memory** `PerchSession.Conversation` from P1 (read-only), not disk. |
| Reduced motion | **The OS preference**, via a new Core platform interface. |
| Global hotkey | Moved into **P1**, because dense mode otherwise only has the tray menu. |
| HistoryWindow tail migration | Its **own checkpoint in P1**, straight after the tail reader lands. |

## The ask (from a user request)
A tmux-style view that opens to show more than the overlay's one-line-per-session rows:
1. **Session history with live tailing:** every session's conversation, growing as it happens.
2. **Perch-controlled sessions are interactive:** reply, answer permissions/questions/plans, interrupt.
3. **Statuses still draw the eye:** "needs attention" (done, unreviewed) and "requires input" (blocked).
4. **An entry point:** a small glyph, right-aligned on the overlay's `+ New session` row.

## Shape of the window
One reused top-level window, `RoostWindow` (created lazily, shown through `WindowHost.ShowOrFocus`, closed in `CloseAuxWindows`).

```
┌ Roost ── [2 need you] [3 working] [1 done] ─────────── Tiled · Main+stack · Zoom ── + New session ┐
│ NEEDS YOU          │ ┌─ perch-api ◆ ── Needs your input · 2m ┐ ┌─ infra ▣ ─────── Working · 2m ┐ │
│  ● perch-api   2m  │ │  …thread (live)…                       │ │ ▸ Bash terraform plan…         │ │
│  ● web-dash    5m  │ │  ┌ Allow Bash: dotnet test? [Allow][Deny]│ ├─ perch ◆ ─────── Working · 41s ┤ │
│ DONE · REVIEW      │ │  └──────────────────────────────────────┘│ │ ▸ Read 3 files                 │ │
│  ● infra       4m  │ │ [Reply…                         ] [⤢]    │ ├─ docs-site ◆ ────────── Idle ──┤ │
│ WORKING            │ └──────────────────────────────────────────┘ │ ✓ Done: "Fixed the links"      │ │
│  ● perch      41s  │ ┌─ web-dash ▣ ── Done 4m ago · review ────┐ └────────────────────────────────┘ │
│  ● docs-site  12s  │ │  …thread (tailed, read-only)…           │   (4th cell free)                  │
│ QUIET              │ │  Waiting in Windows Terminal [Focus ↗]  │                                    │
│  ● billing-svc     │ └──────────────────────────────────────────┘                                    │
└ Ctrl+1–9 pane · Ctrl+. next needing you · Ctrl+Shift+E expand/collapse · Ctrl+Shift+Z zoom · Esc ─┘
```

- **Left rail = the attention queue.** Every live session, grouped by urgency: *Needs you* (AwaitingInput,
  ApiError) → *Done · review* (NeedsAttention) → *Working* (Running) → *Quiet* (Idle, Ended greyed). Each row
  shows the status dot, the name, elapsed time ("waiting 2m" from `AwaitingElapsedLabel`) and the Perch mark for
  controlled sessions. Clicking a row focuses that pane: it scrolls the pane into view, expands it if collapsed,
  and zooms it in Zoom layout. The rail itself is collapsible.
- **Panes keep their order.** Every layout lays panes out in first-seen order, and a status change **never
  reorders** them. An expand or collapse can move later panes along (see Packing), but it never swaps their
  order. Urgency shows through the rail's ordering and the pane chrome.
- **Three layouts** (a persisted segmented toggle):
  - **Tiled:** a fixed **2×2 cell viewport**. More cells scroll in a row at a time, with row snap. A floating
    "↓ N more" pill counts off-screen panes, and turns the awaiting hue with "· 1 needs you" when a blocked
    pane is out of view.
  - **Main + stack:** tmux main-vertical. The focused pane is expanded and large, and every other pane sits in
    the stack as a mini card.
  - **Zoom:** one pane, like tmux `prefix z`. The rail picks which one.
- **The summary chips in the title bar double as filters.** Clicking "2 need you" shows only those panes.

### Collapse model (new since the mockup)
Each pane is **Expanded** or **Collapsed** (a mini card). One pure resolver decides which:

1. **Manual pin wins.** Expanding or collapsing a pane by hand (header double-click, `Ctrl+Shift+E`, or the
   pane menu) pins that state for the session. "Auto size" in the pane menu clears the pin. Pins live in memory
   in the roster and survive closing and reopening the window, but not an app restart.
2. **Otherwise the status decides.** AwaitingInput / ApiError / NeedsAttention → Expanded. Running / Idle /
   Ended → Collapsed.
3. **Typing hold.** An auto-expand is **held** while the user is typing in any pane's composer, meaning the
   composer is focused and there was a keystroke in the last ~2s. While held, the collapsed card pulses in the
   status hue. The hold releases on send, on composer blur, or after the pause. (P1 has no composer, so the
   hold input is always false there, but the resolver supports it from the start.)
4. **The focused pane never auto-collapses.** If it would, the collapse waits until focus moves off it, so a
   pane you're reading doesn't shrink under you once you've acknowledged it.

**Mini card:** the pane header plus the last 2–3 activity lines, built by a pure `ActivitySummary`:
- Recent tool calls: tool, target and collapsed result, via the existing `ToolResultFormat` summaries.
- A pending permission or question ("⚠ Allow Bash: dotnet test?").
- The final assistant prose on completion ("✓ Done: …").

**Packing (Tiled):** panes flow through the cells in first-seen order.
- An Expanded pane takes a whole cell. If the current cell already holds mini cards, the expanded pane starts
  the next cell.
- Mini cards stack in a cell up to its capacity, derived from the cell height and the measured mini-card
  height (`RoostLayout.CellCapacity`: as many whole cards as fit, never fewer than 1; 3–4 at normal window
  sizes).
- A pure `RoostLayout.Pack(panes, capacity)` returns the cells. The "N more / needs you" pill reads the
  off-screen cells from the same result.

## Pane anatomy
- **Header:** status dot · project name · origin badge (◆ Perch / ▣ terminal / IDE glyph from the IDE-host
  work) · branch · model · **status pill** (right) · context thermo · pane menu (⋯).
- **Pane menu:** expand/collapse · auto size · open full window · focus terminal · copy resume · close pane.
- **Body (Expanded):** `SessionThreadView` in a new **compact density**: smaller type, tighter bubbles, tool
  cards collapsed, and read-only tool runs folded as today.
- **Body (Collapsed):** the `ActivitySummary` lines only. No `SessionThreadView` is created.

The footer depends on origin:

| | Perch-controlled | External terminal / IDE |
|---|---|---|
| Source | in-memory `PerchSession.Conversation` (streaming deltas, no disk hop), **from P1** | transcript `.jsonl` tailed from disk (`TranscriptTailReader`) |
| Permission / question / plan | P1: shown, plus "Answer in window ⤢". P2: **answerable in the pane** | shown as the last tool call; not answerable |
| Footer | P1: ⤢ open full window. P2: compact composer (Enter send, Shift+Enter newline, Esc interrupt) + ⤢ | "Tailing · read-only" + **Focus terminal ↗** (`IWindowActivator`). P3 adds **Take over in Perch** (confirm modal) |

## Status treatment (the "draw the eye" requirement)
Hues come from the existing semantic theme roles (`Palette.Status*`), so custom themes and the CVD preview
carry over.

| Status | Pane chrome | Status pill | Rail group | Collapse default | Extra |
|---|---|---|---|---|---|
| AwaitingInput | 2px ring in `StatusAwaiting`, **slow pulse**, header wash | "Needs your input · 2m" | Needs you | Expanded (auto-expands; typing hold) | overlay glyph badge dot |
| ApiError | 2px ring `StatusError`, steady | "API error · 529 Overloaded" | Needs you | Expanded | badge dot |
| NeedsAttention | 1.5px ring `StatusAttention`, steady | "Done 4m ago · review" | Done · review | Expanded | cleared on pane focus → `SessionMonitorHost.Acknowledge(pid)` |
| Running | none; typing dots in thread | "Working · 41s" | Working | Collapsed | — |
| Idle | dimmed header | "Idle" | Quiet | Collapsed | — |
| Ended | greyed pane | "Ended 3m ago" | Quiet (greyed) | Collapsed | dropped after 10 min |

The pulse uses one shared breathing curve, extracted from `OverlayCanvas.PulseIntensity` (the account-guardrail
pulse). Under the **OS reduced-motion preference** it falls back to a steady, thicker ring.

Other attention behaviour:
- A new *Needs you* arrival while the window is inactive flashes the taskbar (P3).
- The existing toasts keep working.
- In P2, a session whose pane is **expanded and on-screen while Roost is active** counts as "seen", so there's
  no toast for something already in front of you.

## The overlay entry point
The `+ New session` row (`OverlayCanvas.Sections.cs: DrawNewSessionRow`) gains a right-aligned ~18px hit box,
with its own hover wash and the tooltip "Roost". It uses the same idiom as the "+" on collapsible headers. The
new-session band shrinks to leave room for it. Pressing it raises `RoostRequested`.

**Glyph: "split panes".** A rounded rectangle split into a tall left pane and two stacked right panes (tmux
`main-vertical`).
- It's stroked at 1.2px in `MutedBrush` (`Palette.AccentBrush` on hover) and drawn by a new
  `DrawRoostGlyph(ctx, brush, cx, cy)`.
- **Badge:** when any session is AwaitingInput or ApiError, a 4px dot sits at the glyph's top-right in
  `StatusAwaiting`/`StatusError`.

Dense strip mode has no `+ New session` row, so Roost is also reachable from the **tray menu** ("Roost…", next
to "New session…") and a **global hotkey** (`HotkeyOpenRoost`, default **Alt+Shift+R**, not taken by the
existing Alt+Shift+W/S/Space or Ctrl+Shift+W bindings). Both are in P1.

## Architecture

### New, UI-free (Perch.Core, unit-tested)
- **`RoostRoster`** (pure). Takes the `ClaudeSession` scan result, the Perch-owned ids and the persisted closed
  ids, plus a clock. It returns:
  - ordered panes (stable, first-seen)
  - rail groups (by urgency)
  - summary counts
  - `NextNeedingYou(afterId)`
  - the manual collapse pins

  Ended sessions linger for 10 minutes, then drop. The closed ids are pruned to the ids still present.
- **`RoostLayout`** (pure): the collapse resolver (pin → status → typing hold → focused-never-collapses) and
  `Pack` for Tiled cells, the Main + stack arrangement, and the off-screen count including the "needs you" flag.
- **`ActivitySummary`** (pure): a conversation model in, the last 2–3 mini-card lines out.
- **`TranscriptTailReader`**: byte-offset tailing that carries a partial trailing line over to the next read.
  It opens with `FileShare.ReadWrite` and resets when the file is truncated or replaced. It starts at the last
  ~600 lines. **Why:** `HistoryWindow` re-reads the whole transcript on every append (`HistoryWindow.cs:442`,
  `TailNow` → `ReadCompleteLines`). That's fine for one window, but with several panes on multi-MB transcripts
  it becomes quadratic IO.
- **`IMotionPreference`** (`Perch.Core/Platform`): `bool ReduceMotion`. The Windows implementation reads
  `SystemParametersInfo(SPI_GETCLIENTAREAANIMATION)`. The Mac one is a stub returning false (the port can
  implement it later with `accessibilityDisplayShouldReduceMotion`). It's resolved through `PlatformServices`.

### New, UI (Perch.App)
- `Windows/RoostWindow.cs`: rail + layout host + key handling.
- `Views/SessionPane.cs`: header + body (`SessionThreadView` or mini card) + footer. `BindControlled(PerchSession)`
  / `BindTailed(HistoryEntry)`. Expanding creates the thread view; collapsing drops it.
- `Services/TranscriptTailHost.cs`: a `FileSystemWatcher` + debounce around `TranscriptTailReader`, run off the
  UI thread and posted back with `Dispatcher.UIThread.Post` (the `*MonitorHost` pattern), with closed-window
  guards. It's shared with `HistoryWindow`.
- `Rendering/Pulse.cs`: the shared breathing curve, taking reduced motion into account. The overlay guardrail
  pulse is moved onto it.
- `SessionThreadView`: a new `Compact` density flag (the additive `MarkdownStyle` knobs already exist).
- `HeadlessRenderer`: `roost_*` surfaces seeded from `SampleData`: one pane per status, both origins, a mix of
  collapsed and expanded, all three layouts, and the overflow pill.

### Wiring
- The app already hands each `SessionWindow` its `ClaudeSession` after every scan (`UpdateBackgroundActivity`).
  Do the same with `RoostWindow.UpdateRoster(sessions)`.
- Perch-controlled panes match on `ClaudeSession.PerchControlled` + `SessionId` against `App._perchSessions`.
  In P2, permission and question answers go through `SessionThreadView.PermissionAnswered`/`QuestionAnswered`
  → `PerchSession.AnswerPermission`/`AnswerQuestion`, the same as `SessionWindow`.
- **One conversation, two views:** a session can be open in its `SessionWindow` *and* in a pane. `Bind`
  subscribes per view, so both follow along. Answering in one turns the other's card into a receipt through
  conversation state. **Verify live.**
- Collapsed, off-screen or filtered-out panes hold **no `SessionThreadView`**. Their conversation model, the
  Core model that tailed panes feed via `AppendTranscriptLine`, stays live and cheap, so mini cards and status
  stay current.
- **Take over (P3)** reuses `App.OnElevateToPerch`, which already shows the "Elevate to Perch?" confirm
  (`App.axaml.cs:801`). The pane then rebinds in place, from tailed to controlled, on the same session id.

### Settings (registry-driven, per CLAUDE.md)
- `ShowRoostButton` (bool, default on) + `SettingDescriptor` (surface: SessionRow, `PreviewTarget.RoostButton`)
  + a canvas gate `SetShowRoostButton` in `OverlaySettingsGates`.
- `HotkeyOpenRoost` (`HotkeyBinding`, Alt+Shift+R): a row on the Settings → Shortcuts page plus a `NotSettings`
  entry (the existing hotkeys' pattern), registered with the other hotkeys in `App`.
- Non-settings (in `SettingsRegistryTests.NotSettings`): `RoostLayout`, `RoostRailCollapsed`, `RoostClosedPanes`.

### Keyboard (inside the window)
| Key | Action |
|---|---|
| `Ctrl+1…9` | focus pane |
| `Ctrl+.` | next session needing you (cycles *Needs you*, then *Done*); expands it |
| `Ctrl+Shift+E` | expand/collapse the focused pane (pins it) |
| `Ctrl+Shift+Z` | zoom toggle |
| `Esc` (P2) | interrupt the focused Perch pane, or deny a pending permission when the composer is empty (matches `SessionWindow`) |
| `Enter` (P2) | on a pending permission with an empty composer = Allow |

---

## Checkpoints

### The green gate (every checkpoint)
A checkpoint is done, and gets committed on `roost`, when:
1. `dotnet build perch.slnx` succeeds (on Windows this builds **both** heads).
2. `dotnet test tests/Perch.Tests/Perch.Tests.csproj` is green, including `UiConventionTests` and
   `SettingsRegistryTests`.
3. **UI checkpoints:** a `-- render <dir>` pass has been eyeballed for the touched surfaces, in both a dark and
   a light theme. `AppSettings.DisablePersistence` stays honoured, so render never writes settings.
4. The checkpoint's own **Done when** list is met, and anything that needs a live session is logged under
   *Owed live checks* rather than blocking the checkpoint.

There's no mandatory pause between checkpoints.

### P1 — Read-only Roost (asks 1, 3, 4)

**CP1 · `RoostRoster`** (Core)
- Scope: pane order (first-seen, stable across rescans), urgency groups, summary counts, `NextNeedingYou`
  (cycles Needs you → Done, wraps), ended linger (10 min with an injected clock), closed ids (hidden, pruned
  when the session is gone), manual collapse pins.
- Done when: xUnit covers reorder-immunity on a status flip, ended expiry, the `NextNeedingYou` wrap and closed
  pruning.

**CP2 · `RoostLayout`** (Core)
- Scope: the collapse resolver (pin, status default, typing hold, focused-never-collapses); `Pack` (an expanded
  pane takes a fresh cell, mini cards fill up to capacity, order preserved); the Main + stack split; the
  off-screen count and the needs-you flag for a given viewport.
- Done when: tests cover the resolver truth table, packing edge cases (expand mid-cell, capacity 3 vs 4, all
  collapsed, all expanded) and the overflow count.

**CP3 · `TranscriptTailReader`** (Core)
- Scope: offset tail, partial-line carry, reset on truncation or replacement, initial "last ~600 lines" seek,
  tolerant of malformed lines.
- Done when: fixture-append tests cover whole lines, a line split across two appends, truncation, and replace
  with a shorter file.

**CP4 · `TranscriptTailHost` + HistoryWindow migration** (App)
- Scope: extract the watcher/debounce/off-thread tail out of `HistoryWindow` into `TranscriptTailHost` over
  `TranscriptTailReader`, and move `HistoryWindow` onto it.
- Done when: the history viewer still live-tails with no visible change (history render surface unchanged), and
  a whole-file re-read no longer happens per append.

**CP5 · `ActivitySummary` + compact density**
- Scope: the Core `ActivitySummary` (tests: tool lines via `ToolResultFormat`, pending permission, done prose,
  empty conversation); the `SessionThreadView.Compact` density flag.
- Done when: tests are green and a compact-density render surface looks right.

**CP6 · Reduced motion + shared pulse**
- Scope: `IMotionPreference` (Windows implementation + Mac stub + `PlatformServices`); `Rendering/Pulse`
  extracted from `OverlayCanvas.PulseIntensity`, with the overlay guardrail pulse moved onto it.
- Done when: both heads build, and the guardrail pulse behaves as before with reduced motion off and turns into
  a steady ring with it on (a render surface for each).

**CP7 · `RoostWindow` + `SessionPane`, Tiled**
- Scope:
  - the window through `WindowHost.ShowOrFocus` and `CloseAuxWindows`
  - the rail (groups, collapsible, click-to-focus)
  - `SessionPane` (header, status chrome and pill, pulse, mini card or expanded body, pane menu subset)
  - Tiled packing via `RoostLayout`, with row-snap scroll and the overflow pill
  - data: tailed panes via `TranscriptTailHost`, Perch panes bound read-only to `PerchSession.Conversation`
    (cards visible with "Answer in window ⤢")
  - `UpdateRoster` wiring from the scan
  - `roost_tiled` render surfaces
- Done when: render shows every status, both origins, mixed collapse and the overflow pill; expanding and
  collapsing creates and drops the thread view (no leaked `SessionThreadView` for collapsed panes).
- As built:
  - **Tiled pages by whole rows** (wheel / PgUp·PgDn / the pills) instead of free scrolling. The row snap comes
    for free, and only the visible 2×2 cells hold controls, so an off-screen pane is parked with no thread.
  - The visible cells are rebuilt only when the layout's shape changes, so a scan that only changes text
    refreshes the panes in place and an expanded thread keeps its scroll.
  - The ↑/↓ pills live in a bottom bar alongside the key hints, so they never cover a pane.
  - **Answering permissions and questions in a Perch pane landed here**, pulled forward from CP10: a live card
    with dead Allow/Deny buttons was worse than routing the answer (the same `PerchSession.AnswerPermission`/
    `AnswerQuestion` path `SessionWindow` uses). CP10 keeps the keyboard shortcuts and the two-view
    receipt check.
  - The Roost lists interactive sessions only; autonomous SDK runs stay out, as they do from the overlay's main
    list.
  - Status hues are `SessionPalette` roles (theme-derived), which gained `Attn`/`AttnWash`/`ErrWash`/`Idle`.

**CP8 · Main + stack, Zoom, filters, keyboard**
- Scope: the other two layouts (layout persisted), summary-chip filters, the keyboard table (except the P2
  keys), acknowledge-on-focus (`SessionMonitorHost.Acknowledge`), Focus terminal ↗, ended greying.
- Done when: a render surface exists per layout and the keyboard paths have been walked by hand.
- As built:
  - Layout persists in `AppSettings.RoostLayout` (a `NotSettings` entry).
  - A chip filters the stage to its group; click it again to clear. Focusing a filtered-out pane (rail, keys)
    lifts the filter, and a filter whose group empties clears itself.
  - The mouse wheel over an expanded thread scrolls that thread; only the gaps between panes page.
  - Window chords tunnel so a focused thread can't swallow them, while PgUp/PgDn bubble so a thread's own paging
    still works.
  - Ctrl+Shift+E outside Tiled switches to Tiled first, since pins are a Tiled concept.
  - An errored session's closing text reads as an error line, never "Done".
  - Captures: `roost_mainstack_1x`, `roost_zoom_1x`, `roost_tiled_filtered_1x`. The keyboard can't be driven
    headlessly, so walking the keys is an owed live check.

**CP9 · Entry points**
- Scope:
  - the overlay split-panes glyph + badge in `DrawNewSessionRow`, with a shrunk new-session band and
    `RoostRequested`
  - `ShowRoostButton` setting + descriptor + `SetRoostButton` gate
  - the tray "Roost…" item
  - `HotkeyOpenRoost` (default Alt+Shift+R) + descriptor + registration
- Done when: registry coverage passes, the overlay render shows the glyph (plain, hover, badged, gated off), and
  the Settings preview reflects the gate.
- As built:
  - **Hotkeys aren't catalogue descriptors here.** They live on the Settings → Shortcuts page (`AddHotkeyRow`)
    and in `NotSettings`, like the other four. So `HotkeyOpenRoost` is a Shortcuts row, registered in
    `RegisterHotkeys`, and forced to the front, because a background tray can't take focus.
  - `ShowRoostButton` is a SessionRow toggle with a new `PreviewTarget.RoostButton` catalogue chip.
  - Badge: an API error (red) outranks awaiting input (yellow). A cut-out ring behind it keeps it legible on the
    glyph's stroke.
  - The dwell tooltip says "Roost · Every session, side by side · N need you".
  - The tray gains "Roost…" after "New session…".
  - The button isn't drawn in the Settings Rearrange preview.
  - Captures: `overlay_roost_hover_1.5x`, `overlay_roost_off_1x`; the plain and badged states are in every
    overlay capture.

**P1 exit:** everything in *Owed live checks* for P1 has been tried once against real sessions.

### P2 — Interactive Perch panes (ask 2)

**CP10 · Answerable cards**
- Scope: the permission, question and plan cards in expanded Perch panes answer through `PerchSession`; the
  pane's Enter/Esc keys for a pending permission; the other view (the open `SessionWindow`) turns into a receipt.
- Done when: an answer from either view resolves the item in both (live check), and render shows pending and
  receipt states.
- As built:
  - The card answer routing landed in CP7. CP10 added the focused Perch pane's keys, mirroring `SessionWindow`
    and the TUI: Enter allows a pending permission (never a question, which is answered by picking); Esc denies
    it, or with nothing pending interrupts a running turn (`InterruptRequested` → `PerchSession.Interrupt`).
  - The keys bubble, so a future composer or a focused control gets first refusal.
  - Terminal/IDE panes ignore them, since they have no control channel.
  - Captures: `roost_zoom_1x` (pending) → `roost_zoom_answered_1x` (receipt, working again).

**CP11 · Lite composer**
- Scope: a compact composer in Perch pane footers (Enter send, Shift+Enter newline, Esc interrupt, queue while
  running) + ⤢ open full window; feeds the **typing hold** into `RoostLayout`.
- Done when: a pane going to needs-you while you type in another pane's composer stays collapsed and pulsing,
  then expands after the pause or send (live check), and the resolver tests cover it.
- As built:
  - **`TypingHold`** (Core, tested): typing = a composer is focused and a keystroke landed within 2s. It
    releases on send, on blur, or after the pause. `ReleasesAt` drives a one-shot timer that re-runs layout, so
    a held pane expands the moment the hold lapses. It feeds `RoostSizeInputs.TypingElsewhere`.
  - **Composer:** a wrapping `TextBox` in Perch pane footers.
    - Enter sends; with nothing typed it allows a pending permission. Shift+Enter is a newline. Esc denies, or
      interrupts. These are tunnel-handled so they beat the TextBox's own Enter.
    - The placeholder follows state: "Reply — or Enter to allow, Esc to deny" / "Queue a message… (Esc
      interrupts)" / "Reply…".
    - Sending goes through `PerchSession.SendPrompt`, which records the user message itself. The CLI queues a
      prompt sent mid-turn.
  - **The footer is built once and only re-labelled**, so a half-typed draft survives every scan.
  - **Layout passes now diff per cell** (and Main + stack / Zoom per container) instead of rebuilding the
    visible stage on any change. Re-parenting a pane drops keyboard focus, so the composer being typed in is
    never moved unless its own cell changes. A layout switch still empties everything.
  - The pane's answer, interrupt and send handlers look up the session id at event time, since a `/clear`
    swaps it under the same pane.
  - Captures: `roost_tiled_held_1x` ("perch" needs you while "api" is being typed in: it stays a pulsing mini
    card), plus the composer in `roost_tiled_1x` / `roost_zoom_answered_1x`.

**CP12 · Attention de-dup**
- Scope: `SessionAttention`'s `windowActive` input becomes "seen" = its `SessionWindow` is active, **or** Roost
  is active and the pane is expanded and on-screen. This is a pure predicate with tests.
- Done when: no toast fires for a permission already visible in an active Roost pane, and toasts still fire when
  Roost is open but inactive, or the pane is collapsed or off-screen.
- As built:
  - **`AttentionSeen.Seen(ownWindowActive, roostActive, paneOnScreen)`** (Core, tested alongside
    `SessionAttention`) feeds `SessionWindow.MaybeAlert`'s "active" input through an app-supplied `RoostView`.
  - **Changed from the plan: a collapsed pane counts as seen.** "On screen" means placed by the current layout,
    expanded or as a mini card, because the mini card shows the "⚠ Allow …" line too. Also, a Perch permission
    usually arrives before the next scan has expanded its pane, so "expanded only" would still toast for a prompt
    the user is looking at. Paged, filtered or zoomed away is not seen.
  - When the Roost is deactivated, open session windows re-evaluate, so a prompt that was only seen in the Roost
    still gets its toast once the user looks away (the existing once-per-item latch).
  - **Wider than planned:** the monitor's Done / Waiting-for-input / API-error toasts (toast, chime, push) also
    skip a session the active Roost shows. The overlay's attention flash still fires.

### P3 — Polish

**CP13 · Take over in Perch**
- Scope: an external pane's footer and menu gain "Take over in Perch". It goes through `App.OnElevateToPerch`,
  so the **confirm modal** ("Elevate to Perch?") is always shown. After takeover the pane rebinds in place, from
  tailed to controlled.
- Done when: Cancel leaves the terminal session untouched, and a confirmed takeover rebinds the same pane
  without it moving (live check; also verify the session id is kept across `--resume`).
- As built:
  - A footer button and a pane-menu item on eligible terminal panes. Eligibility is the overlay's Elevate rule,
    now shared as `App.CanElevate`: `entrypoint == "cli"` and not already Perch-owned.
  - Both go through `App.OnElevateToPerch`, which now takes the requesting window, so the "Elevate to Perch?"
    **confirm is modal over the Roost**. Nothing is stopped before Elevate is clicked.
  - **Rebinds in place:** `RoostRoster.AdoptContinuations` (tested). A live process whose session id matches a
    lingering ended pane takes that pane's slot and inherits its pin, and the ended pane goes. That covers both
    orders: the old process ending first, or a scan briefly showing both. It also makes any `claude --resume`
    of a still-lingering pane come back in place.
  - Capture: `roost_tiled_1x` shows the button on the terminal pane.

**CP14 · Taskbar flash**
- Scope: a new `IWindowChrome.FlashTaskbar` (Windows `FlashWindowEx`; Mac stub, later a dock bounce); fires on a
  new *Needs you* arrival while Roost is inactive.
- Done when: both heads build and the flash has been checked live.
- As built:
  - `IWindowChrome.FlashTaskbar`. Windows uses `FlashWindowEx(FLASHW_TRAY | FLASHW_TIMERNOFG)`: the taskbar
    button flashes until the Roost is activated, and nothing happens if it's already foreground. The Mac stub
    now does a real `requestUserAttention:` informational Dock bounce, unverified on a Mac.
  - The trigger is `RoostRoster.NeedsYouArrivals` (tested): panes that entered Needs you in the latest scan. A
    session that stays blocked doesn't re-flash; one that blocks again does.
  - Only fires from `RosterChanged` (a scan) while the Roost isn't the active window.

**CP15 · Shared `SessionComposer`**
- Scope: extract the full composer out of `SessionWindow` (slash palette, `@`-mentions, input history, the
  highlight overlay) into a shared control, and swap the pane's lite composer for it.
- Risk: this is the riskiest checkpoint. `SessionWindow.cs` is ~3.7k lines, and the composer highlight overlay
  has regressed three times.
- Done when: the `session_composer_long` render capture is unchanged, the composer tests are green, and both
  surfaces have been checked by hand for caret drift and clipped lines.

**CP16 · Pane close + load earlier**
- Scope: pane close / reopen through the menu (backed by the CP1 closed ids) + a "load earlier" affordance for
  tailed panes past the initial ~600 lines.
- Done when: a closed pane stays closed across rescans and reappears if the session starts again.
- As built:
  - "Close pane" in the pane menu. Closed panes leave both the grid and the rail.
  - A faint **"N hidden"** chip in the title bar opens a menu: "Reopen {name}" (which focuses it) or "Reopen
    all".
  - Persisted as `AppSettings.RoostClosedPanes` (`NotSettings`) and seeded with `RoostRoster.SeedClosed`, since
    the roster exists before settings load. Pruned to live sessions as the roster runs, so a new process is never
    pre-closed.
  - **Load earlier:** a tailed pane that started from the last 600 lines shows a "Showing the last 600 lines ·
    Load earlier" strip atop its thread. It re-tails the whole transcript into a fresh conversation
    (`RoostFeed.LoadEarlier`), and the strip hides once everything is loaded.
  - Fixed along the way: `EnsureThread` could try to re-parent an already-parented thread when a refresh landed
    between a tail reset and its change notice. It now only rebinds a materialised thread.
  - Capture: `roost_tiled_hidden_1x`. The load-earlier strip needs a real >600-line transcript (the renderer
    never reads `~/.claude`), so it's an owed live check.

**Why a lite composer before CP15:** `SessionWindow`'s composer is tangled up with the launcher, the pills and
the highlight layer. Extracting it isn't needed to meet the ask, so it waits until everything else is proven.

## Owed live checks (log results here as they're done)
- [ ] CP4: the history viewer still live-tails an **active** session (now through `TranscriptTailHost`). Render is
      unchanged and a fixture round-trip test (tail in pieces = read whole) is green, but the watcher path needs
      a real session.
- [ ] P1: a real terminal session tails into a pane, and the mini card updates while collapsed.
- [ ] CP8: walk the keyboard — Ctrl+1–9, Ctrl+. (cycles Needs you → Done), Ctrl+Shift+E, Ctrl+Shift+Z (and
      back), PgUp/PgDn (and a focused thread's own PgUp still scrolls it); focusing a done-review pane clears its
      overlay badge.
- [ ] CP7: answering a permission in a Perch pane resolves it, and the open `SessionWindow`'s card turns into a
      receipt. The Roost can't be opened until CP9 adds the entry points, so this is owed from then.
- [ ] P1: a Perch session streams live into a pane (in-memory bind) while also open in its `SessionWindow`.
- [ ] P1: a status flip to AwaitingInput auto-expands a collapsed pane, and the badge shows on the overlay glyph.
- [ ] P1: the Alt+Shift+R hotkey opens Roost from dense mode.
- [ ] P2: one conversation in two views: an answer in the pane turns the window's card into a receipt, and the
      other way round.
- [ ] P2: the typing hold works under a real burst of activity.
- [ ] CP11: send from a pane composer (idle and mid-turn/queued); Enter-to-allow / Esc-to-deny / Esc-interrupts
      from the composer; a draft survives a scan and a layout change elsewhere on the grid.
- [ ] CP12: with the Roost active and the pane on screen, a Perch permission and a terminal session's
      done/waiting raise no toast; tab away from the Roost → the pending Perch prompt then toasts once.
- [ ] P3: takeover keeps the session id and rebinds in place.
- [ ] CP16: a long terminal session's expanded pane shows "Load earlier"; clicking it loads the whole
      transcript and the strip goes; a closed pane stays hidden across a Perch restart.
- [ ] CP14: with the Roost open but not focused, a session blocking on you flashes its taskbar button; activating
      the Roost stops it.

## Open questions
- None blocking. Revisit **mini-card capacity** (3 vs 4 per cell) and the **~2s typing-hold pause** after the
  first dogfood.
