using System.Text.RegularExpressions;

namespace Perch.Data.Control;

/// <summary>
/// The <c>[Image #N]</c> placeholder an attached image leaves in the composer text, as the Claude Code terminal
/// does: it shows where the image sits in the prompt, gives the user (and the model) a name to refer to it by,
/// and deleting it from the text drops the image.
/// </summary>
internal static partial class ImageMarker
{
    /// <summary>The marker for image number <paramref name="n"/>, e.g. <c>[Image #2]</c>.</summary>
    public static string Format(int n) => $"[Image #{n}]";

    /// <summary>Every marker in <paramref name="text"/>, as <c>(Start, Length)</c> spans in order.</summary>
    public static IEnumerable<(int Start, int Length)> Find(string? text)
    {
        if (string.IsNullOrEmpty(text)) yield break;
        foreach (Match m in Pattern().Matches(text)) yield return (m.Index, m.Length);
    }

    /// <summary>The marker a Backspace (<paramref name="backward"/>) or Delete at <paramref name="caret"/> would
    /// bite into — the caret at its end or inside it for Backspace, at its start or inside it for Delete — so the
    /// composer can remove it whole rather than leave a broken <c>[Image #</c>. Null when the key would touch none.</summary>
    public static (int Start, int Length)? SpanToDelete(string? text, int caret, bool backward)
    {
        foreach (var (start, length) in Find(text))
            if (backward ? start < caret && caret <= start + length : start <= caret && caret < start + length)
                return (start, length);
        return null;
    }

    [GeneratedRegex(@"\[Image #\d+\]")]
    private static partial Regex Pattern();
}
