# Config-dir discovery — implementation plan (Layer 1)

Make Perch aware of **every Claude Code config directory on the machine**, not just the one it
resolved at start-up, so sessions and transcripts are found and attributed correctly wherever
`CLAUDE_CONFIG_DIR` is in play — and do it in a way that does **not** assume any particular
org-separation scheme.

This is **Layer 1 of three** (see [Architecture: three decoupled layers](#architecture-three-decoupled-layers)).
It is deliberately **org-free**. The words "org", "account", "login" and "credential" do not appear
in any type this plan introduces. Live-org and declared-org handling are [Future work](#future-work-layers-2--3-live-org--declared-org),
and the whole point of the decoupling is that Layer 1 ships and is useful on its own.

Prior art reviewed: the external `claude-envs` scheme (see
`.claude/artefacts/claude-config-dir-multi-org.md`) and a contributor PR
(`ArcticGizmo/perch#27`) that solved the same problem but **coupled discovery to org identity and to
one scheme's directory convention**. This plan keeps that PR's good mechanisms (resolved-path dedup,
a hook-written self-report sidecar, per-owner writes) and fixes its two weaknesses: **declaration is
the backbone, not a directory-layout guess**, and **the value model carries no org**.

---

## Architecture: three decoupled layers

| Layer | Owns | Depends on |
| --- | --- | --- |
| **1. Config dirs** (this plan) | Which dirs exist, dedup, finding + attributing sessions/transcripts to a **directory** | filesystem only |
| **2. Orgs** ([future](#future-work-layers-2--3-live-org--declared-org)) | Observed *live* org, *declared* expected org, and the **reconciliation** between them | Layer 1 |
| **3. Opinionated env management** ([future](#future-work-layers-2--3-live-org--declared-org)) | Creating/managing a dir-per-org, launchers, shared-store junctions, `/login` routing | Layers 1 + 2 |

The dependency arrow points one way (3 → 2 → 1). The test of the decoupling: **Layer 1 must never
grow an `Org` property, and nothing here may hard-code `.claude-envs`.** The moment either happens,
the coupling has leaked.

### Design principles (hold these through every milestone)

- **Org-free.** A config dir is an identity-less container. Attribution is to a *directory*, never an
  org.
- **Declaration is the backbone; guessing is a convenience.** Explicit user-declared roots (and, via
  a seam, a scheme's own manifest) are the authoritative source. A convention scan may *add*
  candidates but must never be the *sole* justification for a **write** (see safe-write policy, M4).
- **Never guess an attribution.** An unknown dir resolves to nothing, never to the primary — that
  would attribute a session to whichever dir happens to be first.
- **Dedup by resolved real path.** Config dirs routinely link `projects/` (and, under some schemes,
  `sessions/`) onto one physical tree; enumerate naively and you list every transcript once per dir.
- **Best-effort, never throw out of a scan.** Every disk touch is `try`/`catch`; the floor is "just
  the primary". One unreadable dir must not sink the scan.
- **Hermetic under an explicit `CLAUDE_CONFIG_DIR`.** When the variable is set (tests, replay
  sandbox), the set is pinned to exactly that one dir and discovery does not fan out.

---

## Where this lands in the codebase

Grounded in the current tree (verified during planning):

- `src/Perch.Core/Data/ClaudePaths.cs` — today the single owner of config-dir paths; `ClaudeDir`
  snapshots `CLAUDE_CONFIG_DIR`-or-`~/.claude` once. Becomes a thin delegate to the primary.
- `src/Perch.Core/Data/ClaudeConfigDir.cs` **(new)** — the org-free value type + derived paths.
- `src/Perch.Core/Data/ClaudeConfigSet.cs` **(new)** — the discovered set, dedup, refresh, `Changed`.
- `src/Perch.Core/Data/SessionMonitor.cs` — scans one `SessionsDir`; becomes multi-dir.
- `src/Perch.Core/Data/ClaudeSession.cs` — gains `ConfigDir` (write target) + `ReportedConfigDir`.
- `src/Perch.Core/Data/Control/SessionLock.cs` — `PathFor/Acquire/Release/Read/SweepStale` are
  hard-wired to `ClaudePaths.SessionsDir`; take a per-owner `sessionsDir`.
- `src/Perch.Core/Data/TranscriptLocator.cs`, `TranscriptReader.cs` — search under one `ProjectsDir`;
  become multi-tree.
- `src/Perch.Core/Data/Control/TranscriptImages.cs`, `Replay/RecordingExporter.cs`,
  `SessionTerminator.cs` — per-session write paths that must target the owning dir.
- `src/Perch.Hook/Program.cs` — writes `.mode`/`.notify` sidecars; add the `.configdir` writer.
- `src/Perch.App/Services/HookInstaller.cs` + `src/Perch.Core/Data/ClaudeUserSettings.cs` — install
  the hook into one settings file; fan out over the set (safely).
- `src/Perch.App/Views/OverlayCanvas.cs` + `Rendering/HeadlessRenderer.cs` + `Rendering/SampleData.cs`
  — the per-session directory chip.
- `src/Perch.Core/Data/AppSettings.cs` + `SettingsRegistry` — the declared-dirs list + its editor.
- `tests/Perch.Tests/` — fixtures under `fixtures/claude/`; `TestEnvironment.cs` sets
  `CLAUDE_CONFIG_DIR`.

### Testing strategy

- Extend the fixture tree so a `CLAUDE_CONFIG_DIR`-pinned run can present **several** synthetic config
  dirs (an env root with a couple of slugs, a `.claude` sibling, a shared-`sessions/` layout).
- **Inject the link resolver.** Real junctions need elevation on Windows and won't exist in CI, so
  `Discover(...)` takes a `Func<string,string>` link resolver; tests exercise dedup by mapping two
  fixture paths onto one "real" path without minting a junction.
- A test seam on the set (`SetForTesting`) mirrors the existing `Clock` precedent so multi-dir logic
  can be driven deterministically.
- UI (the chip) has no automated coverage: verify via `dotnet run … -- render <dir>` per the repo
  convention.

---

## Milestones

Each milestone is a **vertical, shippable slice**: the app builds, all tests pass, and nothing is
half-wired. Ship order is M0 → M5.

### M0 — The config-dir value model (invisible refactor)

**Goal.** Introduce the org-free value model and route the existing single-dir paths through it, with
**zero behaviour change**.

**Phases.**
1. Add `ClaudeConfigDir` (record): `Root`, `RealRoot` (links resolved, for dedup), optional `Slug`,
   `Label` (derived: slug → dir name), and the derived paths (`SessionsDir`, `ProjectsDir`,
   `PluginsDir`, `DaemonDir`, `DaemonRosterFile`, `CredentialsFile`, `UserSettingsFile`,
   `ImageCacheDir`). Equality + hash **by `RealRoot` only**. Case sensitivity per-OS
   (`Ordinal` on Linux, `OrdinalIgnoreCase` elsewhere). **No `Org`/`Account`/`OrgState`.**
2. Add `ClaudeConfigSet` with `Primary` only: snapshot `CLAUDE_CONFIG_DIR`-or-`~/.claude` once (as
   `ClaudePaths` does today), `All => [Primary]`, and the `ResolveReal(path)` helper
   (`Directory.ResolveLinkTarget(returnFinalTarget: true)`, best-effort).
3. Refactor `ClaudePaths` so every member delegates to `ClaudeConfigSet.Primary.*`. Public surface
   unchanged.

**Files.** `ClaudeConfigDir.cs` (new), `ClaudeConfigSet.cs` (new), `ClaudePaths.cs`.

**Tests.** Value semantics; dedup equality by `RealRoot`; `ResolveReal` on an ordinary dir returns
itself; `ClaudePaths.*` still resolve to the same strings as before (golden).

**Exit.** No user-visible change. Full suite green. The seam exists.

---

### M1 — Discover a *set* of config dirs (declaration-first)

**Goal.** `ClaudeConfigSet.All` becomes a real, deduped set — but nothing consumes the extra dirs
yet, so this ships as a safe no-op observable only in a debug/list surface.

**Phases.**
1. `AppSettings.DeclaredConfigDirs` (`List<string>`) — the backbone source.
2. `ClaudeConfigDiscovery.Discover(home, primary, declaredRoots, linkResolver?)` builds the ordered,
   deduped set from, in priority order: **primary → declared roots → convention scan**. Dedup via a
   `RealRoot`-keyed dictionary, primary first.
3. Convention scan (the *convenience* tier, marker-gated by `LooksLikeConfigDir`): `~/.claude*`
   siblings of home, and a manifest-declared env root **through a pluggable seam** (`IConfigDirSource`)
   — claude-envs's `manifest.json`/`envs.json` is the first such source, contributing **labels only**,
   never membership or org.
4. `LooksLikeConfigDir`: any one of `sessions/` exists, `.claude.json` exists, or
   `settings.json` + `projects/` both exist. Siblings-only for the home scan — do **not** descend into
   children (that is what matched a scheme's shared store during the PR's development).
5. Throttled refresh (`RefreshIfStale`, ~5s floor) + a `Changed` event. **Pinned
   `CLAUDE_CONFIG_DIR` ⇒ `All = [Primary]`, discovery disabled** (hermetic).
6. `DistinctProjectsDirs()` and `DistinctSessionsDirs()` — dedup by resolved real path.

**Files.** `ClaudeConfigSet.cs`, `ClaudeConfigDiscovery` (in the same file or a sibling),
`AppSettings.cs`, plus a tiny internal `list` debug affordance.

**Tests.** Discovery from declared roots; declared + convention union deduped; marker gate accepts/
rejects the right shapes; siblings-scan does **not** match a shared store; pinned collapses to one;
link-resolver injection proves junction dedup without a real junction.

**Exit.** The set is populated and correct; the rest of the app still reads only the primary. No
regression.

---

### M2 — Scan and attribute sessions across the whole set

**Goal.** The user-visible win: sessions from **every** config dir in the set appear, and every
per-session write lands in the **owning** directory. Attribution here is by **directory** (correct
for the common case where each dir has its own `sessions/`).

**Phases.**
1. `ClaudeSession.ConfigDir` (init-only) — the dir a session's sidecars were found in = the write
   target. `SessionsDir => ConfigDir?.SessionsDir ?? ClaudePaths.SessionsDir`.
2. `SessionMonitor.Scan` iterates `DistinctSessionsDirs()`, reading `{pid}.json` from each, tagging
   each `ClaudeSession` with its `ConfigDir`, and keeping a `sessionId → ClaudeConfigDir` map so the
   id-keyed public entry points still resolve the owning dir. Reconcile per-pid state against the
   **union** of dirs, not whichever was scanned last.
3. Route per-session writes to `ConfigDir`:
   - `SessionLock.PathFor/Acquire/Release/Read/HeldByOther` gain an optional `sessionsDir`;
     `SweepStale` loops `DistinctSessionsDirs()`.
   - `ToggleExternalNotify`, `SessionTerminator`, `TranscriptImages`, `RecordingExporter` write into
     the owning dir (add a `SessionsDirFor(sessionId)` accessor on the monitor).
4. `TranscriptLocator`/`TranscriptReader` search `DistinctProjectsDirs()`.
5. Subscribe the monitor to `ClaudeConfigSet.Changed` (a dir appearing ⇒ rescan); attach a
   `FileSystemWatcher` per distinct sessions dir, lazily (an env's `sessions/` is created only when
   first used).

**Files.** `SessionMonitor.cs`, `ClaudeSession.cs`, `Control/SessionLock.cs`, `SessionTerminator.cs`,
`Control/TranscriptImages.cs`, `Replay/RecordingExporter.cs`, `TranscriptLocator.cs`,
`TranscriptReader.cs`.

**Tests.** `MultiConfigDirSession` fixtures: sessions in two unshared dirs both listed; a lock/notify
written for a non-primary session lands in that dir and is read back; sweep cleans every dir;
transcript found in a non-primary projects tree.

**Exit.** With per-dir `sessions/` (unshared), everything works end to end: list, attribute, lock,
terminate, notify, transcripts.

---

### M3 — Self-reported attribution for a **shared** `sessions/`

**Goal.** Handle the case a directory-based attribution cannot: a scheme that junctions `sessions/`
across dirs, so every dir's sidecars land in one physical folder and the folder can no longer say who
ran a session (claude-envs's default). Still org-free — we report the **directory**, not an org.

**Phases.**
1. Hook: `WriteConfigDir` writes `{sessionId}.configdir` (containing the resolved config dir) on
   `SessionStart`, beside the existing `.mode` write. `start` only (covers resume), so the hot path
   stays one write. Add `.configdir` (and the legacy `.slug`) to hook cleanup.
2. `ClaudeSession.ReportedConfigDir` (from the sidecar) + `EnvDir => ClaudeConfigSet.ForRoot(reported)`.
3. `ClaudeConfigSet.SharesSessionsDir(dir)` — true when another dir resolves to the same real
   `sessions/`. Cache the derivation against set identity.
4. `AttributedConfigDir => EnvDir ?? (ConfigDir when its sessions/ is NOT shared)`. The fallback keeps
   the common unshared case (from M2) attributing by path; the reported marker rescues the shared
   case. `ForRoot`/`ForSlug` **never** fall back to the primary.
5. Attribution is **forward-only**: a session already running when the hook lands has no marker and no
   chip until it turns over. Accept the legacy `.slug` marker too, so an in-flight session that only
   has the old one still attributes.

**Files.** `Perch.Hook/Program.cs`, `ClaudeSession.cs`, `ClaudeConfigSet.cs`, `SessionMonitor.cs`
(read the marker during `ReadSession`).

**Tests.** Drive the hook directly for both cases (a launcher-style dir and a bare `~/.claude`); two
sessions in one **shared** `sessions/` under different dirs, each attributed correctly; the shared-
sessions detection behind the fallback (falsifiable — stub it and confirm the test fails).

**Exit.** Correct attribution under both shared and unshared `sessions/`.

---

### M4 — Install the hook into the set (safe-write policy) + persistence

**Goal.** Sessions in other dirs get the **full** sidecar treatment (mode badge, sub-agent/teammate
alerts, cleanup), and the set survives restarts — without ever writing into a directory we merely
guessed at.

**Phases.**
1. `HookInstaller.ReconcileAll` fans `ClaudeUserSettings.ReconcileHooks(dir.UserSettingsFile, …)` over
   the set, serialised under one gate; `Uninstall` fans `RemoveManagedHooks` over the same set.
2. **Safe-write policy (addresses the guessing concern).** Only **declared** dirs and dirs that have
   **self-reported** (a `.configdir` was seen) are hook-install targets. A dir that entered the set
   *only* from the convention scan is listed and read from, but is **not written to** until it is
   promoted by a self-report or a user declaration. Encode this as a `Provenance`
   (`Primary`/`Declared`/`SelfReported`/`Convention`) on `ClaudeConfigDir`, and gate writes on it.
3. `WatchForNewConfigDirs`: re-reconcile on `ClaudeConfigSet.Changed`, on the thread pool. Note the
   irreducible race (a session already running in a brand-new dir when hooks land picks them up only
   on its next start).
4. Persist the discovered set (sticky memory) so a dir revealed once (declared or self-reported)
   survives the session ending and the tray restarting, rather than being re-guessed each launch.

**Files.** `HookInstaller.cs`, `ClaudeUserSettings.cs`, `ClaudeConfigDir.cs` (Provenance),
`ClaudeConfigSet.cs` (persistence).

**Tests.** Reconcile writes to each **eligible** dir; a convention-only dir is **not** written to;
uninstall fans out; a self-report promotes a convention dir to writable; persistence round-trips.

**Exit.** Full sidecar behaviour across eligible dirs; no write into a merely-pattern-matched dir.

---

### M5 — Surface it: the directory chip + the declared-dirs editor

**Goal.** Make it visible and manageable — still labelled by **directory**, not org.

**Phases.**
1. Overlay: a per-session chip showing `AttributedConfigDir?.Label` (slug/dir name), only when
   `ClaudeConfigSet.IsMulti`. Follow the existing owner-drawn chip patterns; size text from font
   line-height (per the repo's owner-drawn text rule).
2. `SampleData` + `HeadlessRenderer` coverage for the multi-dir chip; verify via `render`.
3. Settings: a **declared-config-dirs editor** — a unique-editor page (the Quick Links page is the
   precedent, not a registry toggle): add/remove/browse a dir, show which entries are declared vs
   auto-discovered vs convention-only, and a validity marker (`LooksLikeConfigDir`). Wire through the
   registry per the settings-are-registry-driven convention (descriptor + `AppSettings` property +
   coverage test).

**Files.** `OverlayCanvas.cs`, `Rendering/SampleData.cs`, `Rendering/HeadlessRenderer.cs`,
`SettingsRegistry`, a new settings page, `AppSettings.cs`.

**Tests.** Registry coverage (a descriptor exists for the new setting); render snapshots eyeballed;
declared-dirs round-trip.

**Exit.** A user can declare dirs and see per-session directory labels. **Layer 1 complete.**

---

## Risks & decisions

- **Guessing vs writing.** Resolved by the M4 safe-write policy: read from anything discovered, write
  only into declared/self-reported dirs. This is the concrete answer to "a false positive becomes a
  hook-install target".
- **A globally-pinned `CLAUDE_CONFIG_DIR`** collapses discovery to one dir (hermetic by design). A
  user who binds `~/.claude` to one env in `$PROFILE` would see only that dir; document it, and note
  that declared roots still can't be scanned while pinned — a later toggle could opt back in if anyone
  hits it.
- **Cross-machine / WSL** slugs don't line up (absolute-path derived); out of scope, same as
  claude-envs.
- **macOS** file-vs-Keychain credential reads are a Layer 2 concern and stay out of this plan.

---

## Future work (Layers 2 & 3): live org & declared org

Deliberately **not** in this plan. Captured here so the Layer 1 seams above are shaped to receive it
without a rewrite.

### The conceptual rule that governs both layers

**A config dir does not *have* an org.** It has whatever org the credential currently in it is signed
into, and `/login` can change that at any moment. So there are three distinct facts, and only one of
them is ever *asserted* rather than *observed*:

- **Live org** — what the credential is signed into *right now*. Observed from `.claude.json`
  `oauthAccount` (`organizationUuid` for identity, `organizationName` for display). Mutable; re-read,
  never cached as identity. **Truth for the directory.**
- **Declared org** — what the user *intends* a dir to be. An assertion Perch (or a scheme manifest)
  stores. **Intent.**
- **Captured org** — what a *session* actually ran under, recorded at start-time. **Truth for the
  session**, immune to a later `/login`.

The valuable feature is the **reconciliation** (`Match` / `Mismatch` / `NotSignedIn` / `Unknown`),
not a static "dir X = org Y". The mismatch *is* the product: it catches the exact footgun — you
`/login`'d a dir into the wrong org and nothing else would tell you.

### Layer 2 — org observation & reconciliation (org-aware, scheme-agnostic)

- An `IOrgProvider` / org-reconciliation service, **outside** Layer 1, that reads a
  `ClaudeConfigDir`'s live org (`.claude.json`), holds an optional declared org per dir, and computes
  `OrgState`. Key orgs by **`organizationUuid`**, not name (names duplicate and change).
- Model an **org** as a first-class entity (`{ uuid, displayName, expectedAccountEmail }`); a dir
  *currently manifests* an org (possibly a different one than declared, possibly the same as another
  dir — "Hub · Acme" vs "Acme · Acme").
- **Capture org at session start.** Extend the hook's `.configdir` write (M3) to also stamp the live
  org uuid+name into the session sidecar, so a session's org is a recorded fact, not a live lookup on
  a dir that may since have been re-logged-in. This is the piece that fully dissolves the "/login
  elsewhere" problem.
- The overlay chip then shows the **live** org as truth and a **warning** when live ≠ declared —
  never the declared org as if it were fact.
- Verify the macOS file-vs-Keychain question here (per-`CLAUDE_CONFIG_DIR` Keychain isolation is
  **unverified** — see the apiKeyHelper/credentials spike notes).

### Layer 3 — opinionated env management (Perch as the claude-envs)

- Create/manage a dir-per-org, generate launchers/shims, junction the shared stores, route `/login`
  into the chosen dir, run the org-mismatch doctor, and — the end state — **Perch as the launcher**:
  inject `CLAUDE_CONFIG_DIR` at `ClaudeSessionController.Start` (`psi.Environment[…]`,
  `UseShellExecute=false`) and pass the env's `sessionsDir` to `SessionLock.Acquire` (the parameter
  M2 adds). When Perch launches, discovery is moot for that session — Perch already knows the dir.
- **apiKeyHelper is not a mechanism here** — spiked and confirmed API-key-mode only, unusable for
  SSO/OAuth orgs (see [[claude-apikeyhelper-oauth-spike]]). Org selection for OAuth stands on
  `CLAUDE_CONFIG_DIR` + `CLAUDE_CODE_OAUTH_TOKEN`.

### Seams to keep clean for the above

- Layer 1 types stay org-free; `ClaudeConfigDir` never gains `Org`.
- The `IConfigDirSource` manifest seam (M1) is where a scheme declares dirs *and*, in Layer 2, their
  declared orgs — so claude-envs stays one provider among others, never hard-coded.
- The `.configdir` sidecar (M3) is the extension point for the start-time org capture.
