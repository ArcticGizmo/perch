# Tray icon blur at 125 % and 175 %

The tray icon goes soft at some display scales in Perch, Sprig, Hypertree and Flamingle, while Shrike stays crisp. The fix is to do what Shrike does: hand the tray one 32 px PNG and let Explorer shrink it. Explorer shrinking an image looks fine; Explorer stretching one is what blurs.

The same doc is in each of the four repos. It was written on 2026-11-27 after fixing the same problem in Ochre.

## How Ochre was fixed

Ochre uses a WinForms `NotifyIcon`, not Avalonia. It picked one frame from its `.ico` for `SM_CXSMICON` at `GetDpiForSystem()`. The system DPI only updates at sign-in, so after a scale change and a restart Ochre still handed the tray a 16 px frame and Explorer stretched it to 20 or 24 px. The fix always hands the tray the 32 px frame (Ochre commit `d8ff7f7`, `src/Ochre.Platform.Windows/Shell/AppIcon.cs`). It still needs a check on a real screen at 125 % and 150 %.

## Why the Avalonia apps blur

All five apps run Avalonia 12.0.5. Its Win32 tray code (`TrayIconImpl.cs`, `IconImpl.cs` and `Interop/Win32Icon.cs` in AvaloniaUI/Avalonia at tag `12.0.5`) does this:

1. It asks for an icon of `ceil(16 × taskbar scale)` px, using the taskbar monitor's current DPI: 16 at 100 %, 20 at 125 %, 24 at 150 %, 28 at 175 %, 32 at 200 %.
2. If the image is an `.ico`, it takes the frame whose size is closest to that. On a tie, the frame listed first wins. It never resizes the frame.
3. If the image is a PNG, it makes the icon at the PNG's own size and never resizes it.
4. It only reloads the icon on `WM_DISPLAYCHANGE` or when Explorer restarts, not when the scale changes.

Perch, Sprig and Hypertree's `.ico` files hold 16, 24, 32, 48, 64, 128 and 256 px frames, with no 20 or 28. At 125 % the 20 px request ties between 16 and 24, so the 16 px frame wins and Explorer stretches it. At 175 % the 28 px request ties between 24 and 32, and the 24 px frame gets stretched. At 100 %, 150 % and 200 % the frames match exactly.

Shrike hands the tray a 32 × 32 PNG (`src/Shrike.App/Assets/tray.png`, rendered by `tools/IconGen`). Avalonia passes it through at 32 px, and Explorer shrinks it to the tray's size at every scale up to 200 %.

## What each app does today

| App | Installed build | Dev build | Where |
|---|---|---|---|
| Perch | `icon.ico`: stretched at 125 % and 175 % | A 64 px inverted PNG that Explorer shrinks from 64 px | `src/Perch.App/App.axaml.cs`, `TrayIconImage()` |
| Sprig | `sprig.ico`: stretched at 125 % and 175 % | A 256 px inverted PNG that Explorer shrinks from 256 px | `src/Sprig.App/AppIcon.cs`, `Current` |
| Hypertree | `icon.ico`: stretched at 125 % and 175 % | A 256 px tinted PNG that Explorer shrinks from 256 px | `src/Hypertree.App/DevChrome.cs`, `AppWindowIcon()` |
| Flamingle | A PNG drawn at the exact size for the primary monitor's scale when it starts | The same, in its dev colour | `src/Flamingle.App/Views/TrayIconArt.cs` |

Shrinking from 256 px to around 20 px in one step tends to look grainy, so the dev builds can look rough too. Flamingle's exact-size PNG should be crisp in theory, and the code doesn't show why it isn't. It does use the primary monitor rather than the taskbar's, and it keeps the size it picked at start. Moving it to the Shrike approach makes all five apps behave the same.

## The fix

1. Add a 32 × 32 `tray.png` to the app's icon generator, rendered straight from the SVG at 32 px, as Shrike's `tools/IconGen` does (`WritePng(doc, Path.Combine(assetsDir, "tray.png"), 32)`).
2. Hand that PNG to the tray. Keep the `.ico` for windows, the taskbar and the `.exe`, which pick their own frames correctly.
3. Tint or invert the 32 px image for a dev build, not a 256 px one.

```csharp
// Avalonia makes a tray icon from a PNG at the PNG's own size, and Explorer only ever shrinks it.
var image = new Bitmap(AssetLoader.Open(new Uri("avares://<app>/Assets/tray.png")));
if (AppProfile.IsDev) image = DevTint(image);
_tray.Icon = new WindowIcon(image);
```

For Flamingle, `TrayIconArt.Create` returns the 32 px frame (`flamingle-32.png` or `flamingle-dev-32.png`) and drops the DPI lookup.

The cost is that at 100 % the tray shows a 32 px image shrunk to 16 px instead of a frame drawn for 16 px. Shrike shows that this looks right in practice.

## How to check

1. Set the display scale to 125 %, restart the app, and compare its tray icon with Shrike's on a light and a dark taskbar.
2. Do the same at 175 %, then at 100 % and 150 %.
3. Check a dev build too, since it takes a different image.
4. Change the scale while the app runs. The icon should stay sharp, because Explorer shrinks the same 32 px image to the new size.
