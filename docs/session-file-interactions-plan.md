# Session UI — interactive file references + changed-files panel

Status: **shipped as code** (branch `session-control-poc`, uncommitted). Interactive parts are
runtime-unverified beyond the headless render; the git plumbing has unit coverage.

## What this adds

The rich Perch session UI (`SessionWindow` + `SessionThreadView`) displayed file references but they were
inert. Two capabilities were added:

1. **Interactive file references in tool cards.** A file tool (Read/Edit/MultiEdit/Write/NotebookEdit)
   renders its filename as its own token. **Left-click a Markdown file** → opens it in the existing
   `MarkdownWindow` viewer. **Right-click any file** → a context menu:
   - **View** (Markdown files only) — open in the viewer
   - **View diff** — open the `GitTreeWindow` git viewer landed on that file
   - **Reveal in Explorer** (Finder on macOS)
   - **Open in VS Code**

   Non-Markdown files have no left-click action, so a left-click still falls through to the tool card's
   expand toggle.

1b. **Interactive file references in Claude's prose** (`FileRef.AttachInline` + `MarkdownView`). Inline-code
   spans in rendered Markdown (e.g. `` `docs/joke.md` ``) become clickable **only when they resolve to a real
   file on disk** (relative to the session cwd, or absolute) — a cheap `.`/`/`/`\` pre-filter then
   `File.Exists`, so arbitrary code (`SessionStart`, `foo()`) stays inert and false positives are near-zero.
   **Right-click** a resolved span → the same menu; **Ctrl+left-click** a Markdown span → view (mirroring the
   existing URL-link `Ctrl+click` convention, so plain text stays selectable). Threaded through
   `MarkdownView.Build(md, style, FileRefContext)`; other `MarkdownView` callers pass none and are unchanged.

   **Extended (bare paths + lines):**
   - **Bare paths.** Paths written without backticks (`src/a.cs`, `README.md`) are found by `FilePathDetect`.
     It is a pure shape filter that skips URLs, `scheme:` words and version numbers, with at most 32 candidates
     per block.
   - **Line refs.** `a.cs:42`, `a.cs:42:7` and `a.cs#L42` (including a link's `#L42` fragment) resolve through
     `FileRefResolver.ResolveRef`. It splits the line off before the CP9 safety check, so a suffix can't make an
     unsafe path probeable.
   - **Opening.** Linked ranges are **underlined**. Hovering one shows the path, and a hand cursor appears while
     Ctrl is held. **Ctrl+click opens any file**: a Markdown file goes to the viewer, anything else to VS Code at
     the line.
   - **Safety.** Opening never shell-executes. `OpenInEditor` falls back only to the view-only default handler, or
     else to a reveal. The menu gains **Copy path**.
   - **Hardening.** The resolver now also refuses NTFS alternate-stream paths (`a.md:payload`).

2. **Changed-files side panel + toggle.** A `◨` toggle at the **top-right of the composer** (right end of the
   quick-action glyph row, shown once a session attaches) shows/hides a right-docked panel listing the **repo's working-tree
   changes** (`git status`) with **git diff line markers** (`+added` / `−removed`). Each row is the same
   `FileRef` affordance: left-click opens the diff, right-click gives the full menu. (Per the design decision,
   the panel is *repo-scoped* — every uncommitted change — not limited to files this session touched.)

## Pieces

| Area | File | What |
| --- | --- | --- |
| Platform seam | `Perch.Core/Platform/IFileRevealer.cs` | `RevealInFileManager` + `OpenInEditor` |
| | `Perch.Platform.Windows/FileRevealer.cs` | `explorer /select`, `code -g` |
| | `Perch.Platform.Mac/FileRevealer.cs` | `open -R`, `code` |
| | `Perch.App/PlatformServices.cs` | `PlatformServices.FileRevealer` |
| Git line markers | `Perch.Core/Data/GitRepoModels.cs` | `GitChangeStat` record |
| | `Perch.Core/Data/GitRepoService.cs` | `GetChangeStats(cwd)` + `ParseNumstat` (unit-tested) |
| Interaction helper | `Perch.App/Views/FileRef.cs` | `Attach` (whole control) + `AttachInline` (prose char-ranges); mirrors `LinkText` |
| Prose refs | `Perch.App/Rendering/MarkdownView.cs` | collects inline-code spans, `ResolveFile` (existence-gated), `FileRefContext` |
| Tool-card refs | `Perch.App/Views/SessionThreadView.cs` | `ToolCard` renders filename as a `FileRef`; `Prose` passes a `FileRefContext`; `OpenFileRequested`/`ViewDiffRequested` events + `Cwd` |
| Panel + toggle | `Perch.App/Windows/SessionWindow.Changes.cs` | `ChangedFilesPanel` + toggle + debounced refresh |
| | `Perch.App/Windows/SessionWindow.cs` | events, composer inset, DockPanel column, Attach/Detach hooks |
| Window entry points | `Perch.App/Windows/MarkdownWindow.cs` | `OpenPath(absolutePath)` |
| | `Perch.App/Windows/GitTreeWindow.cs` | `Retarget(..., focusPath)` seeds the existing reselection |
| App wiring | `Perch.App/App.axaml.cs` | `OpenSessionFileInViewer` / `ViewSessionFileDiff` (reuse the single viewer/tree windows) |

## How the data flows

- The panel loads `GitRepoService.GetChangeStats(cwd)` **off the UI thread** (generation-token guarded), which
  reads `git status --porcelain=v2` for the file set + kinds and `git diff --numstat HEAD` for tracked
  line counts; an untracked file's additions come from `GetUntrackedDiff` (whole file as added). It refreshes
  when toggled open, when a session attaches, and — debounced 900 ms — on every `Conversation.Changed` (a
  tool result landing usually means a file was written).
- The viewer/tree are **app-owned single instances** (`WindowHost.ShowOrFocus`). The view raises
  `OpenFileInViewerRequested` / `ViewFileDiffRequested`; the app retargets and opens. `MarkdownWindow.OpenPath`
  loads an arbitrary absolute path (its private `TryOpenPath`); `GitTreeWindow.Retarget(focusPath)` converts
  the absolute path to git's repo-relative form and seeds `_pendingFilePath` so the tip node lands on it.

## Known limitations / follow-ups

- Panel is repo-scoped (all working-tree changes), not session-scoped. A change **committed mid-session** no
  longer appears (it's no longer in `git diff HEAD`).
- Renames show `R` with no `+/-` (numstat's `{old => new}` key doesn't match the status path — deliberately
  simple).
- Panel open/closed state is **per-window ephemeral** (default closed), not persisted.
- Prose file refs are existence-gated, so a file mentioned in prose that doesn't exist yet won't link. The hover
  tip/cursor only clears what it set itself, so it never clobbers a URL link armed on the same block. The hand
  cursor follows Ctrl on the next pointer move (no key hook, unlike `LinkText`).
- Only the settled part of a streaming reply links; the still-forming tail is rendered without file refs.
- "Open in VS Code" is best-effort: if the `code` CLI isn't on PATH it falls back to the OS default handler.
- The panel isn't in the headless render harness (it's hidden by default) — eyeball it by running the tray
  app and toggling `◨`.
