# Async background-agent double-notification investigation

## Symptom

A session alerted a **"done"** completion and then, a beat later, **immediately started the parent
turn** — two signals for one event. Reported against session `11933c36-385e-4b14-a9bc-e5f67280c2f9`
(the `hypertree` repo).

Expected behaviour: a notification should fire only when the **parent** is done, **or** when the
**children** are done and the parent is *not* going to do something with the result.

## What the session actually did

It launched a single **async / background** agent — an `Explore` agent
(`agentId acb35e86398c03652`, "Find monitor-change prompt and settings"). The parent's tool_result for
the launch records `toolUseResult.isAsync: true`, `status: async_launched` — so this was **not** a
classic blocking Task. The parent got an immediate "Async agent launched successfully… working in the
background" result, did a little more work, **finished its own turn and went genuinely idle**, all while
the agent kept running.

## Timeline (UTC, 2026-08-24)

| Time | Event |
|------|-------|
| `00:46:09` | Parent emits the `Agent` tool_use (async) |
| `00:46:14` | tool_result returns immediately: *"Async agent launched successfully… working in the background"* |
| `00:46:16–24` | Parent does a bit more work (a Bash call) |
| `00:46:24.786` | Parent emits a final assistant **text** block → **its turn is complete; it goes idle** |
| `00:46:14 → 00:47:59` | The Explore agent runs in the background |
| `00:47:59.516` | Agent's transcript goes quiet (its tail is a completed assistant turn = idle) |
| **~`00:48:02.5`** | **Perch fires the synthetic sub-completion "done"** (grace window expires) |
| `00:48:02.583` | Claude Code injects the `<task-notification>` as a **user record** (via a `queue-operation`) |
| `00:48:06.785` | Parent wakes and **starts a new turn** to process the result |

Perch alerted ~4 s before the parent picked the work back up.

## Root cause

The sub-agent-completion logic in `SessionMonitor.ReadSession`
(`_subsFinishedIdleAt` grace window + the `IsParentMidTurn()` /
`TranscriptReader.LastTurnAwaitingAssistant` guard) was built for the **synchronous/blocking** sub-agent
model, and two of its assumptions do not hold for async agents:

1. **"The parent is blocked while the child runs, and its transcript tail shows an unanswered
   tool_result (awaiting assistant) until it resumes."**
   For an async agent the parent had already **completed its own turn** at `00:46:24`. At grace-expiry its
   transcript tail is a *finished* assistant turn, so `LastTurnAwaitingAssistant` correctly returns
   `false` — but "not mid-turn" no longer means "done" here.

2. **"If the parent is going to resume, it does so within the 3 s `SubsCompletionGraceMs` window."**
   An async agent's result is delivered by an **injected `<task-notification>` user record**, and that
   injection landed **~3.1 s** after the agent's transcript went quiet
   (`00:47:59.5` → `00:48:02.583`) — a hair *past* the 3 s grace. The one signal that would have
   suppressed the alert (that user record, which flips `LastTurnAwaitingAssistant` to `true`) arrives just
   *after* the window that decides whether to fire.

It is fundamentally a **race the async model tends to lose**: the parent completed its turn and went
idle, so the guard sees no reason to wait; then the task-notification wakes it into a fresh turn.

## The two idle states an async agent produces

The key that unlocked the real fix: while a background agent is in play, the session status goes
busy→idle **twice**, and on disk the two are identical (session idle, parent transcript tail a completed
turn):

1. **When the *agent* finishes** — the background work ends, so Claude Code flips the session idle, but a
   `<task-notification>` is coming that re-wakes the parent. **Not** a real completion.
2. **When the *parent* finishes** — after it has processed that notification and produced its final turn.
   The real completion.

Perch must alert only on the second. In `d1573ef5…` the agent's `.stopped` marker landed at `09:15:05.8`
and the session went idle; the `<task-notification>` only arrived at `09:15:08.7` — a **~3 s gap**. That
gap is long enough for the **busy→idle completion-settle** window (1 s) to elapse and fire the premature
"done", just before the parent resumed. (The first reported session, `11933c36…`, is the same story via
the sub-agent-completion grace path — the session had gone idle *before* the agent finished.)

## Fix

The durable discriminator between the two idle states is **whether the async agent's result has come
back yet**. Both facts live in the parent transcript, matched by **tool-use-id**:

- an async **launch** is a tool_result carrying `toolUseResult.isAsync == true` (its `tool_use_id`);
- its **result** arrives as a `<task-notification>` naming the same `<tool-use-id>`.

A tool-use-id that has been launched but not yet notified back is **outstanding**.
`TranscriptReader.HasOutstandingAsyncAgent` computes this (task-notifications matched by regex on the raw
line — their embedded result can be huge — and only the small `isAsync` launch records JSON-parsed;
cached by `(length, last-write)`). `SessionMonitor.ReadSession` consults it at **both** "done" edges and
holds the alert while an agent is outstanding:

- the **busy→idle completion-settle** expiry (`fireCompletionSettled`) — the path that actually fired in
  `d1573ef5…`;
- the **sub-agent-completion grace** expiry (`fireSubsCompletion`).

Once the notification lands, the id is no longer outstanding, so the parent's own subsequent busy→idle
(its real completion) alerts exactly once. Because these are transcript facts, not the parent's live
session status, the fix does **not** depend on a scan landing in any particular window.

Verified against both reported transcripts: at the premature-alert instant the launched id is
outstanding (suppress); in the final transcript state it is notified (fire).

Teammates (Agent Teams) are unaffected — they're launched as `in_process_teammate`, never as `isAsync`
tool_results, so a pure-team session has nothing outstanding and its completion path is untouched.

### First attempt (reverted): the idle-vs-busy heuristic

The first fix recorded the async signature at runtime — a pid seen with a background sub running *while
the parent's raw session status read `idle`*. It **did not fire in practice**, because it needs a `Scan`
to land while both conditions hold, and the sessions-dir `FileSystemWatcher` never sees the agent
transcript change — so between the parent going idle and the agent finishing there may be **no scan at
all** (or the session may read busy the whole time). The observation window was simply missed.

A second pass keyed on `HasAsyncSubAgentLaunch` (any async launch, ever) but gated **only** the
sub-completion path — so the completion-settle "done" (the one that actually fires when the session is
busy throughout the agent's run) still slipped through. The launch↔notification pairing above is the
precise, complete signal, gating both paths.

### Why not just lengthen the grace/settle window?

Any fixed window is fragile against Claude Code's queue latency — the `~3 s` gap here would defeat a
small bump and a large one needlessly delays every real completion. The pairing has no timing dependency.

### Known limitation

If a background agent is **re-tasked** (resumed via `SendMessage`) it can notify more than once for the
same tool-use-id. Once that id has been seen in any task-notification it counts as delivered, so a
*second* completion's pre-notification gap is no longer suppressed and could double-alert again — the same
shape, for the rarer re-drive flow. Closing that would mean correlating each agent's most-recent finish
with its most-recent notification (rather than a set membership); deferred until it's actually observed.

## Tests

`tests/Perch.Tests/SubAgentCompletionTests.cs` drives a real `SessionMonitor.Scan` under a mutable clock:

- `AsyncAgent_SessionBusyThenIdleOnAgentFinish_DoesNotFireDone` — the reported bug's shape (session busy
  through the run, busy→idle on the agent finishing); the completion-settle "done" is held.
- `AsyncAgent_ReturnWhileParentIdle_DoesNotFireDone` — the other variant (parent already idle while the
  agent ran); the sub-completion "done" is held.
- `AsyncAgent_AfterResultDeliveredAndParentCompletes_FiresExactlyOneDone` — once the task-notification is
  in the transcript and the parent completes, exactly one "done" fires.
- `NonAsyncSubAgentReturn_StillFiresDoneAfterGrace` — regression guard: with nothing outstanding, a
  sub-agent completion still fires.
