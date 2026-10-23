# Update progress strip: manual test prompts

Manual checks for the visible in-app update flow. Clicking the update badge, the toast, the tray item or Settings → About → **Update now** used to run in the background. A couple of toasts fired, then every window closed with no progress shown. Now:

- **A progress strip under the overlay header** shows while the update runs. It reads "Checking for the update…", then "Downloading vX.Y.Z…" with a percentage and a brand-orange bar, then "Installing vX.Y.Z — Perch will restart". It shows even with the panel collapsed to its header. It doesn't show in the closed dense strip or the collapsed docked column, which have no header; the "Downloading" toast still fires for those.
- **Every way to start an update greys out while one runs.** The header's update badge fades to grey, takes no hover and ignores clicks. Settings → About disables **Update now** and **Check for updates**, and its status line follows the stage and percent. The tray item is disabled.
- **Windows and sessions close only after the download lands**, just before Velopack applies the update. They used to close up front, so a slow or failed download left you with everything torn down. A failed download now leaves windows, sessions and the "update available" state as they were, so you can retry.

The headless render (`render <dir>` → `overlay_update_progress_*.png`, `overlay_update_installing_1.5x.png`) covers the strip and the greyed badge. Only a real Velopack install covers the rest. A dev run (`dotnet run`) is "unpackaged", so the badge just explains it can't update itself.

## Quick look in a dev run (no install needed)

Run the dev tray (`dotnet run --project src/Perch.App -f net10.0-windows10.0.19041.0`), open Settings → About and click **Simulate an update** (Debug builds only). Over ~9s it plays "Checking…" (1s), "Downloading v9.9.9…" from 0 to 100%, then "Installing…" (1.5s), through the same progress path a real update uses. While it runs, the overlay strip shows, the header badge appears greyed, **Check for updates** greys out (**Update now** only exists on an installed copy, so a dev run doesn't show it), and the tray item is disabled. A second click on the simulate button while it runs does nothing. At the end everything returns to its real state. It downloads, closes and restarts nothing, so it can't show the windows closing or the restart. Use the install route below for those.

## Setup: an installed copy that's behind the latest release

The update has to start from **a build of this branch**, installed by Velopack with a version lower than the latest GitHub release. It then updates itself to that release, which doesn't have this change, but the download UI you're testing belongs to the running copy.

1. On this branch, run `publish.bat 0.5.1`. Passing the version as an argument leaves the csproj alone.
2. Run `releases\Perch-win-Setup.exe`. This replaces your installed Perch.
3. Wait for the "Update available" badge (the hourly check also runs at startup), or use tray → **Check for Updates…**.

At the end you're on the latest public release, so nothing needs undoing.

To compare against **before**, do the same from `main` (`git switch main`, `publish.bat 0.5.1`, install, update).

## 1. Start from the overlay badge

Click the orange download disc in the overlay header and confirm the stop-turns prompt if one shows.

| | Before (main) | After (this branch) |
|---|---|---|
| Under the header | Nothing | Strip: "Checking for the update…", then "Downloading v…" with a filling bar and a rising % |
| Header badge | Stays orange and clickable | Greys out; no hover wash, no hand cursor, clicks do nothing |
| Toasts | "Preparing update…", then "Downloading v…" | Only "Downloading v…" |
| Open windows and Perch sessions | Close straight away, before the download | Stay open and working through the download |
| End of download | — | Strip reads "Installing v… — Perch will restart", windows close, Perch restarts |

## 2. Settings during the download

Start the update, then open Settings → About from the tray while it downloads. On main, Settings was closed up front, so reopen it.

| | Before | After |
|---|---|---|
| Status line | "Version X is available…" | "Downloading vX.Y.Z… 37%", updating as it goes |
| **Update now** / **Check for updates** | Enabled, though clicks do nothing | Greyed out |

Also start the update **from** Settings → **Update now**. Settings should stay open, with both buttons greyed and the line counting up, until the "Installing" stage closes it.

## 3. Tray item and toast during the download

While it downloads, open the tray menu. The update item is disabled. Clicking the earlier "Update available" toast in the Action Center does nothing more; the latch in `UpdateService` was already there.

## 4. Collapsed panel and dense mode

- Collapse the floating panel to its header (click the header), then start the update. The strip still shows under the header.
- In dense mode with the strip closed, start the update from the tray. Only the "Downloading" toast shows. Hover the strip to open the popup, and the progress strip is in it.

## 5. A failed download keeps everything

Hard to do on demand. Start the update and pull the network (disable Wi-Fi or the adapter) mid-download.

| | Before | After |
|---|---|---|
| Windows and sessions | Already closed | Still open |
| Strip | — | Disappears |
| Badge / Update now / tray item | Badge still lit, but the pending version was already wiped from settings | Re-enabled, pending version kept; retrying works |
| Toast | "Update Failed" + reason | Same |

## Not covered

- **macOS.** Same code path, but the mac head's update channel hasn't been run live.
- **A very fast download.** On a fast link the bar can jump straight to 100%. That's correct, just brief.
- **The "Installing" frame.** It shows for ~300ms before Velopack takes over, so it may only flash.
