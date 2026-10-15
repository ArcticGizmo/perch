namespace Perch.Data;

/// <summary>
/// The two-letter monogram a session wears on the collapsed docked column's tiles, derived from its display
/// name. Two words give their first letters ("my-api" → "MA", "PerchApp" → "PA"); a single word gives its
/// first two letters ("perch" → "PE"). Always upper case, and "?" for a name with no letters or digits, so a
/// tile always has something to draw. Two sessions in the same project share a monogram; the tile's tooltip
/// names them in full.
/// </summary>
public static class SessionInitials
{
    public static string For(string? displayName)
    {
        var words = Words(displayName ?? "");
        if (words.Count == 0) return "?";
        if (words.Count == 1)
            return words[0].Length >= 2 ? words[0][..2].ToUpperInvariant() : words[0].ToUpperInvariant();
        return $"{char.ToUpperInvariant(words[0][0])}{char.ToUpperInvariant(words[1][0])}";
    }

    // Splits on anything that isn't a letter or digit, and at a lower→upper camelCase boundary.
    private static List<string> Words(string name)
    {
        var words = new List<string>();
        var current = new System.Text.StringBuilder();
        foreach (var ch in name)
        {
            if (!char.IsLetterOrDigit(ch))
            {
                if (current.Length > 0) { words.Add(current.ToString()); current.Clear(); }
                continue;
            }
            if (char.IsUpper(ch) && current.Length > 0 && char.IsLower(current[^1]))
            {
                words.Add(current.ToString());
                current.Clear();
            }
            current.Append(ch);
        }
        if (current.Length > 0) words.Add(current.ToString());
        return words;
    }
}
