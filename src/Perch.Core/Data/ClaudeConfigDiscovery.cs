namespace Perch.Data;

/// <summary>
/// A pluggable source of config-dir <i>candidates</i> declared by some external scheme (the first
/// being claude-envs). A source only ever <b>proposes</b> roots and their label slugs; it never
/// decides membership (every candidate is still gated by <see cref="ClaudeConfigDiscovery.LooksLikeConfigDir"/>)
/// and it never carries org identity — that keeps Layer 1 org-free and keeps any one scheme from being
/// hard-coded into discovery. See docs/config-dir-plan.md.
/// </summary>
internal interface IConfigDirSource
{
    /// <summary>Candidate <c>(root, slug)</c> pairs. Best-effort; must never throw.</summary>
    IEnumerable<(string Root, string? Slug)> Enumerate();
}

/// <summary>
/// The claude-envs scheme as an <see cref="IConfigDirSource"/>: a scheme root at
/// <c>~/.claude-envs</c> with a <c>manifest.json</c>/<c>envs.json</c> and one config dir per env under
/// <c>envs/&lt;slug&gt;</c> (see <c>.claude/artefacts/claude-config-dir-multi-org.md</c>). The
/// manifest's presence is what authorises descending into <c>envs/</c> (the home-sibling scan
/// deliberately does not); each env's folder name becomes its label slug. The manifest is <b>not</b>
/// parsed for paths or org — only its existence gates the standard <c>envs/*</c> layout — so an
/// unknown manifest schema and a hostile file are both harmless.
/// </summary>
internal sealed class ClaudeEnvsManifestSource : IConfigDirSource
{
    private readonly string _schemeRoot;

    public ClaudeEnvsManifestSource(string home)
        => _schemeRoot = Path.Combine(home, ".claude-envs");

    public IEnumerable<(string Root, string? Slug)> Enumerate()
    {
        List<(string, string?)> result = new();
        try
        {
            var hasManifest =
                File.Exists(Path.Combine(_schemeRoot, "manifest.json")) ||
                File.Exists(Path.Combine(_schemeRoot, "envs.json"));
            if (!hasManifest) return result;

            var envsDir = Path.Combine(_schemeRoot, "envs");
            if (!Directory.Exists(envsDir)) return result;

            foreach (var dir in Directory.EnumerateDirectories(envsDir))
            {
                var slug = Path.GetFileName(Path.TrimEndingDirectorySeparator(dir));
                result.Add((dir, string.IsNullOrEmpty(slug) ? null : slug));
            }
        }
        catch
        {
            // Best-effort: a missing scheme, an unreadable envs dir — contribute nothing.
        }
        return result;
    }
}

/// <summary>
/// Builds the ordered, deduped set of Claude Code config directories from, in priority order:
/// <b>primary → declared roots → convention scan</b>. Declaration is the backbone; the convention
/// scan (home-sibling <c>~/.claude*</c> dirs and manifest sources) only ever <i>adds</i> candidates,
/// each gated by <see cref="LooksLikeConfigDir"/>. Dedup is by resolved real path so link aliases
/// collapse to one entry, with the higher-priority (earlier) entry winning. Pure and best-effort —
/// never throws.
/// </summary>
internal static class ClaudeConfigDiscovery
{
    /// <summary>
    /// Discovers the set. <paramref name="linkResolver"/> maps a path to its link-resolved real path
    /// (defaults to <see cref="ClaudeConfigSet.ResolveReal"/>); tests inject it to prove junction dedup
    /// without minting a real junction. <paramref name="sources"/> defaults to the built-in claude-envs
    /// source rooted at <paramref name="home"/>.
    /// </summary>
    public static IReadOnlyList<ClaudeConfigDir> Discover(
        string home,
        ClaudeConfigDir primary,
        IReadOnlyList<string>? declaredRoots,
        IReadOnlyList<string>? selfReportedRoots = null,
        Func<string, string>? linkResolver = null,
        IReadOnlyList<IConfigDirSource>? sources = null,
        IReadOnlyDictionary<string, string>? labels = null,
        IReadOnlyCollection<string>? suppressedRealRoots = null)
    {
        linkResolver ??= ClaudeConfigSet.ResolveReal;
        sources ??= new IConfigDirSource[] { new ClaudeEnvsManifestSource(home) };
        var suppressed = new HashSet<string>(suppressedRealRoots ?? [], ClaudeConfigDir.PathComparer);

        var result = new List<ClaudeConfigDir>();
        var seen = new HashSet<string>(ClaudeConfigDir.PathComparer);

        // Apply the user's custom label (keyed by real path) to a dir, clearing a stale one when the map no
        // longer names it — so a relabel and a label-removal both take effect. No-op when no map was given.
        ClaudeConfigDir WithLabel(ClaudeConfigDir dir)
        {
            if (labels is null) return dir;
            var custom = labels.TryGetValue(dir.RealRoot, out var c) && !string.IsNullOrWhiteSpace(c) ? c : null;
            return string.Equals(custom, dir.CustomLabel, StringComparison.Ordinal) ? dir : dir with { CustomLabel = custom };
        }

        void Add(ClaudeConfigDir dir)
        {
            // The primary is never hidden, whatever the hidden list says; other tiers are excluded when the
            // user has removed them, so a convention/self-reported dir stays gone across refreshes.
            if (dir.Provenance != ConfigDirProvenance.Primary && suppressed.Contains(dir.RealRoot)) return;
            var labelled = WithLabel(dir);
            if (seen.Add(labelled.RealRoot)) result.Add(labelled);
        }

        void AddRoots(IReadOnlyList<string>? roots, ConfigDirProvenance provenance)
        {
            foreach (var root in roots ?? [])
            {
                if (string.IsNullOrWhiteSpace(root)) continue;
                var full = SafeFullPath(root);
                if (full is null) continue;
                Add(new ClaudeConfigDir(full, linkResolver(full)) { Provenance = provenance });
            }
        }

        // 1. Primary — always first, always wins its real path.
        Add(primary);

        // 2. Declared roots — the authoritative backbone.
        AddRoots(declaredRoots, ConfigDirProvenance.Declared);

        // 3. Self-reported roots — dirs a running session's hook has stamped (sticky, persisted). These
        //    are writable (safe-write policy) and rank above the convention scan so a self-report promotes
        //    a would-be Convention dir.
        AddRoots(selfReportedRoots, ConfigDirProvenance.SelfReported);

        // 4. Convention scan — the convenience tier, marker-gated. Home-sibling ~/.claude* dirs first,
        //    then scheme-manifest sources. Both only ADD; neither can override a declared/primary entry.
        foreach (var candidate in ScanHomeSiblings(home))
            if (LooksLikeConfigDir(candidate))
                Add(new ClaudeConfigDir(candidate, linkResolver(candidate))
                {
                    Provenance = ConfigDirProvenance.Convention,
                });

        foreach (var source in sources)
            foreach (var (root, slug) in SafeEnumerate(source))
                if (!string.IsNullOrWhiteSpace(root) && LooksLikeConfigDir(root))
                    Add(new ClaudeConfigDir(root, linkResolver(root), slug)
                    {
                        Provenance = ConfigDirProvenance.Convention,
                    });

        return result;
    }

    /// <summary>
    /// A marker gate: does <paramref name="dir"/> look like a Claude Code config directory? True when
    /// any one of: a <c>sessions/</c> subdir exists, a <c>.claude.json</c> file exists, or both
    /// <c>settings.json</c> and <c>projects/</c> exist. Deliberately loose (any one marker) but never
    /// empty — an unrelated directory that happens to be named <c>.claude-notes</c> won't match. Never
    /// throws.
    /// </summary>
    public static bool LooksLikeConfigDir(string dir)
    {
        try
        {
            if (Directory.Exists(Path.Combine(dir, "sessions"))) return true;
            if (File.Exists(Path.Combine(dir, ".claude.json"))) return true;
            if (File.Exists(Path.Combine(dir, "settings.json")) &&
                Directory.Exists(Path.Combine(dir, "projects"))) return true;
        }
        catch
        {
            // ignore
        }
        return false;
    }

    /// <summary>
    /// The home-directory convention scan: <c>~/.claude*</c> directories that are <b>direct siblings</b>
    /// (children of home). Deliberately does not descend into their children — that is what matched a
    /// scheme's shared store during earlier work — so a scheme's per-env dirs come only through an
    /// <see cref="IConfigDirSource"/>. Never throws.
    /// </summary>
    private static IEnumerable<string> ScanHomeSiblings(string home)
    {
        try
        {
            return Directory.EnumerateDirectories(home, ".claude*").ToList();
        }
        catch
        {
            return [];
        }
    }

    private static IEnumerable<(string Root, string? Slug)> SafeEnumerate(IConfigDirSource source)
    {
        try
        {
            return source.Enumerate().ToList();
        }
        catch
        {
            return [];
        }
    }

    private static string? SafeFullPath(string path)
    {
        try
        {
            return Path.GetFullPath(path);
        }
        catch
        {
            return null;
        }
    }
}
