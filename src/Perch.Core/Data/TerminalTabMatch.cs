namespace Perch.Data;

/// <summary>
/// Picks which tab of a tabbed terminal (Windows Terminal) hosts a session, from the tab names and the
/// session's own console title. Pure and UI-free so the rule is testable; the platform head reads the names
/// (UI Automation) and the title (the session's console) and does the selecting.
/// <para>
/// Claude Code sets the console title to a status glyph plus the session title (<c>"✳ Fix login"</c>), and
/// Windows Terminal shows that title as the tab name. The glyph animates (<c>✳ ◐ ◑ …</c>), so the name read
/// a moment later can carry a different one: both sides are compared with their leading symbols stripped.
/// </para>
/// <para>
/// Only an unambiguous match counts. Two tabs with the same title, a tab the user renamed, or a console
/// title that is still the default ("Claude Code" in several tabs) yield no match, and the caller keeps
/// to raising the window alone rather than guessing a tab.
/// </para>
/// </summary>
public static class TerminalTabMatch
{
    /// <summary>The index of the one tab whose name matches <paramref name="consoleTitle"/>, or -1.</summary>
    public static int FindTab(IReadOnlyList<string> tabNames, string? consoleTitle)
    {
        var wanted = Normalize(consoleTitle);
        if (wanted.Length == 0) return -1;

        int found = -1;
        for (int i = 0; i < tabNames.Count; i++)
        {
            if (!string.Equals(Normalize(tabNames[i]), wanted, StringComparison.Ordinal)) continue;
            if (found >= 0) return -1; // ambiguous: never pick one of two look-alike tabs
            found = i;
        }
        return found;
    }

    // Drops leading symbols and whitespace (the status glyph and the space after it), so "✳ Fix login" and
    // "◐ Fix login" compare equal. A title that is all symbols normalizes to empty and never matches.
    internal static string Normalize(string? title)
    {
        if (string.IsNullOrEmpty(title)) return "";
        int start = 0;
        while (start < title.Length && !char.IsLetterOrDigit(title[start])) start++;
        return title[start..].TrimEnd();
    }
}
