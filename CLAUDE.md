# Perch

A Windows system-tray app (.NET 10 / [Avalonia UI](https://avaloniaui.net/), C#) that monitors active
Claude Code sessions and surfaces their status as desktop overlays, notifications, and stats. The UI is
Avalonia (owner-drawn overlay + dashboards, XAML-ish code-built windows); the codebase is structured so
macOS/Linux heads can follow — a macOS port is in progress (see `docs/macos-port-plan.md`).

## Layout

Multi-project solution (`perch.slnx`); the projects live under `src/`:

- `src/Perch.Core/` — the UI-free core (plain `net10.0`). The `~/.claude` data layer (`Data/`: file readers,
  parsers, models, `AppSettings`, stats/flight/history services, `NotificationService`, `PluginManager`,
  `AppInfo`) plus the platform-service **interfaces** the UI resolves (`Platform/`: `INotifier`,
  `IWindowActivator`, `IPathInstaller`, `IAudioCue`, `IAppIconProvider`, `ISystemMetrics`, `ISessionLock`,
  `IGlobalHotkey`, `IMicrophoneMonitor`, …). No UI, no `System.Drawing`.
- `src/Perch.Platform.Windows/` — Win32 implementations of the Core interfaces (`net10.0-windows`).
- `src/Perch.Platform.Mac/` — macOS implementations of the same Core interfaces (plain `net10.0`, reaching
  AppKit/libSystem via P/Invoke — no `net10.0-macos` workload, so it builds on any host). Currently no-op
  stubs being filled in per the port plan.
- `src/Perch.App/` — the app head (assembly/exe name **`perch`**). `Program` (Velopack bootstrap +
  single-instance), `App.axaml(.cs)` (the tray/overlay/window wiring shell — the counterpart of the old
  `OverlayApplicationContext`), `PlatformServices` (composition root), `Views/` (owner-drawn controls:
  `OverlayCanvas`, `StatsDashboard`, `FlightPathTimeline`, …), `Windows/` (Settings, History, Stats, QR,
  etc.), `Rendering/` (`OverlayDraw` mini-PaintKit, `HeadlessRenderer`), `Services/` (`*MonitorHost`,
  `UpdateService`), `Theming/Palette`, `Notifications/`.
- `tests/Perch.Tests/` — xUnit tests over `Perch.Core` (see Testing).
- `src/Perch.Hook/` — `perch-hook`, the small NativeAOT console app Perch wires into Claude Code's
  hooks (self-managed in `~/.claude/settings.json`; see `Perch.Data.ClaudeUserSettings` +
  `Perch.App/Services/HookInstaller`). It writes the session sidecars the tray watches and launches/
  detects the tray by the process name **`perch`**. (This replaced the old marketplace plugin.)
- `tools/IconGen` — regenerates the raster icons from `perch.svg` (`tools/gen-icons.ps1`/`.cmd`), writing
  `src/Perch.App/Assets/icon.{png,ico}` and `landing-icon.png`.
- `install.ps1` — the Windows one-liner installer (`irm …/install.ps1 | iex`), served straight from
  `raw.githubusercontent.com` on `main`. It resolves a release, verifies `Perch-win-Setup.exe` against the
  release's `SHA256SUMS.txt`, and runs it — so every install ends up an ordinary Velopack install that
  self-updates. Tested by `tools/test-install.ps1`; see `docs/distribution-plan.md`.

## Build & run

- Build: `dotnet build perch.slnx` (or just the head: `dotnet build src/Perch.App/Perch.App.csproj`).
  On **Windows** the head multi-targets, so a build compiles **both** heads: the Windows head
  (`net10.0-windows10.0.19041.0`) and the cross-platform head (plain `net10.0`, used for macOS). On
  **macOS/Linux** the csproj drops the Windows TFM (its reference packs need a Windows host), so the head
  builds as the single `net10.0` cross-platform head — plain `dotnet build`/`run`/`test`, no `-f` or flags.
- Run (dev): on Windows `dotnet run --project src/Perch.App -f net10.0-windows10.0.19041.0` — the `-f` is
  required there because the head multi-targets. On macOS just `dotnet run --project src/Perch.App` (only
  the `net10.0` head exists on that host).
- **Headless render (UI verification):** `dotnet run --project src/Perch.App -f net10.0-windows10.0.19041.0 -- render <outDir>`
  (Windows) or `dotnet run --project src/Perch.App -- render <outDir>` (macOS) dumps every owner-drawn
  surface to PNG at 1× and 1.5× via `HeadlessRenderer` — the standing way to eyeball UI changes without a
  display. Use it when touching any owner-drawn control.
- Release artifacts: Velopack (`vpk`) via `publish.bat`, or the `v*`-tag GitHub Actions workflow
  (`.github/workflows/release.yml`) — see `README.md`. Bump `<Version>` in
  `src/Perch.App/Perch.App.csproj`.

`Perch.Core` and `Perch.Platform.Mac` target plain `net10.0`; `Perch.Platform.Windows` targets
`net10.0-windows`; the app head multi-targets `net10.0-windows10.0.19041.0` (real Action Center toasts via
the UWP shim) **and** plain `net10.0` (the macOS/Linux head). `PlatformServices` picks the implementation
set at compile time with `#if WINDOWS`. `Nullable` and `ImplicitUsings` enabled everywhere.

## Testing

Two suites. Run the .NET one with `dotnet test tests/Perch.Tests/Perch.Tests.csproj`, and — after touching
`install.ps1` — `powershell -NoProfile -File tools\test-install.ps1`, which covers the installer's manifest
parsing, its cross-host (`5.1`/`7.x`) response decoding, and its download/verify path against a loopback
`HttpListener` serving a real artifact out of `releases\` (run `publish.bat` once first, or those checks
skip). Nothing in it touches the network, so the live GitHub API lookup stays a manual check.

The .NET suite: It exercises `Perch.Core`,
pointing the data layer at a synthetic `~/.claude` fixture tree via `CLAUDE_CONFIG_DIR` (set in
`TestEnvironment.cs`; fixtures live under `tests/Perch.Tests/fixtures/claude/` and `FixtureCwd` is
`C:\fixtures\proj`). Prefer adding a fixture + an xUnit test alongside the existing `*Tests.cs` files when
changing logic-heavy data-layer code (transcript parsing, stats, detection) — it's faster and more durable
than a throwaway script. The UI has no automated coverage; eyeball it via the `render` mode above or by
running the tray app.

## Conventions & gotchas

- **Owner-drawn text must size its rectangle from the font's line height, never a hard-coded pixel
  value.** When painting text into a bounded region, derive the height from the measured text
  (`OverlayDraw.Text(...).Height`, i.e. `FormattedText.Height`) plus padding — not a magic number. A box
  shorter than the line height clips the **bottoms** of the glyphs, and it's worst for large numbers and
  anything that must survive a DPI change. This has bitten the stat cards in `StatsDashboard` before;
  watch for it in any new card/badge/number rendering. The `OverlayDraw` mini-PaintKit bakes this in — go
  through it.
- **Dashboards are owner-drawn through a single measure-or-paint routine.** e.g. `StatsDashboard.Draw(DrawingContext?, width)`
  returns the content height when the context is null (measure pass) and paints when it isn't. Keep the
  two in one method so the measured height and the painted layout can never drift apart.
- **IO / heavy work runs off the UI thread**, then marshals back: `Task.Run(...)` →
  `Dispatcher.UIThread.Post(...)` (or `ContinueWith(..., TaskScheduler.FromCurrentSynchronizationContext())`).
  Guard the callback against a window that closed mid-flight (`IsVisible` / disposed checks) and swallow
  the resulting exceptions. See the `*MonitorHost` services, `HistoryWindow`, `StatsWindow`, and
  `UpdateService` for the pattern.
- **Colour comes through `Theming.Palette`, but from one of two sources — pick the right one.** Colours in
  `Perch.Core` are kept UI-free as `Rgb`, split by whether a user can change them:
  - **`Perch.Theming.Theme`** — the *tintable* roles a theme actually varies: surfaces/chrome, text,
    `Accent`/`AccentHover`, `FocusRing`. These are what the designer edits and what gets persisted/shared. A
    theme swap (`ThemeService.Apply`) mutates the cached chrome brushes; `Palette.Active` is the active theme.
  - **`Perch.Theming.FixedColors`** — the *theme-independent* palette: brand, danger, the semantic status
    hues (running=green, error=red, …), and the teammate/mode accents. These carry fixed meaning and never
    vary by theme, so they are **not** part of `Theme` and are **never persisted** (`FixedColors.Default` is
    the one source; `Palette.Fixed` exposes it — only the designer's colour-blind preview swaps in a
    simulated copy). This is why the overlay stays glanceable across themes.

  Use `Palette.X` colours and `Palette.XBrush` cached fills — don't hand-code `Color.FromArgb` in new UI, and
  don't read colour off a raw `Theme`/`FixedColors` outside `Palette`. When adding UI that needs a colour:
  reach for an existing accessor first. If nothing fits, add the role to the **correct** home — a *fixed*
  brand/semantic hue goes in `FixedColors` (+ its `Simulate`) and a `Palette` accessor/brush; a *tintable*
  chrome/text role goes in `Theme` + `Themes.Midnight` (presets inherit via `with`) + `ThemeCodec.Roles`
  (append only — order is the share-code wire format) + `CvdSim.Simulate(Theme)` + a `Palette` accessor/brush
  + `Palette.Apply`. New/changed text colours must clear WCAG AA on their surface — `PresetContrastTests`
  gates every built-in theme, and `Theming.Contrast` is the helper. The in-app designer (Settings →
  Appearance) writes custom themes to `AppSettings.CustomThemes`; those deserialise through
  `ThemeJsonConverter`, which seeds absent roles from Midnight (so a future `Theme` role never reads back as
  black on an old file). Eyeball changes with `dotnet run … -- render <dir> [themeId]`.
- **Data sources are files under `~/.claude/`**, read best-effort:
  - Live session state: `~/.claude/sessions/{sessionId}.json` plus sidecars (`.mode`, `.notify`, `.history`).
  - Transcripts: `~/.claude/projects/{enc-cwd}/{sessionId}.jsonl` (append-only, one JSON record per line,
    each with a `timestamp`; assistant records carry `message.usage` and `message.model`).
  Open with `FileShare.ReadWrite` (files are written live) and tolerate malformed/partial trailing lines —
  parse defensively and never throw out of a scan.
- **Single reused window instances.** Settings / history / stats / flight windows are created lazily and
  reused via `WindowHost.ShowOrFocus`; they're closed together in `App` (Exit / update flow via
  `CloseAuxWindows`). Wire any new top-level window into that idiom.
- **Settings are registry-driven — add a `SettingDescriptor`, not another page.** Every user-facing setting
  is described once in `Perch.Core/Data/SettingsRegistry` (id, name, keywords, surface, kind, the
  `AppSettings` property it backs, and a `PreviewTarget`). That one entry powers the **Search** page, the
  surface **Features** catalogue (`SettingsCatalogView`), and the docked **live preview** — a real
  `OverlayCanvas` (`Views/PreviewPane`) seeded from `Rendering/SampleData` and re-gated through the shared
  `Services/OverlaySettingsGates.Apply(canvas, settings)`, the same helper the live overlay uses. When you
  add a setting: add the `AppSettings` property **and** a registry descriptor (a coverage test,
  `SettingsRegistryTests`, fails the build otherwise); if it drives an overlay glyph, add a canvas `Set*`
  gate + a line in `OverlaySettingsGates` and a `PreviewTarget`; if it needs live activation beyond the
  idempotent `DisplayChanged` (a poll/sampler), extend `SettingsLiveApply`. The pre-registry per-topic
  pages (Indicators, Monitoring, Usage, …) are retired; a handful of pages with unique actions/editors
  remain (Stats, Notifications, Quick Links, Export, About, Changelog, …).
- **Overlay placement is corner-relative and persisted.** The floating panel's and the dense strip's
  *initial* positions are stored in `AppSettings.FloatingPlacement` / `DensePlacement` as an
  `OverlayPlacement` (UI-free `Perch.Core`): a nearest-corner anchor (`HAnchor`/`VAnchor`) + DIP offsets +
  the target monitor's physical bounds. Null means "use the computed default" (floating → primary
  top-right; dense → right edge). All the geometry lives in `Perch.Core/Data/PlacementMath` (pure, tested)
  so heads share it; `OverlayCanvas.PlaceAtInitialFloating` / `DenseController` consume it at launch and
  `ApplyPlacementsLive` on edit. Users set it by dragging a preview in `Windows/PlacementEditorWindow`,
  opened from the overlay header's right-click menu or the "Initial overlay placement" setting. A manual
  drag of the overlay is deliberately **not** persisted to disk — the editor is the sole *on-disk* source of
  truth. It **is**, however, remembered at runtime: `OverlayCanvas._effectiveFloating` (and the dense twin
  `_denseVAnchor`/`_denseVOffsetDip`) capture wherever the overlay currently sits, corner-relative, after
  each placement and each drag, so a **display change re-derives the same distance from the borders** rather
  than clamping the window toward the centre (the fix for large→small→large monitor drift). The signal is
  Avalonia's `Screens.Changed` → `OverlayCanvas.OnScreensChanged` → `RestoreOrEnsureFloating` /
  `DenseController.OnScreensChanged`; the monitor is resolved by exact bounds, else most-overlap
  (`PlacementMath.PickMostOverlapping`, survives a resolution change), else primary. **Screen-change
  re-heals must never write back into that memory** — a clamp-to-a-shrunken-screen would poison it. See
  `docs/initial-placement-plan.md`.
- **Don't assume a Velopack install.** Perch also ships as Velopack's portable zip, extracted wherever the
  user likes. `Services/InstallChannel` classifies the running copy (`Setup` / `Portable` / `Unpackaged`);
  anything that *writes to the install dir or applies an update* must gate on `InstallChannel.SelfUpdates`.
  Checking the update feed works on every channel, so only the apply step needs the gate. (`install.ps1`
  produces a plain `Setup` install — it only downloads, verifies and runs the Velopack installer, so it adds
  no channel of its own.)
- **Release-pipeline scripts stay pure ASCII — use plain hyphens, never em dashes.** `install.ps1`,
  `publish.bat`, `publish-mac.sh`, `tools/gen-ic*`, `release.yml`. The `.ps1` files are the ones that can
  actually break: they ship with no BOM, so Windows PowerShell 5.1 decodes them as the system codepage, and a
  UTF-8 em dash becomes three chars ending in `0x94` = U+201D — a curly quote, which PowerShell honours as a
  *string delimiter*, silently mis-parsing everything after it. Holding the whole pipeline to ASCII is one
  rule instead of three. Shell scripts must also stay LF (a CRLF shebang fails on macOS).
- **Never wait on the installer's process tree.** `Start-Process -Wait` waits for descendants, so it hangs
  forever on the tray app Velopack's Setup launches — wait on the Setup process's own handle instead
  (`[Diagnostics.Process]::Start(...)` + `WaitForExit`). `tools/test-install.ps1` guards all of the above.
- **Every OS-specific capability goes behind a `Perch.Core` interface** with a `Perch.Platform.Windows`
  implementation, resolved through `PlatformServices`. Don't call Win32 (or reference the concrete types)
  from UI code — add/extend an interface so a future macOS/Linux head can implement it.
