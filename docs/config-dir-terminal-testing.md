# Config-dir "Terminal" button: manual test prompts

Each row in Settings → Config directories gets a **Terminal** button (Windows only). It opens a shell in that directory with `CLAUDE_CONFIG_DIR` already set to it, so you can debug an account by hand (`claude`, `claude auth status`, poking at `settings.json`) without typing the variable yourself. The shell follows the "Reopen sessions in" terminal choice. It echoes the variable on open so you can see which directory it's on.

`ClaudeCliTerminalCommandTests` covers the command lines (cmd, the Windows Terminal re-tokenising, the PowerShell literal, and refusing unsafe paths). These checks cover the actual launch.

## Setup

- **Before:** the installed Perch (0.5.43).
- **After:** this change, run with `dotnet run --project src/Perch.App -f net10.0-windows10.0.19041.0` (quit the installed tray first; Perch is single-instance).

Have at least one non-primary config dir listed, e.g. `%USERPROFILE%\.claude-envs\envs\pdg`.

## 1. Each terminal choice

For each value of Settings → search "Reopen sessions in" (Automatic, Windows Terminal, PowerShell, Command Prompt), click **Terminal** on a non-primary row.

| | Before | After |
|---|---|---|
| Config-dir row buttons | Edit, Remove | Terminal, Edit, Remove |
| Terminal opens in | — | The config dir itself |
| First output line | — | `CLAUDE_CONFIG_DIR=<that dir>` |
| Automatic / Windows Terminal | — | A WT tab running cmd (not your default WT profile, which can't be handed the variable) |
| PowerShell | — | Windows PowerShell 5.1, left open |

Then in that shell run:

```text
claude auth status
```

| | After |
|---|---|
| Account shown | The one signed in to that config dir (matches the row's "Signed in" line), not the primary's |

## 2. A path with spaces

Add a config dir whose path contains a space (e.g. `C:\Temp\claude work`, create it first) and click **Terminal** with "Automatic" selected.

| | After |
|---|---|
| Echoed value | The full path including the space, with no trailing space |

## 3. Unusable paths

| Case | After |
|---|---|
| A dir that doesn't exist on disk (row shows the ⚠ meta line) | Terminal button is disabled |
| A dir containing `&`, `%`, `;` or `^` | Button text changes to "Couldn't open"; no shell opens |

## Not covered

- **macOS.** The button is hidden; `open -a Terminal` can't pass an environment variable, so `OpenTerminal` refuses a config dir there until the macOS launcher is filled in.
