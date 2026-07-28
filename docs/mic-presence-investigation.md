# Investigation: microphone-in-use detection, "jump back to the call", and remote mute

**Status:** findings, now implemented — see [What shipped](#what-shipped) at the end for the code. Verified
on this machine (Windows 11 Pro 26200, new Teams `MSTeams 26163.405.4842.717`) with a throwaway probe (see
[Probe](#the-probe)).

**Goal.** Perch should be able to tell that the microphone is live and *which app* has it, offer a
one-click "jump back to the meeting" that survives working across multiple virtual desktops, and — if
possible — mute/unmute without leaving the current desktop.

**Short answer.** All three are achievable on Windows. Detection is two independent, fully public
mechanisms; cross-desktop activation works via a documented shell API; and *real* Teams-level
mute/unmute is possible through Teams' own local WebSocket API, which as a bonus gives a far better
presence signal than sniffing the mic at all.

---

## 1. Detecting that the mic is in use, and by whom

### 1a. CapabilityAccessManager ConsentStore (registry) — recommended for "which app"

This is the ledger behind the Windows 11 privacy indicator (the mic icon in the tray).

```
HKCU\SOFTWARE\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore\microphone\
    <PackageFamilyName>\                       # packaged apps, e.g. MSTeams_8wekyb3d8bbwe
    NonPackaged\<exe path with \ replaced by #> # e.g. C:#Program Files#Google#Chrome#Application#chrome.exe
```

Per-app values (`REG_QWORD`, FILETIME):

| Value | Meaning |
| --- | --- |
| `LastUsedTimeStart` | when this app last opened the mic |
| `LastUsedTimeStop` | when it last released it — **`0` while it is in use right now** |
| `Value` | `Allow` / `Deny` (the privacy setting, not usage) |

The same tree has a `webcam` sibling, and an `HKLM` counterpart for service-hosted callers (empty on this
machine).

Verified read from this machine (a real Teams meeting earlier today, 15:01→15:34 local):

```
idle  [HKCU] MSTeams_8wekyb3d8bbwe                       start=2026-07-28 15:01:05Z
idle  [HKCU] 91750D7E.Slack_8she8kybcnzg4                start=2026-06-22 14:37:38Z
idle  [HKCU] NonPackaged\C:#...#chrome.exe               start=2026-06-19 14:02:52Z
idle  [HKCU] NonPackaged\C:#programs#obs-studio#...      start=2026-07-17 15:53:44Z
```

**Why it's the best identity signal:** the key name *is* the app identity — a package family name for
Teams/Slack, a full exe path otherwise. No PID→process guessing, and it covers Teams, Slack, Zoom,
Chrome/Meet, OBS uniformly.

**Caveats.** Reads are cheap but this is undocumented-ish (widely relied on by privacy tools, not a
contract). `RegNotifyChangeKeyValue` gives change notifications, so it needn't be polled hard. **Not yet
observed live** — every app was idle during the spike; confirm `LastUsedTimeStop == 0` flips during a real
meeting with the probe's `--watch` mode before building on it.

### 1b. WASAPI capture sessions (`IAudioSessionManager2`) — recommended for "is it live *now*"

Enumerate `eCapture` endpoints, then their audio sessions. Each session yields
`GetState()` (`Inactive`/`Active`/`Expired`) and `GetProcessId()`, and
`IAudioSessionNotification` / `IAudioSessionEvents` make it event-driven rather than polled.

Verified output (no meeting live, so everything is `Inactive`):

```
-- device: Microphone (2- Logitech Webcam C930e)
   endpoint mute = False
     [Inactive] pid=1116   explorer
     [Inactive] pid=19640  ms-teams
-- device: Microphone Array on SoundWire Device (10- SoundWire Audio)
```

**Gotcha found:** the capture session belongs to **pid 19640**, a *child* `ms-teams.exe` (the media
engine — it owns `SkyLibWindowsReactor` / `RtcPalVideoPnPMonitorHWND` windows and has no visible UI),
while the meeting/hub windows belong to **pid 5028**. Any PID→window mapping must walk the process tree,
exactly as `IWindowActivator.FocusTerminalForProcess` already does for terminals. Note also that a
session lingers in the list as `Inactive` long after the call ends — presence must key off `Active`, not
mere existence.

`IAudioMeterInformation` on the session additionally gives a peak level, which could drive a live
"you're talking" meter. It is **not** a reliable mute indicator: Teams mutes in software upstream of the
device, so the stream can stay hot while you are muted.

### 1c. Teams local API (`isInMeeting`) — the best signal of all, Teams-only

See §3. If the API is enabled, Teams tells you `isInMeeting` / `isMuted` directly and you don't have to
infer presence from the mic at all. Best used as the primary source with 1a/1b as the generic fallback
for Slack/Zoom/Meet.

---

## 2. Jumping back to the meeting across virtual desktops

Confirmed working via the documented shell API `IVirtualDesktopManager`
(CLSID `aa509086-…`, IID `a5cd92ff-…`):

```
hwnd=0x20466 pid=5028 onCurrentDesktop=False
             desktop=5ebd21ea-f7c9-4cbe-9840-0c8a1f313e48 title='Calendar | Microsoft Teams'
```

That is precisely the target scenario — Perch can see that the Teams window lives on another desktop.

- **Finding the window:** meeting windows are top-level, class **`TeamsWebView`**, in the Teams UI process.
  The hub is titled `… | Microsoft Teams` (e.g. `Calendar | Microsoft Teams`); a meeting opens its own
  `TeamsWebView` window titled with the meeting subject. Filter on class + "not the hub" rather than on
  the process's `MainWindowTitle`.
- **Activating it:** `SetForegroundWindow` on a window that lives on another desktop makes Windows switch
  to that desktop — so plain activation is the "jump back". Perch's existing `IWindowActivator` is the
  right home; it needs a new method rather than new interop from UI code.
- **What is *not* possible:** `IVirtualDesktopManager::MoveWindowToDesktop` only works on windows owned by
  the calling process, so Perch cannot pull the Teams meeting onto the *current* desktop with the public
  API. Switching desktops is the supported behaviour. (The undocumented `IVirtualDesktopManagerInternal`
  can, but its interface GUIDs change with Windows builds — not worth the breakage.)

---

## 3. Mute / unmute from Perch

Ranked best → worst.

### 3a. Teams third-party app API (WebSocket) — the real answer for Teams

New Teams (not classic) runs a local WebSocket server. This is what the Stream Deck Teams plugins use.

```
ws://127.0.0.1:8124/?protocol-version=2.0.0&manufacturer=Perch&device=PC&app=Perch&app-version=1.0&token=<token>
```

- **Command:** `{"action":"toggle-mute","parameters":{},"requestId":1}`. Also `toggle-video`,
  `toggle-hand`, `toggle-background-blur`, `leave-call`, `stop-sharing`, and reactions.
- **State feed:** Teams pushes `isMuted`, `isCameraOn`, `isInMeeting`, `isHandRaised`, `isRecordingOn`,
  `isBackgroundBlurred`, `isSharing`, `hasUnreadMessages`, plus `canToggleMute`-style capability flags.
  This makes it a presence source *and* a control channel.
- **Pairing:** connect with an empty `token`; Teams shows an in-app authorisation prompt, then sends a
  `tokenRefresh` message with a token to persist and reuse.

### Observed protocol (captured against Teams 26163.405.4842.717)

Worth recording, because two of these contradict the third-party write-ups above and the second one was a
real bug in the first implementation.

On connecting (API enabled, no token, not in a call), Teams sent:

```json
{"requestId":0,"response":"Success"}
{"meetingUpdate":{"meetingPermissions":{"canReact":false,"canToggleVideo":false,"canToggleMute":false,
 "canToggleHand":false,"canToggleShareTray":false,"canLeave":false,"canToggleBlur":false,
 "canToggleChat":false,"canStopSharing":false,"canPair":false}}}
```

1. **`meetingUpdate` frames are partial.** That frame has no `meetingState` *at all*. Rebuilding the
   snapshot from each frame therefore resets every field the frame omits — which is exactly why a mute made
   inside Teams went unnoticed: the next permissions-only frame reset `IsMuted` to false. State must be
   *merged*, keeping any field the frame doesn't mention (`TeamsCallController.Merge`, pinned by tests using
   these captured frames).
2. **Since fields are carried forward, `isInMeeting` needs a way back down.** The end-of-call frame is the
   idle one above — permissions all false, no state — so the permissions double as the in-call signal when
   `meetingState` is silent: Teams only grants `canLeave`/`canToggleMute` while there's a call.
3. **`{"response":"Success"}` is the authorisation signal**, and it arrives immediately. Waiting for meeting
   state instead leaves a client stuck "awaiting approval" for as long as the user stays out of a call.
4. **No `tokenRefresh` arrived, and no approval prompt appeared** — the connection simply succeeded without a
   token. So token persistence is a nice-to-have, not a precondition.
5. **Field names** (confirmed against Teams' own client model): `isMuted`, `isInMeeting`, `isVideoOn` (*not*
   `isCameraOn`), `isHandRaised`, `isRecordingOn`, `isBackgroundBlurred`, `isSharing`, `hasUnreadMessages`.

**Reachability is detectable:** the port is only open when the third-party app API is enabled, so a bare TCP
connect to 8124 distinguishes "Teams not running" / "API off" / "ready" — without a WebSocket handshake, so
it neither consumes Teams' single client slot nor triggers the approval prompt
(`TeamsCallController.ProbeAsync`, surfaced on the settings page with a re-check button). There is **no
documented deep link** to Teams' privacy settings page, so the setting has to be described rather than
linked.
- **Requires the user to opt in once:** Teams → Settings → Privacy → *Third-party app API* → *Manage API*
  → *Enable API*. **Currently off on this machine** — nothing is listening on 8124, which doubles as a
  clean capability probe (fail to connect ⇒ show a "enable this in Teams" hint).
- **Constraints:** localhost only; **Teams accepts one WebSocket client at a time**, so Perch would fight
  a Stream Deck plugin or similar for the connection. Unofficial/undocumented by Microsoft — treat as
  best-effort and degrade quietly.

Crucially this mutes *inside Teams*, so the Teams UI and other participants agree with Perch — it avoids
the "muted at the OS level while Teams thinks you're live" trap below. It works regardless of focus, so
it also works from another virtual desktop.

### 3b. Endpoint mute (`IAudioEndpointVolume::SetMute` on the capture device) — universal kill-switch

Works for any app, no opt-in, trivially reversible, and the probe already reads the current state. But
Teams doesn't know: it keeps showing you as unmuted while nobody can hear you — the classic "you're on
mute" failure. Good as an explicit, clearly-labelled **hard mute** (with Perch showing the muted state
prominently), bad as the default mute button.

### 3c. Synthetic `Win`+`Alt`+`K` — the OS call-mute shortcut

Windows 11's taskbar call-mute; Teams supports it, and it keeps the app's own mute state in sync.
Synthesising it with `SendInput` is plausible but **unverified here**, depends on the shell feature and
per-app support, and can misfire into whatever app has focus. Fallback only.

### 3d. Per-session mute (`ISimpleAudioVolume` on the capture session) — don't

Per-session mute is honoured inconsistently for capture endpoints. Not worth pursuing.

---

## Suggested shape in Perch

Fits the existing conventions — every OS capability behind a `Perch.Core` interface, Windows impl in
`Perch.Platform.Windows`, resolved in `PlatformServices`. `IMediaController` is the closest precedent
(event-driven, immutable snapshot, best-effort, no-op on other heads) and the overlay's now-playing strip
is the UI precedent.

1. **`ICapturePresence`** (`Perch.Core/Platform/`) — `CaptureSnapshot? Current`, `event Action? Changed`,
   `Start()`/`Stop()`. Snapshot: app display name, package/exe identity, whether capture is `Active`,
   the owning pid, since-when. Windows impl = ConsentStore change notifications (identity) joined with
   WASAPI session events (liveness), off the UI thread per the `*MonitorHost` idiom. Mac head no-ops.
2. **`IWindowActivator` addition** — `bool FocusWindowForApp(...)` (or a capture-presence-aware overload)
   that walks the process tree from the capture pid, picks the `TeamsWebView` meeting window, and
   activates it; the desktop switch comes free. Keep the `IVirtualDesktopManager` interop inside the
   Windows impl.
3. **`ICallController`** — `bool IsAvailable`, `CallSnapshot? Current` (`IsMuted`, `IsInMeeting`, …),
   `ToggleMute()`, `LeaveCall()`. Windows impl = the Teams WebSocket client with the persisted token
   (sidecar under `~/.claude/` or `AppSettings`), falling back to endpoint mute when the API is disabled
   — with the UI making very clear which of the two is in play.
4. **UI** — an overlay strip mirroring the now-playing strip: "🎤 Microsoft Teams — in a meeting" with
   *Jump to* and *Mute* buttons; a tray-menu item; and a `IGlobalHotkey` binding for mute so it works
   without finding the Perch window first.

Worth doing in that order — step 1 alone delivers the "what has my mic" answer, step 2 the jump-back, and
step 3 is the only part that depends on a user opt-in inside Teams.

## What shipped

Built on the `ms-teams` branch, following the suggested shape above. Generic detection is the whole feature;
the Teams integration is strictly additive and behind its own opt-in.

| Piece | Where |
| --- | --- |
| `IMicrophoneMonitor`, `MicUser`, `MicSnapshot` — app-agnostic detection + device mute | `src/Perch.Core/Platform/IMicrophoneMonitor.cs` |
| `ICallController`, `CallSnapshot`, `CallLinkState`, `NullCallController` — the optional per-product layer | `src/Perch.Core/Platform/ICallController.cs` |
| `MicApps` — identity → name, product recognition, ledger↔process matching, and `CallLinkApplies` (the single gate on product-specific behaviour) | `src/Perch.Core/Data/MicApps.cs` |
| `TeamsCallController` — the local WebSocket client (pairing, state feed, toggle-mute/leave) | `src/Perch.Core/Data/TeamsCallController.cs` |
| Windows detection: ConsentStore ∪ WASAPI, endpoint mute | `src/Perch.Platform.Windows/MicrophoneMonitor.cs` + `CoreAudio.cs` |
| macOS stub (reports "can't tell you", so the strip stays hidden; the Teams link still works there) | `src/Perch.Platform.Mac/MicrophoneMonitor.cs` |
| `FocusAppWindowForProcess` — jump to the app owning a capture pid, across desktops | `IWindowActivator` + both platform impls |
| Overlay strip (label, jump, mute, tooltip with the setup hints) | `src/Perch.App/Views/OverlayCanvas.Mic.cs` |
| Host bridging both halves to the UI thread, and choosing which mute a click means | `src/Perch.App/Services/MicMonitorHost.cs` |
| Settings page + `ShowMicPresence` / `TeamsCallControls` / `TeamsApiToken` | `SettingsWindow.BuildMicrophonePage`, `AppSettings` |
| Tests for the pure logic | `tests/Perch.Tests/MicAppsTests.cs` |

Notable decisions:

- **The mic monitor polls every two seconds** rather than wiring WASAPI session notifications and
  `RegNotifyChangeKeyValue`. Two seconds is imperceptible for "am I in a call", and it publishes only on a
  real change (hence the hand-written value equality on `MicSnapshot`, which the compiler's record equality
  would get wrong for its `Users` list).
- **`TeamsCallController` lives in `Perch.Core`, not a platform project** — it's loopback sockets and JSON,
  and Teams exposes the same API on macOS, so one implementation serves every head.
- **Detection is a union of the two sources**, so a gap in either still yields a sighting; `IsStreaming`
  keeps the "holding the device open" vs "audio flowing" distinction visible instead of collapsing it.
- **The mute button is disabled, not silently ineffective**, when Teams refuses the toggle (an organiser
  hard-mute). Falling back to the device mute there would be worse than useless — it can't unmute you in
  the meeting.
- **The app's name is the jump control**, not a separate button — clicking the name is the obvious gesture,
  so the strip carries one button (mute) instead of two. The name brightens on hover and only becomes
  clickable when a pid is actually attributable to jump to.
- **`meetingUpdate` frames are merged, not replayed wholesale.** See
  [Observed protocol](#observed-protocol-captured-against-teams-261634054842717) — this is the difference
  between tracking a Teams-side mute and never seeing it.

## Open questions

- **Still unconfirmed live:** a snapshot with an actual *holder*. The device/mute half of
  `MicrophoneMonitor` was smoke-tested on this machine (it named the real C930e endpoint, and the mute
  toggled and restored), and the underlying flags were read straight from the registry — but nothing was
  using the mic during the work, so `LastUsedTimeStop == 0` and WASAPI `Active` have not been *observed*
  flipping. Join a call and run the probe's `--watch` mode, or just switch the strip on.
- Confirm the meeting window's class/title once a meeting is actually running (only the hub window existed
  during the spike), and whether Teams' "open meetings in main window" setting changes what
  `FocusAppWindowForProcess` lands on.
- Whether the Teams API's single-client limit collides with anything already in use here.
- Privacy: presence stays local and nothing is persisted today beyond the pairing token. If a mic-history
  view is ever added, note that a log of "mic was live 15:01–15:34" is more sensitive than Perch's existing
  session stats.

## The probe

Throwaway spike, kept out of the repo, at:

```
%LOCALAPPDATA%\Temp\claude\C--Users-JonHowell-Documents-git-personal-perch\<session>\scratchpad\micprobe\
    dotnet run             # one-shot dump of all three probes
    dotnet run -- --watch  # poll every 1s, print only on change
```

Contains hand-written COM interop for `IMMDeviceEnumerator`, `IAudioSessionManager2`,
`IAudioSessionControl2`, `IAudioEndpointVolume` and `IVirtualDesktopManager` that can be lifted into
`Perch.Platform.Windows` largely as-is.

## References

- [Teams.ThirdPartyAppApi (C# client for the local Teams API)](https://github.com/ferenyl/Teams.ThirdPartyAppApi)
- [teams-local-mute (raw protocol + token pairing)](https://github.com/Russell-KV4S/teams-local-mute)
- [StreamDeckMSTeams_Udo (works while Teams is unfocused)](https://github.com/kosmonautica/StreamDeckMSTeams_Udo)
- [teams-monitor (state feed fields)](https://github.com/svrooij/teams-monitor)
- [Third Party App API — enabling it (Microsoft Q&A)](https://learn.microsoft.com/en-us/answers/questions/df024bbc-b51c-4672-8771-2ec372d3c30d/third-party-app-api-disabled?forum=msteams-all)
- [Windows 11 mic-mute shortcut background](https://www.windowslatest.com/2021/11/29/windows-11-is-getting-a-new-keyboard-shortcut-to-mute-or-unmute-mic/)
