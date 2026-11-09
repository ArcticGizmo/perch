# Remote workers plan

Drive a Perch session that runs on another of your machines from this one, in the normal session window: prompts,
permission and question cards, interrupt, mode/model/effort. The machine that hosts the session is the **worker**; the
one you drive it from is the **controller**. One Perch can be both.

## Why this shape

Perch can only fully control a session it is the *host* of: it owns the `claude` process's stdin. A session on another
machine has its host there. The only other way into it is Remote Control, whose client side is claude.ai's private
protocol, and driving that from a third-party client with a subscription sign-in is a risk to the account. The CLI does
let its host *list* the account's Remote Control sessions and read their transcripts (`list_cloud_sessions`,
`@internal`), but it has no host-side way to prompt or answer one. So the worker Perch, which is the host, relays.

Decisions (2026-11-06):
- **Transport: direct.** The worker listens on a TCP port; the controller connects by host name or IP. Same LAN, or
  anywhere over Tailscale/a VPN. No server, no quota, nothing leaves your network. A Supabase relay may come later as a
  second transport; the protocol doesn't depend on the transport.
- **Scope: attach only.** The controller sees and drives Perch sessions already running on the worker. Starting a new
  session on the worker from here is a later phase.

## How it fits the existing code

Every client of a session already speaks the CLI's stream-json, and Perch already turns that into a conversation with
`StreamJsonParser` + `SessionConversation`. So the relay forwards **raw stream-json lines** one way and a handful of
**commands** the other, and the controller's session is an ordinary `PerchSession` whose backend is the network link
instead of a `claude` process. The session window, the overlay row and Roost need no remote-specific rendering.

```
controller Perch                                         worker Perch
SessionWindow ─ PerchSession ─ RemoteSessionBackend ═TLS═ WorkerServer ─ PerchSession ─ ClaudeSessionController ─ claude
                     ▲ StreamJsonParser ◀── raw lines ═══════════════════════════════ LineReceived ◀── stdout
                     └── commands (prompt, answer, interrupt, mode…) ══════════════▶ PerchSession methods
```

What the Remote Control work already gives us (docs/remote-control-sync-plan.md):
- `--replay-user-messages` echoes every prompt with its `uuid`, whoever sent it, so a prompt typed on either machine
  shows on both. The controller stamps the uuid on prompts it sends, adds its bubble at once, and the echo dedupes.
- `session_state_changed` gives the true turn state on both sides.
- The CLI echoes the host's permission answer back as a `control_response` (`{behavior, updatedInput}`, keyed by the
  request id; seen in the P0 capture). Parsed into a `PermissionAnsweredEvent`, it settles the card on the side that
  didn't answer, with the real verdict (Allowed / Denied), not just "answered elsewhere".

## Security

A paired controller can run anything the session's permission mode allows on the worker. That is the feature, so the
model is: only machines you explicitly paired, over an authenticated, encrypted channel, with nothing reachable before
authentication.

- **Off by default.** "Let other Perch machines control this one" is an opt-in setting. Off = no listener at all.
- **Mutual TLS with pinned keys.** Each Perch has a device identity: a self-signed ECDSA P-256 certificate, made once,
  whose private key stays in the OS key store, non-exportable (CNG on Windows, Keychain on macOS; see W0). The connection is `SslStream` (TLS 1.3, falling back to 1.2) with client
  certificates; each side accepts only the peer key it pinned at pairing (`SubjectPublicKeyInfo` SHA-256). Standard
  TLS, no home-made crypto.
- **Pairing needs the user on the worker once.** The controller connects with its certificate; the worker, if it is in
  a 2-minute pairing window the user opened, shows "*<controller name>* wants to control this machine — code
  **482 913**. Allow?". The controller shows the same code: six digits derived from both certificates' key hashes, so a
  man in the middle (who would present a different certificate) produces a different code. Allow pins both keys.
  Outside a pairing window, an unknown certificate is dropped during the handshake.
- **Nothing parsed before authentication.** The TLS handshake (client certificate required, pinned) completes before
  the server reads a byte of application data.
- **Bounded.** Frames are newline-delimited JSON, at most 8 MiB (a prompt with images) and 1 MiB otherwise; a read
  times out; too many bad frames or a parse failure closes the connection. Images keep the existing 5 MB cap.
- **Least surface.** The worker exposes only its live Perch sessions and the commands the session window already
  sends. No file reads, folder listing, remote launch, rewind, restart, hand-back or Remote Control toggling in v1.
- **Revocable and visible.** Settings lists paired machines with "Remove". The worker's session window and overlay row
  show when a remote controller is attached, and by whom; one click disconnects everyone.
- **Bind address.** Default: all interfaces, so it works on a LAN and over Tailscale. A setting narrows it to one
  address (e.g. the machine's Tailscale `100.x` address). Windows asks once to allow the port through the firewall.
- **Not covered:** a compromised paired machine. Removing it from the list is the remedy.

## Phases

### W0 - Spike: mutual TLS with self-signed certs (done 2026-11-06, Windows)
A loopback console check: two self-signed ECDSA P-256 certificates, `SslStream` with client certificates required and
a pinning callback on each side.
- Works: TLS 1.3, `TLS_AES_256_GCM_SHA384`, both pins checked. A third, unpinned certificate is refused inside the
  handshake (`AuthenticationException` on the server); no application data is read.
- **A PFX loaded with `EphemeralKeySet` fails the handshake on Windows** (SChannel can't use an ephemeral key). With the
  default key set it works, but every load writes a temporary key file to the user's key store that is only removed on
  dispose. So the device key does not live in a PFX in `ISecretStore`. Instead, a platform `IDeviceKeyStore`: on
  Windows a **named, persisted, non-exportable CNG key** (`CngKey.Create` + `ECDsaCng`) with the self-signed
  certificate bound to it via `CopyWithPrivateKey`; on macOS the Keychain (W6). The public certificate is kept in
  settings.
- The pairing code derivation (SHA-256 over both pins, sorted, first 4 bytes mod 10⁶) gives the same six digits on
  both sides.
- Firewall behaviour on a non-loopback bind is still to check, in W2.

### W1 - Session backend seam (no networking yet) - built 2026-11-06
Status: built with tests, not run live. `ISessionBackend` (`Perch.Core/Data/Control/ISessionBackend.cs`), implemented by
`ClaudeSessionController`; `PerchSession` holds one, with `ForBackend(...)` for a remote one and `IsRemote` gating
restart, rewind, hand-back and Remote Control. `LineReceived` on the controller. `SendPrompt(…, uuid)`.
`PermissionAnsweredEvent` + `AskUserQuestionInput.SummariseReply`. A bare echo (no `origin`) reads as an ordinary
prompt; only one with an origin is marked "via Remote Control". Hiding the local-only controls in the session window
moved to W4, where a remote session first exists to show.
- `ISessionBackend` in `Perch.Core/Data/Control`: the controller surface `PerchSession` uses (`SessionId`, `IsRunning`,
  `SendPrompt(text, images, uuid?)`, `RespondToPermission`, `SetPermissionMode`, `SetModel`, `SetEffort`,
  `Interrupt`, `StopTask`, `Stop`, the `EventReceived`/`Exited` events) plus `IsRemote`.
  `ClaudeSessionController` implements it; `PerchSession` holds an `ISessionBackend`.
- `ClaudeSessionController.LineReceived(string)`: each raw stdout line, before parsing (the relay's tap).
- `SendPrompt` takes an optional uuid (a remote controller's), so both sides key the prompt the same.
- `PermissionAnsweredEvent(RequestId, Allowed)` from the echoed `control_response`; `SessionConversation` settles a
  still-pending card with it. Tests from the P0 capture.
- Local-only `PerchSession` features (rewind, fork, restart, hand-back, Remote Control, account switch) report
  unavailable when `IsRemote`, and the session window hides their controls.

### W2 - Identity, pairing, transport - built 2026-11-06
Status: built, not run live. Core: `IDeviceKeyStore` (Windows CNG store in `Perch.Platform.Windows/DeviceKeyStore.cs`,
macOS stub), `Perch.Remote.DeviceTrust` (pins, pairing code, `PairedDevice`), `RemoteLink` (framing, one 8 MiB cap for
every frame, which is simpler than two), `WorkerServer`, `WorkerClient`. App: `Services/WorkerHost`, the Workers page
(`SettingsWindow.Workers.cs`), the pairing prompt (`App.Workers.cs`). `RemoteWorkerTests` pairs and connects over real
TLS on loopback (Windows). A controller that connects is held but nothing is exchanged yet (W3). Manual checks:
`docs/remote-workers-testing.md`.
- `IDeviceKeyStore` (Core interface; Windows CNG implementation, macOS stub) and `DeviceIdentity` (Core): makes/loads
  the certificate; `Fingerprint`; a friendly device name (machine name, editable).
- `PairedDevices` (Core, persisted on `AppSettings`): name, pinned key hash, role (controls me / I control it),
  host:port for workers, added date.
- `WorkerServer` (App/Services): `TcpListener` on the configured address and port (default 41540), `SslStream` server
  with required client certificates, pinning, a pairing window, connection caps.
- `WorkerClient`: connects, pins, reconnects with backoff.
- Pairing SAS: `PairingCode.From(keyHashA, keyHashB)` (Core, pure, tested).
- Settings: a **Workers** page (unique actions, so a page rather than only registry entries): this machine's name and
  fingerprint, the worker toggle + bind address + port (registry descriptors), "Pair a controller" (opens the window),
  paired machines with Remove, "Add a worker" (host:port → pairing).

### W3 - Protocol - built 2026-11-09
Status: built with tests, not run live (nothing on the controller uses it until W4). Core: `SessionJournal`,
`WorkerProtocol` (frames + `WorkerSessionInfo`), `WorkerSessionHub` (worker side, over `IWorkerSession`),
`WorkerConnection` + `RemoteSessionBackend` (controller side). App: `PerchSession.Journal` and its
`*FromController` commands, `Services/WorkerSessionAdapter`, `WorkerHost.Offer` (called from `HoldSession`).
`RemoteSessionTests` drive a worker session from a controller end to end over loopback TCP.

NDJSON frames, each `{"t": type, …}`. Versioned in `hello`.
- Controller → worker: `hello{v, name}`, `list`, `attach{sid}`, `detach{sid}`, `prompt{sid, uuid, text, images?}`,
  `answer{sid, requestId, allow, denyMessage?, updatedInput?}`, `interrupt{sid}`, `mode{sid, mode}`,
  `model{sid, model}`, `effort{sid, level}`, `stopTask{sid, taskId}`.
- Worker → controller: `hello{v, name}`, `sessions{sessions[{sid, sessionId, title, cwd, model, mode, effort, running}]}`
  (sent on `list` and pushed when anything in it changes), `history{sid, transcript[], lines[], truncated, done}`,
  `line{sid, raw}`, `ended{sid, exitCode}`, `error{sid?, msg}`.
- **`sid` is the worker's handle**, fixed for the session's life; `sessionId` is Claude's, which `/clear` changes. The
  controller follows the new id from the stream's `init`.
- **Catch-up.** Each worker `PerchSession` keeps a `SessionJournal`: the transcript lines it loaded on resume, then raw
  stdout lines minus `stream_event` partials (their final records follow; partials are still relayed live). Capped at
  16M characters, oldest dropped (`truncated` → "earlier messages aren't shown"). `attach` snapshots the journal and
  subscribes under one lock, queuing the history frames inside it, so no line is missed, doubled or reordered. History
  goes in chunks of at most 1M characters (JSON escaping can grow that six-fold and still fit the 8 MiB frame);
  `done` marks the last. A resume's transcript that lands after an attach goes as a second `history`.
- **The journal records only while "Let paired machines control this one" is on**, so a machine that never uses this
  holds no extra memory. A session that ran while it was off catches up from the point it was turned on, marked
  truncated.
- **The controller attaches in two steps**: `WorkerConnection.Attach` makes the backend, `RemoteSessionBackend.Start`
  asks for the stream, so the consumer wires its events in between and misses nothing.
- **Stopping a remote session on the controller detaches**; the session keeps running on the worker.
- **What the worker refuses**: switching to `bypassPermissions` (only someone at the worker turns every check off), an
  unknown mode or effort level, a model or task id that isn't a bare token, a prompt image of the wrong type, not
  base64 or over 5 MB, edited tool input on anything but an AskUserQuestion card. Each comes back as `error`, shown as a
  note in the controller's thread.
- **Back-pressure.** Frames go out through one ordered queue per link; past 64 MB waiting the link closes ("the other
  machine isn't keeping up"). A single line too large for a frame is left out of the relay.
- Unknown frame types are ignored (forward compatibility); a malformed frame closes the connection.
- **Known gap:** a rewind on the worker restarts its process, but the journal keeps the dropped branch, so a controller
  that attaches afterwards sees it. Rewind isn't reachable from a controller.

### W4 - Controller UI - built 2026-11-09
Status: built, render-checked (`render` shows an online and an offline remote row, and `roost_remote_1x.png` the two as
Roost panes), never run live.
- **Connections.** `Services/WorkerLinks` (owned by `WorkerHost`) keeps a link to every paired worker while Perch runs;
  pairing is the opt-in, so there's no separate switch. A failed or dropped link retries after 2 s, 5 s, 15 s, 30 s,
  then every minute. Settings → Workers shows each worker as connected (with its session count), connecting, or
  offline with the reason.
- **Rows.** `App.RemoteRows.cs` adds a row per offered session after the not-running rows. Its key is
  `ClaudeSession.RemoteKey(sid)` (`@sid`, which no pid can equal); `RemoteWorker` names the machine and
  `RemoteOffline` fades it. The second line reads "on <worker>" (or "<worker> · <activity>" while it works); status
  follows the worker's `state` (busy → running, waiting → input). A dropped worker keeps its last list, so its rows
  stay, faded and "offline", until it reconnects. Offline rows aren't counted in the header tally. The row menu is only
  Open and Copy session ID: its folder, process and files are on the worker.
- **Opening one.** The click attaches (`WorkerConnection.Attach` → `PerchSession.ForBackend` → `Start`) and shows the
  normal session window. One view per remote session: a second click shows it. Closing the last window on it detaches;
  the next click re-attaches and catches up again.
- **The session window, remote.** Path and title say "on <worker>". Hidden or inert: End session and its Restart menu,
  Resume, hand-back, open folder/terminal, the changed-files panel, the branch pill, the account chip, `@`-mention file
  scan, file-ref clicks, rewind/fork, and `/rewind`, `/fork`, `/rc`, `/login`, `/logout` (each says it works at the
  worker). A lost link ends the view with "lost the connection to <worker>".
- **The Roost.** Remote rows join the roster with every scan and whenever a worker's list changes, so each gets a
  pane (and a rail entry) like a Perch session here: the header's origin reads "◆ Perch · <worker>", replies, cards,
  Enter-to-allow and Esc-to-interrupt go over the link. The pane's feed is the same shared `PerchSession` a window
  uses (`App.RemoteSessionFor`), and counts as a viewer, so the link stays attached while a pane shows it and detaches
  once neither a pane nor a window does. Offline: the pill says "Offline", the composer goes, and a pane that never
  attached reads "Waiting for <worker> to be reachable". When the worker is back, a pane whose link dropped
  (`RoostFeed.IsStale`) attaches afresh. A session the worker ends lingers as an ended pane, as a local one does.
- **Not done:** notifications (done, input) for remote sessions, since the monitor that raises them only sees this
  machine's scan; dropped *files* in a remote prompt send their local path as text (images travel fine).

### W5 - Worker-side visibility
- "Controlled from <name>" chip on the worker's session window and overlay row while a controller is attached.
- Tray/overlay menu: "Disconnect remote controllers".
- A short log of remote commands (prompt sent, permission answered, interrupt) in the session thread as quiet notes,
  so the worker's user sees what was done from elsewhere.

### W6 - Hardening and docs
Reconnect/liveness (heartbeat every 20 s, drop after 60 s), slots for unauthenticated handshakes counted separately
from paired connections (today 8 slow handshakes from anyone can hold every slot for 10 s and lock a paired controller
out; nothing gets in, but it's a nuisance), worker sleep/resume, Perch updates on either side (version
in `hello`; refuse with a clear message on mismatch), `docs/remote-workers-testing.md`, macOS pass.

## Later
- Start a new session on the worker (folder picker over the link).
- A Supabase relay transport for machines that can't reach each other directly.
- Show the account's other Remote Control sessions (any Claude Code) read-only via `list_cloud_sessions`.

## Risks
- `SslStream` client certificates on macOS (keychain behaviour with self-signed PFX imports). W0 checks Windows; macOS
  may need a different key store path.
- Corporate endpoint protection may flag a listening port. It's opt-in and off by default.
- The `control_response` echo and `--replay-user-messages` are undocumented; the conversation falls back to "Expired"
  on `idle` if the echo ever disappears.
