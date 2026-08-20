# Spike: reserve a screen edge (AppBar)

Proves Perch can claim a permanent, non-overlapping column so maximized windows
never cover it — using the Windows **AppBar API** (`SHAppBarMessage`), the same
mechanism the taskbar uses.

## Run it

```powershell
# Reserve a 320px right column until you close the little window:
powershell -NoProfile -ExecutionPolicy Bypass -File .\Reserve-Edge.ps1

# Unattended 8s smoke test (auto-releases):
powershell -NoProfile -ExecutionPolicy Bypass -File .\Reserve-Edge.ps1 -Seconds 8

# Other edges / widths:
powershell -NoProfile -ExecutionPolicy Bypass -File .\Reserve-Edge.ps1 -Edge Left -Width 260
```

While it's registered, **maximize any window** — it stops at the strip's edge.

## What it proved (2026-08-20)

```
Work area BEFORE reserve: X=0 Y=0 W=1920 H=1032
Work area DURING reserve: X=0 Y=0 W=1600 H=1032   <- 320px column removed
Reserved strip rect: L=1600 T=0 R=1920 B=1032
Work area AFTER release:  X=0 Y=0 W=1920 H=1032   <- cleanly restored
```

## Gotcha found

`System.Windows.Forms.Screen.PrimaryScreen.WorkingArea` **caches** the screen
table at process start, so it reports the stale (un-shrunk) size even after the
reservation lands. Read the authoritative value via
`SystemParametersInfo(SPI_GETWORKAREA)` (as the script now does). The real Perch
head uses Avalonia's `Screens`, which refreshes on `Screens.Changed`, so this
particular trap won't bite the integration — but it's a reminder to trust the OS,
not a cached snapshot.

## Spike 2 — runtime interactions (`Reserve-Edge-Interactive.ps1`)

Proves the reservation is fully **mutable at runtime** — every mutation is just another
`ABM_QUERYPOS`/`ABM_SETPOS` on the *same* registered appbar, no re-register. Run it and
drag the blue inner grip (resize), the buttons, or the dark header onto another
monitor/side; or `-AutoDemo` to drive it all and print the work area at each step.

`-AutoDemo` output (3-monitor machine, 2026-08-20):

```
[Right] width=320  workarea=W=1600     <- baseline reserve
1) resize 320 -> 480   [Right] width=480  workarea=W=1440   <- drag-to-expand: column grows
2) collapse -> 56      [Right] width= 56  workarea=W=1864   <- narrow mode
3) expand -> 320       [Right] width=320  workarea=W=1600
4) flip side R -> L    [Left ] width=320  workarea=X=320 W=1600  <- column jumps to left edge
5) next monitor        [Left ] width=320  workarea=X=0 W=1920   <- reservation left primary onto mon 2
6) release             workarea=W=1920
```

So: **drag-to-expand ✅, collapse-to-narrow ✅, change side ✅, change monitor ✅** — all
live, all reversible, all on one appbar handle.

Measurement note: `SPI_GETWORKAREA` returns only the **primary** monitor's work area, so
step 5 shows the primary snapping back to full (the reservation moved *off* it). To assert
the secondary monitor shrank, query `GetMonitorInfo(rcWork)` on that monitor specifically —
the relocation itself is already proven by the primary reverting.

## Registration sequence (the load-bearing bit)

1. `ABM_NEW` — register the appbar against a real top-level HWND.
2. Fill `rc` with the desired full-edge strip on the target monitor.
3. `ABM_QUERYPOS` — let the shell nudge `rc` around existing appbars (taskbar).
4. Re-pin the strip's *thickness* (QUERYPOS only guarantees the edge position).
5. `ABM_SETPOS` — **commit; this is what shrinks the work area.**
6. Move the window into the returned `rc`.
7. On exit: `ABM_REMOVE` (or the reservation leaks until the HWND is destroyed).

See `../../docs/reserve-edge-plan.md` for caveats and how this folds into Perch.
