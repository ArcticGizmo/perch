# Reserving a dedicated Perch column (edge reservation)

**Status:** Feasibility proven by spike (`spikes/reserve-edge/`), 2026-08-20. Not yet
integrated. This doc records what's possible, the caveats, and how it would fold into
Perch's platform-seam + overlay-mode architecture.

## The problem

Perch's floating overlay is a topmost tool window parked over the top-right corner. As
features accrue it grows taller and permanently occludes that corner of maximized apps,
so the user constantly toggles it on/off. They want it **persistent but non-overlapping**:
a dedicated column the rest of the desktop won't grow into.

## The mechanism: Windows AppBar (`SHAppBarMessage`)

Windows has a first-class, documented API for exactly this — the *application desktop
toolbar* (AppBar). It's what the taskbar itself is, and what sidebar apps (old Windows
Sidebar, RocketDock's reserve mode, DisplayFusion) use. Registering an appbar on an edge
and committing a position **shrinks the desktop work area**; the shell then makes every
**maximized** window respect the new boundary. No polling, no per-window hooking — the OS
does the containment.

Proven in the spike: reserving a 320px right column took the primary work area from
1920→1600 wide and restored it on release. See `spikes/reserve-edge/README.md` for the
exact sequence and output.

### Load-bearing call sequence

`ABM_NEW` → fill `rc` → `ABM_QUERYPOS` → re-pin thickness → `ABM_SETPOS` (commits the
reservation) → move window into `rc` → `ABM_REMOVE` on teardown.

## Caveats (be honest about these up front)

1. **Fullscreen apps bypass it — by design, and that's fine.** The reservation governs
   *maximized* windows, not true/borderless fullscreen. A fullscreen game or video will
   still cover the column. Windows even sends `ABN_FULLSCREENAPP` so an appbar can get out
   of the way (the taskbar does this). For Perch that's the desired behaviour — you don't
   want the overlay over a fullscreen film — so we'd honour the notification and let the
   column yield, then reclaim when the fullscreen app leaves.
2. **One monitor per reservation.** An appbar reserves an edge on the single monitor its
   `rc` lands on. Perch would reserve on the user's chosen monitor (reuse the existing
   placement/monitor-picking machinery — `PlacementMath.PickMostOverlapping`, the dense
   monitor-by-bounds resolution).
3. **Physical pixels + DPI.** `rc` is physical px. Convert Perch's DIP width via the target
   monitor's scale (`Screen.Scaling`). Perch's Avalonia head is per-monitor DPI aware, so
   this is a straight multiply — but it must use the *target* monitor's scale, not the
   primary's.
4. **Must release, or it leaks.** A committed reservation that's never `ABM_REMOVE`d keeps
   the work area shrunk. Safety net: Windows auto-releases when the HWND is destroyed, so a
   crash self-heals on process exit — but normal enable/disable/exit/mode-switch/display-
   change paths must all call `ABM_REMOVE` explicitly.
5. **Notification wndproc hook.** A well-behaved appbar handles its callback message:
   `ABN_POSCHANGED` (another appbar moved → re-`QUERYPOS`/`SETPOS`), `ABN_FULLSCREENAPP`,
   `ABN_STATECHANGE`. Needs a Win32 message hook on the reservation window (Avalonia:
   `Win32Properties.AddWndProcHookCallback`, or hook the platform HWND).
6. **macOS has no direct equivalent.** There's no clean public AppBar analog; reserving
   `NSScreen.visibleFrame` needs private/awkward APIs. This is a **Windows-first** feature;
   the Mac head gets a no-op stub, consistent with `docs/macos-port-plan.md`.

## How it folds into Perch

Follows the project's platform-seam + registry-driven-settings conventions.

### 1. Core interface (platform seam)

`src/Perch.Core/Platform/IEdgeReservation.cs`:

```csharp
public enum ReservedEdge { Left, Right, Top, Bottom }

public interface IEdgeReservation
{
    // Reserve `thicknessPx` on `edge` of the monitor `monitorBounds`, tied to `handle`.
    void Reserve(IntPtr handle, ReservedEdge edge, int thicknessPx, PixelRect monitorBounds);
    void Release();
    // Raised on ABN_POSCHANGED / ABN_FULLSCREENAPP so the overlay can re-assert or yield.
    event Action? PositionChanged;
    event Action<bool>? FullscreenAppChanged;
}
```

### 2. Windows implementation

`src/Perch.Platform.Windows/EdgeReservation.cs` — the hardened spike code:
`SHAppBarMessage` + a registered callback message + wndproc hook for the `ABN_*`
notifications. Resolved in `PlatformServices` under `#if WINDOWS`; Mac gets a no-op.

### 3. A new overlay presentation mode

Perch already has **Floating** and **Dense** (a strip that *floats over* an edge). This is
naturally a third mode — **Docked column** — that *reserves* the edge instead of floating
over it. It's the logical upgrade of dense mode: same edge-hugging placement, but the
desktop yields the space. Two shapes to decide between (see the open question):

- **Reservation-only strip + existing panel floats inside it** — least invasive; the
  current overlay renders at the top of a reserved (possibly near-transparent) column. The
  column below the panel is empty reserved space.
- **Full-height docked panel** — the overlay redraws as a full-height column (more design
  work; owner-drawn layout changes in `OverlayCanvas`), but visually it's a true sidebar.

Either way, register on enter, `Release()` on exit/mode-change, and re-assert on
`Screens.Changed` (Perch already listens: `OverlayCanvas.OnScreensChanged` /
`DenseController.OnScreensChanged`).

### 4. Settings (registry-driven)

Add an `AppSettings` property (e.g. `ReserveEdgeSpace` + a width) **and** a
`SettingDescriptor` in `SettingsRegistry` (a coverage test enforces this). Reuse the
existing placement editor / monitor-picking for *which* edge and monitor.

## As-built: the Docked mode (shipped as code)

Implemented per the user's decisions (2026-08-20). Both heads build; all 720 .NET tests pass
(incl. the settings-registry coverage gate); the docked column + collapsed strip verified via
`render` mode. The live in-app reservation is a faithful port of the proven spike — flip
**Settings → Advanced → Overlay mode → Docked** to try it.

Key files: `IEdgeReservation` (Core) + `EdgeReservation` (Windows AppBar / Mac no-op) via
`PlatformServices`; `AppSettings.OverlayMode`/`DockedPlacement`/`HotkeyToggleDocked`; the
`overlay-mode` registry descriptor + segmented editor; `OverlayCanvas.Docked.cs` (geometry,
reservation, collapsed-strip paint) + docked hooks in `OverlayCanvas.Draw`; the placement editor's
third "Docked" segment; and the App wiring (startup enter, Ctrl+Shift+W, live mode switch, release
on exit/switch, re-assert on screen change).

Decisions (2026-08-20):

- **`OverlayMode { Floating, Docked }`** — a new persisted setting (mirrors `DenseStatusChangeStyle`).
  Modes are binary; "dense" becomes a *state within* each mode, not a third mode.
  - **Floating**: unchanged, including today's Alt+Shift+W hover dense-strip.
  - **Docked**: reserves a full-height edge column via `IEdgeReservation` (AppBar). **Ctrl+Shift+W**
    (`HotkeyToggleDocked`) toggles **collapsed** (narrow reserved strip, ~50px) ↔ **expanded**
    (fixed 280px panel). Side (left/right) is set in the initial-placement editor; width is fixed for now.
- **Collapsed strip (MVP):** shows per-status **counts like the floating dense strip** — reuses that art.
- Release the reservation on app exit and on switching back to Floating; re-assert on `Screens.Changed`.

### Deferred (address later)
- **Richer docked-collapsed rendering** (the long-term vision): a much narrower exposed edge showing a
  *dense version of each element* rather than just counts — session limits as **percentages only**,
  **Hypertree hidden**, each session row summarised as **indicator counts**, **friends/social hidden**,
  and an **expand-on-hover quick-peek** that previews the full panel *without* resizing/re-reserving the
  window. Worth a dedicated mockup artefact before building. Tracked in memory `reserve-edge-feature`.
- **User-adjustable docked width** (the placement editor's width knob) — user flagged as last.
- **`ABN_*` wndproc hook** (`ABN_POSCHANGED` re-assert, `ABN_FULLSCREENAPP` yield). MVP re-asserts on
  `Screens.Changed` only; a stray taskbar move won't auto-adjust until the next screen change. Appbars
  don't force themselves over fullscreen apps, so deferring the fullscreen-yield is cosmetically safe.

## Recommended next step

A second, in-app spike: add `IEdgeReservation` + the Windows impl and wire it to a hidden
debug toggle that reserves the right column and parks the *current* floating overlay at its
top — the "reservation-only" shape. That validates the DPI conversion, the notification
hook, and clean teardown inside the real app before committing to any owner-drawn redesign.
