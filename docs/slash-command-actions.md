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
| `/compact` | Compacts context (emits `status` records) | ✅ verified | Tier-3 — drop a marker, continue |
| `/rename` | Renames the session | ⚠ (title record?) | Tier-3 — update the title |
| `/init` | **Writes/updates `CLAUDE.md`** | ✅ (as text; may run a Write turn) | Renders; confirm the file-write turn |
| `/export` | Exports the conversation (file/clipboard) | ⚠ | Confirm what it returns headlessly |
| `/memory` | Opens a memory file in an editor | ❌ editor | ⚠ likely TUI-only → native memory view or omit |
| `/add-dir <path>` | Adds a working directory | ⚠ | Native folder picker (nicer) |
| `/effort <level>` | Sets reasoning effort | ✅ (`/effort` as text) | **Native** — the effort pill (done) |
| `/import <path>` | Imports context from a file | ⚠ | Confirm arg passing |
| `/autocompact` | Toggles auto-compaction | ⚠ | Reflect the setting |
| `/heapdump` | Writes a heap dump file | ⚠ | Power-user; keep out of the palette |
| `/reload-plugins`, `/reload-skills` | Reloads plugins/skills | ⚠ | Power-user; render-as-text if it works |
| `/goal` | Sets/updates the session goal | ⚠ | Render-as-text |
| `/color`, `/fast` | Toggle display prefs | ❌ N/A | Perch owns its own look → omit |

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

**Open question for review:** the ⚠ rows want a live spike to confirm what they actually do over stream-json
before we wire a native surface or decide to omit them. Say which ones matter and I'll spike them (each is a
one-shot capture like the `/clear` and `/compact` ones already done).
