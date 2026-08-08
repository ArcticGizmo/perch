namespace Perch.Data;

using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Text;

/// <summary>
/// Read-only git queries for a single working tree — status, recent log, and unified diffs — by shelling
/// out to <c>git</c>. The read side of the experimental "Session Change Review" feature (see
/// <c>docs/git-review-plan.md</c>). Unlike <see cref="GitStatsService"/>/<see cref="PrStatusService"/>,
/// which fold a glyph onto every session on the hot scan, this is an <b>on-demand</b> loader for the one
/// repo a review window is open on: no per-scan caching or master switch, just direct best-effort calls
/// the caller runs off the UI thread.
///
/// Every method is best-effort: it returns null / an empty list on any failure (git missing, not a repo,
/// timeout, non-zero exit) and never throws. Each git invocation carries <c>--no-optional-locks</c> so it
/// can't touch <c>index.lock</c> and interfere with a live session's own git, a hard timeout so a
/// pathological repo can't wedge a caller, and async draining of both pipes so a large diff can't deadlock
/// the child. The parsing lives in the <c>internal static</c> <see cref="ParseStatusV2"/>/
/// <see cref="ParseLog"/>/<see cref="ParseUnifiedDiff"/> methods, unit-tested against canned output.
/// </summary>
internal sealed class GitRepoService
{
    // Hard ceiling on a status/diff invocation, matching GitStatsService. log gets more headroom since a
    // deep history walk can be slower than a working-tree diff.
    private const int GitTimeoutMs = 4000;
    private const int LogTimeoutMs = 6000;

    // The field delimiter for `git log --format` — the ASCII unit separator (U+001F). It can't appear in a
    // hash/name/date and, since we only use %s (subject, single line), records split cleanly on newline.
    private const char Us = '\u001f';
    // %P (space-separated parent hashes) sits second-to-last so the multi-line %B stays last, and so the
    // first five fields (H, h, an, aI, s) keep their indices — the date/subject positions and the
    // short-record fallbacks in ParseLog are unchanged by adding it.
    private static readonly string LogFormat = $"--format=%H{Us}%h{Us}%an{Us}%aI{Us}%s{Us}%P{Us}%B";

    /// <summary>The working tree's status (branch, ahead/behind, changed paths), or null when
    /// <paramref name="cwd"/> isn't a readable git repo. Runs <c>git status --porcelain=v2 --branch</c>.</summary>
    public GitRepoStatus? GetStatus(string cwd)
    {
        if (!IsRepo(cwd))
            return null;
        var (exit, stdout) = RunGit(cwd, GitTimeoutMs, "--no-optional-locks", "status", "--porcelain=v2", "--branch");
        return exit == 0 ? ParseStatusV2(stdout) : null;
    }

    /// <summary>The most recent <paramref name="maxCount"/> commits on the current branch, newest first;
    /// empty on any failure. Runs <c>git log --max-count=&lt;n&gt; --format=…</c>.</summary>
    public IReadOnlyList<GitCommit> GetLog(string cwd, int maxCount)
    {
        if (maxCount <= 0 || !IsRepo(cwd))
            return [];
        var (exit, stdout) = RunGit(cwd, LogTimeoutMs,
            "--no-optional-locks", "log", "-z", $"--max-count={maxCount}", LogFormat);
        return exit == 0 ? ParseLog(stdout) : [];
    }

    /// <summary>
    /// The commits on the current branch since it diverged from <paramref name="baseRef"/> — git's
    /// <c>&lt;baseRef&gt;..HEAD</c> range, first-parent only (so a merge from the base doesn't drag the
    /// base's commits into the branch view), newest first. Returns null on any git failure (so a caller can
    /// fall back to an unscoped <see cref="GetLog"/>); an empty list means the branch genuinely has no
    /// commits since the divergence point. A null/empty <paramref name="baseRef"/> falls back to plain HEAD.
    /// </summary>
    public IReadOnlyList<GitCommit>? GetBranchLog(string cwd, string? baseRef, int maxCount)
    {
        if (maxCount <= 0 || !IsRepo(cwd))
            return null;
        string range = string.IsNullOrEmpty(baseRef) ? "HEAD" : $"{baseRef}..HEAD";
        var (exit, stdout) = RunGit(cwd, LogTimeoutMs,
            "--no-optional-locks", "log", range, "--first-parent", "-z", $"--max-count={maxCount}", LogFormat);
        return exit == 0 ? ParseLog(stdout) : null;
    }

    /// <summary>The merge-base (common ancestor) of HEAD and <paramref name="baseRef"/> — where the current
    /// branch left <paramref name="baseRef"/> — as a full hash, or null on any failure. Runs
    /// <c>git merge-base HEAD &lt;baseRef&gt;</c>.</summary>
    public string? GetMergeBase(string cwd, string baseRef)
    {
        if (string.IsNullOrEmpty(baseRef) || !IsRepo(cwd))
            return null;
        var (exit, stdout) = RunGit(cwd, GitTimeoutMs, "--no-optional-locks", "merge-base", "HEAD", baseRef);
        if (exit != 0)
            return null;
        var hash = stdout.Trim();
        return hash.Length == 0 ? null : hash;
    }

    /// <summary>
    /// The candidate "base branches" to scope this branch against, best first — a fork setup's
    /// <c>upstream/main</c> before <c>origin/main</c> before a local <c>main</c>. Enumerates local and
    /// remote refs with <c>git for-each-ref</c> and ranks them with <see cref="PickBaseRefCandidates"/>.
    /// Empty when nothing plausible exists. <paramref name="currentBranch"/> is excluded from the local tier
    /// so a branch can't be its own base.
    /// </summary>
    public IReadOnlyList<string> GetBaseRefCandidates(string cwd, string? currentBranch)
    {
        if (!IsRepo(cwd))
            return [];
        var (exit, stdout) = RunGit(cwd, GitTimeoutMs,
            "--no-optional-locks", "for-each-ref", "--format=%(refname:short)", "refs/heads", "refs/remotes");
        if (exit != 0)
            return [];
        var refs = stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return PickBaseRefCandidates(currentBranch, refs);
    }

    /// <summary>The unified diff of one path in the working tree — staged (index vs HEAD) when
    /// <paramref name="staged"/>, else unstaged (worktree vs index); null on any failure.</summary>
    public GitDiff? GetWorkingDiff(string cwd, string path, bool staged)
    {
        if (string.IsNullOrEmpty(path) || !IsRepo(cwd))
            return null;
        var (exit, stdout) = staged
            ? RunGit(cwd, GitTimeoutMs, "--no-optional-locks", "diff", "--cached", "--", path)
            : RunGit(cwd, GitTimeoutMs, "--no-optional-locks", "diff", "--", path);
        return exit == 0 ? ParseUnifiedDiff(stdout) : null;
    }

    /// <summary>The whole working tree's unified diff — staged (index vs HEAD) when <paramref name="staged"/>,
    /// else unstaged (worktree vs index); null on any failure. One call for every changed path, so a caller
    /// can classify the whole change set (e.g. which paths carry no net text change) without a diff per
    /// file.</summary>
    public GitDiff? GetWorkingTreeDiff(string cwd, bool staged)
    {
        if (!IsRepo(cwd))
            return null;
        var (exit, stdout) = staged
            ? RunGit(cwd, GitTimeoutMs, "--no-optional-locks", "diff", "--cached")
            : RunGit(cwd, GitTimeoutMs, "--no-optional-locks", "diff");
        return exit == 0 ? ParseUnifiedDiff(stdout) : null;
    }

    /// <summary>The unified diff introduced by a single commit; null on any failure. Runs
    /// <c>git show &lt;hash&gt; --format= --patch</c> so only the patch (no commit header) is parsed.</summary>
    public GitDiff? GetCommitDiff(string cwd, string hash)
    {
        if (string.IsNullOrEmpty(hash) || !IsRepo(cwd))
            return null;
        var (exit, stdout) = RunGit(cwd, GitTimeoutMs, "--no-optional-locks", "show", hash, "--format=", "--patch");
        return exit == 0 ? ParseUnifiedDiff(stdout) : null;
    }

    /// <summary>
    /// The full contents of an untracked file rendered as an added-file diff, so the review window can show
    /// what a session's new files contain; null on any failure. Runs <c>git diff --no-index -- /dev/null
    /// &lt;path&gt;</c>. <c>--no-index</c> exits <b>1</b> whenever the files differ — which they always do
    /// against <c>/dev/null</c> — so unlike the other queries this accepts exit 0 <i>or</i> 1 and only
    /// treats a launch/timeout failure (exit &lt; 0) as "no diff".
    /// </summary>
    public GitDiff? GetUntrackedDiff(string cwd, string path)
    {
        if (string.IsNullOrEmpty(path) || !IsRepo(cwd))
            return null;
        var (exit, stdout) = RunGit(cwd, GitTimeoutMs, "--no-optional-locks", "diff", "--no-index", "--", "/dev/null", path);
        return exit >= 0 ? ParseUnifiedDiff(stdout) : null;
    }

    // ---- write operations (Phase 2 commit authoring) --------------------------------------------------
    //
    // Unlike the read side, these intentionally take git's index lock (no --no-optional-locks) and surface
    // failure text to the caller, so the Tree window can report why a stage/commit didn't happen. Whole-file
    // granularity only — hunk/line staging is a later phase.

    /// <summary>Stages a path (git's index-add), covering a modified, new, or deleted file. Returns whether
    /// it succeeded and, on failure, git's error text.</summary>
    public (bool Ok, string Error) StageFile(string cwd, string path)
    {
        if (string.IsNullOrEmpty(path)) return (false, "No path.");
        if (!IsRepo(cwd)) return (false, "Not a git repository.");
        var r = RunGitCore(cwd, GitTimeoutMs, null, "add", "--", path);
        return r.Exit == 0 ? (true, "") : (false, ErrorText(r));
    }

    /// <summary>Unstages a path (git restore --staged), leaving the working-tree change intact. Returns
    /// whether it succeeded and, on failure, git's error text.</summary>
    public (bool Ok, string Error) UnstageFile(string cwd, string path)
    {
        if (string.IsNullOrEmpty(path)) return (false, "No path.");
        if (!IsRepo(cwd)) return (false, "Not a git repository.");
        var r = RunGitCore(cwd, GitTimeoutMs, null, "restore", "--staged", "--", path);
        return r.Exit == 0 ? (true, "") : (false, ErrorText(r));
    }

    /// <summary>Stages everything (<c>git add -A</c>): all modifications, new files, and deletions across the
    /// working tree. Returns whether it succeeded and, on failure, git's error text.</summary>
    public (bool Ok, string Error) StageAll(string cwd)
    {
        if (!IsRepo(cwd)) return (false, "Not a git repository.");
        var r = RunGitCore(cwd, GitTimeoutMs, null, "add", "-A");
        return r.Exit == 0 ? (true, "") : (false, ErrorText(r));
    }

    /// <summary>Unstages everything (<c>git reset</c> — a mixed reset of the index to HEAD), leaving the
    /// working tree intact. Returns whether it succeeded and, on failure, git's error text.</summary>
    public (bool Ok, string Error) UnstageAll(string cwd)
    {
        if (!IsRepo(cwd)) return (false, "Not a git repository.");
        var r = RunGitCore(cwd, GitTimeoutMs, null, "reset", "--quiet");
        return r.Exit == 0 ? (true, "") : (false, ErrorText(r));
    }

    /// <summary>Discards a whole file's <b>unstaged</b> working-tree changes. Destructive: the edits are
    /// gone. An untracked file is removed from disk (<c>git clean -f -d</c>); a tracked file is reverted to
    /// its index content (<c>git restore --worktree</c>), so any staged part is kept. Returns whether it
    /// succeeded and, on failure, git's error text.</summary>
    public (bool Ok, string Error) DiscardFile(string cwd, string path, bool untracked)
    {
        if (string.IsNullOrEmpty(path)) return (false, "No path.");
        if (!IsRepo(cwd)) return (false, "Not a git repository.");
        var r = untracked
            ? RunGitCore(cwd, GitTimeoutMs, null, "clean", "-f", "-d", "--", path)
            : RunGitCore(cwd, GitTimeoutMs, null, "restore", "--worktree", "--", path);
        return r.Exit == 0 ? (true, "") : (false, ErrorText(r));
    }

    /// <summary>Commits the staged changes with <paramref name="message"/>. Returns whether it succeeded
    /// and, on failure, git's error text (e.g. "nothing to commit"). The message may span multiple lines —
    /// it is passed as a single <c>-m</c> argument.</summary>
    public (bool Ok, string Error) Commit(string cwd, string message)
    {
        if (string.IsNullOrWhiteSpace(message)) return (false, "Empty commit message.");
        if (!IsRepo(cwd)) return (false, "Not a git repository.");
        var r = RunGitCore(cwd, LogTimeoutMs, null, "commit", "-m", message);
        return r.Exit == 0 ? (true, "") : (false, ErrorText(r));
    }

    // Prefers git's stderr for the human-facing message, falling back to stdout, then a generic line.
    private static string ErrorText((int Exit, string Stdout, string Stderr) r)
    {
        var err = r.Stderr.Trim();
        if (err.Length > 0) return err;
        var outp = r.Stdout.Trim();
        return outp.Length > 0 ? outp : $"git exited with code {r.Exit}.";
    }

    // ---- hunk staging (git apply --cached) ------------------------------------------------------------

    /// <summary>Stages one hunk of a working-tree change: takes the file's <b>unstaged</b> raw diff, slices
    /// out just that hunk, and <c>git apply --cached</c>s it into the index. The hunk is identified by its
    /// header (as shown in the diff). Returns success + git's error text on failure.</summary>
    public (bool Ok, string Error) StageHunk(string cwd, string path, string hunkHeader)
    {
        var patch = ExtractHunkPatch(GetFileDiffRawText(cwd, path, staged: false), hunkHeader);
        if (patch is null) return (false, "Could not isolate that hunk (it may have changed).");
        return ApplyCached(cwd, patch, reverse: false);
    }

    /// <summary>Unstages one hunk: takes the file's <b>staged</b> raw diff, slices out that hunk, and
    /// reverse-applies it to the index (<c>git apply --cached --reverse</c>). Returns success + error.</summary>
    public (bool Ok, string Error) UnstageHunk(string cwd, string path, string hunkHeader)
    {
        var patch = ExtractHunkPatch(GetFileDiffRawText(cwd, path, staged: true), hunkHeader);
        if (patch is null) return (false, "Could not isolate that hunk (it may have changed).");
        return ApplyCached(cwd, patch, reverse: true);
    }

    /// <summary>Discards one hunk of a working-tree change — reverts just that hunk in the working tree
    /// (like <c>git checkout -p</c>). Destructive: the change is gone. Slices the hunk from the file's
    /// unstaged diff and reverse-applies it to the worktree (not the index).</summary>
    public (bool Ok, string Error) DiscardHunk(string cwd, string path, string hunkHeader)
    {
        var patch = ExtractHunkPatch(GetFileDiffRawText(cwd, path, staged: false), hunkHeader);
        if (patch is null) return (false, "Could not isolate that hunk (it may have changed).");
        return ApplyPatch(cwd, patch, cached: false, reverse: true);
    }

    /// <summary>The raw (unparsed) unified diff text for one path — staged (index vs HEAD) or unstaged
    /// (worktree vs index). The source text a hunk patch is sliced from, so it stays byte-accurate for
    /// <c>git apply</c>. Empty on failure.</summary>
    public string GetFileDiffRawText(string cwd, string path, bool staged)
    {
        if (string.IsNullOrEmpty(path) || !IsRepo(cwd)) return "";
        var (exit, stdout) = staged
            ? RunGit(cwd, GitTimeoutMs, "--no-optional-locks", "diff", "--cached", "--", path)
            : RunGit(cwd, GitTimeoutMs, "--no-optional-locks", "diff", "--", path);
        return exit == 0 ? stdout : "";
    }

    /// <summary>The full text of a path at a git ref — <c>HEAD</c>, a commit hash, a parent (<c>hash^</c>),
    /// or the index (<paramref name="gitRef"/> = <c>""</c>, i.e. <c>git show :path</c>). Null when the path
    /// doesn't exist at that ref (a new/deleted file) or on error, so the caller can show an "empty" note.</summary>
    public string? GetFileAtRef(string cwd, string path, string gitRef)
    {
        if (string.IsNullOrEmpty(path) || !IsRepo(cwd)) return null;
        var (exit, stdout) = RunGit(cwd, GitTimeoutMs, "--no-optional-locks", "show", $"{gitRef}:{path}");
        return exit == 0 ? stdout : null;
    }

    // Stages/unstages a patch against the index (git apply --cached [--reverse]) via stdin.
    private (bool Ok, string Error) ApplyCached(string cwd, string patch, bool reverse) =>
        ApplyPatch(cwd, patch, cached: true, reverse: reverse);

    // Feeds a patch to `git apply` via stdin — to the index (cached) or the worktree, optionally reversed.
    // Surfaces git's error text.
    private (bool Ok, string Error) ApplyPatch(string cwd, string patch, bool cached, bool reverse)
    {
        if (!IsRepo(cwd)) return (false, "Not a git repository.");
        var args = new List<string> { "apply" };
        if (cached) args.Add("--cached");
        if (reverse) args.Add("--reverse");
        args.Add("-");
        var r = RunGitCore(cwd, GitTimeoutMs, patch, args.ToArray());
        return r.Exit == 0 ? (true, "") : (false, ErrorText(r));
    }

    /// <summary>
    /// Slices a single hunk out of one file's raw unified diff, returning a standalone patch — the file
    /// header (<c>diff --git</c> … <c>---</c>/<c>+++</c>) plus just that one hunk — that <c>git apply</c>
    /// accepts. The hunk is matched by the range part of its header (<c>@@ -a,b +c,d @@</c>), which is unique
    /// within a file's diff, so a trailing function-context suffix git may append doesn't matter. Null when
    /// the diff has no file header or the hunk isn't found. Pure and unit-tested.
    /// </summary>
    internal static string? ExtractHunkPatch(string rawFileDiff, string hunkHeader)
    {
        if (string.IsNullOrEmpty(rawFileDiff)) return null;
        string want = HunkRange(hunkHeader);
        if (want.Length == 0) return null;

        var lines = rawFileDiff.Replace("\r\n", "\n").Split('\n');
        int firstHunk = Array.FindIndex(lines, l => l.StartsWith("@@", StringComparison.Ordinal));
        if (firstHunk < 0) return null;

        int start = -1;
        for (int i = firstHunk; i < lines.Length; i++)
            if (lines[i].StartsWith("@@", StringComparison.Ordinal) && HunkRange(lines[i]) == want) { start = i; break; }
        if (start < 0) return null;

        int end = lines.Length;
        for (int i = start + 1; i < lines.Length; i++)
            if (lines[i].StartsWith("@@", StringComparison.Ordinal) || lines[i].StartsWith("diff --git ", StringComparison.Ordinal))
            { end = i; break; }

        var sb = new StringBuilder();
        for (int i = 0; i < firstHunk; i++) sb.Append(lines[i]).Append('\n');           // file header
        for (int i = start; i < end; i++)
        {
            if (i == lines.Length - 1 && lines[i].Length == 0) break;                    // trailing split artifact
            sb.Append(lines[i]).Append('\n');
        }
        return sb.ToString();
    }

    // The "@@ -a,b +c,d @@" prefix of a hunk header (through the second "@@"), or "" if not a hunk header.
    private static string HunkRange(string header)
    {
        if (!header.StartsWith("@@", StringComparison.Ordinal)) return "";
        int second = header.IndexOf("@@", 2, StringComparison.Ordinal);
        return second < 0 ? header : header[..(second + 2)];
    }

    // ---- line staging (partial-hunk git apply --cached) -----------------------------------------------

    /// <summary>Stages only the selected lines of a hunk (<paramref name="selectedBodyIndices"/> are 0-based
    /// indices over the hunk's body lines, same order as the parsed <see cref="GitDiffHunk.Lines"/>). Builds
    /// a subset patch from the file's unstaged diff and <c>git apply --cached</c>s it. Returns success +
    /// error; the error is set when the selection contains no actual change.</summary>
    public (bool Ok, string Error) StageLines(string cwd, string path, string hunkHeader, IReadOnlyCollection<int> selectedBodyIndices)
    {
        var patch = BuildLineSubsetPatch(GetFileDiffRawText(cwd, path, staged: false), hunkHeader, selectedBodyIndices, stage: true);
        if (patch is null) return (false, "No stageable change in the selection.");
        return ApplyCached(cwd, patch, reverse: false);
    }

    /// <summary>Unstages only the selected lines of a hunk (indices as in <see cref="StageLines"/>). Builds a
    /// subset patch from the file's staged diff and reverse-applies it to the index.</summary>
    public (bool Ok, string Error) UnstageLines(string cwd, string path, string hunkHeader, IReadOnlyCollection<int> selectedBodyIndices)
    {
        var patch = BuildLineSubsetPatch(GetFileDiffRawText(cwd, path, staged: true), hunkHeader, selectedBodyIndices, stage: false);
        if (patch is null) return (false, "No unstageable change in the selection.");
        return ApplyCached(cwd, patch, reverse: true);
    }

    /// <summary>
    /// Builds a patch that applies only the selected lines of one hunk — the core of line-level staging. From
    /// the file's raw diff it slices the file header and target hunk, then rewrites the hunk body to keep only
    /// the selected changes and recomputes the <c>@@</c> counts: an unselected addition is dropped when
    /// staging / kept as context when unstaging; an unselected removal is kept as context when staging /
    /// dropped when unstaging (the mirror, because an unstage patch is built forward and applied with
    /// <c>--reverse</c>). All context is preserved so <c>git apply</c> can still locate the hunk. A trailing
    /// "\ No newline" marker is kept only when the line it annotates is kept. Returns null when the diff/hunk
    /// isn't found or the selection leaves no change. Pure and unit-tested. <paramref name="stage"/> true =
    /// forward (stage); false = the forward-form patch to apply reversed (unstage).
    /// </summary>
    internal static string? BuildLineSubsetPatch(
        string rawFileDiff, string hunkHeader, IReadOnlyCollection<int> selectedBodyIndices, bool stage)
    {
        if (string.IsNullOrEmpty(rawFileDiff) || selectedBodyIndices.Count == 0) return null;
        string want = HunkRange(hunkHeader);
        if (want.Length == 0) return null;

        var lines = rawFileDiff.Replace("\r\n", "\n").Split('\n');
        int firstHunk = Array.FindIndex(lines, l => l.StartsWith("@@", StringComparison.Ordinal));
        if (firstHunk < 0) return null;
        int start = -1;
        for (int i = firstHunk; i < lines.Length; i++)
            if (lines[i].StartsWith("@@", StringComparison.Ordinal) && HunkRange(lines[i]) == want) { start = i; break; }
        if (start < 0) return null;
        int end = lines.Length;
        for (int i = start + 1; i < lines.Length; i++)
            if (lines[i].StartsWith("@@", StringComparison.Ordinal) || lines[i].StartsWith("diff --git ", StringComparison.Ordinal))
            { end = i; break; }

        var body = new List<string>();
        for (int i = start + 1; i < end; i++)
        {
            if (i == lines.Length - 1 && lines[i].Length == 0) continue; // trailing split artifact
            body.Add(lines[i]);
        }

        var sel = selectedBodyIndices as ISet<int> ?? new HashSet<int>(selectedBodyIndices);
        var outBody = new List<string>();
        int oldCount = 0, newCount = 0, changes = 0;
        bool prevKept = false;
        for (int i = 0; i < body.Count; i++)
        {
            string ln = body[i];
            char c = ln.Length > 0 ? ln[0] : ' ';
            string rest = ln.Length > 0 ? ln[1..] : "";
            bool isSel = sel.Contains(i);
            switch (c)
            {
                case '\\': // "\ No newline at end of file": keep iff the line it annotates was kept.
                    if (prevKept) outBody.Add(ln);
                    break;
                case '+':
                    if (isSel) { outBody.Add("+" + rest); newCount++; changes++; prevKept = true; }
                    else if (stage) { prevKept = false; }                                  // drop
                    else { outBody.Add(" " + rest); oldCount++; newCount++; prevKept = true; } // keep as context
                    break;
                case '-':
                    if (isSel) { outBody.Add("-" + rest); oldCount++; changes++; prevKept = true; }
                    else if (stage) { outBody.Add(" " + rest); oldCount++; newCount++; prevKept = true; } // keep as context
                    else { prevKept = false; }                                            // drop
                    break;
                default: // ' ' context (and any unexpected line)
                    outBody.Add(" " + rest);
                    oldCount++; newCount++; prevKept = true;
                    break;
            }
        }
        if (changes == 0) return null;

        int oldStart = ParseOldStart(lines[start]);
        string suffix = HunkSuffix(lines[start]);
        string header = $"@@ -{oldStart},{oldCount} +{oldStart},{newCount} @@{suffix}";

        var sb = new StringBuilder();
        for (int i = 0; i < firstHunk; i++) sb.Append(lines[i]).Append('\n'); // file header
        sb.Append(header).Append('\n');
        foreach (var b in outBody) sb.Append(b).Append('\n');
        return sb.ToString();
    }

    // The old-side start line of a hunk header ("@@ -A,B +C,D @@" → A), or 1 if unparseable.
    private static int ParseOldStart(string header)
    {
        int dash = header.IndexOf('-');
        if (dash < 0) return 1;
        int e = dash + 1;
        while (e < header.Length && char.IsDigit(header[e])) e++;
        return int.TryParse(header.AsSpan(dash + 1, e - (dash + 1)), out var v) ? v : 1;
    }

    // Whatever git appended after the second "@@" (a leading space + function context, or ""); preserved so
    // the rebuilt header reads naturally.
    private static string HunkSuffix(string header)
    {
        int second = header.IndexOf("@@", 2, StringComparison.Ordinal);
        return second < 0 ? "" : header[(second + 2)..];
    }

    // ---- change classification (pure) -----------------------------------------------------------------

    /// <summary>
    /// True when one file's diff carries no net text change — every removed line reappears, in order, as an
    /// identical added line (the parser having already stripped a leading BOM and trailing CR). That is what
    /// a byte-order-mark-only or line-ending-only edit reduces to. A binary change is never "no change".
    /// </summary>
    internal static bool FileHasNoTextChange(GitDiffFile file)
    {
        if (file.IsBinary)
            return false;
        var lines = file.Hunks.SelectMany(h => h.Lines);
        var removed = lines.Where(l => l.Kind == GitDiffLineKind.Removed).Select(l => l.Text);
        var added = file.Hunks.SelectMany(h => h.Lines)
            .Where(l => l.Kind == GitDiffLineKind.Added).Select(l => l.Text);
        return removed.SequenceEqual(added);
    }

    /// <summary>
    /// True when a whole parsed diff carries no net text change (see <see cref="FileHasNoTextChange"/>). An
    /// empty diff — git emitted nothing, e.g. a line-ending-only change it normalised away under
    /// <c>core.autocrlf</c> — counts as no change.
    /// </summary>
    public static bool HasNoTextChange(GitDiff diff) => diff.Files.All(FileHasNoTextChange);

    // ---- base-ref ranking (pure) ----------------------------------------------------------------------

    // The branch names we recognise as a "trunk" to scope against, most-likely first. A ref only becomes a
    // base candidate if its leaf name is one of these.
    private static readonly string[] TrunkNames = ["main", "master", "trunk", "develop"];

    /// <summary>
    /// Ranks branch refs into the plausible base branches to measure the current branch against, best first.
    /// Preference is by tier — a remote literally named <c>upstream</c> (the fork convention) first, then
    /// <c>origin</c>, then local branches, then any other remote — and within a tier by trunk name
    /// (<c>main</c> &gt; <c>master</c> &gt; <c>trunk</c> &gt; <c>develop</c>). Only refs whose leaf name is a
    /// known trunk qualify; <c>*/HEAD</c> pseudo-refs and the <paramref name="currentBranch"/> itself (in the
    /// local tier) are dropped; the result is de-duplicated preserving order. Pure and unit-tested.
    /// </summary>
    internal static IReadOnlyList<string> PickBaseRefCandidates(string? currentBranch, IReadOnlyList<string> refs)
    {
        var ranked = new List<(int Tier, int Name, string Ref)>();
        foreach (var r in refs)
        {
            if (string.IsNullOrEmpty(r))
                continue;
            int slash = r.IndexOf('/');
            string leaf = slash >= 0 ? r[(slash + 1)..] : r;
            if (leaf == "HEAD")
                continue; // origin/HEAD and friends are symbolic pointers, not a base
            int name = Array.IndexOf(TrunkNames, leaf);
            if (name < 0)
                continue; // not a trunk-shaped name — never a base candidate

            int tier;
            if (slash < 0)
            {
                if (r == currentBranch)
                    continue; // a branch can't be its own base
                tier = 2; // local branch
            }
            else
            {
                string remote = r[..slash];
                tier = remote switch { "upstream" => 0, "origin" => 1, _ => 3 };
            }
            ranked.Add((tier, name, r));
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        return ranked
            .OrderBy(x => x.Tier).ThenBy(x => x.Name)
            .Select(x => x.Ref)
            .Where(seen.Add)
            .ToList();
    }

    // ---- parsers (internal static, pure, unit-tested) -------------------------------------------------

    /// <summary>
    /// Parses <c>git status --porcelain=v2 --branch</c> output. Header lines (<c># branch.head</c>,
    /// <c># branch.upstream</c>, <c># branch.ab</c>) fill the branch/upstream/ahead/behind fields; entry
    /// lines fill <see cref="GitRepoStatus.Changes"/>: <c>1</c> (ordinary), <c>2</c> (rename/copy, whose
    /// tab-separated trailer carries the original path), <c>u</c> (unmerged), <c>?</c> (untracked). Ignored
    /// (<c>!</c>) and unknown lines are skipped; blank/malformed lines never throw. Tolerates CRLF.
    /// </summary>
    internal static GitRepoStatus ParseStatusV2(string output)
    {
        string? branch = null, upstream = null;
        int ahead = 0, behind = 0;
        var changes = new List<GitFileChange>();

        foreach (var raw in output.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (line.Length == 0)
                continue;

            switch (line[0])
            {
                case '#':
                    ParseBranchHeader(line, ref branch, ref upstream, ref ahead, ref behind);
                    break;

                case '1': // "1 <XY> <sub> <mH> <mI> <mW> <hH> <hI> <path>"
                {
                    var p = line.Split(' ', 9);
                    if (p.Length < 9) break;
                    var (st, un) = MapXy(p[1]);
                    changes.Add(new GitFileChange(p[8], null, st, un, false));
                    break;
                }

                case '2': // "2 <XY> … <score> <path>\t<origPath>"
                {
                    var p = line.Split(' ', 10);
                    if (p.Length < 10) break;
                    var (st, un) = MapXy(p[1]);
                    var tab = p[9].IndexOf('\t');
                    string path = tab >= 0 ? p[9][..tab] : p[9];
                    string? orig = tab >= 0 ? p[9][(tab + 1)..] : null;
                    changes.Add(new GitFileChange(path, orig, st, un, false));
                    break;
                }

                case 'u': // "u <xy> … <path>" — a conflict; both slots read as Unmerged
                {
                    var p = line.Split(' ', 11);
                    if (p.Length < 11) break;
                    changes.Add(new GitFileChange(p[10], null, GitChangeKind.Unmerged, GitChangeKind.Unmerged, false));
                    break;
                }

                case '?': // "? <path>"
                {
                    var p = line.Split(' ', 2);
                    if (p.Length < 2) break;
                    changes.Add(new GitFileChange(p[1], null, GitChangeKind.None, GitChangeKind.None, true));
                    break;
                }

                // '!' (ignored) and anything unexpected: skip.
            }
        }

        return new GitRepoStatus(branch, upstream, ahead, behind, changes);
    }

    // Folds one "# branch.*" header into the accumulating branch/upstream/ahead/behind.
    private static void ParseBranchHeader(string line, ref string? branch, ref string? upstream,
        ref int ahead, ref int behind)
    {
        const string head = "# branch.head ", up = "# branch.upstream ", ab = "# branch.ab ";
        if (line.StartsWith(head, StringComparison.Ordinal))
        {
            var name = line[head.Length..];
            branch = name == "(detached)" ? null : name;
        }
        else if (line.StartsWith(up, StringComparison.Ordinal))
        {
            upstream = line[up.Length..];
        }
        else if (line.StartsWith(ab, StringComparison.Ordinal))
        {
            // "+<ahead> -<behind>"
            foreach (var tok in line[ab.Length..].Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                if (tok.Length < 2) continue;
                if (tok[0] == '+' && int.TryParse(tok.AsSpan(1), out var a)) ahead = a;
                else if (tok[0] == '-' && int.TryParse(tok.AsSpan(1), out var b)) behind = b;
            }
        }
    }

    // Maps a porcelain-v2 two-char "XY" field to (staged, unstaged) kinds. Anything unexpected → None.
    private static (GitChangeKind Staged, GitChangeKind Unstaged) MapXy(string xy)
    {
        char x = xy.Length > 0 ? xy[0] : '.';
        char y = xy.Length > 1 ? xy[1] : '.';
        return (MapCode(x), MapCode(y));
    }

    private static GitChangeKind MapCode(char c) => c switch
    {
        'M' => GitChangeKind.Modified,
        'A' => GitChangeKind.Added,
        'D' => GitChangeKind.Deleted,
        'R' => GitChangeKind.Renamed,
        'C' => GitChangeKind.Copied,
        'T' => GitChangeKind.TypeChanged,
        'U' => GitChangeKind.Unmerged,
        _ => GitChangeKind.None, // '.', ' ', or anything else
    };

    /// <summary>
    /// Parses the unit-separator-delimited <c>git log</c> output this service requests
    /// (<c>%H\x1f%h\x1f%an\x1f%aI\x1f%s</c>), one commit per line. Lines with too few fields or an
    /// unparseable date are skipped; never throws.
    /// </summary>
    internal static IReadOnlyList<GitCommit> ParseLog(string output)
    {
        var commits = new List<GitCommit>();
        // `-z` separates commits with NUL, so a record can hold a multi-line %B body without ambiguity.
        foreach (var record in output.Split('\0'))
        {
            if (record.Length == 0)
                continue;
            var f = record.Split(Us);
            if (f.Length < 5)
                continue;
            if (!DateTimeOffset.TryParse(f[3], CultureInfo.InvariantCulture,
                    DateTimeStyles.RoundtripKind, out var date))
                continue;
            string subject = f[4];
            // %P (parent hashes) is second-to-last; empty for a root commit or an old five-field record.
            string parents = f.Length >= 6 ? f[5].Trim() : "";
            // %B (full message) is the last field; it carries its own trailing newline(s). Fall back to the
            // subject when it wasn't captured.
            string body = f.Length >= 7 ? f[6].Replace("\r\n", "\n").Trim('\n', '\r') : subject;
            if (body.Length == 0)
                body = subject;
            commits.Add(new GitCommit(f[0], f[1], f[2], date, subject, body, parents));
        }
        return commits;
    }

    /// <summary>
    /// Parses unified-diff text (from <c>git diff</c> or <c>git show --patch</c>) into per-file hunks and
    /// typed lines. Handles <c>diff --git</c> file boundaries, <c>--- </c>/<c>+++ </c> paths (with
    /// <c>/dev/null</c> → null for add/delete), <c>rename from/to</c>, and binary markers. File-header
    /// <c>--- </c>/<c>+++ </c> lines are only treated as headers before the first hunk of a file, so a
    /// removed content line beginning "<c>-- …</c>" inside a hunk isn't mistaken for one. Tolerates CRLF
    /// and leading non-diff preamble (e.g. an empty <c>--format=</c> blank line); never throws.
    /// </summary>
    internal static GitDiff ParseUnifiedDiff(string output)
    {
        var files = new List<GitDiffFile>();

        // Mutable accumulators for the file currently being built.
        string? oldPath = null, newPath = null;
        bool isBinary = false;
        List<GitDiffHunk>? hunks = null;         // non-null once a "diff --git" has opened a file
        string? hunkHeader = null;
        List<GitDiffLine>? hunkLines = null;

        void FlushHunk()
        {
            if (hunkHeader != null)
            {
                hunks!.Add(new GitDiffHunk(hunkHeader, hunkLines ?? []));
                hunkHeader = null;
                hunkLines = null;
            }
        }

        void FlushFile()
        {
            if (hunks == null)
                return;
            FlushHunk();
            files.Add(new GitDiffFile(oldPath, newPath, isBinary, hunks));
            oldPath = newPath = null;
            isBinary = false;
            hunks = null;
        }

        foreach (var raw in output.Split('\n'))
        {
            var line = raw.TrimEnd('\r');

            // Empty lines only ever appear as the trailing split artifact of a final newline or as a
            // separator — git prefixes every real body line (a blank context line is " ", not ""), so
            // skipping them can't drop payload.
            if (line.Length == 0)
                continue;

            if (line.StartsWith("diff --git ", StringComparison.Ordinal))
            {
                FlushFile();
                hunks = [];
                continue;
            }

            if (hunks == null)
                continue; // preamble before the first file — ignore

            // Header region for this file (before its first hunk). Once a hunk is open, --- / +++ are
            // content lines, so only match them here.
            if (hunkHeader == null)
            {
                if (line.StartsWith("--- ", StringComparison.Ordinal)) { oldPath = DiffPath(line[4..]); continue; }
                if (line.StartsWith("+++ ", StringComparison.Ordinal)) { newPath = DiffPath(line[4..]); continue; }
                if (line.StartsWith("rename from ", StringComparison.Ordinal)) { oldPath = line[12..]; continue; }
                if (line.StartsWith("rename to ", StringComparison.Ordinal)) { newPath = line[10..]; continue; }
            }

            if (line.StartsWith("Binary files ", StringComparison.Ordinal) ||
                line.StartsWith("GIT binary patch", StringComparison.Ordinal))
            {
                isBinary = true;
                continue;
            }

            if (line.StartsWith("@@", StringComparison.Ordinal))
            {
                FlushHunk();
                hunkHeader = line;
                hunkLines = [];
                continue;
            }

            if (hunkHeader == null)
                continue; // other metadata lines (index, mode, similarity) before the first hunk

            // Body of an open hunk. Strip a leading UTF-8 BOM from the content: a file whose first line is
            // shown carries U+FEFF at the start of that line, and it should neither render nor be copied.
            switch (line[0])
            {
                case ' ': hunkLines!.Add(new GitDiffLine(GitDiffLineKind.Context, Body(line))); break;
                case '+': hunkLines!.Add(new GitDiffLine(GitDiffLineKind.Added, Body(line))); break;
                case '-': hunkLines!.Add(new GitDiffLine(GitDiffLineKind.Removed, Body(line))); break;
                case '\\': hunkLines!.Add(new GitDiffLine(GitDiffLineKind.Meta, line)); break; // "\ No newline…"
                default: hunkLines!.Add(new GitDiffLine(GitDiffLineKind.Meta, line)); break;
            }

            // Drop the +/- prefix, then a leading UTF-8 BOM (U+FEFF) if the file's first line is shown.
            // (char)0xFEFF keeps this source pure ASCII - no invisible BOM glyph in the literal.
            static string Body(string l) => l[1..].TrimStart((char)0xFEFF);
        }

        FlushFile();
        return new GitDiff(files);
    }

    // Strips a diff path's "a/"/"b/" prefix; "/dev/null" (an added or deleted side) maps to null.
    private static string? DiffPath(string p)
    {
        if (p == "/dev/null")
            return null;
        if (p.Length >= 2 && (p[0] == 'a' || p[0] == 'b') && p[1] == '/')
            return p[2..];
        return p;
    }

    // ---- process plumbing -----------------------------------------------------------------------------

    // Runs `git <args>` in cwd, returning (exitCode, stdout) — the read-side convenience wrapper that drops
    // stderr. Takes an argument list so paths/hashes with spaces are passed safely.
    private static (int Exit, string Stdout) RunGit(string cwd, int timeoutMs, params string[] args)
    {
        var r = RunGitCore(cwd, timeoutMs, null, args);
        return (r.Exit, r.Stdout);
    }

    // As RunGit, but also returns stderr — the write operations surface it as the failure message. When
    // <paramref name="stdin"/> is non-null it is written to the child's standard input then closed (for
    // `git apply`, which reads a patch from stdin). Exit is -1 on any failure to launch/complete.
    private static (int Exit, string Stdout, string Stderr) RunGitCore(
        string cwd, int timeoutMs, string? stdin = null, params string[] args)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "git",
                WorkingDirectory = cwd,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                RedirectStandardInput = stdin is not null,
                UseShellExecute = false,
                CreateNoWindow = true,
                // Git emits UTF-8 (file bytes, commit messages, paths); without this .NET would decode the
                // child's output with the console/ANSI code page (CP1252 on Windows), mangling non-ASCII and
                // rendering a file's UTF-8 BOM as "ï»¿".
                StandardOutputEncoding = System.Text.Encoding.UTF8,
                StandardErrorEncoding = System.Text.Encoding.UTF8,
                StandardInputEncoding = stdin is not null ? new System.Text.UTF8Encoding(false) : null,
            };
            foreach (var a in args)
                psi.ArgumentList.Add(a);

            using var proc = Process.Start(psi);
            if (proc == null)
                return (-1, "", "");

            // Drain both pipes async so a large diff (or a chatty stderr) can't deadlock the child.
            var stdout = proc.StandardOutput.ReadToEndAsync();
            var stderr = proc.StandardError.ReadToEndAsync();

            if (stdin is not null)
            {
                try { proc.StandardInput.Write(stdin); proc.StandardInput.Close(); } catch { }
            }

            if (!proc.WaitForExit(timeoutMs))
            {
                try { proc.Kill(entireProcessTree: true); } catch { }
                return (-1, "", "");
            }
            return (proc.ExitCode, stdout.GetAwaiter().GetResult(), stderr.GetAwaiter().GetResult());
        }
        catch
        {
            return (-1, "", "");
        }
    }

    /// <summary>
    /// Cheap filesystem check: is <paramref name="cwd"/> inside a git working tree? Walks up looking for a
    /// ".git" entry (a dir in a normal clone, a file in a worktree/submodule). No process is spawned, so
    /// it's safe to call on the UI thread — e.g. to gate the "Review changes…" menu item. (Same walk as
    /// <see cref="PrStatusService"/>'s internal check.)
    /// </summary>
    internal static bool IsRepo(string cwd)
    {
        if (string.IsNullOrEmpty(cwd) || !Directory.Exists(cwd))
            return false;
        try
        {
            for (var d = new DirectoryInfo(cwd); d != null; d = d.Parent)
            {
                var git = Path.Combine(d.FullName, ".git");
                if (Directory.Exists(git) || File.Exists(git))
                    return true;
            }
        }
        catch { }
        return false;
    }
}
