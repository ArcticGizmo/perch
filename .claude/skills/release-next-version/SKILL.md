---
name: release-next-version
description: Ship the current branch as the next Perch release. Opens (or reuses) a PR for the current branch, waits for the PR checks, stops and reports a FAIL if any check fails, otherwise squash-merges it, then tags the new main head with the csproj version (vX.Y.Z) and pushes the tag, which kicks off the Release workflow. Use when the user says "release next version", "release this", "/release-next-version", or wants to merge the current branch and cut the tagged release.
---

# Release Next Version

Takes the current branch from "done" to "tagged release": PR → green checks → squash-merge → tag the
new `main` head → push the tag. Pushing a `v*` tag triggers `.github/workflows/release.yml`, which builds
and publishes the GitHub Release.

Invoking this skill is the user's go-ahead for the outward-facing steps (pushing the branch, opening the
PR, merging, pushing the tag). Anything *not* listed here (force-pushes, bypassing checks, deleting
branches, re-tagging) is out of scope — stop and ask.

## Repo facts this relies on

- Repo is `ArcticGizmo/perch`; **squash-merge only** (merge commits and rebase are disabled) and the
  remote branch is auto-deleted on merge.
- Tags are **lightweight**, `vMAJOR.MINOR.PATCH`, and must match `<Version>` in
  `src/Perch.App/Perch.App.csproj` (the release build takes its version from the tag).
- CI (`ci.yml`) runs on pull requests only, and is skipped by `paths-ignore` when a PR only touches
  root `*.md`, `docs/**`, `captures/**` or `backend/**` — so a PR can legitimately have **no checks**.
- Use the Bash tool (Git Bash) for the snippets below.

## Steps

### 1. Preflight — gather everything in one batch

```bash
BRANCH=$(git rev-parse --abbrev-ref HEAD); echo "BRANCH=$BRANCH"
echo "=== status ==="; git status --porcelain
git fetch origin --tags --quiet
VERSION=$(sed -n 's/.*<Version>\(.*\)<\/Version>.*/\1/p' src/Perch.App/Perch.App.csproj | head -1); echo "VERSION=$VERSION"
echo "LAST_TAG=$(git tag --sort=-v:refname | head -1)"
echo "TAG_EXISTS_LOCAL=$(git tag -l "v$VERSION")"
echo "TAG_EXISTS_REMOTE=$(git ls-remote --tags origin "refs/tags/v$VERSION")"
echo "=== commits on $BRANCH not on origin/main ==="
git log origin/main..HEAD --no-merges --pretty=format:'%h %s'
echo; echo "=== existing PR ==="; gh pr view --json number,state,url,title 2>&1
```

Stop and report (don't try to fix it yourself) if any of these hold:

- `BRANCH` is `main` (or detached `HEAD`) — there's nothing to PR. Say so.
- The working tree is dirty — uncommitted changes wouldn't be in the release. List them and ask
  whether to commit them first.
- There are no commits on the branch ahead of `origin/main`.
- `v$VERSION` already exists (locally or on the remote), or `VERSION` isn't greater than `LAST_TAG` —
  the version hasn't been bumped. Suggest running `/bump-version` (and committing its csproj +
  `CHANGELOG.md` edits) first. Never move or overwrite an existing tag.
- An existing PR for the branch is `CLOSED` or `MERGED` — ask rather than open a second one.

### 2. Push the branch and open (or reuse) the PR

```bash
git push -u origin HEAD
```

If step 1 found an `OPEN` PR, reuse it. Otherwise create one against `main`:

- **Title:** a concise summary of the branch's net change (if the branch is a single feature, its name;
  otherwise the headline items), ending with `; bump to $VERSION` when the branch carries the bump —
  matching history, e.g. `Composer: mid-prompt skills, [Image #N] markers, popup repaint fix; bump to 0.5.18`.
- **Body:** a short `## Summary` of the user-facing changes (from the commit list in step 1 and, if
  present, the new `## [v$VERSION]` section in `CHANGELOG.md`), a `## Test plan` noting what CI covers
  and anything still unverified live, then the PR attribution line.

Write the body to a temp file and pass it with `--body-file` (avoids quoting trouble):

```bash
gh pr create --base main --head "$BRANCH" --title "<title>" --body-file <file>
```

Record the PR number and URL.

### 3. Wait for the checks

CI takes several minutes (each job has a 30-minute ceiling), so run the wait with the Bash tool's
`run_in_background: true` and a timeout of about 45 minutes (`2700000` ms), then wait for the
completion notification — don't poll. Checks can take a few seconds to register after the PR opens,
hence the short lead-in loop:

```bash
PR=<number>
found=0
for i in $(seq 1 18); do
  if gh pr checks "$PR" 2>&1 | grep -q 'no checks reported'; then sleep 10; else found=1; break; fi
done
if [ "$found" = 0 ]; then echo "RESULT=NO_CHECKS"; exit 0; fi
gh pr checks "$PR" --watch --fail-fast --interval 30
echo "RESULT=EXIT_$?"
```

Then branch on the result:

- **`EXIT_0`** — all checks passed. Continue to step 4.
- **Any other exit** — a check failed (or was cancelled). **Stop: this is a FAIL that needs fixing.**
  Do not merge, do not tag. Gather the evidence:

  ```bash
  gh pr checks "$PR"
  gh run list --branch "$BRANCH" --workflow ci.yml --limit 1 --json databaseId,url,conclusion
  gh run view <databaseId> --log-failed | tail -80
  ```

  Report back headed **`RELEASE FAILED — checks did not pass`**, with the PR URL, which job/step failed,
  the run URL, and the relevant tail of the failed log (the failing test names / compiler errors). Leave
  the PR open. Don't attempt a fix unless the user asks.
- **`NO_CHECKS`** — nothing ran. Usually that's `paths-ignore` (docs/markdown-only PR); say so, and ask
  the user whether to merge without checks. Don't merge on your own.

### 4. Squash-merge

```bash
gh pr merge "$PR" --squash
```

Don't pass `--delete-branch` (the remote branch is auto-deleted; leave the local branch alone — after a
squash it can only be force-deleted, which is the user's call). Don't pass `--admin` or `--auto`. If the
merge is refused (required review, branch protection, merge conflict), stop and report the message.

Then confirm the merge and capture the squash commit:

```bash
gh pr view "$PR" --json state,mergeCommit -q '.state + " " + .mergeCommit.oid'
git fetch origin --tags --quiet
git checkout main && git pull --ff-only origin main
echo "MAIN_HEAD=$(git rev-parse HEAD)"
```

`state` must be `MERGED`. Normally `MAIN_HEAD` equals the merge commit. If it doesn't, something else
landed on `main` in the meantime — stop and ask which commit to tag rather than guessing.

If `git pull --ff-only` fails (local `main` had diverged), stop and report — don't reset it.

### 5. Tag the new main head and push the tag

```bash
git tag "v$VERSION" "$MAIN_HEAD"
git push origin "v$VERSION"
```

(Lightweight tag, matching the existing ones.) Then grab the Release run it triggered — it can take a
few seconds to appear:

```bash
gh run list --workflow release.yml --limit 1 --json status,url,headBranch
```

Don't wait for the release build to finish unless the user asks.

### 6. Report back

Print, plainly:

- **`Released v$VERSION`**
- The PR URL (merged) and the squash commit SHA that was tagged.
- The Release workflow run URL (and that it's in progress).
- That local `main` is checked out and up to date, and the local branch `$BRANCH` still exists (the
  remote one was auto-deleted).

On any stop in steps 1–5, report exactly which step stopped, why, and what state things were left in
(e.g. "PR #57 open, not merged, no tag").
