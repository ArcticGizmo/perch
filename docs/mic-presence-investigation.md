# Investigation: microphone-in-use detection, "jump back to the call", and remote mute

> ## Status: two of three shipped; **mute and meeting state were removed**
>
> **What Perch does today:** the mic strip names whichever app holds the microphone, and clicking that name
> focuses it. That's all. Detection (§1) and cross-desktop activation (§2) are solid and stay.
>
> **What was removed, and why.** The Teams local-API layer (§3a) — in-app mute, real meeting state, pairing,
> the Connect/Retry pill, the four-state link model — was built, shipped, and then deleted, because it could
> not be made reliably *correct*:
>
> - **You cannot ask Teams for the current state.** `query-meeting-state` is a 1.0.0 service, absent from the
>   2.0.0 protocol that pairing needs. Teams volunteers state only when it *changes*, and its opening frame on
>   a connection can carry permissions alone. So a client that starts mid-call may not know your mute for the
>   rest of that call.
> - **Every honest response to that is a worse UI than none.** Claiming "unmuted" was wrong. Hiding the
>   indicator until Teams speaks left a strip that said nothing, on a feature whose whole promise was to tell
>   you something. Neither is worth the pairing prompt, the token, the retry states and the settings copy that
>   paid for it.
> - **The device mute isn't a substitute.** The app in the call doesn't know about it, so muting there creates
>   exactly the "you're on mute" trap the feature was meant to prevent. Removed as well.
>
> The protocol findings below are kept verbatim: they cost real effort, and if Microsoft ever adds a state
> query this becomes viable again. Everything in "What shipped" that mentions a call link is now history.
> `AppSettings` no longer carries `TeamsCallControls`/`TeamsApiToken`; a settings file still holding them is
> ignored and rewritten without them.

**Goal (as originally framed).** Perch should be able to tell that the microphone is live and *which app* has
it, offer a one-click "jump back to the meeting" that survives working across multiple virtual desktops,
and — if possible — mute/unmute without leaving the current desktop.

**Short answer at the time.** All three looked achievable on Windows. Detection is two independent, fully
public mechanisms; cross-desktop activation works via a documented shell API; and *real* Teams-level
mute/unmute is possible through Teams' own local WebSocket API. The first two held up. The third worked in the
narrow sense that commands were honoured — and failed in the sense that matters, which is knowing what to
show before the user touches anything.

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
  `tokenRefresh` message with a token to persist and reuse. (The prompt is triggered by the first *command*
  during a *call*, not by connecting — see "Pairing, corrected" below.)

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
3. ~~**`{"response":"Success"}` is the authorisation signal**, and it arrives immediately.~~ **Wrong — see
   "Pairing, corrected" below.** The `requestId:0` `Success` is a *connection* ack; it says nothing about
   whether commands will be honoured. Reading it as a grant is what makes the unpaired window invisible.
4. ~~**No `tokenRefresh` arrived, and no approval prompt appeared**~~ — true, but only because the capture was
   taken *outside a call and without sending a command*. Both are preconditions for the prompt. Token
   persistence is not a nice-to-have: the token is the only durable proof of pairing.

### Pairing, corrected (July 2026)

Prompted by the observed behaviour: with the integration on and the strip showing an apparently live mute
button, the **first** mute click pops a Teams "allow third-party connection" prompt and *does not mute*.

The read feed and the command channel have **separate** permissions, and only the latter needs pairing:

- **The state feed is free.** Teams pushes `meetingUpdate` frames to an unpaired client (that is what the
  idle capture above is). So a client can look connected, and be, for reads.
- **Commands require pairing, and pairing requires a call.** `meetingPermissions.canPair` is the flag:
  true means *in a meeting and not yet paired*, i.e. pairing can be triggered now. Both captured frames
  above have `canPair:false` — the idle one because there is no call, the in-call one because that session
  was already paired. Pairing simply cannot be completed outside a call, which is why the settings page can
  never be the place it happens.
- **Any command triggers the prompt; `pair` is the side-effect-free one.**
  `{"action":"pair","parameters":{},"requestId":N}` exists precisely to negotiate without toggling anything.
  A command sent while unpaired pops the prompt and comes back
  `{"requestId":N,"response":"Pairing response resulted in no action"}` — a second, independent "you are not
  paired" signal, and the exact shape of the silent-mute-failure above.
- **`{"tokenRefresh":"<token>"}` is the grant**, and the only one. Persist it (`AppSettings.TeamsApiToken`);
  reconnecting with it in the query string skips the prompt for good.
- **The refusal also answers the `pair` request itself** — pairing performs no action, which is exactly why
  `pair` is the safe way to ask. So the refusal only means "not paired" when its `requestId` belongs to
  *something else*; reading it unconditionally tore the link down moments after it was granted, with the strip
  flipping back to "not synced" the instant the user approved Perch
  (`TeamsCallController.RejectionMeansUnpaired`, pinned by tests).
- **There is no way to ask Teams for the current state on this protocol.** `query-meeting-state` is a 1.0.0
  service and is absent from 2.0.0 — confirmed by [Raycast's 2.0.0 client](https://github.com/raycast/extensions/blob/main/extensions/microsoft-teams-calling/src/teams/meetingClient.ts),
  which resorts to *"the last state we received, or undefined if none has arrived yet"*. Perch sent it in both
  envelopes for a while; it did nothing, and the code is gone.
  <br>Which makes **"muted" a genuinely unknown quantity**, not a startup blip: Teams volunteers state on change,
  its opening frame on a connection can carry `meetingPermissions` alone, and nothing can be asked. Connect to a
  call already in progress and the mute may stay unknown for the whole call. Hence `CallSnapshot.IsMuted` is
  `bool?` and stays null until Teams says — flattening it to false is what made restarting Perch mid-call
  confidently report an unmuted mic until the user toggled it. The strip drops the status glyph and marks the mute
  button with a "?"; the button stays live, because toggling is both legitimate and the only thing that makes
  Teams answer.
- **Everything received before the grant was said to an unauthorised client**, so a mid-session grant still
  closes the socket and reconnects carrying the new token — the closest thing to a bootstrap (Teams re-sends its
  opening frame), and it makes the paired session identical to the one every later launch gets.
  <br>Guard the reconnect on the session having started *unpaired*. Teams may rotate the token mid-session, and
  reconnecting on every `tokenRefresh` would loop forever.
- **The prompt renders inside the Teams window.** With Teams minimised or on another virtual desktop the
  user never sees what they are waiting for — so whatever triggers pairing should also raise Teams
  (`IWindowActivator`, the same pid the strip's jump-to-app uses).

Which gives the state machine — four states, each answering "can the strip say anything true about the mic?":

| `CallLinkState` | Means | Strip |
|---|---|---|
| `Unknown` | Integration off, Teams absent, API unreachable, connecting, or on the socket unpaired. The default, and where every disconnect lands. | No indicators. Connect pill when `canPair`. |
| `Connecting` | A `pair` request is in flight; Teams' prompt is on screen. | No indicators, flat "Connecting…" pill. |
| `Blocked` | Refused: the prompt went unanswered, or a command came back rejected. | No indicators, "Retry" pill. |
| `Connected` | `tokenRefresh` arrived, or the connection carried a stored token. | Real meeting state, in-app mute. |

Deliberately about *knowledge*, not plumbing: "the socket is open but unpaired" is not worth a state of its own,
because an unpaired connection knows nothing it can be trusted on and is `Unknown` like any other ignorance.
Nothing learned from a connection outlives it.

`canPair:true` has not been captured on this machine (Perch paired before the flag was being read), so that
half rests on two independent third-party clients — [teams-monitor](https://github.com/svrooij/teams-monitor)
documents the semantics, [StreamDeckMSTeams_Udo](https://github.com/kosmonautica/StreamDeckMSTeams_Udo) pairs
off it — plus its consistency with both frames above. Clear `TeamsApiToken` to re-observe it.
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
| `MicApps` — identity → name, product recognition, ledger↔process matching, and `CallLinkApplies` / `CallLinkPairable` (the two gates on product-specific behaviour: drive it, or offer to pair) | `src/Perch.Core/Data/MicApps.cs` |
| `TeamsCallController` — the local WebSocket client (pairing, state feed, toggle-mute/leave) | `src/Perch.Core/Data/TeamsCallController.cs` |
| Windows detection: ConsentStore ∪ WASAPI, endpoint mute | `src/Perch.Platform.Windows/MicrophoneMonitor.cs` + `CoreAudio.cs` |
| macOS stub (reports "can't tell you", so the strip stays hidden; the Teams link still works there) | `src/Perch.Platform.Mac/MicrophoneMonitor.cs` |
| `FocusAppWindowForProcess` — jump to the app owning a capture pid, across desktops | `IWindowActivator` + both platform impls |
| Overlay strip (label, jump, mute, Connect pill, context-menu connect/unpair, tooltip with the setup hints) | `src/Perch.App/Views/OverlayCanvas.Mic.cs` |
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
- **Perch never asks Teams for control on its own.** Connecting is silent; the only thing that makes Teams
  prompt is the user pressing Connect, which sends `action:"pair"` — the one action with no side effect if
  they decline. Unpaired therefore has its own state (`PairingRequired`), a visible affordance, and an honest
  label, instead of an in-app mute button that pops a surprise prompt and mutes nobody.
- **Nothing is shown about a mute Perch can't read.** In every state but `Connected` the strip drops *both*
  indicators — the glyph on the left and the mute button on the right — and the label names the link state
  instead ("not linked", "connecting…", "link blocked"). The capture device's mute is still measurable, but it
  isn't the question: Teams can have you muted while the device is wide open, so drawing either indicator would
  assert something Perch cannot see (`MicApps.CallStateUnknown`, the second gate beside `CallLinkApplies`; a test
  pins that the two never contradict each other).
  <br>That same predicate puts the Connect pill in the space the indicators vacated — one fact, two consequences,
  since being unable to read the app is exactly when there is something to offer.
- **Never gate the offer on `canPair`.** It reads like the right precondition — it is Teams' own "you may ask" —
  but Teams only sends it when it chooses to, and *not* on a socket opened mid-call. Which is precisely what an
  unpair leaves behind, so gating on it meant unpairing during a call offered no way back into the call you were
  already in. Ask regardless: if Teams won't pair it just doesn't answer, `ApprovalWindow` turns the silence into
  `Blocked`, and the user gets a Retry. A dead end that explains itself beats a missing button.
  <br>`canPair` keeps one job — on a head with no microphone detection it is the only evidence a call exists, so
  it still earns the strip its place on screen.
  <br>The integration being *switched on* is the other half of that test, and it is a setting rather than a link
  state — so it's pushed to the overlay separately (`OverlayCanvas.SetCallControlsEnabled`). With it off, Perch
  never claimed to know Teams' mute, so a Teams call is treated like any app it has no integration for (Slack,
  Zoom, a browser, OBS) and keeps both indicators, reporting only what it measured about the device.
- **An unpair lands in `Unknown` because that is where every disconnect lands** — one rule, enforced in one
  place (`TeamsCallController.Forget`), rather than a special case. `Stop()` clears the snapshot as well as the
  state, so the last mute Perch was allowed to read never lingers on screen. The restart behind an unpair pushes
  once at the end rather than repainting the strip through each step of the teardown
  (`MicMonitorHost.RestartCallControls`).
- **The strip's right-click menu carries the same two actions the strip itself can be in** — Connect while
  unpaired, Unpair once paired — hung off the strip's region the way the system-metrics and usage strips hang
  theirs. It can only appear while the strip is visible (i.e. while something holds the mic), so the settings
  page keeps the unconditional version.
- **`canPair` is trusted over our own token.** A stored token that Teams no longer honours is otherwise
  invisible until a mute silently fails, so a `canPair:true` frame (or a `"…Pairing…"` response to a command)
  demotes a `Connected` link back to `PairingRequired` and the Connect button returns.

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
- **Unverified in the pairing path:** whether Teams streams full `meetingState` (not just `meetingPermissions`)
  to an *unpaired* client mid-call, and whether it answers a `pair` action with a prompt every time or only
  once. Both only affect wording — the strip's Connect pill needs nothing but `canPair` — but the second
  decides whether `ApprovalWindow`'s fallback to `PairingRequired` ever gets used. Clear `TeamsApiToken`
  ("Forget Teams pairing" in settings) and join a call to observe.
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
