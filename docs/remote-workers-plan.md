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

### W2 - Identity, pairing, transport
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

### W3 - Protocol
NDJSON frames, each `{"t": type, …}`. Versioned in `hello`.
- Controller → worker: `hello{v, name}`, `list`, `attach{sid}`, `detach{sid}`, `prompt{sid, uuid, text, images?}`,
  `answer{sid, requestId, allow, denyMessage?, updatedInput?}`, `interrupt{sid}`, `mode{sid, mode}`,
  `model{sid, model}`, `effort{sid, level}`, `stopTask{sid, taskId}`.
- Worker → controller: `hello{v, name}`, `sessions[{sid, title, cwd, model, mode, state, running}]` (sent on `list` and
  pushed on change), `history{sid, lines[{kind: transcript|stream, line}], truncated}`, `line{sid, raw}`,
  `ended{sid, exitCode}`, `error{msg}`.
- **Catch-up.** Each worker `PerchSession` keeps a journal of what fed its conversation: the transcript lines it loaded
  on resume, then raw stdout lines minus `stream_event` partials (their final records follow). Capped at 16 MB per
  session, oldest dropped (`truncated` → "earlier messages not shown"). `attach` snapshots the journal and subscribes
  under one lock, so no line is missed or doubled.
- Unknown frame types are ignored (forward compatibility); a malformed frame closes the connection.

### W4 - Controller UI
- Remote sessions as overlay rows (synthesised like the not-running rows in `App.DormantRows.cs`), labelled with the
  worker's name; Roost panes the same way.
- Clicking opens the normal `SessionWindow` on a `PerchSession` backed by `RemoteSessionBackend`.
- A worker that's unreachable greys its rows ("<name> offline") rather than dropping them; reconnect restores them.

### W5 - Worker-side visibility
- "Controlled from <name>" chip on the worker's session window and overlay row while a controller is attached.
- Tray/overlay menu: "Disconnect remote controllers".
- A short log of remote commands (prompt sent, permission answered, interrupt) in the session thread as quiet notes,
  so the worker's user sees what was done from elsewhere.

### W6 - Hardening and docs
Reconnect/liveness (heartbeat every 20 s, drop after 60 s), worker sleep/resume, Perch updates on either side (version
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
