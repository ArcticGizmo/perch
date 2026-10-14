# Do Not Disturb header button: manual test prompts

Manual checks for the opt-in **Do Not Disturb button** on the right of the overlay header. It adds:

- **A sleeping-bell glyph in the header's right-hand cluster** (a bell with two "z"s, left of the ▲/▼ chevron, and
  left of the update badge when one shows). It's outlined in the muted colour while Windows Do Not Disturb is off
  and filled in the accent colour while it's on. A 5s poll mirrors the OS state, so a change made in the Action Center or by a schedule shows up.
- **One-click toggle.** A click flips Windows DND. There's no documented API for that:
  `FocusSessionManager.TryStartFocusSession` is a Limited Access Feature that needs a Microsoft unlock token, and
  rewriting the CloudStore blob doesn't take effect live. Publishing the shell's WNF state
  (`WNF_SHEL_QUIETHOURS_ACTIVE_PROFILE_CHANGED` via `NtUpdateWnfStateData`) was tried first and Windows ignored it
  on 26200. So `WindowsDoNotDisturb.TrySetActive` calls the shell's own out-of-process `QuietHoursSettings` COM
  server (CLSID `f53321fa-…`, undocumented `IQuietHoursSettings` `6bff4732-…`) and sets `UserSelectedProfile` to
  `Microsoft.QuietHoursProfile.PriorityOnly` (on) or `.Unrestricted` (off). The call runs off the UI thread.
- **The setting** "Do Not Disturb button" (`AppSettings.ShowDndButton`, off by default). It sits under
  Notifications and is hidden on macOS (`PlatformFeature.DoNotDisturbToggle`).

- **Reading the state.** On newer builds (seen on 26200) the profile blob lives under
  `CloudStore\Store\DefaultAccount\Current\{guid}$windows.data.donotdisturb.quiethourssettings`, found by suffix.
  The older `Cache\DefaultAccount\$$windows.data.notifications.quiethourssettings` key stays frozen there, so it's
  only a fallback. Reading only the old key made the button ignore the Action Center *and* snap back after every
  click, whatever the write did.

The headless render (`render <dir>` → `overlay_dnd_*.png`) covers the glyph. Only a real tray covers the toggle.

## Setup

Run the dev tray: `dotnet run --project src/Perch.App -f net10.0-windows10.0.19041.0`. Keep the Windows
notification centre (Win+N) handy, since its bell button is the reference for the real DND state.

**Watch for EDR.** Activating an undocumented shell COM server is less unusual than a raw ntdll call, but still
watch for it. If the toggle fails with nothing visible, or Falcon raises an alert, stop and raise it with SecOps.
Don't retry it.

## 1. The setting shows and hides the button

Settings → search "do not disturb" → turn on **Do Not Disturb button**.

| | Before | After |
|---|---|---|
| Overlay header, right side | ▲ chevron + dense-toggle glyph only | A sleeping bell to the left of the chevron |
| Turn the setting off again | — | Sleeping bell disappears; nothing else moves |

## 2. The button follows DND changed elsewhere

With the button on, toggle DND from the notification centre's bell.

| | Expected |
|---|---|
| DND turned on in the Action Center | Sleeping bell fills with the accent colour within ~5s |
| DND turned off in the Action Center | Sleeping bell returns to a muted outline within ~5s |

## 3. One-click toggle (the COM write)

With DND off, click the sleeping bell.

| | Expected |
|---|---|
| Sleeping bell | Fills immediately (optimistic) and **stays** filled after ~5s |
| Notification centre bell (Win+N) | Shows Do Not Disturb on |
| A test toast (e.g. Settings → Notifications → send a test) | Held; doesn't pop |

Click it again: the sleeping bell goes back to an outline and the notification centre shows DND off.

If the sleeping bell fills and then **snaps back** to an outline within a second (the post-click read-back), Windows took the call but didn't apply it.
Note `winver`'s build number.

If you get a toast saying "Windows didn't accept the change", the COM activation failed or the setter returned an
error HRESULT. That could be the interface having moved on this build, or an EDR block.

## 4. Hover and tooltip

Hover the sleeping bell: a faint wash shows behind it and the hand cursor appears. After ~¾s a tooltip reads "Do Not
Disturb is off / Click to silence notifications" (or "…is on / Click to turn it off").

## 5. Interplay with Social auto-close

With Social on and "Close friends in Do Not Disturb" on, expand the friends region and then turn DND on with the
sleeping bell. The friends region collapses within a second, rather than waiting for the next 60s feed tick.

## Not covered

- **macOS.** The setting is hidden there and `MacDoNotDisturb.CanToggle` is false, so a settings file carried over
  from Windows doesn't draw a dead button.
- **Dense strip (closed).** The closed strip has no header, so the button only shows in the dense hover popup,
  floating mode and docked mode.
- **Future Windows builds.** `IQuietHoursSettings` is undocumented and may change. The failure mode is the snap-back above,
  not a crash.
