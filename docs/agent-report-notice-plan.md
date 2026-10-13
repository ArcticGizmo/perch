# Agent report notice

**Status:** built 2026-10-13 on branch `fix/subagent-handback-idle` (investigated against claude 2.1.296). Unit and
replay tested, render-verified (`render` → `session_agent_report*.png`); not yet run live. Manual checks:
[`agent-report-notice-testing.md`](agent-report-notice-testing.md). The "Optional: Open in agent tab" link was not built.

## Goal

When a background (async) agent finishes, the session thread shows a quiet centred notice
(`✓  Agent "Survey Api v2 endpoints and flags" finished · 1m 25s`). Make that notice expandable so the agent's
**report**, the text it handed back to the parent, can be read in place: collapsed by default, a chevron to open it,
the report rendered as Markdown with clickable file references.

Today the report is not shown anywhere in the parent thread, and in live sessions it leaks into the wrong places
(see "Current behaviour").

## How a report reaches Perch

Two report forms exist, and two feeds (live stream-json for Perch-driven sessions, the transcript for history,
resumed sessions and tails).

| | Old form (agent ends with plain text) | New form (agent ends with `SubagentHandback`) |
|---|---|---|
| **Stream-json (live)** | `system/task_notification` `summary` = the agent's whole final text (verified: "pineapple", "done. Steps 1 and 3 worked, …"). The agent's own records also stream with `parent_tool_use_id` = the launching `Agent` tool-use id. | The agent's `SubagentHandback` `tool_use` streams with `parent_tool_use_id`; its `input.message` is the report. The `task_notification` summary for this form is **unverified**: the tool is per-session gated and couldn't be forced in a probe. |
| **Transcript** | The `<task-notification>` user record's `<result>…</result>` is the report. Perch skips `<result>` today (`TaskNotification.Parse` stops at it). `<summary>` is `Agent "…" finished`. | An `isMeta` user record `Another Claude session sent a message: <agent-message from="{agentId}">[Subagent hand-back] … The report follows:` plus the report, indented two spaces. `origin` = `{kind:"peer", from:agentId, handback:true, body}`. The `<task-notification>` that follows is `queueTranscriptOnly`, and its `<result>` only says the report "was delivered to you as a message … not repeated here". |

The agent id (`origin.from`, `<agent-message from>`) is the background task id. The launching tool-use id
(`parent_tool_use_id`, `<tool-use-id>`) is the task's `ToolUseId`. `BackgroundTaskTracker` already holds both, so
any source can be keyed onto the right task.

## Current behaviour (findings)

1. **Sub-agent output lands in the parent thread (live sessions).** `StreamJsonParser` tags records that carry
   `parent_tool_use_id` as `FromSubagent`, but `SessionConversation.Apply` only checks that tag in `ReopenTurn`.
   Text, thinking and tool calls from a background agent are therefore appended as an ordinary assistant message.
   Verified by replaying `fixtures/stream-json/agent-message-mid-task.jsonl`: the thread gets
   `assistant: tool ToolSearch | tool SendMessage | text done`, which is the sub-agent's work under the parent's
   name. With the new form, the whole report becomes a `SubagentHandback` tool card there. (Your memory notes this as
   a pending follow-up to PR #33.) History is unaffected: sub-agent records live in their own transcripts.
2. **Live old-form notices print the whole report** as the centred notice line, because `BackgroundTaskText.Notice`
   returns `task.Summary` verbatim and the stream's summary is the report. In the transcript it reads
   `Agent "…" finished`.
3. **History shows no report at all.** `<result>` is skipped, and the new-form peer record is `isMeta`, so
   `GenuineUserPrompt` drops it and `StreamJsonParser` finds no task notification in it.

## Design

### Core (`Perch.Core`)

- `SessionEvent` gains `ParentToolUseId` (the raw `parent_tool_use_id`). `FromSubagent` becomes `ParentToolUseId is not null`.
- `BackgroundTask` gains `Report` (string?, capped at ~64 KB with a truncation note).
  `BackgroundTaskTracker.SetReport(taskIdOrToolUseId, text)` raises `Changed`. The first non-empty source wins, but
  a hand-back beats a plain summary.
- **Sources:**
  - Stream, new form: `ToolUseEvent` named `SubagentHandback` with a `ParentToolUseId` → `input.message`.
  - Stream, old form: `TaskNotificationEvent` (not `Delivered`) for an agent task → its `Summary` is the report. Keep a
    short `Agent "<description>" finished` as the notice's summary.
  - Transcript, new form: a new `AgentHandbackEvent(agentId, report)` from the `isMeta` peer record when
    `origin.handback == true`. Read `origin.body` (fall back to the `<agent-message>` text), strip the frame preamble
    up to "The report follows:", and de-indent two spaces.
  - Transcript, old form: extend `TaskNotification.Parse` with an opt-in `Result` read
    (`<result>` … `</result>`, the last one, since a report can quote tags). Ignore the "was delivered to you as a
    message" placeholder.
- **Stop sub-agent output reaching the thread:** in `SessionConversation.Apply`, return early for `FromSubagent`
  text, delta, thinking, tool-use and tool-result events, after capturing a hand-back. Task events are unaffected.
  Agent tabs read the sub-agents' own transcripts, so they lose nothing.
- `TaskNoticeItem` needs to re-render when a report lands after the notice exists (the live order is report first,
  but not guaranteed). Mirror `_launchCards`: a task-id → notice map, so `OnBackgroundTaskChanged` raises `Changed`
  on it too.

### UI (`SessionThreadView.BuildTaskNotice`)

- No report: unchanged (a centred one-liner).
- With a report: a compact card shaped like `BuildThinking`. The header row is
  `▸ ✓ Agent "Survey Api…" finished · 1m 25s`, with the report's first heading or line as a muted gist. Clicking the
  header toggles it. The body is `Prose(report)` (Markdown + clickable file refs), plus a small "Copy report" action.
  The expand state goes in `_expandState`, so it survives a rebuild. It provides a `Sticky` so a long open report
  pins its header like the other tall cards (sticky card headers).
- Collapsed by default: the parent's next reply usually summarises the report.
- Width: same max width as other centred cards (`ThreadMaxWidth * 0.86`), left-aligned body text.
- Optional: an "Open in agent tab" link, since the agent tabs already know the agent id.

### Tests

- `StreamJsonParser`: `ParentToolUseId` carried; `SubagentHandback` input.
- `SessionConversation`: sub-agent events don't create items (rework `SubagentOutputAfterResult_DoesNotReopenTurn`).
  The report attaches to the task by either key, and a late report re-raises the notice.
- Replay: extend `agent-message-mid-task.jsonl` with `expect` checks on the item list, or add an item-shape checkpoint
  kind to `StreamJsonReplayTests`.
- Transcript: the hand-back peer record (trimmed from the 2026-10-13 capture), preamble stripping and de-indent, and
  `<result>` extraction including the placeholder.
- Render: a `HeadlessRenderer` sample with a collapsed and an expanded report notice, dark and light.

## Decisions (2026-10-13)

1. Drop sub-agent output from the parent thread entirely: yes.
2. Collapsed by default: yes.
3. Index reports in transcript search: yes, if it's easy.
4. Still open: the live `task_notification` summary for the new form is unverified. Capture it the next time a gated
   session runs (the `SubagentHandback` reminder appears in the agent's transcript) and pin it in a fixture.

Every change ships with manual test prompts for checking before and after: see
[`agent-report-notice-testing.md`](agent-report-notice-testing.md).

## Risks

- Synchronous (foreground) agents also stream with `parent_tool_use_id`. Dropping their output from the thread
  changes what users see mid-turn; their result still lands on the `Agent` tool card.
- Report text is model output rendered as Markdown. That's the same trust level as assistant prose, which
  `MarkdownView` already renders.
