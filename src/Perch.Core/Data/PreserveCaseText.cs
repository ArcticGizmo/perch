using System.Linq;

namespace Perch.Data;

/// <summary>
/// VS Code-style "preserve case" for find/replace: copy the casing <em>shape</em> of the matched text onto the
/// replacement so a case-insensitive search-and-replace keeps the surrounding prose's capitalisation. Pure and
/// side-effect-free so the editor's replace (which also handles regex group expansion and the undo-tracked
/// TextBox edit) can lean on a tested helper for just the casing rule.
/// </summary>
public static class PreserveCaseText
{
    /// <summary>
    /// Return <paramref name="replacement"/> recased to match <paramref name="matched"/>: an ALL-UPPERCASE match
    /// uppercases it, an all-lowercase match lowercases it, and a Capitalized match (leading upper, e.g. "Colour")
    /// capitalises the first letter. Any other/mixed shape — or a match/replacement with no letters — leaves the
    /// replacement untouched.
    /// </summary>
    public static string Apply(string matched, string replacement)
    {
        if (replacement.Length == 0 || string.IsNullOrEmpty(matched))
            return replacement;
        var letters = matched.Where(char.IsLetter).ToArray();
        if (letters.Length == 0)
            return replacement;               // no letters to take a shape from (digits/symbols)
        if (letters.All(char.IsUpper))
            return replacement.ToUpperInvariant();
        if (letters.All(char.IsLower))
            return replacement.ToLowerInvariant();
        if (char.IsUpper(matched[0]))         // Capitalized (leading upper, mixed rest)
            return char.ToUpperInvariant(replacement[0]) + replacement[1..];
        return replacement;                   // mixed with a lowercase lead — leave as authored
    }
}
