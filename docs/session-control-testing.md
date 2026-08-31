# Session control — interactive test guide

Hands-on passes for the session-control PoC (branch `session-control-poc`). **The primary dogfood
surface is now the embedded ConPTY terminal** (tray → "Session terminal (PoC)…") — a real `claude` TUI
inside Perch you can drive from the terminal or from a Perch input box. The stream-json chat console and
the permission valet are secondary/parked; the terminal is what to exercise first.

## Setup

Build + run the dev tray (isolated `(Dev)` profile, won't touch an installed Perch):
```
dotnet run --project src/Perch.App -f net10.0-windows10.0.19041.0
```
Use a scratch project folder for anything that writes files, e.g. `mkdir C:\tmp\sc-test`.

---

## THE headline test — embedded terminal (dogfood this first)

Open it either way:
- **Overlay:** click the **"+ New session"** row at the top of the session list (works even with no
  sessions running), or
- **Tray:** **Session terminal (PoC)…**.

Set the folder (or accept the default) — a real `claude` starts in an embedded terminal.

- **It's a real terminal:** type in it directly, exactly as you would in Windows Terminal — `hi, what can you do?`,
  arrow keys, Ctrl+C, `/help`, the lot. The full TUI should render (colour, the input box, spinners).
- **Prompt from Perch:** type into the **bottom input box** (not the terminal) and press Enter, e.g.
  `Write a haiku about pseudo-consoles.` → it appears in the terminal as if you typed it, and claude answers.
- **Both at once:** alternate — a prompt typed in the terminal, then one from the Perch box — same session.
- **Exit handling:** type `exit` (or finish claude) → the status shows "claude exited (N)" and the
  button flips to **Restart claude**.

**Looking for:** the TUI renders and is fully interactive; the Perch input box injects prompts into the
same session; no separate console window pops up.

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

## Elevate an existing terminal session into Perch

The escape-character problem you hit is addressed by resuming into the embedded terminal instead of the
chat console.

1. In a normal external terminal, run `claude` and teach it something:
   `Remember this codeword: PELICAN. Reply with just: stored.`
2. In the Perch **overlay**, right-click that session's row → **Elevate to Perch (PoC)…** → confirm.
   The external process is stopped and an **embedded Session terminal** opens, resuming the session.
3. In the embedded terminal (or the Perch box): `What was the codeword I gave you?` → **PELICAN**
   (same session, one transcript).

**Looking for:** the resumed session works in the embedded terminal; the overlay row for an owned
session brings its Perch window forward. **Known residue:** the *external* terminal window is still
killed and may show teardown escapes — that's unavoidable when killing a TUI in someone else's terminal;
starting sessions in Perch's terminal from the outset avoids it entirely.

---

## Secondary — the stream-json chat console (M5)

Tray → **Session console (PoC)…** is the alternative rich-chat surface (not a terminal). Set the folder,
pick a model, click **Start session**.

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
| **Embedded terminal (headline)** | real claude TUI in Perch + prompt-from-Perch | dep+compile verified; render owed |
| Elevate → codeword | resume into the embedded terminal | resume verified headlessly, full flow owed |
| M1 live follow (history) | rich reading tracks a real session | headless capture only |
| Console markdown/queue | rich chat surface | headless capture only |
| Console interrupt on sonnet/opus | turn cancellation actually works | **unproven** |

The permission valet is **parked** — no tray toggle, dormant. Ignore it for now.
