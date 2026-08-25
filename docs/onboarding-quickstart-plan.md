# Onboarding Quick Start — implementation plan

**Branch:** `onboarding-quickstart`
**Status:** M0–M3 shipped as code (M0 fully tested; the wizard eyeballed via `render`; the first-run
popup + live-apply are runtime-unverified — they need the interactive tray app).
**Design mockup:** [`docs/onboarding-quickstart-mockup.html`](./onboarding-quickstart-mockup.html) — the interactive concept this plan implements.

## Problem

A first-time user meets Perch with everything on: an explosion of achievements, 60-odd
configuration options, and no map of the overlay. Getting started is hard. We want a **guided
Quick Start** that (a) explains the overlay layout and (b) lets the user pick a starting feature
set — **Basic / Intermediate / Kitchen sink** — each showing, grouped logically, exactly what it
switches on. It runs once on first launch and is **re-runnable any time** without undoing later
tweaks.

## Design in one paragraph

A four-step wizard in its own window: **Welcome** (what Perch is) → **The lay of the land** (an
annotated overlay so the layout is learned) → **Choose your setup** (three tiers with a live,
grouped breakdown + running counts) → **You're all set** (summary + "everything's still in
Settings, re-run any time"). Tiers are strict supersets: `Basic ⊆ Intermediate ⊆ Kitchen sink`.

A tier is a **preset**, not a locked bundle: picking one seeds a working set, and **each feature in
the grouped breakdown is individually clickable** so the user can fine-tune before applying. Deviate
from a preset and the chooser shows "custom" with a one-tap reset back to the tier. The wizard
applies whatever the final working set is — a tier, or any custom mix. Applying flips a **managed
set** of feature toggles; every non-managed setting (fields, thresholds, quick-links list) is left
untouched. Nothing is destroyed — a feature left off just stays one search away in Settings.

## Key architecture decisions

1. **Tier model lives in `Perch.Core`, data-driven off the settings registry.** A tier resolves to a
   set of `SettingDescriptor` ids (its preset). The **apply step takes an explicit enabled-set of
   ids**, not a tier — the tier just produces the initial set, and per-feature toggles mutate it.
   Applying walks the *managed* toggle universe and sets each to `id ∈ enabledSet`, using each
   descriptor's existing `SetBool`/`SetInt`. This keeps tiers testable headless, cross-platform, and
   automatically consistent with the registry (a coverage test fails if a tier names an id that
   doesn't exist). The HTML mockup's counts (11 / 33 / 51) are *illustrative* — the catalog is the
   source of truth; reconcile the mockup to it in M1.
2. **First-run signal is a new persisted bool, not `Program.IsFirstRun`.** `Program.IsFirstRun` is a
   Velopack per-install hook that never fires on `dotnet run`; we need something that survives and
   is dev-testable. Add `AppSettings.FirstRunComplete` (default `false`) + a migration that seeds
   **existing** installs to `true` (so an upgrading user isn't ambushed) keyed off the existing
   `LastSeenVersion != null` signal.
3. **Apply through the existing live funnel.** Persistence and live-apply are separate steps in this
   codebase. A tier apply mutates the shared `_appSettings`, calls `Save()` **once**, then
   `ApplyEffectiveSettings()` **once** — the same set→Save→apply funnel `OnQuietModeRequested` uses.
   Host-backed toggles (media, hypertree, social feed, todo poller, PR/Jira, stuck detection,
   hotkeys, metrics) additionally need their host started/stopped — the wizard's apply must fire the
   same syncs the Settings window's `SettingsHooks` do, or those features won't come alive until the
   next launch.
4. **Reuse the Settings preview stack for the visuals.** The "layout tour" and any live tier preview
   render a real detached `OverlayCanvas` seeded from `Rendering/SampleData` and gated through
   `Services/OverlaySettingsGates.Apply` — the same components the Settings live preview already
   uses. No new rendering primitives.
5. **The window follows the house pattern.** `OnboardingWindow : Window`, code-built (à la
   `StatsWindow`), colours from `Theming.Palette.*`, reused/opened via `WindowHost.ShowOrFocus` with
   an `_onboardingWindow` field closed in `CloseAuxWindows`.

## Grounding — files this touches

| Area | File / symbol |
|---|---|
| Settings model + load/save/migrate | `src/Perch.Core/Data/AppSettings.cs` — `Load()`, `Save()`, `Clone()`, `MigrateStartMode()` pattern; `LastSeenVersion` |
| Registry + descriptors | `src/Perch.Core/Data/SettingsRegistry.cs` (`All`), `SettingDescriptor.cs` (`Id`, `Playful`, `Requires`, `GetBool/SetBool`, `GetInt/SetInt`) |
| Quiet mode / playful mask | `src/Perch.Core/Data/QuietMode.cs` — `Resolve(raw, quietActive)` |
| Startup + trigger site | `src/Perch.App/App.axaml.cs` — `OnFrameworkInitializationCompleted()` (post-`_overlay.Show()`, mirror the `ShowChangelog` deferred block); replay guard |
| Live-apply funnel | `App.ApplyEffectiveSettings()` → `ApplyDisplaySettings()` → `OverlaySettingsGates.Apply`; host syncs in `OpenSettings` `SettingsHooks` |
| Window infra | `src/Perch.App/Windows/WindowHost.cs` `ShowOrFocus<T>`; `StatsWindow.cs` as the code-built template |
| Settings catalogue (re-run) | `SettingsCatalogView` / `SettingSurface` — a "Getting Started" section hosting the "Run quick start…" action |
| Preview stack | `Views/PreviewPane`, `Rendering/SampleData`, `Services/OverlaySettingsGates` |
| Theming | `src/Perch.App/Theming/Palette.cs` (`*Brush` singletons, colour accessors) |

---

## Milestones

Each milestone is independently shippable and leaves the build green. M0 has no user-visible effect
(no trigger wired), so it can land first and de-risk everything downstream.

### M0 — Tier model & persistence (Core, headless, fully tested)

The whole feature-set logic with zero UI. Highest test leverage; unblocks M1.

**Build**
- `Perch.Core/Data/OnboardingTier.cs` — `enum OnboardingTier { Basic, Intermediate, KitchenSink }`.
- `Perch.Core/Data/OnboardingTiers.cs` — the catalog:
  - The **managed toggle universe**: the explicit list of boolean `SettingDescriptor` ids the wizard
    governs (the six logical groups from the mockup — *At a glance / Usage & machine / Alerts /
    Dashboards & tools / Integrations / Fun & social*). **Excluded:** non-boolean config (Jira
    subdomain, thresholds, quick-links list); the always-openable dashboards (Stats/History/Flight —
    decision 2, described only); and `overlay-mode` (decision 4, set by its own step).
  - Per-tier membership (which managed ids are on), enforced as supersets.
  - `IReadOnlyList<OnboardingGroup> Groups` for UI: group label + icon + ordered items (id, display
    name, the lowest tier each turns on in) — so M1 renders straight from Core, no duplicated lists.
  - `IReadOnlySet<string> Preset(OnboardingTier tier)` — the managed ids a tier turns on (used to
    seed the chooser and to detect "does the current working set still equal a tier?").
  - `void Apply(AppSettings s, IReadOnlySet<string> enabledIds)` — the real apply: for each managed
    id, resolve its `SettingDescriptor` and `SetBool(s, id ∈ enabledIds)`. Plus any implied int/enum
    seeds (e.g. ensure `NotificationsEnabled` master on when any notify toggle is in the set). A thin
    `Apply(s, OnboardingTier tier) => Apply(s, Preset(tier))` overload covers the un-customised path.
  - `int Count(OnboardingTier tier)` helper for the card counts.
- `AppSettings`: add `bool FirstRunComplete { get; set; }` (default `false`) and
  `OnboardingTier? OnboardingTierChosen { get; set; }` (`[JsonIgnore(WhenWritingNull)]`, remembers
  the last preset picked to pre-select on re-run; a custom mix leaves it null / marked custom). Add
  both to `SettingsRegistryTests.NotSettings` (they're flags, not user-facing descriptors — like
  `ArcadeUnlocked`).
- `MigrateFirstRun()` called from `AppSettings.Load()` (beside `MigrateStartMode`): if
  `LastSeenVersion != null` (an existing install) and the key was absent, set
  `FirstRunComplete = true` so upgraders don't get the wizard.

**Tests** (`tests/Perch.Tests/OnboardingTiersTests.cs`)
- Every id in every tier / group resolves to a real `SettingsRegistry.All` descriptor with a
  non-null `SetBool`.
- Superset invariant: `Basic ⊆ Intermediate ⊆ KitchenSink`.
- `Apply(s, enabledIds)` sets exactly the given set (on for members, off for every other managed id)
  and mutates **no** unmanaged property (round-trip a settings object with sentinel field values and
  assert they survive).
- **Custom sets round-trip:** a hand-built set (e.g. Basic + one Kitchen-sink id) applies exactly —
  the one extra on, nothing else from Kitchen sink leaking in.
- `Apply` is idempotent and order-independent; applying Basic's preset after KitchenSink's turns the
  extras back off.
- Migration: fresh install (`LastSeenVersion == null`) → `FirstRunComplete == false`; existing
  install → `true`.
- `Count` matches the group membership (and reconcile the mockup's displayed numbers to it).

**Acceptance:** `dotnet test` green; no UI, no trigger, no behaviour change on launch.

### M1 — The Quick Start window (UI shell + tier chooser)

The window itself, driven entirely off M0. Not yet triggered on startup.

**Build**
- `src/Perch.App/Windows/OnboardingWindow.cs` — `internal sealed class OnboardingWindow : Window`,
  code-built, `Background = Palette.SurfaceSunkenBrush`, centred, fixed comfortable size. Ctor takes
  `(AppSettings settings, ...preview deps)`.
- Wizard chrome: left step rail (Welcome / The lay of the land / Where it lives / Choose your setup /
  You're all set), footer Back / Next / Finish, progress dots — mirroring the mockup, styled from
  `Palette.*`.
- **Step 1 (layout tour):** a detached `OverlayCanvas` seeded from `SampleData`, with numbered
  callouts/legend describing header+bird, system+usage, quick links, session rows, movable sections.
- **Step 2 (placement):** a two-card select — **Floating** (pre-selected) vs **Docked** — writing
  `AppSettings.OverlayMode`. Gate Docked on `IEdgeReservation.IsSupported` via `PlatformServices`:
  where unsupported, disable the card ("Windows only") or omit the step and keep Floating. Applied
  through the same live funnel as the tier (Docked needs the edge-reservation host started — the
  Settings `OverlayModeChanged` hook is the reference).
- **Step 3 (tier chooser + fine-tune):** three tier cards + a live grouped breakdown rendered from
  `OnboardingTiers.Groups`. Each feature chip is a **toggle button** (`aria-pressed`) — clicking it
  adds/removes that id from the working set. Selecting a tier card reseeds the working set to that
  preset. On-items highlighted, off-items ghosted, per-group `n/total`, a running total. When the
  working set no longer equals any preset, no card is selected, the header reads "your custom mix",
  and a **Reset to tier** control reseeds `selectedPreset`. Pre-select
  `OnboardingTierChosen ?? Intermediate`. Optionally re-gate a live `OverlayCanvas` preview via
  `OverlaySettingsGates.Apply` on a cloned settings as the set changes.
- **Finish:** `OnboardingTiers.Apply(settings, workingSet)` → record `OnboardingTierChosen` (the
  matched tier, or null when custom) + `FirstRunComplete = true` → `settings.Save()` → raise an
  `Applied` event the App handles to run the live funnel (M2) → close. (Window only persists +
  signals; App owns live-apply.)
- Headless render: register the window's surfaces in `HeadlessRenderer` so
  `dotnet run … -- render <dir>` dumps them at 1× / 1.5× for eyeballing.

**Tests / verification**
- `UiConventionTests` still green (no `Watermark`, owner-drawn text sized from line height).
- Manual: `render` snapshots of all four steps in both themes; open the window directly via a temp
  debug entry.

**Acceptance:** window opens standalone, tiers switch and re-count correctly, Finish writes the
right settings to disk; renders cleanly in light and dark.

### M2 — Wiring: first-run trigger + re-run entry points + correct live-apply

Make it appear at the right time and apply completely.

**Build**
- **First-run trigger** in `OnFrameworkInitializationCompleted`, after `_overlay.Show()`, inside the
  existing `if (!ReplaySession.IsActive)` guard, mirroring the deferred `ShowChangelog` block:
  `if (!settings.FirstRunComplete) Dispatcher.UIThread.Post(ShowOnboarding, DispatcherPriority.Background);`
- `App.ShowOnboarding()` via `WindowHost.ShowOrFocus(ref _onboardingWindow, ...)`; add
  `_onboardingWindow` field; close it in `CloseAuxWindows`.
- **Re-run entry (decision 3):** a single "Run quick start…" action in a **Getting Started** section
  of Settings that calls `ShowOnboarding()`. If a new `SettingSurface.GettingStarted` /
  catalogue section is the cleanest home, add it; otherwise a button on the nearest existing intro
  page. Keyword-searchable ("setup", "onboarding", "quick start"). No tray item.
- **Complete live-apply** on the window's `Applied` event: call `ApplyEffectiveSettings()` **and**
  fire the host syncs a tier/placement can flip — the same set the Settings `SettingsHooks` cover:
  `MediaEnabledChanged`, `HypertreeEnabledChanged`, `HotkeysChanged`, `MetricsChanged`,
  `OverlayModeChanged` (placement step), `_feedHost.SetActive`, `_todoHost.Start/Stop`, and the
  `_monitorHost` PR/Jira/GitStats/Stuck flags. Factor a single `ApplyAllSettingsLive()` if it reduces
  duplication with `OpenSettings`.

**Tests / verification**
- Manual first-run: delete the profile `settings.json`, `dotnet run` → wizard appears once; finish →
  overlay reflects the tier live (no restart); relaunch → wizard does **not** reappear.
- The Settings "Run quick start…" entry reopens it; re-running a **lower** tier turns the extras off
  live; re-running does not clobber unmanaged config (Jira subdomain, thresholds); changing placement
  re-docks/undocks live.
- Replay (`perch replay`) does not trigger the wizard.

**Acceptance:** clean first-run once, re-runnable from the Settings Getting Started section, tier and
placement changes apply live and completely, upgraders are not ambushed.

### M3 — Polish, Quiet-mode interaction, a11y, docs

**Build / verify**
- **Quiet mode note:** if a tier enables playful features while a Quiet window is active, reads go
  through `Effective` so they'll show as off until quiet ends — surface a one-line note in the wizard
  ("Quiet mode is on; playful features switch on when it ends") rather than looking broken.
- Keyboard nav (arrows/Tab/Enter), visible focus, `prefers-reduced-motion`-equivalent restraint, DPI,
  both themes; final `render` snapshots.
- `PresetContrastTests`/contrast: any new text/glyph colours clear WCAG AA on their surface.
- Changelog entry (`changelog-on-update` surfaces it) + update this doc's status.

**Acceptance:** feature complete, both themes pass contrast, docs current, changelog notes it.

---

## Risks & gotchas

- **Live-apply is two-phase.** Mutating a property shows nothing until `ApplyDisplaySettings`/
  `ApplyEffectiveSettings`; host-backed features additionally need start/stop. A tier that flips
  media/hypertree/feed/todo/PR/Jira/stuck without the host sync will look half-applied until relaunch
  — M2 must cover the full `SettingsHooks` set.
- **Don't clobber unmanaged settings.** Apply only touches the managed toggle universe; assert this
  in M0 tests so a future contributor adding to a tier can't silently reset a field/threshold.
- **Upgraders.** Without the seed migration, every existing user gets the wizard on the update that
  ships this. The `LastSeenVersion`-keyed migration prevents that.
- **perch-hook mini-parser.** The hook reads `settings.json` as strings and cares about enum member
  names; new bools/enums here don't affect it, but keep `OnboardingTier` member names stable if ever
  serialized by name.
- **SettingsRegistry coverage test.** `FirstRunComplete` / `OnboardingTierChosen` are flags, not
  descriptors — add them to `NotSettings` or the coverage test fails the build.
- **Mockup vs catalog drift.** The mockup's 11/33/51 are illustrative; the catalog is authoritative.
  Reconcile the displayed numbers in M1 and keep them derived from `OnboardingTiers.Count`.

## Decisions (settled — 2026-08-25)

1. **Basic is silent.** No sound chimes in Basic — desktop notification only for done/waiting. Chimes
   start at Intermediate. A user who wants a chime can flip it on the fine-tune chips.
2. **Dashboards are described, not managed.** Stats / History / Flight have no on/off setting and are
   always openable; the wizard *mentions* them (layout tour + summary copy) but they are **not** in
   the managed toggle universe and render as read-only notes, not clickable chips.
3. **Re-run lives in a "Getting Started" section.** A single entry point — "Run quick start…" — in a
   Getting Started area of Settings (see M2). No separate tray item / Advanced descriptor.
4. **Placement is its own wizard step, before the tier chooser.** A dedicated select step —
   **Floating vs Docked**, defaulting to **Floating** — sets `AppSettings.OverlayMode`. This removes
   overlay-mode from the tier bundles entirely. Docked requires `PlatformFeature.EdgeReservation`
   (Windows-only), so on heads without it the Docked option is disabled/omitted and Floating is the
   silent default — no platform filtering needed inside the tier `Apply`.

## Wizard steps (final)

`0` Welcome · `1` The lay of the land · `2` **Where it lives** (Floating / Docked) · `3` Choose your
setup (tiers + per-feature fine-tune) · `4` You're all set.
