# Perch — Feature Ideas

A parking lot for features that don't yet exist. Nothing here is committed; it's a
grab-bag to pull from. Split into genuinely useful and whimsical/delightful — Perch
has always been both.

> Note: `IGlobalHotkey` already exists as a Core interface, so a couple of these are
> closer than they look.

## Useful

- **Focus / Do-Not-Disturb hours** — a schedule (or one-click toggle) that mutes chimes,
  push, and the attention glow during meetings or after hours, then delivers a quiet
  "3 sessions needed you" summary when you come back.
- **Daily spend guardrail** — set a soft token/cost ceiling; the tray badge warms as you
  approach it and pings once at 80/100%. Complements the burn-rate readout (that's the
  speedometer, this is the fuel gauge).
- **Git diff peek on hover** — the `+142 -37` chip already exists; let hovering it pop the
  actual changed-files list (or a mini diff) so you can gut-check a session without
  alt-tabbing.
- **Weekly digest** — an opt-in Sunday-night notification (or exportable card) summarising
  the week: active hours, busiest project, longest session — basically Wrapped on a
  cadence, not just on demand.
- **Notification sound themes** — beyond the single chime, a small set of cues (and
  per-event sounds: "done" vs "needs you" vs "stuck") so you can tell what happened without
  looking.
- **Stats CSV / JSON export** — let the data layer that's already built escape for people
  who want to chart it themselves.

## Whimsical

- **Bird skins & seasonal hats** — unlockable plumage, plus a Santa hat in December and
  sunglasses in summer. Purely cosmetic, entirely worth it.
- **Tamagotchi mode** — the bird's long-term wellbeing tracks your streak; feed it by
  shipping, neglect it and it looks a bit ruffled. A gentle guilt engine.
- **Milestone fireworks** — confetti is per-session; add a screen-wide celebration for
  lifetime landmarks (first 1M tokens, 100th session, a 30-day streak).
- **Time-of-day roosting** — the idle bird yawns and tucks its head at night, perks up in
  the morning. Matches the mood system already shipped.
- **Rubber-duck click** — click the tray bird when stuck and it offers a random rubber-duck
  prompt ("what did you expect to happen?"). Half joke, half genuinely useful.
- **Idle micro-animations** — when nothing's happening the bird preens, hops, or looks
  around, so the overlay feels alive rather than frozen.
- **Ambient chirps** — optional, very-off-by-default soft birdsong when all sessions are
  idle; silenced the moment work resumes.
