# Quiet mode

A **Quiet mode** that temporarily silences the fun / social / silly features for a chosen span,
toggled from the overlay header's right-click menu. Named "Quiet mode" (not "Do Not Disturb") to
avoid colliding with the existing Windows-OS-DND signal (`CloseFeedInDoNotDisturb`,
`PlatformServices.DoNotDisturb`).

## The abstraction (the important part)

Rather than sprinkling `&& !quiet` across 50+ feature use-sites, there is a single **effective-settings
resolution layer**:

- A feature opts in **once, at its definition** by marking its `SettingsRegistry` entry
  `playful: true` (backed by `SettingDescriptor.Playful`).
- `Perch.Data.QuietMode.Resolve(raw, quietActive)` returns a masked **copy** of `AppSettings` with
  every playful toggle forced off while a quiet window is active (it iterates the registry and uses
  each descriptor's existing `SetBool` binding). `QuietUntil` is preserved on the copy.
- Everything downstream reads the **resolved** settings and never knows Quiet mode exists.

This works because feature gating already funnels through two narrow points:
- `OverlaySettingsGates.Apply(canvas, settings)` — all overlay glyphs.
- A handful of behavioural reads in `App` (`OnFriendPosted`, `OnReactionToMyPost`,
  `PresentAchievementUnlocks`) and feed-poll activation.

`App` keeps `_appSettings` (raw, edited/persisted) and a cached `_effectiveSettings` (resolved).
`ApplyEffectiveSettings()` is the one place a settings change **and** a quiet toggle/expiry funnel
through — it re-resolves, calls `ApplyDisplaySettings(_effectiveSettings)`, and re-arms the expiry
timer. Behavioural reads use the `Effective` property.

### Adding a new fun feature

Set `playful: true` on its `SettingsRegistry.Toggle(...)` entry. Nothing else — Quiet mode picks it
up automatically, and `QuietModeTests.RegistryPlayfulSetMatchesExpectation` will flag the change so
the intent is reviewed.

## What it silences

Social & friends (`SocialEnabled`, `NotifyOnFriendPost`, `ShowLargeReactions`), Whimsy
(`PerchReacts`, `NotifyOnAchievement`, `AchievementToasts`, `UpsideDownQuickLinks`), and the Arcade
header shortcut (hidden while quiet). Deliberately **not** the media/mic strips or any functional
alert (done / waiting / API error / PR) — those stay on the raw settings.

## State & interaction

- `AppSettings.QuietUntil` (nullable local wall-clock deadline, persisted; a past value reads as off).
- Header right-click menu: a "Quiet mode" submenu (30 min / 1 hour / 2 hours / until tomorrow
  morning) when off; a single "turn off (Nm left)" item when on. DEBUG builds also offer a "For 1
  minute (dev)" preset to test the come-back-online path quickly (`QuietDuration.Minute1`).
- While active, the header brand mark is swapped for a 💤 glyph (`DrawHeader`) so quiet is visible
  at a glance; it reverts when the window ends.
- Auto-expires via a one-shot `DispatcherTimer` (`ScheduleQuietExpiry`); survives a restart.

## Files

- `src/Perch.Core/Data/QuietMode.cs` — resolver + `QuietDuration` + deadline policy (pure, tested).
- `src/Perch.Core/Data/AppSettings.cs` — `QuietUntil`.
- `src/Perch.Core/Data/SettingDescriptor.cs` — `Playful` flag; `SettingsRegistry.cs` — `Toggle(playful:)`.
- `src/Perch.App/Services/OverlaySettingsGates.cs` — `SetQuietUntil`.
- `src/Perch.App/Views/OverlayCanvas.cs` — menu, event, `_quietUntil`.
- `src/Perch.App/App.axaml.cs` — effective-settings plumbing, expiry timer, toggle handler.
- `tests/Perch.Tests/QuietModeTests.cs`; `SettingsRegistryTests.cs` (`QuietUntil` in `NotSettings`).
