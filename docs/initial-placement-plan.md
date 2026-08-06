# Initial Placement Editor — Implementation Plan

## Goal

Let users define the **initial on-screen placement** for both overlay presentations —
the **floating "expanded panel"** and **dense mode** — and have those preferences persist
across restarts.

Interaction (from the feature request):

- Right-click the overlay header → **"Set initial placements…"**.
- Opens a full-screen **placement editor overlay** where you drag a real-size *test window*
  around to define where the overlay should first appear.
- Placement is expressed **relative to the nearest monitor corner** — e.g. dragged near the
  top-right, the editor shows "Top 32 · Right 16" and that is how the placement is stored
  (anchor + offset), so it survives resolution changes and reads naturally.
- A button to **swap between overlay (floating) and dense mode**, so each is placed individually.
- A button to **reset to defaults**.

## Current state (why these choices)

- **Floating placement** — `OverlayCanvas.DefaultFloatingPosition` / `PlaceAtDefaultFloating`
  (`src/Perch.App/Views/OverlayCanvas.cs:283-301`). Hardcoded to the **primary** monitor's
  **top-right** work-area corner (`FloatTopGap=32`, `FloatRightMargin=16` DIP). Self-heal /
  clamp in `EnsureFloatingOnScreen` (`:308-343`).
- **Dense placement** — `DenseController` (`src/Perch.App/Views/DenseMode.cs`): `_denseSide`
  (Left/Right), `_denseY` (physical), `_denseScreenBounds` (monitor by physical bounds, self-heals
  to primary). Edge-docked: X is locked to the docked edge, only vertical is free.
- **Nothing is persisted.** All placement is in-memory; every launch recomputes defaults.
- **Header right-click menu already exists** — `OverlayCanvas.ShowContextMenuAt` header branch
  (`:3139-3148`), built via the `MenuItem(...)` factory (`:3154`) + `ShowFlyout(...)` (`:3166`).
- **Settings** — `AppSettings` (`src/Perch.Core/Data/AppSettings.cs`) → `%APPDATA%\Perch\settings.json`;
  plain auto-properties; deep-`Clone()` via JSON round-trip; coverage test `SettingsRegistryTests`
  with a `NotSettings` allowlist for non-UI state.
- **Coordinate convention:** `Position`/`Bounds`/`WorkingArea` are **physical px**; sizes are **DIP**;
  convert with `Screen.Scaling`. (Documented in `DenseMode.cs:18-25`.)

## Data model

Pure, UI-free, in `Perch.Core` (no Avalonia types — the macOS head reuses it):

```csharp
namespace Perch.Data;

public enum HAnchor { Left, Right }
public enum VAnchor { Top, Bottom }

// Serialized in settings.json. Monitor stored as physical bounds ints (mirrors the existing
// _denseScreenBounds identity), null => primary / auto. Offsets are DIP from the anchored edges.
public sealed class OverlayPlacement
{
    public int? MonitorX { get; set; }   // physical bounds of the target monitor (null = auto/primary)
    public int? MonitorY { get; set; }
    public int? MonitorW { get; set; }
    public int? MonitorH { get; set; }
    public HAnchor HAnchor { get; set; } = HAnchor.Right;
    public VAnchor VAnchor { get; set; } = VAnchor.Top;
    public double OffsetX { get; set; }  // DIP from the anchored horizontal edge (dense: ignored/0)
    public double OffsetY { get; set; }  // DIP from the anchored vertical edge
}
```

`AppSettings` gains two nullable properties (null = "use computed default"):

```csharp
[JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
public OverlayPlacement? FloatingPlacement { get; set; }
[JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
public OverlayPlacement? DensePlacement { get; set; }
```

Both added to `SettingsRegistryTests.NotSettings` (not toggle cards). *Optional:* one `Info(...)`
descriptor ("overlay-placement") so it's discoverable in Settings → Search and deep-links to the editor.

## Pure placement math (testable, in Core)

Extract the corner/offset math into a static `PlacementMath` in `Perch.Core`, working entirely in
ints/doubles (work-area rect + scale + physical size), no Avalonia:

- `ToPosition(placement, waX,waY,waW,waH, scale, physW, physH) -> (posX, posY)` — anchor+offset → physical top-left.
- `FromPosition(posX,posY, waX,waY,waW,waH, scale, physW, physH) -> OverlayPlacement` — chooses the
  **nearest** H/V anchors and computes the DIP offsets from those edges (the inverse; what the editor stores).
- `Clamp(...)` — keep the window fully inside the work area.

The App layer adapts Avalonia `Screen` → these ints and back; `OverlayCanvas`/`DenseController`
call the shared helper instead of their private math.

---

## Phase 0 — Data model + persistence + math (Core only, fully testable)

**Deliverables**
- `OverlayPlacement`, `HAnchor`, `VAnchor`, `PlacementMath` in `Perch.Core/Data`.
- `AppSettings.FloatingPlacement` / `DensePlacement` (+ `NotSettings` entries, verify Clone round-trips).
- `tests/Perch.Tests/PlacementMathTests.cs`: round-trip (`ToPosition∘FromPosition` ≈ identity),
  nearest-corner selection across all four quadrants, DPI scaling (scale 1.0 / 1.5 / 2.0), clamping,
  and an `AppSettings` save/load/Clone round-trip that includes a placement.

**Done when** `dotnet test` is green and the registry coverage test still passes. No behaviour change yet.

## Phase 1 — Consume persisted placement at startup

**Deliverables**
- `PlaceAtDefaultFloating` uses `settings.FloatingPlacement` when present (resolve monitor by bounds,
  self-heal to primary if gone, clamp on-screen), else today's computed default.
- `DenseController` seeds `_denseSide` / `_denseY` / `_denseScreenBounds` from `settings.DensePlacement`
  when present, else today's default.
- Both `OverlayCanvas` and `DenseController` route their geometry through `PlacementMath`.
- **Manual header drags do NOT persist** (decided): the editor is the single source of truth for
  initial placement; a normal drag reverts to the saved placement on next launch.

**Done when** a placement written by hand into `settings.json` positions both modes correctly on next
launch, verified by running the tray app.

## Phase 2 — Header menu entry + editor window skeleton

**Deliverables**
- New `MenuItem("Set initial placements…", …)` in the header branch of `ShowContextMenuAt`.
- New `OverlayCanvas.SetPlacementsRequested` event → wired in `App.axaml.cs` to open the editor via
  `WindowHost.ShowOrFocus` (nullable field + `CloseAuxWindows` entry, per the reuse idiom).
- `PlacementEditorWindow` skeleton: full-screen, topmost, transparent, no-activate dim overlay on the
  active monitor (modelled on `ConfettiWindow`'s per-screen sizing — but **not** click-through, it needs
  pointer input). Hosts a real-size draggable *test window* (mock of the overlay/dense strip), a Done and
  a Cancel button. Closing applies nothing yet.

**Done when** right-click → menu item opens the dimmed editor with a draggable mock, and Done/Cancel/Esc close it cleanly.

## Phase 3 — Editor interaction + HUD

**Deliverables**
- Drag the test window (manual drag, constrained to the work area). Live HUD readout of the **nearest-corner
  offsets** ("Top 32 · Right 16") plus guide lines to the two nearest edges; snapping to the default margins.
- **Mode toggle** button (Overlay ↔ Dense): swaps the mock's size/shape and which `OverlayPlacement` is being
  edited. Dense constrains to edge-docking (X locked to Left/Right, vertical free), mirroring `DenseController`.
- **Reset to defaults** button: clears the current mode's placement → mock jumps to the computed default.
- **Done** writes `FloatingPlacement` / `DensePlacement` to `AppSettings`, `Save()`s, and applies live to the
  running overlay (both modes); **Cancel/Esc** discards.

**Done when** both modes can be placed, the HUD matches where the overlay actually lands, reset works, and
saved placement is applied live and survives restart.

## Phase 4 — Multi-monitor

**Deliverables**
- Editor spans **all** monitors (one dim window per `Screens.All`, ConfettiWindow-style, or one union window).
- Dragging the mock across monitors updates the stored monitor identity + nearest-side anchors.
- Persisted placement self-heals when that monitor is later unplugged / resolution changes (reuse the
  `EnsureFloatingOnScreen` / `DenseScreen` self-heal pattern).

**Done when** placing on a secondary monitor persists and restores, and unplugging it falls back gracefully.

## Phase 5 — Polish, tests, docs

**Deliverables**
- Headless-render coverage if any editor surface is owner-drawn (`-- render <dir>`), theme-correct via `Palette`.
- Edge cases: single monitor, DPI mismatch between monitors, work-area smaller than the panel.
- Optional `Info("overlay-placement", …)` registry descriptor for discoverability + deep-link.
- Update `CLAUDE.md` (data-source / placement notes) and this plan's status.

**Done when** tests green, headless render clean in light+dark, docs updated.

---

## Decisions (locked)

1. **Manual header drags do not persist** — the editor is the sole source of truth for initial placement.
2. **Reset to defaults = current mode only** — resets just the mode being edited (floating or dense).
3. **MVP = single-monitor (Phases 0-3)**; multi-monitor is Phase 4.

## Risk / watch-list

- Coordinate-system mixing (physical vs DIP) — the whole feature lives on this seam; the extracted
  `PlacementMath` + unit tests are the guard.
- The editor is a topmost no-activate tool window like the overlay; Avalonia light-dismiss/focus quirks apply
  (see the `_openFlyout` manual-dismiss handling) — keep Done/Cancel explicit.
- Every OS-specific bit (click-through/no-activate) already sits behind `IWindowChrome`; don't call Win32 from the editor.
