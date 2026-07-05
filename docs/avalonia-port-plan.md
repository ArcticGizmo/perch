# Perch → Avalonia Port Plan

Porting Perch from .NET 10 / WinForms to [Avalonia UI](https://avaloniaui.net/), with the
end goal of a cross-platform (Windows first, then macOS/Linux) build. This plan is written to
be executed incrementally: **the app stays shippable on Windows at the end of every phase.**

---

## 1. Goals & guiding principles

1. **Never break the Windows app.** Each phase leaves `main` in a buildable, shippable state.
   The port runs *alongside* WinForms until the Avalonia UI reaches parity, then we cut over.
2. **Extract before you port.** Most of Perch's value is in the `~/.claude` data layer, which is
   already platform-agnostic. Pull that (and everything else portable) into a UI-free core *first*,
   so the Avalonia work is purely a UI + platform-services exercise.
3. **Isolate the OS.** Every Windows-specific capability goes behind an interface with a Windows
   implementation. macOS/Linux become *new implementations of existing interfaces*, not a rewrite.
4. **Preserve the visual identity.** Perch is a heavily owner-drawn app with a distinct look. The
   owner-drawn surfaces port to Avalonia custom-drawn controls (near 1:1 with the existing
   measure-or-paint routines); only the chrome-heavy control forms move to XAML.
5. **Keep the tests green.** `tests/Perch.Tests` exercises the data layer via `CLAUDE_CONFIG_DIR`.
   It is our regression net through the whole port and must keep passing at every step.

---

## 2. Current-state assessment

### 2.1 What's already portable (little/no change)

| Area | Files | Notes |
|---|---|---|
| Data layer | almost all of `src/Data/` (~5k LOC) | Pure file I/O + JSON parsing over `~/.claude`. `FileSystemWatcher`, `System.Text.Json`, `HttpClient` are all cross-platform. |
| App settings & models | `AppSettings`, `ClaudeSession`, `SessionStats*`, `Transcript*`, `SubAgentReader`, `TeamReader`, etc. | Logic only. Some carry hard-coded `\` paths / `.exe` assumptions to fix (§2.3). |
| Dependencies | Markdig, QRCoder, Velopack | All cross-platform. Velopack already targets Windows **and** macOS/Linux. |

### 2.2 Windows-locked surfaces (must be abstracted)

| Capability | Where today | Port strategy |
|---|---|---|
| **Tray icon + menu** | `NotifyIcon` / `ContextMenuStrip` in `OverlayApplicationContext` | Avalonia built-in `TrayIcon` + `NativeMenu` (cross-platform). |
| **Toast/balloon notifications** | `NotifyIcon.ShowBalloonTip` in `NotificationService` | No Avalonia built-in → `DesktopNotifications` lib (Win toast + Linux libnotify) or per-platform behind `INotifier`. |
| **System chimes** | `System.Media.SystemSounds` | `IAudioCue` — Win `SystemSounds`, macOS `afplay`, Linux `canberra`. |
| **Layered per-pixel-alpha overlay (the "glow")** | `GlowForm` + `UpdateLayeredWindow` in `NativeMethods` | Transparent topmost Avalonia window; input-transparent. Glow's click-through may still need Win32 initially → behind `IAmbientGlow`. |
| **Always-on-top click-passthrough overlay** | `OverlayForm` (layered, `WS_EX_*`) | Avalonia `Window`: `SystemDecorations=None`, `Topmost`, `ShowInTaskbar=false`, transparent, custom hit-testing. |
| **Global hotkey** | `RegisterHotKey` / `WM_HOTKEY` in `NativeMethods` | No cross-platform API → `IGlobalHotkey`, Win32 impl now, `SharpHook`/per-platform later. |
| **Focus a session's terminal (process-tree walk)** | `FocusTerminalForProcess` in `NativeMethods` | Inherently OS-specific → `IWindowActivator`. Windows keeps current logic; mac/Linux = later, best-effort. |
| **Shell icon loading** | `ShellIcon` (COM `IShellItemImageFactory`) | `IAppIconProvider`. Windows keeps COM; mac/Linux stub or platform equivalent. |
| **Dark title bar / dark scrollbars** | `DwmSetWindowAttribute`, uxtheme in `NativeMethods` | **Deleted** — Avalonia themes the whole window; these hacks disappear. |
| **PATH registration** | `PathRegistration` (per-user PATH + `WM_SETTINGCHANGE`) | `IPathInstaller`. Windows keeps env-var logic; mac/Linux = shell profile / symlink. |
| **System CPU/RAM sampling** | `MetricsMonitor` (`GetSystemTimes`) | `ISystemMetrics`. The one **Data-layer** file that's OS-locked. |
| **Single-instance + STA + first-run** | `Program` (named `Mutex`, `[STAThread]`, Velopack hooks) | Avalonia has its own app lifetime; single-instance → cross-platform mutex/socket. STA becomes irrelevant off-Windows. |
| **Borderless window drag** | `ReleaseCapture`/`WM_NCLBUTTONDOWN` | Avalonia `BeginMoveDrag()` — built in, cross-platform. |

### 2.3 Path/shell assumptions to sweep

`src/Data/` files flagged for hard-coded `\`, `.exe`, or Windows env-var use:
`ClaudePaths`, `AppSettings`, `FlightPathService`, `ModelContext`, `PluginManager`,
`SessionStatsService`, `ToolSummary`, `SessionHistory`, `QuickLink`.
Most are benign (`~/.claude` resolves fine via `Environment.SpecialFolder.UserProfile`), but audit
each for `Path.Combine` vs literal separators and `.exe`/`cmd.exe` launcher assumptions.

### 2.4 Rendering inventory (drives the biggest decision)

Owner-drawn GDI+ surfaces to translate to Avalonia `DrawingContext`:
`OverlayForm` (2.8k LOC), `StatsForm` (dashboard), `HistoryViewerForm`, `WrappedRenderer`,
`FlightPathForm`, `UsageBarRenderer`, `ConfettiForm`, `GlowForm`, `BirdMood`, `Glyphs`,
`PaintKit`, `GradientButton`, `PopoverMenu`, `HintTooltipForm`, `UsageTooltipForm`.

Control-based forms better suited to XAML: `SettingsForm`, `QuickLinkDialog`,
`SettingsControls`/`ThemedControls` (become Avalonia `Styles` + the `Theme` palette as brushes).

---

## 3. Target architecture

Split the single `Perch.csproj` into layered projects:

```
Perch.Core            (netX.0, no UI)   ── data layer, models, view-models, settings, services
  └─ interfaces:  INotifier, IAudioCue, ITrayHost, IGlobalHotkey, IWindowActivator,
                  IAppIconProvider, IPathInstaller, ISystemMetrics, IAmbientGlow, ISingleInstance
Perch.Platform.Windows (netX.0-windows) ── Win32 impls (wraps today's NativeMethods/ShellIcon/etc.)
Perch.Platform.Mac     (later)
Perch.Platform.Linux   (later)
Perch.App.Avalonia     (netX.0)         ── Avalonia UI: windows, custom-drawn controls, tray wiring
Perch.App.WinForms     (existing)       ── kept until cutover (Phase 6), then deleted
tests/Perch.Tests                       ── unchanged; now references Perch.Core
```

`net10.0-windows` stays only for the Windows platform project and the (temporary) WinForms app.
`Perch.Core` and `Perch.App.Avalonia` target plain `net10.0` so they can build for any RID.

---

## 4. Key decisions (with recommendations)

| Decision | Recommendation | Why |
|---|---|---|
| **Owner-draw vs XAML** | **Keep owner-drawing** for the overlay + dashboards as Avalonia custom controls; **XAML** for Settings/dialogs. | GDI+ `Graphics` → Avalonia `DrawingContext` is near 1:1 (rounded rects, brushes, `FormattedText`). Preserves the exact look and the DPI/line-height discipline in `CLAUDE.md`. Rewriting the overlay in XAML would be a far bigger, riskier job. |
| **Tray** | Avalonia `TrayIcon` + `NativeMenu`. | Built in, works on all three OSes. |
| **Notifications** | `DesktopNotifications.Avalonia` behind `INotifier`. | No Avalonia built-in; this covers Win toast + Linux; add a macOS path later. |
| **Glow / click-through** | Abstract as `IAmbientGlow`; Windows keeps `UpdateLayeredWindow` initially. | Avalonia's input-passthrough for per-pixel-alpha topmost windows is limited; don't block the port on it. |
| **Project split** | Multi-project (§3), not multi-target of one project. | Keeps `net10.0-windows` contamination out of Core; lets WinForms + Avalonia coexist during migration. |
| **Packaging** | Stay on **Velopack** (adds macOS/Linux targets). | Already in use; avoids a second migration. |
| **Reactive stack** | Plain view-models + `INotifyPropertyChanged` (or CommunityToolkit.Mvvm). Avoid heavy ReactiveUI unless wanted. | Perch's state is push-from-file-watchers; keep it simple. |

---

## 5. Phased plan

Each phase lists **goal**, **work**, **exit criteria**, **risk**. Phases 0–2 ship no visible
change; 3–6 build and cut over the Avalonia Windows app; 7 is the actual cross-platform payoff.

### Phase 0 — De-risk spike (throwaway)
**Goal:** Prove the three scariest Avalonia unknowns on Windows before committing.
**Work:**
- Spike an Avalonia `Window` that is borderless, transparent, `Topmost`, no taskbar entry, and
  drag-to-move — the overlay's shell. Confirm hit-testing + `BeginMoveDrag`.
- Spike `TrayIcon` + `NativeMenu` with left-click and right-click menu.
- Spike one custom-drawn control that renders a **stat card** via `DrawingContext` and matches the
  GDI+ version pixel-for-pixel at 100% and 150% DPI (validates the `CLAUDE.md` font-height rule
  survives the port).
**Exit:** All three demonstrated in a scratch repo/branch. Document gaps (esp. click-through glow).
**Risk:** Low cost, highest information. Do not skip.

### Phase 1 — Extract `Perch.Core` (no Avalonia yet)
**Goal:** A UI-free core the WinForms app runs against unchanged.
**Work:**
- Create `Perch.Core`; move all of `src/Data/` except the OS-locked bits.
- Define the platform interfaces (§3). Move `MetricsMonitor`'s `GetSystemTimes` behind
  `ISystemMetrics`; sweep §2.3 path/exe assumptions.
- Introduce view-models for each surface (overlay rows, stats dashboard, history, settings) that
  expose already-computed display state — this is the seam both UIs bind to.
- Repoint `tests/Perch.Tests` at `Perch.Core`.
**Exit:** WinForms app builds against Core; **all tests green**; no behaviour change.
**Risk:** Low — pure refactor, guarded by tests.

### Phase 2 — `Perch.Platform.Windows`
**Goal:** Every Windows capability implemented behind a Core interface.
**Work:** Wrap `NativeMethods`, `ShellIcon`, `PathRegistration`, `NotificationService` chimes,
single-instance mutex into `IWindowActivator`, `IAppIconProvider`, `IPathInstaller`, `IAudioCue`,
`IGlobalHotkey`, `ISingleInstance`, `IAmbientGlow`, `ISystemMetrics`. WinForms app resolves these
via a tiny composition root instead of calling statics directly.
**Exit:** WinForms app runs entirely through the interfaces; tests green.
**Risk:** Low–medium — mechanical, but touches the interop that makes Perch feel native.

### Phase 3 — Avalonia shell + Settings window (Windows)
**Goal:** A running Avalonia Perch that boots, shows the tray, and opens **Settings** — the
easiest window — as a second executable alongside WinForms.
**Work:**
- Create `Perch.App.Avalonia`: `App.axaml`, classic-desktop lifetime, `ISingleInstance`, Velopack
  bootstrap, `TrayIcon` wired to the same commands `OverlayApplicationContext` exposes.
- Port `Theme` palette to Avalonia `Styles`/resources; port `SettingsForm` + `QuickLinkDialog` to
  XAML bound to the Phase-1 settings view-model.
- Port `INotifier` (toasts) and reuse `IAudioCue`.
**Exit:** `Perch.App.Avalonia.exe` launches, tray works, Settings reads/writes real settings and
notifications fire. WinForms remains the shipped app.
**Risk:** Medium — first real Avalonia surface; establishes all the patterns.

### Phase 4 — Port the overlay (the hard one)
> Broken into 17 small, independently-committable steps in **[avalonia-phase4-overlay.md](avalonia-phase4-overlay.md)**.

**Goal:** The floating status widget, in Avalonia, at parity.
**Work:**
- Overlay `Window` from the Phase-0 shell. Build a `DrawingContext` mini-`PaintKit`
  (rounded rects, pill bars, glyphs, `FormattedText` sizing off line height per `CLAUDE.md`).
- Port `OverlayForm`'s measure-or-paint render list, row/sub-agent/section layout, hover/hit
  regions, expand-collapse, dense mode, attention "chase" border animation, drag, right-click menu,
  clickable artifact glyph, update badge.
- Wire `IWindowActivator` (row click → focus terminal), `IAmbientGlow`, `ConfettiForm` equivalent.
**Exit:** Avalonia overlay matches WinForms overlay feature-for-feature on Windows.
**Risk:** **High** — largest, most detailed surface. Budget the most time here; lean on Phase 0.

### Phase 5 — Port remaining windows
> Broken into small, independently-committable steps in **[avalonia-phase5-windows.md](avalonia-phase5-windows.md)**.

**Goal:** History, Stats, Flight Path, tooltips, popovers, confetti, glow — all in Avalonia.
**Work:** Port each owner-drawn surface as a custom-drawn control (Stats/History/FlightPath reuse
the mini-PaintKit). Port `WindowHost` "single reused window" idiom to Avalonia window management
(the `CLAUDE.md` "wire into all three" rule → now one place).
**Exit:** Full feature parity with the WinForms app on Windows.
**Risk:** Medium — repetitive application of Phase-4 patterns.

### Phase 6 — Cutover & retire WinForms
**Goal:** Avalonia becomes *the* Windows Perch.
**Work:** Switch Velopack/`publish.bat` to package `Perch.App.Avalonia`. Verify first-run plugin
install, PATH registration, auto-start (`--autostarted`), auto-close, update flow, single-instance.
Delete `Perch.App.WinForms` and dead Win32 (dark-titlebar/scrollbar hacks). Update `CLAUDE.md`,
`README.md`, `tools/`.
**Exit:** Shipped Windows release is Avalonia; WinForms deleted; docs updated.
**Risk:** Medium — packaging/update-flow regressions; test the installed artifact, not just `dotnet run`.

### Phase 7 — Cross-platform enablement (the payoff)
**Goal:** macOS and/or Linux builds.
**Work:** Implement `Perch.Platform.Mac` / `.Linux` for each interface (notifications, chimes,
window activation best-effort, icons, path/autostart, system metrics, hotkey, glow). Verify
`~/.claude` path resolution and the plugin's shell hooks on POSIX. Velopack macOS/Linux packaging.
Decide per-OS graceful degradation for capabilities that can't port (e.g. terminal focusing).
**Exit:** At least one non-Windows platform builds, runs, and monitors sessions.
**Risk:** Medium–high — real OS testing; some Win-only affordances degrade rather than port.

---

## 6. Cross-cutting concerns

- **Testing:** `Perch.Tests` stays the data-layer net through every phase. Add view-model tests in
  Phase 1 (they're now pure logic). UI stays eyeball-only (as today), so keep logic out of controls.
- **DPI:** The `CLAUDE.md` rule — size text rectangles from font line height, never magic pixels —
  must be re-enforced in the `DrawingContext` port. Bake it into the mini-PaintKit helpers.
- **Threading:** WinForms `BeginInvoke` + `IsHandleCreated` guards become Avalonia
  `Dispatcher.UIThread.Post` + disposed/closed guards. Port the `Task.Run(...).ContinueWith(...)`
  off-UI pattern verbatim in shape.
- **Plugin (`plugins/perch/`):** hooks are PowerShell today; cross-platform later needs POSIX
  equivalents. Out of scope until Phase 7, but note it — sessions won't populate on mac/Linux
  without portable hooks.

## 7. Suggested sequencing note

Phases 0–2 are low-risk and independently valuable (cleaner architecture even if the port paused
there). The real commitment starts at Phase 3. Recommend a **go/no-go checkpoint after Phase 0**:
if the click-through/transparent-overlay spike reveals a blocker, we learn it for a day's cost, not
a month's.

---

## 8. Progress log

- **Phase 0 — DONE.** Avalonia 12 spike under `spike/AvaloniaSpike` proved all three unknowns on
  Windows: transparent/borderless/topmost/no-taskbar drag window, `TrayIcon`+`NativeMenu`, and a
  custom `DrawingContext` `StatCard`. Headless-Skia PNG renders at 1× and 1.5× confirmed
  owner-drawing ports cleanly and line-height sizing stays crisp (no glyph clipping) across DPI.
  **Verdict: GO.** (Spike is throwaway; delete when `Perch.App.Avalonia` is created in Phase 3.)
- **Phase 1 — DONE.** `Perch.Core` (net10.0, UI-free) now holds the whole data layer; the WinForms
  app and tests consume it as a library. Tests no longer depend on the WinForms app. `LockMonitor`
  stayed in the app (Windows-only API) pending its interface. Build clean, 144 tests green.
- **Phase 2 — DONE (reusable set).** `Perch.Platform.Windows` (net10.0-windows, no WinForms) with
  `IWindowActivator`, `IPathInstaller`, `IAudioCue`, `ISessionLock` — all resolved through a
  `PlatformServices` composition root; no UI code touches Win32 directly anymore. Deferred to the
  phase that grounds them: `IAppIconProvider` (needs a UI-neutral image contract → Phase 3),
  `IGlobalHotkey` / `IAmbientGlow` / tray / toasts (UI-toolkit-bound → Avalonia phases),
  `ISystemMetrics` (only matters off-Windows → Phase 7). Build clean, 144 tests green.
- **Phase 3 — in progress.**
  - *Shell scaffold — DONE.* `Perch.App.Avalonia` boots with a dark Fluent theme, system tray + menu,
    the single-reused-window idiom, and the `Theming/Palette` port of the WinForms `Theme`. Runs as a
    second exe beside WinForms (distinct single-instance mutex); Velopack deferred to Phase 6.
  - *Thin live vertical — DONE.* The overlay shows **live** sessions end-to-end: `SessionMonitorHost`
    drives `SessionMonitor` (UI-thread-marshalled scans) into an `OverlayViewModel`; `OverlayView`
    (reusable body) renders status-coloured rows in a transparent/topmost/borderless window; row-click
    focuses the terminal via `IWindowActivator`. A headless-Skia `render` mode (`HeadlessRenderer`)
    dumps views to PNG for verification. Verified against real `~/.claude` data + synthetic render.
  - *Settings UI + quick-links dialog — DONE.* The full `SettingsWindow` is ported (a nav rail over
    scrollable pages: Getting started, Plugin Control, Usage, Indicators, Monitoring, Session Stats,
    Notifications, Quick Links, Experimental, About, Changelog). It edits the shared `AppSettings` and
    applies live to the overlay/monitors via a compact `SettingsHooks` callback set (the Avalonia
    counterpart of `SettingsForm`'s ~30 events). New Avalonia custom controls port the WinForms ones —
    the pill `PerchToggle`, `SpinnerView`, owner-drawn `UsageBarsView`, `ModeLegendView`, and the
    draggable `ContextThresholdSliderView` — plus the `QuickLinkDialog` (Browse via `StorageProvider`,
    name-resolution via the `IAppIconProvider` seam). Best-effort backends where the head has none yet:
    notification "Test" plays the real chime via `IAudioCue`, "Send test" does a real ntfy POST, About
    shows version/links (the updater is Phase 6). Verified via headless renders + a full-window frame
    capture across pages. `IAppIconProvider` was already grounded in Phase 5.
  - *Desktop notification delivery — DONE.* A finishing/blocking session now fires a desktop toast +
    chime + external ntfy push, each gated by its setting. New `INotifier` seam + a toolkit-neutral
    `NotificationService` in Core (ported from the WinForms one; `NtfyNotifier` moved to Core and shared),
    with an owner-drawn `AvaloniaToastNotifier`/`ToastHostWindow` — a transparent, non-activating toast
    stack at the overlay screen's corner, click-to-focus+acknowledge. No packaging/AUMID; cross-platform
    by construction (a native-toast `INotifier` can slot in per-OS later).
  - **Phase 3 is now complete** — the Avalonia head is at Windows parity; next is the Phase-6 cutover
    (packaging + the deferred update flow), then Phase 7 cross-platform.
- **Phase 4 — DONE.** The floating overlay is ported to a single owner-drawn `OverlayCanvas` at full
  parity with `OverlayForm` across 17 steps (see [avalonia-phase4-overlay.md](avalonia-phase4-overlay.md)):
  panel/header/rows/sub-agents, every info glyph + bar strip, hit-testing + hover + dwell tooltips,
  the right-click menu, the attention chase-border, the auto-close countdown, window drag/behaviors,
  and the settings-driven display gates. New seam: `IGlobalHotkey` (Alt+Shift+W → toggle overlay).
  The thin-vertical scaffolding is retired. Build clean, 144 tests green; verified 1×/1.5× renders +
  live launch. Overlay stubs left for Phase 5: history/QR/confetti/glow windows + external-notify.
- **Phase 5 — DONE.** Every remaining WinForms window is ported to Avalonia across 8 steps (see
  [avalonia-phase5-windows.md](avalonia-phase5-windows.md)): an Avalonia `WindowHost` + tray entries;
  the QR card; the confetti + ambient-glow (`IAmbientGlow`) transient windows; the Stats, Flight-Path,
  and History (chrome/list + rich Markdown/tool-expand/live-tail) surfaces — all owner-drawn via the
  overlay's `OverlayDraw`/`Palette` kit, over Core's stats/flight/history services loaded off the UI
  thread; and the multi-artifact picker + external-notify wiring. This retired every overlay Phase-5
  stub (history/QR/confetti/glow/external-notify). The remaining transient surfaces (hint tooltips,
  popovers) are covered by `OverlayTooltip` + `MenuFlyout`. Build clean, 144 tests green; verified via
  headless renders + live launch. **The Phase-3 remainder (Settings UI, quick-links dialog, and desktop
  notification delivery) is now complete — see the Phase 3 progress entry — so the Avalonia head is at
  Windows parity and the Phase-6 cutover is the next milestone.**
