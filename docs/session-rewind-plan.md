# Session rewind, fork, and Remote Control across `/clear`

Plan for three related asks on Perch-controlled (stream-json) sessions:

1. **Rewind** - go back to an earlier prompt and drop everything after it (conversation, code, or both), picked interactively by scrolling the thread.
2. **Fork** - branch a session into a new one, either from its end or from an earlier prompt, leaving the original untouched.
3. **Remote Control after `/clear`** - claude.ai stops following a session once it is cleared.

Status (2026-11-04, branch `session-rewind`, uncommitted):

- **Done in code:** P1-P5. Render-verified (`session_rewind_1x`, `session_rewind_light_1x`); never driven live through the GUI.
- **Waiting on RC-1:** P6, the `conversation_reset` parse and Remote Control re-link.
- **Already in:** a restart of any kind (rewind, account switch, sign-in refresh) now turns Remote Control back on when it was on.

The protocol facts below were read from the CLI bundle (`~/.local/bin/claude.exe`, 2.1.318). All of them except the in-process `rewind_conversation` request were then confirmed with live probes on 2026-11-04.

## Protocol facts

### Conversation rewind: `--resume-session-at`

A hidden CLI flag: `--resume <id> --resume-session-at <uuid>` resumes keeping "only messages up to and including the chain entry with `<uuid>`" (print mode). The next message written parents onto that entry, so the transcript becomes a **branch**: the dropped records stay in the `.jsonl` file, but the live chain no longer reaches them.

Live probe:
- The model only recalled the kept turn.
- **Nothing is written to the transcript until the next prompt.** Until then, the file's last record is still the dropped branch's, so `PerchSession` keeps the rewind point (`ResumeAt`) and reuses it for any restart, wake, or history read before the next send.

A sibling flag, `--resume-drops-turn <prompt uuid>`, declares which turn the truncation is meant to discard. The CLI then refuses the resume if the dropped range holds anything not attributable to that turn (absorbed queued messages, task notifications). That guard fits a "drop the last turn" action, but not a rewind across many turns, so we won't pass it.

There is also an undocumented in-process `rewind_conversation` control request (`target_message_uuid`, `last_seen_user_message_uuid`, `interrupt_if_running`, `allow_summarized_target`, ...). It has no published schema and a lot of policy around it, so we **don't** use it. Decision: rewind restarts the process with truncation, reusing `PerchSession.Restart`.

### Code rewind: `rewind_files`

The Agent SDK's control request:

```json
{"type":"control_request","request_id":"perch-rewind-1","request":{"subtype":"rewind_files","user_message_id":"<uuid>","dry_run":true}}
```

The response is `{canRewind, error?, filesChanged?: string[], insertions?, deletions?}`. `dry_run` gives the preview without touching disk.

Live probe with Haiku:
- Rewinding to the second prompt restored the file's content from before it.
- Rewinding to the first prompt deleted the file that prompt created.
- An unknown id returned `canRewind:false, error:"No file checkpoint found for this message."`.

The probe's working directory has to be a long path: Claude's permission check refuses Write/Read on an 8.3 short path (`C:\Users\ABCDEF~1\...`).

**Gating:** in print mode the CLI only records file checkpoints when `CLAUDE_CODE_ENABLE_SDK_FILE_CHECKPOINTING=1` is set and `CLAUDE_CODE_DISABLE_FILE_CHECKPOINTING` isn't. Perch doesn't set it today, so:

- `ClaudeSessionController.Start` sets it on the child env from now on.
- A session started before that (or resumed from a terminal run that predates it) answers `canRewind:false`, and the UI says code can't be rewound past that point.
- Checkpoints only cover the file tools (Edit/Write/NotebookEdit). Bash-made changes are not tracked, which the preview says in one line, as the TUI does.

A `--rewind-files <user-message-id>` flag also exists ("restore files ... and exit"), but the control request does the same against the running process without a restart.

### Fork: `--fork-session`

`--resume <id> --fork-session` creates a new session id from the old conversation. Combined with `--resume-session-at <uuid>` it forks **from a point**. The original transcript is never written to.

`--session-id <new>` is accepted alongside `--fork-session` (the CLI refuses it with `--resume` otherwise). Perch passes it, so the fork's lock and registry entry are taken under the new id from the start and the source session's lock is never touched.

### Remote Control across `/clear` (live probe, 2026-11-04)

Steps: enable via `remote_control`, send `/clear`, read `sessions/{pid}.json`, re-enable, then disable.

- The CLI **keeps the bridge**. `bridgeSessionId` stays in the session file across the clear (same `session_...` value), and re-enabling after the clear returns the **same** `session_url`.
- `/clear` emits a record Perch doesn't parse yet: `{"type":"conversation_reset","new_conversation_id":"...","trigger":"clear","user_message_uuid":"..."}`. Next come `SessionStart:clear` hooks, a fresh `init` with a new `session_id`, and a `result` with `local_command:"clear"`.
- Disabling nulls `bridgeSessionId` in the file.

So Perch's own state is right. `PerchSession.RemoteControlEnabled` stays true, and the overlay's on-disk signal stays set. The symptom the user saw (claude.ai stops showing new messages) is on the bridge side: the bridge survives, but claude.ai doesn't follow the new conversation. **Still unproven:** whether cycling RC (off then on) after a clear gives a bridge that does follow. One manual check settles it (see `docs/session-rewind-testing.md`, step RC-1).

## Design

### R1 - Transcript chain awareness (Core, prerequisite)

Today `SessionConversation.LoadHistory` folds transcript lines **in file order**. After any rewind (Perch's, or the TUI's own Esc-Esc rewind) that shows the abandoned branch too, so a rewound session reopened later shows turns that no longer exist. Fix:

- New `TranscriptChain.ActiveLines(lines)`: index records by `uuid`, take the last main-chain (non-sidechain) record as the leaf, and walk `parentUuid` back to the root. Compaction boundaries hop via `logicalParentUuid`. Return the surviving lines in file order. If the chain is broken (missing parent, cycles, no uuids), fall back to file order: never lose history.
- `PerchSession.LoadHistoryAsync` and the history viewer feed `ActiveLines` into `LoadHistory`.
- `UserMessageItem` gains `Uuid` (and `ParentUuid`), set from the transcript record on history load.
- Tests: fixture transcripts with a TUI-style branch, a compaction boundary, and a broken chain.

### R2 - Resolving uuids for live prompts

A prompt Perch sends live has no uuid on the wire (the CLI only echoes user messages with `--replay-user-messages`, which brings dedupe work, so we skip it). At rewind time Perch re-reads the transcript off the UI thread, runs `ActiveLines`, and pairs the real user prompts with the thread's `UserMessageItem`s **in order**, checking text equality and skipping tool results, meta records and command echoes. A mismatch disables that target with "can't place this prompt in the transcript" rather than guessing.

Only prompts **since the last `/clear`** are targets. Older ones belong to a different session id, and the thread already shows a "conversation cleared" marker there.

### R3 - Rewind mode (the interactive picker)

Rewind is a mode on the thread, not a dialog with a list, so picking a spot is scrolling the chat itself.

- **Entry:** `/rewind` (a Native command in `SlashCommandCatalog`), **Esc Esc** in an empty composer (TUI parity), or a hover action (an anticlockwise-arrow glyph) on any of your past prompts in `SessionThreadView.BuildUser`, which enters the mode with that prompt selected.
- **In the mode:**
  - The composer is replaced by a docked **rewind bar**.
  - Each eligible prompt gets a target affordance (left rail marker + hover wash).
  - A horizontal **rewind line** sits just above the selected prompt, and everything below it renders dimmed, so you see exactly what goes.
  - You scroll freely. Click a prompt, or press ↑/↓ to move between prompts (the thread scrolls the selection into view), Home/End for the first/last. Esc leaves the mode.
- **Rewind bar contents:**
  - "Rewind to before *'<prompt preview>'* · drops N turns".
  - The code preview from a `rewind_files` dry run, debounced per selection, e.g. "code: 4 files, +120 -30", expandable to the file list. Or "code can't be rewound to here" when `canRewind:false`, plus the Bash caveat.
  - Actions: **Rewind conversation + code** (default, Enter), **Conversation only**, **Code only**, **Fork from here**, **Cancel**. When code can't rewind, the combined and code-only actions are disabled with the reason in their tooltip.
- **Running turn:** entering the mode while a turn runs asks to interrupt first, as the TUI does.

### R4 - Applying a rewind

- **Code:** send `rewind_files` (not a dry run) to the running process, then add a note: "restored 4 files to before '<prompt>'". An error leaves everything as it was and adds an error note.
- **Conversation:**
  - Restart in place with `--resume <id> --resume-session-at <parentUuid of the selected prompt>`. That needs `SessionLaunchOptions.ResumeAt` and a `ClaudeSessionController.Start` parameter, validated with `IsSafeToken`.
  - When the restart lands, the thread drops every item from the selected prompt on, with a "rewound to before '<prompt>'" marker.
  - The selected prompt's text and attachments go back into the composer, so it can be edited and re-sent (TUI parity).
- **Both:** code first, then the conversation, so a failed file restore never leaves a truncated conversation with un-restored code.
- **First prompt:** rewinding to before it has no parent to keep. Conversation rewind there means "start empty": offer it as a `/clear` (new id) with the prompt put back in the composer, and say so on the bar.
- Remote Control stays on across the restart only if Perch re-enables it. After a rewind restart, if `RemoteControlEnabled` was true, Perch re-sends `remote_control enabled:true` and notes the link if it changed.

### R5 - Fork

- **Fork from here** (rewind bar) and **Fork session** (composer overflow menu + a `/fork` Native command) open a **new** Perch session window with `--resume <id> --fork-session` (+ `--resume-session-at` for a point). It inherits the source's cwd, model, mode, effort and account.
- The new window's history loads from the **source** transcript through `ActiveLines`, cut at the fork point. Its own id comes from the new `init`, and the lock/registry follow it through the controller's existing "different id" branch.
- The original session is untouched and keeps running.
- The title is the source's title + " (fork)", until renamed.

### R6 - Remote Control after `/clear`

- Parse `conversation_reset` into a `ConversationResetEvent(trigger, newConversationId)`. It is a firmer `/clear` signal than "a second init with a different id", so `SessionConversation` keys `ClearForNewConversation` off it, keeping the init check as a fallback for older CLIs.
- Then, depending on RC-1:
  - **If cycling helps:** on a `conversation_reset` while `RemoteControlEnabled`, Perch sends `remote_control enabled:false` then `enabled:true`, and notes "remote control re-linked to the new conversation - <url>", flagging a changed link so the QR can be re-shown.
  - **If it doesn't:** Perch adds a note after `/clear` that the claude.ai view keeps showing the earlier conversation (a CLI limitation), and we leave it at that.

## Phases

| Phase | Scope | Verifies by |
|---|---|---|
| P0 | RC-1 manual check (cycle after `/clear`) | user on claude.ai |
| P1 | R1 chain-aware history + `UserMessageItem.Uuid` | xUnit fixtures |
| P2 | Checkpoint env var + `rewind_files` wire (`RewindFilesEvent`, dry run + apply) | parser tests, live probe |
| P3 | Rewind mode UI (R3) | `render` capture of the mode + bar |
| P4 | Apply (R4): restart with `ResumeAt`, thread truncation, composer refill | live dogfood |
| P5 | Fork (R5) | live dogfood |
| P6 | `conversation_reset` + RC re-link (R6) | parser test, live check |

## Open questions

- Esc Esc: does anything in `SessionWindow` already bind a double Esc (palette dismiss, find bar)? Rewind should only claim it with an empty composer and no popup open.
- Should "drop the last turn" get a one-click shortcut (passing `--resume-drops-turn` for the CLI's stricter check)? Not planned; the rewind bar defaults to the last prompt anyway.
