# Perch Theming & Accessible Colour — Implementation Plan

Branch: `themes`

## 1. Goal

Improve the **contrast, legibility and eye-comfort** of Perch's UI (settings surface first,
overlay and every window second), and turn that work into a shippable, *fun* accessibility feature:
an **in-app theme designer** with curated Perch-flavoured presets (some very slightly red/pink dark
themes) and a live WCAG contrast readout, so the colour system is demonstrably built with
accessibility in mind.

Reference points the user likes: **GitKraken Desktop** (calm, low-saturation dark chrome with a
clear accent) and **VS Code + Monokai** (warm dark background, high text contrast, distinct
semantic hues). The common thread: *desaturated, slightly-warm neutrals + a small number of
high-contrast, meaningful accent hues*, not the flat cool-grey Perch uses now.

---

## 2. Research: how to build a cohesive, accessible colour system

This section is the reasoning the presets and the designer are built on. It is deliberately concrete
so the milestones can lift numbers straight out of it.

### 2.1 Work in a perceptual colour space, not raw RGB

Perch currently hand-picks `Color.FromRgb(...)` triples. That makes "the same colour but one step
lighter" or "the whole theme, but red-tinted" impossible to express — every shade is an independent
guess, which is exactly why we have three slightly-different greys today.

A cohesive palette is generated, not guessed. The tool of choice is **HSL** (cheap, already
expressible with the existing `Color` type) or, better, **OKLCH** (perceptually uniform: equal
lightness steps *look* equal, and changing hue doesn't change perceived brightness). Recommendation:

- Model a theme as **one neutral ramp + a hue** rather than ~40 literals.
- The neutral ramp is a small tonal scale (e.g. 8 steps from near-black surface to near-white text),
  all sharing **one hue and one low chroma** — this is what makes a dark theme read as "one
  material" instead of a pile of greys.
- Derive chrome roles (surface / elevated / border / track / muted-text / text / title) by *picking
  steps off that ramp*, not by inventing colours.

We don't need a full OKLCH engine on day one. A pragmatic middle path: keep authoring presets as hex
(designed once, in an OKLCH-aware external tool), but store the neutral **hue + chroma** so the
*designer* can re-tint a whole theme by rotating one number. (See M4.)

### 2.2 Contrast & WCAG — the hard numbers

The point of the exercise. Targets we hold ourselves to:

| Pair | WCAG 2.x minimum | Perch target |
|---|---|---|
| Body text on its surface | 4.5:1 (AA) | ≥ 5.5:1 |
| Large / bold text (≥ 18.66px bold or 24px) | 3:1 | ≥ 4.5:1 |
| Muted / secondary text | 4.5:1 (it's still text) | ≥ 4.5:1 — *this is where we fail today* |
| UI glyphs, borders, focus rings, status dots | 3:1 (non-text, 1.4.11) | ≥ 3:1 |
| Disabled text | exempt | keep ≥ 2.5:1 for dignity |

Contrast ratio = `(L_light + 0.05) / (L_dark + 0.05)` where `L` is WCAG relative luminance
(linearised sRGB, `0.2126 R + 0.7152 G + 0.0722 B`). This is ~15 lines of C# and becomes the core of
both the automated tests and the designer's live badge.

> **Known current failure:** `Palette.Muted = (140,140,160)` on `FormBg = (24,24,32)` is ≈ **4.0:1**
> — under AA for the body-sized captions it's used for (`FieldCaption`, `BodyText`,
> `ToggleCaption`, the usage-bar footer). The overlay's muted `(110,110,130)` on its darker bg
> `(15,15,20)` is worse, ≈ **3.4:1**. Fixing muted-text contrast is the single highest-impact change
> and should land even before the theme system (M0).

A note on **APCA** (the WCAG 3 draft algorithm): it models dark-mode contrast far better than the 2.x
ratio (which is known to over-reward light-on-dark). Worth *showing* APCA Lc alongside the 2.x ratio
in the designer as an advisory, but 2.x remains the pass/fail gate we ship against (it's the legal
standard).

### 2.3 Dark-theme craft (why Monokai/GitKraken feel good)

1. **Never pure black.** `#000` makes text vibrate (halation) and kills elevation. Use a dark but
   non-zero surface (Monokai `#272822`, GitKraken ~`#1a1d21`). Perch's `#0f0f14` overlay is close;
   the `#181820` settings bg is fine.
2. **Elevation via lightness, not shadow.** Cards/popovers/nav get a *lighter* surface step, not a
   drop shadow. Perch already does this (`ButtonBg` lighter than `FormBg`) but inconsistently — the
   nav rail is *darker* than the body, which is backwards for a raised element.
3. **Desaturate on dark.** Saturated fills glow unpleasantly on a dark field. Both references pull
   chroma out of chrome and reserve saturation for a *few* semantic accents. Perch's status ramp
   (green/yellow/orange/red at full Tailwind-500 saturation) is a touch hot on `#0f0f14`; nudge
   lightness up and chroma down a hair.
4. **Tint the neutrals.** A completely neutral grey looks digital/cold. Both references warm their
   greys slightly (Monokai warm/olive, GitKraken cool-neutral). **This is our Perch-flavour lever**
   (§2.4).
5. **One accent, used sparingly.** GitKraken = one purple/teal; Monokai = the yellow/green/pink for
   syntax only. Perch has a clear brand accent already (`#ff442d`) — currently under-used for chrome
   and only wired to update affordances.

### 2.4 "Perch-flavoured" — tinting the neutrals toward the brand

The brand red-orange is `#ff442d` ≈ **hue 9°**. Pink lives ≈ **hue 330–345°**. To make a dark theme
feel like Perch without shouting, we **shift the neutral ramp's hue toward the brand and add a whisp
of chroma** — the surfaces read as "warm charcoal with a red undertone" rather than "blue-grey".

Concretely, today's `FormBg (24,24,32)` is actually *cool* (blue-biased, hue ≈ 240°). Warming it:

- **Ember (red-tinted charcoal):** base `#17110F` → elevated `#211815` → border `#38251F`, i.e. the
  ramp rotated to ~15° at low chroma. Neutral text stays near-white but can carry a 2–3% warm bias.
- **Blush (pink-tinted charcoal):** base `#16101300`… base `#171015` → elevated `#211722` →
  border `#3A2635`, ramp rotated to ~330°.

**Design rule that keeps it legible:** the theme tints **neutrals, chrome and the primary accent
only**. The **semantic status hues stay put** — running is always green, waiting always yellow,
error always red, sub-agent always purple. Those hues *are* the glanceable meaning of the overlay;
re-tinting them per theme would destroy the app's core read. Presets may nudge a status hue's
lightness/chroma to hold ≥3:1 on a darker base, but not its identity.

### 2.5 Semantic token architecture (the deliverable of the research)

Stop naming colours by appearance (`Muted`, `Border`) and name them by **role**, so a theme is a
table of roles → colours and every surface asks for a role. Proposed role set (maps 1:1 onto what the
code already uses — see §3):

**Surfaces & chrome**
- `surface.base` — window/overlay background (today `FormBg` / overlay `BgColor`)
- `surface.raised` — cards, buttons, nav, badges (today `ButtonBg` / `BadgeBrush`)
- `surface.raisedHover` — hover on the above (today `ButtonHover` / `RowHoverBrush`)
- `surface.overlayScrim` — the translucent panel fill (today overlay `BgBrush` ARGB)
- `surface.track` — usage/metric bar troughs (today `Track`)
- `border` / `separator` — (today `Border` / `SepPen` / `TreeLinePen`)

**Text**
- `text.primary` (today `Fg`), `text.title` (today `Title`), `text.muted` (today `Muted`),
  `text.onAccent`, `text.disabled` (derived via `Blend`)

**Accent**
- `accent` / `accentHover` (today `Accent` / `AccentHover`), `brand` (today `Brand`),
  `focusRing` (new — needed for keyboard-focus visibility, currently absent)

**Semantic status** (theme-stable identity, tunable lightness)
- `status.running` (green), `status.attention` (orange), `status.awaiting` (yellow),
  `status.idle` (slate), `status.error` (red), `status.warn` (amber), `status.subAgent` (purple),
  `status.remote`/`nav` (blue), `status.mail` (teal), `status.burn` (blue)

**Functional ramps** (already methods, keep them but source from the theme)
- `mode(PermissionMode)`, `usage(pct)`, `team(name)`, `blend(a,b,t)`

This role list is intentionally the union of `Palette.cs` **and** the `OverlayCanvas` constant block,
so unifying them (M1) is a mechanical "replace literal → role lookup".

---

## 3. Current state (why this is real work)

From a full read of the codebase. The colour system is **fractured across at least three
uncoordinated copies plus per-window locals**:

1. **`Theming/Palette.cs`** — `public static class` of `static readonly Color` fields + cached
   `static readonly IBrush`es. Frozen at compile time; a runtime swap is impossible without reworking
   this into instance state. Powers the settings surface and every window's *functional* colours.
2. **`Views/OverlayCanvas.cs:73–162`** (+ ~inline literals to ~2496) — the overlay's **own** private
   palette block. Deliberately *different* values: bg `#0f0f14` (vs Palette `#181820`), muted
   `(110,110,130)` (vs `(140,140,160)`), its own separators/status/feature brushes. It only reuses
   `Palette` for the four functional mappers (`ModeColor`/`UsageColor`/`TeamColor`/`Blend`).
3. **`App.axaml` `Application.Resources`** — a third copy of the chrome brushes as XAML keys
   (`FormBgBrush`, `FgBrush`, …) "kept in sync with Palette.cs by hand".
4. **~10 windows with private colour fields** — `SettingsWindow.NavBg (18,18,24)`,
   `Achievements/Changelog/Daemon/Qr/Wrapped/SessionSwitcher/Stats/FlightPath/History/GitReview`
   each redefine near-duplicate `Bg/Stroke/Muted` constants; `StickyNoteWindow` is a whole separate
   light "paper" theme.

**~295 hard-coded colour literals across 33 files.** `App.axaml:4` hard-forces `RequestedThemeVariant="Dark"`.
**No** theme/colour/contrast setting exists in `AppSettings` or `SettingsRegistry` (which has a
build-enforced coverage test — a new setting *must* get a descriptor). **No** contrast logic,
focus-ring styling, high-contrast mode, or OS-theme following anywhere.

Implication: a "theme system" is 20% new feature and 80% **consolidating the palette into one runtime
source of truth**. The milestones front-load that so the designer has something real to drive.

---

## 4. Target architecture

- **`Theme` (record/class in `Perch.Core`)** — a table of the §2.5 roles → `Color`, plus the neutral
  `hue`/`chroma` metadata used to re-tint. Pure data, UI-free, lives beside `AppSettings` so
  `Perch.Core` and all heads can see it. Serializable (custom themes persist as JSON).
- **`ThemePalette` façade (`Perch.App/Theming`)** — replaces today's `static Palette`. Exposes the
  same member *names* (`FormBg`, `Fg`, `AccentBrush`, `ModeColor(...)`, …) but as properties reading
  the **active theme**, and a cached brush set that's rebuilt on theme change. This keeps the ~200
  existing `Palette.X` call-sites compiling with near-zero churn — the swap is `static field` →
  `static property backed by the active theme`.
- **`ThemeService`** — holds the active `Theme`, raises `ThemeChanged`; `App` subscribes and
  re-invalidates every open window/overlay (reuse the existing `DisplayChanged` fan-out and
  `InvalidateVisual` discipline). Owner-drawn surfaces just repaint; XAML-bound windows swap the
  `Application.Resources` brushes' colours in place.
- **Built-in presets** as static `Theme` instances; **custom themes** as a `List<Theme>` in
  `AppSettings`, with the active theme id a new persisted setting.
- **Contrast utility (`Perch.Core`)** — `WcagContrast(Color, Color)`, `RelativeLuminance(Color)`,
  `PassesAA(...)`. Unit-tested; drives both the designer badge and a regression test asserting every
  shipped preset passes its own contrast contract.

---

## 5. Candidate presets to ship

Authored in an OKLCH tool, verified against §2.2, then hard-coded as `Theme` instances. Starting set
(final hex tuned in M3 against the contrast tests — these are the design intent):

| Preset | Feel | Neutral hue | Notes |
|---|---|---|---|
| **Midnight** (default / current-ish) | today's cool charcoal, *contrast-fixed* | ~240° | keeps existing look but lifts muted text to AA; the safe default |
| **Perch Ember** | warm red-tinted charcoal | ~15° | the headline Perch-flavour dark; brand accent feels native |
| **Perch Blush** | soft pink-tinted charcoal | ~330° | the "very slightly pink" ask; gentle, low-chroma |
| **Dim** (Monokai-inspired) | warm olive-brown dark, high text contrast | ~60° | for the VS Code crowd |
| **High Contrast Dark** | near-black surface, ≥7:1 everywhere (AAA-ish) | 0° neutral | the accessibility flagship; thicker borders/focus ring |
| **Daylight** (stretch) | light theme | ~15° warm | proves the token system isn't dark-only; ships only if M1 fully de-hardcodes |

Each preset is a full §2.5 role table. Status hues are shared/near-shared across presets by design
(§2.4).

---

## 6. Milestones

Each milestone is independently shippable and leaves the app working.

### M0 — Contrast quick-wins + contrast utility *(no theming yet)*
The cheapest, highest-impact eye-strain fix, decoupled from the big refactor.
- Add `Perch.Core/Theming/Contrast.cs` (`RelativeLuminance`, `WcagContrast`, `PassesAA/AAA`) + xUnit
  tests.
- Lift failing muted/secondary text to ≥4.5:1 in **both** `Palette.Muted` and the overlay's muted —
  and audit `Title`/`Border`/status dots for the 3:1 non-text floor. (Pure value edits; no new
  surface.)
- Add a `ContrastTests` fixture asserting the *current* palette's text roles pass AA, so we can't
  regress.
- **Exit:** measurably better legibility today; the contrast tooling every later milestone needs.

### M1 — One source of truth: the `Theme` token model + `ThemePalette` façade
- Define `Theme` (roles from §2.5) + `Themes.Midnight` seeded from **today's** (M0-fixed) values, so
  this milestone is a pure refactor with *zero visual change*.
- Convert `static Palette` → `ThemePalette` reading the active theme; keep every existing member name.
- Fold the `OverlayCanvas` constant block and the per-window local constants onto theme roles
  (mechanical literal→role sweep; the render harness diff proves no pixels moved).
- Make `App.axaml` brushes get their colours from the theme at startup instead of duplicated literals.
- **Exit:** every colour in the app resolves through one `Theme`; render-harness output byte-identical
  to M0. This is the big one.

### M2 — Runtime switching + persistence
- `ThemeService` + `ThemeChanged`; `App` re-invalidates all surfaces on change (reuse `DisplayChanged`
  plumbing).
- `AppSettings.ActiveThemeId` (string) + `AppSettings.CustomThemes` (`List<Theme>`); `SettingDescriptor`
  for the theme picker (satisfies the coverage test). Serialize `Theme` through the existing JSON path.
- Ship **Midnight + one alternate** (Ember) selectable from a new **Appearance** page in
  `SettingsWindow` (`AddPage(nav, "appearance", "Appearance", …)`), with the docked `PreviewPane`
  re-used to show the overlay under the chosen theme live.
- **Exit:** user can switch between two themes and it persists across restart.

### M3 — The preset library
- Author + tune all §5 presets against the contrast tests (a preset fails the build if it violates its
  own contract). Add a `PresetContrastTests` parametrised over every preset × text role.
- Present them as a gallery of swatch cards on the Appearance page (each card is a mini live preview,
  reusing the `PreviewPane` / `HeadlessRenderer` approach).
- **Exit:** 4–5 curated, accessible presets shippable; screenshots via `render` mode for the changelog.

### M4 — The theme designer *(the fun accessibility feature)*
- A designer surface (dedicated `ThemeDesignerWindow`, or an expandable section on the Appearance
  page) that clones a preset into an editable custom `Theme`:
  - **Neutral tint control** — one hue + chroma slider that re-tints the whole neutral ramp (the §2.1
    payoff): "make my theme 8% more Perch-red" is one drag.
  - **Accent picker** + a few role overrides (surface, text, accent, focus ring).
  - **Live preview** — the real `OverlayCanvas` + a strip of settings controls repaint on every edit
    (existing off-thread → `Dispatcher.Post` pattern; the preview canvas is already inert/hit-test-off).
  - **Live WCAG readout** — every text-on-surface pair shows its ratio + an AA/AAA/FAIL chip
    (`Contrast` util from M0); an advisory APCA Lc alongside. A **"nudge to pass"** button that walks a
    failing pair's lightness until it clears AA — this is the "accessibility is baked in" demo.
- Save as a named custom theme (into `AppSettings.CustomThemes`); appears in the gallery.
- **Exit:** a user can design an accessible custom theme and the tool *proves* it's accessible.

### M5 — Accessibility polish & sharing
- **Focus ring** role wired through interactive controls (keyboard nav is invisible today).
- **Colour-blindness preview** — protanopia/deuteranopia/tritanopia simulation toggle on the preview
  (matrix transform in the render path), so status hues can be checked for confusability.
- **Import/export** a theme as JSON (and, fittingly for Perch, a **QR share** reusing `QrWindow`).
- Optional: **follow OS theme / high-contrast** — detect Windows high-contrast + light/dark and pick a
  matching preset (needs a `Perch.Core` interface + `Perch.Platform.Windows` impl per the platform
  rule; the hard-forced dark in `App.axaml` gets relaxed here).
- **Exit:** the feature reads as a genuine accessibility tool, not just a re-skin.

### M6 — Hardening, verification, docs
- `render <outDir>` sweep at 1× and 1.5× for **every** preset (the standing UI-verification path); eyeball
  glyph clipping, muted-text legibility, focus rings.
- `bump-version` + `CHANGELOG.md` entry (dry humour, per house style).
- Update `CLAUDE.md`'s theming conventions (the "use the shared `Palette`" note becomes "use the active
  `Theme` roles"); short `docs/` note on adding a preset.
- **Exit:** shipped, tested, documented.

---

## 7. Risks & open decisions

- **Scope of M1 (the refactor).** Touching ~33 files / ~295 literals is the risk centre. Mitigation:
  seed Midnight from exact current values so M1 is provably no-op via the render harness; land it in
  reviewable slices (overlay block, then windows, then App.axaml).
- **Light theme.** Truly de-hardcoding dark (`App.axaml:4`, `StickyNoteWindow`'s paper theme, any
  literal that assumed a dark bg) is more than a palette swap. **Recommendation: dark-first.** Ship all
  dark presets on the token system; treat *Daylight* + OS-follow as a stretch (M5) gated on M1 being
  truly complete.
- **OKLCH vs HSL at runtime.** Full OKLCH re-tinting is nicer but adds a colour-space lib. **Recommendation:**
  author presets in OKLCH offline; do the *live* designer re-tint in HSL (good enough, zero deps), and
  revisit OKLCH only if the tint quality disappoints.
- **Status-hue identity.** Confirm the §2.4 rule (themes don't restyle semantic status hues) — it's a
  deliberate constraint that protects the overlay's glanceability. A "chaos mode" that *does* recolour
  everything could be a Whimsy-surface toggle later, but not the default.
- **`Theme` in `Perch.Core`.** Colours there means either taking an `Avalonia.Media.Color` dependency
  into Core (currently UI-free) or storing ARGB `uint`/`(byte,byte,byte)` and converting at the UI
  edge. **Recommendation:** store raw ARGB in Core, convert in `ThemePalette` — keeps Core UI-free per
  the project's platform rule.

## 8. Testing strategy

- **Unit:** `Contrast` math; every preset × text-role passes its AA contract; `Theme` JSON round-trips;
  `SettingsRegistry` coverage test stays green with the new setting.
- **Visual:** `HeadlessRenderer` / `render` sweep per preset — the primary UI check (no automated UI
  coverage otherwise). M1's no-op is proven by a byte-diff against pre-refactor output.
- **Manual:** run the tray, switch themes, design one, verify persistence + live repaint of overlay and
  open windows.
