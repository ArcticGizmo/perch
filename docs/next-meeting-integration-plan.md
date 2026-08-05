# Next-meeting indicator — integration plan (UNBLOCKED)

> **Status: unblocked, with a trust caveat (2026-07-30).** The shelving blocker — "we need our own
> Entra app registration" — has a technical work-around: MSAL can authenticate as an **existing
> Microsoft first-party public client** (the client the Graph PowerShell SDK / `az` CLI use), already
> a registered service principal in every tenant, so **no new registration is strictly required**.
> **But** that route is a ToS gray area and can look like an OAuth attack to a locked-down tenant's
> security monitoring — see "Trust / ToS caveat" below; it is a deliberate opt-in fallback, not the
> default. Preferred order: (1) request a sanctioned registration from an admin; (2) the fully-local
> cache reader (most defensible on trust grounds, brittle on technical grounds); (3) first-party
> client reuse, eyes open. All five hard constraints are technically satisfiable by more than one of
> these. The architecture/storage/wiring below stands regardless of which auth path is chosen.
>
> Re-probed on 2026-07-30: WinRT appointment store still returns **1 empty calendar, 0 events**
> (`Microsoft account` / `Calendar`, ±60 days) — new Outlook still does not feed the system store,
> so the zero-auth path remains dead. The new-Outlook local cache
> (`%LocalAppData%\Microsoft\Olk\EBWebView\...\https_outlook.office.com_...leveldb`, ~148 MB) *does*
> hold the calendar, but as Snappy-compressed, V8-structured-clone blobs inside Chromium LevelDB —
> readable only with a full deserializer, brittle across updates, and heavy on PII. Kept as a
> last-resort fallback only.

Goal: show the user's **next scheduled meeting** (e.g. "Standup in 15 min") as a slim line on the
overlay, sourced from their Teams/Outlook calendar, ideally without Perch running its own auth backend.

> **What this actually reads.** It does *not* scrape the new Outlook or new Teams apps. Both surface
> the same underlying Microsoft 365 / Exchange Online calendar, and Teams meetings are just Exchange
> calendar events — so we read that calendar directly via Graph as the signed-in user. This is more
> robust than depending on either client's private on-disk state, and it "supports new Outlook/new
> Teams" by construction (it is account-scoped, not app-scoped).

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

## Unblocking without an app registration — first-party public client

The shelved plan assumed Perch needed its **own** Entra app registration to get a client ID. It does
not. MSAL can run as a **public client using a client ID that Microsoft already registered** — the
multi-tenant first-party apps that ship consented (or user-consentable) with delegated Graph access in
every tenant. This is the exact mechanism the Microsoft Graph PowerShell SDK and Azure CLI use, and it
requires **nothing created in Entra by us or an admin**.

Candidate client IDs (Microsoft-owned, multi-tenant, broker-capable):

| Client | Client ID | Notes |
|---|---|---|
| Microsoft Graph Command Line Tools | `14d82eec-204b-4c2f-b7e8-296a70dab67e` | What Graph PowerShell uses; broadest Graph delegated surface; **preferred default** |
| Microsoft Azure CLI | `04b07795-8ddb-461a-bbee-02f9e1bf7b46` | Very widely pre-consented; good fallback |
| Microsoft Azure PowerShell | `1950a258-227b-4e31-a9cf-717495945fc2` | Second fallback |

- **No secret**, same as before — public client, delegated `Calendars.Read` (+ `offline_access`).
- **WAM works**: these first-party clients carry the broker redirect URIs MSAL needs, so
  `.WithBroker(BrokerOptions(Windows))` does silent SSO against the Entra account already on the device
  (the same account new Outlook/Teams are signed into). No interactive prompt after first consent.
- **Make the client ID a setting** (`CalendarClientId`, defaulting to the Graph CLI client). If one
  client is blocked, the user switches to another — or drops in their own registration's ID later — with
  no code change. This is the "user-configurable client ID" idea the old blocker section already floated,
  promoted to the primary path.

### The one real risk, and how it degrades

The tenant's **user-consent policy**. First use needs consent to `Calendars.Read` for the chosen client:

1. **Already consented (common):** if anyone in the tenant has used Graph PowerShell / `az`, the
   first-party client is often already granted — silent, zero prompts.
2. **User consent allowed:** a one-time consent dialog on first sign-in, then silent forever.
3. **User consent blocked tenant-wide:** the sign-in is refused for *any* client the user doesn't own —
   this is the **same wall** that blocked the original plan, so a self-owned registration wouldn't have
   helped either. Degradation path: try an alternate first-party client ID → if all blocked, fall back to
   the local-cache reader (below), which needs no tenant cooperation at all.

### Constraint check

| Constraint | How it's met |
|---|---|
| No Entra app registration | Reuse Microsoft's existing first-party service principal; nothing created |
| No publicly-exposed protocol (no ICS) | Outbound OAuth2 + HTTPS to `login.microsoftonline.com`/`graph.microsoft.com` only; no inbound listener, no shareable URL. WAM avoids even the loopback redirect |
| Refresh no worse than every 15 min | Timer polls `/me/calendarView` on an interval (default 5 min, clamped) |
| Windows first | WAM broker; silent SSO from the AAD account already on the box |
| New Outlook + new Teams only | Account-scoped, not app-scoped — reads the shared M365 calendar both clients project |

### Trust / ToS caveat (important — do not treat first-party reuse as "clean")

Reusing a Microsoft first-party client ID is **not a clear ToS breach** (no explicit clause found in the
MSA, Graph API Terms, or product terms names or forbids it) but it is a **genuine gray area**, and it is
*not* the honest/sanctioned option this doc first implied. Three concrete concerns:

1. **Misrepresentation.** Sign-in and app-consent logs attribute the traffic to the borrowed Microsoft app
   (e.g. "Microsoft Graph Command Line Tools"), not to Perch. That runs against the spirit of the MSA's
   prohibition on misleading activity, even absent a named clause.
2. **Looks like an attack.** First-party client-ID reuse (and FOCI refresh-token behaviour) is a documented
   *offensive* technique; Elastic / Microsoft Defender ship detection rules for "OAuth phishing via
   first-party Microsoft application." A non-Microsoft process authenticating as the Graph CLI client can
   trip a SOC.
3. **Worst-case tenant.** A tenant locked down enough to forbid app registration almost certainly runs the
   monitoring that (2) describes — this is exactly where impersonating a Microsoft client looks worst.

**Consequence for the ranking:** first-party reuse is a working fallback, not the default. Prefer, in order:

1. **A sanctioned app registration** — the user cannot *create* one, but *requesting* one from a tenant
   admin is a different ask and yields the clean, honest, robust path. Try this first.
2. **Local-cache reader (below)** — filed as "break-glass" on *technical* grounds, but it is actually the
   **most defensible on trust grounds**: no authentication, no impersonation, no unusual traffic to
   Microsoft — just reading files new Outlook already wrote into the user's own profile. Its only cost is
   brittleness.
3. **First-party client-ID reuse** — only with the user's eyes open to the caveats above, and ideally with
   their security team informed. Keep it behind the configurable `CalendarClientId` so it is a deliberate
   opt-in, never a silent default that ships enabled.

### Fallback (only if consent is blocked tenant-wide): local-cache reader

Read the new Outlook IndexedDB LevelDB
(`%LocalAppData%\Microsoft\Olk\EBWebView\Default\IndexedDB\https_outlook.office.com_0.indexeddb.leveldb`).
Needs no auth and no tenant cooperation, so it satisfies every constraint mechanically — but it means
implementing LevelDB block reads (Snappy) + Chromium IndexedDB key decoding + V8 structured-clone value
deserialization, snapshotting files the app holds open (`FileShare.ReadWrite` copy), and re-validating on
every Outlook update. High effort, brittle, all-PII-on-disk. Treat as break-glass only.

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

## The old blocker (resolved)

Originally shelved because it assumed a self-owned **Entra app registration** was required. It isn't —
see "Unblocking without an app registration" above: reuse a Microsoft first-party public client ID (no
secret, no registration). A self-owned registration is now only an *optional* upgrade, not a prerequisite:

- If the user later gets their own registration (public client, delegated `Calendars.Read` +
  `offline_access`, loopback + WAM redirect URIs), just point `CalendarClientId` at it — no code change.
- Client IDs are not secret; whichever ID is used can be baked into the default and overridden in settings.

## Why not zero-auth (recap, for future re-evaluation)

Would become viable again only if the work calendar starts landing in the **Windows system appointment
store** (WinRT path) — e.g. a future Windows/Outlook build that repopulates it, or the user's account being
added with calendar sync under corporate policy. Worth re-probing the WinRT store before committing to Graph
if picking this up on a different machine/config.
