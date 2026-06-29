# Iconography settings tab — consolidation plan

Consolidate three overlay-glyph settings groups into a single **Iconography** tab, add a display
toggle for permission-mode badges, and relocate **Automation** onto the Getting started page.

## Goal

A new left-nav tab **Iconography** that gathers everything controlling what icons/badges show next
to a session in the overlay:

1. **Permission mode badges** — moved out of *Getting started*. Gains its own on/off toggle
   (default **on**) that gates whether the badge is drawn in the overlay.
2. **Context pressure** — moved from the *Context* tab. The "Thresholds" explainer is dropped; the
   threshold slider sits directly under the context-pressure description.
3. **Detection** — moved from the *Detection* tab, with its explanatory prose trimmed.

The standalone **Context**, **Detection**, and **Automation** tabs are removed. Automation's two
toggles move to the bottom of the **Getting started** page.

## Clarifying questions (answered)

1. **Where should the Iconography tab sit in the nav rail?**
   → **Replace the Context/Detection slot** — just after *Usage Limits*, before *Session Stats*.
2. **Where do the Automation toggles go on Getting started?**
   → **Bottom of the page** (a new "Automation" section after the existing content).
3. **Permission-mode-badge toggle / legend behaviour?**
   → **Legend lives in Iconography only**; remove the legend section from Getting started (its prose
   bullet mentioning badges stays). When the toggle is **off**, the overlay hides the badge **and
   reclaims its layout width**.
4. **How should Detection move across?**
   → **Trim the explanatory text** — keep the master toggle and both sub-rows, cut the long prose to a
   single concise line (and drop the "What to watch for" sub-heading) to keep the merged tab compact.

### Open questions (none blocking)

- None outstanding. Defaults below are taken from the answers above.

---

## Current state (reference)

- `src/Ui/SettingsForm.cs`
  - `BuildLayout()` (~L192–202) registers pages via `AddPage(...)`. Today includes `"context"`,
    `"detection"`, and `"auto"`.
  - `BuildGettingStartedPage()` (~L340) — banner, "What it does" bullets, then a
    **"Permission mode badges"** section (SectionTitle + BodyText + `ModeLegend`).
  - `BuildContextPage()` (~L603) — context-pressure toggle, two body paragraphs, a separator, a
    **"Thresholds"** SectionTitle + explainer BodyText, then the `ContextThresholdSlider`.
  - `BuildDetectionPage()` (~L642) — stuck master toggle, two body paragraphs, separator,
    "What to watch for" SectionTitle, two `BuildStuckSubRow(...)` sub-rows.
  - `BuildAutomationPage()` (~L1137) — `_autoStartToggle` + body, separator, `_autoCloseToggle` + body.
  - Existing events already wired: `ContextPressureChanged`, `ContextThresholdsChanged`,
    `StuckDetectionChanged`. The slider is `ContextThresholdSlider`; the legend is `ModeLegend`
    (both in `src/Ui/SettingsControls.cs`).
- `src/Data/AppSettings.cs` — `ShowContextPressure` (L24), `ContextPressure*Percent` (L29–31),
  `StuckDetectionEnabled`/`DetectErrorStreaks`/`DetectFailingLoops` (L37–39),
  `AutoStartOnFirstSession`/`AutoCloseAfterLastSession` (L83–84). **No** permission-badge display
  setting exists yet.
- `src/Ui/OverlayForm.cs` — badge always drawn when `session.Mode != PermissionMode.Normal`:
  `badgeWidth` computed at ~L1165 (used in name-width math at L1177 and the thermometer offset), and
  the draw at ~L1211–1217. Sibling display flags follow the `_showContextPressure` /
  `_showStuckWarnings` pattern with `SetShow...` methods (~L356).
- `src/App/OverlayApplicationContext.cs` — startup pushes settings to the overlay (~L163–170,
  `ApplyStuckDetectionSettings` at L170), `OpenSettings()` wires form events (~L222–233), and
  per-setting handlers live at ~L286–331 (`SetContextPressureEnabled`, `SetContextThresholds`,
  `SetStuckDetection`).

---

## Phase 1 — Data layer: badge display setting

**File:** `src/Data/AppSettings.cs`

- Add `public bool ShowPermissionModeBadges { get; set; } = true;` near the other overlay-display
  flags (next to `ShowContextPressure`). Add a brief comment matching the surrounding style.

**Tests:** add/extend a settings round-trip test if one exists for these flags (default true,
persists on save/load). See `tests/Perch.Tests/`.

---

## Phase 2 — Overlay rendering: gate the badge

**File:** `src/Ui/OverlayForm.cs`

- Add a field `private bool _showModeBadges = true;` alongside `_showContextPressure` etc.
- Add `public void SetShowModeBadges(bool show)` mirroring `SetShowContextPressure` (early-return on
  no-change, set field, `Invalidate()`).
- In the row paint (~L1165): change `badgeWidth` to
  `session.Mode != PermissionMode.Normal && _showModeBadges ? 16 : 0`. Because `badgeWidth` already
  feeds `nameMaxWidth` (L1177) and the thermometer/badge X offsets, setting it to 0 reclaims the
  layout width automatically — no other geometry edits needed.
- Guard the draw block (~L1211) with `&& _showModeBadges` so the glyph isn't painted when off.

**Verify:** toggling the flag hides the badge *and* lets the session name expand into the freed space.

---

## Phase 3 — Build the Iconography page

**File:** `src/Ui/SettingsForm.cs`

1. **New event:** add
   `public event Action<bool>? PermissionModeBadgesChanged;` near the other display events
   (alongside `ContextPressureChanged`), with an XML doc comment.

2. **New field:** `private ToggleSwitch _modeBadgesToggle = null!;` in the field block.

3. **Replace** `BuildContextPage` and `BuildDetectionPage` with a single `BuildIconographyPage`
   (or keep them as private section-builders called from `BuildIconographyPage` — either is fine, but
   a single method reads cleaner). Section order = user's list, separated by `Separator()`:

   **a. Permission mode badges**
   - `_modeBadgesToggle = MakeToggle(); _modeBadgesToggle.Checked = _settings.ShowPermissionModeBadges;`
     On change: raise `PermissionModeBadgesChanged?.Invoke(...)` **and** dim the legend
     (`legend.Enabled = _modeBadgesToggle.Checked;` — or grey it, matching the `ApplyStuckEnabled`
     dimming idiom).
   - `page.Add(TitleRow("Permission mode badges", _modeBadgesToggle));`
   - `page.Add(BodyText("When the Claude Code plugin is installed, each session's live permission mode is shown as a coloured badge next to that session in the overlay:"));`
   - `var legend = new ModeLegend { Margin = new Padding(0, 2, 0, 8) }; _fluidWidth.Add((legend, 0)); page.Add(legend);`
     (lifted verbatim from the old Getting started section).

   **b. Context pressure** (lifted from `BuildContextPage`, explainer dropped)
   - Keep the context-pressure toggle + its `TitleRow("Context pressure", toggle)` and both body
     paragraphs.
   - **Remove** the `Separator()`, the `SectionTitle("Thresholds")`, and the
     "Drag the handles…" explainer BodyText.
   - Place the `ContextThresholdSlider` **directly under** the context-pressure body text. Preserve
     the existing `SetValues` / `Enabled` / `RangeChanged` wiring and `_fluidWidth` registration. The
     slider's painted bands already serve as the visual explainer the prose used to provide.

   **c. Detection** (lifted from `BuildDetectionPage`, prose trimmed)
   - Keep `_stuckMasterToggle` + `TitleRow("Stuck detection", _stuckMasterToggle)` and the
     `ApplyStuckEnabled()` / `RaiseStuckChanged()` plumbing.
   - **Trim:** replace the two explanatory paragraphs with a single concise line, e.g.
     *"Flags a running session that looks stuck with an amber overlay warning. It's a heuristic —
     switch off whichever check is too eager."* Drop the `SectionTitle("What to watch for")`
     sub-heading.
   - Keep both `BuildStuckSubRow(...)` calls and the trailing `ApplyStuckEnabled();`.

4. **Update `BuildLayout()`** `AddPage` list (~L192–202):
   - Remove `AddPage("context", …)`, `AddPage("detection", …)`, and `AddPage("auto", …)`.
   - Insert `AddPage("iconography", "Iconography", BuildIconographyPage);` in the slot vacated by
     Context (after `"usage"`, before `"stats"`).

---

## Phase 4 — Getting started: drop badges, add Automation

**File:** `src/Ui/SettingsForm.cs`, `BuildGettingStartedPage()` (~L340)

- **Remove** the trailing `Separator()` + `SectionTitle("Permission mode badges")` + its BodyText +
  `ModeLegend` block (now lives in Iconography). Leave the "What it does" bullet that mentions
  permission-mode badges as prose.
- **Append** an Automation section at the bottom: lift the body of `BuildAutomationPage()` here —
  `Separator()`, then `TitleRow("Start automatically", _autoStartToggle)` + body, `Separator()`,
  `TitleRow("Close automatically", _autoCloseToggle)` + body. Keep the existing toggle fields and
  their save-on-change handlers unchanged.
- **Delete** `BuildAutomationPage()` once its content is relocated.

---

## Phase 5 — Wire the new badge event

**File:** `src/App/OverlayApplicationContext.cs`

- In `OpenSettings()` (~L222), add `f.PermissionModeBadgesChanged += SetPermissionModeBadgesEnabled;`.
- Add the handler beside `SetContextPressureEnabled` (~L294):
  ```csharp
  private void SetPermissionModeBadgesEnabled(bool enabled)
  {
      if (_settings.ShowPermissionModeBadges == enabled) return;
      _settings.ShowPermissionModeBadges = enabled;
      _settings.Save();
      _overlay.SetShowModeBadges(enabled);
  }
  ```
- In startup (~L165, near `SetShowContextPressure`), add
  `_overlay.SetShowModeBadges(_settings.ShowPermissionModeBadges);`.

> The Context, Threshold, and Stuck-detection handlers/events are unchanged — they were already wired
> and the form still raises the same events from the merged page.

---

## Phase 6 — Build, test, verify

- `dotnet build src/Perch.csproj` — confirm no references to the removed `BuildContextPage` /
  `BuildDetectionPage` / `BuildAutomationPage` or the `"context"` / `"detection"` / `"auto"` page keys
  remain (grep for them).
- `dotnet test tests/Perch.Tests/Perch.Tests.csproj` — including the new settings test.
- Manual (tray app) eyeball, per CLAUDE.md (UI has no automated coverage):
  - Nav rail shows **Iconography** in place of Context/Detection; no Automation tab.
  - Iconography page: badge section + legend, context pressure + slider (no "Thresholds" block),
    trimmed Detection. Separators between the three.
  - Toggling **Permission mode badges** off hides the overlay badge and the name reclaims the space;
    legend dims when off.
  - Context toggle still enables/disables the slider; slider drag still persists thresholds.
  - Detection master + sub-rows still dim/persist and re-scan.
  - Getting started: badge section gone, Automation toggles at the bottom and still persisting.

## Touched files

- `src/Data/AppSettings.cs` — new `ShowPermissionModeBadges` flag.
- `src/Ui/OverlayForm.cs` — `_showModeBadges`, `SetShowModeBadges`, gated badge draw/width.
- `src/Ui/SettingsForm.cs` — new event + toggle, `BuildIconographyPage`, page-list edits, Getting
  started edits, delete the three old page builders.
- `src/App/OverlayApplicationContext.cs` — new handler + event wiring + startup push.
- `tests/Perch.Tests/` — settings round-trip coverage for the new flag.

## Notes / risks

- `ModeLegend` and `ContextThresholdSlider` are unchanged and simply re-hosted.
- No settings-key renames, so existing `settings.json` files keep working; the new flag defaults to
  `true` when absent.
- Keep the merged page's owner-drawn controls (`ModeLegend`, slider) registered in `_fluidWidth` so
  they re-flow on resize, per the existing fluid-layout pattern.
