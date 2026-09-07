# Session UI — composer & thread enhancements (round 2)

**Status (2026-09-07): all five SHIPPED AS CODE** (uncommitted, branch `session-control-poc`). Build green
on both heads (0 warnings), 1049 tests pass (+4 new). Render-verified: `captures/20260907-composer-enhancements/`
(the ↑ "jump to last prompt" button shows in `session_window_1x.png`; copy buttons are hover-only so don't
appear in a static capture). **Live dogfood owed** — see the bottom of this doc.

**Dogfood round 1 (2026-09-07) → follow-up changes:**
- Images: the `[Image #N]` token is now **kept inline** (context) and made interactive — `Views/ImageRefText.cs`
  makes each token hover-preview + click-open, paired to the k-th image attachment (`SessionConversation` no
  longer strips it; the chip stays too).
- Clicking any image (chip or inline token) opens an **in-app viewer** (`Windows/ImageViewerWindow.cs`) with
  **Reveal in Explorer / Open with… / Open** buttons — not the OS default handler. Added `IFileRevealer.OpenWith`
  (Windows `OpenAs_RunDLL`; Mac best-effort `open`).
- Preview res: the hover preview now decodes at **native resolution** lazily on first hover (`AttachmentChip`
  `TryLoadFull`) instead of upscaling the 240px thumbnail — that was the "weird compression".
- File mentions: rows are now **VS Code-style** — filename in full on the left, dimmed directory on the right
  truncated with a **leading ellipsis** (`TextTrimming.PrefixCharacterEllipsis`), so a long path keeps the name.
- Composer selection bug ("everything turns purple"): the coloured highlight layer now sits **on top** of the
  transparent-text box, so the box's selection rectangle paints *behind* the glyphs; selection uses a soft
  brand wash. (`textArea` z-order + `SelectionBrush`/`SelectionForegroundBrush`.)

**Dogfood round 2 (2026-09-07) → inline-image polish (`ImageRefText` + `ImageViewerWindow`):**
- The `[Image #N]` token is now **tinted violet + semibold** (the UI's "special" hue) so it reads as more than
  text (`Tint` rebuilds the block's runs as `Inlines`).
- The hover preview is **delayed ~750ms** (a `DispatcherTimer`) instead of appearing instantly, and the preview
  popup is **hit-test-transparent** — together this stops the preview sitting under the cursor and swallowing a
  click, so a plain (non-drag) left-click on the token reliably opens the viewer.
- The in-app viewer keeps the image **in frame**: `Stretch=Uniform` + `StretchDirection=DownOnly` (scales a
  large image down to fit, leaves a small one native) instead of a scroll-viewer that let it overflow.

**Branch:** `session-control-poc`. Five improvements to the rich `SessionWindow`, requested 2026-09-07.
All visual tokens come from `SessionPalette`; nothing hard-codes colour. Each is independently shippable.

1. **File mentions (`@path`)** — typing `@` in the composer opens a fuzzy file-suggestion popup over the
   project's files; accepting inserts the repo-relative path.
2. **Input history** — ↑/↓ in an empty/at-start composer recalls this session's previous prompts (shell-style),
   with the in-progress draft restored at the end.
3. **Per-message copy** — a hover copy affordance on each user bubble and assistant message copies its text.
4. **Scroll buttons** — floating "jump to bottom" and "jump to last prompt" buttons at the thread's
   bottom-right, shown only when they'd do something.
5. **Inline images on resume** — a resumed user message that contained a pasted image renders the image as a
   thumbnail chip (click to open, hover to preview) instead of the bare `[Image #N]` placeholder text.

## Key facts (from code)

- **Composer + a slash-command popup already exist** in `SessionWindow.cs`: `_composer` (TextBox), a `_palettePopup`
  `Popup` above the composer driven by `TextChanged → UpdatePaletteFromText`, with ↑↓/Tab/Enter handled in
  `OnComposerKeyDown`. The `@`-mention popup mirrors this (a second popup + rows), and input-history ↑↓ slots
  into the same key handler (only when neither popup is open and the caret is at the start).
- **`FuzzyMatch.TryMatch(query, path, out Result)`** (`Perch.Core/Data/FuzzyMatch.cs`, public) is the same
  matcher the slash palette uses — reuse it for `@`-file ranking + match highlighting.
- **`MarkdownProjectScan`** enumerates a repo's `.md` files via `git ls-files --cached --others
  --exclude-standard` (gitignore-honouring), with a bounded-walk fallback and a hard cap. A new
  `ProjectFileScan` generalises it to *all* files for the mention popup.
- **`AttachmentChip`** already renders an image attachment as a thumbnail that opens on click and shows a hover
  preview — so feature 5 is just *producing `MessageAttachment` (Image) items for resumed history*; the view
  is unchanged. `UserMessageItem` already carries `Attachments`, and `SessionThreadView.BuildUser` already
  renders a chip tray under the bubble.
- **History parsing** lives in `SessionConversation.ApplyTranscriptLine` / `GenuineUserPrompt`, which today
  keeps only `text` blocks and drops `image` blocks. Claude Code writes pasted images both as a `[Image #N]`
  text token *and* an `image` content block (base64), and caches the file at
  `~/.claude/image-cache/{sessionId}/{N}.{ext}`.
- **Thread view is a `ScrollViewer` subclass** (`SessionThreadView`) whose content is `_stack`; it already
  tracks `_stickToBottom` from `ScrollChanged`. `_center` (a `Panel`) layers the thread — the scroll buttons
  are added there, over the thread, aligned bottom-right.

## Design

### 1 — File mentions
- New `Perch.Core/Data/ProjectFileScan.cs`: `Scan(cwd) -> ProjectFiles(RelativePaths, Truncated)` — git
  ls-files (all paths, no pathspec) + bounded-walk fallback, cap ~5000, forward-slashed. (Factor the shared
  git runner / walk out of `MarkdownProjectScan`, or duplicate the tiny runner as that file already does.)
- `SessionWindow`: a `_mentionPopup` + `_mentionRows` mirroring the palette. In `TextChanged`, detect an active
  `@`-token = the text from a `@` (preceded by start-of-line or whitespace) up to the caret, containing no
  whitespace. Fuzzy-rank the scanned files; show top ~40. Keys (in `OnComposerKeyDown`, guarded by
  `MentionOpen` like `PaletteOpen`): ↑↓ move, Tab/Enter complete, Esc close. Accepting replaces the `@token`
  span with the path + trailing space. Files scanned off-thread on attach (and cached per cwd).

### 2 — Input history
- `SessionConversation.RecentUserPrompts()` → the `UserMessageItem.Text`s in order (includes resumed history;
  skips slash-command chips is unnecessary — recalling a prior `/cmd` is fine). Window keeps a nav index and a
  stashed draft. In `OnComposerKeyDown`: when no popup is open and the caret is at index 0 (Up) / end on the
  last-recalled entry (Down), cycle. Any edit/selection-move resets the index; sending resets it.

### 3 — Per-message copy
- `SessionThreadView`: a small, hover-revealed copy button on the user bubble and the assistant message row.
  Text source: `UserMessageItem.Text`; assistant = its `TextPart`s joined (raw markdown). Clipboard via
  `TopLevel.GetTopLevel(this)?.Clipboard`. Brief "Copied" tick on success. A reusable `CopyButton` control.

### 4 — Scroll buttons
- `SessionThreadView` exposes `JumpToBottom()`, `JumpToLastPrompt()`, a `ScrollStateChanged` event, and
  `AtBottom` / `LastPromptOffscreen` state (tracked from `ScrollChanged` + the last user-message control).
- `SessionWindow` adds a bottom-right overlay stack into `_center`: a "↓" button (visible when `!AtBottom`)
  and a "↥ last prompt" button (visible when `LastPromptOffscreen`). Styled as round `SessionPalette` chips.

### 5 — Inline images on resume
- New `Perch.Core/Data/Control/TranscriptImages.cs`: given a user message's content array + the session id,
  return `MessageAttachment`s for its `image` blocks. **Primary:** resolve `~/.claude/image-cache/{sessionId}/
  {N}.{ext}` (N = the block's 1-based order), no decode/write when the cache file exists. **Fallback:** decode
  the block's `source.data` base64 to a stable temp file (`%TEMP%/perch-images/{sha1}.{ext}`). Managed C#
  only (mirrors the existing send-side base64 *encode* in `PerchSession.BuildImageContents`).
- `SessionConversation.LoadHistory` / `ApplyTranscriptLine` gain the session id (threaded from
  `PerchSession`). `GenuineUserPrompt` also returns image attachments; the `[Image #N]` tokens are stripped
  from the display text when a matching attachment was produced. `UserMessageItem` + the chip tray are unchanged.

## Files
- New: `Perch.Core/Data/ProjectFileScan.cs`, `Perch.Core/Data/Control/TranscriptImages.cs`,
  `Perch.App/Views/CopyButton.cs`.
- Edit: `SessionWindow.cs` (mention popup, history nav, scroll-button overlay, thread wiring),
  `SessionThreadView.cs` (copy buttons, scroll API + last-prompt tracking),
  `SessionConversation.cs` (image extraction in history, `RecentUserPrompts`),
  `PerchSession.cs` (pass session id into `LoadHistory`), `HeadlessRenderer.cs` (render coverage).

## Verification
- `dotnet build perch.slnx` + `dotnet test tests/Perch.Tests/…` green; new tests:
  `ProjectFileScanTests` (or fold into the git-scan tests), `TranscriptImagesTests` /
  `SessionConversationTests` (an image-bearing history line → one Image attachment + stripped text),
  `RecentUserPrompts` ordering.
- `perch … -- render <dir>` capture: the `@`-mention popup open, a message with a copy button, the scroll
  buttons, and a resumed image chip (seed via `HeadlessRenderer`).
- **Live dogfood owed:** `@`-complete from a real repo, ↑/↓ history recall, copy, both scroll buttons, and a
  genuinely resumed session that had a pasted image.
