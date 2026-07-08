# macOS completion plan

Companion to `docs/macos-port-plan.md` (the original architecture plan) and `docs/self-managed-hooks-plan.md`
(Phase 4, done). This doc is the **current, on-a-Mac** plan: what's actually left, in the order we'll do it,
written after auditing the code and confirming the head builds and renders on Apple Silicon.

## Where we actually are (audited 2026-07-06, on macOS arm64, .NET 10.0.301)

The port is much further along than "stubs to fill in" — the heavy lifting (Phases 1–4 of the original
plan) is code-complete. What was *never possible before* was running any of it on a Mac. It is now.

**Verified live on this machine:**
- The cross-platform head **builds** on macOS: `Perch.Core` → `Perch.Platform.Mac` → `perch` (0 warnings).
- Every owner-drawn surface **renders** correctly via `render` mode — stat cards, overlay, flightpath,
  settings, stats, wrapped. No text clipping under macOS font metrics (the CLAUDE.md text-height concern
  holds). The Avalonia 12 backend behaves.

**Code-complete but unverified at runtime** (native P/Invoke only resolves live — this is the real risk):
- All 8 platform interfaces in `Perch.Platform.Mac/` are implemented, not no-ops:
  `WindowChrome`, `WindowActivator`, `SystemMetrics`, `SessionLock`, `AudioCue`, `AppIconProvider`,
  `GlobalHotkey`, `PathInstaller` (+ the shared `ObjC` interop helper).

**The one genuine code stub — FIXED (2026-07-07):**
- `Perch.Platform.Mac/ImageClipboard.cs` used to `return false` (Wrapped "Copy image" a no-op). Now it
  encodes the BGRA poster to PNG via a portable `Perch.Core/Data/PngEncoder` and writes it to the general
  `NSPasteboard` as `public.png`. Verified on-device: the clipboard carries a valid 8-bit RGBA PNG (macOS
  auto-derives TIFF/JPEG/etc. from it, so it pastes into any app).

**Not started:**
- macOS packaging (`.app` / `Info.plist` / `.icns` / DMG) — `publish.bat` + `release.yml` are Windows-only.
- `perch-hook` publish on a macOS RID (the binary itself is portable NativeAOT; only the ship step is Win).
- First-run permission UX (Accessibility, Notifications) and menu-bar/`LSUIElement` behaviour.

**Build friction found:** on a non-Windows host `dotnet build`/`run` fails with `NETSDK1100` unless you pass
`-p:EnableWindowsTargeting=true`, because the multi-targeted project still *restores* the `net10.0-windows`
TFM even when you ask for `-f net10.0`. Phase A fixes this so the Mac workflow needs no extra flags.

## Decisions (settled 2026-07-06)

- **Distribution:** unsigned local builds for now (Gatekeeper right-click-open workaround). Developer-ID
  signing + notarization is a deferred follow-up — no Apple Developer account yet.
- **Architecture:** **arm64 only** (`osx-arm64`). No Intel lane.
- **Sequencing:** **verify interop live first** — shake out the runtime bugs on this Mac before investing in
  packaging.
- **Build friction:** auto-condition the csproj so macOS builds need no extra flags.

---

## Phase A — Build ergonomics (quick, unblocks everything else)

Make the repo build and run on macOS with plain `dotnet build` / `dotnet run`, no flags.

- In `src/Perch.App/Perch.App.csproj`, condition the TFM list on the host OS so the `net10.0-windows` TFM
  is only present when building on Windows (or in CI with `EnableWindowsTargeting`). Sketch:
  ```xml
  <TargetFrameworks Condition="'$([MSBuild]::IsOSPlatform(Windows))' == 'true'">net10.0-windows10.0.19041.0;net10.0</TargetFrameworks>
  <TargetFrameworks Condition="'$([MSBuild]::IsOSPlatform(Windows))' != 'true'">net10.0</TargetFrameworks>
  ```
  Keep the Windows CI/publish path building both heads (it runs on `windows-latest`, so the condition keeps
  both TFMs there). Verify `dotnet build src/Perch.App/Perch.App.csproj` (no `-f`, no flag) works on macOS
  and that the Windows publish is unchanged.
- Update CLAUDE.md's build/run/render commands with the macOS forms (drop `-f net10.0-windows…`; use
  `-f net10.0` or bare on a Mac).
- Sanity: `dotnet test tests/Perch.Tests/Perch.Tests.csproj` still runs on macOS (Core is portable; confirm
  the fixture paths — `FixtureCwd` is a `C:\…` path, so check the tests aren't Windows-path-bound).

**Exit:** `dotnet build`, `dotnet run --project src/Perch.App`, and `render` all work on macOS flag-free.

---

## Phase B — Verify the 8 interfaces live on this Mac (the priority)

Run the real tray app (`dotnet run --project src/Perch.App`) and exercise each capability. Each interface
below has a **what to check** and the **likely failure mode**. Fix as we go; anything objc-heavy is the most
likely to need tuning. Keep a running scorecard in this doc.

1. **Overlay renders as a real window** — `WindowChrome.MakeToolWindowNoActivate` /
   `MakeClickThroughNoActivate` (`LiveOverlayWindow`, `GlowWindow`, `ConfettiWindow`, `DenseDropZoneWindow`,
   toast host). *Check:* overlay floats above other apps on all Spaces, doesn't steal focus, click-through
   overlays pass clicks. *Likely issue:* Avalonia fighting the `NSWindow` level, or the window activating on
   show.
2. **`SystemMetrics`** — CPU/RAM bars in the overlay. *Check:* CPU% and RAM% track Activity Monitor roughly;
   per-process parent-pid map resolves (used for session→terminal attribution). *Likely issue:* `host_statistics`
   struct layout / `libproc` offset (`pbi_ppid` at 16), or values pinned to wrong constants.
3. **`AudioCue`** — Done/WaitingForInput chimes via `afplay`. *Check:* both sounds play. Cheap to confirm.
4. **`SessionLock`** — notification suppression while screen locked. *Check:* lock the screen, trigger a
   notify, confirm suppressed; unlocked → fires. *Likely issue:* `CGSessionCopyCurrentDictionary` value-type
   handling — but it fails safe (reads unlocked).
5. **`AppIconProvider`** — quick-link app icons + `open -a` launch. *Check:* terminal/IDE icons appear on
   overlay rows; clicking a quick-link launches the app. *Likely issue:* `.app` bundle resolution / `sips`
   conversion; falls back to initials on failure.
6. **`GlobalHotkey`** — Carbon `RegisterEventHotKey`. *Check:* the configured hotkey toggles the overlay
   from an unfocused state, no Accessibility permission needed. *Likely issue:* handler wiring, keycode map
   (`kVK_ANSI_*`).
7. **`WindowActivator`** — hardest; walks parent pids to the hosting terminal/IDE and activates it. *Check:*
   clicking a session row focuses the right app. *Known limitation:* app-level focus, not tab/window level
   (`projectHint` unused until an `AXUIElement` pass — Phase E, needs Accessibility).
8. **`PathInstaller`** — symlink into `~/.local/bin/perch`. *Check:* after Register(), `perch` resolves in a
   fresh shell (if `~/.local/bin` is on PATH). Wired into install hooks in Phase D.

**Hook path verified end-to-end on macOS (2026-07-06)** — Phase 4 was Windows-only; now confirmed on-device:
- `perch-hook` publishes NativeAOT for `osx-arm64` (`dotnet publish src/Perch.Hook -r osx-arm64 -c Release`)
  → a 2.1 MB arm64 Mach-O. Driven directly (`echo '{...}' | perch-hook mode`) it exits 0 and writes the
  `{sid}.mode` sidecar. ✅
- `HookInstaller` (already fully cross-platform) copies the shipped binary to the stable bin dir, `chmod +x`,
  and reconciles `~/.claude/settings.json`: all seven managed hook events wired with absolute path + args +
  `_perch.managed` markers; a pre-existing user `Stop` hook and `statusLine` were preserved. ✅
- **Settings-path coupling — non-issue on .NET 10.** The port docs warned `SpecialFolder.ApplicationData`
  maps to `~/.config` (XDG) on macOS, diverging from where the bin/icon cache land. **Verified false on
  .NET 10:** both `ApplicationData` and `LocalApplicationData` resolve to `~/Library/Application Support`, so
  `AppSettings`, `HookInstaller.BinDir`, and `AppIconProvider` all share one `~/Library/Application
  Support/Perch[ (Dev)]` root. The reconcile wired hooks to `~/Library/Application Support/Perch (Dev)/bin/
  perch-hook` and the copied binary is exactly there — app and `perch-hook` agree with no special-casing.

**Exit:** a scorecard in this doc marking each of the 8 interfaces + the hook path pass/fail on-device, with
follow-up items filed for any that need tuning. `IWindowActivator` and `IGlobalHotkey` are release-blocking
(full-parity decision) — they must pass here.

### Phase B results (macOS arm64, verified 2026-07-06)

Verified with a headless harness driving each impl directly (`scratchpad/macverify`) plus a live boot of the
tray app. `dotnet build`/`run` on macOS builds the single `net10.0` head flag-free (Phase A).

| Interface | Result | Notes |
|---|---|---|
| `ISystemMetrics` | ✅ pass | CPU busy fraction, RAM used/total, and a 433-entry parent-pid map (incl. self→ppid) all sane. |
| `ISessionLock` | ✅ pass | Reports unlocked interactively; fails safe. Lock/unlock transition still worth a manual eyeball. |
| `IAudioCue` | ✅ pass | `afplay` fires; Glass/Funk aiffs present. Audible check is manual. |
| `IAppIconProvider` | ✅ pass | Produced a real 32px PNG from `Safari.app` via `sips`. |
| `IPathInstaller` | ✅ pass | Created + removed `~/.local/bin/perch` symlink. |
| `IGlobalHotkey` | ✅ pass (registration) | Carbon `RegisterEventHotKey`+`InstallEventHandler` succeed; unmappable key refused. **Firing** needs the app run loop — confirm the bound key toggles the overlay in the live app. |
| `IWindowActivator` | ✅ pass (window-precise for Terminal.app) | Focus moves to the exact Terminal window+tab via tty match; other terminals fall back to app-level. See the bugs below. |
| `IWindowChrome` | ✅ pass (after fix) | See bug below — was a fatal startup crash; fixed. Visual "floats above all Spaces / click-through" is a manual live check. |

**Bug found & fixed — fatal startup crash (`WindowChrome`).** Booting the tray app on macOS crashed with
`-[AvnWindow window]: unrecognized selector`. Avalonia 12's macOS platform handle
(`TryGetPlatformHandle()`) is the **`NSWindow` (`AvnWindow`) itself**, not the top-level `NSView` the code
assumed — so `[handle window]` hit a nonexistent selector. Two lessons baked into the fix:
1. `WindowChrome.WindowOf(...)` now probes `respondsToSelector:@selector(window)` — an NSView responds, an
   NSWindow doesn't — so it works whichever Avalonia hands back.
2. **A managed `try/catch` does not contain an Objective-C `NSException`**: it unwinds through the P/Invoke
   boundary and terminates the process. So a bad selector isn't "best-effort degradation" — it's fatal.
   Any future objc send must target a selector we're certain the receiver implements.
After the fix the app boots and runs cleanly (empty log, no exception).

**Bug found & fixed — click-to-focus-terminal did nothing on macOS** (`WindowActivator`). Two independent
causes, both needed:
1. **Activation is ignored for a background agent.** `NSRunningApplication.activateWithOptions:` returns YES
   and flips the target's `isActive`, but on Sonoma+ the window server refuses the focus change when the
   request comes from a background app — and Perch is exactly that (`LSUIElement` + a no-activate overlay,
   so it's never frontmost when a row is clicked). Fix: resolve the app's bundle id via `NSRunningApplication`
   and foreground it with the stock **`open -b <bundleId>`** (falls back to `open <bundlePath>`), which the
   system honours from a background process with no Accessibility/Automation permission.
2. **The ppid walk dead-ended at a root process.** Terminal spawns a setuid-**root** `login` (Terminal →
   login(root) → shell → claude), and `libproc`'s `proc_pidinfo` refuses to read a root-owned process's info
   from our non-root process — so the walk stopped at `login` and never reached Terminal. Fix: read the full
   pid→ppid map once via **`ps -Ao pid=,ppid=`** (world-readable) and walk that. (Note: `ISystemMetrics.ReadParentMap`
   uses `proc_pidinfo` too and has the same blind spot for root processes, but that only affects a session's
   *descendant* rollup, which is user-owned — left as-is.)
Verified end-to-end: with Finder frontmost, `FocusTerminalForProcess(claudePid)` moves focus to Terminal.

**Follow-up — wrong Terminal window.** `open -b` foregrounds the app but lands on its last-used window, not
the one running the session, so with several Terminal windows open it focused the wrong one. Fixed by raising
the exact window+tab whose `tty` matches the session's controlling tty (`ps -o tty= -p <pid>` → `/dev/ttysNNN`),
via AppleScript against Terminal.app (the only interface exposing a tab's `tty`). Other terminals fall back to
app-level `open` (iTerm2 also exposes `tty` per session — a clean extension point; VS Code / others don't
script tabs). Verified end-to-end: with the other Terminal window frontmost, focusing the session raises its
window/tab (`ttys000 → ttys001`).
- **Packaging dependency (Phase D):** controlling Terminal via Apple Events needs the app bundle's
  `Info.plist` to declare **`NSAppleEventsUsageDescription`**, and triggers a one-time "Perch wants to control
  Terminal.app" Automation prompt (Phase E first-run flow). If declined, it degrades to app-level `open`.

**Also noted:** on macOS `SpecialFolder.LocalApplicationData` → `~/Library/Application Support` (where the
icon cache lands) while `SpecialFolder.ApplicationData` → `~/.config` (where settings land). The two roots
diverge — relevant to choosing the `perch-hook` stable-bin location (Phase B hook task / Phase D).

**Bug found & fixed — credentials on macOS live in the Keychain, not a file.** The usage strip showed
"Couldn't read Claude credentials — sign in to Claude Code" because `UsageMonitor` only read
`~/.claude/.credentials.json`, which **does not exist on macOS** — Claude Code stores the OAuth blob in the
login **Keychain** (generic-password item, service `Claude Code-credentials`). Added an `IClaudeCredentials`
Core seam: a portable `FileClaudeCredentials` (Windows/Linux) and a `KeychainClaudeCredentials` (macOS, via
`/usr/bin/security -w`, falling back to the file), resolved through `PlatformServices` and injected into
`UsageMonitor`. Verified live: the Keychain read returns the same JSON shape and a valid access token is
parsed, so the usage poll authenticates. (First read may raise a one-time Keychain access prompt.)

**Remaining manual checks** (need a human at the machine, not scriptable): hotkey actually toggles the
overlay; overlay floats above all Spaces and click-through overlays pass clicks; window activation lands on
the right terminal; lock/unlock suppresses/fires a notification; chimes are audible.

---

## Phase C — Close the last stub + fix what Phase B surfaces — DONE

- **`ImageClipboard.TryCopyBgra`** — DONE. Encodes the BGRA poster to PNG (`Perch.Core/Data/PngEncoder`,
  unit-tested) and writes it to the general `NSPasteboard` as `public.png` via the `ObjC` helper. Verified
  on-device (valid 8-bit RGBA PNG on the clipboard). "Copy image" now works on macOS.
- Interop bugs Phase B surfaced were fixed as they were found (WindowChrome `AvnWindow` crash;
  WindowActivator activation + root-process walk + wrong-window; Keychain credentials). No known remaining
  interop bugs.

**Exit:** met — no known interop bugs; `ImageClipboard` implemented and verified.

---

## Phase D — Packaging: unsigned arm64 `.app` + DMG — DONE (2026-07-07)

Scoped to the decisions: **arm64 only, unsigned.**

- **`Info.plist`:** bundle id (e.g. `com.arcticgizmo.perch`), `LSUIElement=true` (menu-bar/agent app, no Dock
  icon), version, min-OS, and **`NSAppleEventsUsageDescription`** (required for the Terminal.app
  window-focus AppleScript — without it the Automation request is denied). Author it under `src/Perch.App/`
  (mac-only) and wire into the publish.
- **`.icns`:** generate from `perch.svg`. Extend `tools/gen-icons.*` with a mac path (`iconutil` from an
  `iconset`, or `sips`), writing `Assets/icon.icns`. `tools/gen-icons.ps1` is PowerShell — either add a
  `gen-icons.sh` or make the existing script cross-platform (`pwsh` runs on macOS).
- **Bundle layout:** `dotnet publish src/Perch.App -f net10.0 -r osx-arm64 -c Release --self-contained`, then
  assemble the `.app` (Velopack's macOS lane: `vpk pack` for `osx-arm64` producing `.app` + DMG). Include the
  `osx-arm64` `perch-hook` next to `perch` inside the bundle so `HookInstaller` can copy it to the stable bin.
- **Install hooks (mac):** wire `PathInstaller.Register()` and `HookInstaller` reconcile on first launch;
  there's no Velopack uninstall hook on mac, so rely on `perch-hook` self-heal (already built) to strip
  orphaned `~/.claude/settings.json` entries.
- **Gatekeeper:** since unsigned, document the right-click → Open workaround (and `xattr -dr com.apple.quarantine`)
  in the README.
- **`publish.bat` sibling:** add a `publish-mac.sh` for local mac builds (mirror the Windows script's
  perch + perch-hook + pack steps).

**Exit:** a runnable unsigned `Perch.app` / DMG built locally on this Mac; launches, shows the menu-bar item,
overlay works, hooks wire up.

### Phase D results (macOS arm64, verified 2026-07-07)

Built end-to-end on this Mac with `./publish-mac.sh` (vpk 1.2.0). Produces `releases/Perch-0.2.0-osx-arm64.dmg`
(+ Velopack's `.pkg`, portable `.zip`, and update feed). DMG mounts with a `Perch.app` / `Applications`
drop-target; the bundled self-contained binary runs (`render` mode exits 0 from inside the mounted DMG).

- **`Info.plist`** (`src/Perch.App/Info.plist`) — committed with a `__VERSION__` token the script substitutes.
  Confirmed in the packed bundle: `CFBundleIdentifier=com.arcticgizmo.perch`, `LSUIElement=true`,
  `LSMinimumSystemVersion=12.0`, `NSAppleEventsUsageDescription` present, `CFBundleIconFile=icon`, version
  stamped. Passed to `vpk pack --plist` — **note vpk rejects `--bundleId` + `--plist` together**, so the id
  lives in the plist alone.
- **`.icns`** — `tools/gen-icns.sh` (sips + iconutil) derives `Assets/icon.icns` from `landing-icon.png`
  (IconGen's SVG→raster path is System.Drawing, Windows-only, so the mac icon has its own tool). Committed;
  excluded from `AvaloniaResource` so it isn't embedded in the assembly. Ends up in `Contents/Resources/`.
- **`publish-mac.sh`** — sibling of `publish.bat`: publishes `perch` (`-f net10.0 -r osx-arm64`, self-contained;
  the multi-target csproj still needs the explicit `-f`) + `perch-hook` (NativeAOT) into one dir, strips the
  `perch-hook.dSYM`, then `vpk pack` (channel `osx`) → `.app`/`.pkg`/portable, and wraps the `.app` into a
  compressed DMG via `hdiutil` (Velopack's mac lane emits `.pkg`, not DMG). Cleans `publish/`+`releases/` for
  a repeatable run.
- **Install hooks (mac)** — `PathInstaller.Register()` now runs on launch **only from inside a `.app`**
  (`App.IsInsideAppBundle()` guards a dev `dotnet run` from clobbering `~/.local/bin/perch`), mirroring the
  Windows Velopack install callback. `HookInstaller.Install()` already runs cross-platform every launch and
  copies the bundled `perch-hook` to the stable per-user bin. No Velopack uninstall hook on mac — relies on
  `perch-hook` self-heal, as planned.
- **Gatekeeper** — README gained a "macOS (unsigned)" install note (`xattr -dr com.apple.quarantine` /
  right-click Open) and the local-build section documents `publish-mac.sh`.

**Not done here (belongs to E/F):** live launch of the installed `.app` (menu-bar item, overlay, the one-time
Notifications/Automation prompts) — needs a human at the machine; the CI mac lane is Phase F.

---

## Phase E — Permissions, menu-bar & polish

- **First-run permission UX:** request **Notifications** and, for the `AXUIElement` window-raising upgrade,
  **Accessibility**. Degrade gracefully when denied — the seams already tolerate refusal (activation falls
  back to app-level focus; Carbon hotkey needs no Accessibility). Note: app-level `WindowActivator` and the
  Carbon `GlobalHotkey` do **not** need Accessibility, so full parity is reachable without it; Accessibility
  only buys tab/window-precise raising.
- **Menu-bar (`TrayIcon`) behaviour** with `LSUIElement`: verify left-click / context-menu parity with
  Windows, no Dock icon, no stray main window.
- **Retina/scaling pass:** `render` at 1×/1.5×/2× and eyeball on a Retina display; re-check the owner-drawn
  text-height rule under macOS metrics (spot-checked clean in the audit, but verify the number-heavy stat
  cards at 2×).
- **Native notifications (optional):** `UNUserNotificationCenter` for real Notification Center toasts. The
  owner-drawn `AvaloniaToastNotifier` already works, so this is an enhancement, not a blocker.
- **`AXUIElement` window raising (optional):** upgrade `WindowActivator` to raise the specific terminal
  window/tab using `projectHint` (needs Accessibility). Deferred unless app-level focus proves too coarse.

**Exit:** clean first-run permission flow; menu-bar parity; Retina verified.

---

## Phase F — CI & docs

- **CI:** add a `macos-latest` (arm64) job to `.github/workflows/release.yml` on the `v*` tag — publish
  `perch` + `perch-hook` for `osx-arm64`, pack the unsigned `.app`/DMG, attach to the release. Keep the
  existing Windows job untouched. (Signing/notarization secrets are a later add when there's a Developer ID.)
- **Docs:** update `README.md` (mac install + Gatekeeper workaround), `CLAUDE.md` (layout note already covers
  `Perch.Platform.Mac`; refresh the build/run/render commands for macOS), and `tools/` docs for the `.icns`
  path.
- Fold `docs/macos-port-plan.md` status forward (mark Phases 1–3 verified-on-device once Phase B passes).

**Exit:** tagging `v*` produces both a Windows and an unsigned macOS artifact; docs describe the mac flow.

---

## Sequencing summary

A (build ergonomics) → **B (live interop verify — the priority)** → C (fix stubs/bugs) → D (unsigned arm64
packaging) → E (permissions/polish) → F (CI/docs). A is an hour; B is where the real unknowns are and gates
the release-blocking interfaces (`WindowActivator`, `GlobalHotkey`); D–F are a separable packaging track once
B/C are green.

## Deferred (explicitly out of scope for first release)

- Developer-ID signing + notarization (needs an Apple Developer account).
- Intel (`osx-x64`) / universal binary.
- `UNUserNotificationCenter` native notifications.
- `AXUIElement` tab/window-precise activation.
