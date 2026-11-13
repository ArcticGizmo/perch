namespace Perch.Data;

/// <summary>
/// A per-file memoiser keyed by (length, last-write-time): it returns the cached value while the file on
/// disk is unchanged, and recomputes only when the file's size or mtime moves. This is the pattern every
/// transcript reader relies on so a scan over an unchanged transcript costs a stat, not a parse — it was
/// previously hand-rolled seven times (five in <see cref="TranscriptReader"/>, two in
/// <see cref="SubAgentReader"/>), each with its own cache-entry record and lookup block.
///
/// Not thread-safe; each reader owns its own instance and is driven from the UI thread's scan loop.
/// </summary>
internal sealed class MtimeCache<T>
{
    private sealed class Entry(long length, DateTime writeUtc, T value)
    {
        public readonly long Length = length;
        public readonly DateTime WriteUtc = writeUtc;
        public readonly T Value = value;
        public long LastUsedMs = Environment.TickCount64;
    }

    private readonly Dictionary<string, Entry> _entries = new();

    internal int Count => _entries.Count;

    /// <summary>Forgets every file not asked about for <paramref name="idle"/>, so a tray that runs for weeks
    /// doesn't keep a value for every transcript it ever saw. A forgotten file is just computed again.</summary>
    public void Sweep(TimeSpan idle)
    {
        long cutoff = Environment.TickCount64 - (long)idle.TotalMilliseconds;
        foreach (var (path, e) in _entries)
            if (e.LastUsedMs < cutoff) _entries.Remove(path);
    }

    /// <summary>
    /// Returns the memoised value for <paramref name="path"/>, invoking <paramref name="compute"/> only
    /// when the file's length or last-write-time has changed since the previous call. If the file can't
    /// be stat'd, or <paramref name="compute"/> throws, <paramref name="fallback"/> is returned and
    /// nothing is cached (mirroring the best-effort "never throw out of a scan" contract of the readers).
    /// </summary>
    public T GetOrCompute(string path, Func<string, T> compute, T fallback)
    {
        try
        {
            var fi = new FileInfo(path);
            if (_entries.TryGetValue(path, out var e) && e.Length == fi.Length && e.WriteUtc == fi.LastWriteTimeUtc)
            {
                e.LastUsedMs = Environment.TickCount64;
                return e.Value;
            }

            var value = compute(path);
            _entries[path] = new Entry(fi.Length, fi.LastWriteTimeUtc, value);
            return value;
        }
        catch
        {
            return fallback;
        }
    }
}
