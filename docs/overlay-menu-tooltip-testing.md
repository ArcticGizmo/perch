# Overlay menus vs dwell tooltips: manual test prompts

Right-clicking a glyph on the overlay that has a hover tooltip (an RSS feed head, for example) opened its menu at the pointer. The pointer was still over the glyph, so the 750 ms dwell tooltip then popped up on top of the menu and swallowed clicks meant for it. Now every overlay menu, all shown through `OverlayCanvas.ShowFlyout`, hides any showing tooltip as it opens, and no tooltip arms or fires until the menu closes.

The UI has no automated coverage and the headless render can't open a menu, so these checks are manual.

## Setup

- **Before:** the installed Perch (0.5.43).
- **After:** this branch, run with `dotnet run --project src/Perch.App -f net10.0-windows10.0.19041.0` (quit the installed tray first).

You need at least one feed in the overlay's feeds row (Settings → search "feeds").

## 1. Right-click a feed head and wait

Hover a feed head, right-click it straight away, and keep the pointer still for a second or two.

| | Before | After |
|---|---|---|
| Tooltip | Appears over the menu after ~¾ s | Never appears while the menu is open |
| Clicking "Check now" (or any item) under where the tip sat | Doesn't register | Works |

## 2. Right-click while the tooltip is already showing

Hover a feed head until its tooltip shows, then right-click.

| | Before | After |
|---|---|---|
| Tooltip | Stays up over the menu | Hidden the moment the menu opens |

## 3. Tooltips come back after the menu closes

Close the menu (Esc, pick an item, or click elsewhere on the overlay), then hover a feed head again.

| | After |
|---|---|
| Tooltip | Appears after the usual dwell |

## 4. Other menus

Repeat check 1 anywhere else a glyph with a tooltip sits under a right-click menu, e.g. a session row's context thermometer (right-click opens the row's menu). Same results as for the feed head.
