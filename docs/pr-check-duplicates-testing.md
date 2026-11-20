# PR check duplicates: manual testing

The PR glyph's tooltip and flyout listed some checks twice. `gh pr view --json statusCheckRollup` returns the check runs of every workflow run on the PR's head commit. When a workflow runs twice on one SHA (a re-run, or a second `pull_request` event such as `labeled` or `ready_for_review`), every job appears once per run. `PrStatusService.ParseChecks` now keeps only the newest run of each (workflow, check name), as `gh pr checks` does.

## Before

1. Open a PR whose CI workflow has run twice on the same head commit. Re-running all jobs from the Actions tab is the easiest way to get one.
2. Hover the session row's PR glyph, or click it to open the flyout.
3. Every job from that workflow is listed twice. If the first run failed and the re-run passed, the status dot stays red.

## After

1. Repeat the steps above.
2. Each check appears once. Its link opens the newer run's logs.
3. If the re-run passed, the dot is green.
4. Start another re-run. While it's queued, its checks show as pending. They don't keep showing the previous run's result.
5. Checks with the same job name in two *different* workflows still appear separately.
