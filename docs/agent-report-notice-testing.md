# Agent report notice: manual test prompts

Manual before/after checks for branch `fix/subagent-handback-idle`. It contains:

- `3647e3e`: keep sub-agents "working" through a thinking-only record (the session that went idle before its hand-back).
- The agent report notice (see [`agent-report-notice-plan.md`](agent-report-notice-plan.md)): reports as expandable
  cards, sub-agent work kept off the parent thread, and reports in transcript search.

Unit and replay tests cover the parsing (`AgentReportTests`, `StreamJsonReplayTests`, `SubAgentReaderTests`,
`TranscriptReaderTests`). These prompts cover what only a live session shows: timing, the overlay and the window.

## Running before and after

Perch is single-instance, so quit the tray app before switching builds.

- **Before:** your installed Perch, or a build of `main`:
  `git checkout main` then `dotnet run --project src/Perch.App -f net10.0-windows10.0.19041.0`
- **After:** `git checkout fix/subagent-handback-idle`, same command.

Run each prompt in a **new Perch session** (the session window, not a terminal). Use a scratch folder or any repo
you don't mind it reading; nothing below writes files. The prompts launch background agents on your account, so
each run costs a few cents.

## 1. Session stays "running" while an agent writes its report (the idle gap)

The bug needs the parent to be idle while the agent spends a long time writing its final report. Paste:

```text
Launch exactly one Explore agent in the background with this prompt: "Look through the files in this repository,
then think carefully and write a detailed report of at least 1,200 words on how it is structured, with headings, a
bullet list and a table. That report is your final answer." Do not wait for it: end your turn immediately by saying
only "waiting". When its report arrives, reply with one sentence summarising it.
```

Watch the overlay row and the window's running-agent chip during the agent's last 30–60 seconds (it has finished
exploring and is writing).

| | Before | After |
|---|---|---|
| Overlay row | Flips to **idle** while the report is being written, then back when the hand-back lands | Stays **running** until the parent's reply finishes |
| Agent chip / sub-row | Disappears early | Stays, reading **Thinking…** |

If the agent writes its report quickly the gap is too short to see; rerun and ask for a longer report. The gap
appears both with `SubagentHandback` (enabled per session) and with plain final text.

## 2. The agent's own work stays out of the parent's thread

```text
Launch one general-purpose agent in the background with this prompt: "Run git log --oneline -5 and git status,
then reply with a three-bullet summary of the repository's state." Do not wait for it: end your turn by saying only
"waiting". When it finishes, reply with just "got it".
```

| | Before | After |
|---|---|---|
| Thread while the agent runs | The agent's `Bash` cards and its bullet summary appear as a **Claude message** in the parent thread | Nothing from the agent appears; only the parent's "waiting" and "got it" |
| The finish notice | The agent's **whole reply** printed as a centred mono line | `✓ Agent "…" finished · 12s` card with `Report · <first line>` |
| Click the card | n/a | Expands to the bullets rendered as Markdown; hover shows a copy button; click again collapses |

Also check that the agent tabs still show the agent's own work. They read its transcript, so nothing is lost.

## 3. The context bar doesn't jump to the agent's size

```text
Launch one general-purpose agent in the background with this prompt: "Read the five largest source files in this
repository in full, then reply with one line naming the largest." Do not wait for it: end your turn by saying only
"waiting". When it finishes, reply with just "noted".
```

| | Before | After |
|---|---|---|
| Composer context bar while the agent reads | Jumps to the **agent's** context size, then snaps back on the parent's next reply | Holds the parent's own size throughout |

## 4. History shows reports

Open **History** and pick a session that used background agents:

- The Safety session from 2026-10-13 06:23 (`10b25140`): its agents used `SubagentHandback`.
- Any session from prompts 2–3: their agents ended with plain text.

| | Before | After |
|---|---|---|
| Each agent notice | `✓ Agent "Survey …" finished`, no report | The same line as a card, with `Report · <heading>`, which expands to the full report |
| File paths in a report | n/a | Inline-code paths that exist under the session's folder are clickable, as in Claude's replies |

## 5. Search finds reports

Open **Search transcripts** and search for a phrase that appears only in an agent's report. For the Safety
session, try `useCurrentUser` (it's in the Mobile survey's report). Leave **Tools & thinking** off.

| | Before | After |
|---|---|---|
| Results | No hit from that session (the reports were never indexed) | Two hits with an **Agent** chip (the Mobile and Spa reports both mention it), each with an excerpt |
| Choosing the hit | n/a | History opens on that session with the find bar on the query. The report card is collapsed; expand it to see the match. |

## 6. Not checkable on demand

- **A terminal session's "done" alert after a hand-back.** The `<task-notification>` that follows a `SubagentHandback`
  is now ignored when deciding whether the parent still owes a reply. That needs a terminal (not Perch) session where
  `SubagentHandback` is enabled, which is decided per session. If one comes up, check that "done" fires once after
  the parent's final reply.
- **The live summary for the new form.** If a Perch session's agent hands back with `SubagentHandback`, check that its
  notice reads `Agent "<description>" finished` and its card holds the report, not a sentence saying it was
  "delivered to you as a message".
