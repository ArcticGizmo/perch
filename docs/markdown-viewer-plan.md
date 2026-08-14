# Markdown viewer/editor plan

> **Status: shipped (2026-08-14, branch `md`).** All five phases landed as code on `md`. Detection
> (`MarkdownFilesReader`), the produced-only glyph, the "Markdown files…" menu item, `MarkdownWindow`
> (session-file groups + `.gitignore`-aware project tree via `MarkdownProjectScan`), and the split
> source/live-preview editor with save (mtime-conflict guard, watcher reload, dirty-close prompt) are all
> in. Verified by `dotnet test` (630 pass, incl. `MarkdownFilesReaderTests` + `MarkdownProjectScanTests`),
> the overlay `render` capture (rose M↓ glyph), and a `HeadlessRenderer` capture of the populated window
> (`markdown_window_1x.png`). The section below is the original plan, kept for context.


A per-session Markdown experience: a **glyph** on a session row that lights up when the session
*produced* a `.md` file, a **right-click menu item** to open a **viewer/editor window**, and a
`.gitignore`-aware **project `.md` browser** inside that window. Renders and edits Markdown with a live
split preview.

Decisions locked (2026-08-14):

- **Glyph = produced-only.** Lights up when the session wrote/edited a `.md`
  (`Write`/`Edit`/`MultiEdit`/`NotebookEdit`). Merely *reading* a `.md` (CLAUDE.md, README) does **not**
  trigger it — that would be on for almost every session. Referenced `.md` files still surface *inside*
  the window; they just don't drive the glyph.
- **File pane = session files + project tree.** Two groups: the `.md` this session produced/referenced,
  plus a browsable tree of all project `.md` (`.gitignore`-respecting, bounded depth).
- **Editor = split source + live preview.** Left: editable source `TextBox`. Right: live-rendered
  `MarkdownRender` preview. No WYSIWYG.

## What already exists (reuse, don't rebuild)

- **Markdig** is already a `Perch.App` PackageReference; **`Rendering/MarkdownRender.cs`** walks the AST into
  Avalonia inlines (`Append(InlineCollection, md, brushes…)`). `ChangelogWindow`/`ChangelogMarkdown` are a
  working read-only precedent. (Limitations: links coloured but not clickable, no images, code is mono text
  with no background box — acceptable for a preview pane; note it in the UI, don't fight it.)
- **Transcript scaffolding**: `TranscriptScan` (shared `FileShare.ReadWrite` IO, tail reads),
  `TranscriptJson` (`ContentArray`/`BlockType`, defensive coercion), `MtimeCache<T>` (per-file memoise by
  length+mtime), `TranscriptLocator.Resolve(sessionId, cwd)` (path resolution + all-projects fallback).
- **Tool taxonomy** already encoded: `ToolSummary.Describe` and `TranscriptReader.Fingerprint` already read
  `file_path`/`notebook_path` for `Read`/`Write`/`Edit`/`MultiEdit`/`NotebookEdit` and `pattern` for
  `Grep`/`Glob`. `ToolSummary.FileLabel` splits on `/` and `\` (transcripts carry either OS's paths). No
  service *aggregates* these into per-session file sets yet — that's the one new bit.
- **Git shelling recipe**: `GitStatsService.RunGitDiff` / `GitRepoService.RunGit` — `ProcessStartInfo` with
  UTF-8 pipe encodings, async pipe reads (no deadlock), `WaitForExit(timeout)` + `Kill(entireProcessTree)`,
  `--no-optional-locks`, best-effort null on any failure. The project-tree walk clones this.
- **Glyph pattern** (Artifacts is the closest analogue) and **menu pattern** (History) — see below.
- **Window conventions**: `internal sealed class : Window`, code-built `DockPanel` toolbar over body,
  `Palette` theming, `WindowStartupLocation.CenterScreen`, Escape-to-close, `WindowHost.ShowOrFocus` reuse,
  `Task.Run(...).ContinueWith(... Dispatcher.UIThread.Post ...)` with `if (!IsVisible) return;` guards.
  Multi-line `TextBox` recipe already in `GitTreeWindow` (`AcceptsReturn`, `TextWrapping`, mono font,
  attached `ScrollViewer.VerticalScrollBarVisibilityProperty`). Avalonia 12.0.5; `Markdig` 1.3.2.

## Architecture

Two data paths, deliberately split by cost:

1. **Cheap per-scan boolean** for the glyph: `ClaudeSession.HasProducedMarkdown`, computed in the
   monitor's existing tail-read + `MtimeCache` loop with a substring pre-filter (`".md"` + a write-tool
   name) before any JSON parse. This is all the hot scan pays.
2. **Full lists computed lazily on window open** (off the UI thread): produced `.md`, referenced `.md`, and
   the project `.md` tree. Never runs during the scan loop, so the overlay stays cheap.

### Phase 0 — Detection (Core)

- **`MarkdownFilesReader`** (new, `src/Perch.Core/Data/`), alongside `TranscriptReader`. Two entry points:
  - `bool ProducedAnyMarkdown(string sessionId, string cwd)` — tail-read + `MtimeCache<bool>`, substring
    pre-filter, then parse `tool_use` blocks for write tools with a `.md` `file_path`/`notebook_path`.
    Confirm the paired `tool_result` isn't an error (id-pairing as in `FlightPathService`/`SubAgentReader`)
    so a *failed* write doesn't light the glyph.
  - `MarkdownFileSets Full(string sessionId, string cwd)` — whole-transcript scan (`TranscriptScan.ReadLines`)
    returning `{ IReadOnlyList<string> Produced, IReadOnlyList<string> Referenced }`, de-duped, absolute
    paths where derivable (transcripts carry absolute `file_path`s). Called only from the window opener.
- Reuse the read/write split: produced = `Write`/`Edit`/`MultiEdit`/`NotebookEdit`; referenced =
  `Read`/`Grep`/`Glob` (+ produced, since editing implies reference). Match `.md`/`.markdown`
  case-insensitively.
- **Tests**: fixture transcript with a `.md` Write, a `.md` Read, a non-`.md` Write, and a failed `.md`
  Write; assert `Produced`/`Referenced`/`HasProducedMarkdown`. Add under `tests/Perch.Tests` next to the
  existing `*Tests.cs`, using the `CLAUDE_CONFIG_DIR` fixture tree.

### Phase 1 — Glyph (produced-only)

Six-spot glyph pattern, modelled on Artifacts:

1. `ClaudeSession.cs` — add `HasProducedMarkdown` (bool init prop / derived flag).
2. `SessionMonitor.cs` — populate it via `MarkdownFilesReader.ProducedAnyMarkdown`. **Display-only gate**
   (like Artifacts, not git-stats): always cheap enough to compute; gate only the glyph. (Revisit only if
   profiling says otherwise.)
3. `OverlayCanvas.cs` — `_showMarkdown` field + `SetShowMarkdown` gate; `MdIconWidth` const; a
   `DrawMdIcon` (document-with-fold glyph); wire width/placement into the left glyph cluster (~L2237–2304)
   so the name budget accounts for it. Non-interactive (no hit-rect/hover needed).
4. `Services/OverlaySettingsGates.cs` — `c.SetShowMarkdown(s.ShowMarkdown);`.
5. `AppSettings.cs` — `public bool ShowMarkdown { get; set; } = true;`.
6. `SettingDescriptor.cs` — add `PreviewTarget.Markdown`; `SettingsRegistry.cs` — a `Toggle(...)` under
   `SettingSurface.SessionRow`; `SettingsCatalogView.cs` `CardPreview` — a chip case. (`SettingsRegistryTests`
   fails the build if the `AppSettings` prop lacks a descriptor.)

Colour: the glyph carries semantic meaning ("this session made docs"), so prefer a `FixedColors` hue routed
through `Palette` over a hard-coded literal — keeps it theme/CVD-correct. Pick a hue distinct from
running-green/error-red/warn-amber/Jira-blue (e.g. a teal/doc tone; add to `FixedColors` + `Simulate` +
`Palette` accessor if none fits).

### Phase 2 — Menu item + window shell

- `OverlayCanvas.cs` (event region ~L1189): `internal event Action<ClaudeSession>? MarkdownRequested;`
  (internal, matching `NoteEditRequested`, because the payload is the Core session and we need `Cwd`).
- `ShowContextMenuAt` (~L3401): add `actions.Add(MenuItem("Markdown files…", () => MarkdownRequested?.Invoke(s)));`
  gated `!subRow`. (Optionally only when `s.HasProducedMarkdown` — but keeping it always-available lets the
  window act as the project `.md` browser regardless.)
- `App.axaml.cs`: `private MarkdownWindow? _markdownWindow;`, subscribe near L232, `OpenMarkdown(session)`
  via `WindowHost.ShowOrFocus`, and a `_markdownWindow?.Close();` line in `CloseAuxWindows` (~L411).
- **`Windows/MarkdownWindow.cs`** — `internal sealed class MarkdownWindow : Window`, namespace
  `Perch.Avalonia.Windows`. Ctor: `DockPanel` toolbar (`Palette.FormBgBrush`) over a body split
  left-file-pane / right-editor; `Palette` theming; ~1000×700; center screen; Escape closes (guard dirty).
  `Retarget(session)` refresh method for `ShowOrFocus` reuse.

### Phase 3 — File pane (session lists + project tree)

- **`MarkdownProjectScan`** (new, Core): `git -C <cwd> --no-optional-locks ls-files --cached --others
  --exclude-standard -- "*.md" "*.markdown"` via the `GitStatsService` process recipe → paths honouring
  `.gitignore` for free, bounded by the repo. **Fallback** (non-git `cwd`): bounded-depth (e.g. ≤6) walk
  skipping `node_modules`/`bin`/`obj`/`.git`/`.venv`, hard cap on file count. Best-effort, timeout, empty
  on failure. Off the UI thread.
- File pane UI: a `TreeView` (or grouped list) with two roots — **This session** (Produced group +
  Referenced group, from `MarkdownFileSets`) and **Project** (the scanned tree, foldered). Selecting a node
  loads that file into the editor (off-thread read, `IsVisible` + selection-token guard).

### Phase 4 — Preview, editing, save

- Right pane split: source `TextBox` (`GitTreeWindow` recipe — `AcceptsReturn`, `TextWrapping.Wrap`, mono
  font, attached vertical scrollbar) + a `ScrollViewer`>`SelectableTextBlock` preview fed by
  `MarkdownRender.Append(...)` with `Palette` brushes. `TextChanged` → debounced re-render (DispatcherTimer,
  ~150ms) so typing stays smooth.
- **Save**: `Ctrl+S` / toolbar button. Write off-thread; **mtime-conflict check** — if the file changed on
  disk since load, warn before overwrite (`ConfirmDialog`). Track a **dirty** flag; on close/switch prompt
  if dirty (StickyNote's `CloseWithoutPrompt` pattern). Save is the user's own repo file — no external
  publish, no PII surface.
- Reload safety: file may be edited by a live Claude session; a `FileSystemWatcher` on the open file
  (debounced, marshalled via `Dispatcher.UIThread.Post` like `HistoryWindow`) offers a non-destructive
  "file changed on disk — reload?" nudge rather than clobbering.

## Risks / call-outs

- **`MarkdownRender` is display-only** (no clickable links/images). Fine for preview; don't invest in
  WYSIWYG. If clickable links are wanted later, that's a `MarkdownRender` enhancement, out of scope here.
- **Glyph noise**: produced-only keeps it rare and meaningful — the whole reason for the decision.
- **Large repos**: the `ls-files` scan is bounded by git; the fallback walk needs the depth+count caps or a
  monorepo could stall. Log (not silently truncate) if the cap is hit.
- **Absolute vs relative paths**: transcripts carry absolute `file_path`s from whatever host wrote them
  (possibly a different OS in shared histories) — resolve/normalise defensively; a path that doesn't exist
  locally still lists (greyed), it just doesn't open.
- **`ClaudeSession` is Core-internal-ish**: menu event carrying it must be `internal` (see `NoteEditRequested`).

## Build order

Phase 0 (+ tests) → Phase 1 (glyph, independently shippable) → Phase 2 (menu + empty window shell) →
Phase 3 (file pane) → Phase 4 (edit/save). Each phase builds and is demoable; the glyph alone is a
complete increment. Verify owner-drawn glyph via `dotnet run … -- render <dir>` per CLAUDE.md.
