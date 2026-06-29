# Perch — Three-Angle Review & Next Steps

A read-only review of the Perch codebase from three angles — **UX**, **technical architecture**,
and **devil's advocate** — to help prioritise where to invest next. No code was changed during the
review.

The most useful result is **convergence**: the same few issues surfaced independently from very
different lenses. That overlap is the signal for what to prioritise.

---

## The one thing all three angles agreed on

**Perch's entire value rests on parsing Anthropic's undocumented, unstable on-disk formats**
(`~/.claude` session JSON, JSONL transcripts, sidecars, and a private OAuth usage endpoint).

- Architecture calls it the "central, unavoidable risk."
- Devil's advocate calls it "a foundation you don't own and can't stabilize."
- UX notes it degrades *silently* to a blank panel users can't distinguish from "no activity."

The code is defensively written, but defensiveness only converts a crash into **silent breakage**.

→ **Cheap, high-leverage mitigation all three implicitly endorse:** a "did we recognise this file's
shape at all?" sanity check that surfaces a *"Perch may need updating"* hint instead of going dark.
Turns silent breakage into a diagnosable signal. (arch #3)

---

## Strategic question to answer before investing further

Worth a real answer (devil's advocate framing): **what survives Anthropic shipping a native
"notify when idle" toggle?**

- The only defensible moat today is **multi-session aggregation + click-to-focus**
  (`NativeMethods.FocusTerminalForProcess`) — something a three-line `Stop` hook genuinely can't do.
- Recommendation from that angle: make *that* bulletproof, and treat stats / usage / quick-links /
  Wrapped as expendable surface rather than a roadmap.
- Also flagged: the **Windows-only WinForms platform bet** (heavy Claude users skew Mac/Linux). No
  cheap fix, but it bounds the addressable audience and a Mac port would be a rewrite.

---

## Release-blocking cluster (do these first — low/medium effort, high impact)

Where UX and architecture reinforce each other. These directly fix the core "alert me when I'm
needed" loop, which both angles say is currently undercut.

| # | Issue | Angle(s) | Effort |
|---|-------|----------|--------|
| 1 | **Needs-attention never floats to the top; overlay can't scale** — sessions sort alphabetically with no height cap/scroll (`OverlayForm.cs:294`, `:514-527`), so the session needing you gets buried below "a…–z…" with 10+ sessions. The core job fails exactly when you have enough sessions to need it. | UX P0 | Med |
| 2 | **Alert signal weaker than the FYI signal** — the only always-on cue is a ~1.5px border flash (`OverlayForm.cs:543`) that stops after 10s (`:199-204`); NeedsAttention lapses to plain Idle after 5 min (`SessionMonitor.cs:9`), even for a permission prompt that won't resolve itself. | UX P0 | Low–Med |
| 3 | **"Done" and "Awaiting input" notified identically and indiscriminately** — both default on (`AppSettings.cs:48-49`), a toast per completion (`SessionMonitor.cs:392`, `OverlayApplicationContext.cs:461-467`) → alert fatigue → users disable *all* notifications, losing the important permission alert too. | UX P0 | Low |
| 4 | **`Scan()` does all transcript IO on the UI thread** — busy sessions miss the mtime cache every scan and whole-file parse multi-MB transcripts on the UI thread several times/sec (`SessionMonitor.cs:286-369`, `TranscriptReader.cs:344/301/149`, `TranscriptLocator.cs:39-47`). Violates CLAUDE.md's own off-thread rule; causes stutter under exactly the many-session load being targeted. | Arch #1 | Med |
| 5 | **The detection state machine is the least-tested code** — busy→idle→NeedsAttention transitions, bare-command suppression, sub-agent roll-up, event de-dup (`SessionMonitor.cs:223-397`). Pure/deterministic, testable with the existing fixture harness. A regression here *is* the "Perch cried wolf" bug that erodes trust. | Arch #2 | Med |

**Note:** #3 and #5 are two views of the same risk — false/noisy alerts kill a notifier's entire
value. Worth tackling as a pair.

---

## Second tier (cheap hardening + comprehension)

- **First-run is an empty box** — no empty state, no "what is this"; plugin install (which gates half
  the features) can silently fail with no indication (`OverlayApplicationContext.cs:194-212`,
  `SettingsForm.cs:340-369`). (UX #4, #5)
- **Validate `sessionId` before using it in file paths** — read verbatim from JSON and used in
  `Path.Combine` for `.notify`/`.mode` writes/deletes (`SessionMonitor.cs:144, 320, 325`).
  Path-traversal trust boundary; trivial `^[A-Za-z0-9_-]+$` fix. (Arch #4)
- **Colourblind risk** — Awaiting (yellow `#FACC15`) vs Needs-Attention (orange `#FB923C`) are
  adjacent hues, dots-only in header pills and the dense strip; the usage over-rate marker signals
  purely by turning red (`UsageBarRenderer.cs:69-81`). Add a shape/glyph differentiator. (UX #7)
- **Usage feature reads `.credentials.json` and impersonates the CLI's User-Agent** against a private
  endpoint (`UsageMonitor.cs:90-111`) — both arch and devil's advocate flag that a future
  Anthropic-side change could plausibly rate-limit/flag the *user's account*, not just break the bar.
  Worth an explicit doc caveat. (Arch #5, DA tactical)

---

## Watch-items / low-urgency hardening

- **`MtimeCache` entries never evicted** — one entry per path forever across 7 caches
  (`MtimeCache.cs:16`); unbounded growth over long uptime. Prune paths not in live `activePids`.
  (Arch #6)
- **Plugin↔app contract robust but unversioned; install not atomic** — `PluginManager.EnableAsync`
  does add-marketplace then install with no rollback; no lock against first-run auto-install
  overlapping a manual enable. (Arch #7)
- **LockMonitor assumes unlocked at startup** — auto-start on an already-locked machine misses AFK
  pushes until the next lock/unlock cycle (acknowledged in comments). (Arch #8)
- **DPI / owner-drawn clipping** — overlay uses hard-coded pixel constants (header 44, row 46, fonts
  down to 7–7.5pt) with no visible per-DPI scaling; CLAUDE.md flags this as a recurring bug class.
  Verify at 150%/200% DPI. (UX #10)
- **Usage windows aren't explained / no inline reset countdown** — reset times tracked but
  tooltip-only; add inline "resets in 2h 30m". (UX #8)
- **Hidden interactions** — right-click quick-actions menu, dense-strip drag, and glyph tooltips have
  no visual cue. (UX #6)

---

## What's genuinely working (don't regress)

All three angles called these out as strengths:

- **Clean App/Data/Ui separation** — `NotificationService`, `UiDispatch`, `WindowHost`,
  `ClaudePaths`, `TranscriptLocator` are clean extractions; logic is not leaking into the UI.
- **`MtimeCache` + tail-scan** — the right perf model for append-only files (just keep the heavy
  variants off the UI thread — see #4).
- **Best-effort discipline** — consistent "parse defensively, never throw out of a scan" is why the
  app survives Claude's partial writes; opens with `FileShare.ReadWrite`.
- **Watcher + reconcile-poll redundancy** — explicit `FileSystemWatcher` overflow handling
  (`OnWatcherError`) and process-exit subscriptions for unclean exits.
- **Single-source-of-truth `.notify` sidecar** — overlay toggle and `/afk` write the same marker, so
  the two paths can't disagree.
- **Dense-mode strip** — preserves per-status dot+count when collapsed and auto-opens on attention.
- **Bare-command suppression & sub-agent roll-up** — tuned against real transcripts to minimise false
  positives.

**Bottom line: the engineering is solid — the risks are about foundation and focus, not craft.**

---

## Recommended sequencing

1. **Decide the strategic question** (moat vs. Anthropic shipping it natively) — it determines whether
   to invest in the next steps or cap the project.
2. If continuing: ship the **release-blocking cluster (#1–#5)** — directly fixes the core
   "alert me when I'm needed" loop.
3. Add the **format-drift sanity signal + sessionId validation** — the cheapest risk-reducers on the
   board.
4. Treat **stats / usage / Wrapped / quick-links as frozen or trimmable**, not a roadmap.

---

*Central files referenced: `src/Data/SessionMonitor.cs`, `src/Data/TranscriptReader.cs`,
`src/Data/TranscriptLocator.cs`, `src/App/OverlayApplicationContext.cs`, `src/Data/UsageMonitor.cs`,
`src/Data/MtimeCache.cs`, `src/Ui/OverlayForm.cs`, `tests/Perch.Tests/`.*
