# macOS integration plan

Now that the app is Avalonia end-to-end, a macOS head is a matter of (a) closing the last few places UI
code still reaches straight into `Perch.Platform.Windows`, (b) making the app head buildable for a
non-Windows TFM, and (c) writing a `Perch.Platform.Mac` sibling that implements the same Core interfaces.
The data layer and the entire UI are already portable — the work is almost all at the platform seam and
in packaging.

## Where we stand (what's already portable)

- **`Perch.Core`** targets plain `net10.0`, no `System.Drawing`, no WinForms. The `~/.claude` data layer
  (readers, parsers, models, stats/flight/history, `NotificationService`, `PluginManager`) is all
  file-based over `~/.claude` and runs anywhere. `Process.GetProcessById(...)` (session liveness) is
  cross-platform. `MetricsMonitor` holds no P/Invoke — the raw OS reads already live behind
  `ISystemMetrics`.
- **The UI** is Avalonia owner-drawn: overlay, dashboards, settings/history/stats/flight windows, the
  `render` headless-dump mode. None of it is Windows-bound.
- **`AvaloniaToastNotifier`** is an owner-drawn toast, already the off-Windows `INotifier` fallback — so
  notifications work on macOS on day one without a native implementation.
- **Every OS capability already has a Core interface** (`INotifier`, `IWindowActivator`, `IPathInstaller`,
  `IAudioCue`, `IAppIconProvider`, `ISystemMetrics`, `ISessionLock`, `IGlobalHotkey`) resolved through
  `PlatformServices`, whose own comment already anticipates "Phase 7 will choose the implementation per OS."

## The blockers (Windows-specific surface)

1. **Abstraction leak — `OverlayNativeChrome`.** UI code calls
   `Perch.Platform.Windows.OverlayNativeChrome` *directly* in five places (`LiveOverlayWindow`,
   `GlowWindow`, `ConfettiWindow`, `DenseDropZoneWindow`, `AvaloniaToastNotifier.ToastHostWindow`) to set
   tool-window / no-activate / click-through window styles. This is the only spot UI reaches past the
   seams, and it won't compile against a mac head. It needs a Core interface.
2. **App-head TFM.** `Perch.App` is `net10.0-windows10.0.19041.0`, `<OutputType>WinExe</OutputType>`, with
   an `app.manifest`, an `.ico`, a hard `ProjectReference` to `Perch.Platform.Windows`, a
   `Microsoft.Toolkit.Uwp.Notifications` package, and `BuiltInComInteropSupport`. All of that must become
   conditional so a macOS TFM can build.
3. **`Perch.Platform.Windows` has no sibling.** Eight interfaces need a macOS implementation (several can
   ship as graceful no-ops first).
4. **The plugin is PowerShell-only.** `plugins/perch/scripts/invoke.ps1` + `hooks.json` invoke
   `powershell.exe`, `Get-Process perch`, `$env:APPDATA\Perch\settings.json`, `Start-Process perch`. macOS
   Claude Code needs a shell sibling and per-OS hook dispatch.
5. **Settings-path divergence.** `AppSettings` writes to
   `Environment.SpecialFolder.ApplicationData` + `Perch/settings.json`. On macOS .NET maps that to
   **`~/.config/Perch/settings.json`** (XDG), *not* `~/Library/Application Support`. The mac hook must
   read the same path the app writes — this is a hard coupling to get right (see Phase 4).
6. **Packaging.** `publish.bat` / `.github/workflows/release.yml` are Velopack-Windows only. macOS needs a
   `.app` bundle, `Info.plist`, `.icns`, Developer-ID signing + notarization, and a DMG.

## Phased plan

### Phase 0 — Spike: prove Avalonia + Core run on macOS
De-risk before investing. On a Mac, build `Perch.Core` (already `net10.0`) and run the Avalonia UI with a
throwaway all-no-op platform layer (or the `render` mode, which needs no platform services). Confirm the
overlay renders, `~/.claude` file-watching fires, and DPI/Retina scaling looks right via `render <out>` at
1× and 1.5×. Small, fast, tells us whether anything in Avalonia 12 misbehaves on macOS before we commit.

### Phase 1 — Close the `OverlayNativeChrome` leak (do this regardless)
Introduce a Core seam, e.g. `IWindowChrome` with `MakeToolWindowNoActivate(...)` and
`MakeClickThroughNoActivate(...)` taking the neutral window handle (the five call sites already have an
Avalonia `Window` + `TryGetPlatformHandle()`). Move the Win32 body into a `WindowChrome : IWindowChrome`
in `Perch.Platform.Windows`, register it in `PlatformServices`, and replace the five
`OperatingSystem.IsWindows() && ... OverlayNativeChrome.X(h.Handle)` sites with a call through the seam
(the seam itself no-ops off its platform). This is pure refactor, testable on Windows today, and removes
the last direct dependency of UI code on `Perch.Platform.Windows`.

### Phase 2 — Make the head build per-OS
Multi-target the existing `Perch.App` rather than forking a second head project (fewer moving parts; the
composition root already switches on OS):
- `<TargetFrameworks>net10.0-windows10.0.19041.0;net10.0-macos</TargetFrameworks>`.
- Condition the Windows-only bits to the windows TFM: `OutputType=WinExe`, `ApplicationManifest`,
  `ApplicationIcon`, `BuiltInComInteropSupport`, the `Microsoft.Toolkit.Uwp.Notifications` package, and
  the `Perch.Platform.Windows` project reference. Add `Perch.Platform.Mac` under the macos TFM.
- Teach `PlatformServices` to pick the implementation set by `OperatingSystem.Is…()` (Windows impls vs
  Mac impls vs no-op defaults).
- Fix the portable-mutex name (drop the `Local\` prefix on non-Windows) and confirm single-instance
  behaviour on macOS.
- Keep `WindowsToastNotifier` behind its existing `OperatingSystem.IsWindows()` gate; macOS falls to
  `AvaloniaToastNotifier`.

At the end of Phase 2 the app should launch on macOS against no-op platform services — overlay + toasts +
detection working, everything else degrading quietly.

### Phase 3 — Implement `Perch.Platform.Mac`
New `net10.0-macos` project, one interface at a time, easiest first so the app is usable early:
- **`IAudioCue`** — `NSSound`/system sounds (or `afplay`). Trivial.
- **`ISessionLock`** — screen-lock state via `CGSessionCopyCurrentDictionary`
  (`kCGSSessionOnConsoleKey`) or the `com.apple.screenIsLocked`/`Unlocked` distributed notifications.
- **`ISystemMetrics`** — CPU via `host_statistics(HOST_CPU_LOAD_INFO)`, memory via
  `host_statistics64`/`sysctl`, parent-pid map via `libproc` (`proc_listpids` + `proc_pidinfo`). Returns
  the same tuples the Windows impl does, so `MetricsMonitor`'s delta maths is unchanged.
- **`IWindowChrome`** (from Phase 1) — set `NSWindow` level / `collectionBehavior` (keep out of
  Mission Control / Spaces cycling) and `ignoresMouseEvents` (click-through) on the handle.
- **`IAppIconProvider`** — `NSWorkspace.iconForFile`/`iconForContentType`, materialised to a PNG cache
  (same on-disk contract); launch via `NSWorkspace.launchApplication` / `open -a`.
- **`IWindowActivator`** — hardest. Raise the session's terminal (Terminal.app / iTerm2 / VS Code
  integrated terminal). Walk parent pids via `libproc`, then raise the owning app via Accessibility API
  (`AXUIElement`) or scoped AppleScript. Expect this to be best-effort per terminal, like the Windows
  ConPTY caveat. **Needs the Accessibility permission** (Phase 6).
- **`IGlobalHotkey`** — Carbon `RegisterEventHotKey` (works without focus) or an `NSEvent` global
  monitor. **Needs Accessibility/Input-Monitoring permission** for the monitor route.
- **`IPathInstaller`** — symlink the launcher into `/usr/local/bin` (or `~/.local/bin`) so `perch`
  resolves in any shell; undo on uninstall. No `SendMessageTimeout` equivalent needed.
- **`INotifier` (optional native)** — `UNUserNotificationCenter` for real Notification Center toasts;
  skip initially since `AvaloniaToastNotifier` already covers it.

Ship the harder ones (activator, hotkey) as no-ops first if needed — the app stays fully usable without
them.

### Phase 4 — Port the plugin to macOS
- Add `plugins/perch/scripts/invoke.sh` mirroring `invoke.ps1`: write the same sidecars
  (`{sid}.mode/.notify/.history`, `agent-*.stopped/.idle`), same `/afk` + `/history` prompt handling,
  same JSON-on-stdin protocol.
- Make `hooks.json` dispatch per-OS (a small cross-platform launcher, or OS-conditional command entries)
  so Windows keeps `powershell.exe` and macOS runs `sh`/`bash`.
- **Match the settings path exactly:** the `start` and `/afk`/`/history` branches read/gate on the app's
  settings file, which on macOS is `~/.config/Perch/settings.json` (Phase 1 finding), not
  `~/Library/Application Support`. Get this wrong and auto-start/`/afk` silently no-op.
- Replace `Get-Process perch` (running check) with `pgrep -x perch`, and `Start-Process perch` with the
  launcher on PATH (or `open -a Perch --args --autostarted`). Confirm the process name the checks look
  for matches the bundle's executable name.

### Phase 5 — Packaging & distribution
- Velopack macOS lane: `vpk pack` for `osx-arm64` (and `osx-x64` if we support Intel) producing a `.app`
  bundle + DMG. Author `Info.plist` (bundle id, `LSUIElement=true` so it's a menu-bar/agent app with no
  Dock icon), and generate `.icns` from `perch.svg` (extend `tools/gen-icons.ps1`, or add a mac path).
- **Apple Developer ID signing + notarization** — required or Gatekeeper blocks the app. This gates public
  distribution and needs an Apple Developer account (see open questions).
- Extend `.github/workflows/release.yml` with a `macos-latest` job on the `v*` tag; add a `publish`
  script sibling for local builds.

### Phase 6 — Permissions, polish, docs
- First-run UX to request **Accessibility** (window activation, global hotkey) and **Notifications**;
  degrade gracefully when denied (the seams already tolerate refusal).
- Menu-bar item (`TrayIcon`) behaviour + `LSUIElement` verified; left-click / context-menu parity.
- Retina/scaling pass via `render` at multiple scales; check the owner-drawn text-height rule (CLAUDE.md)
  holds on macOS font metrics.
- Update `README.md`, `CLAUDE.md` layout section, and `tools/` docs for the new `Perch.Platform.Mac`
  project and mac build/run/package commands.

## Suggested sequencing

Phase 1 is worth doing now on its own (it's a Windows-safe refactor and the one true architectural gap).
Phases 0→2 are a short runway to "it launches on a Mac." Phase 3 is the bulk of the engineering and can
land incrementally (no-op → real per interface). Phase 4 unlocks real end-to-end session detection driven
by the plugin. Phase 5 is a separable track (mostly Apple/CI, not code) that can proceed in parallel once
there's something to package.

## Decisions (settled)

- **Head strategy:** multi-target the one `Perch.App` (no separate `Perch.App.Mac`). Phase 2 stands as
  written.
- **First-release scope:** **full parity** — window activation, global hotkey, and quick-links must all
  work before shipping. So the "ship as no-ops first" latitude in Phase 3 is only an *interim* step
  during development, not an acceptable release state: `IWindowActivator` and `IGlobalHotkey` are
  release-blocking and need their real implementations (and the Accessibility permission flow) done.
- **Architectures:** both `osx-arm64` and `osx-x64` (universal binary or two lanes). Phase 5 packaging and
  the CI job cover both.
- **Apple signing:** no Developer account yet. Phase 5 targets **unsigned local builds** for now;
  Developer-ID signing + notarization is a deferred follow-up track (public distribution waits on it).
  Document the Gatekeeper right-click-open workaround for unsigned builds in the meantime.

## Still open

- **Notifications:** owner-drawn Avalonia toast is enough for launch; native Notification Center
  (`UNUserNotificationCenter`) stays an optional later enhancement.
