namespace Perch.Data;

/// <summary>
/// Extracts the last segment of a path splitting on <b>both</b> separators, regardless of host OS.
/// <see cref="System.IO.Path.GetFileName(string)"/> only honours the running platform's separator, so on
/// macOS/Linux a Windows-style <c>cwd</c> or <c>file_path</c> from a transcript (<c>C:\a\proj</c>) would
/// come back whole instead of as its leaf. Transcripts carry paths from whatever machine wrote them, so
/// leaf extraction must be separator-agnostic. Trailing separators are ignored.
/// </summary>
internal static class PathLeaf
{
    public static string Of(string path)
    {
        var trimmed = path.TrimEnd('/', '\\');
        int cut = trimmed.LastIndexOfAny(['/', '\\']);
        return cut >= 0 ? trimmed[(cut + 1)..] : trimmed;
    }
}
