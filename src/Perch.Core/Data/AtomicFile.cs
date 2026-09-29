using System.Text;

namespace Perch.Data;

/// <summary>
/// Crash-safe replacement of a whole file: the new content goes to a uniquely named temp file beside the
/// target, is flushed to disk, and is then moved over the target in one rename — so a reader (Claude Code, the
/// hook, another Perch) sees either the old file or the new one, never an empty or half-written one, and a kill
/// or power loss mid-save can't truncate it. The move is retried briefly on a sharing violation (another process
/// holding the target open for a moment). Review fixes CP14, docs/review-fixes-plan.md.
///
/// <para>The Perch.Hook NativeAOT app doesn't reference Core, so it carries its own copy of this routine; keep
/// the two in step.</para>
/// </summary>
public static class AtomicFile
{
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    // Move attempts and the pause after each failure (~0.6s total), for a target another process holds open.
    private static readonly int[] RetryDelaysMs = [20, 40, 80, 160, 300];

    /// <summary>Replaces <paramref name="path"/> with <paramref name="text"/> (UTF-8, no BOM — what
    /// <c>File.WriteAllText</c> writes), creating the directory if needed. Throws when the file can't be replaced;
    /// the temp file is never left behind.</summary>
    public static void Write(string path, string text)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        var tmp = $"{path}.{Guid.NewGuid():N}.tmp";
        try
        {
            using (var fs = new FileStream(tmp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                var bytes = Utf8NoBom.GetBytes(text);
                fs.Write(bytes, 0, bytes.Length);
                fs.Flush(flushToDisk: true);
            }
            for (int attempt = 0; ; attempt++)
            {
                try
                {
                    File.Move(tmp, path, overwrite: true);
                    return;
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException && attempt < RetryDelaysMs.Length)
                {
                    Thread.Sleep(RetryDelaysMs[attempt]);
                }
            }
        }
        finally
        {
            try { if (File.Exists(tmp)) File.Delete(tmp); } catch { /* best-effort */ }
        }
    }

    /// <summary>
    /// <see cref="Write"/>, skipped when <paramref name="path"/> already holds exactly <paramref name="text"/> —
    /// so an idempotent rewrite (a hook reconcile on every launch) never touches the file, its timestamp, or any
    /// watcher. Returns whether it wrote.
    /// </summary>
    public static bool WriteIfChanged(string path, string text)
    {
        if (ReadShared(path) is { } current && string.Equals(current, text, StringComparison.Ordinal)) return false;
        Write(path, text);
        return true;
    }

    /// <summary>Reads <paramref name="path"/> as text with shared access (tolerating a writer that has it open).
    /// Null when it doesn't exist or can't be read — callers that must tell those apart use <see cref="TryRead"/>.</summary>
    public static string? ReadShared(string path) => TryRead(path, out var text) == ReadResult.Ok ? text : null;

    /// <summary>The outcome of <see cref="TryRead"/>: whether the file is provably absent, was read, or exists but
    /// couldn't be read (locked, permissions, IO error). A caller about to rewrite the file must treat
    /// <see cref="Failed"/> as "don't touch it", never as "start fresh".</summary>
    public enum ReadResult { Missing, Ok, Failed }

    /// <summary>Reads <paramref name="path"/> as text with shared access, distinguishing a missing file from one
    /// that exists but couldn't be read.</summary>
    public static ReadResult TryRead(string path, out string text)
    {
        text = "";
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(fs, Utf8NoBom, detectEncodingFromByteOrderMarks: true);
            text = reader.ReadToEnd();
            return ReadResult.Ok;
        }
        catch (FileNotFoundException) { return ReadResult.Missing; }
        catch (DirectoryNotFoundException) { return ReadResult.Missing; }
        catch { return ReadResult.Failed; }
    }
}
