# GitHub Pages site (P0 + P5) — testing

Checks for the first deploy of the docs site (`site/`, `.github/workflows/pages.yml`). Plan:
`docs/github-pages-plan.md`.

## One-time setup (maintainer)

1. Repo **Settings → Pages → Build and deployment → Source: GitHub Actions**.
2. Merge the branch to `main`. The `Pages` workflow runs on the push.
3. Once it's live, set the repo's website field:
   `gh repo edit ArcticGizmo/perch --homepage https://arcticgizmo.github.io/perch/`

## Local build

```sh
cd site
npm ci
npm run build      # must finish with "Complete!" and 8 pages
npm run preview    # http://localhost:4321/perch/
```

- [ ] Home shows the overlay hero, the four-states table, six cards and three screenshots.
- [ ] Toggle the theme (top-right). The session-window and Roost screenshots swap between light and dark;
      only one of each pair is visible at a time.
- [ ] Sidebar: Install, Updating, FAQ & troubleshooting, Privacy, What's new, Contributing. Every link works.
- [ ] What's new starts at `[Unreleased]` / the newest version, with no duplicate "Changelog" heading.
- [ ] Privacy has no duplicate "Perch — Privacy" heading.
- [ ] Search (Ctrl+K) finds "SmartScreen" and "xattr".
- [ ] `site/dist/install.ps1` is byte-identical to the root `install.ps1` (`cmp install.ps1 site/dist/install.ps1`).
- [ ] At phone width (DevTools, 375px) nothing scrolls sideways.

## After the first deploy

- [ ] `https://arcticgizmo.github.io/perch/` loads, with styles and images (a missing base path shows unstyled HTML).
- [ ] The **Edit page** link on Install opens `site/src/content/docs/getting-started/install.md` on GitHub; on
      What's new it opens the root `CHANGELOG.md`.
- [ ] A PR that touches only `site/**` runs the `Pages` build job, skips `deploy`, and doesn't run `CI`.

### install.ps1 on Pages (P5)

The site still tells people to use the `raw.githubusercontent.com` one-liner. Switch the docs to the Pages URL
only after all of these pass.

- [ ] `curl -sI https://arcticgizmo.github.io/perch/install.ps1` returns 200. Note the `Content-Type`.
- [ ] Windows PowerShell 5.1, in a VM or on a machine without Perch:
      `irm https://arcticgizmo.github.io/perch/install.ps1 | iex` installs Perch. If `irm` hands `iex` bytes
      rather than text (likely if the content type is `application/octet-stream`), it fails here.
- [ ] Same in PowerShell 7.
- [ ] The raw URL still works: `irm https://raw.githubusercontent.com/ArcticGizmo/perch/main/install.ps1 | iex`.

If the Pages copy fails because of its content type, leave the docs on the raw URL and record the result in
the plan.
