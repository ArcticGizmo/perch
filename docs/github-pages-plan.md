# GitHub Pages documentation site — plan

**Status:** P0 built (site, `pages.yml`, README link) and P5 half-done (`install.ps1` is published on Pages,
but the docs still point at the raw URL until the content-type check in `docs/github-pages-testing.md` passes).
Not yet deployed: needs Settings → Pages → Source: GitHub Actions, then a merge to `main`. Branch
`github-pages-site`.

**Goal:** a public documentation site for Perch at `arcticgizmo.github.io/perch`. It should cover what Perch
is, how to install it, a tour of every user-facing feature, a full settings reference and the release history.
The site is built from this repo and deployed by GitHub Actions. Wherever the source of truth already lives in
code, the site is generated from it so it can't drift.

## Why now

`README.md` documents roughly the original feature set: overlay, usage bars, notifications, stats, quick links
and hooks. Since then Perch has gained the session window and composer, Roost, themes and the theme designer,
the social feed, achievements, feeds, to-dos, GitHub alerts, Docked mode, multiple config directories, session
recovery, background tasks, the statusline designer and more. None of that is in the README. A single README
can't hold it all, and nobody can discover it from the tray menu.

## What we already have

| Asset | Where | Use on the site |
| --- | --- | --- |
| Settings registry: 92 descriptors with id, name, description, surface, kind, keywords, `Playful` and `Requires` | `src/Perch.Core/Data/SettingsRegistry.cs`, `SettingDescriptor.cs` | **Generated** settings reference, grouped by surface, with platform-support marks |
| Headless renderer: every owner-drawn surface at 1× and 1.5×, per theme, from synthetic `SampleData` | `src/Perch.App/Rendering/HeadlessRenderer.cs`, `perch render <dir> [themeId]` | **Generated** screenshots, rebuilt on every deploy, in light and dark themes, with no personal data |
| Changelog: 211 version sections | `CHANGELOG.md` | "What's new" page, rendered as is |
| Styled overview page with light/dark tokens and the status hues | `docs/perch-overview.html` | Seed for the landing page's look |
| Install, update, checksum and maintainer text | `README.md` | Install, Updating and Contributing pages (moved, then the README is trimmed) |
| Icon source | `perch.svg`, `landing-icon.png` | Site logo and favicon |

Hosting prerequisites are met: the repo is **public**, the maintainer account has **admin**, and Pages is
**not yet enabled** (`GET /repos/ArcticGizmo/perch/pages` returns 404).

## Decisions

### D1. Stack: Astro Starlight, in `site/`

Starlight gives a docs layout, sidebar, light/dark mode, built-in Pagefind search and Markdown/MDX pages with
almost no configuration. It is actively maintained. It adds a Node toolchain, which is acceptable because it
stays inside `site/`:

- `site/package.json` with **exact** versions and a committed `package-lock.json`. CI runs `npm ci`. Never
  `npx` an unpinned package, locally or in CI.
- Nothing outside `site/` depends on Node.

Alternatives considered:

- **MkDocs Material**: simpler (Python, one YAML file), but reportedly heading into maintenance mode.
- **Docusaurus**: heavier than we need.
- **Hand-written HTML** (like `perch-overview.html`): fine for one page, doesn't scale to around 30.

**Decided 2026-10-15:** Starlight.

### D2. The site lives in `site/`, never in `docs/`

`docs/` holds around 90 internal plans, investigations and testing scripts. It is **not** user documentation
and is **never** published. It mentions unreleased branches, security-review findings and hidden features.
The site's content lives in `site/src/content/docs/`, and only what's written there (plus generated inputs) is
published.

### D3. Generate what the code already knows

- **Settings reference:** `perch export-settings <out.json>`, a new verb next to `render` in `Program.cs`. It
  serialises `SettingsRegistry.All` to JSON: id, name, description, surface, kind, keywords, playful and
  requires. The `Get`/`Set` delegates aren't serialised. A Starlight page (or a small build-time script in
  `site/`) turns the JSON into one table per `SettingSurface`. Perch.Core is already
  `InternalsVisibleTo("perch")`, so the verb can read the internal registry directly. Nothing new needs to be
  made public.
- **Screenshots must be rendered hermetically.** Run `render` with `PERCH_DEV=0` (no "Perch - DEV" label or
  pink border) and `CLAUDE_CONFIG_DIR` pointed at an empty directory named `work`. Otherwise the renderer pins
  the config set to whatever `CLAUDE_CONFIG_DIR` the shell inherited, and real config-dir labels and org names
  leak into the overlay and launcher. Found 2026-10-15, when the first renders showed the maintainer's own
  account names. CI is clean by default, but local runs from a Claude Code session are not.
- **Screenshots:** CI runs `perch render` and copies a **curated allowlist** of PNGs (`site/screenshots.txt`)
  into the site's assets. The allowlist keeps debug/bench renders and anything secret out, and gives each
  image a stable published name, so a renamed render fails the build rather than silently dropping an image.
- **Changelog:** copied into the site at build time, with a frontmatter header added. The source stays at the
  repo root.

### D4. Hidden content stays hidden

The site must not reveal the secret arcade (it has never been in the changelog either) or the
mystery/secret achievements. Concretely:

- The settings export skips any setting whose only purpose is a hidden feature. Visible Whimsy toggles such as
  desktop basketball are ordinary settings and stay in.
- The screenshot allowlist excludes arcade windows and secret-achievement cards.
- Feature pages are written by hand and reviewed before merge. Nothing is bulk-imported from `docs/`.

### D5. Document only what has shipped on `main`

Features that only exist on unmerged or unpushed branches get no page until they merge. These include Roost
tabs, feeds, background tasks, session recovery, the plugin system and the IDE-host glyph. When a feature
merges, its PR adds or updates its page, in the same way a new setting already has to add a registry entry.

## Information architecture

```
Home (landing)                      hero, the four states, 3-4 screenshots, "Install" CTA
Getting started
  Install (Windows / macOS)         from README; one-liner, by-hand, checksums, Gatekeeper
  First run                         hooks wiring, tray, permissions prompts on macOS
  Updating                          channels table
Features
  The overlay                       states, rows, sub-agents, click-to-focus, dense / docked, placement, sections & order
  Usage & rate limits               session/weekly bars, pace colours, monthly spend, per-account chips
  Notifications                     toasts, chimes, ntfy push, away override, DND button, quiet mode
  Session window                    controlled sessions, composer (@-mentions, history, images), slash palette,
                                    tool cards, sub-agent tabs, changed-files panel, find bar
  Roost                             (once roost-merge lands)
  History & search                  history viewer, transcript search
  Stats & achievements              dashboard, breakdowns, cost, trophies (non-secret ones only)
  Themes                            presets, designer, share codes, contrast guarantees
  Multiple accounts                 config directories, Scan, per-feature toggles, account guardrails
  Quick links, To-dos, GitHub alerts
  Social                            friends feed; privacy and account deletion
  Statusline designer
  Markdown viewer
  CLI                               `perch`, `perch.com`, --tray, --help/--version, statusline verbs
Reference
  Settings                          GENERATED from SettingsRegistry
  Data & privacy                    what Perch reads under ~/.claude, what leaves the machine (PRIVACY.md)
  FAQ / Troubleshooting             SmartScreen, "damaged" on macOS, terminal focus, hooks
What's new                          GENERATED from CHANGELOG.md
Contributing                        build, render mode, release process (from README "maintainers")
```

## Deployment

A new `.github/workflows/pages.yml`:

- **Triggers:** push to `main` touching `site/**`, `CHANGELOG.md`, `SettingsRegistry.cs`, `src/Perch.App/**`
  (renders) or the workflow itself, plus `workflow_dispatch`.
- **Job `generate`** (`windows-latest`): checkout, setup-dotnet, `perch export-settings` and `perch render`
  under the Midnight and Daylight themes, filter renders through `screenshots.txt`, upload as an artifact.
  It runs on Windows because the render path is only proven on the Windows head. Moving it to
  `ubuntu-latest` with the `net10.0` head is a later optimisation.
- **Job `build`** (`ubuntu-latest`): checkout, download the artifact into `site/`, setup-node with the version
  pinned, `npm ci`, `npm run build`, `actions/upload-pages-artifact`.
- **Job `deploy`:** `actions/deploy-pages`.
- **Permissions:** `contents: read` at the top. `pages: write` and `id-token: write` on the deploy job only.
- **Pinning:** every action pinned to a commit SHA with the version in a trailing comment, as `ci.yml` and
  `release.yml` already do. Dependabot proposes bumps.
- **ASCII only and LF only**, per the release-pipeline script rule (the workflow is covered by
  `tools/test-install.ps1`'s glob).
- **`concurrency: pages`**, so a newer deploy supersedes an in-flight one.

One-time manual step: repo **Settings → Pages → Source: GitHub Actions**.

Housekeeping: add `site/**` to `ci.yml`'s `paths-ignore`, and add a `site` job to CI that only runs
`npm ci && npm run build` on PRs touching `site/**`, so a broken page fails on the PR rather than on deploy.

## Phases

### P0: skeleton and first deploy (~1 day)

- Scaffold Starlight in `site/` (pinned), with the logo, favicon and colour tokens from `perch-overview.html`.
- Pages: Home, Install, Updating, FAQ, What's new (changelog copy), Contributing.
- `pages.yml` with the build and deploy jobs only. No generation yet; screenshots copied by hand from one local
  `render` run.
- Enable Pages and confirm the site serves at `arcticgizmo.github.io/perch`.
- Add a `homepageUrl` on the repo and a link from the README.

### P1: generated settings reference (~0.5 day)

- `perch export-settings <file>` verb, plus a test that every descriptor round-trips and that no
  hidden-feature setting is emitted.
- Build-time page generation from the JSON, with one table per surface, a platform-support badge from
  `Requires`, and a "silenced by Quiet mode" mark from `Playful`.
- Add the `generate` job's export step.

### P2: generated screenshots (~0.5-1 day)

- `site/screenshots.txt` allowlist mapping render names to published names.
- `generate` job renders the Midnight and Daylight themes; pages use a `<ThemedImage>` component that swaps
  with the site's light/dark mode.
- The build fails if an allowlisted render is missing.

### P3: feature pages (~2-4 days, mostly review)

- One page per feature in the IA above, drafted from the code, `CHANGELOG.md` and the existing plan docs,
  each with its screenshots and a "Related settings" block that links into the generated reference.
- **Every page is checked against the running app before it merges.** Several features have only been
  render-verified or never run live (see the per-feature notes). A page must not claim behaviour nobody has
  seen work.

### P4: trim the README (~1 hour)

- Keep the hero, a short pitch, the one-liner install and a prominent link to the site. Move the long feature
  list and the maintainer sections to the site, so there's one copy of each.

### P5: serve `install.ps1` from Pages

Decided 2026-10-15: do this now, not later. It can land with P0 once the site deploys.

- Publish `install.ps1` at the site root, so the one-liner becomes
  `irm https://arcticgizmo.github.io/perch/install.ps1 | iex`. `docs/distribution-plan.md` already treats
  this as a pure URL swap.
- **The `raw.githubusercontent.com` URL must keep working forever**, because it's in old READMEs, blog posts
  and people's shell history. Pages serves a copy; it doesn't replace the original.
- Run `tools/test-install.ps1` against the published copy's bytes (identical to the repo file).

### P6: "Unstable" marks (after P3)

- One pass over the feature pages to mark features that are shipped but not yet proven in daily use, such as
  the social feed's realtime, blocking and rate limits.
- A Starlight `<Aside type="caution">` or a small "Unstable" badge component in the page title, used the same
  way on every marked page. Removing a mark is a one-line change when the feature settles.

### P7 (optional): polish

- Custom domain (CNAME and DNS). Not for now; `arcticgizmo.github.io/perch` is the address.
- Open Graph image (one of the renders) for link previews.
- A "Report a docs bug" link on every page, pointing at a GitHub issue template.

## Keeping it current

- A new setting already has to add a `SettingDescriptor`. With P1 it appears on the site automatically.
- A UI change already has to be checked with `render`. With P2 the site's screenshots update on the next
  deploy.
- A new feature's PR adds or updates its page under `site/src/content/docs/features/`. Add a line to the PR
  checklist.

## Risks

| Risk | Mitigation |
| --- | --- |
| Internal plans or hidden features published | D2 (site never reads `docs/`), D4 (allowlists + review) |
| Pages claim behaviour that has never been verified | P3 "checked against the running app" gate |
| Docs drift from the app | Generated settings, screenshots and changelog; feature-page line in the PR checklist |
| Node supply chain in CI | Exact versions, committed lockfile, `npm ci`, SHA-pinned actions, Dependabot |
| Windows render job is slow or flaky | Path filters on the trigger; cache the NuGet restore; move to Linux later |
| `install.ps1` URL change breaks old instructions | P5 keeps the raw URL as a permanent alias |

## Decisions log (2026-10-15)

1. **Stack:** Astro Starlight.
2. **Domain:** `arcticgizmo.github.io/perch` for now; a custom domain is deferred to P7.
3. **`install.ps1` on Pages:** now (P5), with the raw URL kept as a permanent alias.
4. **Experimental features:** document every feature on `main`; a later pass (P6) marks the shaky ones as
   unstable.
