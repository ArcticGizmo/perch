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

## W3 - the session protocol
Nothing to check by hand yet: the controller doesn't connect to its workers until W4, which adds the sessions to its
overlay. `RemoteSessionTests` cover the protocol end to end over loopback.

## Send back
Which checks failed, and anything odd in the wording. No logs are needed for W2.
