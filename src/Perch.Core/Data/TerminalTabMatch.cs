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
/// Only an unambiguous match counts. Two tabs with the same title (several untitled "Claude Code" tabs)
/// report <see cref="TabMatchKind.Ambiguous"/> rather than a guess; the caller can then tag the session's
/// title with a one-off marker (<see cref="Mark"/>) and look for that instead (<see cref="FindMarked"/>).
/// A tab the user renamed matches nothing either way.
/// </para>
/// </summary>
public static class TerminalTabMatch
{
    /// <summary>
    /// Matches <paramref name="consoleTitle"/> against <paramref name="tabNames"/>, telling a single match
    /// apart from several look-alikes (<see cref="TabMatch.Count"/> of them) and from none.
    /// </summary>
    public static TabMatch Match(IReadOnlyList<string> tabNames, string? consoleTitle)
    {
        var wanted = Normalize(consoleTitle);
        if (wanted.Length == 0) return new(TabMatchKind.None, -1, 0);

        int found = -1, count = 0;
        for (int i = 0; i < tabNames.Count; i++)
        {
            if (!string.Equals(Normalize(tabNames[i]), wanted, StringComparison.Ordinal)) continue;
            if (count++ == 0) found = i;
        }
        return count switch
        {
            0 => new(TabMatchKind.None, -1, 0),
            1 => new(TabMatchKind.Found, found, 1),
            _ => new(TabMatchKind.Ambiguous, -1, count),
        };
    }

    /// <summary>
    /// <paramref name="consoleTitle"/> with a marker appended that no other tab can carry. Appended rather than
    /// substituted so the tab still reads as itself for the moment the marker is visible, and a bird so that
    /// moment reads as Perch's doing. The token keeps a marker stranded by a failed restore from ever matching
    /// a later click.
    /// </summary>
    public static string Mark(string consoleTitle, string token) => $"{consoleTitle}{MarkerPrefix}{token}";

    private const string MarkerPrefix = " \U0001F426";

    /// <summary>The index of the one tab whose name carries <paramref name="markedTitle"/>'s marker, or -1.</summary>
    public static int FindMarked(IReadOnlyList<string> tabNames, string markedTitle)
    {
        int open = markedTitle.LastIndexOf(MarkerPrefix, StringComparison.Ordinal);
        if (open < 0) return -1;
        var marker = markedTitle[open..];

        int found = -1;
        for (int i = 0; i < tabNames.Count; i++)
        {
            if (!tabNames[i].Contains(marker, StringComparison.Ordinal)) continue;
            if (found >= 0) return -1;
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

public enum TabMatchKind { None, Found, Ambiguous }

/// <summary>A <see cref="TerminalTabMatch.Match"/> result: the tab's index when <see cref="Kind"/> is
/// <see cref="TabMatchKind.Found"/>, else -1; <see cref="Count"/> is how many tabs carried the title.</summary>
public readonly record struct TabMatch(TabMatchKind Kind, int Index, int Count);
