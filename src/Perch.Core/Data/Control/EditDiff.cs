using System.Text.Json.Nodes;

namespace Perch.Data.Control;

/// <summary>
/// Turns a Claude Code file-edit tool call (<c>Edit</c> / <c>MultiEdit</c> / <c>Write</c>) into a unified
/// line diff, so the rich session UI can show a terminal-style <c>+</c>/<c>-</c> diff instead of dumping the
/// raw input JSON (<c>old_string</c>/<c>new_string</c> blobs). Pure and UI-free — the result is a list of
/// <see cref="GitDiffLine"/>, the same shape the git diff viewer consumes, so both surfaces colour it the
/// same way. Shared by <c>SessionThreadView</c>; covered by <c>EditDiffTests</c>.
/// </summary>
internal static class EditDiff
{
    /// <summary>Total diff lines above which we stop line-by-line LCS and fall back to a coarse
    /// all-removed-then-all-added block — LCS is O(n·m), so a giant edit shouldn't allocate a huge table.</summary>
    private const long LcsCellCap = 400_000;

    /// <summary>True for the tools this can render as a diff.</summary>
    public static bool IsEditTool(string tool) => tool is "Edit" or "MultiEdit" or "Write";

    /// <summary>
    /// Builds a unified diff for an edit tool's <paramref name="input"/>, or <c>null</c> when the tool isn't
    /// an edit tool, the input can't be read, or nothing actually changes. <c>Edit</c> diffs
    /// <c>old_string</c>→<c>new_string</c>; <c>MultiEdit</c> walks each entry in <c>edits</c> in order;
    /// <c>Write</c> shows the whole <c>content</c> as added lines (a fresh file's contents).
    /// </summary>
    public static IReadOnlyList<GitDiffLine>? Build(string tool, JsonNode? input)
    {
        if (input is null) return null;
        var lines = new List<GitDiffLine>();

        switch (tool)
        {
            case "Edit":
                DiffInto(lines, Str(input, "old_string"), Str(input, "new_string"));
                break;

            case "MultiEdit":
                if (input["edits"] is not JsonArray edits) return null;
                bool first = true;
                foreach (var edit in edits)
                {
                    if (edit is null) continue;
                    // A faint separator between successive edits so two replacements don't read as one hunk.
                    if (!first) lines.Add(new GitDiffLine(GitDiffLineKind.Meta, "⋮"));
                    first = false;
                    DiffInto(lines, Str(edit, "old_string"), Str(edit, "new_string"));
                }
                break;

            case "Write":
                foreach (var l in SplitLines(Str(input, "content")))
                    lines.Add(new GitDiffLine(GitDiffLineKind.Added, l));
                break;

            default:
                return null;
        }

        // Nothing to show if the edit was a no-op (only context / separators survived).
        return lines.Any(l => l.Kind is GitDiffLineKind.Added or GitDiffLineKind.Removed) ? lines : null;
    }

    /// <summary>Added / removed line counts of a built diff, for a "<c>+N -M</c>" summary.</summary>
    public static (int Added, int Removed) Counts(IReadOnlyList<GitDiffLine> lines)
    {
        int a = 0, r = 0;
        foreach (var l in lines)
        {
            if (l.Kind == GitDiffLineKind.Added) a++;
            else if (l.Kind == GitDiffLineKind.Removed) r++;
        }
        return (a, r);
    }

    // Appends a line-level diff of oldText→newText to outp: shared lines as Context, deletions as Removed,
    // insertions as Added — the classic LCS backtrace, with a coarse fallback for very large inputs.
    private static void DiffInto(List<GitDiffLine> outp, string oldText, string newText)
    {
        var a = SplitLines(oldText);
        var b = SplitLines(newText);
        int n = a.Length, m = b.Length;

        if ((long)n * m > LcsCellCap)
        {
            foreach (var l in a) outp.Add(new GitDiffLine(GitDiffLineKind.Removed, l));
            foreach (var l in b) outp.Add(new GitDiffLine(GitDiffLineKind.Added, l));
            return;
        }

        // lcs[i,j] = length of the longest common subsequence of a[i..] and b[j..].
        var lcs = new int[n + 1, m + 1];
        for (int i = n - 1; i >= 0; i--)
            for (int j = m - 1; j >= 0; j--)
                lcs[i, j] = a[i] == b[j]
                    ? lcs[i + 1, j + 1] + 1
                    : Math.Max(lcs[i + 1, j], lcs[i, j + 1]);

        int x = 0, y = 0;
        while (x < n && y < m)
        {
            if (a[x] == b[y]) { outp.Add(new GitDiffLine(GitDiffLineKind.Context, a[x])); x++; y++; }
            else if (lcs[x + 1, y] >= lcs[x, y + 1]) { outp.Add(new GitDiffLine(GitDiffLineKind.Removed, a[x])); x++; }
            else { outp.Add(new GitDiffLine(GitDiffLineKind.Added, b[y])); y++; }
        }
        while (x < n) outp.Add(new GitDiffLine(GitDiffLineKind.Removed, a[x++]));
        while (y < m) outp.Add(new GitDiffLine(GitDiffLineKind.Added, b[y++]));
    }

    private static string Str(JsonNode? node, string key)
    {
        try { return node?[key] is { } v ? TranscriptJson.AsString(v) ?? "" : ""; }
        catch { return ""; }
    }

    // Splits on newlines, tolerant of CRLF/CR, dropping the single phantom empty a trailing newline yields
    // (so an edit that ends in "\n" doesn't show a spurious blank line at the bottom of the diff).
    private static string[] SplitLines(string s)
    {
        if (s.Length == 0) return [];
        s = s.Replace("\r\n", "\n").Replace("\r", "\n");
        var arr = s.Split('\n');
        return arr.Length > 1 && arr[^1].Length == 0 ? arr[..^1] : arr;
    }
}
