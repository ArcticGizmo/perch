# Phase 5 — Porting the remaining windows (step breakdown)

With the overlay at parity (Phase 4), Phase 5 ports every other window the WinForms app owns:
the tray-opened dashboards (**Stats**, **Flight Path**, **History**), the overlay-triggered
transient windows (**QR**, **Confetti**, **Glow**), and the last shared tooltip/popover surfaces.
Each is owner-drawn today, so — like the overlay — it becomes an Avalonia custom-drawn `Control`
hosted in a `Window`, reusing the `OverlayDraw` mini-PaintKit, `UsageBarRenderer`, and the `Palette`.

This closes the last of the overlay's Phase-5 stubs: `HistoryRequested`, `QrRequested`, the confetti
arm/consume path, and `DragCompleted → glow`.

## Ground rules for every step
- **Small & independently committable.** One window (or one shared helper + its first consumer) per
  step; the build stays green and every prior surface keeps working.
- **Verify before commit** via the headless-Skia `render` harness (synthetic data → PNG, eyeballed)
  for the drawn surfaces, and a real launch for anything animated or window-managed.
- **Fidelity:** port each `Draw*` routine faithfully; keep the measure-or-paint single-routine
  pattern (CLAUDE.md `DrawDashboard(Graphics?, width)`) and size text from `FormattedText` line
  height, never magic pixels.
- **Reuse:** drawing goes through `OverlayDraw` / `UsageBarRenderer` / `Palette`; new shared
  primitives (transcript wrapping, the mood bird, glyphs) live in one place and are pulled in as the
  first consumer needs them.
- **Single reused window instances** (CLAUDE.md): every top-level window is created lazily and
  reused. Phase 5 introduces one Avalonia `WindowHost` so "wire into all three" becomes one place.

## Dependencies / seams pulled in as needed
- **`WindowHost` (Avalonia)** — the single-reused-window idiom → built in **5.1**.
- **`IAmbientGlow`** (deferred since Phase 2, UI-toolkit-bound) — built in **5.7** (glow window).
- **`WrappedRenderer`** transcript layout — ported in **5.8** (history), the one large shared helper.
- **Data services already exist in Core:** `SessionStatsService`, `FlightPathService`,
  `SessionHistory`, `GitStatsService`, `UsageMonitor` — Phase 5 is UI over these, no new data logic.

## Out of scope (deliberately)
- **Settings UI + quick-links dialog + desktop notifications** — these are the Phase-3 remainder,
  tracked separately; the `SettingsWindow` stays a stub until then.
- **Dense mode** (`DenseModeController` / `DenseDropZoneForm`) — a WinForms-only overlay affordance;
  the global hotkey now toggles overlay visibility instead (Phase 4 decision). Not ported unless
  revisited as a product decision.
- **Update flow / update badge** — packaging-tied, belongs to the Phase-6 cutover.

---

## Steps

### 5.1 — Window management + tray entries
Build `WindowHost.ShowOrFocus<T>` (the Avalonia counterpart of the WinForms one: create-or-focus a
single reused `Window`, null the owner's field on close, run a refresh on both paths). Add the tray
menu entries **Session history…**, **Session stats…**, **Flight path…** wired through the host to
(temporary) empty windows, plus the lazy single-reused fields in `App`. Establishes the entry points
and the management seam every later step slots into. *Verify:* tray items open/focus empty windows.

### 5.2 — QR code window
Port `QrCodeForm`: a small centred card rendering a session's remote-control deep-link as a QR
(QRCoder → module bitmap drawn via `DrawingContext`) with the session name + URL. Wire the overlay's
`QrRequested` (retire that stub). Only one shown at a time. *Verify:* render + real launch from a
remote-controlled session's context menu.

### 5.3 — Confetti window
Port `ConfettiForm`: a transparent, click-through, topmost, full-screen-on-the-overlay's-monitor
window that fires a one-shot particle burst on a ~60fps `DispatcherTimer`, then self-closes. Wire the
overlay's confetti arm → `ConsumeConfetti` → launch path in `App` (the `NeedsAttention` handler).
*Verify:* real launch — arm confetti on a session, let it finish.

### 5.4 — Glow window (`IAmbientGlow`)
Build **`IAmbientGlow`** (show/hide a coloured screen-edge glow on a given screen) + its Windows
Avalonia implementation: a transparent, input-transparent, topmost window drawing an inward edge
gradient (port `GlowForm`). Wire it to attention (glow while any session needs attention / awaits
input) and to `DragCompleted` (re-home onto the overlay's current monitor). *Verify:* real launch —
trigger attention, drag the overlay across monitors.

### 5.5 — Stats window
Port `StatsForm`'s owner-drawn dashboard as a scrollable custom control over `SessionStatsService`:
stat cards, per-model/tool breakdowns, cost — keeping the single `DrawDashboard(ctx?, width)`
measure-or-paint routine so measured height and painted layout can't drift (and the CLAUDE.md
card-height-from-font rule). Pull in `BirdMood`/`Glyphs` if the header needs them. *Verify:* render
at 1×/1.5× against synthetic stats + real launch.

### 5.6 — Flight Path window
Port `FlightPathForm` over `FlightPathService` (the session-timeline / activity visualisation) as a
custom-drawn control. *Verify:* render + real launch.

### 5.7 — History viewer + `WrappedRenderer`
Port `WrappedRenderer` (transcript record → wrapped, styled text layout) as the shared helper, then
`HistoryViewerForm`: the session list, the transcript pane, live-session refresh
(`SetActiveSessions`), and the jump-to-session entry (`OpenHistoryRequested`). Wire the overlay's
`HistoryRequested` + the monitor's `OpenHistoryRequested` (retire those stubs). The largest surface —
splittable (list/chrome first, transcript rendering second) if it proves large. *Verify:* render +
real launch on a session with a transcript.

### 5.8 — Tooltips / popovers + stub cleanup
Port the remaining shared transient surfaces not already covered by the overlay's `OverlayTooltip`:
the generic `HintTooltipForm` and the `PopoverMenu` (the multi-artifact picker the overlay opens when
a session has several artifacts). Wire the overlay's multi-artifact path to the popover. Sweep for any
remaining Phase-5 `TODO(phase5)` stubs and confirm each is now wired. *Verify:* real launch — hover a
hint, open a multi-artifact session's picker.

---

## Sequencing notes
- **5.1** lays the entry points + reuse seam. **5.2→5.4** are the small, self-contained,
  overlay-triggered windows (QR, confetti, glow) — they retire the easiest overlay stubs and build
  confidence with transparent/animated windows before the big dashboards. **5.5→5.7** are the
  tray dashboards in increasing size (Stats → Flight Path → History), all reusing the same drawing
  kit. **5.8** mops up the shared tooltip/popover surfaces and confirms no Phase-5 stub is left.
- Steps 5.5–5.7 are internally splittable if any proves large (History especially: chrome/list, then
  transcript rendering).
- After Phase 5 the Avalonia app is at full window parity with WinForms on Windows — the gate for the
  Phase-6 cutover (packaging + retiring WinForms), with only the Phase-3 Settings remainder outstanding.
