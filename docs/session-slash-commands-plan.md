# Perch rich-session slash-command interop — plan & checklist

**Branch:** `session-control-poc` · **Phase:** this is Phase 2 of `docs/session-ui-plan.md`
("Slash commands: verify which work over stream-json; surface a command palette").
**Scope:** the **built-in / special** slash commands only — the ones baked into the `claude` CLI
(`/model`, `/usage`, `/context`, `/compact`, …). **Skills are explicitly out of scope** here (they are
user/project/plugin commands like `/grill-me`, `/bump-version`, `/nexus:*`, `/code-review`; they already
flow as plain text and need no special interop). This doc is the working checklist so a Perch session can
pick up one command at a time.

---

## How slash-command interop works (verified live, claude 2.1.247, Windows, 2026-09-04)

Three mechanisms, in preference order:

1. **Plain text over stream-json — the default, and it just works.** Sending `{"type":"user",
   "message":{"role":"user","content":[{"type":"text","text":"/context"}]}}` on stdin **executes the
   command** and returns its output as an ordinary **assistant `text` block (markdown)** — not literal
   prose. Verified: `/context` returned a real `## Context Usage` table; `/cost` and `/status` returned
   their readouts; `/compact` actually compacted (emitting `system` records with `subtype:"status"` while
   it worked). **Consequence:** for most commands, interop = *detect the leading `/`, send the text,
   render the returned markdown* — which the thread view already does (`MarkdownView`). No new protocol.
2. **Control request — for the few things Perch already drives structurally.** `set_model`,
   `set_permission_mode` (and `interrupt`) are already sent as control requests from
   `ClaudeSessionController`; the model / mode / effort pills own these. Prefer the native pill over the
   text command for these, but the text command is a fine fallback.
3. **Native Perch UI — where Perch already has the data or a better surface.** `/usage`, `/cost`,
   `/context`, `/config`, `/agents`, `/mcp`, `/resume` all have (or can have) a richer Perch surface than
   a markdown dump. These render-as-text for free today; a native panel is an *upgrade*, tracked below.

### Two findings that shape the palette

- **The advertised `slash_commands` list (from the `system/init` record) is a *subset* and is polluted
  with this account's skills.** In `-p` mode the CLI advertises far fewer than the TUI shows, yet
  commands *not* in the list (e.g. `/cost`, `/status`) still execute as text. So: **seed the palette from
  `SessionConversation.SlashCommands`, but don't treat it as the whole truth** — carry a curated built-in
  catalogue (below) and merge. Filter out entries containing `:` or matching the known skills list to
  keep skills out of the *built-in* palette section (show them in a separate "Skills" group if wanted).
- **Some commands mutate the session** (`/clear`, `/compact`, `/rename`) — Perch can't just render their
  output, it must react (reset the thread, drop a marker, update the title). These are the load-bearing
  ones; see the Tier-3 items.

Live-advertised built-ins in this build (skills stripped): `advisor, agents, auto-mode-setup, autocompact,
clear, color, compact, config, context, effort, fast, heapdump, init, mcp, import, model, reload-plugins,
reload-skills, rename, usage-credits, extra-usage, usage, insights, recap, goal, design-consent,
design-revoke, list-agents, team-onboarding` (plus internal `__remote-workflow`, `workflow-launch-exec`).

---

## Foundation (do these first — they unlock every command)

**Status: shipped as code (2026-09-04, branch `session-control-poc`) — builds green, 20 catalogue tests pass;
render/live dogfood still owed (a running dev instance held the Windows-head DLL, so no capture yet).**

- [x] **Curated built-in catalogue.** `src/Perch.Core/Data/Control/SlashCommandCatalog.cs` — a static table
      (`SlashCommandInfo`: name, arg-hint, one-liner, `SlashCommandTier`) + `Search` (fuzzy, via `FuzzyMatch`),
      `LooksLikeCommand`, `CommandName`, `IsBuiltIn`, `IsInternal`. Tests: `tests/SlashCommandCatalogTests.cs`.
      This is the palette's backbone since `init` under-reports; **skills are excluded by scope** (a Skills
      group sourced from `init.SlashCommands` minus built-ins/internal is the deferred follow-up).
- [x] **Composer `/`-detection.** `SessionWindow._composer.TextChanged` → `UpdatePaletteFromText`: a lone
      slash-command token (leading `/`, no space yet) opens the palette; a space or non-command text closes it.
- [x] **Command palette / autocomplete.** A `Popup` above the composer, driven entirely by composer text
      (focus stays on the composer; selection tracked and drawn manually). A bare `/` lists **every** command
      alphabetically in a scrollable popup (the selection scrolls into view); typing fuzzy-filters. Rows show
      `/name` + arg hint + description + a tier tag (`text`/`perch`/`session`/`terminal`). Keys: ↑↓ select
      (wraps), ↹ complete, ↵ run
      (no-arg commands send immediately; arg commands complete with a trailing space), Esc dismiss, click =
      run. **Native routing:** accepting `/model` or `/effort` opens the existing pill flyout instead of
      sending text (they're already control-request-driven). Other Native-tier entries (`/usage`, `/config`,
      `/resume`, `/help`, `/mcp`) still just insert text for now — their native surfaces are Tier-2/4 items.
- [x] **Command chip render.** `SessionThreadView.BuildUser` renders a slash-command user message as a compact
      mono chip (brand-tinted) instead of a prose bubble. Assistant command *output* already renders through
      `MarkdownView` (same `text`-block shape) — **verify visually** once a capture is possible.
- [ ] **Spike harness.** A tiny xUnit/manual harness that sends `/{cmd}` over the controller and asserts
      it comes back as `text` (or the expected `system/status`), so each checkbox below is *verified*, not
      assumed. Capture one fixture line per command into `StreamJsonParserTests` where the shape is new.
      *(Not started — this is the per-command verification tool for Tiers 1–4.)*
- [ ] **Render/dogfood the foundation.** `perch … -- render <dir>` capture of the palette open + a command
      chip (needs the Windows head, so stop any running dev instance first), then live: type `/`, arrow, Tab,
      Enter. *(Owed.)*

---

## Tier 1 — Works as plain text today; interop = palette entry + render

Each: **[ ] add to catalogue → [ ] spike-confirm text routing → [ ] verify markdown renders.**
(✅ = routing already confirmed live this session.)

- [ ] `/context` ✅ — context-window breakdown table. (Perch also has its own context indicator; native
      upgrade tracked in Tier 4.)
- [ ] `/cost` ✅ — session cost/token/duration readout. (Perch shows a per-turn cost line; this is the
      full breakdown.)
- [ ] `/status` ✅ — account/model/apiKey/session status dump.
- [ ] `/recap` — summarise the session so far.
- [ ] `/insights` — session insights readout.
- [ ] `/init` — generate/refresh `CLAUDE.md`; renders as text (it also writes the file → a normal Write
      tool turn may appear; confirm).
- [ ] `/import <path>` — import context; confirm arg passing.
- [ ] `/export` — export the conversation; confirm what it returns headlessly (may be a file path).
- [ ] `/review` — code review of the working tree; renders as text.
- [ ] `/pr-comments` — fetch PR comments; renders as text.
- [ ] `/release-notes` — show release notes.
- [ ] `/memory` — view/edit memory; confirm headless behaviour (may be TUI-editor-only → Tier 4).
- [ ] `/usage-credits`, `/extra-usage` — credit/extra-usage readouts (Perch already parses the usage
      endpoint — see the `usage-endpoint-spend` memory; native upgrade in Tier 4).
- [ ] `/goal` — set/show the session goal.
- [ ] `/advisor` — advisor output.
- [ ] `/list-agents` — list available agents (renders as text; `/agents` is the manager → Tier 3/4).

## Tier 2 — Already native in Perch (keep the pill; text command is a fallback)

- [ ] `/model [name]` — **done** via the model pill + `set_model` control request. Add a palette alias
      that routes to the pill flyout (don't double-implement). Confirm `/model` as text still works as a
      fallback and doesn't fight the pill's echo-guard.
- [ ] `/effort <level>` — **done** via the effort pill (`/effort` as a mid-session message, per
      `session-ui-plan.md`). Palette alias → pill.
- [ ] permission mode (the CLI's `/permissions`-style modes) — **done** via the mode pills +
      `set_permission_mode`. Palette alias → mode flyout.

## Tier 3 — Mutates the session; Perch must *react*, not just render (highest value)

- [ ] `/clear` — starts a fresh conversation. **Perch must reset the thread** (clear `Items`) and re-point
      to the new/continuing transcript. Verify whether the session id changes or the transcript resets,
      and that `SessionConversation` handles a mid-session reset (there is already a `Reset` event path
      for resume-history — reuse it). This is the most important one to get right.
- [ ] `/compact` ✅ — compacts context. Emits `system`/`subtype:"status"` records while working. **Drop a
      compaction marker** in the thread (a `NoteItem`) and confirm the conversation continues cleanly
      after (usage/context numbers change). Confirm the status records don't break the parser.
- [ ] `/rename [title]` — retitles the session. **Perch must update the session title** (bar + overlay
      row). Confirm where the new title lands (transcript title record vs control response) and wire it to
      the existing `TitleChanged` path.
- [ ] `/autocompact` — toggles auto-compaction. Reflect the setting; likely renders as text.

## Tier 4 — Native-only / TUI-only / defer (no clean text interop, or a native surface is the point)

Decide per item: **build a native Perch surface**, **map to an existing Perch feature**, or **omit**.

- [ ] `/usage` — **native.** Perch already surfaces usage-endpoint spend/limits; open that panel instead
      of dumping markdown. (Text fallback works today.)
- [ ] `/config` — **map to Perch Settings** (the registry-driven Settings window). Palette entry opens it.
- [ ] `/context` (upgrade) — optional native context panel reusing Perch's context indicator; text
      version already covers it.
- [ ] `/agents` — agent **manager** (interactive in the TUI). Decide: native manager vs `/list-agents`
      (text) for now. Likely defer the manager.
- [ ] `/mcp` — MCP server status/auth. `init` already carries `mcp_servers` (status incl. `needs-auth`);
      render a small native MCP panel from that; auth flows are TUI/browser → defer.
- [ ] `/resume` — **map to Perch's own launcher/picker** (`SessionHistory.ListAll`), not the TUI picker.
      Already how Perch starts/resumes sessions; palette entry opens the launcher.
- [ ] `/help` — **native.** Render the palette itself / a help card, not the CLI's TUI help.
- [ ] `/login`, `/logout` — auth; almost certainly not driveable over stream-json. Defer; if needed, hand
      off to a terminal. Confirm behaviour, then omit from the palette or grey out.
- [ ] `/vim` — editor mode; **N/A** for the Perch composer. Omit.
- [ ] `/terminal-setup` — **N/A** headless. Omit.
- [ ] `/statusline` — TUI status-line config; map to Perch settings or omit.
- [ ] `/hooks` — hook config; Perch manages its own hook (`HookInstaller`). Map to settings or render.
- [ ] `/add-dir <path>` — add a working directory. Check if it works as text or needs a control request;
      a native folder picker is the nicer surface. Spike then decide.
- [ ] `/bug` — feedback/bug report; decide route (native form vs omit).
- [ ] `/doctor` — environment diagnostics; renders as text but may be TUI-interactive — spike.
- [ ] `/color`, `/fast` — TUI display prefs; **N/A** (Perch owns its own theme/rendering). Omit.
- [ ] `/heapdump`, `/reload-plugins`, `/reload-skills` — maintenance; render-as-text if they work, else
      omit from the user-facing palette (keep as power-user entries).
- [ ] `/auto-mode-setup`, `/team-onboarding`, `/design-consent`, `/design-revoke`, `/skill-doctor` —
      newer/feature-specific; spike each, then render-as-text or omit.
- [ ] Internal (`__remote-workflow`, `workflow-launch-exec`) — **never surface.** Filter out.

---

## Definition of done (per command)

A command is "done" when: it's in the catalogue with a description + arg hint; the palette offers it;
sending it is verified (a spike fixture or a live capture); its output renders correctly (markdown, native
panel, or session-mutation reaction as appropriate); and — for Tier 3 — the session state (thread/title/
context) stays correct afterwards. Skills stay out of the built-in palette group.

## Verification

- `dotnet build perch.slnx` (both heads) + `dotnet test tests/Perch.Tests/…` green; add
  `StreamJsonParser`/`SessionConversation` fixtures for any new record shape a command introduces
  (`/compact` status records especially).
- Live dogfood in `SessionWindow`: open the palette, run one command from each tier, and specifically
  `/clear` → thread resets, `/compact` → marker + continues, `/rename` → title updates.
- Screenshots → `./captures/` (Windows: `-f net10.0-windows10.0.19041.0`).

## Notes for the next session

- Reuse the existing `Reset`/`LoadHistory` path in `SessionConversation` for `/clear`.
- The model/mode/effort pills already own their control requests — **don't** re-route those through text;
  make the palette entries aliases to the pills.
- The advertised list under-reports and includes skills — the curated catalogue is the source of truth for
  the *built-in* palette; `init.SlashCommands` is a merge input, not the whole list.
