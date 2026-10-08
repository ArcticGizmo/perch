# Remote Control sync - manual test script

A live check for P1 + P2 of `docs/remote-control-sync-plan.md` (branch `remote-control-sync`). Each test below
can be run on its own in a fresh Perch session. The prompts are written to trigger one specific thing and nothing
else, so that one result can't hide another.

## Setup (once)

1. Make a scratch folder for the session to work in, e.g. `C:\scratch\rc-test`. Nothing in it matters.
2. Start the dev build with the raw stream log on (PowerShell, from the repo root):
   ```powershell
   $env:PERCH_SESSION_LOG = '1'
   dotnet run --project src/Perch.App -f net10.0-windows10.0.19041.0
   ```
   The log goes to `%APPDATA%\Perch (Dev)\logs\session-stream.log`.
3. Open a new Perch session in the scratch folder with permission mode **default**. Other modes skip the
   permission prompts these tests depend on.
4. Run `/rc` in the session and scan the QR code with your phone, or open the link on claude.ai.
5. **Bash approvals stick for the session.** Every prompt below uses a new file name so it asks again. If one
   doesn't ask, start a fresh session.

**Before test 1, check P2 is active at all:** after the first turn, search the log for `session_state_changed`.
If it isn't there, the CLI ignored the environment variable and none of the P2 checks below can pass. Stop and
send me that.

---

## Test 1 - Baseline, Perch only (no phone)
Checks nothing regressed and the state events arrive.

**Type in Perch:**
```
Use the Bash tool to run exactly this command and nothing else: echo baseline > rc-test-1.txt
Then reply with just the word DONE.
```
**Do:** Allow the prompt in Perch.

**Expect:**
- [ ] Overlay goes Busy → Waiting (while the card is up) → Busy → Idle
- [ ] Card settles as "Allowed Bash"
- [ ] Log shows `session_state_changed` with `running`, `requires_action`, then `idle`

## Test 2 - Permission answered on the phone (P1)
The main bug: the card used to stay stuck.

**Type in Perch:**
```
Use the Bash tool to run exactly this command and nothing else: echo remote-allow > rc-test-2.txt
Then reply with just the word DONE.
```
**Do:** When the prompt appears, **allow it on the phone**. Don't touch Perch.

**Expect:**
- [ ] Perch's card changes to "↗ Answered remotely for Bash" with no buttons
- [ ] Overlay leaves Waiting and goes Busy, then Idle once "DONE" arrives
- [ ] The session window's status line doesn't stay on "awaiting you"
- [ ] Log shows a `control_cancel_request` whose `request_id` matches the `can_use_tool` request

## Test 3 - Permission denied on the phone (P1)
**Type in Perch:**
```
Use the Bash tool to run exactly this command and nothing else: echo remote-deny > rc-test-3.txt
If the command is denied, reply with just the word DENIED.
```
**Do:** **Deny on the phone.**

**Expect:**
- [ ] Card shows "Answered remotely" (Perch can't tell allow from deny; that's expected)
- [ ] Claude replies DENIED and the overlay ends Idle

## Test 4 - Question answered on the phone (P1, AskUserQuestion)
**Type in Perch:**
```
Use the AskUserQuestion tool to ask me one question: "Which colour?" with the options Red and Blue.
After I answer, reply with just the colour I picked.
```
**Do:** Answer the question **on the phone**.

**Expect:**
- [ ] Perch's question card shows "↗ Answered remotely" and its options disappear
- [ ] Claude replies with the colour you picked; overlay ends Idle

## Test 5 - Turn started from the phone in a brand-new session (P2)
The case that used to read idle while Claude worked: nothing has been sent from Perch yet.

**Setup:** a **fresh** Perch session, `/rc` on, and **nothing typed in Perch**.

**Type on the phone:**
```
Without using any tools, write the numbers from 1 to 150, one per line, then the word FINISHED.
```
**Expect:**
- [ ] Perch's overlay goes Busy while it writes and Idle after FINISHED
- [ ] The session window shows the "working" row while it runs
- [ ] (Not fixed yet, P4) Your phone prompt doesn't appear as a user bubble in Perch; only the reply does

## Test 6 - Phone turn that needs a permission, answered in Perch (P2 + normal path)
**Type on the phone:**
```
Use the Bash tool to run exactly this command and nothing else: echo from-phone > rc-test-6.txt
Then reply with just the word DONE.
```
**Do:** Allow it **in Perch** this time.

**Expect:**
- [ ] Perch shows the card while the overlay reads Waiting
- [ ] Allowing in Perch works; the phone's prompt goes away; Perch ends Idle

## Test 7 - Interrupt from the phone (P2)
**Type in Perch:**
```
Use the Bash tool to run exactly: powershell -NoProfile -Command "Start-Sleep -Seconds 60; 'slept' | Out-File rc-test-7.txt"
Then reply with just the word DONE.
```
**Do:** Allow in Perch. While it sleeps, **stop/interrupt the turn from the phone**.

**Expect:**
- [ ] Perch leaves Busy within a second or two of the interrupt (Idle, no stuck "working…")
- [ ] Interrupt button disappears in the session window

## Test 8 - Prompt left open, then interrupted from the phone (P1/P2 stale-card path)
**Type in Perch:**
```
Use the Bash tool to run exactly this command and nothing else: echo never > rc-test-8.txt
```
**Do:** Leave the card unanswered. **Interrupt from the phone.**

**Expect:**
- [ ] Perch's card settles as "Answered remotely" or "Expired" (either is fine), not still Pending
- [ ] Overlay ends Idle, not Waiting
- [ ] Clicking anywhere on the old card does nothing

## Test 9 - Regression, Remote Control off
**Setup:** a fresh session, **no `/rc`**.

**Type in Perch:**
```
Use the Bash tool to run exactly: powershell -NoProfile -Command "Start-Sleep -Seconds 15"
Then reply with just the word FIRST.
```
**Do:** Allow it, and while it sleeps send a second prompt:
```
Reply with just the word SECOND.
```
**Expect:**
- [ ] Status line shows "1 queued" during the sleep
- [ ] Both FIRST and SECOND arrive and the session ends Idle (not stuck "working…", no lingering "queued")

---

## Send back
- Which checkboxes failed, and on which test
- The log lines around any failure: search `session-stream.log` for `control_cancel_request`,
  `session_state_changed` and `can_use_tool`. The log holds your prompts, so trim anything private.
- Turn the log off afterwards: close Perch, or start it without `PERCH_SESSION_LOG`
- Close the Remote Control bridge when you're done: click the composer's Remote Control glyph and choose
  "Stop remote control", or end the session
