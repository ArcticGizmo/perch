# Open the session window on the monitor `perch` was launched from

**Status:** prototype, shipped as code (uncommitted). Build + 1058 tests green. Not yet interactively
verified on a real multi-monitor setup.

## Problem

`SessionWindow` is created with `WindowStartupLocation.CenterScreen` (`Windows/SessionWindow.cs`), so a
session opened from the CLI lands on whatever screen Avalonia's `CenterScreen` picks — not necessarily the
monitor the user is looking at. When you run `perch` (or `perch <dir>`, `perch -c`) at a terminal on a
secondary monitor, the window can appear on the primary.

## Why it isn't a one-liner: the placing process isn't the launched process

In the common case a tray is already running:

- The `perch` CLI process builds a `SessionOpenIntent` and **forwards it over a named pipe** to the
  long-running tray, then exits (`Program.ForwardSessionIntent`).
- The **tray** — a different process — receives the intent and opens the `SessionWindow`
  (`App.OpenSessionIntent` → `OpenSessionNew` / `OpenSessionResume`).

So the tray has no idea which monitor the terminal was on. The monitor must be **sampled in the CLI process
at launch**, because the terminal is the foreground window at that instant; by the time the tray shows the
window (a pipe hop later) the foreground may have moved.

## Approach

Sample the foreground window's monitor in the CLI process (the foreground window at a terminal launch *is*
that terminal — this sidesteps the Windows-Terminal DefTerm ancestry mess entirely, cf.
`docs/terminal-focus-correlation.md`), carry its geometry in the intent, and have the tray centre the
window on it. The geometry is physical pixels in this machine's virtual-desktop space, so it is valid to
position against directly — no Avalonia `Screen` matching needed.

## Changes

1. **`IWindowChrome.GetForegroundMonitorGeometry()`** (`Perch.Core/Platform/IWindowChrome.cs`) — new seam
   returning the `MonitorGeometry` of the monitor showing the foreground window, or null.
   - Windows impl (`Perch.Platform.Windows/WindowChrome.cs`): `GetForegroundWindow` → `MonitorFromWindow`
     → `GetMonitorInfo` (+ per-monitor DPI). The bounds/work-area/scale conversion shared with the existing
     `GetMonitorGeometryAt` was pulled into a private `Geometry(hMonitor)` helper.
   - Mac stub (`Perch.Platform.Mac/WindowChrome.cs`): returns null.
2. **`SessionOpenIntent.OriginMonitor`** (`Perch.Core/Data/Control/ControlProtocol.cs`) — optional
   `MonitorGeometry?` on the intent record, serialised under a `"monitor"` object in `ToJson` and read back
   in `Parse` (`ParseMonitor`). A missing/malformed hint → null (falls back to default placement).
3. **`Program.Main`** — after resolving the intent, if the launch came from a terminal
   (`!isReplay && LaunchedFromTerminal()`), attach
   `PlatformServices.WindowChrome.GetForegroundMonitorGeometry()` to it. Covers both the synthesised bare-
   `perch` `StartFresh` and a parsed `FromArgs` intent (`perch <dir>`, `perch -c`, `perch --resume <id>`).
4. **`App`** — `PlaceOnLaunchMonitor(SessionWindow, MonitorGeometry?)` centres a not-yet-shown window on the
   hint's work area, sizing in physical pixels via the target monitor's scale so centring is correct across
   a DPI boundary; called from `OpenSessionNew` and `OpenSessionResume` before `Show()`. No hint → the
   window keeps its `CenterScreen` default.

## How to try it

- **Installed binary:** run `perch` (or `perch <dir>`) from a terminal sitting on a secondary monitor — the
  session window should open centred on that monitor.
- **Dev:** from a terminal on the target monitor,
  `dotnet run --project src/Perch.App -f net10.0-windows10.0.19041.0 -- C:\some\existing\dir`.
  A bare dev `dotnet run` is `trayOnly` (dev profile) so it won't synthesise a fresh session, but a
  positional dir produces a `FromArgs` intent, and the monitor hint attaches regardless of `trayOnly`.

## Terminal launch detaches (returns control to the shell)

A `perch` typed at a terminal used to stay bound to the tray for its whole lifetime — POSIX shells (Git
Bash) wait on the process handle regardless of the GUI subsystem, so the prompt never came back. Now, when
a terminal launch would *become* the tray (no instance running yet), `Program.DetachTray` relaunches the
tray as a **detached** background process (`Process.Start` with `UseShellExecute = true`) and returns to the
shell at once — the `code .` shape. The session intent (including the `OriginMonitor` hint) is handed to the
relaunched process through a short-lived JSON file named by `--open-intent-file <path>`, which
`Program.ReadRelaunchIntent` reads and deletes; this reuses `SessionOpenIntent.ToJson`/`Parse` so every
field survives without reconstructing a command line. The relaunched process runs the tray in-process
(`LaunchedFromTerminal()` is false when spawned detached, so it neither re-detaches nor re-samples the
foreground monitor — it keeps the hint from the file).

Skipped for: a relaunch we started ourselves, dev runs (`dotnet run` stays in the foreground for logs), and
non-terminal launches (login item, double-click, the hook, an update restart — no terminal to free). The
already-running-tray path (forward over the pipe) already returned control, so it's unchanged.

## Not covered / follow-ups

- The bare `perch --resume` (pick-a-session launcher) path goes through `OpenSessionWindow()`, which
  deliberately reuses an idle launcher and avoids dragging windows across virtual desktops; it is left on
  its existing placement, not repositioned.
- A manual monitor-hint is a launch-time hint only — nothing persists it, and moving the window afterwards
  is unaffected.
- Interactive verification on a real multi-monitor, mixed-DPI setup is still owed.
- The detach relaunch is unverified at runtime: confirm the prompt returns immediately in Git Bash /
  PowerShell / cmd, that the session still opens on the right monitor, and that the ~1–2s cold-boot delay
  before the window appears is acceptable. macOS takes the same path but is untested there.
