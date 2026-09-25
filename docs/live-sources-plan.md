# Live sources: GitHub, CI, deployments and tickets in Perch

**Status (2026-09-25):** feasibility and approach only; parked to pick up later. Nothing built. This extends
the command center design (`docs/command-center-plan.md`, which stays as signed off) with information from
outside Claude Code.

**Where we landed:** it's feasible if Perch owns the model and UI and sources only supply data. The end state
is a **separate, generic local signal tool** that any process can emit into, with Perch as its first-class
consumer. It gets built as a Perch-free library behind `ISignalBus`, runs embedded in the tray first, and is
extracted into its own exe only when a trigger fires (see "Architecture option" below). **To resume:** answer
the open questions at the bottom, then start at S0/S1.

## The ask
Users are running their whole day from Perch and want live information from other tools beside their
sessions:
- live GitHub PR lists, with an alert when they're **asked to review** or when **Actions fail**
- deployment tools such as **Octopus Deploy**
- **Jira** ticket status changes

**Verdict: this is feasible, provided Perch owns the model and the UI and each source only supplies data.**
The flexible part is a small normalized item model plus a finite set of Perch-drawn widgets. Letting each
integration draw its own UI inside an owner-drawn Avalonia tray app would be neither maintainable nor safe.

## What already exists
- **`PrStatusService`** (main) runs `gh pr view` per session branch, with a TTL cache, concurrency cap and
  transition events (`PrFinished`/`PrReviewed`/`PrApproved`) → `NotificationService`. It already solves
  GitHub auth: `gh`'s own login is reused, and Perch stores no token.
- **The pluggability branch** (`pluggability-plan`, unmerged, last touched 2026-08-21) has M1–M3 working end
  to end: out-of-process plugins over NDJSON stdio, a manifest with capabilities, a consent dialog,
  re-consent when capabilities grow, GitHub install with SHA-256 verification, fault → auto-disable, a
  master kill switch, and a Windows Job Object sandbox (memory and process caps). The branch's own finding
  is that **per-host egress enforcement isn't solved**: AppContainer breaks realistic plugins, and doing it
  properly needs WFP or a local proxy. Its only output today is `render {glyph,text,tooltip}`, which is too
  thin for a PR list.
- **`ISecretStore`** encrypts secrets at rest (DPAPI on Windows, Keychain on macOS). It's already used for
  the Social OAuth token.
- **`NotificationService`** handles toast, chime and ntfy delivery. **`QuietMode.Resolve`** gives an
  effective-settings layer. **`ISessionLock`** reports whether the machine is locked, so polling can pause.
- **The Jira deep-link plan** covers branch → key → URL offline. It names "fetch status" as the later tier
  that needs REST and credentials, which is what this doc is.
- **The PR/CI list plan** (`pr-ci-list` branch) proposes a retained `PrEventLog`. That becomes the GitHub
  case of the general event log below.

## The model: signals, not widgets
Each source produces a snapshot of **signals**: normalized, typed records with a stable id.

```
Signal {
  id            // stable per source: "gh:pr:org/repo#412", "octo:task:ServerTasks-9812", "jira:SFTY-1234"
  source, kind  // github/pr, github/review-request, github/check-run, octopus/deployment, jira/issue…
  title, subtitle, url
  state         // normalized: ok | running | needs-you | failed | info   (maps onto the status hues)
  since, actor
  fields        // a few small key/values for the row ("env": "Production", "status": "In Review")
  correlate     // optional: repo + branch, or a Jira key, so Perch can attach it to a session
}
```

Everything below the source is **generic Perch code**, written once and tested with fake sources:
1. **Scheduler:** per-source minimum interval, jitter, `Retry-After`/429 backoff, and a pause while the
   machine is locked or idle. It keeps one shared budget, so ten sources can't hammer anything.
2. **Store and diff:** the latest snapshot is kept per source. Diffing by `id` yields **transitions**
   (new, state changed, gone), which feed a retained event log.
3. **Alert rules (declarative, owned by the user, not the source):** e.g. "notify when a signal enters
   `needs-you`" or "chime when a deployment to Production fails". Rules go through `NotificationService` +
   `QuietMode`, so snooze, quiet hours and dedup behave the same everywhere.
4. **Rendering from a finite widget catalogue:** a list pane, a count badge, a status row, and a rail entry.
   A new source gets all of them for free, and none of them run source code.

### Where it shows up
- **Command center:** an **Inbox** pane type beside the session panes (grouped by source), plus rail entries.
  Signals in `needs-you` join the rail's *Needs you* group: a review request, an Octopus manual
  intervention, a ticket assigned to you. The "↓ N more · needs you" pill and the overlay glyph's badge
  count these too.
- **Session correlation** is what a generic dashboard can't offer. A signal whose `correlate` matches a
  session's repo/branch (or the Jira key in its branch name) shows on that session's pane header. For
  example: "CI failing on this branch", "SFTY-1234 moved to In Review", "deployed to Staging 4m ago".
- **Overlay:** an optional compact section with count chips. It's off by default, so the overlay stays small.

## Three ways to add a source (same pipeline behind each)

| Tier | What it is | Good for | Trust |
|---|---|---|---|
| **1. Built-in** (C#, in `Perch.Core`) | First-party source classes | GitHub (the most requested; `gh` auth already solved) | Full trust, tested |
| **2. Declarative HTTP** (JSON config) | URL + auth header (from `ISecretStore`) + JSON→Signal field mapping + interval. Perch ships **presets** | Jira (JQL search), Octopus (tasks/interruptions), status pages, most REST APIs | No code. Perch makes the call, so the token never leaves the host and egress is exactly the configured host |
| **3. Plugin** (the pluggability branch) | A process emitting `{"type":"signals", …}` lines | The long tail: internal tools, odd auth, custom logic | Consent-disclosed; the egress gap applies |

**Why tier 2 carries most of the load:** Jira and Octopus are both "poll a REST query, map the rows".
A preset is a JSON file, tested against recorded fixture responses, so Perch doesn't carry a C# connector for
every vendor. It's also safer than a plugin: Perch attaches the API key itself, so a third-party script
never holds it.

### Per-source notes
- **GitHub (tier 1).** Poll the **notifications API** as the main feed. One conditional request
  (`If-Modified-Since`, honouring `X-Poll-Interval`, normally 60s) covers review requests, mentions,
  assignments and CI activity, and a `304` doesn't count against the rate limit. Add a search for
  `is:pr is:open review-requested:@me` / `author:@me` to build the list, and check runs for the PRs you
  own. Auth comes from `gh auth token`, so nothing new is stored.
- **Octopus (tier 2 preset).** Poll the task list (executing/failed) and pending **interruptions**
  (manual intervention and guided failure). An interruption maps to `needs-you`, the deployment equivalent
  of a session awaiting input. Auth is an API key in `ISecretStore`. Base URL is per instance, since most are
  self-hosted.
- **Jira (tier 2 preset).** Run a JQL poll such as `assignee = currentUser() AND updated >= -15m`, with the
  changelog expanded to detect status transitions. The diff also catches a change the query window missed.
  Auth is email + API token (Cloud) or a PAT (Data Center).

## Limitations (stated plainly)
1. **"Live" means polling, typically 30s–2 min.** Webhooks can't reach a laptop behind NAT. Real push would
   need a relay that **you** run, and routing an organisation's PR, ticket and deployment data through
   Perch's Supabase (the Social backend) is a privacy and compliance non-starter. So: conditional requests,
   short intervals where they're free, and instant refresh when you open the command center.
2. **Rate limits are shared.** GitHub allows about 5,000 REST requests/hour per user, and that budget is
   shared with `gh` inside your Claude sessions. Jira Cloud has its own limits. Hence the central scheduler,
   backoff, pausing while locked, and a visible "last updated / backing off" state on each source.
3. **Auth is the real friction, not the code.** API tokens and PATs are the pragmatic v1. OAuth "Sign in
   with Atlassian" would need a registered app that Perch owns and operates. Some enterprises disable API
   tokens, force SSO, or IP-allowlist their Jira or Octopus, and Perch can't work around that. Secrets live
   only in `ISecretStore`, never in `settings.json`.
4. **Third-party plugins can't be network-fenced yet.** Per the pluggability M4 finding, the `network`
   capability is disclosure plus consent, not enforcement. That's another reason to cover Jira and Octopus
   with host-made declarative calls rather than plugins.
5. **Flexible data, fixed UI.** Sources can't draw custom visuals, by design. If a source needs a chart
   Perch doesn't have, the widget gets added to the catalogue (once, for everyone).
6. **"Me" differs in every system.** "Assigned to me" needs the GitHub login, the Jira accountId and the
   Octopus user. Each source resolves its own identity when it connects. Perch doesn't try to link them.
7. **Read-only in v1.** Approving a PR, re-running a job or approving a manual intervention are writes. They
   come later, each behind an explicit confirm.
8. **External text is untrusted.** PR comments, ticket descriptions and deployment logs are shown, never fed
   into a Claude session automatically. A future "start a session to fix this failing check" action must be
   explicit and must present the content as quoted data, because a hostile PR comment is a prompt-injection
   vector.
9. **Maintenance cost scales with vendors.** Vendor API deprecations (e.g. Jira REST v2 → v3) land on
   whoever owns the preset. That's why built-ins are limited to GitHub, and presets are data with fixture
   tests.
10. **Everything stays on the machine.** Fetched items are cached locally only (like the transcripts) and
    are never synced to the Social backend.

## Architecture option: a separate signal tool (recommended as the end state)
This is really a **generic, local signal store for any process**, and Perch is its richest consumer. So
build it as its own component, but **don't ship it as a separate install on day one.**

**Why it should be separable**
- **Signals are stateful items, not messages.** A PR *has* a state; it isn't "a message about a PR". That's
  the gap next to ntfy/Gotify/Apprise, which are message pipes (Perch already sends to ntfy). An upsertable
  store keyed by id, with diff, TTL and acknowledgement, is the reusable part.
- **"Any process" is the cheap half.** Pulling from GitHub, Jira and Octopus is the hard part: auth, rate
  limits, mapping. *Pushing* is trivial: `signals emit --id build:web#812 --state failed --url …` from a
  build script, a cron job, a Claude hook or CI on a dev box. That half alone justifies an external contract.
- **Real second consumers already exist in Perch's orbit.** The statusline designer's Node script can't ask
  the tray anything, but it can read a snapshot ("CI failing on this branch" as a statusline segment). A
  CLI/TUI for Linux or remote boxes with no tray. An MCP server so a Claude session can ask what's failing.
  That last one is gated: structured fields only, and titles treated as untrusted.
- **Polling state survives the tray restarting.** Velopack updates and crashes restart Perch. A separate
  process keeps its snapshots, event log and rate-limit budget through that.
- **A cleaner place for secrets.** The API tokens and the untrusted third-party sources live in a process
  that has no UI and holds no Claude transcripts.

**What it costs** (the reasons not to ship it separately yet)
- Two things to install, auto-start, update and version, with protocol skew between them. Velopack only
  manages the tray.
- A local API is an attack surface. The pipe or socket must be ACL'd to the current user. Any same-user
  process could still spoof "deploy succeeded", so each signal carries its emitter, and **reserved id
  namespaces** (`gh:`, `jira:`, `octo:`) are writable only by the daemon's own sources.
- Lifecycle: who starts it, crash recovery, and "Perch says no data, but the daemon is simply down."

**Recommended path: design it separable, host it embedded, extract it when triggered**
1. A `Signals.Core` library with **zero references to Perch**: the model, store/diff, scheduler, sources,
   rules and a wire protocol. It gets its own tests. Name it neutrally.
2. Perch talks only to an **`ISignalBus`** with two implementations: **InProcess** (the library hosted in
   the tray, the default) and **PipeClient** (a running daemon). Perch uses the daemon if it finds one and
   falls back to embedded otherwise. The two modes are the same code, so extracting the daemon later isn't
   a rewrite.
3. The protocol is NDJSON over a named pipe (Windows) or a unix socket (macOS/Linux), with `snapshot`,
   `subscribe(filter)` → upserts/removals/transitions, `emit`, `ack` and `snooze`. Perch already speaks
   this shape (`ControlServer`, `ValetServer`), and it already consumes other processes' state
   (`DaemonMonitorHost`, `HypertreeMonitorHost`).
4. **First-class integration** means `ack` and `snooze` are two-way. Acknowledging in Perch is recorded in
   the store, so every consumer sees it. Perch can also **mirror its session attention states onto the bus**
   (awaiting input, done), so a phone or statusline sees them. The session monitor itself stays native in
   Perch, because it's latency-sensitive and far richer than a signal.
5. **Extract the daemon** (a NativeAOT exe in this repo, like `perch-hook`; a separate repo only once it
   has outside users) when any of these becomes real: a second consumer ships, users want signals without
   the tray, or background polling has to survive tray restarts.

## Suggested phases
- **S0: land the plugin core.** Rebase `pluggability-plan` onto main and merge its Core. Consent, health,
  store and sandbox are reused by tier 3, and it has been sitting for five weeks.
- **S1: `Signals.Core` library + `ISignalBus` (tested).** A project with **zero Perch references**:
  `ISignalSource`, `Signal`, scheduler, store/diff, event log, alert rules, and the wire protocol
  (snapshot / subscribe / emit / ack / snooze). Perch consumes it only through `ISignalBus` (the in-process
  implementation for now) and routes alerts to `NotificationService`. Built against fake sources, with no UI.
- **S2: GitHub built-in + Inbox pane.** Notifications-API source, the command center Inbox pane, rail
  entries, and the badge count. `PrStatusService`'s per-session PR glyph keeps working, and later reads
  from the same store. This one phase meets the "review requested / actions failed" ask.
- **S3: declarative HTTP sources + Jira and Octopus presets.** A Sources settings page (add source →
  preset → base URL + token → test connection → preview of mapped rows).
- **S4: session correlation.** Signals on session pane headers, plus the "Needs you" merge.
- **S5: plugin `signals` message** for the long tail, then writes and "start a session about this".
- **Sx (whenever a trigger fires): extract the daemon.** A NativeAOT exe hosting `Signals.Core` behind a
  user-ACL'd pipe/socket, a `signals emit` CLI, and the `ISignalBus` PipeClient in Perch (daemon if running,
  embedded otherwise). The triggers: a second consumer ships, users want signals without the tray, or
  polling must survive tray restarts.

## Open questions
1. Is a self-hosted relay (Octopus or GitHub webhooks → something the team runs) wanted for true push, or
   is 30–60s polling enough?
2. Should sources be per config dir or per account (the multi-org work)? A work GitHub and a personal
   GitHub on one machine would suggest per account.
3. Where should non-session signals live when the command center is closed: an overlay section, or only
   the glyph badge?
4. What should the separate tool be called, and should it be positioned for use outside Perch from the
   start (its own repo and docs) or only after it has outside users?
