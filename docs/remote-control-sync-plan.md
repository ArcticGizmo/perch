# Remote Control sync plan

When a controlled (stream-json) session has Remote Control on, a second client (claude.ai / the phone) can
answer prompts and start turns. Perch works out session state from its own actions (it sends a prompt, so a
turn is running; it answers a permission, so the permission is resolved), and that breaks once a second
client can act. Symptoms: permission cards that never clear, an overlay stuck on "Waiting", turns that read
idle while Claude works, and prompts from the phone that never appear in the thread.

Findings first came from reading the JavaScript bundled inside the installed `claude.exe` (2026-10-08). P0 confirmed
them live on 2026-11-06 (claude 2.1.319); see "What the capture showed" below.

## What the CLI does

| Wire message | Direction | Meaning | Perch today |
|---|---|---|---|
| `{"type":"control_cancel_request","request_id":X}` | CLI → host | Withdraws control request X: another client (the bridge) answered it first, or its wait ended | Dropped: `ParseRecord` has no case for it |
| `{"type":"system","subtype":"session_state_changed","state":"idle"\|"running"\|"requires_action"}` | CLI → host | Authoritative turn state, whoever started the turn. Only emitted when the child env has `CLAUDE_CODE_EMIT_SESSION_STATE_EVENTS=1` | Not enabled |
| `user` echo (with `uuid`, `origin`) | CLI → host | Every user message, including ones from the bridge. Only with `--replay-user-messages` | Not enabled |
| `control_request` with a subtype other than `can_use_tool` | CLI → host | Anything else the CLI asks the host | Dropped silently, never answered |

## What the capture showed (P0, 2026-11-06, claude 2.1.319)

Captured with a standalone harness that launched `claude` with Perch's exact flags and environment plus
`--replay-user-messages`, turned Remote Control on and logged stdout, while a phone drove the session.

- **Withdrawn permission:** allowing on the phone sends `{"type":"control_cancel_request","request_id":X}` (X = the
  `can_use_tool` id), then `session_state_changed` `running`. Seen three times; P1's shape is right.
- **State events:** `running` → `requires_action` → `running` → `idle` for a Perch turn, and `running` → `idle` for a
  phone turn that Perch never saw a prompt for. P2's shape is right.
- **Echoes:** every prompt comes back as a `user` record with `"isReplay":true`. Perch's own carries the `uuid` Perch
  put on it and no `origin`. A phone prompt carries its own uuid, string `content` and `"origin":{"kind":"human"}`.
  Only replayed records carry `isReplay`; the CLI's `[Request interrupted by user for tool use]` marker is an
  ordinary user text record without it.
- **Order:** for a phone prompt, `running` arrives *before* the echo, so the echo must not do the "turn already
  active → one queued" bookkeeping.
- **A `system/init` per turn**, phone turns included. Perch already treats init as a turn start.
- **Interrupt from the phone:** no control request reaches the host. The turn ends with a `result` carrying
  `is_error:true`, `terminal_reason:"aborted_tools"`, then `idle`. P2 covers it.
- **Perch's own permission answer is echoed back** as a `control_response` with the request's id and
  `{behavior, updatedInput}`. It carries no `mode`, so `ParseControlResponse` ignores it.
- **New, not used yet:** `{"type":"command_lifecycle","command_uuid":U,"state":"queued"|"started"|"completed"|"cancelled"}`
  per prompt, keyed by the prompt's uuid. An authoritative queue signal if "N queued" ever needs one. `init` also
  lists `capabilities` (`msg_lifecycle_v1`, `interrupt_receipt_v1`, …).

## Status
- P1 + P2 shipped in v0.5.16 (PR #53). The P0 capture confirmed their wire shapes; the UI hasn't been driven live.
- P0 done (above).
- P4 built: `--replay-user-messages`, uuid-stamped prompts, `UserPromptEchoEvent`, and a "↗ via Remote Control" marker
  under the bubble. Tests in `StreamJsonParserTests` / `SessionConversationTests` use the captured shapes. The
  session window hasn't been driven live.
- P3 not started.
- How P2 turned out: state events **add to** the inference rather than replacing it. `result` still settles a
  turn and output still reopens one; `running`/`requires_action` set the turn active, and `idle` settles it
  (clearing queued state and expiring any card the CLI never withdrew). So an older CLI behaves exactly as before.
  One known gap: if `result` lands while the CLI is still `running` (it keeps background agents going), the
  session reads idle until the next state change, same as today.

## Phases

### P0 - Capture (optional, costs one real RC bridge)
Run one controlled session with `PERCH_SESSION_LOG=1`, `/rc` on, and the env var + flag from P2/P4 set by hand.
From the phone: answer a permission, send a prompt, interrupt. Save the relevant lines from
`logs/session-stream.log` as fixtures. Confirm the shapes in the table above before relying on them.

### P1 - Withdrawn permissions (fixes stuck prompts)
- `SessionEvents.cs`: `PermissionCancelledEvent(string RequestId)`.
- `StreamJsonParser.ParseRecord`: `"control_cancel_request" => [new PermissionCancelledEvent(id)]`.
- `SessionConversation.Apply`: find the `PermissionItem` with that `RequestId`. If it's still Pending, set a new
  `PermissionResolution.AnsweredElsewhere`, clear `PendingPermission` if it matches, then raise `Changed` +
  `StateChanged`. An unknown id is a no-op.
- The permission/question card renders `AnsweredElsewhere` as a quiet "answered remotely" receipt, with
  no buttons. Check every `switch` over `PermissionResolution` (the Roost pane, `SessionPane`, `SessionWindow`).
- Tests: parser case in `StreamJsonParserTests`; `SessionConversationTests`: a cancel clears the pending item,
  an unknown id changes nothing, a cancel after the user already answered changes nothing.

### P2 - Authoritative turn state (fixes stuck/false status)
- `ClaudeSessionController.Start`: `psi.Environment["CLAUDE_CODE_EMIT_SESSION_STATE_EVENTS"] = "1"`.
- Parser: `system/session_state_changed` → `SessionStateEvent(string State)`.
- `SessionConversation`: a `HasSessionState` flag set on the first such event. Once it's set:
  - `running` / `requires_action` → `TurnActive = true`, `MayHaveQueuedTurn = false`
  - `idle` → `TurnActive = false`, `QueuedPrompts = 0`, `MayHaveQueuedTurn = false`
  - `ReopenTurn` and the `TurnActive` writes in the `result` / `compact_boundary` arms defer to it (they stay as
    the fallback for a CLI that never emits the event).
  - `requires_action` with no `PendingPermission` (a remote-side prompt Perch can't see) still reads as Waiting.
    Add a `RemoteWaiting` bool and teach `PerchSession`'s activity mapping (`PerchSession.cs:248`) about it.
- Tests: the state sequence with and without the event; `idle` settles a turn the conversation thought
  was queued; `IsSettled` turns true after `idle`.

### P3 - Answer what we don't handle
- In `ClaudeSessionController.Pump`, a `control_request` whose subtype isn't `can_use_tool` gets
  `{"type":"control_response","response":{"subtype":"error","request_id":X,"error":"Unsupported control request subtype: S"}}`.
  This matches what the SDKs and the CLI itself do. Keep it in the controller (it owns the write side): the
  parser can surface an `UnhandledControlRequestEvent(id, subtype)` and the pump replies, then swallows it.
- Test: the parser emits the event for an unknown subtype and not for `can_use_tool`.

### P4 - Remote prompts in the thread
- `Start`: add `--replay-user-messages`.
- `SendPrompt` stamps a fresh `uuid` on each outgoing `user` message and remembers it (a small bounded set).
- Parser: a `user` record whose content is text (not `tool_result`, not a `<task-notification>`) →
  `UserPromptEchoEvent(string? Uuid, string Text, string? OriginKind)`. Check that this doesn't change how
  transcripts parse: `ParseTranscriptLine` already takes genuine prompts out before `StreamJsonParser`
  sees them, but confirm with `StreamJsonReplayTests`.
- Controller drops echoes whose uuid is ours. Conversation: an echo from somewhere else → `UserMessageItem` (tagged
  remote, so the bubble can show a small "from phone/web" marker). Turn bookkeeping only when no state events have
  arrived (`CliState` null); otherwise `running` already started the turn (see the capture).
- Also replayed with this flag: `control_response` frames. Confirm they don't trip `ParseControlResponse`'s
  mode/RC matching (they echo the host's own answers, so the inner shape differs).
- Tests: our own echo is dropped; a foreign echo adds one user item and starts a turn; the duplicate-ack path
  (same uuid twice) adds one item.

## Order and size
P1 and P2 are small and fix the reported symptoms; ship them together. P3 is a few lines. P4 is the biggest
(dedupe + UI marker) and is mostly about display, so it can follow. Each phase must keep both heads building
and the .NET suite green.

## Risks
- `CLAUDE_CODE_EMIT_SESSION_STATE_EVENTS` and the `session_state_changed` shape aren't documented. Fall back
  quietly when the event never arrives.
- `--replay-user-messages` echoes more than user prompts. P4 must be tested against a real capture (P0) before
  merging.
- Nothing here changes behaviour when Remote Control is off, apart from P2's state events, which then simply
  confirm what Perch already worked out.
