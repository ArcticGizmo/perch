# Docked collapsed column: session tiles instead of auto-expand (manual testing)

The change: in **Docked** mode with the column **collapsed**, a session that needs you no longer expands the
column. Expanding re-reserved the screen edge and reflowed every maximised window. Instead, the empty space
below the status counts now shows **one tile per session**:

- a rounded square ringed in the status colour, with a two-letter monogram of the project name;
- sessions that need you (done / input / api error) sort to the top, get a status-coloured wash and a corner
  badge: `↩` done, `?` input, `!` api error;
- the wash **breathes while the attention chase border runs** (~10s), then holds steady until handled;
- **click** a tile to focus that session, and **hover** for its full name + status;
- when they don't all fit, the last slot is a **"+N"** tile, and clicking it expands the column.

Floating mode and dense mode are unchanged: they still pop open on attention.

Render check: `dotnet run --project src/Perch.App -f net10.0-windows10.0.19041.0 -- render <dir>` writes
`overlay_docked_collapsed_attention_1x.png` and `overlay_docked_collapsed_overflow_1x.png`. Reference renders
are in `captures/20261015-docked-tiles/`.

## How to run each build

- **Before (main):** check out `main`, then `dotnet run --project src/Perch.App -f net10.0-windows10.0.19041.0`
- **After (branch `fixes`):** same command on `fixes`.

Quit any installed Perch first (tray → Exit). Switch to Docked (Settings → search "overlay mode" → Docked),
then collapse the column (Ctrl+Shift+W, or the bottom `›` handle). Keep a maximised window next to it so you
can see whether it reflows.

## Prompts to drive a status change

Start two or three sessions (different projects make the monograms distinct). In one, send something that
finishes quickly:

```
Reply with just the word "done".
```

To get an **input** (`?`) state, ask for something that needs a permission prompt (in default permission
mode):

```
Create a file called scratch-test.txt containing "hello", then stop.
```

## Scenarios

| # | Steps | Before (main) | After |
| --- | --- | --- | --- |
| 1 | Column collapsed, maximised window beside it. Send the "done" prompt and switch away. | The column **expands** to the full panel. The maximised window **shrinks/reflows**. | The column **stays collapsed** and nothing reflows. The session's tile jumps to the top with an orange wash + `↩` badge, breathing under the chase border. |
| 2 | Wait ~10s after 1. | The chase stops, and the panel stays expanded. | The chase stops. The tile keeps a **steady** wash + badge until you handle it. |
| 3 | Click the highlighted tile. | (n/a) | Focuses that session's terminal / Perch window. |
| 4 | Hover a tile for ~1s. | (n/a) | A tooltip opens **toward the screen** (left of a right-docked column, right of a left-docked one) with the name, status and "Click to focus". |
| 5 | Run the permission prompt and leave it waiting. | Expands. | Tile at the top, yellow wash, `?` badge. |
| 6 | Open enough sessions that they can't all fit (or use a short screen). | (n/a) | The last slot reads `+N`. Hover says "N more sessions". Clicking it expands the column. |
| 7 | Handle the session (reply / approve). | Panel stays open. | Its tile drops back down (green while running, grey once idle). |
| 8 | Repeat 1 with the column **expanded**. | Chase border on the panel. | Same: the expanded behaviour is unchanged. |
| 9 | Repeat 1 in **Floating** mode, collapsed. | The panel expands. | The panel expands (unchanged). |
| 10 | Windows "Show animations" **off** (reduce motion), then repeat 1. | (n/a) | The tile wash is steady from the start (no breathing). |

## Known limits

- Two sessions in the same project share a monogram. The tooltip tells them apart.
- Sub-agents don't get tiles. A session's tile covers its sub-agents, as the counts do.
- "Jump to next session" (cycle hotkey) and PR banners still don't surface anything on a collapsed docked
  column. Both paint on rows that aren't shown there. That's pre-existing and not changed here.
