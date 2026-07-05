# Phase 4 — Porting the overlay (step breakdown)

The overlay (`src/Ui/OverlayForm.cs`, 2,838 lines) is Perch's centerpiece and its most feature-dense,
owner-drawn surface. Phase 4 replaces the thin-vertical XAML overlay (`OverlayView`) with a **custom
owner-drawn Avalonia control** (`OverlayCanvas : Control`, `Render(DrawingContext)`) that matches the
WinForms look — because the overlay's glyphs, tree lines, thermometer, mini-bars, gradient pills, and
chase-border are custom GDI+ that map to `DrawingContext`, not to XAML controls.

## Ground rules for every step
- **Small & independently committable.** One coherent feature cluster per step; build stays green.
- **Verify before commit** via the headless-Skia `render` harness (synthetic sessions → PNG, eyeballed)
  and, for interaction/animation, a real launch against live `~/.claude`.
- **Fidelity:** port each `Draw*` routine faithfully; keep the measure-or-paint single-routine pattern
  (CLAUDE.md) and size text from `FormattedText` line height, never magic pixels.
- **Reuse:** the drawing primitives live in one `OverlayDraw` helper (the formalized mini-PaintKit).
- The thin vertical's `OverlayView`/row-VMs are scaffolding — retired once `OverlayCanvas` reaches
  parity (step 4.17).

## Dependencies pulled in as they're needed
- **`IAppIconProvider`** (UI-neutral icon contract, deferred from Phase 2) — built in **4.10** (quick links).
- **`IGlobalHotkey`** (deferred from Phase 2) — built in **4.16** (window behaviors).
- **Confetti / glow / QR windows** are Phase 5; overlay steps here wire only the *triggers*.
- **Settings UI** (Phase 3 remainder) sets the display toggles; **4.17** wires sensible defaults until then.

---

## Steps

### 4.1 — Drawing foundation + empty canvas
Formalize `OverlayDraw` (rounded rect, pill bar, stroked-glyph helpers, `FormattedText` text sized from
line height). Add `OverlayCanvas : Control` with the measure-or-paint skeleton, drawing just the rounded
panel background + 1px border. Swap it into `LiveOverlayWindow` in place of `OverlayView`.
*Verify:* renders the empty dark rounded panel at 1×/1.5×.

### 4.2 — Header / collapsed bar
Port `DrawHeader`: mood-bird/logo, "Perch" title, status-count pills (`DrawStatusPill` — running /
attention / awaiting / idle counts), expand/collapse chevron (`DrawSideCollapseIcon`), dense-toggle icon,
update-badge placeholder (`SideIconRect`). This is the always-visible compact bar.
*Verify:* header render matches WinForms header for several session mixes.

### 4.3 — Session rows (expanded)
Port `DrawSessionRow` core: status dot, `DisplayName`, project, activity line, running/awaiting elapsed
labels (`RunningElapsedLabel`/`AwaitingElapsedLabel`, `WarmWaitingColor`), text truncation
(`TruncateString`). Expand/collapse via header click; `RowTop`/`HeightOf`/`FullPanelHeight`/`RelayoutWindow`
height math. *Verify:* multi-session expanded render + collapse toggle.

### 4.4 — Sub-agents, teammates, Autonomous section
Port `DrawSubAgentRow`, `DrawPlainSubAgentRow`, `DrawTeammateRow`, `DrawTeammateGlyph`, tree lines, and the
collapsible **Autonomous** section-header row (`DrawSectionHeaderRow`) that groups background/SDK sessions.
Honor the hide-inactive-teammates gate. *Verify:* render with nested sub-agents + a collapsed section.

### 4.5 — Row status glyphs (identity cluster)
Port the small per-row glyphs: remote-control (`DrawRemoteIcon`), background/SDK bot (`DrawBotIcon`),
external-notify mail (`DrawMailIcon`), and permission **mode badge** (`ModeColor`). Column reservation
(`RcIconWidth`/`BotIconWidth`/…). *Verify:* render sessions carrying each glyph.

### 4.6 — Context/health glyphs
Port context-pressure **thermometer** (`DrawThermoIcon`, thresholds, green segment), **stuck-warning**
(`DrawWarnIcon`), **task-progress** count, **burn-rate** label, **git-stats** (+/- lines). Each behind its
display flag. *Verify:* render sessions with high context, a stuck signal, tasks, burn, git.

### 4.7 — Artifact glyph (clickable)
Port `DrawArtifactIcon` + `ArtifactIconRect` + hover state; wire the (later) click path stub. Display gate
`_showArtifacts`. *Verify:* render + hover render.

### 4.8 — Usage bars strip
Port `DrawUsageBars`/`DrawUsageBar`: session (5h) + weekly rate-limit bars, colour thresholds
(`UsageColor`), expected-rate mark, reset-time text. Wire `UpdateUsage` from `UsageMonitor`.
*Verify:* render with sample usage; real launch shows live usage.

### 4.9 — System-metrics strip + per-session mini-bars
Port `DrawSystemMetricsStrip`/`DrawSysBar` (CPU + RAM) and `DrawMetricsBars`/`DrawMiniBar` (per-row
CPU/RAM). Wire `UpdateSystemMetrics`/`UpdateSessionMetrics` from `MetricsMonitor` (introduce
`ISystemMetrics` here so whole-machine CPU is behind the platform seam). *Verify:* live launch shows
moving bars.

### 4.10 — Quick-links row
Build **`IAppIconProvider`** (UI-neutral: returns PNG bytes; Windows impl wraps `ShellIcon`) + Avalonia
bitmap decode. Port `DrawQuickLinksRow`, `HitTestQuickLink`, `Initials`/`FallbackColor`/`ColorFromHsv`,
upside-down option, launch/focus via `QuickLinkLauncher` equivalent. *Verify:* render with a few links
(icon + initials fallback).

### 4.11 — Hit-testing + mouse interaction
Consolidate and port `HitTestRow`/`HitTestArtifactIcon`/`HitTestThermoIcon`/`HitTestWarnIcon`/
`HitTestTaskCount`/`HitTestMetrics`/`NameMidY`, hover highlight (`_hoveredRow` etc.), hand cursors, and
click routing (row→focus, artifact→open). Port `OnMouseDown/Move/Up/Enter/Leave` to Avalonia pointer
events. *Verify:* real launch — hover states + row-click focus + artifact open.

### 4.12 — Tooltips
Port the five hover popups: usage (`ShowUsageTooltip`), context-pressure (`ShowThermoTooltip`,
`FormatTokens`), stuck-warning (`ShowWarnTooltip`), task-list (`ShowTaskTooltip`), metrics
(`ShowMetricsTooltip`, `FormatBytes`). Avalonia `ToolTip`/`Popup` with owner-drawn content where needed.
*Verify:* real launch hover.

### 4.13 — Right-click context menu
Port `ShowContextMenuAt`: focus terminal, open history (`HistoryRequested`), toggle external-notify
(`ExternalNotifyToggleRequested`), arm confetti, show QR (`ShowQrCode` — triggers the Phase-5 QR window),
exit (`ExitRequested`). Avalonia `ContextMenu`/`MenuFlyout`. *Verify:* real launch.

### 4.14 — Attention chase-border animation
Port `DrawChaseBorder` (the travelling comet over the inward glow) + `TriggerAttention` + the ~33ms tick
(`_chasePhase`/`ChaseStep`) on an Avalonia render/`DispatcherTimer`. *Verify:* real launch — trigger an
attention flash.

### 4.15 — Auto-close countdown bar
Port `DrawAutoCloseBar` + `StartAutoCloseCountdown`/`CancelAutoCloseCountdown` + `UpdateTickTimer`, wired
to the `--autostarted` lifecycle. *Verify:* launch with the countdown armed.

### 4.16 — Window behaviors
Auto-position (`FloatTopGap`, clear window-control buttons), multi-monitor follow (`DragCompleted` →
glow), no-Alt+Tab / no-activate (`CreateParams` → Avalonia window flags / Win32 where needed), position
persistence, and **`IGlobalHotkey`** (build the interface + Windows impl of `RegisterHotKey`/`WM_HOTKEY`).
*Verify:* multi-monitor drag, hotkey toggle, no taskbar/Alt-Tab entry.

### 4.17 — Display-toggle wiring + scaffolding cleanup
Honor all `SetShow*` gates (mode badges, context pressure, task progress, burn rate, git stats, waiting
timer, artifacts, expected rate, green segment, hide-inactive-teammates, context thresholds, upside-down
quick links) — defaults until the Phase-3 Settings UI drives them. Retire the thin-vertical `OverlayView`
+ row-VMs now that `OverlayCanvas` is authoritative. *Verify:* full render parity pass at 1×/1.5×.

---

## Sequencing notes
- 4.1→4.4 build the structural overlay (panel, header, rows, sub-agents) — after this it's recognizably
  Perch. 4.5→4.10 layer on the information glyphs/bars. 4.11→4.13 make it interactive. 4.14→4.16 add
  animation, lifecycle, and window polish. 4.17 closes parity and cleans up.
- Steps 4.5–4.9 are internally splittable if any proves large (e.g. 4.6 could be one commit per glyph).
- Confetti/glow/QR **windows** are intentionally left to Phase 5; only their overlay triggers land here.
