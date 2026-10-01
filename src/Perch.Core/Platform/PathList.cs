namespace Perch.Platform;

/// <summary>
/// Edits a PATH value as the user's raw registry string (review fixes CP19): entries such as
/// <c>%USERPROFILE%\bin</c> stay unexpanded, and every entry we don't own is kept exactly as written, including
/// its spacing, order and any empty segments. Both edits return null when nothing would change, so the caller
/// writes (and broadcasts) only on a real change. Pure: the platform head does the registry IO.
/// </summary>
internal static class PathList
{
    /// <summary><paramref name="current"/> with <paramref name="dir"/> appended, or null when it's already on it.
    /// An entry matches whether written literally or with variables that <paramref name="expand"/> resolves to
    /// <paramref name="dir"/>, ignoring case and trailing slashes.</summary>
    public static string? WithEntry(string current, string dir, Func<string, string> expand)
    {
        if (current.Split(';').Any(e => Matches(e, dir, expand))) return null;
        var trimmed = current.TrimEnd(';');
        return trimmed.Length == 0 ? dir : trimmed + ";" + dir;
    }

    /// <summary><paramref name="current"/> without any entry matching <paramref name="dir"/> (see
    /// <see cref="WithEntry"/>), or null when there's none. The other entries are untouched.</summary>
    public static string? WithoutEntry(string current, string dir, Func<string, string> expand)
    {
        var entries = current.Split(';');
        var kept = entries.Where(e => !Matches(e, dir, expand)).ToArray();
        return kept.Length == entries.Length ? null : string.Join(';', kept);
    }

    private static bool Matches(string entry, string dir, Func<string, string> expand)
    {
        var e = entry.Trim();
        if (e.Length == 0) return false;
        return Same(e, dir) || (e.Contains('%') && Same(expand(e), dir));
    }

    private static bool Same(string a, string b) =>
        string.Equals(a.Trim().TrimEnd('\\', '/'), b.Trim().TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase);
}
