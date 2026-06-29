# Perch — Team Member Detection & Visualisation Plan

## Design decisions (locked in)
- **Roster persists** while the parent session is alive; idle members are dimmed, the working one(s) highlighted.
- **Per-member colour** from the meta `color` field (green/yellow/blue/…).
- **Scope:** overlay → stats/history → notifications.

## Core technical finding
Teammates already flow through `SubAgentReader.ScanBackground` (`SubAgentReader.cs:66`) — they're just
rendered as anonymous purple "running" rows. The discriminator is **`taskKind == "in_process_teammate"`**
in the `.meta.json`; `name` and `color` are the display ingredients. So this is mostly: parse 3 fields →
carry them → branch the render + lifecycle.

### Meta sidecar shape
`…/{sessionId}/subagents/agent-*.meta.json`:

| Field | Ordinary sub-agent | Teammate |
|---|---|---|
| `taskKind` | *(absent)* | `"in_process_teammate"` ← **the discriminator** |
| `name` | *(absent)* | `"arch-explorer"`, `"devils-advocate"`, `"ux-explorer"` |
| `teamName` | *(absent)* | `"session-a0a997f1"` |
| `color` | *(absent)* | `"green"` / `"yellow"` / `"blue"` |
| `agentType` | `"Explore"` (built-in) | custom type (= the name) |
| `toolUseId` | present (the launching Task) | *(absent)* |
| `spawnDepth` | `1` | `0` |

The lead drives teammates via `SendMessage {to: "<name>"}`; each teammate's "what it's doing" can be
derived from its own transcript tail (the last `tool_use`).

---

## Phase 1 — Data model & detection (no UI change)
**Goal:** teammates become first-class, distinct from sub-agents, in the data layer.

1. **Extend `SubAgent`** (`ClaudeSession.cs:33`) with `bool IsTeammate`, `string? TeamName`,
   `string? Color`, and `string? Activity`. Keep `AgentId`/`Description`/`AgentType`.
2. **`ReadAgentMeta`** (`SubAgentReader.cs:93`) — read `taskKind`, `name`, `teamName`, `color`.
   When `taskKind=="in_process_teammate"`, prefer `name` as the label and set `IsTeammate=true`.
3. **Lifecycle split in `ScanBackground`** (`SubAgentReader.cs:66`): today it drops any agent whose tail
   isn't "running". Change to: **ordinary sub-agents** keep the running-only rule; **teammates** are
   returned whenever their transcript exists (alive), each tagged with a derived state — `Working`
   (tail busy, per existing `ClassifyRunning`) vs `Idle` (tail = finished assistant turn). Extend
   `ClassifyRunning` to also return the **last `tool_use` name** → the `Activity` string ("Reading…",
   "Searching…", etc.; reuse `ToolSummary.cs`).
4. **Don't let idle teammates force the parent "Running."** `SessionMonitor.cs:291` upgrades a parent to
   Running whenever `subAgents.Count > 0`. Gate that on *working* sub-agents/teammates only, or a session
   with idle teammates would never show idle. **This is the subtlest correctness point.**
5. **Tests:** new fixtures under `tests/Perch.Tests/fixtures/claude/` — a `subagents/` dir with one
   teammate meta (`in_process_teammate` + colour) and one plain `Explore` meta; assert detection,
   idle-vs-working classification, and that idle teammates don't flip the parent state.

## Phase 2 — Overlay visualisation
**Goal:** the `@name` + colour + activity rows.

1. **`DisplayRow`** (`OverlayForm.cs:66`) already carries `SubAgent?`; branch rendering on `Sub.IsTeammate`.
2. **`DrawSubAgentRow`** (`OverlayForm.cs:952`) → teammate variant:
   - **user/person glyph** instead of the dot.
   - label `@arch-explorer` in the member's colour; activity ("Searching the codebase…") in muted text;
     **"idle"** vs the live activity for state.
   - **Dim idle members** (lower alpha on glyph+text), full-strength for working. Colour map:
     green/yellow/blue/red/… → RGB, added to `Theme` (`SettingsControls.cs`) as a `TeamColor(string)`
     helper so it's centralised, not hand-coded per call site.
3. **Row height / font:** size the glyph rect from `font.Height`, not a magic number (DPI-safe).
4. **Ordering:** group teammates above ordinary sub-agents under their parent.

## Phase 3 — Stats & history
1. **Stats** (`StatsForm.cs:328`): split the "includes N sub-agent runs" line into sub-agents vs teammates,
   and (optional) roll teammate token usage from their transcripts.
2. **History** (`SessionHistory.cs` / `HistoryViewerForm`): attribute teammate transcripts to the parent
   session so a past session shows who was on the team. Lower priority.

## Phase 4 — Notifications
1. **`NotificationService.cs`**: notify when a **teammate goes idle/finishes** (the lead likely needs to
   act). Per-teammate state cache (like `_hadRunningSubs` in `SessionMonitor.cs:288`) keyed by
   `teamName+name`, firing on transitions only.
2. Gate behind a new settings toggle in `AppSettings.cs` (opt-in).

---

## Risks / watch-items
- **Format drift:** leans on Claude Code 2.1+'s `subagents/` layout and the `taskKind` field
  (experimental, `CLAUDE_CODE_EXPERIMENTAL_AGENT_TEAMS`). Parse best-effort, tolerate missing fields,
  keep the legacy fallback intact (`SubAgentReader.cs:58`).
- **Idle detection reliability:** "finished assistant turn = idle" is the same heuristic `ClassifyRunning`
  uses; a teammate mid-long-shell-command must stay "working" (existing logic handles this — preserve it).
- **Clutter:** persistent rosters could make the overlay tall for big teams. "Dim idle" mitigates;
  consider a per-session collapse affordance if teams get large (defer until observed).

---

**First cut:** Phase 1 + Phase 2 — delivers the `@teammember` + activity visual, fully tested,
smallest blast radius. Phases 3–4 layer on once detection is proven against live teams.
