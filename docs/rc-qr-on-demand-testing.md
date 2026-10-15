# /rc toggles remote control, QR on demand: manual testing

The change:

- `/rc` (`/remote-control`) is a **toggle**. The first one turns remote control on, and a second one turns it
  off. It used to re-show the QR when it was already on.
- Turning it on (via `/rc` or the composer's "…" → "Turn on remote control") **no longer pops the QR card**.
  The thread note carries the link and says where the QR is.
- You get the QR on demand from the remote-control glyph's menu → "Show QR code" (or, from the overlay, a
  remote-controlled row's right-click → "Show QR code").
- The QR card now opens **centred on the monitor of the window you asked from**: the session window, or the
  overlay. It used to always open on the primary monitor.

## How to run each build

- **Before (main):** check out `main`, then `dotnet run --project src/Perch.App -f net10.0-windows10.0.19041.0`
- **After (branch `fixes`):** same command on `fixes`.

Quit any installed Perch first (tray → Exit). Each scenario needs a running Perch session window (New
session). **Note:** enabling remote control registers the session with claude.ai under the session's account.
Make sure it's off at the end.

| # | Steps | Before (main) | After |
| --- | --- | --- | --- |
| 1 | In a running session, type `/rc` and send. | Note "remote control on — <url>" **and the QR card pops up**. | The note (pointing at the remote-control icon for the QR) appears, and **no QR card**. The remote-control glyph lights up. |
| 2 | With it on, type `/rc` again. | The QR card shows, and it stays on. | Note "remote control off". The glyph goes out. |
| 3 | Turn it on, then click the remote-control glyph → "Show QR code". | The QR card shows. | The QR card shows. |
| 4 | **Two monitors:** move the session window to the **secondary** monitor, then do 3. | The card opens on the **primary** monitor. | The card opens **centred on the secondary** monitor. |
| 5 | Same as 4, with a mixed-DPI pair (e.g. a 150% laptop + a 100% external), both ways round. | Primary. | Centred on the session's monitor and fully on screen, with no jump after it appears. |
| 6 | **Overlay path:** with the overlay on the secondary monitor, right-click a remote-controlled session row → "Show QR code". | Primary. | Centred on the overlay's monitor. |
| 7 | Composer "…" → "Turn on remote control". | The QR card pops up. | No QR card, just the note. |
| 8 | Click the link in the note. | Opens the claude.ai session. | Opens the claude.ai session. |

If the card briefly appears elsewhere and then jumps (5), that's the `Opened` fallback in `QrWindow.CentreOn`
kicking in. It means Avalonia's `CenterScreen` didn't honour the pre-set position, so please note it.
