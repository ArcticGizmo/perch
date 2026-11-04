# Session list scroll and compact rows: manual test prompts

Manual checks for keeping the overlay on its monitor when there are a lot of sessions (`Views/OverlayCanvas.Scroll.cs`). It adds:

- **A height budget.** When floating or in the dense popup, the panel may be as tall as the monitor's work area less 32 DIP at the top and 16 at the bottom. Docked, it is the reserved column less room for the bottom collapse handle. The budget is re-read from the window's monitor whenever the panel resizes and on a display change.
- **A scrolling session list.** When the whole layout would run past the budget, only the session rows give way and become a viewport, never smaller than about three roomy rows. The "+ New session" row, the daemon strip and every other section keep their height.
  - **Scrolling.** The mouse wheel scrolls the rows. A thin thumb on the panel's right edge can be dragged, and a press elsewhere in its band jumps there.
  - **Cues.** A pill at the viewport's top or bottom ("● 2 above" / "● 1 below") counts the sessions waiting on you (API error, input, done) that are scrolled out of view. Its dot takes the most urgent colour among them. A click scrolls to the nearest one.
  - **Jumps.** The jump-to-next-session hotkey and a PR banner both scroll their row into view.
- **A floating panel that grows upward near the bottom.** A panel that gets taller keeps its top where it is, so when its bottom would cross the work area it is lifted just enough to stay on screen. This isn't saved to the remembered placement.
- **Compact rows.** A new density button on the "+ New session" row (left of the grouping button, or furthest right when there are no work items). Compact rows drop the activity/elapsed line, so a session takes 26px instead of 46px. The button's glyph is the density showing now: two spaced rows (roomy) or three tight rows (compact). The choice is kept in `AppSettings.CompactSessionRows`, which is off by default and has no Settings control (it's in `SettingsRegistryTests.NotSettings`).

The headless render (`render <dir>` → `overlay_scroll_*.png`, `overlay_compact_rows_1.5x.png`) covers the layout with a 560 DIP budget. Only a real tray covers the wheel, the thumb drag, the cues' clicks and the monitor-derived budget.

## Setup

Run the dev tray: `dotnet run --project src/Perch.App -f net10.0-windows10.0.19041.0`.

To overflow the panel without dozens of sessions, shrink the work area. Set Windows display scaling to 175% or 200% (Settings → System → Display), or use a small monitor. Turn on a few strips (system info, usage, todos), then start idle `claude` sessions in terminals until the rows reach the bottom. Idle sessions cost nothing.

## 1. The panel stops at the bottom of the screen

Default placement (top-right). Keep adding sessions.

| | Before | After |
|---|---|---|
| Panel bottom once the rows outgrow the screen | Runs off the bottom of the monitor; the last rows can't be reached | Stops ~16px above the taskbar; the rows scroll |
| Other sections (usage, todos, daemon strip, …) | — | Unchanged in height; only the session rows shrink to fit |
| A thin thumb on the panel's right edge alongside the rows | — | Present only while the rows overflow |

## 2. Scrolling

| Action | Expected |
|---|---|
| Wheel over the rows | Rows scroll about a row per notch; the hover highlight follows the row under the cursor; any open tooltip hides |
| Wheel over any other section | Nothing scrolls (and nothing else happens) |
| Hover the thumb | It widens and brightens; hand cursor, not the resize cursor |
| Drag the thumb | Rows follow it smoothly; releasing ends the drag (no click lands on a row) |
| Press the thumb's band above/below the thumb | The thumb jumps there, and you can keep dragging |
| Scroll down by any amount | A divider appears under the "+ New session" row where rows slide beneath it |
| Click a row, a PR glyph, a note glyph after scrolling | Acts on the row actually under the cursor (hit-testing follows the scroll) |
| Hover the right edge **above or below** the rows | The resize cursor and grip dots still appear; the panel still widens |

## 3. Cues for hidden sessions that need you

Scroll so a session awaiting input (or with an API error / done) is out of view.

| | Expected |
|---|---|
| Hidden above | "● N above" pill at the top-right of the rows; yellow dot for input, red if any is an API error, orange for done |
| Hidden below | "● N below" at the bottom-right |
| Click a pill | Scrolls just far enough to show the nearest such session; the pill's count drops |
| Nothing hidden needs you | No pill |

## 4. Jumping to a session

With the target session scrolled out of view, press the jump-to-next-session hotkey (or wait for a PR state change banner on a hidden row).

| | Expected |
|---|---|
| The row | Scrolls into view, then shows the blue landing highlight (or the PR banner) |

## 5. Compact rows

| Action | Expected |
|---|---|
| The "+ New session" row | A new density button, left of the grouping button (or far right with no work items); glyph = two spaced rows |
| Hover it | Tooltip "Roomy rows / Click for one line per session" |
| Click it | Every session row becomes one line (name, glyphs, status); the activity/elapsed line goes; the panel gets shorter; the glyph becomes three tight rows |
| Overflowing list, then compact | Far more rows fit; if they all fit now, the thumb and cues disappear |
| A wrong-account row in compact | Still has the red wash and pulsing outline, without the warning text line |
| Restart the tray | Compact is kept (`"CompactSessionRows": true` in settings) |
| Click again | Back to roomy two-line rows |

## 6. Floating near the bottom of the screen

Drag the panel so its bottom sits near the taskbar, then start another session (or expand the Autonomous group).

| | Before | After |
|---|---|---|
| Panel grows | Grows down past the taskbar | Lifted just enough to stay on screen, then scrolls once it reaches the top margin |
| Restart, or change display | — | Returns to the placement you set, not the lifted spot |

## 7. Dense popup and docked column

| Mode | Expected |
|---|---|
| Dense: hover the strip to open the popup with an overflowing list | Popup fits the monitor; rows scroll as above |
| Docked: an overflowing list | Rows stop above the bottom collapse handle and scroll; the handle never covers a row |
| Change resolution / scaling while overflowing | Budget follows the new work area after the display change |

## Not covered

- **Dragging a row onto a work item that's scrolled out of view.** The list doesn't auto-scroll during a session drag; scroll first, then drag.
- **A panel dragged partly off the bottom by hand.** The budget is the monitor's height, not the room under wherever the panel was dropped, and a manual drag is never clamped.
- **Sub-agent rows.** They keep their own 24px line in compact mode; collapse a session's agents with their chevrons if they crowd the list.
