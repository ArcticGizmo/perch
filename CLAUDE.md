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
  `IGlobalHotkey`, …). No UI, no `System.Drawing`.
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
- `plugins/perch/` — the companion Claude Code plugin (`commands/`, `hooks/`, `scripts/`). Its hooks
  launch/detect the tray by the process name **`perch`**.
- `tools/IconGen` — regenerates the raster icons from `perch.svg` (`tools/gen-icons.ps1`/`.cmd`), writing
  `src/Perch.App/Assets/icon.{png,ico}` and `landing-icon.png`.

## Build & run

- Build: `dotnet build perch.slnx` (or just the head: `dotnet build src/Perch.App/Perch.App.csproj`).
  The head multi-targets, so a build compiles **both** heads: the Windows head
  (`net10.0-windows10.0.19041.0`) and the cross-platform head (plain `net10.0`, used for macOS).
- Run (dev): `dotnet run --project src/Perch.App -f net10.0-windows10.0.19041.0` — the `-f` is required
  now that the head multi-targets (Windows is the head to run on Windows).
- **Headless render (UI verification):** `dotnet run --project src/Perch.App -f net10.0-windows10.0.19041.0 -- render <outDir>` dumps
  every owner-drawn surface to PNG at 1× and 1.5× via `HeadlessRenderer` — the standing way to eyeball UI
  changes without a display. Use it when touching any owner-drawn control.
- Release artifacts: Velopack (`vpk`) via `publish.bat`, or the `v*`-tag GitHub Actions workflow
  (`.github/workflows/release.yml`) — see `README.md`. Bump `<Version>` in
  `src/Perch.App/Perch.App.csproj`.

`Perch.Core` and `Perch.Platform.Mac` target plain `net10.0`; `Perch.Platform.Windows` targets
`net10.0-windows`; the app head multi-targets `net10.0-windows10.0.19041.0` (real Action Center toasts via
the UWP shim) **and** plain `net10.0` (the macOS/Linux head). `PlatformServices` picks the implementation
set at compile time with `#if WINDOWS`. `Nullable` and `ImplicitUsings` enabled everywhere.

## Testing

Run the test project with `dotnet test tests/Perch.Tests/Perch.Tests.csproj`. It exercises `Perch.Core`,
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
- **Use the shared `Theming.Palette`** for colours (and the overlay's own palette constants in
  `OverlayCanvas`) — don't hand-code `Color.FromArgb` in new UI; the overlay, settings, history, and stats
  windows are meant to read as one app.
- **Data sources are files under `~/.claude/`**, read best-effort:
  - Live session state: `~/.claude/sessions/{sessionId}.json` plus sidecars (`.mode`, `.notify`, `.history`).
  - Transcripts: `~/.claude/projects/{enc-cwd}/{sessionId}.jsonl` (append-only, one JSON record per line,
    each with a `timestamp`; assistant records carry `message.usage` and `message.model`).
  Open with `FileShare.ReadWrite` (files are written live) and tolerate malformed/partial trailing lines —
  parse defensively and never throw out of a scan.
- **Single reused window instances.** Settings / history / stats / flight windows are created lazily and
  reused via `WindowHost.ShowOrFocus`; they're closed together in `App` (Exit / update flow via
  `CloseAuxWindows`). Wire any new top-level window into that idiom.
- **Every OS-specific capability goes behind a `Perch.Core` interface** with a `Perch.Platform.Windows`
  implementation, resolved through `PlatformServices`. Don't call Win32 (or reference the concrete types)
  from UI code — add/extend an interface so a future macOS/Linux head can implement it.
