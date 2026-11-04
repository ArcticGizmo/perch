# Session rewind / fork / RC-after-clear: manual tests

Companion to `docs/session-rewind-plan.md`.

## RC-1 - does Remote Control follow a session after `/clear`? (before any code change)

1. Open a new Perch session in any scratch folder.
2. Send `/rc`. Open the remote-control icon's menu → **Show QR code**, and open the link on your phone or in a browser.
3. Send `say "before"`. Confirm claude.ai shows it.
4. Send `/clear`.
5. Send `say "after clear"`. **Record:** does claude.ai show "after clear"? (Expected, given the reported bug: no.)
6. Send `/rc` again. Perch reports the link; note whether it **changed**. Then use the overflow menu "⋯" → **Stop remote control**, then **Turn on remote control**. Note the link again.
7. Send `say "after cycle"`. **Record:** does the claude.ai view at the link from step 6 show it?

Report: the step-5 result, whether the link changed in step 6, and the step-7 result. A "yes" at step 7 means Perch re-links automatically after `/clear` (plan R6). A "no" means it notes the limitation instead.

## Setup for the rewind and fork tests

Use a **new** Perch session (one started after this build), in a scratch git folder containing a file `notes.txt` with the line `alpha`. Sessions started before this build have no file checkpoints, so their code rewind reports "can't be rewound here". That is expected.

Send these three prompts, waiting for each to finish:

1. `Append the line bravo to notes.txt`
2. `Append the line charlie to notes.txt`
3. `What lines are in notes.txt? Answer from memory, use no tools.`

## RW-1 - opening rewind mode

- **Before:** the composer was the only way to act on a past prompt.
- Hover prompt 2's bubble. A ↺ button shows beside the copy button. Click it.
- **After:** the composer is replaced by the rewind bar.
  - A "↺ rewind to here" line sits above prompt 2, which is outlined. Prompts 2 and 3 and their replies are dimmed.
  - The bar reads "Rewind to before "Append the line charlie…" · drops 2 prompts".
  - A moment later the code line reads `code: 1 file · +1 −0` (hover it for the path).
- ↑/↓ moves the line between prompts, Home/End jump to the first/last, and clicking any prompt moves it there. Esc closes the bar and brings the composer back.
- Also check that `/rewind`, Esc Esc in an empty composer, and "⋯" → **Rewind…** each open the same mode.

## RW-2 - rewind conversation + code

- Open rewind on prompt 2 and press Enter (or click **Rewind conversation + code**).
- **After:**
  - `notes.txt` is back to `alpha` and `bravo`.
  - The thread ends with prompt 1's reply, a "restored 1 file…" note and a "rewound to before…" marker.
  - The composer holds prompt 2's text.
  - Send prompt 3 again: the answer lists alpha and bravo only.
- Close and reopen the window (or restart Perch and reopen the session): the history shows only the kept branch, not the dropped prompt 2/3.

## RW-3 - conversation only / code only

- Repeat the setup.
- **Conversation only** on prompt 2: `notes.txt` keeps `charlie`, and the thread is cut as in RW-2.
- Repeat the setup again. **Code only** on prompt 2: `notes.txt` loses `charlie`, the thread is untouched, and a "restored…" note appears.

## RW-4 - first prompt and dormant sessions

- Rewind to before prompt 1: the conversation is cleared (a `/clear`) and prompt 1 is back in the composer.
- Restart Perch so the session comes back dormant. Open rewind on prompt 2.
  - The code line says to start the session first, so only **Rewind conversation** and **Fork from here** are offered.
  - Rewind, then send: the session wakes truncated.

## FK-1 - fork

- **Fork from here** on prompt 3:
  - A second window opens with prompts 1 and 2 (and replies), "forked from session …", and prompt 3's text in its composer.
  - Send it. The original window is unchanged and still works.
- "⋯" → **Fork into a new session** (or `/fork`): a new window with the whole conversation.
- Both sessions show as separate rows in the overlay.

## RC-2 - Remote Control survives a rewind

- Turn on `/rc`, then rewind the conversation.
- **After:** a "remote control on …" note follows the restart, and the composer's remote-control icon stays lit.
