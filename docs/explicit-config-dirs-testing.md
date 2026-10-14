# Explicit config directories: manual test prompts

Manual before/after checks for the move from auto-detected config directories to an explicit list.

- **No more auto-detection.** The set used to pull in every `~/.claude*` sibling, every claude-envs env (when
  `~/.claude-envs` has a manifest) and every dir a session self-reported. Now it is the primary plus the dirs in
  `DeclaredConfigDirs`, nothing else (`ClaudeConfigDiscovery.Discover`).
- **Scan suggests instead.** Settings → Config directories → **Scan for directories** runs the same scan
  (`ClaudeConfigDiscovery.Suggest`) and lists what isn't added yet, each with an **Add…** that opens the usual
  add dialog prefilled. The scan looks in every `~/.claude-envs/envs/*` folder, manifest or not (the old
  discovery needed `manifest.json`/`envs.json`); each must still look like a config dir. A session's self-report (`.configdir` marker) is still remembered in
  `%APPDATA%\Perch\config-dirs.json`, but only as a suggestion.
- **One-time migration.** A settings file from before the change (no `ConfigDirsExplicit` key) gets every dir the
  old discovery showed declared, keeping its hooks state: a scanned dir lands with hooks off unless you'd turned
  them on, a session-reported dir with hooks on. Dirs you'd removed (`HiddenConfigDirs`) are not carried over. Then
  `HiddenConfigDirs` and `HooksEnabledDirs` are cleared and `ConfigDirsExplicit` is set (`ConfigDirMigration`).
  A fresh install starts explicit. A pinned tray (`CLAUDE_CONFIG_DIR` set) skips the migration.
- **Remove works.** Remove used to hide a dir by real path. A hidden entry with a stray trailing space (`…\incontrol `)
  never matched the discovered `…\incontrol`, so Remove did nothing. Remove now just un-declares.

`ClaudeConfigDiscoveryTests`, `ConfigDirMigrationTests`, `ConfigDirSafeWriteTests` and `HookPolicyTests` cover the
logic. These checks cover the Settings page and a real upgrade.

## Setup

Quit Perch before each check. Back up `%APPDATA%\Perch\settings.json` first; the migration rewrites it.

- **Before:** the installed Perch (0.5.33).
- **After:** this branch, `dotnet run --project src/Perch.App -f net10.0-windows10.0.19041.0` (uses the
  `Perch (Dev)` profile, so copy your `settings.json` into `%APPDATA%\Perch (Dev)\` to test the upgrade), or a
  release build via `publish.bat`.

`perch configdirs` prints the set and, after this change, what a scan would suggest.

## 1. Upgrade keeps what you had

Note the rows in Settings → Config directories on the old build, and each one's "Perch hooks" line. Start the
new build.

| | Before | After |
|---|---|---|
| Rows listed | Primary, added, "auto · discovered", "auto · reported by a session" | The same dirs, all "added" except the primary |
| Each row's "Perch hooks" line | As noted | Unchanged |
| A dir you'd removed earlier | Absent | Still absent |
| `settings.json` | `HiddenConfigDirs` / `HooksEnabledDirs` present | Both gone; `"ConfigDirsExplicit": true`; found dirs in `DeclaredConfigDirs`; scanned ones also in `HooksDisabledDirs` unless you'd turned hooks on |

## 2. Remove sticks

Remove a non-primary dir. Restart Perch.

| | Before | After |
|---|---|---|
| Right after Remove | Row stays (if its hidden entry had a trailing space) | Row gone |
| After restart | Back if it was auto-discovered and the hidden entry didn't match | Still gone |
| Overlay chips / sessions from that dir | Still shown | Gone |

## 3. New folders aren't picked up on their own

With Perch running, create `%USERPROFILE%\.claude-scratch\sessions\`. Wait 10 s.

| | Before | After |
|---|---|---|
| Settings → Config directories | `.claude-scratch` appears as auto-discovered | Not listed |

## 4. Scan and add

Click **Scan for directories**.

| | Expected |
|---|---|
| While scanning | "Scanning…" under the buttons; the Scan button is disabled |
| Results | "Found — not added" with `.claude-scratch` (from check 3), anything you removed in check 2, claude-envs envs not yet added. Each row: name, path (long paths elide the *front*), and where it came from |
| Nothing to suggest | "No other config directories found." |
| Click **Add…** on a row | The add dialog opens with the path filled in, hooks on |
| Save | The dir joins the list above; its suggestion row disappears without rescanning |
| Cancel | Nothing changes; the suggestion stays |

Delete `%USERPROFILE%\.claude-scratch` afterwards.

## Not covered

- **Session self-reports only surface through a shared `sessions/`.** The `.configdir` marker is read from the
  sessions folders Perch already watches, so a session in a dir you haven't added is only noticed if that dir's
  `sessions/` is junctioned onto one you have. Otherwise the home/claude-envs scan is what finds it.
- **Hooks left in a dir that drops out.** A dir that was auto-discovered with hooks opted in, then *not* migrated
  (e.g. you'd removed it), keeps whatever Perch hooks were already in its `settings.json`. Remove has always
  stripped them; the migration doesn't touch dirs it doesn't carry over.
