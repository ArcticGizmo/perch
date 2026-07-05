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

> **Status: Phases 1–3 are done** (branch `cross-platform`). The app compiles as two heads and every
> `Perch.Platform.Mac` interface now has a real implementation — **all written on Windows and not yet
> verified on a Mac** (the native calls only resolve at runtime). Phase 4 (plugin `invoke.sh`) is next;
> on-device verification of the interop rides along with Phase 5 packaging when a Mac is available.

### Phase 1 — Close the `OverlayNativeChrome` leak (do this regardless) — DONE
Introduce a Core seam, e.g. `IWindowChrome` with `MakeToolWindowNoActivate(...)` and
`MakeClickThroughNoActivate(...)` taking the neutral window handle (the five call sites already have an
Avalonia `Window` + `TryGetPlatformHandle()`). Move the Win32 body into a `WindowChrome : IWindowChrome`
in `Perch.Platform.Windows`, register it in `PlatformServices`, and replace the five
`OperatingSystem.IsWindows() && ... OverlayNativeChrome.X(h.Handle)` sites with a call through the seam
(the seam itself no-ops off its platform). This is pure refactor, testable on Windows today, and removes
the last direct dependency of UI code on `Perch.Platform.Windows`.

### Phase 2 — Make the head build per-OS — DONE
Multi-target the existing `Perch.App` rather than forking a second head project (fewer moving parts; the
composition root already switches on OS). **Key correction to the original sketch:** the cross-platform
head is plain **`net10.0`**, not `net10.0-macos`. Avalonia is not MAUI — a desktop Avalonia app is a
normal .NET app that runs on macOS via Avalonia's own native backend, and `Perch.Platform.Mac` reaches
AppKit/libSystem through P/Invoke. So no `macios` workload and no Mac are needed to *build* the mac head —
it compiles on Windows/CI, the native entry points only resolving at runtime on macOS.
- `<TargetFrameworks>net10.0-windows10.0.19041.0;net10.0</TargetFrameworks>`.
- Condition the Windows-only bits to the `-windows` TFM: `OutputType=WinExe`, `ApplicationManifest`,
  `ApplicationIcon`, `BuiltInComInteropSupport`, the `Microsoft.Toolkit.Uwp.Notifications` package, and
  the `Perch.Platform.Windows` project reference. The `net10.0` TFM references `Perch.Platform.Mac`,
  compiles `WindowsToastNotifier.cs` out, and uses `OutputType=Exe`.
- `PlatformServices` picks the implementation set with `#if WINDOWS` (the SDK auto-defines `WINDOWS` for
  the `-windows` TFM); the same guard wraps the `WindowsToastNotifier` instantiation in `App`.
- Portable-mutex name (drop the `Local\` prefix off Windows) done; Velopack's Windows-only install
  fast-callbacks are `#if WINDOWS`-guarded (macOS install hooks come with Phase 5 packaging).
- `publish.bat` / `release.yml` gained `-f net10.0-windows10.0.19041.0` so the Windows publish still
  resolves a single TFM. Dev run/render now need `-f net10.0-windows10.0.19041.0` too (see CLAUDE.md).

The app builds as both heads; the macOS head links against no-op platform stubs (overlay + toasts +
file-based detection work, everything else degrades quietly) until Phase 3 fills the interop in.

### Phase 3 — Implement `Perch.Platform.Mac`
The `net10.0` project now exists with no-op stubs (Phase 2). Fill in the real interop via P/Invoke
(`objc_msgSend` into AppKit/CoreGraphics, plus `libSystem`/`libproc`), one interface at a time, easiest
first so the app is usable early. **Everything below compiles on Windows/CI; anything marked DONE still
needs on-device verification on a Mac** (the native calls only resolve at runtime).
- **`IAudioCue`** — DONE (unverified): shells to `/usr/bin/afplay` with the stock
  `/System/Library/Sounds` aiffs — "Glass" for Done, "Funk" for WaitingForInput. Future refinement:
  `NSSound` to avoid a process per chime.
- **`ISessionLock`** — DONE (unverified): polls `CGSessionCopyCurrentDictionary()` for
  `CGSSessionScreenIsLocked` on the getter (handling both CFNumber and CFBoolean value types via
  `CFGetTypeID`); any failure reads as unlocked so a notification is never wrongly suppressed. No
  distributed-notification observer needed since the dispatcher only reads it per push.
- **`ISystemMetrics`** — DONE (unverified): CPU via `host_statistics(HOST_CPU_LOAD_INFO)` mapped to the
  Windows `(idle, kernel⊇idle, user)` convention; total RAM via `sysctlbyname(hw.memsize)` and used via
  `host_statistics64(HOST_VM_INFO64)` (`total − (free + inactive)`); parent-pid map via `libproc`
  (`proc_listpids` + `proc_pidinfo(PROC_PIDTBSDINFO)`, reading `pbi_ppid` at offset 16). Constants pinned
  from the XNU headers; symbols imported from the libSystem umbrella. `MetricsMonitor`'s delta maths is
  unchanged.
- **`IWindowChrome`** (from Phase 1) — DONE (unverified): a shared `ObjC` interop helper (class/selector
  lookup + typed `objc_msgSend` overloads, BOOL-as-byte, no struct returns) underpins it. Gets the
  `NSWindow` via `[nsView window]`, then sets `level` (NSStatusWindowLevel), `collectionBehavior`
  (all-Spaces | stationary | ignores-cycle | fullscreen-aux) and, for the click-through overlays,
  `setIgnoresMouseEvents:`. The `ObjC` helper is the foundation the remaining objc-based impls build on.
- **`IAppIconProvider`** — DONE (unverified): shell-based to dodge the fragile `NSImage → PNG` objc chain.
  Launch via `open -a <name>`; icon by locating the `.app` bundle (pinned/resolved path or Spotlight
  `mdfind`), finding its `.icns` (`CFBundleIconFile` via `defaults read`, else first `*.icns`), and
  converting to a cached PNG with `sips -Z <size>`. Null on any failure → strip draws initials. Future
  refinement: `NSWorkspace.iconForFile` for pixel-exact icons.
- **`IWindowActivator`** — DONE (unverified), hardest. Walks parent pids via `libproc` up to the nearest
  regular GUI app (the hosting terminal/IDE) and activates it via
  `NSRunningApplication.runningApplicationWithProcessIdentifier:` + `activateWithOptions:`. Coarser than
  Windows — focuses the app, not a specific window/tab (`projectHint` unused until we add `AXUIElement`
  window-level raising in Phase 6). No Accessibility permission needed for app-level activation.
- **`IGlobalHotkey`** — DONE (unverified): Carbon `RegisterEventHotKey` (fires without focus and needs no
  Accessibility permission, unlike an `NSEvent` global monitor). One lazily-installed app-wide Carbon
  event handler dispatches to the registered `Action` by hot-key id; letters/digits map to `kVK_ANSI_*`
  virtual key codes.
- **`IPathInstaller`** — DONE (pure managed, low-risk): symlinks `Environment.ProcessPath` into
  `~/.local/bin/perch` (user-writable, no sudo) and removes it on uninstall. Phase 5 may relocate to
  `/usr/local/bin` via a privileged installer step. Still needs wiring into the mac install hooks (Phase 5).
- **`INotifier` (optional native)** — `UNUserNotificationCenter` for real Notification Center toasts;
  skip initially since `AvaloniaToastNotifier` already covers it.

All eight interfaces are now implemented (no remaining no-op stubs). What's left for Phase 3 proper is
purely **on-device verification** — every one is flagged "unverified on a Mac" until then. The likeliest
to need tuning are the objc-heavy ones: `WindowChrome` (does Avalonia fight the window level?),
`WindowActivator` (which ancestor is the terminal; does focus land right), and `GlobalHotkey` (Carbon
handler wiring + keycode map).

### Phase 4 — Replace the plugin with self-managed hooks (direction changed)
Rather than port `invoke.ps1` to a per-OS `invoke.sh` under the marketplace plugin, Perch drops the
plugin/marketplace entirely (its separate release cadence made changes painful) and self-manages hooks in
the user's `~/.claude/settings.json`, calling our own cross-platform binary. This also deletes the per-OS
script problem — one code path, both OSes. The `/afk` + `/history` commands are dropped (unused; their
functions already live in the tray). See the discussion decision recorded in project memory.
- **`perch-hook` helper — DONE (Windows-verified; commit pending).** A tiny NativeAOT console app
  (`src/Perch.Hook/`) ported 1:1 from `invoke.ps1` minus the commands: events `mode` (write `{sid}.mode`),
  `agentstop`/`teammateidle` (drop `agent-{id}.stopped`/`.idle`), `start` (seed mode + auto-launch the
  tray when opted in), `cleanup` (remove sidecars + sweep markers). Reflection-free (`Utf8JsonReader`),
  honours `CLAUDE_CONFIG_DIR` and `PERCH_DEV` like the app, always exits 0, never emits a block decision.
  Perf-validated (~2.8× faster than `powershell invoke.ps1`, and that was the R2R upper bound). In the
  solution; builds AOT-clean (0 warnings). Functionally verified via a hermetic temp-config harness.
- **Detailed handoff spec for the remaining work: `docs/self-managed-hooks-plan.md`** (self-contained,
  can be tackled by a separate process).
- **Still TODO (part of #3):** the self-wiring — write/reconcile Perch's hook block in
  `~/.claude/settings.json` on launch (idempotent, tagged with a `_perch`/`managed` marker; always exit 0
  so a stale entry can't wedge Claude Code), self-heal (remove own entries when Perch is gone), remove on
  uninstall, and a migration that removes the old marketplace plugin. Reuse `ClaudeUserSettings.cs` (it
  already does a tolerant merge of `~/.claude/settings.json`).
- **Packaging note:** the AOT helper is built per-RID (`dotnet publish -r <rid> -p:PublishAot=true`); the
  release pipeline gains a `perch-hook` publish step alongside `perch` (Phase 5). NativeAOT needs the VS
  "Desktop development with C++" workload — present on the GitHub `windows-latest` runner.

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
