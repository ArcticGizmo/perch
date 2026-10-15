# /resume arguments, all-projects scope, and account switching: manual checks

Branch `resume-account-swap`. These are render- and unit-verified only; nothing below has been run live.

## What changed

- **`/resume <arg>`.** A full session id (a GUID) resumes that session straight away, from any project. Any other text
  opens the picker with the search pre-filled. Before this, `/resume foo` went to the CLI as plain text, where the
  picker doesn't exist headless.
- **Scope.** The picker opens on this project's sessions, as before. Tab (or the "This project" / "All projects" pill)
  switches to every project's sessions.
- **Choosing the account on resume.** When several config dirs share one `projects/` folder through a junction or
  symlink, a session's transcript can be read under any of them, so it can be resumed under any of those accounts.
  Rows like that get an account chip in the picker. The chip names the account the session would resume under, and
  clicking it offers the other accounts.
- **Switching account mid-session.** The footer account chip becomes clickable on the same sessions:
  - Running: it confirms, then restarts the process in place under the new account (the same as End session ▾ →
    Restart, but with a different `CLAUDE_CONFIG_DIR`).
  - Dormant or ended: it records the new account, and the next send or Resume runs there.
- **Which accounts are offered.** A dir counts only if its `projects/` is the same real folder as the transcript
  owner's (`TranscriptLocator.ResumableDirs`). Dirs signed into the same account collapse to one entry, and a folder
  guardrail (account rules) limits the list to the orgs it allows (`ResumeAccountChoice`).
- **How the choice persists.** A pick is stored in `SessionAccounts` (`session-accounts.json`), the same record
  resume already reads first. A later resume from anywhere (Recent, the overlay row, the Roost, a restart of Perch)
  follows it.

Not covered: a single `projects/<enc-cwd>` folder junctioned on its own, rather than the whole `projects/`. That case
still resolves to one owner. It would also list the session twice, because the history scan doesn't remove duplicate
session ids.

## Setup

You need two config dirs signed into **different** accounts, with `projects/` shared. For example, with
`~/.claude-work` declared in Settings → Config directories:

```powershell
# One-off: share the primary's projects/ into the work dir (needs an empty or absent work\projects first).
cmd /c mklink /J "%USERPROFILE%\.claude-work\projects" "%USERPROFILE%\.claude\projects"
```

Builds: `main` is the "before" build and this branch is the "after" build. Run each with
`dotnet run --project src/Perch.App -f net10.0-windows10.0.19041.0`.

Cost: every check that sends a message bills the account it runs under. An account switch makes the next message
re-send the whole context cold.

## Checks

| # | Do this | Before (main) | After (this branch) |
|---|---------|---------------|---------------------|
| 1 | In a Perch session, type `/resume` and press Enter. | Picker opens on this project's sessions. | Same, plus a "This project" pill, and the hint line includes "tab all projects". |
| 2 | In the picker, press Tab, then Tab again. | Tab moves focus out of the search box. | It flips to "All projects" (the subtitle reads "every project" and rows from other folders appear), then back. |
| 3 | Copy a session id from another project (right-click a Recent row), then type `/resume <that id>` and press Enter. | Sent to Claude as a message. | That session opens dormant in this window straight away. |
| 4 | Type `/resume webhook` (any word from a session title) and press Enter. | Sent to Claude as a message. | The picker opens with `webhook` already in the search. |
| 5 | Open `/resume` on a project whose sessions ran under the shared dirs. | Rows show no account. | Each row has an account chip such as "Acme Corp ▾". Clicking it lists "Resume under …" for both accounts, with ✓ on the current one. |
| 6 | From check 5, pick the *other* account. | n/a | The session opens dormant. Its footer chip's tooltip reads "Resumes under account: <other>". Send `Reply with just the word ok.` and check the overlay row's account label is the other account. |
| 7 | Open a dormant session from Recent and click its footer account chip. | Chip isn't clickable. | A menu lists both accounts with ✓ on the current one. Picking the other adds the note "will resume under …" and updates the chip. |
| 8 | In a **running** session on the shared dirs, click the footer account chip and pick the other account. | Chip isn't clickable. | A confirm dialog appears ("Switch to …?", with the cache warning). On Switch you get "switching to …", then "restarted session …", and the chip names the new account. Send `/status` and check it reports the new account. |
| 9 | Repeat check 8 while a turn is running (for example `Count slowly to 30, one number per line.`). | n/a | The dialog also warns that the turn will stop. Cancel leaves the session running untouched. |
| 10 | End a session, then click its footer chip, pick the other account, and press Resume. | n/a | Resume starts under the picked account (check with `/status`). |
| 11 | A session whose transcript is **only** in one dir (not shared). | n/a | No row chip, and the footer chip isn't clickable (tooltip has no "Click to switch"). |
| 12 | Add an account rule for the folder allowing only one org, then open a session there. | n/a | Only the allowed account is offered. If the session currently runs under a disallowed one, the menu still offers to move it to the allowed one. |

To check which `CLAUDE_CONFIG_DIR` a launch used, look at `%APPDATA%\Perch (Dev)\logs\launch.log` (a dev build;
`%APPDATA%\Perch\logs\launch.log` for an installed one). A switch writes a
`perch session switch account:` line, followed by the restart's `perch session restart:` line.
