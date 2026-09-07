namespace Perch.Data;

/// <summary>
/// Shared, best-effort reading of a session transcript. Centralises the boilerplate every reader
/// repeated: open the file with <see cref="FileShare.ReadWrite"/> (transcripts are appended live), and
/// — for the tail scanners — seek to the last window and drop the partial first line so parsing resumes
/// on a clean boundary. The line enumerators are lazy iterators; the underlying stream is opened when
/// enumeration begins and disposed when it ends (or the caller breaks out).
/// </summary>
internal static class TranscriptScan
{
    /// <summary>Opens a transcript for shared reading — it may be written concurrently by Claude Code.</summary>
    public static FileStream OpenShared(string path) =>
        new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);

    /// <summary>Every line of the file, in order.</summary>
    public static IEnumerable<string> ReadLines(string path) => ReadLinesFrom(path, 0);

    /// <summary>
    /// Lines from byte offset <paramref name="start"/> onward. When <paramref name="start"/> is non-zero the
    /// first line is, by default, discarded (a mid-file seek almost certainly lands inside a line, so this
    /// yields whole records). Pass <paramref name="dropPartialFirst"/> = false when <paramref name="start"/> is
    /// known to be an exact line boundary (e.g. a previously recorded file length) so the first appended record
    /// isn't dropped.
    /// </summary>
    public static IEnumerable<string> ReadLinesFrom(string path, long start, bool dropPartialFirst = true)
    {
        using var fs = OpenShared(path);
        if (start > 0)
            fs.Seek(start, SeekOrigin.Begin);
        using var reader = new StreamReader(fs);
        if (start > 0 && dropPartialFirst)
            reader.ReadLine();   // drop the partial line the seek landed in the middle of

        string? line;
        while ((line = reader.ReadLine()) != null)
            yield return line;
    }

    /// <summary>The last <paramref name="tailBytes"/> of the file, line by line (partial first line
    /// dropped). Used by the activity / bare-command scanners, where only the most recent records matter.</summary>
    public static IEnumerable<string> ReadTailLines(string path, int tailBytes)
    {
        long len = new FileInfo(path).Length;
        return ReadLinesFrom(path, Math.Max(0, len - tailBytes));
    }
}
