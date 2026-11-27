# Quick links: link kinds, custom icons, upright icons

Status: **plan**. Phase 1 is built in code (uncommitted on `main`); phases 2–4 are not started.

## Why

A quick link is currently "an app, found by name or exe path". Anything else, such as an image you want to open, gets pushed through the app-icon path. That mismatch caused the v0.6.20 report: an `ExePath` pointing at a `.png` stalled the shell icon lookup, and the whole strip vanished. Links should say what they are (an app, a file or folder, or a web page) and each kind should launch and get its icon in its own way. On top of any kind, the user can import their own image as the link's icon.

Along the way the icon orientation is fixed where it goes wrong, which retires the overlay's flip workaround and the "upside-down quick links" easter egg.

## Phases

| Phase | What | Ships as |
|---|---|---|
| 1 | Hang fix: the strip shows its links at once, and each icon lookup runs on an STA thread with a deadline | patch release |
| 2 | Upright icons at the source; remove the flip and the upside-down setting | with phase 3, or on its own |
| 3 | Link kinds: App / File or folder / Web link | feature release |
| 4 | Custom icons: import, scale, store in Perch | feature release |

Each phase ships a section in `docs/quick-links-icon-hang-testing.md`, renamed to `docs/quick-links-testing.md` when phase 2 lands, with before/after checks.

---

## Phase 1: one bad link can't hide the strip (built)

- `App.ReloadQuickLinks` puts the enabled links on the strip immediately (initials), then resolves icons off the UI thread and swaps them in. Each link's lookup is in its own `try`, and a generation counter stops a slower, older pass from overwriting a newer edit.
- `WindowsAppIconProvider.GetIconFile` renders each icon on a dedicated **STA** background thread with a 10s deadline (`RenderOnSta`). `IShellItemImageFactory` is apartment-threaded; on a thread-pool (MTA) thread, a file whose icon comes from a packaged handler (a `.png` owned by Photos) can block forever, and the provider's lock then starves every later request. A render that misses the deadline is abandoned (background thread) and memoised as "no icon".

Remaining: commit, release, and run the testing doc's checks against the installed build.

## Phase 2: upright icons, no flip, no easter egg

**Root cause.** `ShellIcon.FromHBitmapWithAlpha` copies the shell's 32bpp DIB section row by row, assuming its rows are stored top-down. `IShellItemImageFactory.GetImage` hands back a **bottom-up** DIB (positive height), so every cached PNG is upside down. The overlay then mirrors it back, in `DrawQuickLinksRow` and in `DrawOriginBitmap(flip: true)`, and the easter egg is just "don't mirror". The two fallback paths, `Image.FromHbitmap` (non-32bpp results) and `ExtractClassicIcon` (`Icon.ToBitmap`), already produce upright bitmaps, so today the overlay flips *those* the wrong way.

**Fix at the source, not at the draw.**
- `FromHBitmapWithAlpha`: read the orientation instead of assuming it. `GetObject` into a `DIBSECTION` (its `dsBmih.biHeight` is signed: positive = bottom-up, negative = top-down). For bottom-up, copy source row `h - 1 - y` into destination row `y`. Fix the comment that says "top-down".
- Every PNG `WindowsAppIconProvider` writes is then upright. `IAppIconProvider`'s contract gains one line: *returned PNGs are upright*. The mac provider (`Perch.Platform.Mac/AppIconProvider.cs`) is a stub today. When it's filled in it must honour that too, which it will naturally, since AppKit images aren't bottom-up DIBs.
- The cache needs no busting: `Render` rewrites each PNG on first use in every process (the memo is per process), so stale upside-down files are overwritten on the next launch.

**Remove the flips.**
- `OverlayCanvas.DrawQuickLinksRow`: draw every icon straight (`ctx.DrawImage(icon, iconRect)`).
- `OverlayCanvas.DrawOriginBitmap`: drop the `flip` parameter and the mirror, and drop `flip: false` from its callers (the Perch mark). Check the tinted-icon path (`_tintedIcons`) and any other `DrawOriginBitmap` caller for an implicit flip assumption.

**Remove the easter egg** (`UpsideDownQuickLinks`):
- `AppSettings.UpsideDownQuickLinks`, its `SettingsRegistry` descriptor (playful), the Settings toggle (`SettingsWindow` ~L1831), `OverlayCanvas._upsideDownQuickLinks` + `SetUpsideDownQuickLinks`, the line in `OverlaySettingsGates.Apply`, the call in `App.ReloadQuickLinks`, and the references in `QuietModeTests`.
- Old settings files still carry `"UpsideDownQuickLinks": …`. Confirm the settings deserialiser ignores unknown members (and that `SalvageMerge` doesn't trip on one); add a test that loads a file containing the key.
- Changelog: one dry line saying the icons now come out the right way up on their own.

**Checks.** Headless `render` of the overlay (quick links + IDE-origin rows) at 1× and 1.5×. Live: presets, an exe-path link, a Store app, and the VS Code / Claude Desktop origin icons, all upright.

## Phase 3: link kinds

**Model** (`Perch.Core/Data/QuickLink.cs`):

```csharp
public enum QuickLinkKind { App, File, Url }

public sealed class QuickLink
{
    public string Name { get; set; } = "";
    public QuickLinkKind Kind { get; set; } = QuickLinkKind.App;  // absent in old files → App
    public string ExePath { get; set; } = "";                      // App only; name kept so old files and perch-hook's mini-parser stay valid
    public string Target { get; set; } = "";                       // File: a path; Url: an http(s) URL
    public bool Enabled { get; set; } = true;
    public string? CustomIcon { get; set; }                        // phase 4
}
```

Serialise `Kind` as a string, as `SectionOrder` does. `Clone()` copies the new fields.

**Behaviour per kind.** Routing is a pure Core function (`QuickLinkAction QuickLinkRouting.Resolve(QuickLink)`) so it's testable without launching anything. `QuickLinkLauncher` executes the action.

| Kind | Left click | Middle click | Icon (before phase 4's override) |
|---|---|---|---|
| App | focus the running window, else launch (unchanged) | same as left | Start Menu → exe icon (unchanged) |
| File | open with its default handler (`UseShellExecute`); a folder opens in Explorer/Finder | same as left | the file-type icon from the shell (safe now that lookups have a deadline) |
| Url | `PlatformServices.UrlOpener.Open(url)` | `UrlOpener.OpenInNewWindow(url)` (the repo-wide middle-click rule) | the site's favicon, else initials |

- **URL validation**: accept only absolute `http`/`https` URLs, both in the dialog and again in `Resolve`. A `file:`, `javascript:`, `ms-settings:` or other scheme is refused, so a web link can never become a disguised shell-execute.
- **File links** don't "focus if running". A missing target leaves the tile visible with its initials; clicking it shows a toast ("Can't find …") instead of failing silently.
- **Favicons** reuse the Feeds machinery: the same fetch `FeedsService` does for a feed's site icon, validated by `FeedIcon.Validate`, cached as a PNG under the Local `IconCache`, fetched once per URL host and refreshed rarely. Fetch off the UI thread; until it lands (or if it fails) the tile shows initials. The overlay strip needs a middle-click path: today `QuickLinkActivated` carries no button. Add the button (or a second event) so Url links can honour it; App/File treat middle as left.
- **Dialog** (`Windows/QuickLinkDialog.cs`): a three-way choice at the top: *App* (the current name/presets/browse-exe UI), *File or folder* (browse file / browse folder), *Web link* (URL box with validation). The name defaults from the target (the file name, or the URL host) and stays editable.
- **Settings list** (`SettingsWindow` quick-links rows): show the kind's target under the name, as a path that keeps its *file name* and elides the directory (`PrefixCharacterEllipsis`, per the repo's path rule).
- **Registry**: `Kind`/`Target` live inside the `QuickLinks` list, which already has its descriptor; no new top-level setting. Confirm `SettingsRegistryTests` doesn't need a new entry.

**Migration.** None needed: a missing `Kind` reads as `App`. The v0.6.20 image link (an `App` with a `.png` `ExePath`) keeps working as before (phase 1 makes it safe). The dialog could offer "this looks like a file, convert it?" when editing such a link, but that's optional. Per [explicit over magic], suggest it and never auto-convert.

**Tests** (Core): routing per kind (including App with an explicit path vs by name); URL validation (http/https accepted; `file:`, `javascript:`, relative, and whitespace-only refused); round-trip of an old file with no `Kind` → App; round-trip of each kind.

## Phase 4: custom icons

**Behaviour.** Any link can have its own icon. Precedence: **custom → kind's own icon → initials**. The link dialog gets *Choose icon…* (shows a preview) and *Reset to default*.

**Import** (App side, because scaling needs Avalonia):
1. Pick an image. Accept what `Perch.Data.ImageHeader` recognises (PNG, JPEG, BMP, GIF, WebP). Refuse SVG and ICO with a clear message.
2. Load through `Views/BoundedBitmap.LoadAsync` (header first, pixel cap, decoded off the UI thread), per the repo rule for user-supplied images.
3. Scale once to fit **64×64** (aspect kept, centred, transparent padding). The strip draws at 16px; 64px stays sharp up to 4× display scale.
4. Encode PNG and write it atomically to `%APPDATA%\<profile data folder>\quick-link-icons\{guid}.png`: Roaming, beside `settings.json`, because it's user data, not cache. Store just the file name in `QuickLink.CustomIcon`, never an absolute path, so the profile stays self-contained and the original image can move or be deleted.

**Lifecycle.**
- Replacing an icon writes a new `{guid}.png` and deletes the old one *after* the settings save succeeds.
- Removing a link, or *Reset to default*, deletes its file.
- Startup sweep: delete any file in `quick-link-icons` no link refers to. This is a pure Core function over (referenced names, files present) so it's testable with a temp dir; skip it under replay/`DisablePersistence`.
- A `CustomIcon` whose file is missing falls back to the kind's icon, never to blank.

**Rendering.** `App.ReloadQuickLinks` resolves custom icons first (no shell call; just the path, after checking the file exists) and only asks the provider for links without one. Phase 2 makes every icon upright, so custom icons need no special case in the canvas.

**Portability.** Copying `settings.json` alone to another machine leaves the icons behind; those links fall back to their normal icons. The Export page could bundle `quick-link-icons/` later; out of scope here.

**Tests** (Core): icon precedence (custom present / custom missing / none); orphan sweep (keeps referenced, deletes unreferenced, ignores non-PNG and subfolders); `CustomIcon` round-trip; the path is rejected if it isn't a bare file name (no separators or `..`), so a hand-edited settings file can't point the loader elsewhere.

## Open questions

- Should a Url link's favicon refresh on a schedule (weekly?) or only when the link is edited? Default: on edit, plus a weekly re-fetch.
- Custom icon size: is 64px enough, or store 128px for very large displays? 64px is plenty at the strip's 16px.
- Does the dense strip show quick links anywhere? If so, the same icons apply; confirm nothing there flips independently.
