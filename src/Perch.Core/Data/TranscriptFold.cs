using System.Text;
using System.Text.Json;

namespace Perch.Data;

/// <summary>One whole-transcript reducer for a <see cref="TranscriptFold"/>: a fresh state, and a step that folds
/// one line into it. Deriving the reader's result from the state is the caller's <c>finish</c>.</summary>
internal abstract class LineFolder
{
    internal abstract object NewState();
    internal abstract void Step(object state, string line);
}

/// <inheritdoc cref="LineFolder"/>
internal sealed class LineFolder<TState>(Func<TState> seed, Action<TState, string> step) : LineFolder where TState : class
{
    internal override object NewState() => seed();
    internal override void Step(object state, string line) => step((TState)state, line);

    /// <summary>Folds every line of <paramref name="lines"/> from a fresh state — the one-shot, whole-file form of
    /// the same reducer, so a static reader and the incremental fold can never disagree.</summary>
    public TState FoldAll(IEnumerable<string> lines)
    {
        var state = seed();
        foreach (var line in lines) step(state, line);
        return state;
    }
}

/// <summary>
/// Incrementally folds append-only transcripts (review fixes CP20, docs/review-fixes-plan.md). For each file it
/// remembers the byte offset it has consumed up to and one accumulated state per registered
/// <see cref="LineFolder"/>; when the file grows, only <c>[offset, EOF)</c> is read, and every folder is fed from
/// that one pass. That replaces the old model where each whole-file reader re-read the entire transcript on
/// every append (a length+mtime cache misses on every write to a live session).
///
/// <para>Rules, so the fold always equals a from-scratch read:</para>
/// <list type="bullet">
///   <item>Only whole lines are consumed. A trailing fragment is left for the next read, unless it is already a
///   complete JSON object (a writer that hasn't sent its newline yet); a strict prefix of a JSON object can never
///   parse as one, so that's safe.</item>
///   <item>The file shrinking below the offset (truncation), or its first bytes changing (it was replaced/rotated),
///   resets every state and re-reads from the start.</item>
///   <item>An unchanged (length, last-write) costs one stat, like <see cref="MtimeCache{T}"/>.</item>
/// </list>
/// <para>Not thread-safe: an instance is owned by one reader and driven by one scan at a time.</para>
/// </summary>
internal sealed class TranscriptFold
{
    private const int HeadBytes = 256;
    private const int ChunkBytes = 64 * 1024;
    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false);

    private readonly LineFolder[] _folders;
    private readonly Dictionary<string, Entry> _entries = new();

    /// <summary>Total bytes read from disk by this fold — a test/benchmark probe that proves a growth only reads
    /// the appended bytes.</summary>
    internal long BytesRead { get; private set; }

    public TranscriptFold(params LineFolder[] folders) => _folders = folders;

    private sealed class Entry
    {
        public long Offset;               // bytes consumed so far (always a line boundary, or EOF of a complete record)
        public long SeenLength = -1;      // length and mtime at the last read, for the unchanged short-circuit
        public DateTime SeenWriteUtc;
        public byte[] Head = [];          // the file's first bytes as consumed, to spot a replaced file
        public object[] States = [];
        public int Version;               // bumps whenever any state changes, so derived results can be cached
        public readonly Dictionary<int, (int Version, object? Result)> Results = new();
    }

    /// <summary>
    /// The result <paramref name="finish"/> derives from <paramref name="folder"/>'s state for
    /// <paramref name="path"/>, after folding in any newly appended lines. <paramref name="finish"/> re-runs only
    /// when the state changed. <paramref name="fallback"/> when the file can't be read, or anything throws.
    /// </summary>
    public TResult Get<TState, TResult>(string path, LineFolder<TState> folder, Func<TState, TResult> finish, TResult fallback)
        where TState : class
    {
        try
        {
            int index = Array.IndexOf(_folders, folder);
            if (index < 0) throw new ArgumentException("Folder not registered with this fold.", nameof(folder));
            var entry = Advance(path);
            if (entry.Results.TryGetValue(index, out var cached) && cached.Version == entry.Version)
                return (TResult)cached.Result!;
            var result = finish((TState)entry.States[index]);
            entry.Results[index] = (entry.Version, result);
            return result;
        }
        catch
        {
            return fallback;
        }
    }

    // Brings the entry for `path` up to date with the file on disk. Throws when the file can't be read (the
    // caller's fallback applies, and the entry keeps its last good state for the next attempt).
    private Entry Advance(string path)
    {
        var fi = new FileInfo(path);
        long length = fi.Length;   // throws FileNotFoundException for a missing file
        var writeUtc = fi.LastWriteTimeUtc;

        if (!_entries.TryGetValue(path, out var entry))
        {
            entry = new Entry();
            Reset(entry);
            _entries[path] = entry;
        }
        else if (entry.SeenLength == length && entry.SeenWriteUtc == writeUtc)
        {
            return entry;
        }

        using var fs = TranscriptScan.OpenShared(path);
        if (length < entry.Offset || !HeadMatches(fs, entry))
            Reset(entry);

        ReadFrom(fs, entry);
        entry.SeenLength = length;
        entry.SeenWriteUtc = writeUtc;
        return entry;
    }

    private void Reset(Entry entry)
    {
        entry.Offset = 0;
        entry.Head = [];
        entry.States = _folders.Select(f => f.NewState()).ToArray();
        entry.Version++;
    }

    // True when the file still starts with the bytes we consumed from it — else it was replaced, not appended to.
    private bool HeadMatches(FileStream fs, Entry entry)
    {
        if (entry.Head.Length == 0) return true;
        var buf = new byte[entry.Head.Length];
        fs.Seek(0, SeekOrigin.Begin);
        int n = ReadFully(fs, buf);
        BytesRead += n;
        return n == buf.Length && buf.AsSpan().SequenceEqual(entry.Head);
    }

    // Reads [Offset, EOF), feeding each whole line to every folder. Offset and Version advance with every line
    // fed (see Feed), so an IO error part-way leaves a consistent entry: the next read resumes after the last line
    // that was folded in, never feeding it twice.
    private void ReadFrom(FileStream fs, Entry entry)
    {
        fs.Seek(entry.Offset, SeekOrigin.Begin);
        var pending = new MemoryStream();   // bytes of the current, not-yet-terminated line
        var chunk = new byte[ChunkBytes];
        int n;
        while ((n = fs.Read(chunk, 0, chunk.Length)) > 0)
        {
            BytesRead += n;
            long chunkAt = entry.Offset + pending.Length;   // file offset of chunk[0]
            if (entry.Head.Length < HeadBytes && chunkAt < HeadBytes)
                CaptureHead(entry, chunk, n, chunkAt);

            int start = 0;
            for (int i = 0; i < n; i++)
            {
                if (chunk[i] != (byte)'\n') continue;
                pending.Write(chunk, start, i - start);
                Feed(entry, Decode(pending, atFileStart: entry.Offset == 0), pending.Length + 1);
                pending.SetLength(0);
                start = i + 1;
            }
            pending.Write(chunk, start, n - start);
        }

        // A trailing fragment: a complete record whose newline hasn't landed yet is safe to take now.
        if (pending.Length > 0)
        {
            var tail = Decode(pending, atFileStart: entry.Offset == 0);
            if (IsCompleteJsonObject(tail))
                Feed(entry, tail, pending.Length);
        }
    }

    // Folds one line into every state, then moves the offset past its `bytes` (the line plus its newline).
    private void Feed(Entry entry, string line, long bytes)
    {
        for (int f = 0; f < _folders.Length; f++)
        {
            try { _folders[f].Step(entry.States[f], line); }
            catch { /* a malformed line is that folder's to skip; the others still see it */ }
        }
        entry.Offset += bytes;
        entry.Version++;
    }

    // Records the file's leading bytes (from this chunk, which starts at file offset `at`) for replacement checks.
    private static void CaptureHead(Entry entry, byte[] chunk, int n, long at)
    {
        int want = (int)Math.Min(HeadBytes - at, n);
        if (want <= 0 || at != entry.Head.Length) return;
        var head = new byte[entry.Head.Length + want];
        entry.Head.CopyTo(head, 0);
        Array.Copy(chunk, 0, head, entry.Head.Length, want);
        entry.Head = head;
    }

    private static string Decode(MemoryStream line, bool atFileStart)
    {
        var bytes = line.GetBuffer().AsSpan(0, (int)line.Length);
        if (atFileStart && bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
            bytes = bytes[3..];   // a BOM, as StreamReader would strip it
        if (bytes.Length > 0 && bytes[^1] == (byte)'\r') bytes = bytes[..^1];
        return Utf8.GetString(bytes);
    }

    private static bool IsCompleteJsonObject(string text)
    {
        var t = text.AsSpan().Trim();
        if (t.Length < 2 || t[0] != '{' || t[^1] != '}') return false;
        try { using var _ = JsonDocument.Parse(text); return true; }
        catch { return false; }
    }

    private static int ReadFully(Stream s, byte[] buf)
    {
        int total = 0, n;
        while (total < buf.Length && (n = s.Read(buf, total, buf.Length - total)) > 0) total += n;
        return total;
    }
}
