# Tray config-dir pin: manual test prompts

Manual before/after checks for branch `fix/tray-inherits-config-dir`. It fixes:

- **The tray inheriting a session's `CLAUDE_CONFIG_DIR`.** A tray started with the variable set pins its config-dir
  set to that one directory (`ClaudeConfigSet.IsPinned`). Every other directory vanishes from Settings, the overlay
  chips and the account picker, and "Add config directory…" silently does nothing. Velopack's update restart then
  carries the pin into the next version. Two launch paths passed the variable on: `perch-hook`'s auto-start
  (`LaunchPerch`) and a terminal `perch` handing off to a background tray (`Program.DetachTray`). Both now drop it
  before starting the tray.
- **Re-adding a directory never cleared a hidden entry with a trailing space** (e.g. `…\envs\incontrol `, left by
  a launcher that set `CLAUDE_CONFIG_DIR` with a stray space). Windows treats both spellings as one folder, so the
  editor now compares them with `ClaudeConfigDir.SameRoot`.

`ClaudeConfigDirTests` covers `SameRoot`. These checks cover the launch paths, which only a real tray shows.

## Setup

Perch is single-instance, so quit the tray before each check. Pick a non-primary config dir you have, e.g.
`%USERPROFILE%\.claude-envs\envs\pdg`, and have at least one other dir (declared or auto-discovered) listed in
Settings → Config directories.

- **Before:** the installed Perch (0.5.32).
- **After:** this branch installed as a release build (`publish.bat`, then run `releases\Perch-win-Setup.exe`).
  The hook check needs the installed hook, which a `dotnet run` dev tray doesn't wire to the release profile.

To see what the running tray thinks, open Settings → Config directories. `perch configdirs` starts a fresh
process, so it shows what discovery *would* find, not what the running tray has.

## 1. Auto-start from a session in another config dir

Turn on auto-start on session start (Settings → search "start"). Quit Perch. In a terminal:

```text
set CLAUDE_CONFIG_DIR=%USERPROFILE%\.claude-envs\envs\pdg
claude
```

Perch's SessionStart hook starts the tray.

| | Before | After |
|---|---|---|
| Settings → Config directories | Only `pdg`, marked primary | `.claude` as primary, plus every other dir |
| Add config directory… (any dir) | Nothing happens | The dir appears |

## 2. `perch` typed in a terminal with the variable set

Quit Perch. In a terminal with `CLAUDE_CONFIG_DIR` set as above, run `perch`.

| | Before | After |
|---|---|---|
| Settings → Config directories | Only `pdg` | All directories |
| The session `perch` opens | Runs under `pdg` | Runs under the remembered/primary account, the same as a `perch` forwarded to an already-running tray |

## 3. Re-adding a dir clears a trailing-space hidden entry

Quit Perch and add a hidden entry with a trailing space to `%APPDATA%\Perch\settings.json`, e.g.
`"HiddenConfigDirs": [ "C:\\Users\\<you>\\.claude-envs\\envs\\incontrol " ]`. Start Perch and add `incontrol`
through Settings → Config directories → Add config directory….

| | Before | After |
|---|---|---|
| `HiddenConfigDirs` in settings.json afterwards | Still holds the spaced entry | Gone |

## Not covered

- **A tray that's already pinned stays pinned across an update.** The update restart inherits the running tray's
  environment, so a tray pinned before this fix keeps the pin through the update to it. Quit it and start Perch
  from the Start menu or a clean terminal once.
- **Where pinning still applies.** A dev run (`dotnet run`) skips the background handoff, so it still honours the
  variable, and `perch replay` sets it in-process for its sandbox. A terminal `perch` no longer pins the tray to the
  terminal's directory.
