namespace Perch.Data;

/// <summary>
/// The set of Claude Code config directories Perch is aware of: the <see cref="Primary"/> (the one
/// <see cref="ClaudePaths"/> resolves at start-up) plus any declared or discovered dirs. A static
/// ambient owner, mirroring the <see cref="ClaudePaths"/> / <see cref="Clock"/> precedent — the
/// codebase has no DI container and config-dir paths are read from many scattered statics.
/// </summary>
/// <remarks>
/// <para>The primary is snapshotted on first access (a static field initializer), so tests that set
/// <c>CLAUDE_CONFIG_DIR</c> in a module initializer are honoured exactly as they were for
/// <see cref="ClaudePaths"/>.</para>
/// <para><b>Hermetic under a pinned <c>CLAUDE_CONFIG_DIR</c>.</b> When the variable is set (tests, the
/// replay sandbox, a user who bound it in their profile), discovery is disabled and <see cref="All"/>
/// stays <c>[Primary]</c> — the whole set is pinned to exactly that one dir.</para>
/// <para><see cref="Instance"/> is swapped wholesale on each refresh; <see cref="Changed"/> fires when
/// the membership actually changes. <see cref="Primary"/> is stable across refreshes.</para>
/// </remarks>
internal sealed class ClaudeConfigSet
{
    // NOTE: declaration order matters — Home, IsPinned and _resolveReal must initialise BEFORE Instance,
    // since BuildInitial() reads all three. Static field initializers run top-to-bottom.

    // The active link resolver. Swappable only by a test (junctions need elevation and won't exist in CI,
    // so a test maps two paths onto one "real" path to exercise dedup / shared-sessions detection).
    private static Func<string, string> _resolveReal = DefaultResolveReal;

    // Sticky memory of self-reported roots (a running session's hook stamped a {sid}.configdir naming the
    // dir). Persisted so a dir revealed once survives the tray restarting, and writable under the M4
    // safe-write policy. Keyed by declared-form path. Loaded once (non-pinned) by EnsureSelfReportedLoaded.
    // Declared before Instance: BuildInitial reads them.
    private static readonly HashSet<string> _selfReported = new(ClaudeConfigDir.PathComparer);
    private static bool _selfReportedLoaded;

    /// <summary>The current user's profile directory (e.g. <c>C:\Users\me</c>).</summary>
    public static string Home { get; } =
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    /// <summary>True when <c>CLAUDE_CONFIG_DIR</c> was set at start-up: discovery is then disabled and
    /// the set is pinned to the primary alone.</summary>
    public static bool IsPinned { get; } =
        !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR"));

    /// <summary>The process-wide config set. Swapped wholesale by <see cref="RefreshNow"/>.</summary>
    public static ClaudeConfigSet Instance { get; private set; } = BuildInitial();

    /// <summary>Raised (off whatever thread refreshed) when the discovered membership changes.</summary>
    public static event Action? Changed;

    private const long RefreshFloorMs = 5_000;
    private static readonly object RefreshGate = new();
    private static long _lastRefreshTick = long.MinValue;
    private static Func<IReadOnlyList<string>>? _declaredRootsProvider;
    private static Func<IReadOnlyList<ConfigDirLabel>>? _labelsProvider;
    private static Func<IReadOnlyList<string>>? _suppressedProvider;

    /// <summary>The primary config dir — <c>CLAUDE_CONFIG_DIR</c> if set, else <c>~/.claude</c>.</summary>
    public ClaudeConfigDir Primary { get; }

    private readonly IReadOnlyList<ClaudeConfigDir> _all;
    // Lazily-derived, per-instance cache of the real sessions/ paths that more than one config dir shares
    // (a junctioned/linked layout). Cached against set identity — Instance is immutable and swapped
    // wholesale on change, so a fresh derivation rides in with each new set. Null = not yet computed.
    private HashSet<string>? _sharedSessionsReal;

    /// <summary>Every config dir in the set, primary first, deduped by real path.</summary>
    public IReadOnlyList<ClaudeConfigDir> All => _all;

    /// <summary>True when the set holds more than one distinct config dir.</summary>
    public bool IsMulti => _all.Count > 1;

    private ClaudeConfigSet(ClaudeConfigDir primary, IReadOnlyList<ClaudeConfigDir> all)
    {
        Primary = primary;
        _all = all;
    }

    /// <summary>The distinct <c>sessions/</c> directories across the set, deduped by resolved real path
    /// (schemes routinely junction one physical <c>sessions/</c> across dirs; enumerate naively and you
    /// read every sidecar once per dir).</summary>
    public IReadOnlyList<string> DistinctSessionsDirs() => DistinctByReal(d => d.SessionsDir);

    /// <summary>The distinct <c>projects/</c> directories across the set, deduped by resolved real
    /// path.</summary>
    public IReadOnlyList<string> DistinctProjectsDirs() => DistinctByReal(d => d.ProjectsDir);

    /// <summary>One representative config dir per distinct <c>sessions/</c> real path (primary first) —
    /// the owner to scan for sidecars and to tag sessions with. For a <b>shared</b> <c>sessions/</c>
    /// (several dirs junctioned onto one; M3) the first owner wins here; the self-reported
    /// <c>.configdir</c> marker (M3) then re-attributes each session within that shared folder.</summary>
    public IReadOnlyList<ClaudeConfigDir> DistinctSessionOwners()
    {
        var result = new List<ClaudeConfigDir>();
        var seen = new HashSet<string>(ClaudeConfigDir.PathComparer);
        foreach (var dir in _all)
            if (seen.Add(ResolveReal(dir.SessionsDir))) result.Add(dir);
        return result;
    }

    private IReadOnlyList<string> DistinctByReal(Func<ClaudeConfigDir, string> select)
    {
        var result = new List<string>();
        var seen = new HashSet<string>(ClaudeConfigDir.PathComparer);
        foreach (var dir in _all)
        {
            var path = select(dir);
            if (seen.Add(ResolveReal(path))) result.Add(path);
        }
        return result;
    }

    /// <summary>The set entry that owns <paramref name="root"/> (matched by resolved real path), or null
    /// when no dir in the set corresponds to it. Never falls back to the primary — an unknown root
    /// resolves to nothing, so a session is never mis-attributed to whichever dir happens to be first.</summary>
    public ClaudeConfigDir? ForRoot(string? root)
    {
        if (string.IsNullOrWhiteSpace(root)) return null;
        var real = ResolveReal(root);
        foreach (var dir in _all)
            if (ClaudeConfigDir.PathComparer.Equals(dir.RealRoot, real))
                return dir;
        return null;
    }

    /// <summary>The set entry whose <see cref="ClaudeConfigDir.Slug"/> or <see cref="ClaudeConfigDir.Label"/>
    /// matches <paramref name="slug"/>, or null. The fallback for the legacy <c>.slug</c> self-report
    /// marker; like <see cref="ForRoot"/> it never falls back to the primary.</summary>
    public ClaudeConfigDir? ForSlug(string? slug)
    {
        if (string.IsNullOrWhiteSpace(slug)) return null;
        foreach (var dir in _all)
            if (string.Equals(dir.Slug, slug, StringComparison.Ordinal) ||
                string.Equals(dir.Label, slug, ClaudeConfigDir.PathComparer == StringComparer.Ordinal
                    ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase))
                return dir;
        return null;
    }

    /// <summary>
    /// True when <paramref name="dir"/>'s <c>sessions/</c> folder is <b>shared</b> — another config dir in
    /// the set resolves to the same real <c>sessions/</c> path (a junctioned layout). In that case the
    /// folder alone can't say which dir ran a session, so attribution needs the self-reported marker
    /// (M3). The derivation is cached against this immutable set.
    /// </summary>
    public bool SharesSessionsDir(ClaudeConfigDir dir) =>
        SharedSessionsReal().Contains(ResolveReal(dir.SessionsDir));

    private HashSet<string> SharedSessionsReal()
    {
        if (_sharedSessionsReal is { } cached) return cached;

        var counts = new Dictionary<string, int>(ClaudeConfigDir.PathComparer);
        foreach (var dir in _all)
        {
            var real = ResolveReal(dir.SessionsDir);
            counts[real] = counts.TryGetValue(real, out var n) ? n + 1 : 1;
        }
        var shared = new HashSet<string>(ClaudeConfigDir.PathComparer);
        foreach (var (real, n) in counts)
            if (n > 1) shared.Add(real);
        return _sharedSessionsReal = shared;
    }

    /// <summary>
    /// Wires the source of declared config-dir roots (the app passes the live
    /// <c>AppSettings.DeclaredConfigDirs</c>) and forces an immediate refresh so they take effect at
    /// once. No-op under a pinned <c>CLAUDE_CONFIG_DIR</c>. Idempotent.
    /// </summary>
    public static void ConfigureDeclaredRoots(Func<IReadOnlyList<string>> provider)
    {
        _declaredRootsProvider = provider;
        RefreshNow();
    }

    /// <summary>
    /// Wires all three settings-backed inputs at once — the declared roots (backbone), the custom labels
    /// (chip display), and the hidden dirs (removed from the set) — and forces a single immediate refresh so
    /// they take effect together. The app calls this at start-up and whenever the config-directories editor
    /// changes anything. No-op under a pinned <c>CLAUDE_CONFIG_DIR</c>. Idempotent.
    /// </summary>
    public static void ConfigureFromSettings(
        Func<IReadOnlyList<string>> declaredRoots,
        Func<IReadOnlyList<ConfigDirLabel>> labels,
        Func<IReadOnlyList<string>> hiddenRoots)
    {
        _declaredRootsProvider = declaredRoots;
        _labelsProvider = labels;
        _suppressedProvider = hiddenRoots;
        RefreshNow();
    }

    /// <summary>Re-runs discovery if at least the throttle floor (~5s) has elapsed since the last
    /// refresh. Cheap to call on every scan. No-op while pinned.</summary>
    public static void RefreshIfStale()
    {
        if (IsPinned) return;
        var now = Environment.TickCount64;
        lock (RefreshGate)
        {
            if (now - _lastRefreshTick < RefreshFloorMs) return;
        }
        RefreshNow();
    }

    /// <summary>Re-runs discovery immediately (bypassing the throttle) and swaps <see cref="Instance"/>,
    /// firing <see cref="Changed"/> if membership changed. No-op while pinned.</summary>
    public static void RefreshNow()
    {
        if (IsPinned) return;

        ClaudeConfigSet next;
        lock (RefreshGate)
        {
            _lastRefreshTick = Environment.TickCount64;
            EnsureSelfReportedLoaded();
            var declared = SafeDeclaredRoots();
            var all = ClaudeConfigDiscovery.Discover(
                Home, Instance.Primary, declared, _selfReported.ToList(),
                labels: SafeLabels(), suppressedRealRoots: SafeSuppressed());
            // Discover always emits the primary first (and never hides it), so all[0] is the primary — carry
            // the labelled copy through so the set's Primary and All[0] agree.
            next = new ClaudeConfigSet(all[0], all);
            if (SameMembership(Instance._all, next._all)) return;
            Instance = next;
        }
        Changed?.Invoke();
    }

    /// <summary>
    /// Records that a running session self-reported <paramref name="root"/> as its config dir (a
    /// <c>.configdir</c> marker naming it was seen). Promotes a would-be convention dir to writable and
    /// makes it sticky across restarts. No-op while pinned, when the dir is already primary/declared/self-
    /// reported, or on a blank/invalid path. Persists and refreshes only when it actually learns something.
    /// </summary>
    public static void NoteSelfReport(string? root)
    {
        if (SelfReportSuppressed || string.IsNullOrWhiteSpace(root)) return;
        var full = SafeFullPath(root);
        if (full is null) return;

        // A dir the user explicitly removed stays removed even if a running session self-reports it.
        var hidden = new HashSet<string>(SafeSuppressed(), ClaudeConfigDir.PathComparer);
        if (hidden.Contains(ResolveReal(full))) return;

        bool learned;
        lock (RefreshGate)
        {
            EnsureSelfReportedLoaded();
            if (_selfReported.Contains(full)) return;
            // Already writable through a higher tier (primary or an explicit declaration)? Nothing to add.
            if (Instance.ForRoot(full) is { IsWritable: true }) return;
            learned = _selfReported.Add(full);
        }
        if (!learned) return;
        SaveSelfReported();
        RefreshNow();
    }

    private static IReadOnlyList<string> SafeDeclaredRoots()
    {
        try
        {
            return _declaredRootsProvider?.Invoke() ?? [];
        }
        catch
        {
            return [];
        }
    }

    /// <summary>The custom labels as a real-path-keyed map (OS-appropriate comparer), best-effort.</summary>
    private static IReadOnlyDictionary<string, string> SafeLabels()
    {
        var map = new Dictionary<string, string>(ClaudeConfigDir.PathComparer);
        try
        {
            foreach (var e in _labelsProvider?.Invoke() ?? [])
                if (e is { } entry && !string.IsNullOrWhiteSpace(entry.Path) && !string.IsNullOrWhiteSpace(entry.Label))
                    map[Path.TrimEndingDirectorySeparator(entry.Path)] = entry.Label.Trim();
        }
        catch
        {
            // Best-effort: a bad labels list just means no custom labels this refresh.
        }
        return map;
    }

    /// <summary>The hidden (removed) dirs' real paths, best-effort.</summary>
    private static IReadOnlyList<string> SafeSuppressed()
    {
        try
        {
            return _suppressedProvider?.Invoke() ?? [];
        }
        catch
        {
            return [];
        }
    }

    // Membership is "same" only when each entry matches on identity (RealRoot), how it earned its place
    // (Provenance — a promotion changes IsWritable) AND its custom label — so a relabel or a hide/promote
    // swaps the set and fires Changed, repainting the overlay. Order matters (primary first, stable).
    private static bool SameMembership(
        IReadOnlyList<ClaudeConfigDir> a, IReadOnlyList<ClaudeConfigDir> b)
    {
        if (a.Count != b.Count) return false;
        for (int i = 0; i < a.Count; i++)
        {
            if (!ClaudeConfigDir.PathComparer.Equals(a[i].RealRoot, b[i].RealRoot)) return false;
            if (a[i].Provenance != b[i].Provenance) return false;
            if (!string.Equals(a[i].CustomLabel, b[i].CustomLabel, StringComparison.Ordinal)) return false;
        }
        return true;
    }

    /// <summary>
    /// Resolves a directory's real path (following symlinks/junctions to the final target), so link
    /// aliases can be deduped. Best-effort: on any failure — including a path that does not exist or is
    /// not a link — returns the input unchanged.
    /// </summary>
    public static string ResolveReal(string path) => _resolveReal(path);

    private static string DefaultResolveReal(string path)
    {
        try
        {
            var target = Directory.ResolveLinkTarget(path, returnFinalTarget: true);
            return target?.FullName ?? path;
        }
        catch
        {
            return path;
        }
    }

    private static ClaudeConfigSet BuildInitial()
    {
        var root = ResolvePrimaryRoot();
        var primary = new ClaudeConfigDir(root, ResolveReal(root));

        // Pinned (CLAUDE_CONFIG_DIR set): hermetic — the primary is the whole set, no disk fan-out.
        if (IsPinned)
            return new ClaudeConfigSet(primary, new[] { primary });

        // Unpinned: discover from the persisted self-reported roots + the convention scan up front
        // (declared roots arrive later via ConfigureDeclaredRoots, which forces a refresh). Best-effort;
        // discovery never throws.
        EnsureSelfReportedLoaded();
        var all = ClaudeConfigDiscovery.Discover(Home, primary, declaredRoots: null, _selfReported.ToList());
        return new ClaudeConfigSet(primary, all);
    }

    private static string ResolvePrimaryRoot()
    {
        var configDir = Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR");
        return string.IsNullOrWhiteSpace(configDir) ? Path.Combine(Home, ".claude") : configDir;
    }

    private static string? SafeFullPath(string path)
    {
        try { return Path.GetFullPath(path); }
        catch { return null; }
    }

    // ── Self-reported sticky persistence ─────────────────────────────────────────────
    // A tiny JSON file beside AppSettings ({profile}/config-dirs.json), holding the roots sessions have
    // self-reported so they survive a restart. Never touched while pinned (tests/replay) — NoteSelfReport
    // early-returns there, and loading is skipped — so it can't leak into the test profile.

    // True when self-report/persistence must stay inert — a pinned CLAUDE_CONFIG_DIR (tests, replay),
    // unless a test explicitly opts in via AllowSelfReportWhenPinnedForTesting.
    private static bool SelfReportSuppressed => IsPinned && !AllowSelfReportWhenPinnedForTesting;

    private static string PersistencePath => PersistencePathForTesting ?? Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        AppProfile.DataFolderName, "config-dirs.json");

    private static void EnsureSelfReportedLoaded()
    {
        if (_selfReportedLoaded || SelfReportSuppressed) return;
        _selfReportedLoaded = true;
        try
        {
            if (!File.Exists(PersistencePath)) return;
            var node = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(PersistencePath));
            if (node?["selfReported"] is System.Text.Json.Nodes.JsonArray arr)
                foreach (var item in arr)
                    if (item?.GetValue<string>() is { } root && SafeFullPath(root) is { } full)
                        _selfReported.Add(full);
        }
        catch
        {
            // Best-effort: a missing/corrupt file just means no sticky roots this launch.
        }
    }

    private static void SaveSelfReported()
    {
        if (SelfReportSuppressed) return;
        try
        {
            var arr = new System.Text.Json.Nodes.JsonArray();
            List<string> snapshot;
            lock (RefreshGate) snapshot = _selfReported.ToList();
            foreach (var root in snapshot) arr.Add(root);
            var obj = new System.Text.Json.Nodes.JsonObject { ["selfReported"] = arr };

            var path = PersistencePath;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var tmp = path + ".tmp";
            File.WriteAllText(tmp, obj.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
            File.Move(tmp, path, overwrite: true);
        }
        catch
        {
            // Best-effort: a failed write just means the dir is re-learned next time it self-reports.
        }
    }

    // --- Test seam (mirrors Clock.SetProvider / Reset) ------------------------------------------------
    // The whole suite runs under a pinned CLAUDE_CONFIG_DIR, so multi-dir behaviour is unreachable
    // through discovery (which is hermetically disabled); these let a test drive the set directly. The
    // pure discovery logic itself is tested through ClaudeConfigDiscovery.Discover with explicit inputs.

    /// <summary>Replaces the live set with an explicit membership (primary = the first entry) and fires
    /// <see cref="Changed"/>. Not for production use.</summary>
    internal static void SetForTesting(IReadOnlyList<ClaudeConfigDir> dirs)
    {
        if (dirs.Count == 0) throw new ArgumentException("at least one dir required", nameof(dirs));
        var swapped = new ClaudeConfigSet(dirs[0], dirs);
        lock (RefreshGate) Instance = swapped;
        Changed?.Invoke();
    }

    /// <summary>Restores the real primary-only set the pinned test env produces (and the real link
    /// resolver).</summary>
    internal static void ResetForTesting()
    {
        lock (RefreshGate)
        {
            _resolveReal = DefaultResolveReal;
            var root = ResolvePrimaryRoot();
            var primary = new ClaudeConfigDir(root, ResolveReal(root));
            Instance = new ClaudeConfigSet(primary, new[] { primary });
            _declaredRootsProvider = null;
            _labelsProvider = null;
            _suppressedProvider = null;
            _lastRefreshTick = long.MinValue;
            _selfReported.Clear();
            _selfReportedLoaded = false;
            PersistencePathForTesting = null;
            AllowSelfReportWhenPinnedForTesting = false;
        }
    }

    /// <summary>Installs a fake link resolver so a test can make two distinct paths resolve to one "real"
    /// path — exercising junction dedup and shared-<c>sessions/</c> detection without minting a real
    /// junction. Null restores the real resolver. Cleared by <see cref="ResetForTesting"/>.</summary>
    internal static void SetLinkResolverForTesting(Func<string, string>? resolver) =>
        _resolveReal = resolver ?? DefaultResolveReal;

    // Self-report persistence seams: the machinery is inert under a pinned CLAUDE_CONFIG_DIR (the suite),
    // so a test opts in and redirects the file at a temp path to exercise the round-trip hermetically.
    internal static string? PersistencePathForTesting { get; set; }
    internal static bool AllowSelfReportWhenPinnedForTesting { get; set; }

    /// <summary>The self-reported roots currently held (test-only).</summary>
    internal static IReadOnlyList<string> SelfReportedForTesting()
    {
        lock (RefreshGate) return _selfReported.ToList();
    }

    /// <summary>Drops the in-memory self-reported set and reloads it from the persistence file (test-only),
    /// so a round-trip can be asserted.</summary>
    internal static void ReloadSelfReportedForTesting()
    {
        lock (RefreshGate)
        {
            _selfReported.Clear();
            _selfReportedLoaded = false;
            EnsureSelfReportedLoaded();
        }
    }
}
