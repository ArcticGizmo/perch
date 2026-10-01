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
