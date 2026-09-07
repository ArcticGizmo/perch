using System.Text.RegularExpressions;

namespace Perch.Data.Control;

/// <summary>A detected http(s) link in a stretch of text: a half-open <c>[Start, Start+Length)</c> span into
/// the original string, plus the exact URL it covers.</summary>
internal readonly record struct UrlSpan(int Start, int Length, string Url);

/// <summary>
/// The one place that finds http(s) URLs in free text — shared by the composer highlighter (which colours
/// them) and the conversation surfaces (which make them clickable), so "what counts as a link" is defined
/// once. UI-free and deterministic; never throws.
/// </summary>
internal static partial class UrlDetect
{
    // Deliberately liberal on the body (any non-space); trailing punctuation is trimmed below so a link at
    // the end of a sentence doesn't swallow the full stop or a closing bracket.
    [GeneratedRegex(@"https?://\S+", RegexOptions.IgnoreCase)]
    private static partial Regex LinkRegex();

    private const string TrailingTrim = ").],;:!?\"'";

    /// <summary>Every http(s) URL in <paramref name="text"/>, in order, with trailing punctuation trimmed.
    /// Empty for null/empty input.</summary>
    public static IReadOnlyList<UrlSpan> Find(string? text)
    {
        if (string.IsNullOrEmpty(text)) return [];
        List<UrlSpan>? result = null;
        foreach (Match m in LinkRegex().Matches(text))
        {
            int len = m.Length;
            while (len > 0 && TrailingTrim.IndexOf(text[m.Index + len - 1]) >= 0) len--;
            if (len > 0) (result ??= []).Add(new UrlSpan(m.Index, len, text.Substring(m.Index, len)));
        }
        return result ?? [];
    }
}
