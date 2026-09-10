namespace Perch.Data;

using System.Text.Json.Nodes;

/// <summary>
/// The set of Claude Code config directories on this machine, and the one Perch treats as primary.
///
/// <para>An explicit <c>CLAUDE_CONFIG_DIR</c> pins the set to exactly that directory, which is what
/// keeps the replay sandbox and the test fixture tree hermetic. Discovery only fans out when it is
/// unset — the case Perch is actually launched in, since Claude Code does not export the variable to
/// child processes.</para>
/// </summary>
internal static class ClaudeConfigSet
{
    // Scan() calls RefreshIfStale on every pass, so the probe is throttled. It must re-run at all
    // because an env's sessions/ directory appears the first time it is signed into.
    private static readonly TimeSpan RefreshFloor = TimeSpan.FromSeconds(5);

    private static readonly object Gate = new();
    private static IReadOnlyList<ClaudeConfigDir> _all;
    private static DateTime _lastProbeUtc;
    private static bool _pinned;

    /// <summary>The dir every non-session-specific path resolves against. Snapshotted on first
    /// access, as <see cref="ClaudePaths"/> has always been.</summary>
    public static ClaudeConfigDir Primary { get; }

    /// <summary>The default <c>~/.claude</c>, whether or not it is <see cref="Primary"/>.</summary>
    public static ClaudeConfigDir Hub { get; }

    static ClaudeConfigSet()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var hubRoot = Path.Combine(home, ".claude");
        var configured = Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR");
        _pinned = !string.IsNullOrWhiteSpace(configured);

        Hub = Describe(hubRoot, isHub: true);
        Primary = _pinned
            ? Describe(configured!, isHub: ClaudeConfigDir.PathComparer.Equals(
                  ClaudeConfigDiscovery.Normalize(configured!), hubRoot))
            : Hub;

        _all = [Primary];
        _lastProbeUtc = DateTime.MinValue;
    }

    /// <summary>Every known config dir, primary first, de-duplicated by resolved path. Never empty.</summary>
    public static IReadOnlyList<ClaudeConfigDir> All
    {
        get
        {
            RefreshIfStale();
            return _all;
        }
    }

    /// <summary>True when there is more than one config dir.</summary>
    public static bool IsMulti => All.Count > 1;

    /// <summary>Raised off the UI thread when a probe changed the set.</summary>
    public static event Action? Changed;

    /// <summary>The environment with this slug, else null. Kept for the <c>.slug</c> marker an earlier
    /// hook wrote: a session running when Perch updates has one of those and nothing newer.</summary>
    public static ClaudeConfigDir? ForSlug(string? slug)
    {
        if (string.IsNullOrWhiteSpace(slug)) return null;
        foreach (var dir in All)
            if (dir.Slug is { Length: > 0 } s && string.Equals(s, slug, StringComparison.OrdinalIgnoreCase))
                return dir;
        return null;
    }

    /// <summary>
    /// The config dir at this root, else null — deliberately no fallback to the primary, which would
    /// attribute a session to whichever dir happens to be first. Matched on the resolved path as well
    /// as the literal one, so a root reported through a link still finds its dir. The value is only
    /// ever compared, never opened: it arrives from a file written by a hook.
    /// </summary>
    public static ClaudeConfigDir? ForRoot(string? root)
    {
        if (string.IsNullOrWhiteSpace(root)) return null;

        var normalized = ClaudeConfigDiscovery.Normalize(root);
        var real = ResolveReal(normalized);
        foreach (var dir in All)
            if (ClaudeConfigDir.PathComparer.Equals(dir.Root, normalized)
                || ClaudeConfigDir.PathComparer.Equals(dir.RealRoot, real))
                return dir;
        return null;
    }

    /// <summary>The distinct <c>projects</c> trees, by resolved real path. Normally one entry: config
    /// dirs tend to link onto the same physical tree, and enumerating each would surface every
    /// transcript once per config dir.</summary>
    public static IReadOnlyList<string> DistinctProjectsDirs()
    {
        var seen = new HashSet<string>(ClaudeConfigDir.PathComparer);
        var result = new List<string>();
        foreach (var dir in All)
            if (seen.Add(ResolveReal(dir.ProjectsDir)))
                result.Add(dir.ProjectsDir);
        return result;
    }

    /// <summary>
    /// The config dirs whose <c>sessions</c> directories are physically distinct, keeping the first
    /// owner of each. A scheme may share <c>sessions/</c> by link the way it shares <c>projects/</c>,
    /// and enumerating each config dir's own path would then list every session once per config dir.
    /// Where it <em>is</em> shared the surviving owner is the primary, which is the honest answer:
    /// a session file in a directory belonging to every environment belongs to none in particular.
    /// </summary>
    public static IReadOnlyList<ClaudeConfigDir> DistinctSessionsDirs()
    {
        var seen = new HashSet<string>(ClaudeConfigDir.PathComparer);
        var result = new List<ClaudeConfigDir>();
        foreach (var dir in All)
            if (seen.Add(ResolveReal(dir.SessionsDir)))
                result.Add(dir);
        return result;
    }

    // Cached against the identity of the set it was derived from: the answer only changes when the
    // set does, and the question is asked once per session per scan - resolving links each time would
    // put a syscall on that path.
    private static IReadOnlyList<ClaudeConfigDir>? _sharingDerivedFrom;
    private static HashSet<string> _sharedSessionRoots = [];

    /// <summary>
    /// True when another config dir resolves to the same <c>sessions</c> directory, so which dir a
    /// sidecar was found in attributes it to nothing. False when the directory belongs to this
    /// environment alone, in which case the path <em>is</em> the attribution.
    /// </summary>
    public static bool SharesSessionsDir(ClaudeConfigDir dir)
    {
        var all = All;
        if (!ReferenceEquals(_sharingDerivedFrom, all))
        {
            var seen = new HashSet<string>(ClaudeConfigDir.PathComparer);
            var shared = new HashSet<string>(ClaudeConfigDir.PathComparer);
            foreach (var candidate in all)
            {
                var real = ResolveReal(candidate.SessionsDir);
                if (!seen.Add(real)) shared.Add(real);
            }
            _sharedSessionRoots = shared;
            _sharingDerivedFrom = all;
        }

        return _sharedSessionRoots.Contains(ResolveReal(dir.SessionsDir));
    }

    /// <summary>Re-probes if the set hasn't been derived recently.</summary>
    public static void RefreshIfStale()
    {
        if (_pinned) return;
        if (DateTime.UtcNow - _lastProbeUtc < RefreshFloor) return;
        Refresh();
    }

    /// <summary>Re-probes now. Returns true when the set changed.</summary>
    public static bool Refresh()
    {
        lock (Gate)
        {
            _lastProbeUtc = DateTime.UtcNow;
            if (_pinned) return false;
            var discovered = ClaudeConfigDiscovery.Discover(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), Primary);
            if (SameSet(_all, discovered)) return false;
            _all = discovered;
        }
        Changed?.Invoke();
        return true;
    }

    /// <summary>Replaces the set for a test (the <see cref="Clock"/> precedent); null restores
    /// discovery.</summary>
    internal static void SetForTesting(IReadOnlyList<ClaudeConfigDir>? dirs)
    {
        lock (Gate)
        {
            if (dirs is null or { Count: 0 })
            {
                _pinned = !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR"));
                _all = [Primary];
                _lastProbeUtc = DateTime.MinValue;
            }
            else
            {
                _pinned = true;
                _all = dirs;
            }
        }
        Changed?.Invoke();
    }

    private static bool SameSet(IReadOnlyList<ClaudeConfigDir> a, IReadOnlyList<ClaudeConfigDir> b)
    {
        if (a.Count != b.Count) return false;
        for (int i = 0; i < a.Count; i++)
            if (!a[i].Equals(b[i]) || a[i].Label != b[i].Label || a[i].DeclaredOrg != b[i].DeclaredOrg)
                return false;
        return true;
    }

    private static ClaudeConfigDir Describe(string root, bool isHub)
    {
        var normalized = ClaudeConfigDiscovery.Normalize(root);
        return new ClaudeConfigDir(normalized, ResolveReal(normalized), isHub: isHub);
    }

    /// <summary><paramref name="path"/> with links resolved to their final target, or the path itself
    /// when it is an ordinary directory or can't be resolved.</summary>
    internal static string ResolveReal(string path)
    {
        try { return Directory.ResolveLinkTarget(path, returnFinalTarget: true)?.FullName ?? path; }
        catch { return path; }
    }
}

/// <summary>The probe behind <see cref="ClaudeConfigSet"/>, separated so it can run against fixture
/// trees. Best-effort throughout; the floor is "just the primary".</summary>
internal static class ClaudeConfigDiscovery
{
    private const string EnvsDirName = ".claude-envs";
    private const string EnvsSubDirName = "envs";

    // Renamed `envs.json` -> `manifest.json`; both are read. It supplies labels and declared orgs but
    // never membership, so a miss costs display detail and reports nothing - how the rename went unseen.
    private static readonly string[] ManifestFileNames = ["manifest.json", "envs.json"];

    /// <summary>
    /// The primary, the hub, every directory under the manifest's environment root, and any
    /// <c>.claude*</c> directory beside the hub — whichever pass <see cref="LooksLikeConfigDir"/>,
    /// primary first.
    ///
    /// <para>The manifest supplies labels and declared organizations only — never filesystem facts,
    /// which it was observed to get wrong in both directions.</para>
    /// </summary>
    /// <param name="linkResolver">Injected so a test can exercise de-duplication without minting real
    /// junctions, which need elevation on Windows.</param>
    public static IReadOnlyList<ClaudeConfigDir> Discover(
        string home, ClaudeConfigDir primary, Func<string, string>? linkResolver = null)
    {
        linkResolver ??= ClaudeConfigSet.ResolveReal;
        home = Normalize(home);

        var byReal = new Dictionary<string, ClaudeConfigDir>(ClaudeConfigDir.PathComparer);
        var ordered = new List<ClaudeConfigDir>();

        Add(primary);

        EnvManifest? manifest = null;
        foreach (var name in ManifestFileNames)
        {
            manifest = ReadManifest(Path.Combine(home, EnvsDirName, name));
            if (manifest is not null) break;
        }

        // Always in the set: Perch may have been started from an environment shell.
        var hubRoot = manifest?.Hub is { Length: > 0 } declaredHub
            ? Expand(declaredHub, home)
            : Path.Combine(home, ".claude");
        if (Directory.Exists(hubRoot))
            Add(Make(hubRoot, isHub: true));

        foreach (var envRoot in EnvRoots(manifest, home))
            foreach (var child in SafeDirectories(envRoot))
            {
                if (!LooksLikeConfigDir(child)) continue;
                var slug = Path.GetFileName(child);
                var declared = manifest?.Find(slug);
                Add(Make(child, slug: slug, label: declared?.Label,
                    declaredOrg: declared?.Org, declaredAccount: declared?.Account));
            }

        // A config dir beside the hub, so discovery is not hard-wired to the conventional
        // envs/<slug> layout. Siblings only, deliberately: descending into their children is what
        // found the scheme's own shared store, which holds the very `sessions/` and `projects/` the
        // marker test looks for - and a false positive is worse than a miss, because a discovered dir
        // is a hook-install target. A store is never itself a `.claude*` sibling.
        foreach (var sibling in SafeDirectories(home))
            if (Path.GetFileName(sibling).StartsWith(".claude", StringComparison.OrdinalIgnoreCase)
                && LooksLikeConfigDir(sibling))
                Add(Make(sibling));

        return ordered;

        ClaudeConfigDir Make(string root, string? slug = null, string? label = null, bool isHub = false,
                             string? declaredOrg = null, string? declaredAccount = null)
        {
            var normalized = Normalize(root);
            return new ClaudeConfigDir(normalized, Normalize(linkResolver(normalized)),
                slug, label, isHub, declaredOrg, declaredAccount);
        }

        void Add(ClaudeConfigDir dir)
        {
            if (byReal.ContainsKey(dir.RealRoot)) return;
            byReal[dir.RealRoot] = dir;
            ordered.Add(dir);
        }
    }

    /// <summary>
    /// Any one marker is enough, and they are not interchangeable: an env never signed into has
    /// neither <c>sessions/</c> nor <c>.claude.json</c> — only <c>settings.json</c> and a linked
    /// <c>projects/</c>. Requiring both of that pair keeps unrelated <c>.claude</c>-prefixed tool
    /// directories out, which matters because a false positive becomes a hook-install target.
    /// </summary>
    public static bool LooksLikeConfigDir(string dir)
    {
        try
        {
            if (!Directory.Exists(dir)) return false;
            if (Directory.Exists(Path.Combine(dir, "sessions"))) return true;
            if (File.Exists(Path.Combine(dir, ".claude.json"))) return true;
            return File.Exists(Path.Combine(dir, "settings.json"))
                && Directory.Exists(Path.Combine(dir, "projects"));
        }
        catch { return false; }
    }

    /// <summary>Absolute, host separators. Every discovered path goes through this, so one directory
    /// cannot enter the set under two spellings.</summary>
    internal static string Normalize(string path)
    {
        try { return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path.Trim())); }
        catch { return path; }
    }

    private static IEnumerable<string> EnvRoots(EnvManifest? manifest, string home)
    {
        var seen = new HashSet<string>(ClaudeConfigDir.PathComparer);
        if (manifest?.EnvRoot is { Length: > 0 } declared)
        {
            var expanded = Expand(declared, home);
            if (seen.Add(expanded)) yield return expanded;
        }
        // So a machine with a missing or unreadable manifest still finds its environments.
        var fallback = Path.Combine(home, EnvsDirName, EnvsSubDirName);
        if (seen.Add(fallback)) yield return fallback;
    }

    private static IEnumerable<string> SafeDirectories(string root)
    {
        try
        {
            if (!Directory.Exists(root)) return [];
            return Directory.EnumerateDirectories(root);
        }
        catch { return []; }
    }

    // The manifest writes POSIX-style tildes even on Windows.
    private static string Expand(string path, string home)
    {
        var trimmed = path.Trim();
        if (trimmed is "~") return home;
        if (trimmed.StartsWith("~/", StringComparison.Ordinal) || trimmed.StartsWith(@"~\", StringComparison.Ordinal))
            trimmed = Path.Combine(home, trimmed[2..].Replace('/', Path.DirectorySeparatorChar));
        return Normalize(trimmed);
    }

    private sealed record EnvManifest(string? Hub, string? EnvRoot, IReadOnlyList<EnvEntry> Environments)
    {
        public EnvEntry? Find(string slug) =>
            Environments.FirstOrDefault(e => string.Equals(e.Slug, slug, StringComparison.OrdinalIgnoreCase));
    }

    private sealed record EnvEntry(string Slug, string? Label, string? Account, string? Org);

    private static EnvManifest? ReadManifest(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(fs);
            if (JsonNode.Parse(reader.ReadToEnd()) is not JsonObject root) return null;

            var entries = new List<EnvEntry>();
            if (root["environments"] is JsonArray array)
                foreach (var node in array)
                {
                    if (node is not JsonObject o) continue;
                    var slug = TranscriptJson.AsString(o["slug"]);
                    if (string.IsNullOrWhiteSpace(slug)) continue;
                    entries.Add(new EnvEntry(slug!, TranscriptJson.AsString(o["label"]),
                        TranscriptJson.AsString(o["account"]), TranscriptJson.AsString(o["org"])));
                }

            return new EnvManifest(TranscriptJson.AsString(root["hub"]),
                TranscriptJson.AsString(root["envRoot"]), entries);
        }
        catch { return null; }   // costs labels only; the probe still finds the dirs
    }
}
