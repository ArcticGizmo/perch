# Session window enhancements — plan

**Status (2026-09-04): all four SHIPPED AS CODE** (uncommitted, branch `session-control-poc`). Build green
(both heads), 1025 tests pass (7 new). Render-verified: `captures/20260904-session-enhancements/`
(`session_window_1x.png` shows the composer toolbar; `session_attachments_1x.png` shows the chips; light twin
too). **Owed:** live dogfood — the one real unknown is whether the CLI's stream-json input accepts a base64
`image` content block (Feature 4's send). Everything else (clickable links, middle-click new window, drag-drop
files, clipboard image paste, the toolbar) is interactively unverified but low-risk.

Four composer/thread enhancements to the rich `SessionWindow` (branch `session-control-poc`), requested
2026-09-04:

1. **Clickable hyperlinks** in the conversation — left-click opens in the default browser, **middle-click
   opens in a new browser window**.
2. **A quick-action icon toolbar above the composer** mirroring the glyphs the user has *enabled* in the
   floating overlay, for consistency — clickable, each doing what its overlay counterpart does.
3. **Drag files into the composer** — a dropped (non-image) file inserts its path into the input.
4. **Drag / paste images** — image files or clipboard images become attachment chips (click to open,
   hover to preview) and are sent to Claude as image content blocks.

All visual tokens come from `SessionPalette`; nothing hard-codes colour.

## Key facts established (from code)

- **Browser opener already exists:** `PlatformServices.UrlOpener` (`IUrlOpener`) — `Open(url)` (default
  browser, reuse window) and `OpenInNewWindow(url)`. Windows + Mac impls. Used already by History/Settings.
- **Link detection regex exists** but is `private` in `ComposerHighlighter.LinkRegex()` (`https?://\S+`,
  trailing-punctuation trim). Promote a small shared `UrlDetect` helper in `Perch.Core` and reuse it in both.
- **Markdown links are non-interactive today:** `MarkdownView.AppendInlines` renders links as coloured +
  underlined `Run`s inside a `SelectableTextBlock`; for a normal markdown link the URL is *discarded*.
- **Text hit-testing is proven here:** `MarkdownWindow.cs:1534` uses
  `tb.TextLayout.HitTestPoint(point - padding)` → `hit.TextPosition` (char index). Same API (Avalonia 12.0.5)
  makes links clickable without breaking wrapping or selection.
- **Send path is plain-text only** but the wire shape is already a content-block **array**:
  `{type:"user",message:{role:"user",content:[{type:"text",text}]}}` in
  `ClaudeSessionController.SendPrompt(string)`. Adding image blocks is a localised overload; `WriteLine`
  is untouched. `PerchSession.SendPrompt` + `UserMessageItem` forward/hold text only today.
- **No drag-drop anywhere** in the app yet (added fresh). `IImageClipboard` (write-only) + `PngEncoder`
  exist in Core; clipboard *read* uses Avalonia's `TopLevel.Clipboard` / drop `DataObject`.
- **Overlay "enabled glyphs":** authoritative map is `OverlaySettingsGates.Apply` → `AppSettings` booleans.
  Genuinely *actionable* ones suited to a composer toolbar: **Quick links** (`AppSettings.QuickLinks`,
  launch/focus via `QuickLinkLauncher`), **Todos** (`ShowTodos` → Todos window), **Media**
  (`ShowMediaController`), **Notes/scratch-pad** (`ShowNotes`). Most other overlay glyphs are per-session
  *status* indicators, not actions.

## Design

### Feature 1 — clickable links (thread only; composer stays edit-only)
- New `Perch.Core/Data/Control/UrlDetect.cs`: `Find(string) -> IReadOnlyList<(int Start,int Len,string Url)>`
  using the promoted regex + trailing-trim. `ComposerHighlighter` reuses it (no behaviour change; keep tests
  green).
- New `Perch.App/Views/LinkText.cs`: `Attach(SelectableTextBlock tb, IReadOnlyList<LinkSpan> spans)` — wires
  `PointerMoved` (hand cursor + hover preview hook), `PointerReleased` (left → `UrlOpener.Open`, middle →
  `UrlOpener.OpenInNewWindow`), distinguishing a click from a drag-select by pointer travel + empty selection.
  Hit-test via `tb.TextLayout.HitTestPoint`.
- `MarkdownView.AppendInlines`: thread a running char count so every link (LinkInline/AutolinkInline/image
  link) records a `LinkSpan(start,len,url)`; keep the URL for normal links. After a block's inlines are set,
  call `LinkText.Attach` when it has spans. Applies app-wide (also improves `MarkdownWindow`).
- Plain (non-markdown) `SelectableTextBlock`s that commonly hold URLs — tool-card output (`_outText`) and the
  user bubble — adopt the same via `UrlDetect.Find` + `LinkText.Attach`, so links are consistent everywhere.

### Feature 2 — composer quick-action toolbar (enabled overlay glyphs)
- A slim `WrapPanel` row inside the composer frame, above the text area. Built by a new
  `Perch.App/Views/ComposerToolbar.cs` from the current `AppSettings`.
- Surfaces the **enabled, actionable** overlay features as small icon buttons, reusing overlay art where a
  reusable control exists and the quick-link bitmaps otherwise:
  - each enabled `QuickLink` → launch/focus (`QuickLinkLauncher.LaunchOrFocus`), icon from `IconFile`;
  - `ShowTodos` → open Todos window; `ShowMediaController` → media; `ShowNotes` → scratch-pad note.
  - Plus two composer-native actions that belong here: **attach file** and **attach image** (feed Features
    3/4), so the row is useful even with all overlay toggles off.
- The App passes the live `AppSettings` + the action callbacks when it builds the window (new
  `SessionWindow.SetComposerActions(...)`), mirroring how it already pushes context-pressure/auto-compact
  config. Hidden entirely when nothing is enabled.

### Features 3 & 4 — file drop, image drop/paste
- **Core model:** `Perch.Core/Data/Control/MessageAttachment.cs` — `Kind (File|Image)`, `Path`, `MediaType?`.
  `UserMessageItem` gains `IReadOnlyList<MessageAttachment>? Attachments`; `AddUserPrompt(text, attachments)`.
- **Send path:** `ClaudeSessionController.SendPrompt(string text, IReadOnlyList<ImageContent>? images)` where
  `ImageContent(string MediaType, string Base64)` is a Core record; appends
  `{type:"image",source:{type:"base64",media_type,data}}` blocks to the content array (Anthropic Messages
  shape — the format Claude Code's stream-json input consumes; **verify live**). `PerchSession.SendPrompt`
  gains the same overload and records the attachments on the `UserMessageItem`.
- **Composer capture** (`SessionWindow`):
  - `DragDrop.SetAllowDrop(_composer, true)` + Drop handler: image files → image attachments; other files →
    insert quoted path at caret.
  - Paste: intercept Ctrl+V; if the clipboard holds a bitmap (`TopLevel.Clipboard` DIB/PNG), save it to a
    per-session temp dir (`%TEMP%/perch-attach/<session>/paste-<ts>.png` via `PngEncoder`) → image attachment;
    else let normal text paste proceed.
  - A pending-attachments tray renders above the text as removable chips with a thumbnail; on send they go to
    `SendPrompt(text, images)` and clear.
- **Thread display:** `SessionThreadView.BuildUser` renders attachment chips under the user bubble — image =
  thumbnail (click opens via `UrlOpener.Open(path)`, hover shows a larger preview popup); file = a mono path
  chip (click opens). A small reusable `AttachmentChip` control.

## Files
- New: `Perch.Core/Data/Control/UrlDetect.cs`, `MessageAttachment.cs`, `ImageContent` (in controller file);
  `Perch.App/Views/LinkText.cs`, `ComposerToolbar.cs`, `AttachmentChip.cs`.
- Edit: `ComposerHighlighter.cs`, `SessionConversation.cs` (UserMessageItem/AddUserPrompt), `PerchSession.cs`,
  `ClaudeSessionController.cs`, `MarkdownView.cs`, `SessionThreadView.cs`, `SessionWindow.cs`, `App.axaml.cs`
  (wire toolbar actions + settings).

## Verification
- `dotnet build perch.slnx` + `dotnet test tests/Perch.Tests/…` green; add `UrlDetectTests` and a
  `SessionConversation` attachment test; keep `ComposerHighlighterTests` green.
- Render mode (`-- render <dir>`) for the composer toolbar + attachment chips + a link-bearing turn.
- **Live dogfood owed:** image content-block acceptance by the CLI (the one protocol unknown), middle-click
  new-window, drag-drop from Explorer, clipboard image paste.
