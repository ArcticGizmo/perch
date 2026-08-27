# Session control — interactive test guide

Hands-on passes for the session-control PoC (branch `session-control-poc`). These cover the items the
milestones couldn't prove headlessly: live behaviour, the valet loop, elevate/hand-back, and the
still-unproven interrupt. Use a throwaway folder as the working directory for anything that writes files.

## Setup

1. Build + run the dev tray (isolated `(Dev)` profile, won't touch an installed Perch):
   ```
   dotnet run --project src/Perch.App -f net10.0-windows10.0.19041.0
   ```
   Leave it running — launching Perch reconciles its hooks (including the new `valet` hook) into
   `~/.claude/settings.json`.
2. **Verify the valet hook registered** (dev pipe name): `~/.claude/settings.json` → `hooks.PreToolUse`
   should contain an entry whose `args` are `["valet","perch-valet-dev"]`. Start your test terminal
   session *after* this exists.
3. Use a scratch project folder, e.g. `mkdir C:\tmp\sc-test`, and run `claude` there in a normal
   terminal for the "terminal session" steps.

---

## M1 — Rich live reading (history viewer as mirror)

Open a normal terminal `claude` session, then in Perch's tray choose **Session history…**, pick the
active session in the dropdown, and click **Follow**. Give the *terminal* session prompts and watch them
render live in Perch:

- `Write a markdown summary with an H2 heading, a bulleted list, a C# fenced code block, and a table comparing three sorting algorithms.`
- `Read one source file in this project and show me a small change as a fenced diff.`
- `Run three quick shell commands in a row (echo one, echo two, echo three), then summarise.`
  — watch the tool chips append and each result stitch onto its call as it lands.

**Looking for:** headings/code/tables render richly (not flat text); new events append without the whole
panel flickering; tool results attach to the right call.

---

## M2 — Permission valet (answer a terminal session's prompts from Perch)

In the tray, click **Permission valet (PoC): off** so it reads **on**. Then, in a terminal `claude`
session running in **default** permission mode (so mutating tools would normally prompt), try:

- Allow: `Create a file valet-allow.txt containing the word hello.`
  → a Perch card appears (project · "Writing valet-allow.txt"). Click **Allow** → the file is written,
  the terminal never prompted.
- Deny: `Run the shell command: echo should-be-denied`
  → card for Bash → **Deny** → the session is told it was denied.
- Ignore / fall-through: `Create a file valet-ignore.txt containing hi.`
  → click **Ignore** (or just wait ~18s) → the card releases and the **terminal's own** prompt appears.
- Fail-open: **Quit Perch entirely**, then in the terminal `Create a file no-tray.txt.` → no delay, the
  normal terminal prompt appears immediately. (Re-launch Perch afterwards.)
- Read-only pass-through: `Read the README in this folder.` → **no** card (read-only tools are skipped).

**Known gap to observe (decision input):** with the valet **on**, ask for something that uses a tool
you've allowlisted in settings (e.g. an allowlisted `Bash(git status)`): the card **still appears**,
because the hook can't see Claude Code's own allowlist. Note how annoying/acceptable this feels — it
drives the "default-on?" decision.

---

## M4 — Elevate a terminal session into Perch, then hand it back

1. In a terminal session, teach it something: `Remember this codeword: PELICAN. Reply with just: stored.`
2. In the Perch **overlay**, right-click that session's row → **Elevate to Perch (PoC)…** → confirm.
   The terminal process is killed and the **Session console** opens, resuming the same conversation.
3. In the console, verify memory carried over: `What was the codeword I gave you? Reply with just the word.`
   → should answer **PELICAN** (same session id, one transcript).
4. Click **Hand back to terminal** in the console. A new terminal opens on the same session.
5. In that terminal: `What was the codeword again?` → still **PELICAN**.

**Looking for:** no duplicated/forked session; memory intact across both hand-offs; the overlay row for
the owned session, if clicked, brings the **console** forward (not a "No window to focus" toast).

---

## M5 — Session console directly

Tray → **Session console (PoC)…**. Set the folder, pick a model, click **Start session**.

- **Markdown rendering:** `Explain how to reverse a singly linked list, with a C# code block and a table of the time and space complexity.`
- **Inline permission bar:** with mode **default**, `Create a file console-write.txt containing hi.`
  → the Allow / Allow+accept edits / Deny bar appears above the input.
- **Live mode switch:** change the mode dropdown to **acceptEdits**, then `Create another file console-write-2.txt.`
  → it writes without prompting.
- **Queued prompts:** while a turn is running, quickly send two more prompts → the status line shows
  `… · N queued`, draining as turns complete.
- **Interrupt (the unproven one — use a slower model so the turn is long enough):** pick **sonnet** or
  **opus**, then `Write a 1500-word essay on the history of timekeeping, one paragraph at a time.`
  Hit **Interrupt** mid-stream, then `Reply with exactly: interrupted-ok.`
  → the essay should stop and the next prompt answer cleanly. (With haiku the turn finishes too fast to
  see the cancel — this is the case the headless test couldn't nail.)

---

## Quick reference — what each pass is proving

| Pass | Proves | Status before this test |
|------|--------|-------------------------|
| M1 live follow | rich reading tracks a real session | headless capture only |
| M2 allow/deny/ignore/quit | the valet loop + fail-open | e2e-verified via script, not in-app |
| M2 allowlisted-tool card | the heuristic gap (default-on decision) | known gap, unmeasured |
| M4 elevate → codeword → hand back | seamless ownership hand-off | resume verified, full round-trip not |
| M5 interrupt on sonnet/opus | turn cancellation actually works | **unproven** |
| M5 focus routing | owned-session click → console | code only |
