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
3. **File link:** add a *File or folder* link to a PDF. Expect: a grey page mark (folded corner, three lines) on the strip, distinct from the amber note button; a click opens it in its viewer. The name is proposed from the file name until you type your own.
4. **Image link:** a *File or folder* link to a `.png`/`.jpg` shows a grey picture mark straight away (no initials, no wait on the shell).
5. **Folder link:** add a *File or folder* link via **Folder…**. Expect: a grey folder mark; a click opens that folder in Explorer.
6. **Script safety:** add a *File or folder* link to a `.bat` or `.ps1`. Expect: a click opens Explorer with the file selected; the script does not run.
7. **Missing file:** add a file link, then rename the file on disk. Click the link. Expect: a "Can't find …" toast. Settings shows the target with "⚠ not found".
8. **Web link:** add a *Web link* to `github.com`. Expect: the address is accepted as `https://github.com/`, the name proposes "github.com", the strip shows initials immediately and GitHub's favicon shortly after. Left click opens it in the browser; middle click opens it in a new browser window.
9. **Bad addresses:** in *Web link*, type `javascript:alert(1)`, `file:///C:/Windows/notepad.exe`, `mailto:a@b.com`. Expect: "Enter a web address, such as https://example.com." and **Save** disabled for each.
10. **Intranet:** a web link to a private-network host (e.g. `http://192.168.1.10:8080`) still gets its favicon from that host.
11. **No favicon:** a web link to a site with no `/favicon.ico` keeps its initials, and editing Settings repeatedly doesn't refetch it (one request a week).
12. **Settings list:** long file paths in the Quick Links list keep their file name visible and lose the leading folders (leading ellipsis).

## Phase 4: custom icons

### The change

Any link can wear an imported image instead of its kind's icon. The dialog's **Icon** row has **Choose icon…** and **Reset to default**. The image is decoded with the pixel cap, fitted into a 64px transparent square and stored as Perch's own PNG in `%APPDATA%\Perch\quick-link-icons\`. The link records only the file name. Nothing is written until **Save**.

### Checks

1. **Import:** edit any link → **Choose icon…** → pick a PNG or JPEG. Expect: the preview shows it, with "Your icon, used in place of the link's own." Save. The strip shows that image, upright and not stretched (a wide image is letterboxed in its square).
2. **Cancel leaves nothing:** choose an icon, then **Cancel**. Expect: no new file in `%APPDATA%\Perch\quick-link-icons\` and the link unchanged.
3. **Original can go:** after importing, move or delete the source image. Expect: the link keeps its icon (Perch has its own copy).
4. **Replace:** choose a different icon for the same link and Save. Expect: exactly one file for that link in the folder (the old one is deleted).
5. **Reset:** **Reset to default** → Save. Expect: the kind's icon returns (app icon, file-type icon or favicon) and the link's file is deleted.
6. **Remove:** remove a link that has a custom icon. Expect: its file is deleted.
7. **Precedence:** give a web link a custom icon. Expect: the custom icon shows, and no favicon request is made for it.
8. **Bad input:** choose a file renamed to `.png` that isn't an image, or a huge image (e.g. 20000×20000). Expect: "Perch can't use that file as an icon…" and the previous icon kept.
9. **Missing file:** delete a link's icon file by hand while Perch runs, then toggle the link off and on. Expect: it falls back to the kind's icon, never blank.
10. **Orphan sweep:** quit Perch, drop a copy of one of the icon files under a new 32-hex-digit name (e.g. `0123456789abcdef0123456789abcdef.png`) and add a `notes.png` too. Start Perch. Expect: the 32-hex file is deleted; `notes.png` is left alone.
11. **Sweep stands down:** corrupt `settings.json` so it fails to parse (Perch recovers it into `settings.unreadable.json`). Expect: no icon files are deleted that launch.
