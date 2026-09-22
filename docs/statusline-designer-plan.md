# Statusline designer

A Perch feature for designing and switching Claude Code status lines: a **mustache-style template**
language (with conditionals and formatters) rendered live from the JSON Claude Code hands its statusLine
command, plus a **profile library** you can back up, import into, and toggle between — so carrying over a
setup from ccstatusline or a hand-rolled bash script is one command, and never loses your old one.

Branch: `statusline-designer`. Interactive mockup: `docs/statusline-designer-mockup.html`.

## How Claude Code's status line works (the ground truth)

`~/.claude/settings.json` → `statusLine` = `{ type: "command", command, padding }`. On each refresh
(session start/resume, new assistant message, `/compact`, permission/vim change, or a `refreshInterval`
timer; 300ms debounce) Claude Code runs the command, pipes it a JSON payload on **stdin**, and renders the
**stdout** (ANSI colours + OSC 8 links supported, multi-line supported, sized to the `COLUMNS`/`LINES` env
vars — never `tput`). Runs locally, costs no tokens. Claude Code also ships a `/statusline` NL setup
command — Perch's edge is *repeatable, visual editing + a data explorer + import/toggle*, not first-time
generation.

Payload highlights (all real, current fields): `model.{id,display_name}`, `workspace.{current_dir,repo.*}`,
`cost.{total_cost_usd,total_lines_*}`, `context_window.{used_percentage,…}` (null early in a session),
`prompt_cache.{warm,hit_ratio,ttl}` (v2.1.251+), `rate_limits.{five_hour,seven_day}.used_percentage`,
`pr.{number,review_state,url}`, `effort.level`, `vim.mode`, `version`. Perch injects `git.branch`.

## Template language

- `{{path.to.value}}` — dotted path into the payload (faithful, **no aliases**).
- `{{value | filter | filter:arg}}` — `money`, `round`, `pct`, `k`, `upper`/`lower`, `bar:N`
  (N-cell block bar from a 0–100 %), `trunc:N`, `default:X`, `color:NAME`.
- `{{#if EXPR}}…{{/if}}` / `{{#unless EXPR}}…{{/unless}}` — EXPR is a truthy path or a comparison
  (`context_window.used_percentage > 80`, `pr.review_state == 'approved'`).
- `{{#path}}…{{/path}}` truthy section / `{{^path}}…{{/path}}` inverted / `{{! comment }}`.
- Colour roles: teal, amber, green, red, yellow, blue, violet, muted → ANSI truecolor in the terminal,
  Palette brushes in the (future) designer. Absent/`null` renders empty and reads as falsy.

## Status

### M0 — Core engine + config *(done)*
`src/Perch.Core/Statusline/`: `StatuslineTemplate` (tokenise/parse/render → coloured `StatuslineSegment`s),
`TemplateData` (dotted-path JSON accessor, absent-vs-null aware), `StatuslineRenderer` (segments → ANSI),
`StatuslineProfile`/`StatuslineConfig` + `StatuslineStore` (`statusline.json` in Perch's app-data),
`StatuslineDefaults` (Perch Default / Minimal / Two-line Pro), `StatuslineSample` + `StatuslineTokens`
(the data-explorer catalogue), `GitHead` (branch from `.git/HEAD`, no subprocess). Fully unit-tested
(`StatuslineTemplateTests`, `StatuslineConfigTests`, `StatuslineSettingsTests`).

### M1 — CLI + standalone script *(done)*
`perch statusline` (`src/Perch.App/Services/StatuslineCli.cs`, dispatched at the top of `Program.Main`
before Avalonia). `ClaudeUserSettings.{Read,Set,Clear}StatusLine` read/write the settings.json block,
preserving every other key.

**Perch is not in the refresh loop.** Applying a Perch profile *compiles* it to a **standalone Node
script** (`StatuslineScript`, `~/.claude/perch-statusline.mjs`) with the engine + template + git-branch
reader baked in; `settings.json → statusLine.command` is `node "…"`, which runs directly. Node is chosen
because Claude Code already requires it, so the script has a guaranteed cross-platform runtime and no
extra dependency (no jq/python/pwsh). The status line keeps working even if Perch is uninstalled. The
embedded engine is a faithful JS port of `StatuslineTemplate`; a **parity test** runs the generated
script under `node` and diffs its stdout against the C# engine byte-for-byte, so the two can't drift.
`perch statusline render` remains only as a local preview aid — nothing in `settings.json` calls it.

### M2 — Designer window *(done)*
`StatuslineDesignerWindow` (opened from the tray, "Statusline designer…"; single reused instance via
`WindowHost`, torn down in `CloseAuxWindows`). Template editor with insert chips; a live ANSI-coloured
preview rendered through the C# `StatuslineTemplate` engine against `StatuslineSample` (colours straight
from `StatusColors`, so preview == terminal); data explorer from `StatuslineTokens` (sample values +
caveat badges, click-to-insert at the caret); profile rail toggling Perch ⇄ imported with New / Back up
current / Import. "Set active" runs the shared `StatuslineInstaller` (also used by the CLI). Profile
library saved on close. Render-verified via `HeadlessRenderer` (`statusline_designer_1x.png`);
`StatuslineStore.DisablePersistence` keeps `render` from overwriting a real `statusline.json`.

### Done since
- **Pace-coloured rate windows** + a `Rate-aware verbose` built-in that ports a classic bash statusline
  (`human`/`dur`/`until`/`pace` filters; `pace:<resetsPath>:<windowSeconds>` uses `Palette.PaceColor`).
- **Built-ins are code, not the file** — the store merges `StatuslineDefaults` with the saved deltas, so a
  new example appears with no migration; only user edits/imports/active persist.
- **git counts** — the generated Node script injects `git.staged/unstaged/changes/dirty` (gated on use).
- **Editor**: wrap-aware line-number gutter + inline autocomplete (token list with sample values).

### Later
A `SettingsRegistry` entry so the designer is discoverable/searchable from Settings; a `COLUMNS`-width
preview control + horizontal-scroll for long lines (the preview still clips a long single line);
delete-profile; per-segment Powerline styling; additional target languages besides the default Node
(Bash+jq, PowerShell) as a per-profile choice; OSC 8 link support. **Not yet:** live dogfood (nothing
`use`d against a real `settings.json` yet; the git-count subprocess is unrun) and merge to `main`.

## Try it (experiment now, before M2)

Build: `dotnet build src/Perch.App/Perch.App.csproj`. The exe is
`src/Perch.App/bin/Debug/net10.0-windows10.0.19041.0/perch.exe`.

```
# render a template against a sample payload (no config change)
echo '{"model":{"display_name":"Opus"},"cwd":"C:/path/to/repo","cost":{"total_cost_usd":0.42},"context_window":{"used_percentage":34},"pr":{"number":30,"review_state":"pending"}}' | perch statusline render

perch statusline list                    # saved profiles + what's in settings.json now
perch statusline backup                  # stash your current status line as an imported profile (safe!)
perch statusline use "Two-line Pro"      # generate ~/.claude/perch-statusline.mjs + point settings.json at it
perch statusline import ccstatusline "npx -y ccstatusline@latest"
perch statusline install                 # seed defaults + apply the active one
```

`use`/`backup`/`import`/`install` write `~/.claude/settings.json` (and `statusline.json`); `render`/`list`
are read-only. Edit templates directly in `statusline.json` for now — the graphical editor is M2.
