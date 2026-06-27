# Daily Session Stats — Design & Implementation Plan

> Source idea (TODO.md → Bigger ideas):
> *"daily session stats — accumulate state-change timing from sidecar files; surface
> 'Today: 4 sessions, 3h 12m active' in tray right-click or history viewer"*

This plan expands that one-liner into a concrete, technically-grounded feature, grounded in
what the codebase can actually observe today. It also folds in the closely-related
*cost/token estimation* idea, because the data that makes stats interesting is the same data
that makes cost estimation trivial.

---

## 1. What we already have (grounded in the current code)

Before designing anything, here is the raw material that already exists on disk and in the app.

### 1a. Live session state — `~/.claude/sessions/{sid}.json`
Read every scan by `SessionMonitor.ReadSession`. Fields: `pid`, `sessionId`, `status`
(`idle`/`busy`/`waiting`), `waitingFor`, `cwd`, `updatedAt` (unix ms), `bridgeSessionId`.
Sidecars: `{sid}.mode`, `{sid}.notify`, `{sid}.history`.

`SessionMonitor` **already computes every state transition we care about** in memory:

- `_runningSince[pid]` — when the current continuous *Running* stretch began.
- `_idleSince[pid]` — when a `busy → idle` completion happened (drives "Needs Attention").
- `_lastRawStatus[pid]` — previous raw status, i.e. it sees each transition edge.
- It already raises `NeedsAttention` (busy→idle) and `AwaitingInput` (→waiting) events.

**The gap:** none of this is ever persisted. When the tray closes, all timing is lost, and
there is no record of sessions that ran while the tray wasn't up. "Daily stats" is therefore
fundamentally a *persistence + aggregation* feature, not a *new-measurement* feature.

### 1b. Full transcripts — `~/.claude/projects/{enc-cwd}/{sid}.jsonl`
Enumerated by `SessionHistory.ListAll`, parsed by `TranscriptParser`. Append-only, one JSON
record per line, each carrying a `timestamp`. **Verified by inspecting a real transcript**, the
records contain far more than the session file does:

- `type`: `user` / `assistant` / `system` / `summary` / `attachment` / `file-history-snapshot` …
- `timestamp` (ISO-8601) on every conversational record.
- `message.model` — e.g. `claude-opus-4-8`.
- `message.usage` on **every assistant record** — full token breakdown:
  `input_tokens`, `output_tokens`, `cache_creation_input_tokens`, `cache_read_input_tokens`,
  a `cache_creation.{ephemeral_1h,ephemeral_5m}` split, and
  `server_tool_use.{web_search_requests,web_fetch_requests}`, plus `service_tier`.
- `cwd`, `gitBranch`, `version`, `entrypoint`, `isSidechain` (sub-agent), tool-use blocks
  (with names → already summarised by `ToolSummary.Describe`), and `toolUseResult` blocks.

This is the richest, most durable source we have, and crucially it is **retroactive**: it
describes sessions that ran long before this feature shipped.

### 1c. Plugin hooks — `plugins/perch/scripts/invoke.ps1`
Fire on `UserPromptSubmit`, `PreToolUse`, `PostToolUse`, `Stop`, `SessionStart`, `SessionEnd`.
Today they only write `{sid}.mode` and handle `/afk` + `/history`. They are deliberately cheap
and privacy-conscious — the script comments explicitly note *"tool-call inputs/outputs are never
recorded"*. They run **even when the tray is not running**, which makes them the only way to
capture events during tray-down periods without re-reading transcripts.

---

## 2. Architectural options

The central decision is **where stats come from**. Three viable approaches:

### Option A — Transcript-derived (retroactive, on-demand)  ⭐ *recommended engine*
Compute stats by scanning the `.jsonl` transcripts under `~/.claude/projects`, bucketed by the
`timestamp` on each record. No new writing anywhere; reuse `SessionHistory` enumeration and a
lightweight stats-focused parser (we don't need the full `TranscriptParser` render path).

- **Pros:** Works from day one with full history (today, last week, all-time). Richest data —
  tokens, cost, models, tool mix, prompt counts, sub-agents, git branch, per-project splits —
  all for free. No hot-path writes, no new privacy surface (everything already on disk locally).
  Survives the tray being closed. Naturally correct across restarts.
- **Cons:** "Active time" must be *inferred* from timestamp gaps (see §4) rather than measured.
  Scanning hundreds of transcripts has a cost — mitigated by (i) only reading files whose
  `LastWriteTime` falls in the window, (ii) caching per-file results keyed by path+size+mtime,
  (iii) doing it off the UI thread exactly like `SessionHistory.ListAll` already does.

### Option B — Tray-observed accumulation (live, precise wall-clock)
Have the running tray persist every transition it already computes (busy↔idle↔waiting, with
`_runningSince`/`_idleSince`) to a local append-only log, then aggregate that.

- **Pros:** Gives a *true* measured "active" wall-clock (we know exactly when a session was
  Running), including the sub-agent-keeps-it-busy nuance the monitor already handles. Tiny writes.
- **Cons:** Blind whenever the tray isn't running (no history before the feature, gaps when the
  user closes it). No token/cost/tool data (the session file doesn't carry it). Needs a new
  persistence format and careful daily-rollover handling.

### Option C — Hook-side accumulation (complete, but invasive)
Extend `invoke.ps1` to append a timing event on each hook (Stop, SessionStart/End, etc.).

- **Pros:** Captures activity even with the tray down, at the source.
- **Cons:** Adds work to the per-tool-call hot path the plugin intentionally keeps minimal;
  expands the privacy footprint the script currently advertises as nil; PowerShell file-append
  contention across concurrent sessions; still no token data (hooks don't get usage). Highest
  risk for the least marginal gain over A.

### Recommendation
**Primary engine = Option A (transcript-derived).** It is retroactive, the richest, requires no
new hot-path or privacy surface, and reuses machinery that already exists. **Optionally augment
with a thin slice of Option B** *only* for the single metric transcripts estimate worst — precise
"active wall-clock time" — by persisting the tray's already-computed Running stretches. Treat B as
a Phase 2 accuracy upgrade, not a dependency. Avoid Option C unless a concrete need appears.

---

## 3. Metrics catalog — what's interesting *and* feasible

Tiered by value-for-effort. Everything in Tier 1/2 is computable from transcripts (Option A).

### Tier 1 — the headline ("Today" at a glance)
- **Sessions today** — distinct `sessionId`s with ≥1 record timestamped today. (The literal TODO ask.)
- **Active time today** — sum of inter-record gaps below an idle threshold (see §4), per session,
  summed. Renders as `3h 12m`. (The other half of the TODO ask.)
- **Prompts / turns** — count of `user` records (excluding sidechain + tool_result-only records).
- **Tool calls** — count of `tool_use` blocks, today.
- **Tokens** — summed `output_tokens` and `input_tokens` (+ cache read/creation shown separately).
- **Estimated cost** — see §5.

### Tier 2 — the "interesting" layer (cheap, high insight)
- **Per-project breakdown** — group by `cwd`/project name: "perch: 5 sessions, 2h 40m".
- **Busiest hour / activity heatmap** — bucket active minutes into 24 hourly bins → a sparkline
  or mini heatmap ("you do your best work at 10pm").
- **Tool mix** — top tools by count (Bash 142, Edit 88, Read 60…) via `ToolSummary`/raw names.
- **Model split** — time/tokens per model (Opus vs Sonnet vs Haiku), since `message.model` is per-record.
- **Thinking ratio** — share of assistant records that are `thinking` blocks.
- **Sub-agent usage** — count of sidechain (`isSidechain`) spawns / Task invocations.
- **Longest session** and **most active project** of the day.
- **Web tool usage** — `server_tool_use` web_search/web_fetch counts.

### Tier 3 — trends & flair (nice-to-have, more work)
- **7-day / 30-day trend** — daily active-time bars; "this week vs last week".
- **Streaks** — consecutive days with ≥1 session ("12-day streak").
- **All-time totals** — lifetime sessions, tokens, active hours.
- **Per-git-branch** breakdown (the transcripts carry `gitBranch`).
- **Personal records** — busiest day ever, longest single session.
- **"Acceptance" / mode mix** — share of time spent in each permission mode (needs `.mode`
  history or the transcript's `permissionMode`/`mode` records — present but sparser; verify).

---

## 4. Defining "active time" (the one genuinely fuzzy metric)

Transcripts give discrete timestamps, not spans. Proposed definition:

> Walk a session's records in time order. Sum the gap between consecutive records. If a gap
> exceeds an **idle threshold T**, count only T toward active time (the user walked away);
> otherwise count the full gap. Add a small tail (e.g. +T or +30s) after the last record so a
> one-shot exchange isn't counted as zero.

- Recommended default **T = 5 minutes**, which conveniently matches the app's existing
  `NeedsAttentionMinutes = 5` notion of "recently finished".
- This is an *engagement* estimate, not CPU time. We should label it honestly ("active",
  with a tooltip explaining it's wall-clock engagement, gaps over 5m excluded).
- **Phase 2 upgrade (Option B):** when the tray was running, prefer its measured Running
  stretches for that window and fall back to the transcript estimate otherwise. Strictly optional.

---

## 5. Cost & token estimation (folds in the separate TODO item)

The TODO's *cost/token estimation* idea proposed deriving cost from "the 5-hour rate-limit delta".
That is unnecessary and less accurate now that we know **every assistant record carries exact
`usage`**. Plan:

- Sum tokens by model and by class (input, output, cache-creation, cache-read — they price
  differently). Cache reads are ~10% of input price; cache-creation ~125%; getting the classes
  right matters for an honest number.
- Maintain a small **per-model price table** (input/output/cache rates per million tokens),
  editable in settings, with sensible built-in defaults for current Claude models. Unknown model
  ids fall back to "—" rather than guessing.
- **Subscription caveat:** the user is on a Claude subscription (Pro/Max), so literal API dollars
  don't leave their wallet. Present cost as *"equivalent API cost"* (what this would have cost on
  pay-as-you-go) — useful as a value/intensity signal, clearly labelled to avoid implying a bill.
  → This is **Open Question 4**.

---

## 6. Surfacing / UX

The TODO suggests "tray right-click or history viewer". Options, roughly increasing in effort:

1. **Tray right-click summary line** — add a disabled header line to the existing tray menu:
   `Today: 4 sessions · 3h 12m active`. Cheapest; zero new windows. Good as a *first slice*.
2. **Tray submenu** — a "Stats ▸" submenu with Today / This week / All-time leaf lines.
3. **Panel inside the History viewer** — the viewer already lists every transcript and runs the
   off-thread scan; a "Stats" toggle (alongside Readable/Raw) showing an aggregate dashboard is a
   natural fit and reuses the dark-chrome window. ⭐ *recommended home for the rich view.*
4. **Dedicated Stats window** — its own first-class dark-chrome window (like `HistoryViewerForm`),
   opened from the tray menu. Most room for heatmaps/sparklines/trends; most work.

**Decided (Q2):** ship (1) the tray summary line as the immediate win, and build the rich view
as **(4) a dedicated Stats window** — its own first-class dark-chrome `Form` modelled on
`HistoryViewerForm`, opened from the tray menu's "Session stats…" item (sibling to "Session
history…"). This gives trends, heatmaps and sparklines room to breathe.

Rendering note: charts can be done with cheap owner-draw (the codebase already hand-paints chrome,
usage bars, and the auto-close countdown), so no charting dependency is needed for sparklines/bars.

---

## 7. Data model & storage

- **Option A needs almost no storage** — stats are computed on demand and held in memory. Add a
  **per-transcript cache** keyed by `path + length + mtime` so re-aggregation is incremental
  (only changed/new files are re-parsed). Optionally persist this cache to
  `%AppData%/Perch/stats-cache.json` so a fresh launch is fast.
- **If we add Option B**, persist Running stretches as append-only newline JSON under
  `%AppData%/Perch/activity/` (e.g. one file per day, `{date}.jsonl`, records
  `{sid, pid, project, start, end}`), flushed when a Running stretch ends. Small, rotatable,
  easy to prune (e.g. keep 90 days).
- A new `SessionStatsService` class owns enumeration + parsing + caching + aggregation, exposing
  e.g. `StatsForDay(DateOnly)`, `StatsForRange(from,to)`, `AllTime()`. Lives off the UI thread,
  mirrors the `Task.Run(...).ContinueWith(BeginInvoke)` pattern already used in `HistoryViewerForm`.

---

## 8. Implementation phases

1. **Phase 0 — Stats engine.** `SessionStatsService`: enumerate transcripts (reuse
   `SessionHistory`), lightweight stats parser (timestamps, usage, model, tool names, sidechain,
   user-turn detection), per-file cache, day/range aggregation. Pure logic, unit-testable.
   Exposes `StatsForDay`, `StatsForRange`, `AllTime`.
2. **Phase 1 — Tray summary line.** Wire `StatsForDay(today)` into the tray context menu header
   (`Today: 4 sessions · 3h 12m`). Smallest shippable slice; validates the engine end-to-end.
3. **Phase 2 — Stats window (Tier 1 + 2).** New dedicated `StatsForm` (dark chrome like
   `HistoryViewerForm`), opened from a "Session stats…" tray item. Headline numbers, per-project
   breakdown, tool mix, hourly heatmap (owner-draw), model split, tokens **and** equivalent cost.
4. **Phase 3 — Trends & flair (Tier 3).** 7/30-day bars, streaks, all-time totals, personal
   records, per-git-branch. Adds a day/week/all-time scope switch to the window.
5. **Phase 4 (optional, deferred per Q1) — Measured active time.** Only if the inferred active
   time proves unsatisfactory: persist tray Running stretches (Option B) and prefer them over the
   transcript estimate where available.
6. **Settings.** Toggles: show stats line in tray, idle threshold T (default 5 min), per-model
   price table, cost on/off, cache-retention/prune. Extend `AppSettings` + `SettingsForm`.

---

## 9. Edge cases & risks

- **Day boundary / timezone.** Bucket by local time (`DateTimeOffset.LocalDateTime`, as the
  parser already does). A session spanning midnight contributes to both days proportionally.
- **Scan cost.** Hundreds of transcripts; some large. Mitigate with mtime windowing + per-file
  cache + off-thread compute. Never block the UI or the monitor scan.
- **Concurrent writes.** Active transcripts are being appended live — open with
  `FileShare.ReadWrite` (as existing readers do) and tolerate a truncated trailing line.
- **Sub-agent double-counting.** `isSidechain` records share the parent session — decide whether
  sub-agent tokens/tools roll up into the parent (recommended: yes, with a separate sub-agent
  count) rather than counting as separate "sessions".
- **"Session" definition.** A `sessionId` can be resumed across days; count it per-day it was
  active rather than once globally.
- **Privacy.** Option A reads only local files the app already reads; no new egress. Keep it that
  way — stats stay on the machine, never pushed (unlike ntfy alerts).
- **Token/usage absence.** Older transcripts or non-assistant records lack `usage`; sum what
  exists and never fabricate. Unknown models → cost shows "—".

---

## 10. Open questions

> Recommendations are baked in; answers will be recorded here once confirmed.

**Q1 — Data source / architecture.**
Transcript-derived engine (retroactive, rich, no hot-path writes) vs tray-observed accumulation
(precise wall-clock, but no history/tokens and blind when tray is down) vs hybrid.
*Recommendation: transcript-derived primary, optional tray-observed augmentation for active-time later.*
**Answer:** ✅ **Transcript-derived.** Option A is the engine. Option B (measured active time) is
explicitly *not* in scope unless we later decide the inferred active-time number isn't good enough.

**Q2 — Where should the rich view live?**
Tray summary line only / submenu / a panel inside the History viewer / a dedicated Stats window.
*Recommendation: tray summary line now + rich view as a panel in the History viewer (or a dedicated window if we want charts to breathe).*
**Answer:** ✅ **Tray summary line + a dedicated Stats window.** Build a new first-class
dark-chrome window (modelled on `HistoryViewerForm`), opened from the tray menu, with room for
heatmaps/sparklines/trends. The one-line tray summary still ships first as the quick win.

**Q3 — Which metrics matter most to you?**
(Sessions, active time, prompts, tool mix, tokens, cost, per-project, hourly heatmap, model split,
sub-agents, trends/streaks, all-time records …) — so Tier priorities match what you'd actually look at.
**Answer:** ✅ **All of it — Tiers 1, 2 and 3.** Core headline, tokens & cost, breakdowns
(per-project, tool mix, hourly heatmap, sub-agents), and trends & flair (7/30-day, streaks,
all-time records). Build in tier order, but everything is in scope.

**Q4 — Cost: dollars or tokens-only?**
You're on a subscription, so literal $ doesn't leave your wallet. Show "equivalent API cost"
(clearly labelled), tokens-only, or both?
*Recommendation: both, with cost labelled "equivalent API cost".*
**Answer:** ✅ **Both.** Show token totals *and* an "equivalent API cost" figure, clearly labelled
so it never implies an actual bill.

**Q5 — (deferred default) Active-time idle threshold.**
Default **5 min** (matches existing `NeedsAttentionMinutes`). Adjustable in settings.
**Answer:** Default **5 min** accepted (no objection raised); adjustable in settings.
