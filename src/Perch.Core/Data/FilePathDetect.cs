namespace Perch.Data;

/// <summary>
/// Finds the path-shaped words in plain prose — <c>src/Perch.App/App.axaml.cs:42</c>, <c>docs\plan.md</c>,
/// <c>README.md</c> — that Claude writes without backticks, so the session UI can offer them as file links. This is
/// only a shape filter: every candidate still goes through <see cref="FileRefResolver"/>, which refuses anything
/// network-shaped without touching it and links only a file that really exists, so a false positive costs one
/// cached local existence check. URLs and other <c>scheme:</c> words are skipped (they're links, not files), as are
/// plain numbers like <c>0.5.27</c>. Pure; never throws.
/// </summary>
public static class FilePathDetect
{
    /// <summary>A path-shaped word: its char range in the text, and the text itself (any line suffix included).</summary>
    public readonly record struct Candidate(int Start, int Length, string Text);

    // A wall of dotted words shouldn't turn into a wall of disk checks.
    private const int MaxCandidates = 32;

    public static IReadOnlyList<Candidate> Find(string? text)
    {
        if (string.IsNullOrEmpty(text)) return [];
        List<Candidate>? found = null;
        int i = 0;
        while (i < text.Length && (found?.Count ?? 0) < MaxCandidates)
        {
            if (IsBreak(text[i])) { i++; continue; }
            int start = i;
            while (i < text.Length && !IsBreak(text[i])) i++;
            int end = i;
            // Sentence punctuation after a path ("see a.md.", "in b.cs:") isn't part of it.
            while (end > start && text[end - 1] is '.' or ':' or '!' or '?') end--;
            if (end - start is < 3 or > 260) continue;
            var word = text[start..end];
            if (LooksLikePath(word)) (found ??= new()).Add(new Candidate(start, end - start, word));
        }
        return found ?? (IReadOnlyList<Candidate>)[];
    }

    private static bool IsBreak(char c) =>
        char.IsWhiteSpace(c) || c is '"' or '\'' or '`' or '<' or '>' or '(' or ')' or '[' or ']' or '{' or '}'
            or ',' or ';' or '|' or '*';

    private static bool LooksLikePath(string word)
    {
        if (word.Contains("://", StringComparison.Ordinal) || HasScheme(word)) return false;
        var (path, _) = FileRefResolver.SplitLine(word);
        if (path.IndexOfAny(['/', '\\']) >= 0) return path.Any(char.IsLetter);
        // No separator: a bare file name needs a real-looking extension ("README.md", not "0.5.27" or "e.g").
        int dot = path.LastIndexOf('.');
        if (dot <= 0 || dot == path.Length - 1) return false;
        var ext = path.AsSpan(dot + 1);
        return ext.Length <= 10 && ext.ContainsAnyExcept("0123456789") && !ext.ContainsAnyExcept(ExtChars)
            && path.AsSpan(0, dot).ContainsAny(Letters) && ext.Length >= 2;
    }

    private static readonly System.Buffers.SearchValues<char> ExtChars =
        System.Buffers.SearchValues.Create("abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789");
    private static readonly System.Buffers.SearchValues<char> Letters =
        System.Buffers.SearchValues.Create("abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ");

    // "scheme:" not followed by a digit (so "a.cs:42" stays a path) and longer than one char (so "C:\x" stays a drive).
    private static bool HasScheme(string s)
    {
        int colon = s.IndexOf(':');
        if (colon < 2 || !char.IsAsciiLetter(s[0])) return false;
        if (colon + 1 < s.Length && char.IsAsciiDigit(s[colon + 1])) return false;
        for (int i = 1; i < colon; i++)
            if (!(char.IsAsciiLetterOrDigit(s[i]) || s[i] is '+' or '-' or '.')) return false;
        return true;
    }
}
