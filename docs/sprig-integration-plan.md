# Sprig integration plan

[Sprig](https://github.com/arcticgizmo/sprig) runs a repo's apps and infra in isolated worktree "slots". This integration shows what sprig is running on the overlay, lets you start, stop, pause and resume it from there, opens sprig's own UI on a workspace, and ties Claude Code sessions to the slot they're working in.

The status feed was designed with sprig for this: the ask is `../sprig/docs/live-status-proposal.md`, and what sprig built is `../sprig/docs/live-status-plan.md` (sprig v0.9.10).

## Decisions

- **One long-lived `sprig watch --json` child, never a poll.**
  - Launching the CLI on a timer costs a .NET start each time and is the kind of behaviour EDR flags (see the `gh` lessons).
  - `watch` prints a full snapshot, then one NDJSON line per change, and is quiet while nothing changes.
  - Perch keeps its stdin redirected and open. When Perch exits or crashes, the pipe closes and `watch` exits on its own, so nothing is orphaned.
- **Off by default and load-bearing while off.** No process starts, and nothing is located or read, until `ShowSprig` is on, the same as Hypertree and GitHub alerts.
- **Restart with backoff.** If `watch` exits (sprig updated or removed, a crash), Perch restarts it after 2 s, doubling to 60 s. A run that lasted a minute resets the backoff. While it's down, the section keeps the last state, dimmed, with "reconnecting…".
- **Locating sprig:** `%LOCALAPPDATA%\Sprig\current\sprig.exe` (the Velopack install), else `sprig.exe` (`sprig` off Windows) on `PATH`. `Sprig\bin` holds only `.cmd`/`.ps1` shims for `sprig cd`, and Perch never launches those.
- **Reading the stream is pure Core:**
  - `Perch.Data.Sprig.SprigStatusFold` turns lines into an immutable `SprigStatus`. It's tolerant: it ignores unknown `type`s and fields, reads what it knows from a higher `schema`, and skips malformed lines without throwing.
  - It's tested from NDJSON fixtures. The child process itself isn't tested (no planted executables).
- **Off the UI thread.** A background thread reads and folds lines, then posts at most one pending repaint to the UI thread. A burst of lines (a workspace starting) costs one repaint, not one per line. The canvas repaints only when something it draws changed (a signature, as Hypertree does).
- **Actions go through the CLI's `--json`, run off the UI thread:**
  - `up --no-wait` for a stopped workspace, `start`/`restart`/`stop` per app or workspace, and `pause`/`resume --no-wait`;
  - `open <orch> <ws>` brings sprig's window up on the workspace.
  - Progress shows through the stream, not the command's output.
  - A failure's `code` becomes one plain notification. `untrusted`, `inputs-needed`, `held` and `slot-broken` say "open sprig to resolve", with sprig opened on the workspace, rather than Perch rebuilding sprig's trust and inputs UI. `docker-unavailable` says Docker isn't running.
- **The overlay section is a standard collapsible section,** `OverlaySection.Sprig`:
  - **Header:** chevron, a sprig mark, a `SPRIG` caption, and a right-aligned summary ("2 running · 1 starting", or "reconnecting…" / "not installed"). Clicking the header toggles it. The expand state is persisted as `SprigSectionExpanded` (a `NotSettings` UI-state bool).
  - **Body:** one line per workspace that isn't free, with a state dot, the branch label, and a right-hand detail ("3/8 ready", "paused", "stopped", "failed").
    - Order: failed, starting, running, paused, stopped. Stopped lines are muted.
    - Free slots are hidden: they're sprig's spare capacity, not something you're working on.
  - **Line click:** opens sprig on that workspace.
  - **Line right-click:** the menu of actions that fit its state, then "Open {app}" (`http://localhost:{port}`) for each ready app with a port, then "Open in sprig" and "Open folder".
  - **Header right-click:** "Open sprig".
  - **Dwell tooltip on a line:** the orchestration, slot and branch, then every app with its state and port, and the workspace's error if it has one.
- **Docker isn't counted (`"infra":"not-counted"`),** so a workspace with only its containers up reads stopped or paused. Perch shows sprig's state as given and doesn't call it "down".
- **Linking sessions to slots:** a session belongs to a workspace when its cwd is inside one of the workspace's `dirs`, or its session id is the workspace's `madeForAgent`. A linked session row gets a small sprig glyph coloured by the workspace's state. Hovering it explains it, and a click opens sprig on the workspace.
- **Notifications are opt-in** (`NotifySprig`, `playful: false`): an app exited or failed, or a workspace failed to start. A workspace you started from Perch also gets "ready" when every app is.
  - Only changes count, so starting Perch never replays old failures.
  - They're held by the notifications master switch and Do Not Disturb. Quiet mode doesn't hold them: they're work alerts, not playful ones.
  - Clicking one opens sprig on the workspace.
- **macOS:** the code is platform-neutral (`Process` + `PATH`). Sprig's mac story is sprig's to land; the section simply reads "not installed" until a `sprig` is on `PATH`.

## Phases

**Status:** S1–S5 are built on branch `sprig-integration`, with unit tests and renders. Live checks are in [sprig-integration-testing.md](sprig-integration-testing.md).

1. **S1 Core:** `SprigStatus` models, `SprigStatusFold` (snapshot, workspace, app, removals, host), `SprigLocator`, `SprigCli` (actions with `--json` result parsing), and tests over fixture lines.
2. **S2 Monitor:** `SprigMonitorHost` (the child process, the reader thread, coalesced UI posts, backoff, Start/Stop), `AppSettings.ShowSprig` plus its registry toggle, `PreviewTarget.Sprig`, and the live-apply wiring.
3. **S3 Overlay section:** `OverlaySection.Sprig` (default spot after GitHub), `OverlayCanvas.Sprig.cs` (header, lines, hover, click, menu, tooltip), the gate, rearrange dimming, sample data for the preview and `render`, and the App handlers for actions and open.
4. **S4 Session link:** match sessions to workspaces, the row glyph, its tooltip and click.
5. **S5 Notifications:** the opt-in transitions, plus the "ready" for a workspace started from Perch.
6. **S6 Docs:** `docs/sprig-integration-testing.md` (manual before/after prompts), a changelog line, and the Starlight site page.

## Not doing (v1)

- Docker infra state: `watch` doesn't carry it yet (a later sprig `--infra`).
- CPU and memory per app (`--usage`): Perch already has its own resource views; revisit if wanted.
- Logs: "Open in sprig" covers them.
- Checking out a branch or creating a slot from Perch: that's sprig's form.
