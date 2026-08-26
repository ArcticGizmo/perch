# Desktop basketball plan

A silly persistent-objects feature: a basketball **ring** attached to the side of the overlay panel and a
**ball** that bounces around the screen under real(ish) physics, eventually coming to rest. To shoot, you
click the resting ball and flick *toward* where you want it to go (originally a drag-away slingshot, but
the ball rests on the bottom edge of the screen where a pull-down has no room, so aim-where-you-flick won);
a partial trajectory preview appears while aiming. Unlike the arcade games (which live in their own windows), these are ambient
desktop objects that share the screen with everything else.

## Shape

Three pieces:

1. **`Perch.Core/Games/BasketballPhysics.cs`** — the pure, UI-free engine (like `Connect4Game`): ball
   state (position/velocity in DIPs), gravity, air drag, wall/floor/ceiling bounces with restitution,
   rolling friction, sleep detection, the hoop colliders (backboard segment + two rim-lip point colliders),
   swish detection (centre crosses the rim line downward between the lips), flick launch mapping
   (drag vector → capped same-direction velocity — with the vertical component mirrored to always mean
   *up*, because the resting ball sits on the very bottom of the work area and a launch into the floor is
   never what anyone meant), and the trajectory preview (the same integrator run
   forward without collisions, sampled). Internally sub-steps so a fast ball can't tunnel through a rim
   lip. Deterministic; tested in `tests/Perch.Tests/BasketballPhysicsTests.cs`.

2. **`Perch.App/Windows/BasketballWindow.cs`** — two windows:
   - **The layer**: a work-area-sized transparent, topmost, *click-through* window (per
     `IWindowChrome.MakeClickThroughNoActivate` — it never eats a desktop click, unlike the deliberately
     hit-testable `ReactionBubbleWindow`, because this layer is persistent). Its owner-drawn
     `BasketballLayer : Control` runs the house 16 ms `DispatcherTimer` loop (tick → step physics →
     `InvalidateVisual`), stopping whenever the ball is asleep and nothing is animating. It draws the
     backboard (with the running swish tally), rim + net, ball, aim rubber-band, and the partial
     trajectory dots.
   - **The hit windows**: small transparent no-activate tool windows over the interactive spots — a
     forgiving 72-DIP halo parked over the *resting* ball (press → pointer capture; drag → aim; release →
     launch and hide until the ball next rests), and one over the ring (left-drag sets the hoop height,
     persisted as `AppSettings.BasketballRimOffsetDip`, an offset below the panel top so it keeps riding
     the panel; right-click opens a menu with "Reset hoop height" and "Hide desktop basketball"). This
     sidesteps per-pixel hit-testing on a persistent full-screen window entirely: the click-through layer
     never takes input, and the hit windows only cover the ball and the hoop.

3. **App wiring** (`App.axaml.cs`) — gate on `Effective.BasketballEnabled` inside `ApplyDisplaySettings`
   (so Quiet mode masks it off and back on for free), re-anchor the hoop from the overlay window's
   `PositionChanged`/`Resized` (the panel resizes constantly — `SizeToContent`), persist the swish tally,
   and close the windows in `CloseAuxWindows`.

## Ring placement

`StickyNoteWindow.PlaceBesideOwner`'s side-picking maths, in physical pixels: compare the space left and
right of the overlay window on its screen's work area and hang the ring off the roomier side, backboard
flush against the panel edge, rim ~110 DIP below the panel's top. This one rule covers all three modes:

- **Floating** — the ring rides whichever side of the panel has more room, and follows drags live.
- **Docked** — the column is flush to a screen edge, so the open side always wins: the ring sits in the
  undocked area with its backboard against the column edge, visually "tied" to the panel. (A window can't
  paint past its own border — see the dock pull-tab note in `OverlayCanvas.Docked.cs` — so the ring is
  drawn by the layer window, not the panel.)
- **Dense** — the strip hugs an edge like docked; same rule, same result.

## Settings / persistence

- `AppSettings.BasketballEnabled` (bool, default off) + a `SettingsRegistry` toggle under
  `SettingSurface.Whimsy` with `playful: true` (Quiet mode silences it via `QuietMode.Resolve`).
  Live-apply needs nothing beyond the always-raised `DisplayChanged` → `ApplyDisplaySettings`.
- `AppSettings.BasketballHoops` (int) — the lifetime swish tally painted on the backboard. Hidden toy
  state like `WordleState`: listed in `SettingsRegistryTests.NotSettings`, saved when a shot drops.

## Colour

The ball is basketball-orange everywhere — a constant identity, so it's a **fixed** hue:
`FixedColors.Basketball` (+ `Simulate`) + `Palette.Basketball`/`BasketballBrush` (+ `Palette.Apply`).
The rim/net reuse `Palette.BrandBrush` (the brand red-orange *is* a rim colour) and theme chrome
(`OverlaySurfaceBrush`/`BorderBrush`) for the backboard, `Fg` alpha for the net and trajectory.

## Headless render

`BasketballLayer.CreateForRender()` poses a mid-flight frame (ring anchored to a fake panel edge, ball in
the air, trajectory dots, a tally on the board) and registers in `HeadlessRenderer.RenderAll` at 1× and
1.5×, like `ReactionBubbleLayer`.

## Deferred / out of scope

- Sound (an `IAudioCue` swish/bounce) — the seam exists if it's ever wanted.
- Achievements (e.g. "Buzzer beater") — easy to add on top of the tally later.
- Multi-monitor: the ball lives on the overlay's screen; it re-presents if the overlay moves screens.
- macOS: everything routes through existing seams (`IWindowChrome`); the mac head's click-through is
  already implemented (`ignoresMouseEvents`), so it should light up with the port untouched.
