# Restart session: manual test prompts

Manual checks for **Restart session**, which stops a Perch-controlled session's `claude` process and starts
`claude --resume` on the same id in the same window. It adds:

- **A split End session button.** The session window's bar shows `End session | ▾`. The ▾ opens a menu with
  **Restart session**. The ▾ half only shows while the process is running. A dormant ("not running") session shows
  plain End session, because its next send starts a fresh process anyway.
- **A Restart button on the sign-in banner.** It shows when the banner is warning about *this process's* access token
  (under 5 min left, the point an interactive Claude Code would refresh it, or lapsed). A headless session never refreshes or re-reads its token, so a restart is what
  picks up a fresh one. A banner that only warns about the account login (`/login`) has no button, because
  restarting doesn't fix that.
- **In-place restart (`PerchSession.Restart`).** It's the same `PerchSession` object, so the window, overlay row,
  Roost pane and recovery ledger all stay attached. Neither `Ended` nor `Woke` fires. The new process starts with the
  session's current model, permission mode, effort and account (config dir). The thread gets "restarting session …",
  "session ended", then "restarted session …" notes. If the relaunch fails, the session ends normally with an
  error note, and Resume is offered.
- **Confirm only when busy.** An idle session restarts on one click. A turn in flight, a pending permission or a
  running background task asks first, since the restart stops them.

The headless render (`render <dir>` → `session_auth_*.png`) covers the banner button and the split button. Only a
real tray covers the restart itself.

## Setup

Run the dev tray: `dotnet run --project src/Perch.App -f net10.0-windows10.0.19041.0`. Open a new Perch session
in any trusted folder and send one prompt so it's running.

## 1. Split button and menu

| | Before | After |
|---|---|---|
| Bar, running session | `End session` | `End session` with a `▾` joined on its right |
| Click `▾` | — | Menu with **Restart session** |
| Click `End session` (left half) | Confirm "End this session?" | Unchanged |
| Dormant session (Recent → open, don't send) | `End session` | Plain `End session` with rounded corners and no `▾` |

## 2. Restart an idle session

Send a prompt like `remember the word pelican`, wait for it to finish, then ▾ → **Restart session**.

| | Expected |
|---|---|
| Confirmation | None (it's idle) |
| Thread | "restarting session xxxxxxxx…", then "session ended", then "restarted session xxxxxxxx" |
| During the gap (a few seconds) | Composer disabled, placeholder "Restarting the session…", `▾` hidden, End session still shown |
| After | Composer enabled. Model, mode and effort pills unchanged. Account chip unchanged |
| Then send `what word did I ask you to remember?` | "pelican". It's the same conversation |
| Overlay row / Roost pane | Same session, back to idle (the row may blink out briefly) |
| `logs/launch.log` | A `perch session restart:` line, then a `controlled session start: … resume=True id=<same id>` line |

## 3. Restart mid-turn

Send `count slowly to 50, one number per line, using a bash sleep 1 between each`, then ▾ → **Restart session**
while it runs.

| | Expected |
|---|---|
| Confirmation | "Restart this session?" with Restart / Keep running |
| Keep running | Nothing happens, and the turn carries on |
| Restart | The running tool card settles as failed and the restart proceeds as in section 2 |

## 4. End during a restart

Start a restart, then click **End session** straight away while the placeholder still reads "Restarting the
session…". Confirm it.

Expected: the window closes, no new process starts, and the overlay row goes. Check Task Manager: no leftover `claude` for that session.

## 5. Sign-in banner Restart button

The banner needs this process's access token within 5 min of expiry. Either:

- leave a session running until about 5 min before its token runs out (tokens last about 8 h from spawn), or
- temporarily raise `AuthExpiryRules.SessionAccessSoon` (e.g. to `TimeSpan.FromHours(9)`) in a dev build. **Revert it
  afterwards.**

| | Before | After |
|---|---|---|
| Banner text | "...End the session, then Resume it to pick up a fresh sign-in." | "...Restart the session to pick up a fresh sign-in." |
| Banner | Text only | A **Restart** button on the right |
| Click Restart (idle session) | — | Restarts straight away, as in section 2. The banner disappears, and comes back only if the new process's token is also near expiry |
| Banner warning only about the login (`/login`) | — | No Restart button |

If a restarted session's token is still the old one (the banner comes straight back), nothing refreshed the
credentials on disk. Open Claude Code in a terminal once to refresh them (the banner's tooltip says so), then
restart again.

## Not covered

- **Roost panes.** They have no End/Restart controls of their own. A restart started from the window just shows up
  in the pane as the session going briefly idle.
- **Perch exiting mid-restart.** App exit calls `End()` on every session, which cancels the pending relaunch, and
  the session parks as "not running" as usual. This hasn't been exercised live.
