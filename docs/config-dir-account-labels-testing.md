# Account picker labels config dirs: manual test prompts

The account picker in the new-session launcher, the PR session launcher, the composer footer chip and the `/resume` row chip choose a **config dir**, not an account. Several config dirs can be signed into the same account, and previously they were collapsed into one choice, so the extra dirs couldn't be picked. Now:

- Every config dir is its own choice.
- A dir's label is the account alone when no other config dir is signed into that account.
- When another dir shares the account, the label is `Account | dir name`, using the dir's custom label from Settings → Config directories, or else the folder name.
- When the dir name matches the account name (case-insensitive), it shows the account alone rather than `Acme | acme`.
- Labels are worked out against **every** config dir, so a guardrail or a transcript that only some dirs can read doesn't change how a dir is named.
- Menu items show the dir's full path as a tooltip, and the footer chip's tooltip adds a `Config dir:` line.

`SessionAccountChoiceTests` covers the resolver and the labels. These checks cover the UI.

## Setup

- **Before:** the installed Perch (0.5.43).
- **After:** this branch, run with `dotnet run --project src/Perch.App -f net10.0-windows10.0.19041.0` (quit the installed tray first).

You need two config dirs signed into the **same** account plus one on a different account, e.g. `~/.claude` (Acme), `~/.claude-work` (Acme), `~/.claude-personal` (personal). To sign a dir in, start a session under it from the account picker and type `/login`; the terminal it opens runs under that dir.

## 1. New-session launcher

Open a new session window (overlay "+ New session") and click the account pill.

| | Before | After |
|---|---|---|
| Menu entries | `Acme (default)`, `me@example.com` | `Acme \| .claude (default)`, `Acme \| .claude-work`, `me@example.com` |
| Pick `Acme \| .claude-work`, start the session, then in it ask: `Print the CLAUDE_CONFIG_DIR environment variable` | Not possible | Prints the `.claude-work` path |
| Pill with only `~/.claude` and `~/.claude-personal` | `Acme (default)` | Unchanged: `Acme (default)` |

Then give `~/.claude-work` the custom label `Acme` in Settings → Config directories, and reopen the menu.

| | After |
|---|---|
| `.claude-work` entry | `Acme` (no repeated name) |
| `.claude` entry | Still `Acme \| .claude (default)` |

## 2. Composer footer chip

In a session running under `.claude-work`:

| | Before | After |
|---|---|---|
| Footer chip | `Acme` | `Acme \| .claude-work` |
| Chip tooltip | Account and email | Same, plus `Config dir: <path>` |
| Chip menu, when both Acme dirs share the project's `projects/` folder | No switch offered (same account) | Switch to `Acme \| .claude` is offered |

## 3. `/resume` row chip and PR launcher

| | After |
|---|---|
| `/resume` row chip for a session resumable from both Acme dirs | `Acme \| <dir>  ▾`, and the menu lists both dirs |
| GitHub dashboard → start a session from a PR → Account dropdown | Same labels as the launcher |

## 4. Guardrail

Add an account guardrail for a folder that allows only Acme, then open the launcher on that folder.

| | Before | After |
|---|---|---|
| Selector | Locked to `Acme` | Unlocked, offering both Acme dirs with the `\| dir` suffix |
