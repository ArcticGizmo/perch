# Next-meeting indicator — integration plan (SHELVED)

> **Status: shelved (2026-07-21).** Blocked on the one hard prerequisite: a Microsoft Entra
> **app registration**, which the author does not currently have rights to create in the tenant.
> Everything else is designed and buildable. Pick this back up if/when a registration (or an
> admin willing to make one) becomes available — or if a viable zero-auth calendar source
> re-appears (see "Why not zero-auth" below).

Goal: show the user's **next scheduled meeting** (e.g. "Standup in 15 min") as a slim line on the
overlay, sourced from their Teams/Outlook calendar, ideally without Perch running its own auth backend.

---

## TL;DR of the investigation

- **You can't read Teams directly.** New Teams (`MSTeams`, WebView2) stores data in an encrypted
  local cache with no supported API. It's also unnecessary: **Teams meetings are just Outlook/Exchange
  calendar events**, so the real target is the calendar.
- **The truly no-OAuth paths don't work on a modern managed Windows box** (verified by probe — see below).
- **Microsoft Graph is the only reliable source**, and it needs an Entra app registration (a client ID).
  No secret is required (public client), but the registration itself is the blocker.

---

## Evidence — probes run on the author's machine (2026-07-21)

Machine state: device is **Entra-joined** (`AzureAdJoined: YES`); **classic Outlook (Office16) installed**
with a default profile; **new Teams + new Outlook** (`MSTeams`, `Microsoft.OutlookForWindows`) present.

1. **WinRT `Windows.ApplicationModel.Appointments`** (reads the Windows *system* calendar store; zero auth):
   - A throwaway unpackaged `net10.0-windows10.0.19041.0` console called
     `AppointmentManager.RequestStoreAsync(AppointmentStoreAccessType.AllCalendarsReadOnly)`.
   - Result: **store acquired OK** — the restricted `appointmentsSystem` capability is **not enforced for
     full-trust unpackaged desktop apps**, so the API is reachable from Perch as shipped.
   - But it returned **1 empty "Calendar" and 0 appointments**. The Entra work calendar is **not synced
     into the Windows system store**. The app that used to feed that store (Windows Mail & Calendar) was
     **retired end of 2024** in favour of new Outlook, and new Outlook does **not** populate the system
     appointment store. This source is drying up on modern Windows 11.

2. **Classic Outlook COM automation** (`Outlook.Application` → MAPI → `GetDefaultFolder(9)`; reads Outlook's
   own Exchange cache, which *does* hold the real meetings):
   - `New-Object -ComObject Outlook.Application` failed with `CO_E_SERVER_EXEC_FAILURE (0x80080005)`;
     launching `OUTLOOK.EXE` and attaching via `GetActiveObject` also failed — classic Outlook **exited
     immediately** on launch. Consistent with the machine being migrated to new Outlook, which supports
     neither COM nor the system store. Unreliable here and being deprecated Office-wide.

**Conclusion:** both zero-auth options are dead ends on this setup. Use Graph.

---

## Recommended approach — Microsoft Graph, public client, broker-first

Read `GET /me/calendarView?startDateTime=...&endDateTime=...` (ordered, `$top` a handful), pick the next
event, surface it. Auth via **MSAL.NET** (`Microsoft.Identity.Client`) as a **public client with no secret**.

- **Windows:** `Microsoft.Identity.Client.Broker` → `.WithBroker(new BrokerOptions(BrokerOptions.OperatingSystems.Windows))`
  = **WAM**. Silent SSO against the Entra work account already on the device. Needs a parent window handle
  (HWND) — we already have the overlay window.
- **macOS:** MSAL.NET runs on plain `net10.0` (drops into `Perch.Platform.Mac`). If the Mac is MDM-managed
  with the Microsoft Enterprise SSO / Platform-SSO plug-in, MSAL uses it as the broker (silent SSO). If not,
  MSAL falls back to the **system browser** for a one-time interactive consent. No secret either way.

### Token / credential storage — let MSAL own it

Use `Microsoft.Identity.Client.Extensions.Msal` for an OS-native encrypted cache. **Do not hand-roll token
storage.**

| Platform | Where | Protection |
|---|---|---|
| Windows | file under `%LocalAppData%\perch` | **DPAPI**, `CurrentUser` scope (tied to the Windows login) |
| macOS | **Keychain** (login) | ACL'd to the app |

Security posture:
- No secret in the app; the client ID is public and fine to embed.
- Least privilege: delegated **`Calendars.Read`** (read-only) + `offline_access` for silent refresh.
- Never persist raw tokens ourselves; never log meeting contents. **Meeting subject/attendees are PII** —
  they stay on-device, rendered locally, sent nowhere but Graph.
- Mirrors Perch's existing `IClaudeCredentials` precedent (file-on-Windows / Keychain-on-Mac).

---

## Architecture fit

The **service-status footer** is a near-exact template: a timer-driven poll, gated by an `AppSettings`
toggle + interval, wired via `SettingsHooks`, surfaced as a slim overlay footer. Reuse it.

The poll/parse/render is platform-agnostic and lives in Core; only the broker inputs (HWND + broker config)
are OS-specific, so exactly **one new platform seam** is needed.

- **`Perch.Core/Platform/ICalendarAuth.cs`** — `Task<string?> AcquireTokenAsync(string[] scopes, CancellationToken ct)`.
  - `Perch.Platform.Windows/WindowsCalendarAuth.cs` — `BrokerOptions(Windows)` + HWND from the overlay window.
  - `Perch.Platform.Mac/MacCalendarAuth.cs` — Mac broker / system-browser config.
  - Resolved in `Perch.App/PlatformServices.cs` under the existing `#if WINDOWS` split.
- **`Perch.Core/Data/MeetingInfo.cs`** — `internal sealed record` (e.g. `NextTitle`, `StartsAt`, `IsOnline`,
  `HasUpcoming` gate); mirror `StatusInfo`. Keep `internal` (reaches App via `InternalsVisibleTo`).
- **`Perch.Core/Data/MeetingMonitor.cs`** — MSAL + Graph HTTP + parse; **never throws**; keeps `_last` on
  failure so a blip keeps last-known state. Mirror `StatusMonitor` (with a static testable `Parse(...)`).
- **`Perch.App/Services/MeetingMonitorHost.cs`** — `DispatcherTimer` with `Start()`/`Stop()`/`SetInterval()`;
  mirror `StatusMonitorHost`.
- **`Perch.Core/Data/AppSettings.cs`** — `EnableTeamsMeetings` (default **false**, opt-in) +
  `TeamsPollIntervalMinutes` (clamped). No PII/tokens in settings.json.
- **`Perch.App/Windows/SettingsWindow.cs`** — new "Meetings"/"Teams" nav page copying
  `BuildServiceStatusSection` + the interval stepper; a "Sign in" button that kicks
  `ICalendarAuth.AcquireTokenAsync` interactively the first time.
- **`Perch.App/Windows/SettingsWindow.cs` (`SettingsHooks`)** — `TeamsEnabledChanged` / `TeamsIntervalChanged`.
- **`Perch.App/App.axaml.cs`** — `_meetingHost` field + construct + conditional `Start()` + dispose + hook
  wiring, symmetric with `_statusHost`.
- **`Perch.App/Views/OverlayCanvas.cs`** — `_meeting` state + `UpdateMeeting(MeetingInfo)` + a footer/line
  draw mirroring `DrawStatusFooter` (or fold into the same band). Only occupies height when `HasUpcoming`.
- **`tests/Perch.Tests`** — fixture + xUnit over `MeetingMonitor.Parse(...)` (Graph `calendarView` JSON →
  `MeetingInfo`), the way `StatusMonitor` is tested.

---

## The blocker (what unshelves this)

A single **Entra app registration** in the tenant:
- Type: **public client** (no secret).
- Single-tenant is enough for one org; multi-tenant if it should work across orgs.
- Delegated permission: **`Calendars.Read`** (+ `offline_access`).
- Redirect URIs: loopback (`http://localhost`) for the browser fallback, and the WAM broker redirect URI
  MSAL documents for desktop apps. (Confirm exact WAM redirect string against current MSAL docs at build time.)
- Bake the resulting **client ID** into the app (client IDs are not secret).

The author does not currently have rights to create this. Options to revisit later: request the registration
from a tenant admin, or supply a client ID the user can configure themselves (a settings field) so anyone
can point Perch at their own registration.

## Why not zero-auth (recap, for future re-evaluation)

Would become viable again only if the work calendar starts landing in the **Windows system appointment
store** (WinRT path) — e.g. a future Windows/Outlook build that repopulates it, or the user's account being
added with calendar sync under corporate policy. Worth re-probing the WinRT store before committing to Graph
if picking this up on a different machine/config.
