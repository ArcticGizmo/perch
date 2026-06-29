# Plan: Hook-driven termination & idle detection for agents, subagents and teammates

## Problem

Perch currently *guesses* when a subagent or teammate has finished:

- `SubAgentReader` demotes a "working" agent to **stale** after a 90-second silence
  (`SubAgentReader.cs`, `DefaultStaleAfter = 90s`).
- The overlay's "needs attention" state lapses on a hardcoded **5-minute** window
  (`SessionMonitor.cs`, `NeedsAttentionMinutes = 5`).

Both are timers, not events, so the displayed state lags reality and can be wrong (an agent that
finished 10s ago still shows "working" for up to 90s; a teammate that went idle isn't recognised as
idle until the silence window elapses).

This plan replaces the guessing with the real lifecycle hooks Claude Code fires, falling back to the
timers only when an event is genuinely unavailable.

## What we verified empirically (Claude Code 2.1.195)

These payloads were captured by an observational hook logger
(`~/.claude/perch-debug/log-hook.ps1`) across real subagent, team-terminate, and team-completion
runs. They are ground truth for this version, and they **corrected several fields the docs got
wrong** (`exit_code` / `completion_status` / `end_reason` / `idle_reason` do **not** exist).

### `SubagentStart`
Fires when any subagent or teammate is spawned.
```jsonc
{ "session_id", "transcript_path", "cwd",
  "agent_id": "a93110f9a1e20c9c8",      // also "aUX-Explorer-308f121a5c4e1c0c" for teammates
  "agent_type": "general-purpose",       // teammate role name for teammates, e.g. "UX-Explorer"
  "hook_event_name": "SubagentStart" }
```

### `SubagentStop`
Fires when a subagent/teammate **finishes a turn**.
```jsonc
{ "session_id", "transcript_path", "cwd", "permission_mode", "effort": { "level" },
  "agent_id": "aUX-Explorer-308f121a5c4e1c0c",
  "agent_type": "UX-Explorer",
  "agent_transcript_path": "…\\<session>\\subagents\\agent-aUX-Explorer-308f121a5c4e1c0c.jsonl",
  "last_assistant_message": "…final text…",   // present on natural completion; ABSENT when cut off
  "background_tasks": [ { "id", "type": "subagent|teammate", "status", "description", "agent_type" } ],
  "stop_hook_active": false, "session_crons": [],
  "hook_event_name": "SubagentStop" }
```
- `agent_transcript_path` is **exactly** the file `SubAgentReader` already enumerates
  (`subagents/agent-{agent_id}.jsonl`) — direct, zero-ambiguity correlation.
- `background_tasks[].type` discriminates `"teammate"` from `"subagent"`.
- `background_tasks` is a live snapshot of *remaining* running work at that instant.

### `TeammateIdle`
Fires ~0.4s **after** a teammate's `SubagentStop`, when that teammate goes idle. Leaner payload —
**no `agent_id`, no transcript path**:
```jsonc
{ "session_id", "transcript_path", "cwd", "permission_mode",
  "teammate_name": "UX-Explorer",        // join key == agent_type
  "team_name": "session-764dc348",        // derived from session id
  "hook_event_name": "TeammateIdle" }
```

### `SessionEnd`
Fires reliably when the main session ends. Field is **`reason`** (not `end_reason`).
```jsonc
{ "session_id", "transcript_path", "cwd", "reason": "prompt_input_exit|other|clear|logout|…",
  "hook_event_name": "SessionEnd" }
```

### Events that did **not** fire / don't exist on 2.1.195
`TaskCreated`, `TaskCompleted` never fired. The doc-claimed `exit_code`, `completion_status`,
`idle_reason`, `end_reason` fields do not exist.

## The semantic model (the crux)

The investigation surfaced the one rule the timer-based code can't express:

> **For a teammate, `SubagentStop` is a turn boundary, NOT a termination.** The same teammate is
> re-tasked and fires `SubagentStart` again (observed: `Devils-Advocate` ran Start→Stop, then
> Start→Stop again minutes later). A plain subagent's `SubagentStop` *is* terminal.

Resulting signal → state mapping:

| Entity | Signal | Meaning |
|---|---|---|
| Plain subagent | `SubagentStop` | **Done, permanently** (carries `last_assistant_message`) |
| Teammate | `TeammateIdle` (+ paired `SubagentStop`) | **Idle but alive** — exact replacement for the 90s stale guess |
| Teammate | `SubagentStart` after an idle | Re-tasked / working again |
| Teammate / session | `SessionEnd` | **Everything for that session is gone** |
| Teammate (explicit UI terminate) | `SubagentStop` with no following `SubagentStart`, dropped from `background_tasks` | Gone |

Discriminate plain-subagent vs teammate at the hook via `background_tasks[].type`, matching
`SubAgentReader`'s existing `in_process_teammate` notion.

## Critical caveat: hard kills orphan in-flight children

In a UI-terminated run, a session started **4** subagents but only **3** fired `SubagentStop` — an
in-flight `Explore` child was killed and never emitted a stop, the session jumped straight to
`SessionEnd`.

**Therefore `SessionEnd` must remain a backstop:** when it fires for a `session_id`, sweep every
agent still marked running under that session to terminated. Events are the fast path; `SessionEnd`
is the guarantee; the 90s timer becomes a last-resort fallback only.

## Proposed architecture

Keep Perch's existing pattern: **plugin hook scripts write marker/sidecar files; the data layer
watches and reads them.** No new IPC, consistent with `.mode` / `.notify` / `.history`.

```
Claude Code hook ──> plugins/perch/scripts/invoke.ps1 <action> ──> writes marker file
                                                                       │
                                          SubAgentReader / SessionMonitor reads marker
                                                                       │
                                                          overlay reflects real state
```

### Where to write markers

The richest correlation is the subagents directory itself, which we can derive in the hook from the
parent `transcript_path` (`{projects}/{enc-cwd}/{session}.jsonl` → strip `.jsonl`, append
`/subagents/`). That is the same directory `SubAgentReader` scans, so markers sit beside the
transcripts they describe:

- `agent-{agent_id}.stopped` — written on `SubagentStop`. Small JSON: `{ ts, agent_type,
  cut_off: <bool = last_assistant_message absent>, last_message_present }`.
- `agent-{agent_id}.idle` — written on `TeammateIdle`. `TeammateIdle` lacks `agent_id`, so the hook
  resolves it by matching `teammate_name` against the newest `agent-*.meta.json` whose role ==
  `teammate_name` (the `agent_id` is literally `a{teammate_name}-{hash}`, so a prefix match
  `a{teammate_name}-` is the cheap join). Marker carries `{ ts, teammate_name, team_name }`.
- On `SubagentStart`, **delete** any stale `.stopped` / `.idle` markers for that `agent_id`
  (handles teammate re-tasking — a re-started teammate is no longer idle/stopped).

`SessionEnd` sweep stays in the existing `cleanup` action: in addition to today's sidecar removal,
drop a `session.ended` marker (or simply let `SubAgentReader` treat "parent session gone" as
terminal for all its children — see below).

## Component changes

### 1. `plugins/perch/hooks/hooks.json`
Add three registrations alongside the existing ones, each calling `invoke.ps1` with a new action:
- `SubagentStart` → `invoke.ps1 agentstart`
- `SubagentStop` → `invoke.ps1 agentstop`
- `TeammateIdle` → `invoke.ps1 teammateidle`

(`SessionEnd` already maps to `cleanup`; extend that handler rather than adding a new one.)

### 2. `plugins/perch/scripts/invoke.ps1`
Add the three handlers. They must stay on the cheap/no-throw path the file already follows
(`$ErrorActionPreference = 'SilentlyContinue'`, always `exit 0`, never block). Each:
1. parse stdin JSON, derive the subagents dir from `transcript_path`,
2. write/delete the marker described above,
3. exit 0.

Privacy note: mirror the existing file's stance — store only `agent_id` / role / timestamps /
booleans. Do **not** persist `last_assistant_message` content; record only whether it was present
(the cut-off signal). Update the script's header comment accordingly.

### 3. `src/Data/SubAgentReader.cs`
- When enumerating `subagents/agent-{id}.*`, read the new `.stopped` / `.idle` markers.
- Classification precedence becomes: **explicit marker > transcript-tail heuristic > 90s timer**.
  - `.stopped` present (plain subagent) → terminated/removed.
  - `.idle` present (teammate) → `IsIdle = true` immediately (no waiting for the 90s window).
  - `.stopped` present for a teammate with no newer `.jsonl` activity → treat as idle-or-gone per
    the `SessionEnd`/`background_tasks` rule.
  - No marker → fall back to existing tail heuristic + `DefaultStaleAfter` (keeps behaviour when
    hooks aren't installed, e.g. older Claude Code or plugin not enabled).
- Keep the 90s window as the documented fallback; don't delete it.

### 4. `src/Data/ClaudeSession.cs`
The `SubAgent` record already has `IsTeammate` / `IsIdle` / `IsStale`. Consider adding
`TerminatedAt` (nullable) / `WasCutOff` (bool) if the overlay wants to show "finished" vs "cut off",
but this is optional polish — the core fix needs no schema change.

### 5. `src/Data/SessionMonitor.cs` (backstop)
On detecting a session has ended (its `SessionEnd` `cleanup` ran / session file gone / PID exited),
ensure all of that session's subagent rows are cleared even if individual `.stopped` markers never
arrived (the orphaned-`Explore` case). The 5-minute `NeedsAttention` window is unaffected — that's
UI grace, not termination, and stays.

## Edge cases the design must hold

1. **Teammate re-tasking** — `.idle`/`.stopped` cleared on next `SubagentStart`. ✔
2. **Hard kill orphans** — `SessionEnd` sweep clears in-flight children with no stop event. ✔
3. **Hooks not installed / older CC** — markers simply absent; reader falls back to the 90s timer. ✔
4. **Plugin update timing** — new hooks apply to new sessions; existing sessions keep working on the
   fallback path. (We observed settings-level hooks taking effect mid-session, but don't rely on it.)
5. **Marker/transcript races** — open with `FileShare.ReadWrite`, tolerate missing/partial markers,
   never throw out of a scan (existing data-layer rule).
6. **Path derivation on Windows** — reuse the same enc-cwd handling the reader already uses; don't
   re-encode `cwd` in the hook, derive the subagents dir from `transcript_path` which is already
   absolute and correct.

## Testing

Per `CLAUDE.md`, add fixtures + xUnit tests rather than throwaway scripts:

- Extend the synthetic `~/.claude` fixture tree with a `subagents/` dir containing
  `agent-*.jsonl` + `.meta.json` plus the new `.stopped` / `.idle` markers.
- `SubAgentReaderTests`: marker beats heuristic; teammate `.idle` → `IsIdle` without the timer;
  `.stopped` removes a plain subagent; re-task (marker absent again) flips back to working;
  no-marker path still honours the 90s fallback.
- A `SessionEnd`-sweep test: a running child with no `.stopped` is cleared once the session is gone.
- `invoke.ps1` handlers can be exercised with the recorded payloads in
  `~/.claude/perch-debug/perch-hooks.log.jsonl` as inputs (pipe `raw` into the script, assert the
  marker written) — a small PowerShell test or a fixture-driven check.

## Rollout

1. Land the plugin hooks + `invoke.ps1` handlers (no app behaviour change until the reader consumes
   markers — safe to ship first).
2. Land `SubAgentReader` marker consumption with the timer fallback intact.
3. Land the `SessionEnd` sweep backstop.
4. Update `CHANGELOG.md` (Unreleased) and bump via `/bump-version` when cutting a release.

## Cleanup of the investigation scaffolding

The debug logger is **not** part of this feature. Before/after implementing, remove:
- `~/.claude/perch-debug/` (the `log-hook.ps1` script + `perch-hooks.log.jsonl`).
- The six debug hook registrations added to `~/.claude/settings.json` (`SubagentStart`,
  `SubagentStop`, `TeammateIdle`, `TaskCreated`, `TaskCompleted`, `SessionEnd`).

## Open decisions

- **Marker location**: beside transcripts in `subagents/` (chosen — best correlation) vs the
  existing `~/.claude/sessions/` sidecar dir (consistent with current code but needs a name→id join
  for teammates). Recommendation: `subagents/`.
- **Show "cut off" vs "completed"** in the overlay using `last_assistant_message` presence — nice to
  have, defer unless wanted.
- **Keep `TaskCreated`/`TaskCompleted` hooks?** They never fired on 2.1.195; omit until a version
  emits them.
