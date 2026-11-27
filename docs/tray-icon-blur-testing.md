# Tray icon blur: testing

How to check the fix described in [tray-icon-blur.md](tray-icon-blur.md). On Windows the tray now gets `Assets/tray.png`, a 32 × 32 PNG rendered by `tools/IconGen`, and Explorer only ever shrinks it. A dev instance inverts that same 32 px image. Windows, the taskbar and the `.exe` still use `icon.ico`. macOS keeps its current icon.

Nothing here can be checked headlessly: the `render` mode doesn't draw the tray.

## Builds

- **Before:** the installed Perch (any release up to 0.6.19), or `main` before this change.
- **After:** this change, run with `dotnet run --project src/Perch.App -f net10.0-windows10.0.19041.0`. That's a dev instance, so its icon is inverted. To check the normal colours, run `publish.bat` and install the build.

Run them side by side: a dev instance and an installed Perch run together, and their inverted and normal icons tell them apart.

## Checks

Restart Perch after each scale change: Avalonia only reloads the tray icon on a display change or an Explorer restart.

| Scale | Before | After |
|---|---|---|
| 100 % | Sharp (exact 16 px frame) | Sharp: 32 px shrunk to 16 |
| 125 % | Soft: 16 px frame stretched to 20 | Sharp: 32 px shrunk to 20 |
| 150 % | Sharp (exact 24 px frame) | Sharp: 32 px shrunk to 24 |
| 175 % | Soft: 24 px frame stretched to 28 | Sharp: 32 px shrunk to 28 |
| 200 % | Sharp (exact 32 px frame) | Sharp (exact 32 px) |
| Dev build, any scale | Can look grainy: shrunk from 64 px in one step | Sharp: the inverted 32 px image |

1. Settings → Display → Scale: 125 %. Restart Perch and compare its tray icon with Shrike's, on a dark and then a light taskbar (Settings → Personalization → Colors).
2. Repeat at 175 %, then at 100 % and 150 %.
3. With Perch running, change the scale without restarting. The icon should stay sharp, even if its size lags until the next restart.
4. Check that the Perch window icons and the taskbar button still look as they did. They still use `icon.ico`.

## `.ico` frames

`icon.ico` now holds uncompressed 32bpp BMP frames below 256 px and a PNG frame only at 256, as Shrike's does. Windows mishandles PNG-compressed small frames, so they can render low-res.

| Where | Before | After |
|---|---|---|
| Explorer, Details or Small icons view of `perch.exe` | Can look soft | Sharp 16/24 px frame |
| Taskbar button and Alt+Tab, at 100 % and 150 % | Can look soft | Sharp |
| Session or Settings window title-bar icon | Can look soft | Sharp |

Explorer caches icons, so after installing, refresh the folder view or sign out and in before comparing.

## Menus and hints after a scale change

Avalonia caches each screen's scale and only refreshes it on `WM_DISPLAYCHANGE`, which a scale change doesn't send. Menus, the tray menu and the overlay's hover hints went on placing themselves with the old scale. Now, when the overlay gets `WM_DPICHANGED` and the cached scale for its monitor is wrong, it posts itself `WM_DISPLAYCHANGE` so Avalonia refreshes the cache.

Run Perch and leave it running throughout: the bug only shows when the scale changes while the app is open.

| Step | Before | After |
|---|---|---|
| Change the scale (say 100 % → 150 %), then right-click the tray icon | Menu opens offset from the cursor, or flips the wrong way near the taskbar | Menu opens at the cursor |
| Hover the usage strip until its panel appears (it opens to the left of the overlay) | Gap to the overlay too big or too small, or overlapping it | A small, even gap |
| Hover a glyph near the bottom or right of the screen | Hint clamps to the wrong spot, or runs off-screen | Hint stays on-screen beside the glyph |
| Right-click a session row near the bottom of the screen | Menu may flip or slide wrongly (it checks fit against the cached work area) | Menu flips up cleanly and stays on-screen |
| Drag the overlay onto a monitor with a different scale | Unchanged | Unchanged: the drag isn't interrupted and the overlay stays where you drop it |

Also new: after a scale change the floating overlay re-places itself from its remembered corner, just as it already did after a resolution change.

Known gap: the refresh needs the overlay to be on the monitor whose scale changed. If the overlay is on another monitor, the cache stays stale until the next display change.
