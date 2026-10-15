# /rc no longer pops the QR: manual testing

The change: turning on remote control (`/rc` / `/remote-control`, or the composer's "…" → Remote control) no
longer opens the QR card. The thread note now ends "(run /rc again for a QR code)". You can still get the QR
on demand by running `/rc` again while it's on, or from the remote-control glyph's menu → "Show QR code".

## How to run each build

- **Before (main):** check out `main`, then `dotnet run --project src/Perch.App -f net10.0-windows10.0.19041.0`
- **After (branch `fixes`):** same command on `fixes`.

Quit any installed Perch first (tray → Exit). Each scenario needs a running Perch session window (New
session). **Note:** enabling remote control registers the session with claude.ai under the session's account.
Stop it afterwards (glyph → Stop remote control).

| # | Steps | Before (main) | After |
| --- | --- | --- | --- |
| 1 | In a running session, type `/rc` and send. | Note "remote control on — <url>" **and the QR card pops up**. | The note (with the "run /rc again" hint) appears, and **no QR card**. The remote-control glyph lights up. |
| 2 | With it on, type `/rc` again. | The QR card shows. | The QR card shows. |
| 3 | With it on, click the remote-control glyph → "Show QR code". | The QR card shows. | The QR card shows. |
| 4 | Composer "…" → "Turn on remote control". | The QR card pops up. | No QR card, just the note. |
| 5 | Click the link in the note. | Opens the claude.ai session. | Opens the claude.ai session. |
| 6 | Glyph → Stop remote control. | Note "remote control off". | Note "remote control off". |
