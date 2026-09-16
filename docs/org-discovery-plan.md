# Org discovery — implementation plan (Layer 2)

Make Perch aware of **which Claude org a config dir is signed into**, let the user **declare which org a
dir or a project is allowed to use**, and **warn when they don't match** — the "you `/login`'d into the
wrong account" footgun.

This is **Layer 2 of three** (see `config-dir-plan.md` → *Architecture: three decoupled layers*). Layer 1
(config-dir discovery, org-free, directory attribution) is **merged and deployed** (`support for config
dirs (#29)`, commit `574fcb4`). This plan builds on those seams without reopening them: **Layer 1 types
stay org-free — `ClaudeConfigDir` never gains an `Org` property.** All org logic lives in a new Layer-2
service that *reads* a `ClaudeConfigDir`, never inside it.

Prior spike that constrains this plan: apiKeyHelper is API-key-mode only, unusable for SSO/OAuth orgs — org
selection stands on `CLAUDE_CONFIG_DIR` (see `.claude/artefacts/claude-config-dir-multi-org.md`).

---

## The conceptual rule

**A config dir does not *have* an org.** It has whatever org the credential currently in it is signed into,
and `/login` can change that at any moment. Three distinct facts, only one *asserted* rather than *observed*:

- **Live org** — what the dir's credential is signed into *right now*. Observed from `<dir>/.claude.json`
  `oauthAccount` (`organizationUuid` = identity, `organizationName` = display). Mutable; re-read, never cached
  as identity. **Truth for the directory.**
- **Declared org** — what the user *intends* a dir, or a project, to be. An assertion Perch stores. **Intent.**
- **Captured org** — what a *session* actually ran under, recorded at start-time. **Truth for the session**,
  immune to a later `/login`. (Only the *attribution track* below needs this.)

The valuable feature is the **reconciliation** (`Match` / `Mismatch` / `NotSignedIn` / `Unknown`), not a
static "dir X = org Y". The mismatch *is* the product.

---

## Two separable tracks

The long-term vision (surface current org; mark a dir "only org X"; mark a project "only org X" so you can't
work the wrong account) is a **guardrails** story. It is achievable almost entirely by **reading files** — it
does **not** need a new hook or the plugin migration. Retrospective per-session attribution is a **separate**
story that does. Keeping them apart lets the high-value guardrails ship first, off the critical path of the
fragile hook-write mechanism.

### Track A — Guardrails (the priority; file-reads only)

Surface live org, let the user declare expected orgs, warn on mismatch. Perch is an **observer** here: it
**warns**, it does not prevent (prevention is Layer 3, below). No hook writes, no plugin.

### Track B — Attribution & delivery (separable, deferred)

Record what org each *past* session ran under (for stats/history), and re-evaluate hook *delivery*. Needs the
hook (and possibly a plugin). Only pursued when retrospective attribution, in-terminal warnings, or tray-down
coverage are wanted. Detailed at the end.

---

## The honest limit on "cannot start from the wrong account"

In Layers 1–2, **Perch is an observer, not a gatekeeper.** It is not in the launch path — the org is already
bound by whichever config dir was active before `claude` ran. So the guardrails MVP delivers a **loud
warning**, not prevention.

True "cannot" arrives with **Layer 3 (Perch as launcher)**, and it is the *nicer* form: instead of marking a
project and blocking the wrong account, Perch reads `cwd → expected org`, looks up which config dir *is* that
org (the dir→org declaration), and **launches on the right account automatically**. Auto-routing, not a
guardrail. This is why the dir→org declaration (M3 below) is worth having even though project→org detection
(M2) doesn't strictly need it: it is the routing table that upgrades warning into auto-selection.

---

## Architecture

| Concern | Home | Notes |
| --- | --- | --- |
| Read a dir's **live** org | `IOrgProvider` (new, Layer 2) | reads `<dir>/.claude.json` `oauthAccount`; keyed by `organizationUuid`; re-read, cached only with invalidation |
| **Declared** org (dir & project) | `AppSettings` (new fields) + `IConfigDirSource` seam | intent; a scheme manifest may also declare a dir's org |
| **Reconciliation** | `OrgState` (new) | `Match` / `Mismatch` / `NotSignedIn` / `Unknown`, live vs declared |
| Session → dir attribution | Layer 1 (`ClaudeSession.AttributedConfigDir`) | already shipped; org rides on top |
| Session `cwd` + arrival | Layer 1 (`SessionMonitor`) | already watches `sessions/`, already knows `cwd` — the seam project→org warning rides on |
| **Captured** org per session | durable log written by the hook | **Track B only** |

Dependency arrow points one way: **org logic depends on Layer 1, never the reverse.** The `.claude.json`
reader is new (Perch reads only `.credentials.json` today, for the usage token) and models a file Layer 1
deliberately never modelled.

---

## Milestones — Track A (guardrails)

Sequenced so each is a shippable slice and the headline value (project mismatch warning) comes early.

### M0 — `.claude.json` live-org reader + org model  *(item 1)*

- `ClaudeConfigDir` gains a **path accessor** `ClaudeJsonFile` (`<root>/.claude.json`) — path only, still no
  `Org` property (stays org-free).
- New Layer-2 reader (pattern: `FileClaudeCredentials` + `UsageMonitor`'s `JsonNode` parse) reads `oauthAccount`
  → `{ organizationUuid, organizationName, accountEmail? }`. Tolerant/best-effort; malformed / absent /
  signed-out → `null`.
- `Org` value type keyed by `organizationUuid` (names duplicate and change).
- Tests: fixture `.claude.json` under the synthetic `~/.claude` tree — present / absent / malformed / signed-out.

### M1 — `IOrgProvider` (live) surfaced per dir  *(item 1, visible)*

- `IOrgProvider.GetLive(ClaudeConfigDir)` over M0; cached with invalidation (mtime/watch), never cached across
  a `/login`.
- Overlay: show the **live** org per attributed dir, gated on `IsMulti` (single-dir users see nothing new),
  following the Layer-1 dir-chip pattern (`DrawDirChip`) + `SettingsRegistry` descriptor + `OverlaySettingsGates`.
- macOS caveat: verify per-`CLAUDE_CONFIG_DIR` credential isolation is a non-issue for the *file* read (it
  should be — `.claude.json` is a plain file; the Keychain question was about the *token*).
- Tests: provider caching/invalidation; render-verify the chip.

### M2 — Project → org rule + mismatch warning  *(item 3 — the headline)*

- New per-project declaration: `cwd → expected organizationUuid` (a new `AppSettings` list, keyed by resolved
  project path; decide subtree vs exact — start exact, subtree later).
- Detection is **tray-side, no hook**: when `SessionMonitor` sees a session appear, compare its `cwd`'s expected
  org against the attributed dir's **live** org (M1) → `OrgState`. On `Mismatch`, warn (overlay banner + toast).
- Settings: a project→org editor (add current project, pick org from the discovered live orgs).
- Tests: reconciliation matrix; a session in a ruled cwd on the wrong live org raises a mismatch; uuid-keyed
  identity survives a display-name change.

### M3 — Config-dir → org declaration  *(item 2 — the bridge to Layer 3)*

- New per-dir declaration: `resolved real-root → expected organizationUuid` (an `AppSettings` map beside the
  Layer-1 label/hide maps; wire the `IConfigDirSource` seam so a scheme manifest can also declare it).
- Reconciliation: dir's **live** org vs its **declared** org → warn when a dir has been re-`/login`'d into the
  wrong account (the footgun M2 can't see for a dir with no project rule).
- This map is the **routing table** Layer 3 consumes.
- Overlay: warning glyph on a dir chip whose live ≠ declared.
- Tests: dir mismatch detection; declaration round-trips through settings.

Layer 3 (Perch as launcher: `cwd → expected org → the dir that is that org → launch with that
`CLAUDE_CONFIG_DIR`` at `ClaudeSessionController.Start`; `/login` routing; the org-mismatch doctor) remains
future work, unchanged from `config-dir-plan.md`. It turns M2's warning into prevention/auto-routing.

---

## Track B — Attribution & delivery (deferred, separable)

Not on the guardrails critical path. Pursue when retrospective per-session org (stats/history), in-terminal
warnings, or tray-down coverage are wanted.

### B1 — Historical capture at SessionStart

- Extend `Perch.Hook` `start`: read the resolved dir's `.claude.json` `oauthAccount`, append
  `{sessionId, uuid, name, email?, capturedAt}` to a **durable Perch-owned append-only log** under the profile
  dir (e.g. `session-orgs.jsonl`). **Not** a `sessions/{sid}.org` sidecar — `HandleCleanup` deletes those at
  SessionEnd, and the record must survive that *and* the tray being down. The tray reads this log; it is never
  the writer of the historical fact. Reuse the AOT-safe `Utf8JsonReader` style; every failure a silent `catch`.
- Surfaces per-session captured org in history/stats and enables per-org spend attribution.

### B2 — Hook-delivery go/no-go spike

The self-managed hook merge (`ClaudeUserSettings.ReconcileHooks`) is correct (strips only Perch-owned entries,
preserves user hooks) but the **write mechanism** is fragile, and Layer 1 multiplied it across N dirs:
non-atomic `File.WriteAllText`, no cross-process lock (races Claude Code's own `settings.json` rewriter), drops
comments/trailing-commas. A real plugin's own `hooks.json` is Perch-owned (no user content), which would
dissolve those. Spike (isolated, pinned `CLAUDE_CONFIG_DIR`) must answer:

1. **Multi-dir enablement cost** — `enabledPlugins` is per-`settings.json`, so a plugin still touches each dir
   to enable. Is a one-time enable write meaningfully smaller/safer than re-reconciling 8 hook entries each
   launch?
2. **Tray auto-launch survives** — the current hook uses `UseShellExecute=true` so the tray doesn't inherit the
   hook's stdout pipe and hang the first prompt. Confirm a plugin-delivered hook can still launch the tray.
3. **No double events** — the reason `PluginManager.MigrateOffPlugin` exists.
4. **Stable binary path** — confirm `${CLAUDE_PLUGIN_ROOT}` removes the `%APPDATA%\Perch\bin\perch-hook.exe`
   copy-if-newer dance.
5. **Marketplace install mechanics** — global vs per-dir, network/subprocess cost, non-interactive install.

**Go** if 1 shows a real reduction in write surface and 2–4 hold. **No-go** → keep self-managed delivery but
land the cheap hardening regardless: **atomic temp+rename writes** and a **cross-process mutex** around the
`settings.json` write. Either outcome, B1's capture logic is delivery-agnostic (same `perch-hook` binary
whether via `settings.json` entries or plugin `hooks.json`).

---

## Seams already in place (Layer 1) that this builds on

- `ClaudeConfigDir` / `ClaudeConfigSet` — resolved-real-root identity, `IsMulti`, `ForRoot`/`ForSlug`,
  shared-`sessions/` dedup, the per-dir label/hide maps M3 mirrors.
- `ClaudeSession.AttributedConfigDir` + `SessionMonitor` (`cwd`, session-arrival watch) — the seam M2's
  tray-side warning rides on, no hook needed.
- `IConfigDirSource` — where a scheme declares dirs, and in Layer 2, their declared orgs.
- `SettingsRegistry` / `OverlaySettingsGates` / `render` — the route for any new setting or overlay glyph.
