namespace Perch.Data;

/// <summary>
/// How a single path changed, in one of git's index/worktree slots. Mirrors the status codes git reports
/// in <c>--porcelain=v2</c> (X = staged, Y = unstaged). <see cref="None"/> is the unchanged slot (git's
/// <c>.</c>); <see cref="Unmerged"/> covers any conflict state (git's <c>u</c> records).
/// </summary>
public enum GitChangeKind
{
    None,
    Added,
    Modified,
    Deleted,
    Renamed,
    Copied,
    TypeChanged,
    Unmerged,
}

/// <summary>Which side of a unified-diff line this is, so a viewer can colour it without re-parsing the
/// leading marker. <see cref="Meta"/> is anything that isn't payload — hunk headers, "\ No newline…".</summary>
public enum GitDiffLineKind
{
    Context,
    Added,
    Removed,
    Meta,
}

/// <summary>
/// One changed path in a working tree, as read from <c>git status --porcelain=v2</c>. <see cref="Staged"/>
/// is the index-vs-HEAD change (git's X), <see cref="Unstaged"/> the worktree-vs-index change (git's Y);
/// either can be <see cref="GitChangeKind.None"/>. <see cref="OrigPath"/> is the pre-rename/-copy path when
/// <see cref="Staged"/> is <see cref="GitChangeKind.Renamed"/>/<see cref="GitChangeKind.Copied"/>, else null.
/// <see cref="Untracked"/> flags a file git isn't tracking yet (its "?" records), which carries no X/Y.
/// </summary>
public readonly record struct GitFileChange(
    string Path,
    string? OrigPath,
    GitChangeKind Staged,
    GitChangeKind Unstaged,
    bool Untracked);

/// <summary>
/// A snapshot of a working tree's git status: the current branch (null when detached), its upstream ref
/// (null when none), how many commits it is <see cref="Ahead"/>/<see cref="Behind"/> that upstream, and the
/// changed paths. Best-effort — a field is simply left at its default when git didn't report it.
/// </summary>
public readonly record struct GitRepoStatus(
    string? Branch,
    string? Upstream,
    int Ahead,
    int Behind,
    IReadOnlyList<GitFileChange> Changes)
{
    /// <summary>True when the working tree has no reported changes.</summary>
    public bool IsClean => Changes.Count == 0;
}

/// <summary>One entry from <c>git log</c>: full and abbreviated hash, author name, author date, the commit
/// subject (first line of the message), and the full raw message <see cref="Body"/> (subject + body, for a
/// hover tooltip). <see cref="Body"/> falls back to <see cref="Subject"/> when no body was captured.</summary>
public readonly record struct GitCommit(
    string Hash,
    string ShortHash,
    string Author,
    DateTimeOffset Date,
    string Subject,
    string Body);

/// <summary>One line of a unified diff — its <see cref="Kind"/> and text with the leading git marker
/// (<c> </c>/<c>+</c>/<c>-</c>) stripped for payload lines; <see cref="GitDiffLineKind.Meta"/> keeps the
/// whole line.</summary>
public readonly record struct GitDiffLine(GitDiffLineKind Kind, string Text);

/// <summary>One hunk of a file diff: its <c>@@ … @@</c> header line and the lines within it.</summary>
public readonly record struct GitDiffHunk(string Header, IReadOnlyList<GitDiffLine> Lines);

/// <summary>
/// The diff for one file. <see cref="OldPath"/>/<see cref="NewPath"/> are null for an added/deleted file
/// respectively (git's <c>/dev/null</c>). <see cref="IsBinary"/> is set for a binary change (no textual
/// hunks). <see cref="Hunks"/> is empty for a pure rename/mode change or a binary file.
/// </summary>
public readonly record struct GitDiffFile(
    string? OldPath,
    string? NewPath,
    bool IsBinary,
    IReadOnlyList<GitDiffHunk> Hunks);

/// <summary>A parsed unified diff — the changed files, in the order git emitted them.</summary>
public readonly record struct GitDiff(IReadOnlyList<GitDiffFile> Files);
