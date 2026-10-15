# Mic strip "jump to call": manual testing

The fix: clicking the overlay's microphone strip should land on the **Teams meeting window**, not the Teams
hub (Chat/Calendar/Activity), regardless of which Teams window you touched last.

## Why it was flaky

The mic stream belongs to Teams' windowless media child process, so `FocusAppWindowForProcess` widens the
search to every `ms-teams.exe`. The hub and the meeting window both live in the same UI process, so they
tied, and the tie went to Z-order: whichever Teams window you used most recently. Go to Chat during a call,
then click the strip, and you got the hub.

Now the mic monitor remembers which window of the app was **in front when the app first took the mic**
(`MicCallWindowTracker` → `MicUser.CallWindow`). That's the meeting window, because you press Join there. The
activator focuses that window first. It only does so while the window still exists and still belongs to the
app, and otherwise falls back to the old Z-order rule.

## How to run each build

- **Before (main):** `git stash` (or check out `main`), then
  `dotnet run --project src/Perch.App -f net10.0-windows10.0.19041.0`
- **After (this change):** the working tree with the change applied, same command.

Quit any installed Perch first (tray → Exit) so the single-instance guard doesn't hand off to it. The mic
strip has to be on (Settings → search "microphone").

## Scenarios

You need a real Teams meeting. A solo "Meet now" works and costs nothing. Join it from the **pre-join
screen** as usual, and leave it **in its own window** (the default).

| # | Steps | Before (main) | After |
| --- | --- | --- | --- |
| 1 | Join a meeting. Click the Teams hub, then open Chat. Switch to some other app. Click the mic strip. | Lands on the **hub** (Chat), because it was the last Teams window used. | Lands on the **meeting window**. |
| 2 | Join a meeting. Switch straight to another app (don't touch the hub). Click the mic strip. | Meeting window (it was last used, so this already worked). | Meeting window. |
| 3 | As 1, but put the meeting window on **another virtual desktop** first, and click from this one. | Switches desktop to wherever the hub is. | Switches to the meeting's desktop and focuses it. |
| 4 | Join a meeting, then **minimize** the meeting window (Teams shows its compact call view). Go to the hub. Click the strip. | Hub. | The meeting window is restored. |
| 5 | Leave the meeting and join a second one. Repeat 1. | Hub. | The **new** meeting window. The old window is forgotten when the mic is released. |
| 6 | **Known limit:** join the meeting first, *then* start Perch (or switch the strip on mid-call). Repeat 1. | Hub. | Hub. The call started before Perch was watching, so the window that was in front back then is unknown, and it falls back to most-recently-used. Leave and rejoin to re-learn it. |
| 7 | **Known limit:** join with the mic auto-started while another app is in front (e.g. auto-join from a calendar reminder and you click away within ~2s). | Hub or meeting (Z-order). | Same as before. Nothing is learned if a different app was in front when the mic went live. |
| 8 | Any non-Teams caller with one window (Zoom, a browser tab with Meet). Click the strip. | Its window. | Its window (no change). |

## What isn't covered

- Unit tests cover the bookkeeping (`MicCallWindowTrackerTests`). The Win32 half (reading the foreground
  window and validating/focusing the handle) has only been compile-checked, not run against a live meeting.
- With Teams' "open meetings in the main window" setting, the meeting *is* the hub, so both builds land on it
  and that's correct.
