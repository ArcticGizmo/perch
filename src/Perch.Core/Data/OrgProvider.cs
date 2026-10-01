namespace Perch.Data;

/// <summary>
/// Observes the <b>live</b> org of a config dir, cheaply and repeatedly. Layer 2.
/// </summary>
/// <remarks>
/// Wraps <see cref="ClaudeJsonReader"/> with an mtime-keyed cache so the overlay can ask on every
/// repaint without re-parsing <c>.claude.json</c> each time — while a <c>/login</c> (which rewrites
/// <c>.claude.json</c>, changing its last-write time) invalidates the entry automatically. The org is
/// therefore never cached across a credential change, which is the whole point: a config dir has no
/// fixed org.
/// </remarks>
internal interface IOrgProvider
{
    /// <summary>The dir's live org, or <c>null</c> if not signed in / unreadable. Cheap on repeat calls
    /// (a cache hit avoids the file parse); re-reads automatically when the file changes.</summary>
    Org? GetLive(ClaudeConfigDir dir);

    /// <summary>Drop all cached entries — e.g. a user-driven manual refresh.</summary>
    void Invalidate();
}

/// <summary>The default file-backed <see cref="IOrgProvider"/>: an mtime cache over
/// <see cref="ClaudeJsonReader.ReadLiveOrg(string)"/>.</summary>
internal sealed class OrgProvider : IOrgProvider
{
    private readonly record struct Entry(long Stamp, Org? Org, long CheckedAtMs);

    /// <summary>How long a cached answer is trusted before the file stamps are looked at again (review fixes
    /// CP23): the overlay asks several times a second, and a <c>/login</c> showing up two seconds late is fine.</summary>
    internal static readonly TimeSpan DefaultRecheck = TimeSpan.FromSeconds(2);

    private readonly object _gate = new();
    private readonly Dictionary<string, Entry> _cache = new(ClaudeConfigDir.PathComparer);
    private readonly Func<ClaudeConfigDir, Org?> _read;
    private readonly Func<ClaudeConfigDir, long> _stamp;
    private readonly Func<long> _nowMs;
    private readonly long _recheckMs;

    public OrgProvider() : this(ClaudeJsonReader.ReadLiveOrg, DefaultStamp, () => Environment.TickCount64, DefaultRecheck) { }

    /// <summary>Test seam: inject the read and the change-stamp so tests need no real files. Re-stamps on
    /// every call (no recheck window).</summary>
    internal OrgProvider(Func<ClaudeConfigDir, Org?> read, Func<ClaudeConfigDir, long> stamp)
        : this(read, stamp, () => 0, TimeSpan.Zero) { }

    /// <summary>Test seam with a clock: within <paramref name="recheck"/> of the last stamp check, a cached
    /// entry is returned without touching the file system.</summary>
    internal OrgProvider(Func<ClaudeConfigDir, Org?> read, Func<ClaudeConfigDir, long> stamp, Func<long> nowMs, TimeSpan recheck)
    {
        _read = read;
        _stamp = stamp;
        _nowMs = nowMs;
        _recheckMs = (long)recheck.TotalMilliseconds;
    }

    public Org? GetLive(ClaudeConfigDir dir)
    {
        // Keyed by RealRoot so link-aliases of one physical dir share a cache entry. (.claude.json is never
        // junctioned — only projects/sessions/plugins are — so one RealRoot means one file.)
        long now = _nowMs();
        lock (_gate)
        {
            if (_recheckMs > 0 && _cache.TryGetValue(dir.RealRoot, out var fresh) && now - fresh.CheckedAtMs < _recheckMs)
                return fresh.Org;
        }

        var stamp = _stamp(dir);
        lock (_gate)
        {
            if (_cache.TryGetValue(dir.RealRoot, out var e) && e.Stamp == stamp)
            {
                _cache[dir.RealRoot] = e with { CheckedAtMs = now };
                return e.Org;
            }
            var org = _read(dir);
            _cache[dir.RealRoot] = new Entry(stamp, org, now);
            return org;
        }
    }

    public void Invalidate()
    {
        lock (_gate)
            _cache.Clear();
    }

    /// <summary>A change-stamp combining the last-write times of both <c>.claude.json</c> locations the
    /// reader may consult (inside the dir, and the parent/home file the default dir uses). Either file
    /// changing — a <c>/login</c> in either place — bumps the stamp and invalidates the cache; when
    /// neither exists the stamp is a stable <c>0</c>.</summary>
    internal static long DefaultStamp(ClaudeConfigDir dir)
    {
        long s = StampOf(dir.ClaudeJsonFile);
        var parent = Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(dir.Root));
        if (!string.IsNullOrEmpty(parent))
            s = s * 397 + StampOf(Path.Combine(parent, ".claude.json"));
        return s;
    }

    private static long StampOf(string path)
    {
        try
        {
            return File.Exists(path) ? File.GetLastWriteTimeUtc(path).Ticks : 0L;
        }
        catch
        {
            return 0L;
        }
    }
}
