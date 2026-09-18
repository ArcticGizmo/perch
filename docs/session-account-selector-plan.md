# Session account selector

Pick which Claude account a Perch-*controlled* session runs under, right in the new-session launcher.
This is the first concrete slice of config-dir **Layer 3** ("opinionated env management",
`docs/config-dir-plan.md` §332-341) / org-discovery **M3** (`docs/org-discovery-plan.md`).

## What it does

- The new-session launcher shows an **optional account selector** built from the accounts found across the
  discovered config dirs (`ClaudeConfigSet.Instance.All`, each dir's live sign-in read from `.claude.json`).
  Accounts are **de-duplicated** — two config dirs signed into the same org (or the same personal email) collapse
  to one choice, with the primary-first dir as the representative.
- The default account is shown by **name with a `(default)` marker** (never the bare word "Default"). The marker
  tracks **only the account tied to the primary config dir** — a guardrail-forced pick is *not* labelled default.
  Selecting the default primary account still means "inherit the environment" (no injection), preserving
  today's behaviour.
- When a guardrail leaves **exactly one** option there is nothing to change, so the selector is **read-only**: it
  shows a **lock icon** (tooltip *"restricted by account guardrails"*) instead of the chevron and doesn't open a
  menu.
- A **footer account chip** sits beside the git-branch chip in the composer, naming which account the running
  session is under (shown only on multi-account machines, where it's not redundant).
- If an **account guardrail** (`AppSettings.AccountRules`) governs the chosen folder, the selector is
  **restricted** to the accounts that rule allows. If the rule allows exactly one, that one is the default.
- Selecting an account launches `claude` with `CLAUDE_CONFIG_DIR` pinned to that config dir's `Root`, so the
  CLI reads that dir's `.credentials.json` and runs under that account. `CLAUDE_CONFIG_DIR` alone is
  sufficient — no OAuth token injection needed (each dir carries its own credentials).

### Behaviour (as shipped)

| Situation | Selector | Default |
|---|---|---|
| No guardrail, **one** config dir | hidden | inherit current environment (unchanged behaviour) |
| No guardrail, **multiple** config dirs | shown, lists all + "Default (current environment)" | **inherit** |
| Guardrail allows **one** account | shown, that account only | **that account** |
| Guardrail allows **several** | shown, restricted to those (primary-first) | first (primary if it qualifies) |
| Guardrail matches but **no signed-in dir qualifies** | shown with a warning | inherit (guardrails are alerting-only, never block) |

Decisions taken (confirmed with the user):

- **New sessions only.** A resume/elevate must run under the dir that owns the transcript, so it keeps
  inheriting Perch's environment (no injection). The selector lives in the launcher chrome, which the resume
  paths never build.
- **Default = inherit** when nothing governs the folder (fully backward-compatible).
- **Hidden** when there is only one account and no guardrail (nothing to choose).

## Implementation

**Core (pure, unit-tested):**
- `Perch.Core/Data/SessionAccountChoice.cs` — `AccountChoice(Dir, Org?, Email?)` (with a dedup `Key` and a
  display `Label`), `AccountChoiceSet` (carrying `Default`, `Primary`, and `InjectRootFor(choice)` — the
  inherit-vs-pin decision), and the pure `SessionAccountChoice.Resolve(cwd, signIns, rules)` which de-duplicates
  then reconciles against `AccountGuard.RuleFor`. All IO (reading each dir's sign-in) is the caller's.
- `ClaudeSessionController.Start(...)` — new `string? configDir` param; when set, injected as
  `psi.Environment["CLAUDE_CONFIG_DIR"]` (never the command line, so no injection surface).
- `SessionLaunchOptions` — new `ConfigDir` field; `PerchSession.Start` threads it to the controller.

**UI (`SessionWindow`):**
- An account pill + warning line in the launcher chrome (`BuildLauncher`), hidden until `RefreshAccounts`
  decides there's a choice. Sign-ins are read **off the UI thread** (`Task.Run` → `Dispatcher.Post`) with a
  generation guard; re-resolved on every folder change (guardrails are cwd-dependent).
- `EffectiveConfigDir(cwd)` resolves synchronously at `StartSession`, so a single-allowed guardrail is
  enforced as the default even on the launcher-less CLI open. A user's explicit pick wins if still valid.
- `AccountRulesProvider` delegate, wired by `App.NewSessionWindow` to `() => _appSettings.AccountRules`
  (the app owns `AppSettings`).
- **Footer account chip** (`_accountChip`, beside `_branchPill`): `PerchSession.ConfigDir` carries the launched
  dir (null = inherit → primary); `RefreshAccountChipAsync` reads that dir's org off the UI thread on attach and
  paints the chip, hidden unless `ClaudeConfigSet.Instance.IsMulti`.
- The composer's chip row is a **`WrapPanel`** (was a horizontal `StackPanel`), so a crowded footer flows onto a
  second line instead of clipping. Each chip carries its own 8px right / 6px bottom gap (WrapPanel has no
  `Spacing`); the panel's `-6` bottom margin absorbs the trailing row so a single row keeps its height, and the
  send button is top-aligned to the first row. Render-verified (`session_window` capture).

**Tests:** `tests/Perch.Tests/SessionAccountChoiceTests.cs` — the table's cases plus de-duplication (same org
across dirs; personal accounts by email) and the inherit-vs-pin `InjectRootFor` decision. Pure/no-IO.

## Nice side effect

New controlled sessions launched via the selector satisfy the M2 mismatch check by construction, so the
pulsing red "wrong account" outline won't fire on them.

## Not in scope / follow-ups

- **Resume/elevate** still inherits the environment (owner dir); a future pass could auto-pin the config dir
  that owns the resumed transcript (and fix cross-dir resume-transcript reads).
- `SessionLock.Acquire(id, cwd)` still takes only `(id, cwd)`; the plan's `sessionsDir` parameter is not
  added here. The chosen dir's session sidecars still land under a dir Perch already monitors
  (`DistinctSessionsDirs`), so the overlay sees the session.
- Personal (no-org) accounts can't be named by a guardrail (rules key on `organizationUuid`), matching the
  existing M2 behaviour.

## Status

Shipped as code (uncommitted). Build clean, 1186 .NET tests green. The launcher selector and the footer account
chip are **interactively unverified** — they only surface with 2+ signed-in accounts or a matching guardrail,
which the headless render/sample data doesn't exercise.
