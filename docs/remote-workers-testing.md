# Remote workers - manual test script

Live checks for `docs/remote-workers-plan.md`. Grows with each phase; W2 (pairing and the listener) is all there is
to see so far: no sessions travel yet.

## Setup

Two machines that can reach each other (same network, or both on Tailscale), each running a dev build of this branch:
```powershell
dotnet run --project src/Perch.App -f net10.0-windows10.0.19041.0
```
Below, **W** is the machine that will be the worker and **C** the controller. On one machine only, the dev and release
builds can play W and C (they keep separate keys and settings), as long as W's port isn't taken by the other.

## W2-1 - The listener is off until asked
On W: Settings → Workers.
- [ ] Status reads "Off"; "Pair a controller…" is disabled
- [ ] `Get-NetTCPConnection -LocalPort 41540 -State Listen` finds nothing

Turn on **Let paired machines control this one**.
- [ ] Windows asks to allow Perch through the firewall (first time only). Allow on private networks
- [ ] Status reads "Listening on port 41540", and the port shows as listening
- [ ] The key fingerprint shows four groups of hex

Turn it off again.
- [ ] Status "Off", the port is closed

## W2-2 - Pairing
On W: turn the toggle on and click **Pair a controller…**.
- [ ] "Pairing is open for 1:59…" counts down; the button reads "Stop pairing"

On C: Settings → Workers → under "Machines this one controls", enter W's name or IP (Tailscale name works), keep port
41540, click **Pair**.
- [ ] C shows "Code NNN NNN. Check W shows the same code, then click Allow there."
- [ ] W shows "Let C control this machine?" with the **same** code

Click **Allow** on W.
- [ ] C: "Paired with W", and W appears in "Machines this one controls" with its address and key fingerprint
- [ ] W: C appears in "Machines that can control this one"; pairing has closed

## W2-3 - Declined, closed and stranger cases
- [ ] Open pairing on W, click Pair on C again, click **Don't allow** on W → C reads "Not paired: pairing was declined…"
- [ ] With pairing closed on W, click Pair on C → "Not paired: W refused: open pairing on it first"
- [ ] Let pairing time out on W (2 minutes) → the line disappears and the button returns to "Pair a controller…"

## W2-4 - Remove and reset
- [ ] On W, Remove C → it leaves the list. (C still lists W; it simply won't get in until they pair again.)
- [ ] On C, "Reset this machine's key…" → Reset: both lists empty, the fingerprint changes

## W2-5 - Settings
- [ ] Set a listen address that isn't this machine's (e.g. `10.255.255.1`) → status says it couldn't listen
- [ ] Set it back to blank → listening again
- [ ] Change the port to 41541 → status follows; the old port closes
- [ ] Search "worker" in Settings finds the toggle, port, address and machine name

## W3/W4 - driving a worker's session
Pair W and C first (W2-2). On W keep **Let paired machines control this one** on, and start a Perch session in some
folder (`+ New session`). On C you don't start anything.

### W4-1 - The link and the rows
- [ ] C: Settings → Workers lists W as "Connected · 1 session"
- [ ] C's overlay has a row for W's session, with the Perch mark, its project/title, and "on W" underneath
- [ ] Hovering the Perch mark says "Perch session on W"
- [ ] On W, start a second Perch session → a second row appears on C within a couple of seconds
- [ ] Right-click the row on C → only **Open** and **Copy session ID**

### W4-2 - Catching up and driving it
Before you open it on C, give W's session a turn so it has history: ask it to `list the files here`.

Click the row on C.
- [ ] A session window opens titled "<project> — on W", path line ending "on W"
- [ ] The thread shows the earlier prompt and answer, tool cards included
- [ ] No End session button, no changed-files toggle, no branch pill; ⋯ offers only Copy resume command and New session

Prompt from C: `write the word hello to a file called remote-check.txt`.
- [ ] The prompt shows once on C, and on W's window too
- [ ] The answer streams on both; C's row reads "running" while it works
- [ ] The Write permission card shows on both. Click **Allow** on C → W's card settles as allowed, and the file appears on W
- [ ] On the next prompt, deny on **W** → C's card settles as denied
- [ ] Interrupt from C mid-answer → the turn stops on both
- [ ] Switch mode to Plan from C's pill → W's pill follows. Try Bypass permissions from C → a note says it can only be
  turned on at W, and W's mode doesn't change
- [ ] `/rewind` on C → a note says it works at W

### W4-3 - Closing, ending, dropping
- [ ] Close C's window → W's session keeps running; click the row again → the window comes back caught up
- [ ] End the session on W → C's row disappears; an open window on C shows the session ended
- [ ] Turn W's toggle off (or quit Perch on W) → C's rows fade to "offline"; an open window says it lost the
  connection. Settings → Workers on C says Offline with the reason
- [ ] Turn it back on → within a minute C reconnects and the rows come back

### W4-5 - The Roost
Open the Roost on C (with W's session running).
- [ ] W's session has a pane and a rail entry; its header reads "◆ Perch · W"
- [ ] Type a reply in the pane → it runs on W; a permission card in the pane answers with Enter (allow) / Esc (deny)
- [ ] Esc while it works interrupts it
- [ ] "Open window" (or the pane menu's Open full window) shows the same conversation in a session window
- [ ] Close that window → the pane keeps streaming. Close the Roost too → reopen the row: it catches up again
- [ ] Take W offline → the pane's pill says "Offline" and its composer goes; bring W back → the pane streams again
  without reopening the Roost
- [ ] End the session on W → the pane greys to "Ended" and drops after ten minutes

### W4-4 - Mid-session attach
- [ ] On W, start a long answer (`count slowly to 200, one number per line`). While it runs, open the row on C → C shows
  what's already there and keeps streaming the rest

## W5 - What the worker sees
With C attached to W's session (open its row on C):
- [ ] W's session window shows "⇄ controlled from C" in the bar; W's overlay row reads "controlled from C"
- [ ] W's tray menu has "Disconnect C"
- [ ] Prompt from C → on W the bubble has "↗ from C" under it
- [ ] Allow a permission card from C → W's thread gets a quiet "C allowed Bash"; deny one → "C denied …"
- [ ] Interrupt from C mid-turn → W shows "C interrupted"
- [ ] Switch mode to Plan from C → W shows "C switched the permission mode to plan"; model/effort from C → "(from C)"
- [ ] Close C's window (and C's Roost) → within a moment W's chip and row mark go, and the tray item goes

### W5-2 - Disconnecting
- [ ] Click W's chip → Disconnect C → C's window says it lost the connection; W's chip goes
- [ ] C's Settings → Workers says W is offline: "W disconnected this machine; it can reconnect after HH:MM"
- [ ] W's Settings shows C as "Disconnected by you · can reconnect after HH:MM"
- [ ] Opening the row on C in that time says W is offline; after the time passes C reconnects by itself
- [ ] The overlay row menu's and the tray's Disconnect do the same

## W6 - Hardening
- [ ] Leave C attached to an idle session on W for five minutes → it stays connected (the heartbeat keeps it up)
- [ ] Pull W's network cable (or turn off its Wi-Fi) with C attached → within about a minute C's rows go "offline" and
  C's Settings says "no answer for 60 seconds". Reconnect W → C is back within a minute
- [ ] Put W to sleep with C attached → same as above; wake it → C reconnects
- [ ] Put C to sleep for a few minutes, wake it → C reconnects to W within about 15 seconds of waking
- [ ] (If you have two builds of different versions) pair them → the message names both Perch versions

The handshake slot caps are covered by `RemoteWorkerTests.StalledHandshakes_FromOneAddress_AreCapped`.

## Send back
Which checks failed, and anything odd in the wording. No logs are needed for W2.
