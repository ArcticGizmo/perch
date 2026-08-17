namespace Perch.Data;

/// <summary>
/// A small VS Code-style fuzzy matcher for the Markdown project search. Matches a query as a case-insensitive
/// subsequence of a candidate path and scores it so consecutive runs, matches at path-segment or camelCase
/// boundaries, and matches in the <em>file name</em> (rather than deep in the directory) rank higher — the
/// "type a few letters of the name or the path and the right file floats up" behaviour. It reports the matched
/// character positions so the UI can highlight them.
///
/// Deliberately simple (greedy left-to-right, with a file-name-first pass) rather than a full optimal aligner:
/// the candidate set is a project's Markdown files (bounded) and queries are short, so this is plenty and stays
/// easy to reason about.
/// </summary>
public static class FuzzyMatch
{
    // Bonuses/penalties tuned to feel like a quick-open palette. Only their relative magnitudes matter.
    private const int SequentialBonus   = 15;   // this match immediately follows the previous match
    private const int BoundaryBonus     = 30;   // match starts a segment: first char, or right after / \ _ - . space
    private const int CamelBonus        = 30;   // match is an uppercase letter following a lowercase one
    private const int LeadingPenalty    = -3;   // per unmatched char skipped before the first match…
    private const int MaxLeadingPenalty = -9;   // …capped, so a deep path isn't punished into oblivion
    private const int UnmatchedPenalty  = -1;   // per candidate char not part of the match
    private const int FilenameBonus     = 40;   // the whole query matched within the file-name portion

    /// <summary>A successful match: its <see cref="Score"/> (higher is better) and the candidate indices of
    /// the matched characters, ascending, for highlighting.</summary>
    public readonly record struct Result(int Score, IReadOnlyList<int> Positions);

    /// <summary>
    /// Try to fuzzy-match <paramref name="query"/> against <paramref name="path"/> (a forward-slashed relative
    /// path). Returns false when the query isn't a subsequence of the path; a blank query never matches (the
    /// caller shows nothing until the user types).
    /// </summary>
    public static bool TryMatch(string query, string path, out Result result)
    {
        result = default;
        if (string.IsNullOrEmpty(query) || string.IsNullOrEmpty(path))
            return false;

        // Prefer a match confined to the file name (after the last '/'), with a bonus, so typing part of a name
        // beats the same letters appearing earlier in the directory. Fall back to the whole path so the
        // directory can still be used to narrow things down.
        int slash = path.LastIndexOf('/');
        int nameStart = slash + 1;   // 0 when there's no directory — the whole path is the file name
        if (nameStart < path.Length && TryMatchIn(query, path, nameStart, out int nameScore, out var namePos))
        {
            result = new Result(nameScore + FilenameBonus, namePos);
            return true;
        }
        // Only meaningful when there's a directory to fall back onto; a name-only match already tried above.
        if (slash >= 0 && TryMatchIn(query, path, 0, out int score, out var pos))
        {
            result = new Result(score, pos);
            return true;
        }
        return false;
    }

    // Greedy left-to-right subsequence match of query against path[start..], scoring boundary/consecutive
    // bonuses and recording absolute positions (indices into the full path).
    private static bool TryMatchIn(string query, string path, int start, out int score, out IReadOnlyList<int> positions)
    {
        score = 0;
        var pos = new List<int>(query.Length);
        positions = pos;

        int ti = start;
        int lastMatch = -2;
        for (int qi = 0; qi < query.Length; qi++)
        {
            char qc = char.ToLowerInvariant(query[qi]);
            while (ti < path.Length && char.ToLowerInvariant(path[ti]) != qc)
                ti++;
            if (ti >= path.Length)
                return false;

            pos.Add(ti);

            // Segment boundary: start of the candidate, or immediately after a separator. Otherwise a
            // camelCase hump (upper after lower) still counts as a soft boundary.
            if (ti == 0 || IsSeparator(path[ti - 1]))
                score += BoundaryBonus;
            else if (char.IsUpper(path[ti]) && char.IsLower(path[ti - 1]))
                score += CamelBonus;
            if (lastMatch == ti - 1)
                score += SequentialBonus;

            lastMatch = ti;
            ti++;
        }

        // Penalise the run-up before the first match (capped) and every candidate char outside the match, so
        // shorter, denser hits rank above sprawling ones.
        int leading = pos[0] - start;
        score += Math.Max(MaxLeadingPenalty, leading * LeadingPenalty);
        score += (path.Length - pos.Count) * UnmatchedPenalty;
        return true;
    }

    private static bool IsSeparator(char c) => c is '/' or '\\' or '_' or '-' or '.' or ' ';

    /// <summary>
    /// Rank <paramref name="paths"/> by fuzzy match against <paramref name="query"/>, best first, keeping at
    /// most <paramref name="limit"/>. Ties break on the shorter path, then alphabetically, so results are
    /// stable. Non-matches are dropped; a blank query yields nothing.
    /// </summary>
    public static IReadOnlyList<(string Path, Result Match)> Rank(string query, IReadOnlyList<string> paths, int limit)
    {
        var hits = new List<(string Path, Result Match)>();
        foreach (var p in paths)
            if (TryMatch(query, p, out var r))
                hits.Add((p, r));

        hits.Sort((a, b) =>
        {
            int byScore = b.Match.Score.CompareTo(a.Match.Score);
            if (byScore != 0) return byScore;
            int byLen = a.Path.Length.CompareTo(b.Path.Length);
            if (byLen != 0) return byLen;
            return string.Compare(a.Path, b.Path, StringComparison.OrdinalIgnoreCase);
        });

        if (limit >= 0 && hits.Count > limit)
            hits = hits.GetRange(0, limit);
        return hits;
    }
}
