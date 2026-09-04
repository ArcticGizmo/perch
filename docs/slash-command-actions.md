# Built-in slash commands that have wizards or actions

For review — which built-in (non-skill) commands do **more than print info** when run: they open an
interactive wizard/picker, or take an action / side effect. Pure read-only readouts (`/context`, `/cost`,
`/status`, `/recap`, `/insights`, `/pr-comments`, `/review`, `/release-notes`) are listed at the bottom for
completeness but need no special handling — they just render markdown.

"Headless?" = does it do anything meaningful over the stream-json protocol Perch drives (vs. needing the real
TUI). Some are **inferred** from behaviour/knowledge and want a live spike before we commit — flagged ⚠.

## A. Wizards / interactive pickers (need a TTY — the real problem cases)

| Command | What happens in the terminal | Headless? | Perch plan |
|---|---|---|---|
| `/config` | Opens the settings editor (arrow-key form) | ❌ TUI | **Map to Perch Settings** window |
| `/model` | Model picker list | ❌ TUI (but a plain `/model <name>` works) | **Native** — the model pill (done) |
| `/resume` | Session picker | ❌ TUI | **Map to Perch's launcher/picker** |
| `/mcp` | MCP server list + per-server auth (browser) | ❌ TUI | **Native MCP panel** from init's `mcp_servers`; auth → defer |
| `/permissions` | Permission-rules editor | ❌ TUI | **Native** — the mode pills (partly done) |
| `/login`, `/logout` | Auth flow (opens browser) | ❌ | Defer / hand off to a terminal |
| `/terminal-setup` | One-time terminal keybinding install | ❌ N/A | Omit |
| `/statusline` | Status-line setup wizard | ❌ TUI | Omit (or map to settings) |
| `/hooks` | Hooks configuration UI | ❌ TUI | Perch manages its own hook → omit/settings |
| `/bug` | Feedback form → submits a report | ⚠ | Native form or omit |
| `/agents` | Subagent manager wizard | — | **Removed** (no longer supported) |
| `/install-github-app` | GitHub app setup wizard | ❌ | Omit |
| `/theme` | Theme picker | ❌ TUI | Perch owns theming → omit |

## B. Actions / side effects (do something; may also print)

| Command | What happens | Headless? | Perch plan |
|---|---|---|---|
| `/clear` | **New session id, context wiped** | ✅ verified | **Done** — thread resets on the new init |
| `/compact` | Compacts context (emits `status` records) | ✅ verified | **Done (code)** — live progress-meter row (elapsed timer + % + freed tokens); echoes `[instructions]` |
| `/rename` | Renames the session | ✅ | **Done** — title flows to `TitleChanged` |
| `/init` | **Writes/updates `CLAUDE.md`** | ✅ (as text; may run a Write turn) | **In catalogue (plain text)** — renders; confirm the file-write turn at dogfood |
| `/export` | Exports the conversation (file/clipboard) | ❌ "not available in this environment" (dogfood 2026-09-04) | **Removed from the catalogue** |
| `/memory` | Opens a memory file in an editor | ❌ "not available in this environment" (dogfood 2026-09-04) | **Removed from the catalogue** |
| `/add-dir <path>` | Adds a working directory | ❌ "not available in this environment" (dogfood 2026-09-04) | **Removed from the catalogue** |
| `/effort <level>` | Sets reasoning effort | ✅ (`/effort` as text) | **Native** — the effort pill (done) |
| `/import <path>` | Imports context from a file | ⚠ (advertised) | **In catalogue (plain text)** — arg rides through in the sent text |
| `/autocompact` | Perch-managed early auto-compaction | native modal | **Done (code)** — opens a modal (toggle + threshold slider); Perch fires `/compact` at the chosen fill % |
| `/heapdump` | Writes a heap dump file | ⚠ | **Kept out of the palette** (still works if typed) |
| `/reload-plugins`, `/reload-skills` | Reloads plugins/skills | ⚠ | **Kept out of the palette** (power-user; still send as text if typed) |
| `/goal` | Sets/updates the session goal | ⚠ (advertised) | **In catalogue (plain text)** — renders |
| `/color`, `/fast` | Toggle display prefs | ❌ N/A | **Omitted** — Perch owns its own look |

> **Dogfood finding (2026-09-04):** the commands that return "not available in this environment" over
> stream-json (`/export`, `/memory`, `/add-dir`) are exactly the ones **absent from the CLI's advertised
> built-in list**; the advertised commands (`/compact`, `/autocompact`, `/goal`, `/import`, `/init`, …) work.
> So the advertised list, while a subset for readouts, is a reliable *positive* signal for actions — the
> non-advertised action commands were pulled from the palette.

## C. Read-only info (no wizard, no side effect — just render markdown)

`/context`, `/cost`, `/status`, `/usage` (→ **native rich panel**, Claude-Desktop-style), `/usage-credits`,
`/extra-usage`, `/recap`, `/insights`, `/advisor`, `/list-agents`, `/review`, `/pr-comments`,
`/release-notes`, `/doctor` (⚠ may be interactive).

## Removed / never surfaced

- `/agents` — no longer supported (removed 2026-09-04).
- `/help` — doesn't work here (removed 2026-09-04).
- `/vim` — N/A for the Perch composer.
- Internal: `__remote-workflow`, `workflow-launch-exec` — never shown.

---

## Progress

- **Dogfood round 1 (2026-09-04) — reworked `/compact` + `/autocompact`, pruned the dead commands.**
- **`/compact` — DONE (code), now a live progress meter.** Feedback: "show a progress meter so we know where
  we are" (the CLI's own bar reads `Compacting conversation… (18s) … 18%`). `SessionConversation` emits a
  `CompactionItem` (not a static note) when `/compact` is sent; `SessionThreadView` renders it as a centred
  card with a **determinate bar when a percentage arrives** and an **indeterminate bar + Perch's own
  elapsed-seconds timer** otherwise (so it animates even when the stream carries no percent), settling to
  "✓ Compacted · freed N · Ms" on the turn's result (freed = context before − after). The CLI's
  `system`/`subtype:"status"` records are parsed into a `StatusEvent` (percent from a numeric field or an
  `NN%` in the text, defensively; null → indeterminate), consumed only while a compaction is live. Tests:
  `Compact_ShowsAProgressRowThenFinalises`, `Status_WithoutAnActiveCompaction_IsIgnored`,
  `StreamJsonParserTests.CompactStatus_ParsesAPercentFromFieldOrText`. **Owed:** confirm the real `status`
  record shape live so the determinate bar populates (else the elapsed-timer fallback, which is fine).
- **`/autocompact` — DONE (code), reworked into a Perch-managed modal.** Feedback: "show a modal with a slider
  to represent where compaction should happen." The old plain-text toggle (+ `AutoCompact` mirror bool) was
  removed. `/autocompact` is now a **Native** command: `SessionWindow` opens an in-window modal (scrim + card,
  Esc/scrim close) with an on/off toggle and a threshold **slider** (50–95%). When enabled, Perch runs
  `/compact` itself once a session's context fill crosses the threshold — `MaybeAutoCompact` fires once per
  crossing (armed → disarmed until the fill drops back below), guarded to a settled live session with a
  completed turn (never on attach, a running/queued turn, or a pending permission), deferred to let the
  state-change unwind, and preceded by an "auto-compacting · context reached N%" note. Persisted in
  `AppSettings` (`SessionAutoCompactEnabled` default **off** so nothing auto-spends tokens unasked, +
  `SessionAutoCompactThresholdPercent` default 80; both in `SettingsRegistryTests.NotSettings`), pushed via
  `SetAutoCompactConfig` and fanned out through the `AutoCompactChanged` app event; the context tooltip
  reflects it. **Owed:** live dogfood of the modal + a real threshold-crossing fire.
- **Pruned the dead commands.** `/export`, `/memory`, `/add-dir` returned "not available in this environment"
  over stream-json (dogfood) and were **removed from the catalogue** — they're the ones absent from the CLI's
  advertised list. `/goal`, `/import` (both advertised) stay. **Kept out of the palette** (still send as text
  if typed): `/heapdump`, `/reload-plugins`, `/reload-skills`. **Omitted** (Perch owns its look): `/color`,
  `/fast`.

---

**Open question for review:** the ⚠ rows want a live spike to confirm what they actually do over stream-json
before we wire a native surface or decide to omit them. Say which ones matter and I'll spike them (each is a
one-shot capture like the `/clear` and `/compact` ones already done).
