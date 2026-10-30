using System.Globalization;

namespace Perch.Data;

/// <summary>
/// Fits a label into a width with a trailing ellipsis, cutting only between user-perceived characters
/// (extended grapheme clusters). The overlay's old cut worked in UTF-16 code units, which could split a
/// surrogate pair or a ZWJ emoji sequence and paint a broken glyph (review fixes CP23). UI-free: the caller
/// supplies the measure.
/// </summary>
internal static class TextFit
{
    public const string Ellipsis = "…";

    /// <summary>
    /// <paramref name="text"/> unchanged when it fits; otherwise the longest whole-grapheme prefix that fits with
    /// <see cref="Ellipsis"/> appended, or the ellipsis alone when not even one grapheme does. "" for an empty
    /// text or a non-positive width. Binary search, so <paramref name="measure"/> runs O(log n) times.
    /// </summary>
    public static string Truncate(string? text, double maxWidth, Func<string, double> measure)
    {
        if (string.IsNullOrEmpty(text)) return "";
        if (measure(text) <= maxWidth) return text;
        if (maxWidth <= 0) return "";

        var ends = GraphemeEnds(text);   // ends[k] = the UTF-16 length of the first k+1 graphemes
        int lo = 0, hi = ends.Length;    // how many graphemes to keep
        while (lo < hi)
        {
            int mid = (lo + hi + 1) / 2;
            if (measure(text[..ends[mid - 1]] + Ellipsis) <= maxWidth) lo = mid; else hi = mid - 1;
        }
        return lo == 0 ? Ellipsis : text[..ends[lo - 1]] + Ellipsis;
    }

    /// <summary>
    /// Fits a branch name by cutting its middle: <paramref name="text"/> when it fits, else its first path segment, an
    /// ellipsis and as much of the end as fits (<c>dependabot/…site-1e18</c>), since the end is what tells similar
    /// branches apart. Falls back to an ellipsis and the end alone when the first segment leaves no room for at least a
    /// few characters of it. Whole graphemes only; binary search, like <see cref="Truncate"/>.
    /// </summary>
    public static string ElideMiddle(string? text, double maxWidth, Func<string, double> measure)
    {
        const int minTail = 4;
        if (string.IsNullOrEmpty(text)) return "";
        if (measure(text) <= maxWidth) return text;
        if (maxWidth <= 0) return "";
        int slash = text.IndexOf('/');
        if (slash > 0 && slash < text.Length - 1
            && FitTail(text[(slash + 1)..], text[..(slash + 1)] + Ellipsis, maxWidth, measure, minTail) is { } kept)
            return kept;
        return FitTail(text, Ellipsis, maxWidth, measure, 1) ?? Ellipsis;
    }

    // `lead` plus the longest whole-grapheme tail of `rest` that fits, or null when fewer than `minTail` graphemes do.
    private static string? FitTail(string rest, string lead, double maxWidth, Func<string, double> measure, int minTail)
    {
        var ends = GraphemeEnds(rest);
        int Start(int keep) => keep >= ends.Length ? 0 : ends[ends.Length - keep - 1];
        int lo = 0, hi = ends.Length;   // how many trailing graphemes to keep
        while (lo < hi)
        {
            int mid = (lo + hi + 1) / 2;
            if (measure(lead + rest[Start(mid)..]) <= maxWidth) lo = mid; else hi = mid - 1;
        }
        return lo < Math.Min(minTail, ends.Length) ? null : lead + rest[Start(lo)..];
    }

    /// <summary>The end offset of each grapheme cluster in <paramref name="text"/>, in order.</summary>
    internal static int[] GraphemeEnds(string text)
    {
        var ends = new List<int>(text.Length);
        for (int i = 0; i < text.Length;)
        {
            i += StringInfo.GetNextTextElementLength(text, i);
            ends.Add(i);
        }
        return ends.ToArray();
    }
}
