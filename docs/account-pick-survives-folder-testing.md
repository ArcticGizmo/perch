# Account pick survives a folder change: manual test prompts

Manual checks for the new-session launcher's account selector. Changing the folder used to re-resolve the account choices **and** throw away whatever account you'd already picked, so picking the account first and the folder second always snapped the account back to the first option (the primary). Now the pick is kept unless the new folder is **restricted** by an account guardrail, in which case the guardrail's default applies as before.

The change is in `SessionWindow.RefreshAccounts`. The UI has no automated coverage.

## Setup

Run the dev tray: `dotnet run --project src/Perch.App -f net10.0-windows10.0.19041.0`. You need at least two config dirs signed in (so the account pill shows), and one folder with an account guardrail (Settings → account guardrails) that allows only one of them.

## 1. Account first, then an unrestricted folder

Open **+ New session**. Pick the second (non-default) account from the pill, then choose an unrestricted folder.

| | Before | After |
|---|---|---|
| Account pill after choosing the folder | Back to the first account `(default)` | Still the account you picked |
| Send the first prompt, then check the session's account chip | The primary | The picked account |

## 2. Account first, then a restricted folder

Pick the account the guardrail does **not** allow, then choose the restricted folder.

| | Expected (unchanged) |
|---|---|
| Account pill | The guardrail's allowed account. Locked (padlock, no chevron) when it allows exactly one |

Then pick an account from that folder's list (when it allows more than one) and change to an unrestricted folder: the pick from the restricted folder carries over.

## 3. Folder first, then account

Choose any folder, then pick an account.

| | Expected (unchanged) |
|---|---|
| Account pill | The picked account, and the session starts under it |

## Not covered

- **Typing a path by hand.** The folder is re-resolved as you type, so typing *through* a restricted folder on the way to an unrestricted one below it (a nested rule that lifts the restriction) can reset the pick, if the re-resolve for an intermediate (restricted) path lands before the next keystroke supersedes it.
