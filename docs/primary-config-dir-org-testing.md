# Primary config dir org: manual test prompts

Manual before/after checks for branch `fix/primary-config-dir-org`. It fixes Perch showing the wrong org for the
default `~/.claude` (e.g. Perch says "Org B" while `/status` in a plain `claude` says "Org A").

Claude Code reads `$CLAUDE_CONFIG_DIR/.claude.json` when the variable is set, but the legacy `~/.claude.json` when it
is unset, even if the variable would have named `~/.claude`. Two bugs combined:

- **Perch pinned the primary explicitly.** `AccountChoiceSet.InjectRootFor` inherited for the primary only when no
  account guardrail governed the folder. Under a guardrail it launched with `CLAUDE_CONFIG_DIR=~/.claude` spelled
  out, so Claude used `~/.claude/.claude.json`, turned the stub into a full config, and stored a login there that
  diverged from `~/.claude.json`. The primary now always inherits, as `TranscriptLocator.ResumeConfigRoot` already
  did for resumes.
- **The reader preferred that inside copy.** `ClaudeJsonReader.ReadSignIn` took `~/.claude/.claude.json` whenever it
  was signed in and only fell back to `~/.claude.json`. For the default dir (unpinned Perch) it now reads
  `~/.claude.json` first. Every other dir still reads its own file first. The statusline script's `account` lookup
  had the same inside-first order and now reads `~/.claude.json` first when `CLAUDE_CONFIG_DIR` is unset.

`SessionAccountChoiceTests` and `ClaudeJsonReaderTests` cover both rules. These checks cover the running tray.

## Setup

Have a primary `~/.claude` signed in to Org A (check with `/status` in a plain `claude`), plus one other config
dir signed in to Org B. Look for a stray `~/.claude/.claude.json`: if it is larger than a few hundred bytes and
holds an `oauthAccount`, it is the divergent copy.

- **Before:** the installed Perch (0.5.34).
- **After:** this branch (`dotnet run --project src/Perch.App -f net10.0-windows10.0.19041.0`, or a release build).

## 1. Org shown for the primary

With the divergent `~/.claude/.claude.json` still present, open Settings → Config directories and hover the
primary's chip on the overlay.

| | Before | After |
|---|---|---|
| Settings → `.claude` row, "Signed in:" | Org B (from the inside copy) | Org A, matching `/status` |
| Overlay dir-chip tooltip / usage header | Org B | Org A |

## 2. Launching a session under a guardrail that allows the primary

Add an account guardrail (Settings → Config directories → Account guardrails) binding a folder to Org A. Start a new
Perch session in that folder with Org A selected, then open `%APPDATA%\Perch\logs\launch.log`.

| | Before | After |
|---|---|---|
| `controlled session start … CLAUDE_CONFIG_DIR=` | `[C:\Users\<you>\.claude]` | `(null)` |
| `/status` inside that session | Org from `~/.claude/.claude.json` | Org A |

## 3. Statusline account

With a Perch statusline profile that shows `{{account.org}}`, run a plain `claude` (variable unset).

| | Before | After |
|---|---|---|
| Statusline org | Org B (inside copy) | Org A |

## Not covered

- **The divergent file stays on disk.** Nothing deletes `~/.claude/.claude.json`. Once no session runs pinned to
  `~/.claude`, nothing reads it, and you can rename it away. Sessions already started pinned keep writing to it until
  they end.
- **A deliberately pinned Perch** (`CLAUDE_CONFIG_DIR=~/.claude` set for the tray, or the test suite) passes the pin to
  every session it starts, so it keeps reading the inside file. That's intended.
