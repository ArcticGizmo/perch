# Self-managed hooks — implementation plan (handoff)

**Status:** ✅ **done** (branch `cross-platform`). All tasks implemented, built (both heads +
`perch-hook`), unit-tested, and verified end-to-end hermetically. Commits: `5cd4e13` (reconcile +
`HookInstaller` + startup wiring + `PluginManager` migration + Settings UI removal + tests), `8c11d34`
(`perch-hook` self-heal), `c1ecc76` (uninstall removal), `236c886` (ship `perch-hook` next to `perch`),
`160a6b3` (deleted `plugins/perch/` + `.claude-plugin/marketplace.json`, docs updated). Kept below as the
implementation record. Parent context: `docs/macos-port-plan.md` (Phase 4).

Resolved open questions: user-`settings.json` hooks are keyed by event → `[{matcher, hooks:[{type,
command, args?}]}]`; the exec `args` form is used (no shell quoting for the `Perch (Dev)` path); unknown
`_perch` fields are ignored; a **missing command is non-blocking** (only exit 2 blocks, which `perch-hook`
never emits). Stable bin: `%APPDATA%\Perch[ (Dev)]\bin`, consistent with `AppSettings` and `perch-hook`'s
profile logic. `plugins/perch/` was **deleted** (git history retains it).

## Why

Perch currently ships a Claude Code **marketplace plugin** (`plugins/perch/`) whose hooks
(`hooks/hooks.json` → `scripts/invoke.ps1`) write sidecar files the tray watches. That plugin is
distributed through a separate marketplace with its own release cadence, which makes changes slow and
awkward. Decision: **drop the plugin/marketplace entirely** and have Perch **self-manage its hooks** by
writing them into the user's `~/.claude/settings.json`, pointing at our own fast binary. The `/afk` and
`/history` slash commands are **dropped** (unused in practice; their functions already exist in the tray:
external-notify toggles via the overlay right-click menu, history via the history window).

This also unifies Windows and macOS on one code path (no per-OS `invoke.sh` needed).

## What's already done (do not redo)

- **`perch-hook`** (`src/Perch.Hook/`, in `perch.slnx`): a NativeAOT console app that is the cross-platform
  replacement for `invoke.ps1`. It reads the hook JSON on stdin and writes the sidecars. Events (arg 1):
  - `mode` — write `<sessions>/{session_id}.mode` (permission mode). Hot path: every PreToolUse/PostToolUse/Stop.
  - `agentstop` — drop `agent-{id}.stopped` beside the agent transcript (SubagentStop).
  - `teammateidle` — drop `agent-{id}.idle` beside the matching transcript (TeammateIdle).
  - `start` — seed mode, then auto-launch the tray if the user opted in (SessionStart).
  - `cleanup` — remove this session's sidecars + sweep agent markers (SessionEnd).
  - It is reflection-free (`Utf8JsonReader`), **honours `CLAUDE_CONFIG_DIR` and `PERCH_DEV`** exactly like
    the app, **always exits 0**, and **never writes a block decision**. Verified via a hermetic harness.
  - Perf: ~2.8× faster than `powershell invoke.ps1` (validated; NativeAOT will be faster still).
- **Dev/prod isolation** (`Perch.Data.AppProfile`): dev builds use a separate settings dir `Perch (Dev)`,
  a separate mutex, and a `(Dev)` tray label. `perch-hook` mirrors the profile choice via `PERCH_DEV`.

## Goal of this work

On launch, Perch reconciles its own hook entries in `~/.claude/settings.json` so the hooks always point at
the current `perch-hook` binary; it heals/removes them appropriately; and it migrates users off the old
marketplace plugin. A stale or orphaned hook must **never** be able to wedge a Claude Code session.

## Tasks

### 1. Install `perch-hook` to a stable per-user location
The Velopack install dir is **versioned** (`AppContext.BaseDirectory` changes on every update), so pointing
`settings.json` at the binary in the install dir would break the wiring after each update until the next
launch. Instead, on launch copy the shipped `perch-hook` to a **stable** location and point the hooks
there:
- Windows: `%APPDATA%\Perch\bin\perch-hook.exe` (or `%LOCALAPPDATA%`). Use the profile dir
  (`AppProfile.DataFolderName`) so a dev instance uses `Perch (Dev)\bin`.
- macOS: `~/Library/Application Support/Perch/bin/perch-hook` (or under the profile dir). Note the app's
  *settings* live at `~/.config/Perch` (XDG, via `SpecialFolder.ApplicationData`) — keep the bin location
  consistent with whatever you choose, and make `perch-hook`'s own `ProfileFolder()` agree.
- Copy-if-newer (compare version/mtime) so it self-updates. `chmod +x` on macOS.
- The shipped binary is published next to `perch` (see Task 6).

### 2. Reconcile the hook block in `~/.claude/settings.json` on launch
Add a service (suggest `src/Perch.App/Services/HookInstaller.cs`, or extend `PluginManager`). On startup
(off the UI thread), read → merge → write `~/.claude/settings.json`:
- **Reuse `Perch.Core/Data/ClaudeUserSettings.cs`** — it already does a *tolerant* read/modify/write of
  `~/.claude/settings.json` (preserves other keys, tolerates `//` comments + trailing commas). Extend it
  with a `hooks`-key merge rather than writing a second JSON path. `ClaudePaths.UserSettingsFile` is the path.
- Write our entries under the `hooks` key for these events → `perch-hook` args:
  `PreToolUse`/`PostToolUse`/`Stop` → `mode`; `SubagentStop` → `agentstop`; `TeammateIdle` → `teammateidle`;
  `SessionStart` → `start`; `SessionEnd` → `cleanup`. (Mirror `plugins/perch/hooks/hooks.json`, minus
  `UserPromptSubmit`.)
- The command is the **absolute path** to the stable `perch-hook` from Task 1 + the event arg.
- **Idempotent + versioned:** tag each managed entry so we can find and replace *only ours* without
  touching the user's own hooks. Suggested marker on each hook object (Claude Code ignores unknown fields):
  ```json
  { "type": "command", "command": "<abs>/perch-hook mode",
    "_perch": { "managed": true, "version": "0.2.0",
                "note": "Added by Perch. Safe to delete if Perch is uninstalled." } }
  ```
  On each launch: remove all entries carrying `_perch.managed == true`, then re-add the current set. That
  makes path/version drift self-correct (critical given the versioned install dir).
- **Confirm the exact user-`settings.json` hooks schema first** (command-string vs `command`+`args`;
  whether `matcher` is required per event). The plugin `hooks.json` uses `{ "matcher": "", "hooks": [ {
  "type": "command", "command": ..., "args": [...] } ] }`. Verify the user-settings shape matches (a
  claude-code-guide agent can confirm) before relying on it.

### 3. Safety invariants (non-negotiable)
- `perch-hook` already always exits 0 and never emits a block decision — keep it that way.
- A **missing** binary (Perch deleted without cleanup) must not block tool calls. Confirm Claude Code
  treats a missing hook command as a non-blocking spawn error (verify with claude-code-guide). If it can
  block, prefer a wrapper that can't.
- Reconcile must be defensive: never throw out of startup; if `~/.claude/settings.json` is missing/garbage,
  best-effort and move on.

### 4. Self-heal for orphaned entries
`perch-hook` should detect when Perch is gone and remove its own hook entries:
- On run, if the stable Perch install is absent (e.g. the main app/bin is gone), have `perch-hook` strip
  the `_perch.managed` entries from `~/.claude/settings.json` and exit 0. This cleans up when the app was
  removed but the hook file survived. (Keep it cheap — it runs per event.)

### 5. Remove hooks on uninstall
- Windows: `Program.cs` already wires Velopack `OnBeforeUninstallFastCallback` (guarded `#if WINDOWS`).
  Add hook-block removal there alongside `PathInstaller.Unregister()`.
- macOS: no uninstall hook yet (Phase 5 packaging). Rely on Task 4 self-heal until then.

### 6. Migrate off the old marketplace plugin
- `src/Perch.App/App.axaml.cs` currently calls `AutoInstallPlugin()` on first run (adds the marketplace +
  enables the plugin via `Perch.Core/Data/PluginManager.cs`). **Replace** that with the reconcile from
  Task 2, plus a one-time migration that **removes** the marketplace + plugin so events aren't delivered
  twice (once by plugin, once by our hooks). Add the inverse of `PluginManager.EnableAsync` /
  `ReadInstalledState` (a disable/remove path). Then delete/retire `plugins/perch/` (or keep only as
  reference) and any Settings UI that toggles the plugin.

### 7. Packaging: ship `perch-hook` next to `perch`
- `Perch.Hook.csproj` builds AOT per-RID: `dotnet publish src/Perch.Hook -r <rid> -c Release -p:PublishAot=true`.
- Publish it into the same output dir as `perch` so Velopack packs it. Update `publish.bat` and
  `.github/workflows/release.yml` (the `windows-latest` runner has the required VS "Desktop development
  with C++" workload; a local `publish.bat` run needs it too — guard/allow accordingly). For the future
  macOS lane, publish the mac RIDs likewise.

## Key files

| Purpose | Path |
|---|---|
| Hook binary (done) | `src/Perch.Hook/` |
| Tolerant `~/.claude/settings.json` read/modify/write to reuse | `src/Perch.Core/Data/ClaudeUserSettings.cs` |
| `~/.claude` path resolution (`UserSettingsFile`, `SessionsDir`) | `src/Perch.Core/Data/ClaudePaths.cs` |
| Marketplace/plugin state + enable (needs an inverse) | `src/Perch.Core/Data/PluginManager.cs` |
| First-run plugin install to replace; uninstall hook removal | `src/Perch.App/App.axaml.cs`, `src/Perch.App/Program.cs` |
| Old plugin (reference / to retire) | `plugins/perch/hooks/hooks.json`, `plugins/perch/scripts/invoke.ps1` |
| Profile (settings dir / mutex / label) | `src/Perch.Core/Data/AppProfile.cs` |
| Settings key the `start` hook reads | `AppSettings.AutoStartOnFirstSession` |

## Testing (all doable on Windows; no Mac needed)

- **Reconcile:** point at a throwaway config via `CLAUDE_CONFIG_DIR`, run the app (dev build is auto-isolated
  via `AppProfile`), and assert `~/.claude/settings.json` gains exactly the managed hook block (idempotent
  across repeated launches; only `_perch.managed` entries change; user entries untouched).
- **End-to-end:** with the same `CLAUDE_CONFIG_DIR`, invoke the wired command manually
  (`echo '<payload>' | perch-hook <event>`) and confirm the dev tray reacts — this is the hermetic pattern
  already used to verify `perch-hook` (see the port plan / commit `a0c4d69`).
- **Migration:** seed a fake old-plugin state in a test `~/.claude/settings.json` and assert it's removed.
- **Self-heal / uninstall:** simulate a missing install and assert the managed entries get stripped and no
  exception escapes.
- Add xUnit coverage in `tests/Perch.Tests` for the settings.json merge/reconcile/removal logic (the
  data-layer parts), following the existing fixture pattern.

## Acceptance criteria

1. Fresh launch writes a correct, idempotent managed hook block to `~/.claude/settings.json`; repeated
   launches don't duplicate or drift; user-authored hooks are preserved.
2. Every event fires `perch-hook` with the right arg and produces the same sidecars the plugin did.
3. After an update (new versioned install dir), hooks still resolve (stable bin location + reconcile).
4. A removed/renamed Perch never blocks a Claude Code session; orphaned entries self-heal or are removed on
   uninstall.
5. No double events: the old marketplace plugin is removed on migration.
6. `perch-hook` ships in the release next to `perch`.

## Open questions to resolve first

- **Exact user-`settings.json` hooks schema** and **missing-command failure semantics** (blocking or not).
  Confirm via a claude-code-guide agent before implementing Task 2/3.
- Stable bin location: `%APPDATA%\Perch\bin` vs `%LOCALAPPDATA%` (and the macOS equivalent) — pick one and
  keep `perch-hook`'s `ProfileFolder()` / settings-path logic consistent with it.
- Whether to delete `plugins/perch/` outright or keep it as reference during rollout.
