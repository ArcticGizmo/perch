# Milestone 0 — Git data layer + tests (Session Change Review)

> Status: proposal / not started. Author: design pass, 2026-08-05.
> Milestone 0 of the **Session Change Review** feature — full proposal + milestone map in
> [`git-review-plan.md`](./git-review-plan.md).

## Context

We're building an experimental **Session Change Review** feature (full proposal in
[`git-review-plan.md`](./git-review-plan.md)): right-click a Claude session → review what it changed
in that repo, read-only first, commit/graph later and optional. This plan covers **only Milestone
0**: the read-only git data layer in `Perch.Core` plus its unit tests. **No UI, no settings toggle,
no `SessionMonitor` wiring** — those belong to M1. The goal is to prove the parsing/read side is
correct and durable (fast xUnit tests over canned command output) before spending effort on
owner-drawn pixels.

The read layer reuses the established shell-out idiom in this codebase
(`src/Perch.Core/Data/GitStatsService.cs`, `PrStatusService.cs`): shell out to `git`, best-effort
(null on any failure), `--no-optional-locks` so we never fight a live session's index, a hard
per-invocation timeout, and async draining of both stdout/stderr pipes to avoid deadlock. The
parsing logic is split into `internal static` pure functions so it's unit-testable with canned
strings exactly like the existing `GitStatsService.ParseNumstat` / `PrStatusService.ParsePrJson`.

### Key design decision: on-demand, not per-scan

The siblings are keyed by `cwd` and called from `SessionMonitor.ReadSession` on **every scan** to
populate a glyph on `ClaudeSession`. `GitRepoService` is different: `git log`/`diff`/`show` are
heavier and are only needed while the Review window is open on **one** repo. So `GitRepoService`
is an **on-demand loader** owned by the window/host in M1 — it is deliberately **not** added to
`SessionMonitor` or the `ClaudeSession` record. M0 delivers the Core service + parsers + models +
tests; M1 constructs it when the window opens and calls it off the UI thread.

## Files

### New — `src/Perch.Core/Data/GitRepoService.cs`

`internal sealed class GitRepoService` in `namespace Perch.Data`. Copies the mechanics of
`GitStatsService` (the private `RunGit`-style helper: `ProcessStartInfo` with `--no-optional-locks`
prefix, `UseShellExecute=false`, `CreateNoWindow`, async `ReadToEndAsync` on both pipes,
`WaitForExit(timeoutMs)` with `Kill(entireProcessTree:true)` on timeout, try/catch → null).

Public surface (all best-effort, all safe to call off the UI thread; return null on any failure):

- `GitRepoStatus? GetStatus(string cwd)` — runs `git --no-optional-locks status --porcelain=v2 --branch -z`.
- `IReadOnlyList<GitCommit> GetLog(string cwd, int maxCount)` — runs
  `git --no-optional-locks log --max-count=<n> --format=<pinned format>`.
- `GitDiff? GetWorkingDiff(string cwd, string path, bool staged)` — `git --no-optional-locks diff [--cached] -- <path>`.
- `GitDiff? GetCommitDiff(string cwd, string hash)` — `git --no-optional-locks show <hash> --format= --patch`.

A `HasGitRepo(cwd)` guard (the `.git` walk already written in `PrStatusService.cs:241`) short-circuits
non-repos before spawning. Reuse a single timeout constant (~4000 ms like `GitStatsService.GitTimeoutMs`;
give `log` a little more headroom). No `Enabled`/cache/event ceremony in M0 — the on-demand window
controls when it runs, and there is no hot scan to protect. (A short TTL cache can be added in M1 if
the window proves chatty; note it, don't build it now.)

Each of the four public methods is a thin wrapper: run the command, hand stdout to the matching
`internal static` parser. The parsers hold all the logic and all the tests:

- `internal static GitRepoStatus ParseStatusV2(string output)`
- `internal static IReadOnlyList<GitCommit> ParseLog(string output)`
- `internal static GitDiff ParseUnifiedDiff(string output)`

### New — `src/Perch.Core/Data/GitRepoModels.cs` (or inline in the service file, matching sibling style)

Records (mirroring the `readonly record struct` / `record` style already used for `GitLineStats`,
`PullRequestInfo`, `TaskItem`):

- `GitRepoStatus(string? Branch, string? Upstream, int Ahead, int Behind, IReadOnlyList<GitFileChange> Changes)`
  with a `bool IsClean => Changes.Count == 0`.
- `GitFileChange(string Path, string? OrigPath, GitChangeKind Staged, GitChangeKind Unstaged, bool Untracked)`
  where `enum GitChangeKind { None, Added, Modified, Deleted, Renamed, Copied, TypeChanged, Unmerged }`.
- `GitCommit(string Hash, string ShortHash, string Author, DateTimeOffset Date, string Subject)`.
- `GitDiff(IReadOnlyList<GitDiffFile> Files)`; `GitDiffFile(string? OldPath, string? NewPath, bool IsBinary, IReadOnlyList<GitDiffHunk> Hunks)`;
  `GitDiffHunk(string Header, IReadOnlyList<GitDiffLine> Lines)`; `GitDiffLine(GitDiffLineKind Kind, string Text)`
  where `enum GitDiffLineKind { Context, Added, Removed, Meta }`. (Typed lines let M1's owner-drawn
  viewer colour from `Theming.Palette` with no syntax highlighter.)

### New — `tests/Perch.Tests/GitRepoServiceTests.cs`

`public class GitRepoServiceTests` in `namespace Perch.Tests`, `using Perch.Data; using Xunit;`.
Plain `[Fact]`/`[Theory]` + `Assert.*` (no FluentAssertions). Canned command-output strings fed to
the three parsers — **no real git repo, no process spawn** (matches the whole suite; the process/
timeout paths stay manual, documented in a class-level `<summary>` like `GitStatsServiceTests`).
`InternalsVisibleTo` already covers `Perch.Tests`, so nothing to change in a csproj.

## Parser details (what each must handle)

**`ParseStatusV2`** — `--porcelain=v2 --branch -z` output:
- Header lines: `# branch.head <name>` (→ Branch; `(detached)` → null), `# branch.upstream <ref>`,
  `# branch.ab +N -M` (→ Ahead/Behind).
- `1 <XY> ... <path>` (ordinary change) and `2 <XY> ... <path>\0<origPath>` (rename/copy — the NUL
  splits new from orig; this is why `-z`). Map each of X (staged) and Y (unstaged) char to
  `GitChangeKind`.
- `u ...` (unmerged → Unmerged), `? <path>` (untracked → `Untracked=true`), `! <path>` (ignored → skip).
- Tolerate CRLF-free NUL framing, blank/partial trailing records, unknown line types (skip).

**`ParseLog`** — records separated by `\n`, fields within a record by a pinned rare delimiter
(`%x1f` unit-separator: `--format=%H%x1f%h%x1f%an%x1f%aI%x1f%s`). Parse ISO-8601 (`%aI`) to
`DateTimeOffset`; skip malformed/short lines. `%s` (subject) can't contain a newline, so line-splitting
records is safe.

**`ParseUnifiedDiff`** — the meatiest:
- `diff --git a/… b/…` starts a new file; `--- a/…` / `+++ b/…` set old/new path (`/dev/null` →
  add/delete); `rename from/to`, `new file`, `deleted file` handled; `Binary files … differ` → `IsBinary`.
- `@@ -a,b +c,d @@[ section]` starts a hunk (kept as `Meta`/header text).
- Body lines: leading ` ` → Context, `+` → Added, `-` → Removed, `\ No newline at end of file` → Meta.
- Tolerate CRLF, trailing partial output, and diffs with no `@@` (pure rename/mode change).

Cover, per parser: happy path, rename (with orig path), binary, deletion/addition (`/dev/null`),
CRLF, empty output (clean tree / no commits / no diff), and malformed/truncated input → no throw.

## Verification

- `dotnet test tests/Perch.Tests/Perch.Tests.csproj` — the new `GitRepoServiceTests` pass; whole
  suite stays green.
- `dotnet build perch.slnx` — Core still builds clean on both TFMs (the service is pure `net10.0`
  Core, no UI/Win32/`System.Drawing`, so it must not break the macOS `net10.0` head).
- Manual smoke (not automated, matching the suite's convention for process-spawning code): a tiny
  throwaway `dotnet script`/LINQPad or a `#if DEBUG` scratch call pointing `GetStatus`/`GetLog`/
  `GetWorkingDiff` at this very repo, eyeballing that the parsed models match `git status`/`git log`/
  `git diff`. Delete after.

## Out of scope (later milestones)

- M1: the Review window, per-session "Review changes…" menu item, `WindowHost` wiring, the
  experimental `SettingDescriptor` + `AppSettings` toggle, owner-drawn diff/commit-list rendering,
  on-demand construction + optional TTL cache, `render`-mode verification.
- M2: staging/commit (guarded on Active sessions). M3: lane graph, syntax highlighting, branch ops.
