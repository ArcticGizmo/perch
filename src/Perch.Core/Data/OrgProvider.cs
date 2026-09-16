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
    private readonly record struct Entry(long Stamp, Org? Org);

    private readonly object _gate = new();
    private readonly Dictionary<string, Entry> _cache = new(ClaudeConfigDir.PathComparer);
    private readonly Func<ClaudeConfigDir, Org?> _read;
    private readonly Func<ClaudeConfigDir, long> _stamp;

    public OrgProvider() : this(ClaudeJsonReader.ReadLiveOrg, DefaultStamp) { }

    /// <summary>Test seam: inject the read and the change-stamp so tests need no real files or clock.</summary>
    internal OrgProvider(Func<ClaudeConfigDir, Org?> read, Func<ClaudeConfigDir, long> stamp)
    {
        _read = read;
        _stamp = stamp;
    }

    public Org? GetLive(ClaudeConfigDir dir)
    {
        var stamp = _stamp(dir);
        lock (_gate)
        {
            // Keyed by RealRoot so link-aliases of one physical dir share a cache entry. (.claude.json is
            // never junctioned — only projects/sessions/plugins are — so one RealRoot means one file.)
            if (_cache.TryGetValue(dir.RealRoot, out var e) && e.Stamp == stamp)
                return e.Org;
            var org = _read(dir);
            _cache[dir.RealRoot] = new Entry(stamp, org);
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
    private static long DefaultStamp(ClaudeConfigDir dir)
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
