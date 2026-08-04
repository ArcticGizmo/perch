# Perch Settings Overhaul — Implementation Plan

> **Goal:** the full settings overhaul — settings organised by *where they appear*, a **search**
> bar that finds anything, and a **live preview** that shows each change as you make it.
>
> **Branch:** `settings-redesign` · **Companion artefacts:** `docs/settings-redesign-proposals.html`,
> `docs/settings-redesign-plan.html`

The three payoffs:

| | | |
|---|---|---|
| **Search** | Find by name or synonym | Type "chime", "cost", "git" → land on the switch. |
| **Catalogue** | Browse by surface | Every feature as a card, grouped by where it draws. |
| **Live preview** | See it before you set it | A real mini-overlay reacts to every toggle. |

---

## Feasibility — confirmed against the code

The live preview **reuses the real overlay**; no new renderer is needed.

- `OverlayCanvas` (`src/Perch.App/Views/OverlayCanvas.cs`) reads **no** settings globally. Every glyph is
  gated through a `Set*` method, and its sessions are pushed in via `Update(IReadOnlyList<ClaudeSession>)`.
  It is a pure sink — all file-watching lives in the `*MonitorHost` services and `Perch.Core/Data`.
- `HeadlessRenderer` (`src/Perch.App/Rendering/HeadlessRenderer.cs`) already constructs synthetic
  `ClaudeSession` data (`SampleSessions()`, ~lines 564–611) and renders a bare `OverlayCanvas` with **no
  window** via a `RenderTargetBitmap`.
- Today's live update path is: toggle → mutate the shared `AppSettings` → `Save()` → `SettingsHooks.DisplayChanged`
  → `App.ApplyDisplaySettings(settings)` → the canvas-gate block (`App.axaml.cs` ~505–528) pushes every gate
  onto `_overlay.Canvas`.

So the preview is **"`HeadlessRenderer`'s setup, embedded in a live control, re-fed on every toggle"** — driven
by a temp `AppSettings` **clone** so the user's real settings stay untouched until they commit.

---

## The architectural spine — a Settings Registry

All three features want the same thing: a description of every setting. Build that **once** as a registry,
and search, the catalogue, and the preview all read from it.

```
                         ┌─────────────────────────────────────────────┐
                         │              Settings Registry              │
                         │  one descriptor per setting:                │
                         │  id · name · keywords[] · surface · kind    │
                         │  · get/set → AppSettings · previewTarget    │
                         └─────────────────────────────────────────────┘
                            │                  │                  │
                 ┌──────────┘         ┌────────┘        └──────────┐
                 ▼                    ▼                            ▼
          ┌────────────┐       ┌────────────┐              ┌────────────┐
          │   Search   │       │  Catalogue │              │Live preview│
          │ name +     │       │ group by   │              │ previewTgt │
          │ keywords   │       │ surface →  │              │ → glyph,   │
          │ token-AND  │       │ cards      │              │ pulse it   │
          └────────────┘       └────────────┘              └────────────┘
```

Bindings read/write a temp `AppSettings` clone → shared `OverlaySettingsGates.Apply(canvas, settings)` feeds
the preview. Committing writes the real settings + fires `DisplayChanged` for the actual overlay.

**Descriptor shape (draft):**

```csharp
enum SettingSurface { SessionRow, UsageBars, TrayAndStats, Notifications, SystemMetrics, Whimsy, Integrations, Advanced }
enum SettingKind    { Toggle, Slider, Stepper, Dropdown, Field, Hotkey, List }
enum PreviewTarget  { None, ModeBadge, TaskProgress, ContextPressure, WaitingTimer, Artifacts, BurnRate,
                      GitStats, PullRequest, Note, UsageBars, ExpectedRate, SystemMetrics, PerchReacts, /* … */ }

sealed record SettingDescriptor(
    string          Id,
    string          Name,
    string[]        Keywords,
    SettingSurface  Surface,
    SettingKind     Kind,
    string          Description,
    PreviewTarget   Preview = PreviewTarget.None,
    // typed access to AppSettings — only the pair matching Kind is set
    Func<AppSettings, bool>?    GetBool = null, Action<AppSettings, bool>?   SetBool = null,
    Func<AppSettings, int>?     GetInt  = null, Action<AppSettings, int>?    SetInt  = null /* … */);
```

---

## Milestones

Each milestone is landable on its own and committed as it lands.

### M0 — Foundations & the de-risk spike ✅ *done*
**Objective:** prove the hard part before building UI.

- [x] Add the `SettingDescriptor` record + `SettingSurface` / `SettingKind` / `PreviewTarget` enums.
      *(Core-side: `src/Perch.Core/Data/SettingDescriptor.cs`.)*
- [x] Extract the canvas-gate block of `App.ApplyDisplaySettings` into a shared
      `OverlaySettingsGates.Apply(OverlayCanvas, AppSettings)`; have the live path call it — **no behaviour change**.
      *(`src/Perch.App/Services/OverlaySettingsGates.cs`.)*
- [x] Add `AppSettings.Clone()` via the existing JSON round-trip for a detached preview snapshot.
- [x] Promote `HeadlessRenderer.Sample*` builders into a shared `SampleData` class (the preview seed).
      *(`src/Perch.App/Rendering/SampleData.cs`.)*
- [x] Spike: drive a seeded `OverlayCanvas` purely through `OverlaySettingsGates.Apply` with a mutated clone
      and render it — added as a durable probe in `render` mode (`overlay_preview_on/off_1x.png`).

**Exit — met:** the gate extraction is a byte-identical move (live overlay unchanged); the render probe shows
the overlay visibly re-gate (system strip, usage bars and row glyphs appear/disappear) from a cloned settings
snapshot, validating the M1 preview approach.
**Effort:** low–med · **Risk:** med · **Depends on:** nothing.

### M1 — The live preview pane
**Objective:** highest technical risk — build it early.

- [ ] Build `PreviewPane`: owns an embedded `OverlayCanvas` (`OwnerWindow` null, never dense — keeps the
      window-coupled relayout paths inert), seeded once from `SampleData`.
- [ ] `PreviewPane.Apply(AppSettings snapshot)` → `OverlaySettingsGates.Apply` + invalidate; scale to a fixed
      miniature width via a `Viewbox`, scroll if taller.
- [ ] Scaffold `Highlight(PreviewTarget)` (a real spotlight lands in M4; a stub is fine here).
- [ ] Verify crispness at 1× and 1.5× through `render` mode.

**Exit:** dropping `PreviewPane` in a harness reflects clone toggles within a frame, never touches the real
overlay, and renders cleanly at both scales.
**Effort:** med · **Risk:** med · **Depends on:** M0.

### M2 — Registry population + search
**Objective:** biggest win on "I can't find it."

- [ ] Populate `SettingsRegistry` — ~50 descriptors with keywords/synonyms, surface, kind and get/set bindings
      for all of `AppSettings`.
- [ ] Coverage unit test in `Perch.Tests`: reflect over the display properties of `AppSettings` and assert each
      has a registry entry — a durable guard against drift.
- [ ] Search UI: persistent filter field, token-AND match over name + keywords, result rows (name, surface
      breadcrumb, live control reusing `PerchToggle`); keyword-hit hint; empty-query = full index; empty-result state.

**Exit:** "chime / sound / cost / cpu / git / phone" each surface the right setting; coverage test green;
toggling a result mutates real settings + `Save()` + `DisplayChanged` at parity with today.
**Effort:** med · **Risk:** low–med · **Depends on:** M0.

### M3 — The catalogue surfaces
**Objective:** biggest win on "what features exist?".

- [ ] Assign each descriptor a surface: *Session row · Usage bars · Tray & stats · Notifications · System &
      metrics · Whimsy · Integrations · Advanced*.
- [ ] Feature-card control driven by descriptor: mini glyph preview + name + one-line description + control
      (static glyphs first; live later).
- [ ] Surface sections + filter chips to narrow by surface.
- [ ] "Advanced" bucket for config-heavy settings that don't fit a card (ntfy host/topic, context thresholds
      slider, hotkeys, reopen-terminal, export, about, changelog) — expandable detail rows.

**Exit:** every setting reachable from the catalogue, each visual feature shows a representative preview, and
the chips filter by surface.
**Effort:** high · **Risk:** med · **Depends on:** M2.

### M4 — Unified shell + preview linkage
**Objective:** the three become one window.

- [ ] New shell: top search bar (`Ctrl+F` focuses it), main pane = catalogue or search results, side/bottom
      dock = `PreviewPane`.
- [ ] Linkage: hovering/focusing a card or result calls `PreviewPane.Highlight(previewTarget)` → the affected
      glyph pulses; editing a setting re-applies to the preview's working clone immediately.
- [ ] Commit path unchanged: `Save()` still writes the real `AppSettings` and fires `DisplayChanged` for the
      actual overlay.
- [ ] Migrate remaining non-visual pages (Getting started, Export, About, Changelog) into shell sections.

**Exit:** end-to-end — hover a card → glyph highlights; toggle → preview and real overlay both update; search,
catalogue and preview coexist; nothing from the old window is unreachable.
**Effort:** high · **Risk:** med · **Depends on:** M1, M3.

### M5 — Cutover, accessibility & cleanup
**Objective:** ship-ready; retire the old.

- [ ] Keyboard: `Ctrl+F` to search, arrow/enter through results and cards, `Esc` to close.
- [ ] Respect `prefers-reduced-motion` (no pulse), keep the preview crisp at 1.5×, tabular numerals on figures.
- [ ] Extend `render` mode to dump the new surfaces + preview for eyeball verification.
- [ ] Remove the dead page builders from `SettingsWindow.cs`; update the CLAUDE.md settings notes and add a
      changelog entry (`/bump-version`).

**Exit:** `render` mode emits the new UI, no orphaned settings code remains, and docs describe the new architecture.
**Effort:** med · **Risk:** low · **Depends on:** M4.

---

## Why this order

1. **Risk before polish.** The preview is the one genuinely uncertain piece, so M0–M1 prove it renders live
   in-window before any UI is committed to it.
2. **The registry earns its keep twice.** Search (M2) forces a complete, correct registry — with a unit test to
   keep it honest. The catalogue (M3) and preview linkage (M4) then inherit that for free.
3. **Value ships mid-way.** After M2 you already have working search — Pain #1 solved — before the catalogue exists.
4. **Integration last, not first.** Building `PreviewPane` and the catalogue as independent controls (M1, M3)
   means M4 is assembly and wiring, not invention.

---

## Testing & cutover strategy

**Keeping it honest**

- **Registry coverage test** — reflect over `AppSettings` display props; fail the build if any lacks a descriptor.
- **Gate-extraction regression** — the M0 refactor must leave the live overlay byte-identical; verify by eye +
  `render` before/after.
- **Render-mode snapshots** — the standing way to eyeball owner-drawn UI; extend it to the new surfaces and preview.
- **Manual overlay parity** — the real overlay and the preview share `OverlaySettingsGates.Apply`, so they can't
  silently diverge.

**Building alongside, not on top**

- New components (`PreviewPane`, catalogue, search) are built **next to** the current `SettingsWindow`, which keeps
  working throughout.
- Swap the `ShowSettings` entry point to the new shell only when M4 reaches parity — a one-line, easily-reverted cutover.
- Optional dev env flag to flip old ↔ new during M4 for side-by-side comparison.
- Old page builders deleted only in M5, after the new window has carried real use.

---

## Risk register

| Risk | Severity | Mitigation |
|---|---|---|
| Miniature sizing / DPI — scaling the overlay down, legible at 1.5×. | Medium | Spike in M1 with `Viewbox` + `render` checks before the shell depends on it. |
| Binding non-toggle controls generically (sliders, steppers, hotkeys, dropdowns). | Medium | Descriptor carries a `kind` + typed accessors; cards/rows switch on kind. Toggles first. |
| Window width growth — a docked preview wants room beyond today's 880px. | Low | Raise `MinWidth`; collapse the preview to a bottom dock at narrow sizes. |
| Registry drift — a new setting added without a descriptor. | Low | The coverage unit test fails the build until the descriptor exists. |
| Sample-data staleness — preview stops exercising a new glyph. | Low | Single `SampleData` source shared with `HeadlessRenderer`. |
| Scope creep on live glyph previews in cards. | Medium | Cards ship with static glyphs in M3; the full live preview lives in the docked pane only. |
