# Replay mode — implementation plan

> **Status: proposed (2026-07-21).** Design agreed; not yet started. A debug-only "replay" mode
> that materialises a synthetic `~/.claude` tree from an exported recording and drives the **real,
> unmodified app** through it under a virtual clock — scrubbable forward and back, "realtime Cypress"
> for demos and bug repros.

Goal: build reusable **reference timelines** from sessions already on disk, bundle them into a portable
export, and replay them through the actual Perch UI/detection/state-machine at a controllable rate,
stepping forward and back like scrubbing a video.

Why: concrete, repeatable demos of the tool, and step-by-step replay of the exact event sequence that
caused a bug — without needing the original machine, processes, or live Claude sessions.

---

## TL;DR

- **Do not fork the app.** Same binary, an opt-in `perch replay <recording>` subcommand (mirrors the
  existing `render` subcommand in `Program.cs:33`). The value is that replay drives the *real* code; a
  replay-only build would drift and stop reproducing real behaviour.
- **Do not write into the real `~/.claude`.** Everything routes through `ClaudePaths`, which snapshots
  `CLAUDE_CONFIG_DIR` once on first access (`ClaudePaths.cs:22`). Point that env var at a disposable
  sandbox tree and the entire reader stack follows — sessions, transcripts, sidecars, subagents. This is
  how the test suite already isolates its fixture tree (`TestEnvironment.cs`).
- **Two seams must be virtualised** for replay to work at all:
  1. **Time** — there is no clock abstraction; `DateTime.Now/UtcNow` is called ad hoc in ~30 places, the
     load-bearing one being `SessionMonitor.cs:142`. Forward *and* back scrubbing requires a virtual
     clock. Timestamp-rewriting ("make it look like now") only gets forward-only playback.
  2. **Process liveness** — `ReadSession` drops any session whose pid isn't a live OS process
     (`SessionMonitor.cs:316` → `IsProcessRunning` at `:786`). Recorded pids are dead, so **every session
     would be silently dropped** without an injectable process probe.
- **The transcript is the master timeline.** It is append-only and every line carries a timestamp, so
  "materialise state at position T" = "write every record with timestamp ≤ T". The recording is an
  immutable event log; the sandbox tree is a **pure projection of T**, which makes back-scrub identical
  to forward-scrub (regenerate, don't undo).
- **Session status is synthesised** from the transcript (see Fidelity, below), because on-disk we only
  have the *final* `sessions/{id}.json` snapshot, not its history. Good enough for demos and most repros;
  a future live-recorder is the exact-fidelity path.
- **Redaction is an opt-in build step**, defaulting to ON for shareable exports (org PII policy).
- **Metrics do not replay** (no live processes to sample). Accepted — metrics is a utility, not a
  correctness surface. Recording metric samples is a future idea.

---

## Architecture

```
  On-disk ~/.claude ──[Exporter]──▶  recording.perchreplay (zip: raw files + manifest + event index)
                                            │
                                            ▼
                          [Projector(T)]  ── pure function of scrub position T
                                            │
                        ┌───────────────────┼────────────────────┐
                        ▼                    ▼                    ▼
              synthetic CLAUDE_CONFIG_DIR   Clock → T        IProcessProbe
              (sessions/, projects/…)     (virtual)      (recorded pids "alive")
                        │
                        ▼
              the real, unmodified Perch app  ◀── driven by its own FileSystemWatcher
                        ▲
                        │  play / pause / speed / scrub / step / markers
              [Replay controller window] (debug-only)
```

Core idea: **Recording (immutable) → Projector(T) → sandbox tree + virtual clock at T → real app.**
The controller only ever moves `T`; the projector makes the world consistent with `T`.

---

## Seam 0 — the two abstractions (Phase 0)

Land these first, with system-default implementations and **zero behaviour change** (tests stay green).
De-risks everything downstream.

### `Clock` — ambient virtual time

Ambient static in `Perch.Core`, mirroring the `ClaudePaths` static-owner precedent (the codebase has no
DI container, and time is read from ~30 scattered sites incl. record methods and static helpers, so
constructor injection everywhere would be far more invasive than the problem warrants).

```csharp
// Perch.Core/Data/Clock.cs
public interface IClockProvider { DateTime Now { get; } DateTime UtcNow { get; } }

public static class Clock
{
    private static IClockProvider _p = SystemClock.Instance;
    public static DateTime Now    => _p.Now;
    public static DateTime UtcNow => _p.UtcNow;
    public static void SetProvider(IClockProvider p) => _p = p;   // replay bootstrap + tests only
}
```

Then replace `DateTime.Now`/`DateTime.UtcNow` with `Clock.Now`/`Clock.UtcNow` at the sites that compare
against on-disk timestamps. Priority order (from the ingestion audit):

- **Must** (drives state machine / disappears sessions otherwise):
  - `SessionMonitor.cs:142` — the scan anchor (`now`) for idle/running/awaiting + grace/settle windows.
  - `SubAgentReader.cs:94` — `nowUtc` for the 90s staleness demotion.
  - `ClaudeSession.cs:253/258/263` — `RunningElapsedLabel`/`AwaitingElapsedLabel` overlay timers.
- **Should** (correct "today" scoping for stats/flightpath during replay):
  - `SessionStatsService.cs:170`, `FlightPathService.cs:81-92,141`, and the App-head "today" origins at
    `App.axaml.cs:400/407`, plus the window/dashboard `DateTime.Now` day origins.
- **Cosmetic** (safe to leave on system time initially): overlay animation/auto-close timers in
  `OverlayCanvas.cs`, `SessionHistory.Relative`, network `LastUpdated` stamps.

Bonus: this makes several existing time-dependent behaviours unit-testable for the first time.

> **File mtime caveat.** `SubAgentReader.IsStale` and the stats/flightpath pre-filters compare *disk file
> mtimes* (`File.GetLastWriteTimeUtc`) — not clock values. The **Projector must stamp each materialised
> file's `LastWriteTimeUtc` to that record's virtual timestamp**, so mtime-vs-`Clock.UtcNow` comparisons
> stay coherent as T moves. This is the one place the projection and the clock have to agree.

### `IProcessProbe` — injectable process liveness

`IsProcessRunning` is used in exactly one place, so this is a small, contained change (constructor
injection into `SessionMonitor`, unlike the clock).

```csharp
// Perch.Core/Platform/IProcessProbe.cs
public interface IProcessProbe { bool IsAlive(int pid); }
```

- Default (`SystemProcessProbe`) wraps the current `Process.GetProcessById(id).HasExited` logic.
- `SessionMonitor` takes an `IProcessProbe` (default = system) and calls it at `:316` instead of the
  static helper.
- Replay supplies a probe backed by the projector: a recorded pid is "alive" iff `T` is within that
  session's active window. This is also what makes sessions **appear and disappear** at the right times.

---

## Recording format (Phase 1 output)

A single `*.perchreplay` zip. Human-inspectable; raw files kept verbatim for fidelity, plus a manifest.

```
recording.perchreplay/
  manifest.json
  timelines/
    <timeline-id>/
      session.json                    # final sessions/{id}.json snapshot (static fields)
      transcript.jsonl                # projects/{enc}/{id}.jsonl  (the master timeline)
      subagents/
        agent-*.jsonl                 # per-subagent transcripts
        agent-*.meta.json             # subagent meta sidecars
        markers/                      # .stopped / .idle hook markers (captured w/ their mtimes)
      sidecars/                       # captured .mode / .notify / .note if present
```

`manifest.json` (one entry per timeline):

```jsonc
{
  "version": 1,
  "createdUtc": "2026-07-21T…",       // stamped by the exporter after the fact (Clock is off-limits pre-boot)
  "redacted": true,
  "timelines": [{
    "id": "auth-bug-repro",
    "sessionId": "…uuid…",
    "cwd": "C:\\work\\proj",          // original (or redacted placeholder) — drives enc-project-dir
    "syntheticPid": 424242,           // assigned; IProcessProbe reports alive within the active window
    "t0Utc": "…",                     // real timestamp of the first transcript record = timeline zero
    "durationMs": 918000,
    "startOffsetMs": 0                 // per-timeline offset onto the shared replay clock (see Scenes)
  }]
}
```

The **event index is derived at load**, not stored separately: transcript lines are already time-ordered,
so the projector reads them lazily and applies those with `ts ≤ T`. Keeping raw files as the source of
truth (rather than a re-encoded event list) means the replay exercises the exact same parsers the live
app uses — no divergence risk.

---

## Exporter (Phase 1)

Point at sessions **already on disk** and build the recording. No live capture in this phase.

- **Discovery** reuses `TranscriptLocator.EnumerateTranscripts()` / `Resolve(sessionId, cwd)` and
  `ClaudePaths`. Present the user a pick-list of recent sessions (id, cwd, last activity, size).
- **Collection** per chosen session: the transcript, its `subagents/agent-*.jsonl` + `.meta.json` +
  hook markers (`SubAgentReader` shows the exact layout: `{sessionId}/subagents/`), the final
  `sessions/{id}.json`, and any `.mode`/`.notify`/`.note` sidecars.
- **Normalisation**: record `t0` = first transcript timestamp; everything replays relative to it.
  Original timestamps are preserved in the files (the projector shifts them at materialise time), so no
  lossy rewrite happens at export.
- **Redaction pass (opt-in toggle, default ON for shareable exports):** replace *content* while
  preserving *structure* so the same code paths run and stats stay realistic:
  - Redact: message text, tool inputs/outputs, file paths, `cwd`, `custom-title`/titles, git branch/repo.
  - **Keep**: record kinds, tool names, `message.model`, `message.usage` token counts, timestamps,
    tool_use/tool_result pairing, interrupt/awaiting markers. These aren't PII and are what stats,
    burn-rate, and the state machine consume.
  - cwd is replaced with a stable placeholder (e.g. `C:\demo\project-a`); the enc-project-dir is
    recomputed from the placeholder so the tree stays self-consistent.

This lives in `Perch.Core` (pure, testable) with a thin App-head window to pick sessions and toggle
redaction. A CLI form (`perch export <sessionId> <out.perchreplay> [--redact]`) is worth adding for
scripted repro capture.

---

## Projector + sandbox bootstrap (Phase 2)

- On `perch replay <recording>`: **before anything touches `ClaudePaths`** (very top of `Main`, ahead of
  the Velopack/mutex work), create a fresh temp dir, set `CLAUDE_CONFIG_DIR` to it, and install the
  replay `Clock` provider + `IProcessProbe`. The env var is snapshotted on first access, so ordering is
  the whole game here.
- `Projector.MaterialiseAt(T)`:
  - **Correctness baseline (jump / back):** wipe the sandbox `sessions/` + `projects/` and rebuild from
    the recording for every record with `ts ≤ T`. Pure function of T ⇒ back-scrub needs no undo logic.
  - **Forward optimisation (playback / step-forward):** apply only the delta between `prevT` and `T`
    (mostly transcript appends) to avoid rewriting large trees each tick. Rebuild remains the fallback.
  - Writes the synthesised `sessions/{id}.json` (see Fidelity) and stamps every file's `LastWriteTimeUtc`
    to its virtual time (mtime caveat above).
- Writing into the sandbox naturally fires the app's own `FileSystemWatcher` (`SessionMonitor.cs:811`),
  which drives a scan — so the pipeline runs itself. The controller should additionally poke an immediate
  reconcile after each projection so scrubbing doesn't wait out the 150ms debounce / 30s reconcile.

### Fidelity: how session status is reconstructed

We only have the *final* `sessions/{id}.json` on disk, so its live status history is gone. The projector
synthesises status from the transcript at T:
- Static fields (`sessionId`, `cwd`, `entrypoint`, `bridgeSessionId`, synthetic `pid`) come from the
  captured snapshot.
- `status`/`waitingFor`/`updatedAt` are derived from the transcript position: busy while the latest turn
  ≤ T is an in-flight assistant/tool_use, waiting on an unanswered permission/tool_result, idle on a
  completed turn — reusing the same discriminators `TranscriptReader`/`SessionMonitor` already compute.
- **Known limitation:** exact busy/idle/waiting *timing* is an approximation of what the hook wrote live.
  Fine for demos and most repros; a bug that depends on precise session-file status timing needs the
  future **live recorder** (which captures every `session.json` mutation with timestamps). Documented as
  the high-fidelity upgrade path, not built now.

---

## Replay controller (Phase 3)

Debug-only window (gate behind `#if DEBUG` or a hidden flag), wired into the existing single-reused-window
idiom (`WindowHost.ShowOrFocus`, closed via `CloseAuxWindows`):

- Transport: play / pause, speed (0.25×–8× and "as fast as possible"), step-forward / step-back by event,
  jump-to-marker, and a scrub bar over `[0, sceneDuration]`.
- **Scenes / multiple timelines:** a scene is a set of timelines each with a `startOffsetMs` onto one
  shared replay clock — so you can stage "project A starts at 0:00, project B joins at 0:30" to exercise
  multi-session overlay behaviour. The projector composes all timelines against the single `T`.
- Markers: auto-mark notable transcript events (turn boundaries, tool_use, interrupts, subagent
  spawn/stop) so you can jump between "interesting" frames.
- Playback loop advances `T` on a `DispatcherTimer`, calls `Projector.MaterialiseAt(T)` off the UI thread
  then reconciles (the established `Task.Run` → `Dispatcher.UIThread.Post` pattern).

Entry point mirrors `Program.cs:33`:
```csharp
if (args.Length > 0 && args[0] == "replay")
    return ReplayBootstrap.Run(args.Length > 1 ? args[1] : null);   // sets env + clock + probe, then boots App
```

---

## Phasing

| Phase | Deliverable | Risk |
|-------|-------------|------|
| **0** | `Clock` ambient + `IProcessProbe`, system defaults, all `DateTime.Now` "Must" sites migrated. No behaviour change; tests green. | Low, mechanical, wide |
| **1** | Exporter (on-disk → `.perchreplay`) + optional redaction pass. Valuable alone: "capture this repro." | Low |
| **2** | Projector + sandbox bootstrap. Forward replay working end-to-end at fixed speed. | Medium |
| **3** | Controller window: scrub/step/speed, markers, multi-timeline scenes. The "video" experience. | Medium |
| **Future** | Live recorder (exact status fidelity); metric-sample recording/replay. | — |

Each phase is independently useful; Phase 1 pays off before any replay exists.

---

## Testing

- **Phase 0** is the big test win: `Clock` makes the `SessionMonitor` state machine, subagent staleness,
  and stats day-windows deterministically testable. Add fixture + xUnit coverage per the repo convention
  (`CLAUDE_CONFIG_DIR` fixture tree, `TestEnvironment.cs`).
- **Projector** is a pure `T → files` function ⇒ golden-file tests: materialise a recording at several T
  values and assert the sandbox tree (and file mtimes) match expected snapshots.
- **Round-trip**: export a fixture session → replay → assert the reconstructed sessions/status match what
  a live scan of the original fixture produced at equivalent T.
- **Redaction**: assert no original content strings survive while structure/token-counts/models do.
- UI controller has no automated coverage (repo norm) — eyeball via `render` mode and by running replay.

---

## Risks & decisions

- **Clock migration breadth** — ~30 sites; mitigated by ambient static (one facade) and the Must/Should/
  Cosmetic ordering so early phases only touch what replay actually needs.
- **Status fidelity** — approximated from transcript; live recorder is the escape hatch. Accepted.
- **Metrics blank in replay** — accepted; utility surface, future idea.
- **PII** — redaction defaults ON for shareable exports; keep raw (unredacted) recordings local only.
- **Sandbox cleanup** — replay temp dir is disposable; delete on exit, and don't reuse across runs.
- **Watcher timing** — force an explicit reconcile after each projection instead of relying on the
  debounce/reconcile cadence, so scrubbing feels immediate.
