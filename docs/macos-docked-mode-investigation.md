# Docked mode on macOS - is the reservation even possible?

**Investigated 2026-08-24.** Companion to `docs/reserve-edge-plan.md` (which shipped the Windows AppBar
implementation and left the Mac head a no-op stub). Question asked: docked mode doesn't behave on macOS the
way it does on Windows - can it?

## Short answer

**No - not the reservation part.** macOS has no supported API, public or private, that lets a third-party app
shrink the space other apps' windows are allowed to occupy. Everything *else* about docked mode already works
on the Mac head; the one thing that doesn't is the one thing that makes it "docked" rather than "floating".

The only way anyone achieves this on macOS is to become a window manager: take the Accessibility permission
and resize other apps' windows out of the way *after* they've been placed. That is a real option, with real
costs, and it is blocked in practice until Perch's macOS build is signed and notarised (see below).

## Why not

On Windows the OS does the containment: `SHAppBarMessage`/`ABM_SETPOS` shrinks the desktop work area, and the
shell then keeps every maximized window clear of the strip. No per-window work.

macOS's equivalent quantity is `NSScreen.visibleFrame`, and the WindowServer computes it from **the menu bar
and the Dock only**. Verified on this machine (throwaway Swift/AppKit probe, single 13" display):

```
screen frame        = (0, 0, 1512, 982)
screen visibleFrame = (0, 90, 1512, 859)      # 33pt menu bar + 90pt Dock, nothing else
```

A third-party window - whatever its level, collection behaviour or style mask - contributes nothing to that
calculation. Concretely:

- **No public API.** The Dock's carve-out is special-cased inside AppKit. Apple radar
  [FB9985546](http://www.openradar.me/FB9985546) asks for exactly this ("provide an API to let UIElement apps
  limit `[NSScreen visibleFrame]`, similar to the Dock"); it is still unimplemented as of macOS 26, and the
  Tahoe window-tiling work added nothing for third parties either.
- **No usable private API.** SkyLight/CGS expose window, space and event plumbing (that's what yabai and
  friends drive), not a reserved-area hook. Note that yabai's `external_bar` setting is *not* a reservation -
  it's an offset applied to yabai's own tiling maths, because yabai is already placing every window itself.
- **The apps that look like they do it, don't.** uBar - the closest macOS analogue to a Windows taskbar - uses
  the **Accessibility API** to resize windows out from under itself. [Their own support
  page](https://support.ubarapp.com/support/solutions/articles/150000000945-app-windows-are-going-under-ubar)
  documents the failure mode: apps that don't fully implement AX (Java, Carbon-derived, some ports) slide
  underneath and uBar can't stop them.

## What actually works on the Mac head today

Docked mode is *mostly* fine on macOS - the gap is narrower than "it doesn't work". Verified by reading the
code paths plus an Avalonia probe on this machine:

| Piece | macOS status |
| --- | --- |
| Column geometry: full work-area height, flush to the edge | **Works.** Avalonia's `WorkingArea` on macOS matches `visibleFrame`, so the column clears the menu bar and a bottom Dock. |
| DIP/pixel conversions in `ApplyDockedGeometry` | **Works** - but by luck. Avalonia reports `Screen.Bounds` in *points* with `Screen.Scaling == 1` on macOS (window `RenderScaling` is still 2), so every `* scale` / `/ scale` is a no-op and the numbers land right. Worth a comment; today's `// physical pixels` is Windows-only truth. |
| Always-on-top column | **Works** - `NSStatusWindowLevel` + collection behaviour in `Platform.Mac/WindowChrome`. |
| Collapse/expand, drag-to-redock, drop lanes, all painting | **Works** - platform-independent code. |
| **Edge reservation** | **Missing, and unfixable as designed.** `Platform.Mac/EdgeReservation` is a no-op, so maximized/zoomed windows sit *under* the column. This is the reported problem. |
| Live OS monitor read (`GetMonitorGeometryAt`) | **Missing** - returns null, so the code falls back to Avalonia's cached screens. Correct today; means no self-heal after a resolution change beyond `Screens.Changed`. Easy to implement (`NSScreen` frame/visibleFrame). |
| Display-change re-derive | **Partial.** The reliable path is the Windows raw-message hook (`WM_DISPLAYCHANGE` / `WM_SETTINGCHANGE`) in `LiveOverlayWindow`. macOS has only Avalonia's `Screens.Changed` (which is wired to `NSApplicationDidChangeScreenParametersNotification`, so probably adequate - untested). |
| **Side Dock collision** | **Real bug.** `ApplyDockedGeometry` takes `x` from the screen *bounds*, not the work area (correct on Windows, where the AppBar negotiates with a side taskbar via `ABM_QUERYPOS`). On macOS a left- or right-positioned Dock gets covered by the column. Using the work area's X/width on the Mac head fixes it. |
| Fullscreen apps | **Diverges.** The Mac head sets `NSWindowCollectionBehaviorFullScreenAuxiliary`, so the column floats *over* fullscreen apps; Windows AppBars yield to them (`docs/reserve-edge-plan.md` calls yielding the desired behaviour). |
| Settings copy | **Wrong on macOS.** The `overlay-mode` descriptor promises "a reserved screen-edge column that maximized windows can't cover". |

## The options

### A. Honest floating column - small

Keep the no-op reservation; fix the things that are simply wrong. Fix the side-Dock collision, implement
`GetMonitorGeometryAt` on macOS, decide the fullscreen behaviour deliberately, and soften the settings copy on
the Mac head so it doesn't promise containment it can't deliver. Docked mode stays "a full-height column
pinned to the edge that's always on top" - useful, but windows still go under it.

### B. Accessibility-based soft reservation - large, and currently blocked

Implement `Platform.Mac/EdgeReservation` as a miniature window manager, the uBar approach:

1. Gate on `AXIsProcessTrusted()`; prompt with `AXIsProcessTrustedWithOptions` and give Settings a button that
   opens System Settings -> Privacy & Security -> Accessibility.
2. On reserve, walk `NSWorkspace.runningApplications` (regular activation policy), and for each
   `AXUIElementCreateApplication(pid)` read `kAXWindowsAttribute`; shrink any window whose frame intrudes into
   the reserved strip.
3. Keep it that way with an `AXObserver` per app (`kAXWindowCreated`, `kAXWindowMoved`, `kAXWindowResized`)
   plus `NSWorkspace` launch/terminate notifications.
4. Only clamp windows that read as *maximized* (frame ≈ `visibleFrame`), or you'll fight users who
   deliberately park a window under the column.

Honest costs:

- **The permission is the blocker.** Accessibility grants are keyed to the app's code-signing identity;
  for an unsigned or ad-hoc-signed build (which is what `publish-mac.sh` produces today - see the README's
  "macOS (unsigned)" note) the grant is tied to the binary and users should expect to re-grant after updates.
  Asking for the scariest permission macOS offers, repeatedly, for a status-bar app, is a bad trade. **This
  option only becomes reasonable after signing + notarisation.**
- Apps that don't fully implement AX escape it - permanently, with no workaround.
- The resize happens *after* the window is placed, so there's a visible snap. It is not the seamless OS-level
  containment Windows gives you.
- Fullscreen windows (which live in their own Space) can't be resized at all.
- It's an ongoing maintenance surface: observers, per-app lifecycles, a heuristic that will need tuning.

Rough size: a multi-day job, several hundred lines of P/Invoke against ApplicationServices plus an
`[UnmanagedCallersOnly]` observer callback and a run-loop source - and then the long tail of app-specific
misbehaviour.

### C. On-demand tidy - medium

The same Accessibility machinery, minus the standing observer fleet: a "push windows out of the column"
action, run when docked mode is entered and on a hotkey. Still needs the permission, but it's far less code,
has no background cost, degrades gracefully when an app ignores it, and never fights the user. It doesn't
*maintain* the invariant - a newly maximized window will cover the column until you ask again.

## Recommendation

Do **A** now, and treat docked mode on macOS as an always-on-top column rather than a reservation - which
means saying so in the UI. Revisit **B** only if the macOS build gets signed and notarised, and even then
prefer starting with **C** to find out whether the AX approach behaves acceptably before committing to the
observer fleet.

The thing not to do is leave the settings copy promising containment that the OS will not provide.
