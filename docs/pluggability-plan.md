# Making Perch pluggable (third-party extensions)

**Status:** In progress, 2026-08-21. Branch `pluggability-plan`. **M1–M3 are usable end-to-end
through the UI** — install a plugin (from GitHub or a local folder), consent to its capabilities,
and see it run in a collapsible "Plugins" overlay section — all built on the tested Core (120+
tests). Remaining: the `command` extension point (menu-coupled), the M4 OS sandbox, and a security
review. This doc records the model, the security posture, the GitHub publish/install story, the
Windows trial plugins, and (below) the milestone status.

**Housekeeping done on this branch:** the old `PluginManager` is renamed to
`ClaudeCodePluginManager` (`src/Perch.Core/Data/ClaudeCodePluginManager.cs`). It was never a
Perch extension system — it only migrates users off the retired *Claude Code marketplace
plugin* by stripping keys from `~/.claude/settings.json`. The explicit name frees the word
"plugin" for the real thing described here.

## The problem

Users want to extend Perch — add a badge, a data source, a notifier — **without waiting for a
release**. Perch has no extension mechanism today. The clean-slate opportunity is to design one
that fits Perch's grain (Core interfaces + platform seams, file-based `~/.claude` data, an
already-tested GitHub release/verify installer) and — because Perch reads genuinely sensitive
data (session transcripts, API-usage spend) — is **secure enough to install a stranger's
plugin from GitHub without fear**.

## Threat model (state it in one sentence, defend that)

> *A malicious plugin author wants to read a victim's `~/.claude` transcripts or API-usage
> figures and exfiltrate them, corrupt Perch's sidecars, or hang/crash the tray.*

Every decision below defends that sentence. The sensitive assets are the `~/.claude` tree and
network egress; the failure modes are exfiltration, corruption, and denial of service.

## The core principle: an extension *contract*, not an extension *hole*

Pluggability inverts a dependency: Perch defines a **contract** and calls whatever implements
it, discovered at runtime. Perch already does this internally — `INotifier`, `IWindowActivator`,
`IAudioCue` in `Perch.Core/Platform/`, resolved through `PlatformServices`. A plugin API is that
same idea pointed *outward*, with two disciplines that make it safe and maintainable:

1. **A finite catalogue of extension points, never "run arbitrary code."** A plugin may do a
   small, enumerated set of things — and nothing else. Proposed v1 points:
   - `overlay.glyph` — contribute a badge/glyph + tooltip to the overlay strip.
   - `poll` — a data source the host polls on an interval (feeds a glyph or the feed).
   - `command` — a tray/context-menu item or quick action the user can invoke.
   - `event` — subscribe to session lifecycle events (`session.attention`,
     `session.idle`, `session.done`, …) and react (notify, POST, speak).
   - `notify` — raise a Perch notification (goes through `INotifier`, host-mediated).
2. **The contract is a public API, versioned like one.** It lives in one place, evolves
   additively (the same "append-only, order is the wire format" rule as `ThemeCodec.Roles`),
   and never leaks Perch internal types.

## Architecture: three models, and the .NET trap

| Model | What a plugin *is* | Isolation | Verdict for Perch |
|---|---|---|---|
| **Declarative / data** | a JSON blob the host interprets | Safe by construction (no code) | Already shipped (theme share-codes); keep pushing extensibility here |
| **Out-of-process** | an executable/script Perch launches, JSON over stdio | OS process boundary (+ optional sandboxed token) | **Primary model for v1** |
| **In-process WASM** | a `.wasm` module run via a host runtime | Capability sandbox (host grants imports) | v2, only if out-of-process proves too limiting |
| ~~In-process .NET DLL~~ | a `.dll` via `AssemblyLoadContext` | **None — full trust** | Reject for third-party; signed first-party only |

**The trap:** modern .NET has *no in-process security sandbox*. Code Access Security is gone;
`AppDomain` sandboxing is gone. `AssemblyLoadContext` isolates *type identity and unload*, **not
permissions** — a loaded DLL can read all of `~/.claude`, open sockets, and spawn processes at
full trust. So loading a stranger's DLL is equivalent to running their `.exe` with your rights,
minus the honesty of admitting it. We do **not** do in-process DLL plugins for third parties.

**Why out-of-process wins for v1:** the trust boundary is the OS process — a crash can't take
down the tray, a hang can't wedge the UI (kill on timeout), and the child can be launched with a
restricted token. Perch already lives this pattern twice: `perch-hook` is a separate process, and
the renamed `ClaudeCodePluginManager.RunProcessAsync` already models the 120 s timeout +
`Kill(entireProcessTree)` discipline we'd reuse.

## The contract

### Manifest — `perch-plugin.json` (at the plugin repo/package root)

```jsonc
{
  "schema": 1,
  "id": "dev.jon.weather",          // reverse-DNS, globally unique, immutable
  "name": "Weather Badge",
  "version": "1.0.0",               // semver
  "description": "Shows the local temperature in the overlay.",
  "author": "Jon H",
  "homepage": "https://github.com/owner/weather-badge",
  "minPerch": "0.9.0",              // host compat floor
  "entry": {
    "type": "process",
    "command": "powershell",
    "args": ["-NoProfile", "-ExecutionPolicy", "Bypass", "-File", "weather.ps1"]
  },
  "extensionPoints": ["overlay.glyph", "poll"],
  "capabilities": {
    "network": ["api.open-meteo.com"],   // egress allowlist (empty/absent = no network)
    "read.sessions": false,              // read ~/.claude session/transcript data
    "read.cwd": false,                   // read the active session's project dir
    "notify": false,                     // raise notifications
    "poll.intervalSec": 300              // host-enforced floor clamps this
  }
}
```

The manifest is the whole security surface: **the host grants only what's declared, and nothing
implicitly.** Capabilities absent = denied.

### Wire protocol — newline-delimited JSON over stdio

Host → plugin and plugin → host, one JSON object per line. The host **enforces** capabilities on
every inbound message (a plugin that never declared `notify` can send `{"type":"notify"}` all it
likes — the host drops it).

```
→  {"type":"init","perch":"0.9.0","grants":["overlay.glyph","poll"]}
←  {"type":"ready","render":{"glyph":"☁","text":"18°","tooltip":"Melbourne"}}
→  {"type":"poll","ts":"..."}                         // host tick
←  {"type":"render","glyph":"☀","text":"24°"}
→  {"type":"event","event":"session.attention","sessionId":"..."}
←  {"type":"notify","title":"...","body":"..."}       // dropped unless 'notify' granted
```

Long-lived process (kept warm, host owns the interval) or one-shot (spawned per tick, simpler,
what scripts want) — support both via `entry.mode`, default one-shot for scripts.

### Host side (all in `Perch.Core`, per the "every OS capability behind an interface" rule)

- `PluginService` — discovery, manifest parse/validate, consent gate, lifecycle, capability
  enforcement, the render→overlay bridge. UI-free.
- `PluginManifest` / `PluginCapabilities` — models + a strict validator (reject unknown top-level
  keys, clamp `poll.intervalSec`, normalise host allowlist entries).
- `IPluginSandbox` — the **OS-specific** bit: launch the entry process with least privilege.
  Windows impl = restricted/AppContainer token, no inherited handles, working dir pinned to the
  plugin's own folder; a mac impl can wrap `sandbox-exec`. Plain `Process.Start` is the
  no-sandbox fallback (still a separate process).
- Overlay integration reuses the existing collapsible-section / glyph patterns
  (`OverlayCanvas`), and notifications go through the existing `INotifier` — plugins never touch
  the UI directly; they emit intents the host renders.

## Security model (the non-negotiables)

1. **Capability-based least privilege.** Manifest declares; host grants only that; everything
   else denied. Mirror the mobile-app permission mental model.
2. **Explicit consent at install, re-consent on capability growth.** A dialog spells it out in
   plain words: *"Weather Badge wants to: access the network (api.open-meteo.com)."* An update
   whose manifest adds a capability is **disabled until re-consented**. Consent state persists in
   `AppSettings` (a `PluginGrants` map), not silently in a file the plugin can edit.
3. **Isolation.** Separate process always; restricted token where `IPluginSandbox` can
   (defence in depth so even a granted `read.cwd` can't wander outside).
4. **Egress allowlist is the load-bearing control.** `network` is a list of hosts, not a bool.
   No entry = no sockets. This is what stops "read transcripts → POST to attacker". For v1,
   enforce at the consent/disclosure layer + sandbox where possible; document honestly that a
   fully airtight per-plugin egress firewall is a v2 sandbox concern.
5. **Provenance & integrity.** Install pins a GitHub `owner/repo@version` and verifies the
   downloaded asset against the release's `SHA256SUMS.txt` — the **exact** pattern `install.ps1`
   already uses and `tools/test-install.ps1` already tests. Record the resolved commit/tag + hash
   in the install record so an update is a visible, re-consentable change.
6. **Resource limits & graceful failure.** Hard per-call timeout + `Kill(entireProcessTree)`
   (already in hand), a memory ceiling where the sandbox allows, poll-interval floor, and all
   plugin I/O off the UI thread (`Task.Run` → `Dispatcher.UIThread.Post`, the house pattern). A
   slow/broken/malicious plugin must never hang or crash the tray; it gets disabled with a badge
   after N consecutive faults.
7. **No ambient secrets.** The plugin never receives the OAuth token, raw absolute paths it
   didn't ask for, or other plugins' data. It asks; the host brokers.
8. **Kill switch & audit.** A master "disable all plugins" toggle, per-plugin enable/disable, and
   a visible log of what each plugin did (last render, last egress host, faults) in a Plugins
   settings page.

## Publishing & installing from GitHub (the nice bit)

Reuse the machinery that already exists and is tested — don't invent a second one.

### Publish (plugin author)

1. A plugin is a **GitHub repo** with `perch-plugin.json` at root.
2. Tag a release `v1.0.0`; attach:
   - the payload (`<id>-1.0.0.zip` containing the manifest + entry script/exe), and
   - `SHA256SUMS.txt` (same convention as Perch's own releases).
3. Add the GitHub **topic `perch-plugin`** so discovery is a topic search — no central registry
   to run on day one.

*(A CI template — `perch-plugin-template` repo — can zip + hash + release on tag, so authors get
the verify story for free, exactly like Perch's `release.yml`.)*

### Install (user)

- Tray/Settings → **Plugins → Add from GitHub…**, paste `owner/repo` (optionally `@vX.Y.Z`).
- Perch resolves the release (latest stable by default), downloads the asset, **verifies its
  SHA-256 against the release's `SHA256SUMS.txt`** (bail on mismatch — the install.ps1 rule),
  extracts to `~/.claude/perch/plugins/<id>/`, parses the manifest, shows the **consent dialog**,
  and enables on approval.
- Updates: a background check compares the pinned version to latest; the user gets an "update
  available" affordance; applying re-verifies the hash and **re-consents if capabilities grew**.
- Discovery page: GitHub topic search `topic:perch-plugin` renders name/description/stars, each
  with an Install button that runs the same verified flow.

This keeps every install an auditable, hash-verified GitHub artifact — the same trust properties
as installing Perch itself.

## Windows trial plugins (prove the model)

Pick a spread where **each exercises a different capability**, so trialling them actually tests
the security model rather than one happy path. PowerShell scripts are the natural Windows trial
vehicle — zero build, present on every box, trivial JSON-over-stdio.

| # | Plugin | Extension point | Capabilities exercised | What it proves |
|---|---|---|---|---|
| 1 | **Pomodoro / focus timer** | `command` + `overlay.glyph` | *(none)* | The safe baseline — a genuinely useful plugin that needs **zero** grants. Menu item starts a timer, glyph counts down, done → `notify` (only if granted). |
| 2 | **Git dirty-count badge** | `poll` + `overlay.glyph` | `read.cwd` | Local read scoped to the active session's project dir; runs `git status --porcelain`, shows uncommitted-file count. Tests **scoped filesystem consent** — and is very on-brand. |
| 3 | **Weather badge** | `poll` + `overlay.glyph` | `network:[api.open-meteo.com]` | Inbound network to a **single allowlisted host**. Tests the egress allowlist + network-consent disclosure. |
| 4 | **Now-Playing badge** | `poll` + `overlay.glyph` | *(none — local media API)* | Reads Windows `GlobalSystemMediaTransportControls`; shows current track. Tests a richer local data source with no grants. |
| 5 | **Webhook notifier (Slack/Discord)** | `event` + `notify` | `network:[hooks.slack.com]`, `read.sessions` (opt) | On `session.attention`, POST to a webhook. This is the **scary one** — read + egress — so it's the best test of the consent UX, the "capabilities grew → re-consent" path, and the kill switch. |
| 6 | **TTS announcer** | `event` | *(none)* | Speaks "session needs attention" via `System.Speech`. Event-driven, no network — proves eventing without touching the scary capabilities. |

Suggested order to build/trial: **1 → 2 → 3 → 5**. #1 shakes out lifecycle with nothing at
stake; #2 the first real capability; #3 the allowlist; #5 the full adversarial consent story.
Each ships as its own GitHub repo tagged `perch-plugin`, so building them *is* the end-to-end
test of publish + verified install.

## Milestones

- **M0 — Rename. ✅ Done** (commit ee7ab02). `PluginManager` → `ClaudeCodePluginManager`.
- **M1 — Contract + host core + overlay. ✅ Done** (2bba88d, 755805a). The Core (manifest/validator,
  NDJSON protocol, capability gate, sandbox seam + `ProcessPluginSandbox`, one-shot session, registry,
  service) **plus** the collapsible **"Plugins" overlay section** (`OverlayCanvas.Plugins.cs`) fed by
  `PluginMonitorHost`. Runnable `samples/plugins/git-dirty`; section verified via render mode.
- **M2 — GitHub install + verify + consent. ✅ Done** (b247941, 755805a). Core install pipeline
  (`PluginInstallSource`, `GitHubReleaseParser`, `Sha256Sums`, `HttpPluginDownloader`, `PluginInstaller`
  with SHA-256 verify + zip-slip-guarded extract + `InstallFromDirectory` sideload) **plus** the
  `PluginConsentDialog` (Allow/Deny over the requested capabilities) and the settings-page install flow.
  `PluginConsent` re-consents on capability/host growth.
- **M3 — Extension points + Plugins page. ✅ Mostly done** (cbc1c4d, c526f3b, 755805a). `event` point +
  `SessionEvents`, consented-grant enforcement, `PluginHealth` (fault → auto-disable), `PluginHost.Resolve`,
  master kill switch, `PluginStore`, **plus** the Settings **"Plugins" page** (master toggle, install from
  GitHub / local folder, per-plugin enable/disable/remove). *Remaining:* the `command` extension point
  (menu-coupled; manifest `commands` + invoke path) and an audit/fault log surface.
- **M4 — Sandbox (`IPluginSandbox`) + resource limits.** Restricted-token launch on Windows;
  timeouts/interval floors/fault-disable hardened. Security review. *Not started.*
- **M5 (later) — WASM tier** if out-of-process proves too limiting for compute-heavy plugins.

### What's committed vs. what needs the user

**Committed (autonomous, fully tested Core):** M0 rename; the entire out-of-process host pipeline —
discover → resolve (consent × master switch) → launch (isolated process) → one-shot JSON exchange
(timeout/kill) → capability-enforced interpret → health/auto-disable; and the GitHub install path —
resolve release → SHA-256 verify → zip-slip-safe extract → validate → place → consent record.

**Needs a UI session with the user (design + can't be verified headless):** the collapsible "Plugins"
overlay section (glyph rendering), the consent dialog, the Plugins settings page, and the menu-coupled
`command` extension point. Then M4's OS sandbox + a security review.

## Maintenance

- **One versioned contract** (`Perch.Core` models + the wire schema). Semver it; treat a break as
  a breaking release.
- **Additive-only evolution.** New capabilities/extension points are new optional fields; never
  repurpose an existing one. `schema` on the manifest + `perch`/`minPerch` version negotiate old
  plugin ↔ new host cleanly (fail with a message, never a crash).
- **A golden sample plugin + conformance test** in `tests/Perch.Tests` so a contract break trips
  the build — the same instinct as `SettingsRegistryTests` / `PresetContrastTests`.
- **Document the extension-point catalogue** as the real public API.

## Open questions

1. **Egress enforcement depth in v1** — disclosure + best-effort sandbox now, or hold network
   plugins until M4's real per-plugin firewall? (Leaning: allow with *loud* consent in M2, gate
   the airtight version behind M4.)
2. **Script runtime dependency** — PowerShell is fine for the Windows trial, but a portable
   ecosystem wants self-contained executables. Do we bless a couple of runtimes, or push authors
   to ship self-contained binaries for anything distributed widely?
3. **Central registry vs. topic search** — start with the GitHub `perch-plugin` topic (zero infra)
   and only build a curated index if abuse/quality demands it.
4. **macOS parity** — the host is all `Perch.Core`; only `IPluginSandbox` needs a mac impl
   (`sandbox-exec`). Slots straight into the port plan.
