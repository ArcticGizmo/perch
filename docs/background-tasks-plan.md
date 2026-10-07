# Background tasks (shells, monitors, async agents) — design & checkpoint plan

Status: **P0–P3 done** (2026-10-07), on branch `background-tasks` (cut from `main`). P0+P1 are unit-tested against real
captures; P2+P3 are render-verified (`render` → `session_bgtasks*_1x.png`, `overlay_1x.png`). Owed: a live check of
Stop, the output tail and the overlay count against real sessions.

## The ask

Show background work properly in the session window, the overlay and the Roost, and let the user control it.
Background work means a `Bash`/`PowerShell` call with `run_in_background`, a `Monitor`, and an async (background)
`Agent`. Today:

- **A background shell's card reads "done" at once.** Its tool result ("Command running in background with ID: …")
  comes back immediately, so the card flips to done and shows that line. Nothing says it is still running, or when
  and how it ended.
- **The CLI already tells us everything, and Perch drops it.** `StreamJsonParser.ParseSystem` keeps only `init`,
  `status` and `compact_boundary`. The `task_*` records below go nowhere.
- **Monitor and TaskStop aren't handled at all.** The RUNNING chips and the overlay's sub-rows cover sub-agents
  only. There's no count of background work anywhere.
- **Bug:** a delivered `<task-notification>` is a plain string user record with no `isMeta`. History and the
  read-only tails render it as a **user bubble** full of XML.

## What Claude Code emits (the P0 spike)

Probed 2026-10-07 against Claude Code **2.1.292** with a throwaway stream-json session per scenario (background
Bash ×2 with one failing, Bash + TaskStop, Monitor, and a background Bash stopped by control request). Captures are
the fixtures under `tests/Perch.Tests/fixtures/stream-json/bg-*.jsonl` (scrubbed).

### stream-json (controlled sessions)

| Record | Fields that matter |
|---|---|
| `system/background_tasks_changed` | `tasks: [{task_id, run_id, task_type, description}]`, the **whole current set**; `[]` = nothing running |
| `system/task_started` | `task_id`, `tool_use_id`, `description`, `task_type` (`local_bash` \| `local_agent`), `is_backgrounded`, `owned_by_subagent?`, `subagent_type?` |
| `system/task_progress` | `task_id`, `tool_use_id`, `description` ("Running …"), `usage{total_tokens,tool_uses,duration_ms}`, `last_tool_name` (agents) |
| `system/task_updated` | `task_id`, `patch{status, end_time}` (`completed` \| `failed` \| `killed`; `end_time` is epoch ms) |
| `system/task_notification` | `task_id`, `tool_use_id`, `status` (`completed` \| `failed` \| `stopped`), `output_file`, `summary`, `usage?` |
| `user` tool_result of the launch | `tool_use_result.backgroundTaskId` (shell), `tool_use_result.{taskId,timeoutMs,persistent}` (Monitor), `tool_use_result.{isAsync,agentId}` (agent) |
| `result` of a wake turn | `origin: {kind: "task-notification", producer: "session-task"}` |

- **A Monitor is a `local_bash` task.** Only the launching tool's name (`Monitor`) tells it apart, so the tracker
  keys kind off the `tool_use_id` → tool name.
- **A foreground shell also gets `task_started`**, with `is_backgrounded: false` (seen for a sub-agent's Bash). Only
  `is_backgrounded: true` tasks are background work.
- **Monitor events are not on stdout.** Each event wakes a turn (`result.origin.kind = task-notification`), but the
  `<task-notification><event>…</event>` user record that carries it isn't echoed (stream-json doesn't replay user
  records). The event text is in the transcript and in the task's `.output` file.
- **Summaries carry the exit code:** `Background command "X" failed with exit code 3`, `… completed (exit code 0)`,
  `Monitor "X" stream ended`, `Task "X" was stopped by the user`.

### Stopping a task: the `stop_task` control request

```json
{"type":"control_request","request_id":"…","request":{"subtype":"stop_task","task_id":"bupmsikht"}}
```

It is acked `{"subtype":"success","response":{}}`. The task gets `task_updated{status:"killed"}`, then
`task_notification{status:"stopped"}`, then `background_tasks_changed` without it. (`kill_task` and `task_stop` are
`Unsupported control request subtype`.) It also enqueues a `<task-notification>` (`status: killed`, "was stopped by
the user"), so Claude hears about it on its next turn. **Stop needs no prompt and no turn.**

### Transcript (terminal sessions, history, tails)

- Launch: the tool_result record's `toolUseResult` carries the same `backgroundTaskId` / `taskId` / `isAsync`.
- End: a `<task-notification>` as a `queue-operation` (`enqueue`, then `dequeue`/`remove`), a `queued_command`
  attachment, and finally a delivered **`user` record with string content** and `origin.kind: "task-notification"`
  (no `isMeta`).

```text
<task-notification>
<task-id>bbm65f6ep</task-id>
<tool-use-id>toolu_…</tool-use-id>
<output-file>C:\…\tasks\bbm65f6ep.output</output-file>
<status>failed</status>
<summary>Background command "Delay then exit with code 3" failed with exit code 3</summary>
</task-notification>
```

  An agent's adds `<note>`, `<result>` and `<usage>`. A **Monitor event** has only `<task-id>`, `<summary>Monitor
  event: "X"</summary>` and `<event>…</event>`, with **no `tool-use-id` and no `status`**.
- Output: `%TEMP%\claude\<enc-cwd>\<sessionId>\tasks\<taskId>.output`. A shell's is plain stdout/stderr and ends with
  `[exited with code N]` once finished.

## Design

### D1. One model, two feeds

`Perch.Data.BackgroundTask` (UI-free): task id, tool-use id, kind (`Shell` / `Monitor` / `Agent`), description,
status (`Running` / `Completed` / `Failed` / `Stopped`), started/ended, exit code, output file, summary, Monitor
event count + last event, owned-by-sub-agent. `BackgroundTaskTracker` folds both feeds into it:

- **Controlled sessions:** the stream-json `task_*` records, parsed into new `SessionEvent`s. These are authoritative
  and live.
- **Transcript (history, tails, terminal sessions):** the launch `toolUseResult` and the delivered
  `<task-notification>` user record (`TaskNotification.Parse`).

The tracker is idempotent: a notification seen from both feeds, or twice, settles the task once.

### D2. Done alerts stay as they are for shells and monitors

The early "done" is held for an outstanding **async agent** because one always ends and wakes the parent. A
background shell can be a dev server that runs for hours, so holding "done" for it could hold it forever. Shells and
monitors don't hold the alert. They show as a count instead (D5), so an idle session with work in the background
doesn't read as finished.

### D3. The live card (session window)

A background launch's tool card links to its task (`ToolCallPart.BackgroundTaskId`). It reads "background · 2m 14s"
with a tail of the `.output` file, then settles to the exit code. It has **Stop**, **Open output** and **Copy path**
buttons. The tail is a bounded read (last ~8 KB), off the UI thread, polled only while the card is on screen and the
task running.

### D4. Notices, not bubbles

A task's end becomes a compact `TaskNoticeItem` ("✗ Background command 'X' failed with exit code 3"). Live it comes
from `task_notification`; in history, from the delivered user record. Either way it is never a user bubble. A Monitor
event in history is a quiet "Monitor 'X': event 1" line.

### D5. Counts everywhere else

- **Session window:** the RUNNING chips grow shell and monitor chips (own glyph + elapsed) beside the agent ones.
  Clicking one brings its launch card into view, expanded to the live output.
- **Overlay:** a small background count glyph on the session row, plus "idle · 2 background" in the status text.
- **Roost:** the same count on the pane pill and the mini card.

Terminal sessions get theirs from a transcript fold (generalising `TranscriptReader.StepAsyncAgent`), cross-checked
against the shell's `.output` trailer so a task orphaned by a dead CLI doesn't count forever.

## Checkpoints

- **P0 — spike.** ✅ Formats above, fixtures captured, `stop_task` found.
- **P1 — Core model.** ✅ `BackgroundTask` + `BackgroundTaskTracker` (`Data/BackgroundTasks.cs`), owned by
  `SessionConversation.BackgroundTasks`. Stream-json `task_*` records parse to new `SessionEvent`s, and a launch result
  carries `ToolResultEvent.Launch`. `TaskNotification.Parse` reads both delivery paths: the delivered user record, and
  the `queued_command` attachment a notification becomes when it lands mid-turn. A launch card links its task via
  `ToolCallPart.BackgroundTaskId`. An end (or Monitor event) is a `TaskNoticeItem`, and `GenuineUserPrompt` no longer
  turns a notification into a user bubble. `ClaudeSessionController.StopTask` sends `stop_task`.
  `TranscriptReader.GetRunningBackgroundTasks` is the terminal-session fold (a clean exit clears it). Tests are in
  `BackgroundTaskTests`, with the fold added to `TranscriptFoldEquivalenceTests`.
  - Still owed for P3: the `.output`-trailer cross-check for a task whose CLI died abruptly (no `cost-state`).
- **P2 — session window.** ✅ A launch card (`SessionThreadView.ToolCard.UpdateBackground`) follows its task, not
  the instant "launched" result. Its status reads "background · 2m 14s" / "watching · 3 events · …" / "exit 1" /
  "stopped". The body is the newest output line, or, expanded, the command plus a scroll panel of the `.output` tail.
  That tail is a bounded 8 KB read off the UI thread every 2s while the card is on screen and the task runs, plus one
  last read after it ends. The card has **■ Stop**, **Open output** and **Copy path**. An async agent's card skips
  the tail, because its output file is a JSONL transcript, and shows `task_progress` instead. `TaskNoticeItem`
  renders as a centred ✓/✗/■/◉ line. The RUNNING row gains an amber chip per running shell or Monitor
  (`SessionWindow.Tasks.cs`) with a ticking elapsed time and its own ■. Clicking a chip reveals and expands the
  launch card, in place of the separate output tab first planned: the card already holds the tail and Stop.
  `PerchSession.StopTask` sends `stop_task`. `BackgroundTaskOutput` (path inference + tail) and `BackgroundTaskText`
  (shared wording) live in Core. Monitor, TaskStop and TaskOutput get proper tool summaries and glyphs.
  - Owed: a live check of Stop + the tail against a real session. Stop isn't offered in the Roost's pane yet (P3).
- **P3 — overlay + Roost.** ✅ `ClaudeSession.BackgroundTasks` comes from the transcript fold for every session
  (controlled ones write transcripts too). `SessionMonitor` drops any task launched before the session's process
  started (`startedAt`), so a task orphaned by a CLI that died without a clean exit doesn't count. That replaces the
  `.output`-trailer cross-check first planned. The fold also keeps each launch's description for the tooltip. The
  overlay row draws "❯ N" (shells + Monitors; agents already have sub-rows) in the theme's sub-agent hue, which
  already means background work. Its hover lists each task with its age. It's gated by a new **Background tasks**
  setting (`AppSettings.ShowBackgroundTasks`, on by default, registry id `background-tasks`). The Roost pill reads
  "Idle · 2 in background", and the rail shows "❯ 2" on an idle row. A Perch pane's thread offers Stop too
  (`SessionPane.StopTaskRequested` → `RoostWindow` → `PerchSession.StopTask`).
  - Seen while rendering, not caused by this work: the kitchen-sink sample row (every glyph on) already truncates
    its name to nothing and overlaps "-37" with the "work" chip at 1×. The new count adds ~30px when on.
- **P4 — polish (optional).** A Monitor's full event list on its card (it shows the last event and a count today),
  and Monitor events in a *live* controlled session, which stream-json doesn't echo (only the transcript and the
  `.output` file have them).
