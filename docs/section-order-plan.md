# Configurable overlay section order — implementation notes

## Status

**Shipped as code (build + tests + headless render green; interactive drag verified via render of the
owner-drawn affordances, the buttons pending a live-app look).**

- Data model — `OverlaySection` enum + `OverlaySectionOrder.Normalize` + `AppSettings.SectionOrder`
  + `SettingsRegistry` entry. ✅ (unit-tested in `OverlaySectionOrderTests`)
- Layout refactor — single ordered pass in `OverlayCanvas` drives both measure and paint. ✅
- Rearrange interaction — drag-to-reorder in the Settings live preview + "Reset arrangement". ✅ (code)
- Verification — `overlay_sections_default/reordered/rearrange_*.png` render probes. ✅

## Goal

Let the user choose the top-to-bottom order of the overlay panel's movable sections by dragging them in
the Settings → Features **live preview**, persist that order, and offer a **"Reset arrangement"** button
whenever it differs from the default.

Decisions (confirmed with the user):

- **Header pinned top, outage status bar pinned bottom.** The nine sections between them are movable:
  system info, claude metrics, quick links, hypertree, todo, sessions, friends, media, call.
- **Default order:** `SystemInfo, ClaudeMetrics, QuickLinks, Hypertree, Todo, Sessions, Friends, Media,
  Call` (this reorders friends/media/call versus the old hard-coded call/media/friends — intentional).
- **Rearrange is a toggle button** beside Reset. In rearrange mode the preview shows **all nine**
  sections (sample data), with the ones **disabled in settings dimmed**, so any can be positioned.
- The daemon-worker strip travels as part of **Sessions**; the "sign in to Social" prompt as part of
  **Friends** (they never split from their unit).

## How it works

### Data model (`Perch.Core`)

- `OverlaySection` (enum, persisted by name via `JsonStringEnumConverter`).
- `OverlaySectionOrder.Default` / `.Normalize(saved)` / `.IsDefault(order)` — pure, tested. `Normalize`
  keeps the saved order, drops unknown/duplicate values, and splices any missing section in at its
  natural spot (after the last of its default-predecessors already present), so a stale/partial file
  self-heals.
- `AppSettings.SectionOrder` (`List<OverlaySection>?`, null = default), registered as an `Info`
  descriptor `section-order` so search finds it and the coverage tests pass.

### Layout (`OverlayCanvas`)

The order used to be hard-coded twice (measure `PanelBodyHeight`, paint `Draw`) with two offset
mechanisms. Now one ordered pass drives both:

- `_sectionOrder` (normalized) + `_sectionTop` (each visible section's absolute top Y).
- `PanelBodyHeight()` walks `_sectionOrder`, records `_sectionTop`, returns the body height.
- `Draw()` paints each visible section at `_sectionTop[s]` via `PaintSection` (in
  `OverlayCanvas.Sections.cs`).
- The chained `…Top` getters (`UsageStripTop`, `QuickLinksTop`, `HypertreeTop`, `TodosTop`, `RowsTop`,
  new `SystemInfoTop`) now read `_sectionTop`, so every existing hit-test follows the painted order.
  Bottom strips (mic/media/feed) already captured their hit-rects at paint time from the passed `top`,
  so they needed no change.
- The system-metrics/usage divider is drawn only when `NextVisibleSection(SystemInfo) == ClaudeMetrics`
  (they may no longer be neighbours).

### Rearrange (`OverlayCanvas.Rearrange.cs`, preview only)

- `RearrangeMode` (set only by `PreviewPane.SetRearrange`) snapshots which sections are off (for
  dimming), then forces every gate on so all nine lay out against the preview's sample data.
- Pointer press picks up the section under the cursor; move shows an accent insertion line and a grip
  (`⋮⋮`) per band; release splices the section into its new slot and raises `SectionOrderChanged`.
- The live overlay never enters rearrange mode, so its drag/click routing is untouched.

### Wiring

- `OverlaySettingsGates.Apply` pushes the order via `SetSectionOrder(Normalize(settings.SectionOrder))`
  — the one seam the live overlay and the preview both go through.
- `SettingsWindow.BuildArrangeControls` (Features page): a **Rearrange** `ToggleButton` and a **Reset
  arrangement** `Button` (shown only when non-default). A drop persists `_settings.SectionOrder`, saves,
  and calls `DisplayChanged` (re-lays-out the live overlay). Leaving rearrange re-applies settings to
  restore the real gates. Reset clears the order and pushes the default onto the preview.
- `SampleData.Hypertree()` / `.Todos()` were added and seeded into `PreviewPane` (previously absent), so
  those sections appear in the preview/rearrange view.

## Verification

- `dotnet build perch.slnx`; `dotnet test tests/Perch.Tests/Perch.Tests.csproj` (incl.
  `OverlaySectionOrderTests`).
- `dotnet run --project src/Perch.App -f net10.0-windows10.0.19041.0 -- render <dir>` →
  `overlay_sections_default_1x.png`, `overlay_sections_reordered_1x.png`,
  `overlay_sections_rearrange_1x.png` (grips + dimmed disabled sections). The mid-drag insertion line
  and the Settings buttons need the running tray app.

## Known limitations / follow-ups

- Hypertree is data-driven (no on/off gate on the canvas), so in rearrange it never shows dimmed even if
  the Hypertree *setting* is off — it just shows with sample branches.
- Editing a catalogue toggle while actively rearranging re-gates the preview (restoring the real gates);
  finish rearranging first. Not expected in practice.
- Section order applies to the floating/docked-expanded full panel; the dense strip and docked-collapsed
  strip have their own compact art and are unaffected.
