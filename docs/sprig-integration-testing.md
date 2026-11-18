# Sprig integration: testing

The overlay gains a collapsible **Sprig** section. Under it is one line per workspace sprig has checked out (free slots are hidden), each with a state dot, its branch, and a right-hand detail ("3/4 ready", "starting 1/3", "1 app failed", "paused", "stopped"). The design is in [sprig-integration-plan.md](sprig-integration-plan.md). This covers phases S1–S4 (S4 is the session-row glyph, check 10).

- **Header:** a chevron, a green sprout, `SPRIG`, and on the right "2 running · 1 starting" (or `connecting…`, `reconnecting…`, `not installed`, `nothing running`, `N paused`). Clicking it collapses or expands the section, and that's remembered.
- **Line click:** opens sprig on that workspace (`sprig open <orch> <slot>`).
- **Line right-click:**
  - Restart / Pause / Stop, or Resume / Stop, or Start, depending on the state;
  - `Open <app> (localhost:<port>)` for each ready app;
  - **Apps ▸**, with a submenu per app (Start or Restart / Stop, Open log);
  - Open in sprig / Open folder / Copy path.
- **Hover a line:** the branch, `<orch> · slot N`, every app with its state and port, and any error.
- **Order:** anything failed (or with a failed app) first, then starting, running, paused, stopped.

How it gets its data: one `sprig watch --json` child for as long as the setting is on, never a poll. Actions run `sprig <verb> <orch> <slot> [app] --json --no-wait` off the UI thread. A failure gives one toast. When sprig needs you (`untrusted`, `inputs-needed`, `held` …), sprig is opened on the workspace. Diagnostics go to `%APPDATA%\Perch (Dev)\logs\sprig.log`.

Unit coverage: `SprigStatusTests` covers the line fold (snapshot, app, workspace, removals, host, malformed and unknown lines), the action command lines, and result parsing. The headless render (`render <dir>`, `overlay_sprig_*.png`) covers the section's states. The checks below are the live ones.

## Setup

- **Before:** the installed Perch (0.6.13), which has no Sprig section.
- **After:** this branch, run with `dotnet run --project src/Perch.App -f net10.0-windows10.0.19041.0`. It uses its own settings (`%APPDATA%\Perch (Dev)`).
- **Sprig:** 0.9.10 or later (`sprig --version`). You need at least one slot checked out (`sprig workspaces`); the examples use `Safety-Virtual` slot `1`.
- **Cost:** starting a workspace builds its apps and brings its Docker infra up. Use a slot you're happy to run.

## 1. Off by default

Start the branch build with a fresh `Perch (Dev)` profile (or with **Sprig** off).

| | Before | After |
|---|---|---|
| Overlay | No Sprig section | No Sprig section |
| `Get-Process sprig -ErrorAction SilentlyContinue` | Nothing from Perch | Nothing (no `sprig watch` until it's turned on) |

## 2. Turn it on

Settings → search `sprig` → turn **Sprig** on.

| | After |
|---|---|
| Overlay | The Sprig section appears under the GitHub/feeds strips; the header reads `connecting…` for under a second, then the summary |
| Lines | One per non-free workspace, matching `sprig workspaces` (paused ones read `paused`, stopped ones are muted) |
| `Get-CimInstance Win32_Process -Filter "Name='sprig.exe'" \| Select ProcessId, CommandLine` | Exactly one `sprig.exe watch --json` |

Turn it off again.

| | After |
|---|---|
| Overlay | The section disappears at once |
| The `sprig watch` process | Gone within ~2 s |

Turn it back on for the rest of the checks.

## 3. Live updates

In a terminal: `sprig resume Safety-Virtual 1` (or `sprig start Safety-Virtual 1` if it isn't paused).

| | After |
|---|---|
| The slot's line | Moves to the top as `starting 0/N` (blue dot), counts up as apps become ready, then `N/N ready` (green), each within about a second of the terminal saying so |
| Header | `1 starting`, then `1 running` |
| Hover the line | Each app with its state (`building`, `waiting`, `ready on :<port>` …) |

Then `sprig stop Safety-Virtual 1`.

| | After |
|---|---|
| The slot's line | `stopped`, muted, moved down |

## 4. Actions from the menu

Right-click the slot's line.

| Menu item | After |
|---|---|
| **Start** (stopped slot) | The line goes `starting` → `ready` as in check 3, with no toast |
| **Open api (localhost:NNNN)** | The browser opens that URL |
| **Apps ▸ api ▸ Restart** | Only that app goes back through `building`/`starting` (hover to see) |
| **Apps ▸ api ▸ Open log** | The app's log opens in VS Code |
| **Pause** | The line reads `paused` (amber); `sprig workspaces` agrees |
| **Resume** | Back to `ready` |
| **Stop** | `stopped` |
| **Open in sprig** (or a left click on the line) | Sprig's window comes up on that workspace |
| **Open folder** / **Copy path** | Explorer opens the slot folder / the path is on the clipboard |

## 5. Failures

- **Docker off.** Quit Docker Desktop, then **Start** a slot that has infra. Expected: one toast, "Couldn't start <branch>", "Docker isn't running. Start Docker Desktop and try again."
- **Needs sprig.** On a slot whose definition isn't approved (edit its `sprig.yaml` so it's untrusted again), **Start**. Expected: one toast saying sprig needs you, and sprig opens on that workspace.
- **An app that crashes.** Break one app (a bad port in its config, say) and start the slot. Expected: the line moves to the top reading `1 app failed` (red), and hovering shows that app as `exited (<code>)` with its error.

## 6. Sprig's host comes and goes

With a slot running, run `sprig host stop`.

| | After |
|---|---|
| The slot's line | Its apps go `off`; it reads `stopped` (or `paused` if it was) |
| Header | No longer counts it as running |

Then **Start** it from the menu: the host comes back and the line goes through `starting` → `ready` again.

## 7. The watch process dies

Kill the `sprig watch` process (Task Manager, or `Stop-Process -Id <pid>` using the id from check 2).

| | After |
|---|---|
| Header | `reconnecting…` (amber), lines faded |
| Within ~2 s | A new `sprig watch` is running; header and lines back to normal |

Kill it a few times in a row. The wait grows (2 s, 4 s, 8 s … up to 60 s), and `logs\sprig.log` has one "watch ended" line per kill.

## 8. Perch exits

Exit the branch build from its tray menu, then check `sprig.exe` processes.

| | After |
|---|---|
| `sprig watch` | Gone (it ends when Perch closes its stdin; nothing is orphaned) |

Also try ending the dev `perch.exe` from Task Manager: the watch should still end within a second or two, because the pipe closes.

## 9. Collapse and order

- Click the `SPRIG` header to collapse it, then restart the branch build. It comes back collapsed.
- Settings → Appearance live preview → **Rearrange**: the Sprig section can be dragged like the others, and is dimmed when the setting is off.

## 10. Session rows (S4)

A session working in a sprig slot gets a small sprout between its worktree and PR glyphs, coloured by the workspace's state: green running, blue starting, red failed (or an app of it failed), amber paused, grey stopped. A session is in a workspace when sprig's hook made the slot for it (`madeForAgent`), or when the worktree it's editing in, or else its folder, is inside one of the workspace's repo folders. Free slots never match.

In a terminal in `C:\Users\JonHowell\Documents\git\Safety-Virtual--1` (a slot), start `claude` and paste:

> Say hi and stop.

| | Before | After |
|---|---|---|
| The session's row | No sprout | Sprout, coloured by slot 1's state |
| Hover it | — | `Sprig · <branch>`, `Safety-Virtual · slot 1`, the state detail, each running app |
| Click it | — | Sprig opens on slot 1 |
| Right-click it | — | Slot 1's menu, the same one as its line in the Sprig section |
| `sprig resume Safety-Virtual 1` | — | The sprout turns blue, then green |

Also start `claude` in a folder sprig doesn't know: no sprout. Turn **Sprig** off: every sprout disappears.

## Not covered live

- **`not installed`:** render-only (`overlay_sprig_missing_1x.png`), unless you're willing to rename `%LOCALAPPDATA%\Sprig\current\sprig.exe` for a minute. If you do, sprig is looked for again every minute.
