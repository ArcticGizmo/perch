# Distribution & code-signing plan

> **Status: the `install.ps1` one-liner + published checksums are implemented (2026-07-29). Scoop was
> implemented the same day and then withdrawn — see *Rejected options*.** The goal is to kill the "Windows
> protected your PC" SmartScreen wall on first install without paying EV-certificate prices and without
> moving Perch off .NET/Avalonia.
>
> **Done:** `install.ps1` at the repo root, run as
> `irm https://raw.githubusercontent.com/ArcticGizmo/perch/main/install.ps1 | iex`. It resolves a release
> through the GitHub API, downloads `SHA256SUMS.txt` + `Perch-win-Setup.exe`, verifies the installer's
> SHA-256 and deletes it on any mismatch, then runs it. `release.yml`'s `release` job generates
> `SHA256SUMS.txt` over the exact flattened artifact set it uploads. Because PowerShell (not a browser) does
> the downloading, nothing is mark-of-the-web tagged, so SmartScreen never fires — and because the payload
> is the ordinary Velopack installer, **every update after the first is in-app**, which is the property Scoop
> couldn't give us.
>
> **Next:** signing (the only thing that addresses Defender heuristics and makes the checksums a real
> identity claim), then WinGet. GitHub build-provenance attestation is a cheap intermediate step.

---

## TL;DR

- SmartScreen's dialog is triggered by **Mark-of-the-Web** — the tag a *browser* puts on downloads.
  `Invoke-WebRequest`, `curl`, package managers and Velopack's own updater don't tag files, so **this is a
  first-install-only problem**. In-app updates already sail past it.
- **The install channel must not take over updating.** This is the lesson from the Scoop experiment: a
  channel that owns the app directory means the user has to leave the app and type a command to update,
  which is worse friction than the one-time dialog we were trying to avoid. Prefer channels that *hand off*
  to the Velopack installer and then get out of the way.
- Reputation accrues to the **signing identity**, so staying unsigned means every release restarts
  from zero, forever. Signing once makes the warning fade permanently.
- **Azure Trusted Signing is US$9.99/month** and Microsoft-operated — no HSM token, no audit. That's
  the cheap answer to "code signing is outrageously expensive". **SignPath Foundation is free** for
  OSS projects (Perch is MIT + public, so it qualifies) if Trusted Signing eligibility blocks us.
- **Checksums are not signatures.** They prove integrity of transport, not authorship — see *Checksums*
  below for exactly what they do and don't cover.
- **Do not rewrite in Electron/JS.** It buys nothing that a download wrapper doesn't, and costs the Win32
  interop (mic presence, Teams control, overlay) that makes Perch what it is.

Recommended order: **`install.ps1` → Trusted Signing (or SignPath) → WinGet.**

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
  `Perch-win-Setup.exe` download hurts.
- "Signing doesn't help until lots of people download it" is true for OV certs, but the reputation
  is **cumulative and permanent** once earned — not re-earned per release, which is our current
  situation.

**Caveat for every no-cert option below:** they bypass the *dialog*, not Microsoft Defender. An
unsigned self-contained single-file .NET binary does occasionally get heuristically flagged. Only
signing addresses that.

---

## Option matrix

| Option | Cost | Publish latency | Kills SmartScreen? | Keeps in-app updates? | Notes |
| --- | --- | --- | --- | --- | --- |
| **`install.ps1` one-liner** | free | instant | N/A — no MotW | **yes** | **Implemented.** Verifies, then hands off to Velopack |
| Azure Trusted Signing | $9.99/mo | none (CI) | Fast, not instant | yes | Eligibility + region check needed |
| SignPath Foundation | free (OSS) | none (CI) | Builds over time | yes | OV-level; named publisher |
| Certum open-source cert | ~€25–70/yr | none (CI) | Builds over time | yes | Cheapest paid; smartcard/cloud |
| EV certificate | $250–400/yr | none (CI) | Historically instant | yes | Used to be a guaranteed pass; no longer |
| WinGet | free | hours | N/A — no MotW | yes (ships the installer) | Auto-submittable from CI |
| npm bootstrapper | free | instant | N/A — no MotW | yes (ships the installer) | Audience already has Node; same shape as install.ps1 |
| Scoop (own bucket) | free | instant | N/A — no MotW | **no** | Withdrawn — Scoop owns the app dir, so updates leave the app |
| Microsoft Store | $19 once | days | Yes (MS signs) | **no** | Packaged MSIX can't self-update |

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
   `Perch-win-Setup.exe` Velopack generates. Velopack's `vpk pack` accepts signing parameters and has a
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

### B1. The `install.ps1` one-liner — **implemented**

```powershell
irm https://raw.githubusercontent.com/ArcticGizmo/perch/main/install.ps1 | iex
```

The whole design in one sentence: **the script does nothing but obtain the real installer safely, then get
out of the way.** No app directory of its own, no second update mechanism, no state to keep in sync — the
result is indistinguishable from having double-clicked `Perch-win-Setup.exe`, which is exactly why in-app
updates keep working.

How it landed:

- **Hosting:** `raw.githubusercontent.com/ArcticGizmo/perch/main/install.ps1` — no Pages setup, no DNS,
  live the moment it's on `main`. The trade-offs accepted: a long URL, GitHub's ~5-minute raw CDN cache
  (so a fix to the script isn't instant), and occasional corporate-proxy blocking of `raw.githubusercontent`.
  Moving to GitHub Pages or a custom domain later is a pure URL swap; the script doesn't change.
- **Release resolution** goes through the GitHub API (`/releases/latest`, or `/releases/tags/v<ver>` with
  `-Version` / `$env:PERCH_VERSION`) and both assets are looked up **by name in the release metadata**
  rather than by constructing a URL — so a release missing an asset fails with a clear reason instead of
  hashing a 404 page. `$env:GITHUB_TOKEN` is used if present, purely for the 60-req/hr anonymous limit
  behind shared IPs; it is deliberately *not* sent when fetching assets, because those redirect to a
  pre-signed `objects.githubusercontent.com` URL that rejects requests carrying an `Authorization` header.
- **Verification** is mandatory and fail-closed: no `SHA256SUMS.txt` on the release, or no line in it for
  `Perch-win-Setup.exe`, is an error — never a silent skip, which would defeat the entire point. A mismatch
  deletes the download before reporting, so a bad file can't be run by hand afterwards.
- **Errors use `throw`, never `exit`.** The script normally runs via `iex` *inside the user's own shell*,
  where `exit` would close their session. A terminating error still surfaces as a non-zero exit code under
  `powershell -Command`, so scripted use is unaffected.
- **The download is streamed manually** (`WebRequest.CreateHttp` + a 128 KB read loop + throttled
  `Write-Progress`) rather than using `Invoke-WebRequest -OutFile`: on Windows PowerShell 5.1 IWR buffers the
  whole response in memory and its own progress rendering makes a ~90 MB download crawl. Verified at
  36 MB in 0.7 s over loopback. A stalled connection is bounded by a 60 s per-read timeout.
- **Only `SHA256SUMS.txt` is new on the release side.** No new artifact, no new job — see *Checksums*.

### B2. npm — a second wrapper of the same shape

Still open, and now clearly a *variant of B1* rather than a different strategy: a few-KB `perch-tray`
package whose `bin` shim downloads the release installer, verifies a SHA-256 baked in at publish time, and
executes it. Node is effectively guaranteed in Perch's audience (Claude Code users), npm-written files carry
no MotW, and Velopack still owns updates.

`perch` itself is **taken on npm** (a dead 2016 stub, v1.0.0, no description — disputing it is slow and
uncertain), so the name would be `perch-tray` (works as bare `npx perch-tray`) or `@arcticgizmo/perch`.

Put it behind the `bin` shim, not a `postinstall` hook — surprise side effects during `npm install` are
rightly unpopular and postinstall failures are opaque.

The rejected alternative — **payload packages** (the esbuild pattern: `optionalDependencies` on
`@perch-tray/win32-x64` etc., each carrying a full self-contained publish) — buys offline installs and would
defuse the macOS `xattr -cr` dance, since npm-written files aren't quarantined. It costs a chunky tarball per
platform and a second parallel update channel, which is the mistake Scoop already taught us. Only revisit if
offline install becomes a real requirement.

Given B1 exists and covers the same ground, npm is now low priority.

### B3. WinGet

Free, and the manifest submission can be automated from `release.yml` with a
`winget-releaser`-style action. Latency is hours, not the Store's days. Unsigned installers are
accepted but attract heavier validation scrutiny, so this pairs best with Track A being done first.

---

## Checksums — **implemented**

`release.yml`'s `release` job is the only place `SHA256SUMS.txt` is produced, and it happens *after* both
build jobs, from the flattened set of files it is about to upload. That ordering is the point: a manifest
generated in a build job could describe something other than what ends up on the release. Plain `sha256sum`
format, so `sha256sum -c SHA256SUMS.txt` validates it as-is.

Two guards fail the release rather than publish a subtly wrong manifest:

- a duplicate basename across the Windows and macOS artifact sets (they're currently distinct — Velopack
  channel-suffixes the macOS nupkg, e.g. `Perch-1.2.3-osx-full.nupkg` — but nothing enforced that before);
- a missing `Perch-win-Setup.exe`, since `install.ps1` resolves that asset by name and a release without it
  is uninstallable by the one-liner.

`publish.bat` writes the same manifest for local packs, so a hand-uploaded release still ships checksums.

**What checksums do and don't buy.** They prove the bytes that ran are the bytes that were published: they
catch a truncated download, a proxy or mirror rewriting the payload, and tampering anywhere between GitHub
and the disk. They are **not** a signature — the manifest sits on the same release as the installer, over the
same TLS connection, so an attacker who could replace one could replace both. The trust root here is
"GitHub's TLS and GitHub's account security", not "we vouch for this binary". Two upgrades close that gap:

1. **GitHub build-provenance attestation** (`actions/attest-build-provenance`, needs `id-token: write`) —
   cryptographically signed, verifiable with `gh attestation verify`, free, and a few lines of YAML. It ties
   the artifact to the workflow and commit that built it. It has **zero** SmartScreen effect, which is why it
   was previously filed under *Rejected* — but as a supply-chain claim on top of the checksums it's cheap and
   worth doing.
2. **Code signing** (Track A) — the only thing that makes the publisher a named identity, and the only thing
   that addresses Defender heuristics rather than just the SmartScreen dialog.

---

## Update ownership — **implemented**

`Perch.App/Services/InstallChannel.cs` classifies the running copy once, from Velopack's own
`UpdateManager.IsInstalled` / `IsPortable`:

| Kind | Detected by | Checks | Applies |
| --- | --- | --- | --- |
| `Setup` | installed, not portable | yes | yes — Velopack, as before |
| `Portable` | portable (hand-extracted zip) | yes | no — "download the new release" |
| `Unpackaged` | not installed (dev run) | no | no |

Velopack's *portable* layout resolves a version and an update feed perfectly well, so **checking works on
every channel**; only the apply step is gated. `UpdateService.PerformUpdate` is unchanged for callers — the
tray item, overlay badge and toast all still route there — it just explains the situation instead of
rewriting a directory it doesn't own. `CheckManual` on a dev run says so plainly rather than reporting a
failure.

This got *simpler* when Scoop was withdrawn: there is no longer any channel that installs Perch somewhere a
package manager owns, so the `Scoop` kind, its `install.json`/`apps` shape probe, the `UpdateCommand`
copy-to-clipboard button in Settings → About, and `StableExePath` (which rewrote the `perch.path` breadcrumb
through Scoop's `current` junction to survive versioned app dirs) are all gone. `HookInstaller` records
`Environment.ProcessPath` directly again.

Channel-by-channel ownership — note every live row says the same thing, which is the design goal:

| Channel | Install shape | Who updates |
| --- | --- | --- |
| `install.ps1` | verifies, then runs `Perch-win-Setup.exe` | Velopack in-app |
| `Perch-win-Setup.exe` by hand | Velopack, `%LocalAppData%\Perch\` | Velopack in-app |
| WinGet (planned) | `Perch-win-Setup.exe` | Velopack in-app |
| npm (planned) | hands off to `Perch-win-Setup.exe` | Velopack in-app |
| Portable zip | extracted by hand | the user, by replacing it |

`perch-hook` is unaffected — the app copies it to a stable per-user path on launch, so portable installs wire
hooks correctly.

---

## Rejected options

- **Scoop bucket — built, tried, withdrawn (2026-07-29).** It worked exactly as designed and the design was
  the problem: Scoop owns its app directory, so Perch must not self-update inside it, which means every
  update is "leave the app, open a terminal, type `scoop update perch`". Perch could detect the update and
  even offer to copy the command, but that is *more* friction than the one-time SmartScreen dialog Scoop
  existed to avoid — and it was friction on every single release rather than once. The `install.ps1`
  one-liner gets the same no-MotW first install with none of that, because its payload is the normal
  installer. Removed: `packaging/scoop/`, `.github/workflows/scoop-publish.yml`, the `scoop` job, and the
  `Scoop` branches of `InstallChannel`/`HookInstaller`/`SettingsWindow`. **The general lesson, worth keeping:
  judge a distribution channel on its update story, not just its install story.**
- **Microsoft Store.** The one channel that truly eliminates the warning, since Microsoft signs the
  MSIX. Rejected for the reason already identified: per-update certification latency throttles
  release cadence. The tempting hybrid — Store as trust anchor, Velopack for cadence — **does not
  work**: packaged MSIX apps can't self-update outside the Store. (Same failure mode as Scoop: the
  channel takes updates away from the app.)
- **Rewriting in JS/Electron.** Would trade an owner-drawn Avalonia tray app with Win32 mic/Teams
  interop for something heavier and slower with worse OS-integration ergonomics for precisely the
  things Perch does — and buys nothing that Track B doesn't deliver in a day. npm distribution and
  implementation language are independent choices.
- **GitHub artifact attestation / SLSA provenance as a *SmartScreen* fix.** Still true that it has zero
  SmartScreen effect. But now that checksums are published it's the obvious next increment in the *integrity*
  story, so it's been promoted out of this list — see *Checksums* above.

---

## Sequencing

1. ~~**`install.ps1` + published checksums**~~ — done: script, `SHA256SUMS.txt` in `release.yml`, local
   manifest from `publish.bat`, README leading with the one-liner. **Needs a human:** nothing. It goes live
   with the first tag that publishes `SHA256SUMS.txt`; the script's URL works as soon as it's on `main`.
2. ~~**Scoop bucket**~~ — withdrawn, see *Rejected options*. The `ArcticGizmo/scoop-arcticgizmo` bucket repo
   and any `SCOOP_BUCKET_TOKEN` secret can be deleted.
3. **Build-provenance attestation** — a few lines of YAML for a signed, verifiable claim about who built the
   artifact. Cheap; do it before signing.
4. **Signing** — check Trusted Signing eligibility/region; if blocked, apply to SignPath Foundation.
   Wire signing into `release.yml`. This is the only item that touches Defender heuristics.
5. **WinGet** — once signed.
6. **npm `perch-tray`** — low priority now that B1 covers the same ground.

## Open questions

- Is Quartex Software a registered entity 3+ years old, and is AU in Trusted Signing's identity-
  validation regions? Decides A1 vs A2.
- Move `install.ps1` off `raw.githubusercontent.com` (GitHub Pages, or a domain) for a shorter URL and no
  proxy-blocking? Pure URL swap whenever it's wanted — the script is unchanged.
- Does macOS want the equivalent `curl … | sh`? It would also defuse the `xattr -cr` quarantine dance, since
  curl-written files aren't quarantined. Deliberately deferred; `SHA256SUMS.txt` already covers the mac
  assets for hand-verification.
- npm name if it happens: `perch-tray` (bare `npx`) or `@arcticgizmo/perch` (scoped)?

## Verified locally (2026-07-29)

Most of this is now a committed suite — **`tools/test-install.ps1`** (22 checks) — rather than a one-off, so
it can be re-run after any change to the script. It was written and run against **Windows PowerShell 5.1**,
the worst case, since the one-liner has to work there without `pwsh`:

- **Parses clean**, and a `param()` block survives being piped into `iex` with its defaults applied — which
  is what makes `$env:PERCH_VERSION` work through the one-liner.
- **Manifest parsing** (`Get-ExpectedHash`): finds the right line; handles both `sha256sum` output styles
  (`hash  name` on Linux/CI and `hash *name`, which Git Bash produces locally); handles CRLF; does not
  prefix-match a different asset; and *throws* on an absent entry, an empty manifest, and an HTML error page
  masquerading as one — the fail-closed cases that matter most.
- **Download + verify** against a loopback `HttpListener` serving a real 36 MB artifact: exact byte count,
  hash matches the source, a tampered manifest is rejected, and a mid-transfer connection kill never returns
  success. 36 MB in 0.7 s, confirming the manual stream isn't throttled by progress rendering.
- **The real chain**, end to end: `publish.bat`'s `SHA256SUMS.txt` → the script's parser → `Get-FileHash` of
  the actual 93 MB `Perch-win-Setup.exe` → match. The generated manifest is LF-terminated, BOM-free, doesn't
  list itself, and passes `sha256sum -c`.
- **The CI shell logic** was run against a simulated two-job artifact tree: correct flatten, `sha256sum -c`
  round-trip, and both guards firing (duplicate basename, missing `Perch-win-Setup.exe`).

**Not yet verified**, and only verifiable once a release with `SHA256SUMS.txt` exists: the live GitHub API
lookup, and the installer actually running to completion from the one-liner. Worth walking through by hand on
the first tag. `pwsh` isn't installed on the dev box either, so the PowerShell 7 path is covered by
construction (an explicit `byte[]`/string decode, with a test proving the branch is required) rather than by
running the script there.

## macOS parity

The same first-install problem exists there as Gatekeeper quarantine, and the README currently tells
users to run `xattr -cr`. The real fix is an Apple Developer Program membership (US$99/yr) plus
notarization in `publish-mac.sh` — a separate exercise. A `curl … | sh` counterpart to `install.ps1` would
defuse it as a side effect, since curl-written files aren't quarantined; deliberately deferred for now, but
`SHA256SUMS.txt` already covers the macOS assets so downloads can be checked by hand
(`shasum -a 256 -c SHA256SUMS.txt --ignore-missing`).
