# Distribution & code-signing plan

> **Status: step 1 (Scoop) implemented 2026-07-29; the rest is still proposed.** The goal is to kill the
> "Windows protected your PC" SmartScreen wall on first install without paying EV-certificate
> prices and without moving Perch off .NET/Avalonia.
>
> **Done:** the release workflow ships `Perch-win-Portable.zip` and pushes a rendered manifest to the
> bucket repo (`packaging/scoop/perch.json` → `ArcticGizmo/scoop-arcticgizmo`); the app knows which channel
> it came from (`Perch.App/Services/InstallChannel.cs`) and hands the apply step back to Scoop; `perch
> uninstall` gives Scoop the same teardown the Velopack uninstaller runs. Remaining before the channel is
> live: create the bucket repo and add the `SCOOP_BUCKET_TOKEN` secret (see README → *The Scoop bucket*).
>
> **Next:** npm `perch-tray` bootstrapper, then signing, then WinGet.

---

## TL;DR

- SmartScreen's dialog is triggered by **Mark-of-the-Web** — the tag a *browser* puts on downloads.
  Package managers (scoop, winget, npm) and Velopack's own updater don't tag files, so **this is a
  first-install-only problem**. In-app updates already sail past it.
- Reputation accrues to the **signing identity**, so staying unsigned means every release restarts
  from zero, forever. Signing once makes the warning fade permanently.
- **Azure Trusted Signing is US$9.99/month** and Microsoft-operated — no HSM token, no audit. That's
  the cheap answer to "code signing is outrageously expensive". **SignPath Foundation is free** for
  OSS projects (Perch is MIT + public, so it qualifies) if Trusted Signing eligibility blocks us.
- **npm distribution does not require moving to JS.** A ~30-line bootstrapper package is enough.
- **Do not rewrite in Electron/JS.** It buys nothing that a package-manager wrapper doesn't, and
  costs the Win32 interop (mic presence, Teams control, overlay) that makes Perch what it is.

Recommended order: **Scoop bucket → npm wrapper → Trusted Signing (or SignPath) → WinGet.**

---

## How the warning actually works

Load-bearing mechanics, because they decide which channels help:

1. A browser download gets a `Zone.Identifier` alternate data stream — **Mark-of-the-Web** (MotW).
2. On execution, MotW sends the file to SmartScreen's **application reputation** service.
3. Unknown hash + no publisher identity ⇒ the full-screen blue dialog with *More info → Run anyway*.
4. Reputation is keyed on **both file hash and signing certificate**. A signed build inherits the
   publisher's accumulated reputation; an unsigned build has only its own hash, which is new every
   single release.
5. Files written by a non-browser process (`npm`, `scoop`, `winget`, `curl`, **Velopack's updater**)
   generally carry no MotW, so step 2 never happens.

Two consequences:

- Perch's existing in-app update path is almost certainly already warning-free. Only the initial
  `PerchSetup.exe` download hurts.
- "Signing doesn't help until lots of people download it" is true for OV certs, but the reputation
  is **cumulative and permanent** once earned — not re-earned per release, which is our current
  situation.

**Caveat for every no-cert option below:** they bypass the *dialog*, not Microsoft Defender. An
unsigned self-contained single-file .NET binary does occasionally get heuristically flagged. Only
signing addresses that.

---

## Option matrix

| Option | Cost | Publish latency | Kills SmartScreen? | Notes |
| --- | --- | --- | --- | --- |
| Azure Trusted Signing | $9.99/mo | none (CI) | Fast, not instant | Eligibility + region check needed |
| SignPath Foundation | free (OSS) | none (CI) | Builds over time | OV-level; named publisher |
| Certum open-source cert | ~€25–70/yr | none (CI) | Builds over time | Cheapest paid; smartcard/cloud |
| EV certificate | $250–400/yr | none (CI) | Historically instant | Used to be a guaranteed pass; no longer |
| Scoop (own bucket) | free | instant | N/A — no MotW | Highest value per hour of work |
| npm | free | instant | N/A — no MotW | Audience already has Node |
| WinGet | free | hours | N/A — no MotW | Auto-submittable from CI |
| Microsoft Store | $19 once | days | Yes (MS signs) | Packaged MSIX can't self-update |

---

## Track A — signing

### A1. Azure Trusted Signing (preferred)

Microsoft runs the CA and the HSM, so there's no hardware token and no annual audit. Certificates
chain to a Microsoft root that SmartScreen sees constantly, so reputation builds considerably faster
than with a fresh third-party OV cert.

**Verify before committing:**

- **Eligibility** — a legal entity in good standing for **3+ years**, or an individual passing
  identity validation. Quartex Software may qualify as the org.
- **Region availability** for identity validation started US/Canada-centric and expanded over time.
  **Confirm Australia is covered** — this is the most likely blocker.

**Mechanics:** certificates are short-lived (~3 days), so signing must happen in CI. That's already
where we build. Wiring:

1. Azure subscription → Trusted Signing account → certificate profile.
2. GitHub Actions OIDC federated credential (no long-lived secret).
3. `azure/trusted-signing-action`, or `AzureSignTool` invoked directly.
4. Sign **every** executable, not just the installer: `perch.exe`, `perch-hook.exe`, and the
   `PerchSetup.exe` Velopack generates. Velopack's `vpk pack` accepts signing parameters and has a
   custom-command template escape hatch for non-`signtool` signers — check `vpk pack --help` for the
   flag name in our pinned version, since it has changed across releases.
5. Timestamp everything (mandatory here — the cert outlives its validity only via the timestamp).

Because signing happens inside `release.yml`, `publish.bat` local builds stay unsigned. That's fine;
just don't hand out locally built installers.

### A2. SignPath Foundation (free fallback)

Perch is MIT-licensed and public at `ArcticGizmo/perch`, which is what SignPath Foundation's OSS
programme wants. They supply both the certificate and the signing service, with CI integration.
It's OV-level, so SmartScreen reputation still has to build — but the publisher becomes *named*
rather than *unknown*, and the reputation accumulates across releases instead of resetting.

### A3. Certum open-source certificate

Tens of euros per year rather than hundreds. Same OV reputation-building caveat. Worth it only if
both A1 and A2 fall through.

### Rules that apply to whichever we pick

- **Never rotate the certificate** unless forced. Reputation lives on the identity.
- Sign every artifact in every release. One unsigned binary in the bundle re-opens the hole.

---

## Track B — channels that need no certificate

### B1. Scoop bucket — **implemented**

We own the bucket repo, so publishing is a JSON commit: zero review, zero delay, and the audience
(developers running Claude Code) already has Scoop or can get it in one line.

```
scoop bucket add arcticgizmo https://github.com/ArcticGizmo/scoop-arcticgizmo
scoop install perch
```

How it landed:

- `release.yml` no longer excludes `*-Portable.zip`, so Velopack's portable payload is a release asset
  with a stable URL (`…/releases/download/v<ver>/Perch-win-Portable.zip`).
- `packaging/scoop/perch.json` is the manifest template; `scoop-publish.yml` fills in `version`, `url` and
  the zip's SHA-256 with `jq` and commits it to `<bucket>/bucket/perch.json`. Missing `SCOOP_BUCKET_TOKEN`
  ⇒ warn and skip on the automatic path (a release must never fail over the bucket), hard-fail on a manual
  one.
- **Recovery without hand-editing the bucket:** `scoop-publish.yml` is a reusable workflow with both
  `workflow_call` (release.yml invokes it after the release exists) and `workflow_dispatch` (Actions → run
  it against any tag). It hashes the zip from the *published release*, not a build artifact, so a repair run
  works after artifact retention has expired and can also repoint the bucket at an older release. Rendering
  is deterministic per tag and an unchanged manifest pushes nothing, so re-running is always safe.
- `bin`/`shortcuts` both point at the portable root's `Perch.exe` — Velopack's stub, which forwards
  args to `current\perch.exe` (verified). Scoop marks the shim as a GUI binary, so no console flashes.
- `checkver`/`autoupdate` are kept as a safety net for when a manual release skips CI.
- **Update-path conflict:** solved by `InstallChannel` (below) — a Scoop copy never self-updates.
- **`scoop uninstall` parity:** `pre_uninstall` stops the Scoop copy (matched by path, so a separately
  installed Perch is never touched) and, on a real uninstall only (`$cmd -eq 'uninstall'`, so updates
  keep their hooks), runs `perch uninstall` — the same teardown as Velopack's uninstall callback:
  PATH entry, login registration, managed hooks.
- **Versioned app dirs:** `scoop update` installs into a new `apps\perch\<version>\` and deletes the old
  one, which would strand the `perch.path` breadcrumb `perch-hook` self-heals against (it strips our hooks
  when the tray binary is gone). `HookInstaller` now records the path through Scoop's stable `current`
  junction instead, so hooks survive an update.

### B2. npm — no JS rewrite required

Perch's audience is Claude Code users; Node is effectively guaranteed. `perch` itself is **taken on
npm** (a dead 2016 stub, v1.0.0, no description — disputing it is slow and uncertain), so:

- **`perch-tray`** — available, and works as bare `npx perch-tray`. Recommended.
- **`@arcticgizmo/perch`** — also available, if we'd rather be scoped.

Two possible package designs:

**Design 1 — bootstrapper (recommended).** The npm package contains only a small JS shim. On first
run it downloads `PerchSetup.exe` for its own version from the GitHub release, verifies a SHA-256
baked into the package at publish time, and executes it. Velopack then owns the install and all
subsequent updates exactly as it does today.

- Package stays a few KB; no per-platform sub-packages; no duplicated update mechanism.
- npm/Node does the downloading, so no MotW, so no SmartScreen dialog.
- Put this behind the `bin` shim rather than a `postinstall` hook — surprise side effects during
  `npm install` are rightly unpopular, and postinstall failures are opaque.
- Trade-off: needs network at first run, and it *is* slightly unusual for an npm package to hand off
  to a native installer. Print clearly what it's doing.

**Design 2 — payload packages (esbuild pattern).** `perch-tray` declares
`optionalDependencies` on `@perch-tray/win32-x64` and `@perch-tray/darwin-arm64`, each gated by
`os`/`cpu` so npm downloads only the matching one; each carries the existing self-contained publish
output; the shim `spawn`s the binary.

- No network at first run; fully offline-installable; the same trick works on macOS, where
  npm-written files aren't Gatekeeper-quarantined — which would also retire the `xattr -cr`
  instructions in the README.
- Trade-off: a self-contained Avalonia publish is a chunky npm tarball, and it's a second, parallel
  update channel to keep straight.

Start with Design 1. Revisit Design 2 if offline installs or the macOS quarantine story become the
priority.

### B3. WinGet

Free, and the manifest submission can be automated from `release.yml` with a
`winget-releaser`-style action. Latency is hours, not the Store's days. Unsigned installers are
accepted but attract heavier validation scrutiny, so this pairs best with Track A being done first.

---

## Update ownership (the one real design wrinkle) — **implemented**

`Perch.App/Services/InstallChannel.cs` classifies the running copy once, from Velopack's own
`UpdateManager.IsInstalled` / `IsPortable` plus a Scoop-shape probe (an `install.json` in an
`…\apps\<app>\<version>\` directory):

| Kind | Detected by | Checks | Applies |
| --- | --- | --- | --- |
| `Setup` | installed, not portable | yes | yes — Velopack, as before |
| `Scoop` | portable + Scoop's `install.json`/`apps` shape | yes | no — surfaces `scoop update perch` |
| `Portable` | portable, unrecognised location | yes | no — "download the new release" |
| `Unpackaged` | not installed (dev run) | no | no |

The useful surprise: Velopack's *portable* layout resolves a version and an update feed perfectly well, so
**checking works on every channel**. Only the apply step is channel-specific, which is what makes the Scoop
experience decent rather than blind: the badge lights up, the toast names the command, and Settings → About
offers a button to copy it. The apply entry point (`UpdateService.PerformUpdate`) is unchanged for callers —
the tray item, overlay badge and toast all still route there; it just hands the user the command instead of
rewriting Scoop's directory. `CheckManual` on a dev run says so plainly rather than reporting a failure.

Channel-by-channel ownership:

| Channel | Install shape | Who updates |
| --- | --- | --- |
| `PerchSetup.exe` | Velopack, `%LocalAppData%\Perch\` | Velopack in-app |
| npm (Design 1) | hands off to `PerchSetup.exe` | Velopack in-app |
| Scoop | portable zip in the Scoop dir | `scoop update perch` |
| WinGet | `PerchSetup.exe` | Velopack in-app |

`perch-hook` itself is unaffected — the app copies it to a stable per-user path on launch, so portable
installs wire hooks correctly; only the tray-path breadcrumb needed the junction fix described above.

---

## Rejected options

- **Microsoft Store.** The one channel that truly eliminates the warning, since Microsoft signs the
  MSIX. Rejected for the reason already identified: per-update certification latency throttles
  release cadence. The tempting hybrid — Store as trust anchor, Velopack for cadence — **does not
  work**: packaged MSIX apps can't self-update outside the Store.
- **Rewriting in JS/Electron.** Would trade an owner-drawn Avalonia tray app with Win32 mic/Teams
  interop for something heavier and slower with worse OS-integration ergonomics for precisely the
  things Perch does — and buys nothing that Track B doesn't deliver in a day. npm distribution and
  implementation language are independent choices.
- **GitHub artifact attestation / SLSA provenance.** Genuine supply-chain value, zero SmartScreen
  effect. Not a solution to this problem.

---

## Sequencing

1. ~~**Scoop bucket**~~ — done in code (manifest template, CI job, channel-aware updater, uninstall
   parity). **Still needs a human:** create `ArcticGizmo/scoop-arcticgizmo` and add the
   `SCOOP_BUCKET_TOKEN` secret; until then the `scoop` job skips with a warning.
2. **npm `perch-tray`** — bootstrapper package, published from `release.yml` on tag.
3. ~~**README**~~ — done for Scoop: `scoop install` leads the Windows section, the raw `.exe` sits
   beneath it, and the Updating section is now per-channel. Revisit when npm lands.
4. **Signing** — check Trusted Signing eligibility/region; if blocked, apply to SignPath Foundation.
   Wire signing into `release.yml`.
5. **WinGet** — once signed.

## Open questions

- Is Quartex Software a registered entity 3+ years old, and is AU in Trusted Signing's identity-
  validation regions? Decides A1 vs A2.
- npm name: `perch-tray` (bare `npx`) or `@arcticgizmo/perch` (scoped)?
- ~~Scoop bucket in a new repo, or a `bucket/` directory in this one?~~ Settled: a separate repo
  (`ArcticGizmo/scoop-arcticgizmo`, manifests under `bucket/`), which is the convention and keeps
  `scoop bucket add` clean.

## Verified locally (2026-07-29)

The Scoop path was exercised end to end before shipping, against a locally packed `0.2.30` portable zip
served over HTTP and installed with the real manifest (as `perch-local`, in Perch's isolated dev profile so
the machine's installed Perch was untouched):

- Extracted layout is what the manifest assumes; the `perch` shim is created as a GUI binary and the
  Start Menu shortcut targets `apps\perch\current\Perch.exe`.
- The running copy classified itself as `Scoop`, checked the feed, and toasted *"Version 0.2.32 is
  available. Perch was installed with Scoop, so run "scoop update perch" to install it."* — Settings →
  About showed the same line with a **Copy "scoop update perch"** button and no *Update now*.
- Launched from the versioned dir, the `perch.path` breadcrumb was still recorded through the `current`
  junction (the `scoop update` survival case).
- `perch uninstall` stripped the hooks, bin dir and login registration for its own profile only;
  `scoop uninstall` ran `pre_uninstall` cleanly and left no shim, shortcut or app dir behind.

## macOS parity

The same first-install problem exists there as Gatekeeper quarantine, and the README currently tells
users to run `xattr -cr`. The real fix is an Apple Developer Program membership (US$99/yr) plus
notarization in `publish-mac.sh` — a separate exercise, but worth noting that npm Design 2 would
defuse it as a side effect, since npm-written files aren't quarantined.
