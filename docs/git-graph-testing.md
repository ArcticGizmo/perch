# Git graph: manual testing

Checks for the git graph work (`docs/git-graph-plan.md`), one section per checkpoint that changes something you can see or run. Each lists what to do, what you should see now, and what you'd have seen before.

## CP3: `perch bench-graph`

Measures the graph's read and layout on a real repository. Run it on the big repos you have checked out.

```
perch bench-graph <path-to-repo> [count]
```

`count` caps the full-history read (default 100,000). From a dev build: `src\Perch.App\bin\Debug\net10.0-windows10.0.19041.0\perch.exe bench-graph <repo>`. In PowerShell, pipe it (`| Out-String`) or redirect it (`> bench.txt`) if the output lands after the prompt.

**Expect** four sections:

1. **Open**: what the Tree window will pay on open: `scope`, then `git: refs, status, log` (run in parallel), `parse`, `layout`, `total`, then rows / lanes / worktrees (and how many are dirty) / refs.
2. **Refresh**: the same with lane hints from the open, and `0 commits changed lane` unless the repo changed between the two reads.
3. **Full history**: `git log`, MB of output, `parse`, `layout`, then commits, lanes, edges and the memory held, then `layout with hints`.
4. **Paging** (only past 2,000 commits): the cost of reading a page at three depths with `--skip`.

**Please send back** the output for each big repo. The budgets it's measured against are in the plan (open < 300 ms, layout < 150 ms per 100k commits, < 40 MB per 100k). If the paging lines grow with depth, CP3's follow-up is a streaming log read instead of `--skip`.

**Before:** `perch bench-graph` didn't exist (`perch --help` now lists it under Diagnostics).
