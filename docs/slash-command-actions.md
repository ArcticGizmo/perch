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
| `/compact` | Compacts context (emits `status` records) | ✅ verified | **Done (code)** — marker + echoes `[instructions]`; live dogfood owed |
| `/rename` | Renames the session | ⚠ (title record?) | Tier-3 — update the title |
| `/init` | **Writes/updates `CLAUDE.md`** | ✅ (as text; may run a Write turn) | **In catalogue (plain text)** — renders; confirm the file-write turn at dogfood |
| `/export` | Exports the conversation (file/clipboard) | ⚠ | **In catalogue (plain text)** — confirm what it returns headlessly |
| `/memory` | Opens a memory file in an editor | ❌ editor | **In catalogue (plain text)** — ⚠ may be TUI-editor-only (could hang the turn); if so, intercept → native memory view. Test first |
| `/add-dir <path>` | Adds a working directory | ⚠ | **In catalogue (plain text)** now; a native folder picker is the nicer surface later |
| `/effort <level>` | Sets reasoning effort | ✅ (`/effort` as text) | **Native** — the effort pill (done) |
| `/import <path>` | Imports context from a file | ⚠ | **In catalogue (plain text)** — arg rides through in the sent text |
| `/autocompact` | Toggles auto-compaction | ⚠ | **Done (code)** — tracks state, marks it, tunes the context tooltip; live dogfood owed |
| `/heapdump` | Writes a heap dump file | ⚠ | **Kept out of the palette** (still works if typed) |
| `/reload-plugins`, `/reload-skills` | Reloads plugins/skills | ⚠ | **Kept out of the palette** (power-user; still send as text if typed) |
| `/goal` | Sets/updates the session goal | ⚠ | **In catalogue (plain text)** — renders |
| `/color`, `/fast` | Toggle display prefs | ❌ N/A | **Omitted** — Perch owns its own look |

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

- **Plain-text actions — surfaced (code, 2026-09-04).** `/goal [text]`, `/import <path>` and `/add-dir
  <path>` added to `SlashCommandCatalog` (Tier 1, plain text); `/init`, `/export`, `/memory` were already
  there. These need no reaction code — they send as text and the CLI's output renders through `MarkdownView`,
  and any argument rides through in the sent text (so `/import`/`/add-dir` arg passing is automatic). **What's
  left is a live spike for the ⚠ ones**: does `/export` return a path or content headlessly; does `/memory`
  render or block on a TTY editor (if it blocks, intercept it and open a native memory view instead of
  sending text); does `/add-dir` take effect over stream-json (if so, a native folder picker is the nicer
  follow-up). **Kept out of the palette** (they still send as text if a power-user types them): `/heapdump`,
  `/reload-plugins`, `/reload-skills`. **Omitted** (Perch owns its own look): `/color`, `/fast`.
- **`/autocompact` — DONE (code, 2026-09-04, branch `session-control-poc`).** Sent as plain text (the CLI
  renders its own confirmation) and Perch mirrors the setting: `SessionConversation.AutoCompact` (a bool,
  default **on** to match the CLI) is flipped by a bare `/autocompact` or set by an explicit
  `on`/`off`/`enable`/`disable`/`true`/`false`/`yes`/`no` argument, with a `NoteItem` marker either way. The
  context-pill tooltip in `SessionWindow` now reads that state — it only promises "a compaction is coming as
  it nears full" when auto-compaction is on, and otherwise says it won't shrink on its own (run `/compact`).
  Added `/autocompact` (`[on|off]`) to `SlashCommandCatalog` as a `SessionMutating` entry. Tests:
  `SessionConversationTests.Autocompact_TogglesStateAndMarksIt` + `Autocompact_HonoursAnExplicitArgument`.
  **Caveat/dogfood owed:** the CLI never reports the *initial* auto-compaction value, so a session that
  started with it already off reads as on until the user toggles it here; confirm the real toggle semantics
  (bare toggle vs. explicit arg) and the confirmation text live.
- **`/compact` — DONE (code, 2026-09-04, branch `session-control-poc`).** It stays a plain-text send (that's
  what makes the CLI compact) and Perch *reacts*: `SessionConversation.AddUserPrompt` detects the `/compact`
  command and appends a `NoteItem` marker after the command chip — "compacting the conversation to free up
  context…", or "…(keeping: <instructions>)…" when an argument is given (the `[instructions]` ride through in
  the sent text; nothing extra needed for arg passing). The progress `status` system records the CLI emits
  while compacting are already ignored by `StreamJsonParser` (`ParseSystem` returns `[]` for any non-`init`
  subtype) — locked by a `UnknownOrMalformedLines_YieldNothing` InlineData case. Context/usage numbers
  self-correct on the compaction turn's `result` (the standard `ContextTokens = latest prompt` path). Tests:
  `SessionConversationTests.Compact_DropsAMarkerAndEchoesInstructions` +
  `OrdinaryPrompt_DropsNoCompactionMarker`. **Live dogfood owed:** confirm the compaction turn ends with a
  `result` (so the spinner clears) and that the context pill visibly drops; if a future build emits a
  `compact_boundary` system record with `pre_tokens`, the marker could be enriched to report tokens freed.

---

**Open question for review:** the ⚠ rows want a live spike to confirm what they actually do over stream-json
before we wire a native surface or decide to omit them. Say which ones matter and I'll spike them (each is a
one-shot capture like the `/clear` and `/compact` ones already done).
