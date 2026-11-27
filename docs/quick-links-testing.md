# Quick links: manual testing

Covers the work in `docs/quick-links-kinds-plan.md`, one section per phase. Each check says what to do and what to expect, with a "before" check where the old behaviour is worth seeing first.

## Phase 1: one stalled icon lookup can't hide the strip

### The bug

The quick-links strip showed only the note button, even though `settings.json` still held every link. The strip waited for every link's icon before it showed any of them. A link whose `ExePath` is a non-executable file (here a `.png`, whose icon comes from the packaged Photos app) made the shell icon call hang on a thread-pool (MTA) thread. That one stall hid every link, and because it held the provider's lock, every later icon request (a Settings edit, removing the link) blocked behind it until Perch restarted.

### The fix

- `App.ReloadQuickLinks` puts the links on the strip at once (initials), then swaps the icons in when they resolve. Each link's lookup is isolated, and a generation counter stops an older, slower pass from overwriting a newer edit.
- `WindowsAppIconProvider` renders each icon on its own STA thread with a 10s deadline. A render that misses the deadline is abandoned and the link keeps its initials.

### Checks

1. **Before (v0.6.20):** add a quick link whose path is an image file (Settings → Quick Links → Add → browse to a `.png`). Restart Perch. Expect: the strip shows only the note button.
2. **After:** same setup with the fixed build. Expect: every link appears straight away. The presets get their real icons within a second or two. The image link shows either an icon or its initials, and never hides the others.
3. **After:** with Perch running, disable and then re-enable the image link in Settings. Expect: the strip updates immediately each time and never goes blank.
4. **After:** click the image link. Expect: it opens in its default viewer, as before.
5. **Regression:** the session-row IDE/Claude Desktop origin icons (same provider) still render.

## Phase 2: upright icons, no upside-down setting

### The change

`ShellIcon.FromHBitmapWithAlpha` read the shell's bitmap rows as if top-down; they're bottom-up, so every cached icon PNG was upside down and the overlay mirrored it back. The copy now follows the bitmap's own orientation, the overlay draws icons as they are, and the "Upside-down quick links" setting is gone.

### Checks

1. **Before:** open `%LOCALAPPDATA%\Perch\IconCache` and look at a cached Outlook or Teams icon PNG. Expect: upside down (Outlook's "O" tile at the top-left).
2. **After:** restart Perch, then look again. Expect: the same files rewritten the right way up (the "O" tile bottom-left).
3. **After:** the overlay's quick links (GitKraken, Slack, Teams, Outlook, and any exe-path link) are all upright.
4. **After:** with *IDE icons* on, VS Code / JetBrains / Claude Desktop session rows show their app icon upright, and a Perch-driven session's Perch mark is upright too.
5. **After:** Settings → search "upside". Expect: nothing. The Quick Links page has no upside-down toggle, and Quiet mode's list in the docs no longer mentions it.
6. **After:** a `settings.json` that still has `"UpsideDownQuickLinks": true` loads with every other setting intact (no `settings.unreadable.json` appears).

## Phase 3: link kinds (App / File or folder / Web link)

### The change

A link now says what it points at. The Add/Edit dialog has a three-way switch. File links open with their default app (folders open in the file manager; programs and scripts are revealed, not run). Web links open in the browser, accept only http/https, and show the site's favicon.

### Checks

1. **Existing links:** after updating, every existing link still shows and opens exactly as before (they read as App links).
2. **Convert hint:** edit the old image link (an App link whose program is a `.png`). Expect: "This path isn't a program." with **Make it a file link**. Click it: the dialog switches to *File or folder* with the path carried over. Save. The link now opens the image with its default viewer.
3. **File link:** add a *File or folder* link to a PDF. Expect: the PDF's file-type icon on the strip; a click opens it in its viewer. The name is proposed from the file name until you type your own.
4. **Folder link:** add a *File or folder* link via **Folder…**. Expect: a folder icon; a click opens that folder in Explorer.
5. **Script safety:** add a *File or folder* link to a `.bat` or `.ps1`. Expect: a click opens Explorer with the file selected; the script does not run.
6. **Missing file:** add a file link, then rename the file on disk. Click the link. Expect: a "Can't find …" toast. Settings shows the target with "⚠ not found".
7. **Web link:** add a *Web link* to `github.com`. Expect: the address is accepted as `https://github.com/`, the name proposes "github.com", the strip shows initials immediately and GitHub's favicon shortly after. Left click opens it in the browser; middle click opens it in a new browser window.
8. **Bad addresses:** in *Web link*, type `javascript:alert(1)`, `file:///C:/Windows/notepad.exe`, `mailto:a@b.com`. Expect: "Enter a web address, such as https://example.com." and **Save** disabled for each.
9. **Intranet:** a web link to a private-network host (e.g. `http://192.168.1.10:8080`) still gets its favicon from that host.
10. **No favicon:** a web link to a site with no `/favicon.ico` keeps its initials, and editing Settings repeatedly doesn't refetch it (one request a week).
11. **Settings list:** long file paths in the Quick Links list keep their file name visible and lose the leading folders (leading ellipsis).
