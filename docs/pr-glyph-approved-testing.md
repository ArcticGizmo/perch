# PR glyph: approval tick

The session row's PR glyph now shows a small green tick just right of its CI-checks dot once the PR is approved. The PR lookup also asks `gh` for `reviewDecision`, so "approved" follows GitHub's own decision: a repo that requires two approvals shows no tick after the first one. On a repo that requires no reviews (no decision reported), an approving review counts, unless another reviewer has requested changes. The tick is separate from the dot, so a PR can read "approved, CI failing" at a glance.

The hover tooltip gains a green `✓ Approved by <login>` line under the title, and the composer toolbar's PR button shows the same tick (its tooltip says `· approved`).

Unit coverage: `PrStatusServiceTests` (`IsApprovedFollowsTheReviewDecision`, `ReviewDecisionChangeIsAChange`). Headless render: the `api` row in `overlay_*.png` carries an approved PR with a failing check. The checks below are the live ones.

## Setup

- **Before:** the installed Perch (0.6.0).
- **After:** this branch, run with `dotnet run --project src/Perch.App -f net10.0-windows10.0.19041.0`. It uses its own settings (`%APPDATA%\Perch (Dev)`), so turn on **GitHub pull requests** there too.

You need a Claude Code session whose working directory is on a branch with an open PR you can get approved (ask a teammate, or use a second account).

## 1. Tick appears on approval

Start with the PR unapproved, then have it approved. The glyph refreshes on the PR poll interval (5 min by default), or at once if you switch branch away and back.

| | Before | After |
|---|---|---|
| PR glyph before approval | Merge mark + CI dot | Same, no tick |
| PR glyph after approval | Unchanged | Green tick right of the CI dot; the session name shifts right to make room |
| Hover tooltip | `#N · Open · title`, then checks | Adds `✓ Approved by <login>` in green under the title |
| Session window composer PR button | No change | Same tick; tooltip ends `· approved` |

## 2. Approval withdrawn

Have the approver dismiss their review, or have someone request changes.

| | Before | After |
|---|---|---|
| PR glyph | Unchanged | Tick disappears on the next refresh |

## 3. Required reviews not yet met (optional)

On a repo whose branch protection requires two approvals, get one.

| | Before | After |
|---|---|---|
| PR glyph | Unchanged | No tick until the second approval |
