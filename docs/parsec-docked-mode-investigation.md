# Docked mode under Parsec (remote desktop) — investigation

**Status:** Investigated + fixed in code, 2026-08-26 (branch `docked-mode-fixes`). Records why the
docked column comes out the wrong vertical height/placement when the work laptop is driven over
[Parsec](https://parsec.app/), which of Perch's assumptions Parsec violates, and the fixes shipped.
Not yet confirmed against a live Parsec session — see "As-built" at the end.

## Symptom

Users remote onto their work laptops with Parsec. The laptop runs Perch. **Docked mode** (the
edge-reserved full-height column) sizes and/or positions itself wrongly on the vertical axis — the
column droops under the taskbar / off the bottom, or falls short of it. Floating mode is fine.

**Reproduced locally without Parsec** (2026-08-26): changing the Windows display scale while docked
breaks the column the same way. Crucially, **a manual Ctrl+Shift+W collapse/expand fixes it** — which
tells us the geometry math is correct *once the scales have settled*, and the real defect is that the
**automatic** re-derive fires at the wrong moment, or not at all. See "Corrected mechanism" below.

## Corrected mechanism (supersedes the "durable split" theory)

An earlier draft of this doc claimed the OS scale and the window's `RenderScaling` stay *permanently*
split and that collapse/expand therefore couldn't fix it. **That was wrong** — collapse/expand does
fix it. The accurate picture:

* The scale mismatch is **transient**, not durable. When the DPI changes, Avalonia updates the
  window's `RenderScaling` (via `WM_DPICHANGED`) on its own schedule, which does **not** coincide
  with the instant the OS DPI read (`GetDpiForMonitor`) changes. So a re-derive that runs *during*
  that window computes against two inconsistent scales and lands wrong; a re-derive that runs *after*
  everything settles is correct.
* **Manual collapse/expand (`ToggleDockedCollapsed`) applies the geometry twice** — once immediately,
  then again via `Dispatcher.UIThread.Post(..., DispatcherPriority.Loaded)` after the layout pass
  settles (`OverlayCanvas.Docked.cs:73,81`), and unconditionally. That **deferred second apply**, at a
  settled moment, is what heals it.
* **The automatic heal (the debounce) applies once**, gated on `sig != _dockGeomSig`, with **no
  posted follow-up**. So it can fire mid-settle (wrong) with nothing to correct it — or, for a
  scale-only change, not fire at all (next section). The compute logic is the same; the *scheduling*
  is not.

So the DIP↔px scale-source question below is still worth fixing (it makes a single apply internally
consistent), but it is **not** the reason the column stays broken. The reason is the auto-heal missing
the settled moment.

## The one assumption docked mode makes that Parsec breaks

`OverlayCanvas.ApplyDockedGeometry` (`src/Perch.App/Views/OverlayCanvas.Docked.cs`) computes the
column's **DIP height** by dividing the OS work-area height by a scale it reads **straight from
Win32**:

```csharp
var os = PlatformServices.WindowChrome.GetMonitorGeometryAt(cx, cy);   // GetDpiForMonitor(EFFECTIVE)
scale  = os.Scale;                       // e.g. 1.5 from GetDpiForMonitor
double dipH = Math.Max(1, waH / scale);  // waH is physical px
w.Height    = dipH;                      // <-- handed to Avalonia in DIPs
w.Position  = new PixelPoint(x, waY);    // physical px — correct
```

But **Avalonia converts `w.Height` (DIPs) back to physical pixels using the *window's*
`RenderScaling`**, not the `GetDpiForMonitor` value we divided by. The code silently assumes

> `GetDpiForMonitor(EFFECTIVE_DPI) / 96  ==  window.RenderScaling`

Everywhere *else* in the overlay, sizing goes through `w.RenderScaling` — floating mode
(`OverlayCanvas.cs:574`, `EnsureFloatingOnScreen`), the dense host (`IDenseHost.WindowScaling =>
HostWindow.RenderScaling`). Docked mode is the **only** place that sizes off a *different* scale
source. That's fine on a real monitor because after any resolution/DPI change Windows drives
`WM_DPICHANGED` and the two reconverge — the exact environment the current self-heal code was
built and tested against. **Under Parsec they can stay split**, and then:

```
rendered physical height = dipH * RenderScaling
                         = waH * (RenderScaling / GetDpiForMonitor_scale)
```

- Parsec settles at DPI 100% (`GetDpiForMonitor` → 1.0) while Avalonia's window is still at
  `RenderScaling` 1.5  → column **50% too tall** → droops under the taskbar / off-screen.
- The reverse (OS 1.5, render 1.0) → column **~33% too short**.

The vertical **position** (`waY`, physical) stays correct, so the top is right and the *bottom*
edge is wrong — which reads as "wrong height and placement."

## Why Parsec specifically produces the split (physical monitors don't)

Parsec doesn't just stream the panel; on the host it manipulates the display topology:

1. **Parsec Virtual Display Driver (VDD).** On connect Parsec can add a virtual monitor sized to
   the *client's* resolution, and remove it on disconnect. With the laptop lid closed the physical
   panel is off, so the whole desktop moves onto the virtual display. **Resolution, DPI, monitor
   identity and origin all change at connect/disconnect time** — repeatedly, over several seconds.
2. **Forced/late DPI.** The virtual display commonly reports **100%** effective DPI regardless of
   what the physical panel was at (often 125–150% on a laptop). So on connect the effective scale
   flips, and the moment when `GetDpiForMonitor` reports the new value and the moment Avalonia
   processes `WM_DPICHANGED` and updates `RenderScaling` **do not coincide** — leaving the two
   scales the docked math conflates pointing at different numbers.

On a physical monitor a DPI change is a single atomic `WM_DPICHANGED`; the scales never durably
disagree, so the bug was invisible in testing.

## Contributing problems (each can also skew the column)

The first two are the likely cause of the reported/reproduced breakage; the rest compound it under
Parsec's churn and are worth addressing together:

1. **A scale-only change may never call `OnDisplayChanged` at all.** The raw-message hook fires on
   `WM_DISPLAYCHANGE` (0x007E) or `WM_SETTINGCHANGE` *with `wParam == SPI_SETWORKAREA`*
   (`LiveOverlayWindow.axaml.cs:24-26,72`). A pure DPI/scale change arrives as **`WM_DPICHANGED`
   (0x02E0), which is not hooked**, and need not be accompanied by a resolution change — so the
   debounce may never even arm, and the column stays wrong until a manual collapse/expand. This is
   the most likely cause of the locally-reproduced case (scale changed, resolution didn't). Fix:
   hook `WM_DPICHANGED`, or subscribe to Avalonia's `ScalingChanged`, → `OnDisplayChanged`.

2. **The auto-heal lacks the manual path's deferred second apply.** `ToggleDockedCollapsed` applies
   the geometry immediately *and* re-applies via `Dispatcher.UIThread.Post(..., Loaded)` after the
   layout settles (`OverlayCanvas.Docked.cs:73,81`). `CreateDisplayDebounce` applies **once**. Even
   when the debounce does arm, its single apply can fire before `RenderScaling` has caught up, and
   nothing re-applies afterward. Give the auto path the same posted follow-up.

3. **The watchdog can be blind to a scale-only change.** `GeomSig` (the loop-safe signature the
   debounce compares) is built from the OS read — bounds + vertical work area + `GetDpiForMonitor`
   scale. That *does* include the OS scale, so a settled DPI change will change the signature and
   re-derive — **but** if the debounce fires during the transient window (OS scale already flipped,
   `RenderScaling` not yet), it heals against inconsistent scales, and a later `RenderScaling`-only
   settle produces no new OS-side signature change to re-trigger it. Fold `RenderScaling` into
   `GeomSig` so a render-scale-only settle also re-derives.

4. **500 ms debounce vs. Parsec's multi-second settle.** Parsec fires a *storm* of
   display/DPI/work-area changes on connect, and the topology can keep moving for well over 500 ms.
   The one-shot debounce can latch an **intermediate** geometry (e.g. the transient state where the
   taskbar hasn't re-reserved work area yet, so `rcWork == rcMonitor` and the column runs full-
   bounds height, overlapping the taskbar once it returns). A connect isn't a single settled event.

5. **Monitor identity by exact-bounds match is fragile across connect/disconnect.**
   `DockedScreen` matches the remembered monitor by `s.Bounds == _dockScreenBounds` (exact). When
   the VDD appears/disappears, bounds and origin shift, the match fails, and it self-heals to
   `screens.Primary` — which under Parsec may now *be* the virtual display, or the wrong one. The
   column can silently re-home to a different monitor than the user docked to.
   (`PlacementMath.PickMostOverlapping` most-overlap logic would survive this better than exact
   equality.)

6. **The AppBar reservation itself.** `EdgeReservation.Reserve` proposes a physical-pixel `rc`
   computed from `physW = (int)(dipW * os.Scale)`. Same scale-source question: if the reserved
   thickness is figured on `GetDpiForMonitor` while the window renders on `RenderScaling`, the
   *reserved* strip and the *drawn* column can be different widths, so the column doesn't sit flush
   (horizontal, not the reported symptom, but the same root cause and worth fixing in one pass).

## Fix direction (not yet implemented)

The headline — "auto-heal misses the settled moment" — is fixed by making the automatic path trigger
on a scale change and re-apply after the layout settles, exactly as the manual path already does:

1. **Trigger on DPI/scale, not just resolution/work-area.** Hook `WM_DPICHANGED` (0x02E0) in
   `LiveOverlayWindow`, or subscribe to Avalonia's `ScalingChanged`, → `OnDisplayChanged`. Without
   this, a scale-only change (the locally-reproduced case) never arms the debounce at all.
2. **Give the auto path the manual path's deferred second apply.** After the debounce's
   `ApplyDockedGeometry`, re-apply via `Dispatcher.UIThread.Post(..., DispatcherPriority.Loaded)` so
   the final size lands *after* `RenderScaling` settles — the same trick `ToggleDockedCollapsed` uses.

Then make each individual apply internally consistent and robust to the churn:

- In `ApplyDockedGeometry`, convert `waH → dipH` with **`w.RenderScaling`** (the scale Avalonia will
  actually render with) rather than the `GetDpiForMonitor` value. Keep the OS read as the authority on
  *where* and *how much space* (`waY`/`waH`, which Avalonia's cache gets stale on), and for the
  *reservation* rect (which genuinely wants physical px); make `RenderScaling` the authority on
  DIP↔px for this window. This can't remove the need for the settled re-apply, but it stops a single
  apply from being self-inconsistent.
- Add `RenderScaling` to `GeomSig` so a render-scale-only settle also re-derives.
- Consider a longer / re-arming settle after connect (Parsec doesn't settle in 500 ms), and resolve
  the docked monitor by most-overlap rather than exact-bounds equality so a VDD add/remove re-homes
  sanely.

A quick way to reproduce without Parsec: change the laptop's display scale (Settings → Display →
Scale) while docked, or RDP into the box (RDP does the same DPI/resolution swap Parsec does).

## As-built (branch `docked-mode-fixes`, 2026-08-26)

Shipped as code; both heads build, all 869 .NET tests pass. Interactive/Parsec verification still
outstanding.

1. **`WM_DPICHANGED` (0x02E0) is now hooked** in `LiveOverlayWindow` alongside `WM_DISPLAYCHANGE`
   and `SPI_SETWORKAREA`, so a scale-only change (local scale change, or a Parsec DPI flip with no
   resolution change) arms the re-derive at all.
2. **The auto-heal got the manual path's deferred second apply** — after the debounce's
   `ApplyDockedGeometry`, a `Dispatcher.UIThread.Post(..., DispatcherPriority.Loaded)` re-derives
   once the layout settles (and `RenderScaling` has caught up).
3. **`ApplyDockedGeometry` now sizes off `w.RenderScaling`**, not `GetDpiForMonitor`, for every
   DIP↔px conversion of the window (height, X, and the reservation thickness). The OS read stays the
   authority on *where* / *how much space*; `RenderScaling` is the authority on this window's px.
4. **`GeomSig` now keys on both scales** (OS scale + `RenderScaling`) so a render-scale-only settle
   re-derives instead of being missed.

Separately, the **dense↔docked coexistence bug** (switching to Docked while in dense mode left the
dense hover-to-expand live, so hovering the column snapped it to the dense popup) was fixed in the
same branch: `DenseController.Suspend()` tears down dense without the floating restore, called from
`OverlayCanvas.EnterDocked`.

Still deferred (see "Contributing problems"): the 500 ms debounce vs Parsec's multi-second settle,
and monitor identity by exact-bounds match across VDD add/remove. Revisit if the above doesn't fully
settle a live Parsec session.

## Files referenced

- `src/Perch.App/Views/OverlayCanvas.Docked.cs` — `ApplyDockedGeometry`, `GeomSig`,
  `CreateDisplayDebounce`, `DockedScreen`, `ReserveDockedColumn`.
- `src/Perch.Platform.Windows/WindowChrome.cs` — `GetMonitorGeometryAt` (`GetDpiForMonitor`).
- `src/Perch.App/Windows/LiveOverlayWindow.axaml.cs` — the raw-message hook (no `WM_DPICHANGED`).
- `src/Perch.Platform.Windows/EdgeReservation.cs` — the AppBar reservation.
- Related: `docs/reserve-edge-plan.md`, `docs/initial-placement-plan.md`,
  `docs/macos-docked-mode-investigation.md` (notes the same `Scaling` vs `RenderScaling` divergence
  on macOS).
