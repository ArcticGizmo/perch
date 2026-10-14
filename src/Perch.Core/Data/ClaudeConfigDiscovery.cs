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
/// The claude-envs scheme as an <see cref="IConfigDirSource"/>: one config dir per env under
/// <c>~/.claude-envs/envs/&lt;slug&gt;</c> (see <c>.claude/artefacts/claude-config-dir-multi-org.md</c>).
/// Every child of <c>envs/</c> is a candidate, manifest or not — the scan only suggests, and each candidate
/// still has to pass <see cref="ClaudeConfigDiscovery.LooksLikeConfigDir"/>. Each env's folder name becomes
/// its label slug. No file is parsed, so nothing here carries org identity.
/// </summary>
internal sealed class ClaudeEnvsSource : IConfigDirSource
{
    private readonly string _schemeRoot;
    private readonly bool _requireManifest;

    /// <param name="requireManifest">Only look in <c>envs/</c> when <c>~/.claude-envs</c> has a
    /// <c>manifest.json</c>/<c>envs.json</c> — how the old auto-discovery behaved, so
    /// <see cref="ConfigDirMigration"/> carries over exactly what it showed.</param>
    public ClaudeEnvsSource(string home, bool requireManifest = false)
    {
        _schemeRoot = Path.Combine(home, ".claude-envs");
        _requireManifest = requireManifest;
    }

    public IEnumerable<(string Root, string? Slug)> Enumerate()
    {
        List<(string, string?)> result = new();
        try
        {
            if (_requireManifest &&
                !File.Exists(Path.Combine(_schemeRoot, "manifest.json")) &&
                !File.Exists(Path.Combine(_schemeRoot, "envs.json")))
                return result;

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

/// <summary>Where a <see cref="ConfigDirSuggestion"/> came from — shown on its row in the scan results.</summary>
internal enum ConfigDirSuggestionSource
{
    /// <summary>A running session's hook reported this dir (a <c>.configdir</c> marker was seen).</summary>
    ReportedBySession,
    /// <summary>A <c>~/.claude*</c> folder next to the default one.</summary>
    HomeFolder,
    /// <summary>A launcher scheme's env folder (<c>~/.claude-envs/envs/*</c>).</summary>
    Scheme,
}

/// <summary>A config dir the scan found that isn't in the set yet — a candidate the user can add, never
/// something Perch adds on its own.</summary>
internal sealed record ConfigDirSuggestion(string Root, string RealRoot, string Label, ConfigDirSuggestionSource Source);

/// <summary>
/// Config-dir membership is <b>explicit</b>: <see cref="Discover"/> builds the set from the primary plus the
/// roots the user added, deduped by resolved real path so link aliases collapse to one entry (the earlier
/// entry wins). Nothing joins the set by pattern-matching — <see cref="Suggest"/> runs the convention scan
/// (home-sibling <c>~/.claude*</c> dirs, <c>~/.claude-envs/envs/*</c>, session self-reports) only to <i>recommend</i>
/// candidates, each gated by <see cref="LooksLikeConfigDir"/>. Pure and best-effort — never throws.
/// </summary>
internal static class ClaudeConfigDiscovery
{
    /// <summary>
    /// Builds the set: the primary, then each declared root. <paramref name="linkResolver"/> maps a path to its
    /// link-resolved real path (defaults to <see cref="ClaudeConfigSet.ResolveReal"/>); tests inject it to
    /// prove junction dedup without minting a real junction.
    /// </summary>
    public static IReadOnlyList<ClaudeConfigDir> Discover(
        ClaudeConfigDir primary,
        IReadOnlyList<string>? declaredRoots,
        Func<string, string>? linkResolver = null,
        IReadOnlyDictionary<string, string>? labels = null)
    {
        linkResolver ??= ClaudeConfigSet.ResolveReal;

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
            var labelled = WithLabel(dir);
            if (seen.Add(labelled.RealRoot)) result.Add(labelled);
        }

        // 1. Primary — always first, always wins its real path.
        Add(primary);

        // 2. Declared roots — everything else the user added.
        foreach (var root in declaredRoots ?? [])
        {
            if (string.IsNullOrWhiteSpace(root)) continue;
            var full = SafeFullPath(root);
            if (full is null) continue;
            Add(new ClaudeConfigDir(full, linkResolver(full)) { Provenance = ConfigDirProvenance.Declared });
        }

        return result;
    }

    /// <summary>
    /// The scan behind "Scan for directories": candidates that look like config dirs but aren't in
    /// <paramref name="existing"/> yet, from (in order) <paramref name="reportedRoots"/> (dirs sessions have
    /// self-reported), home-sibling <c>~/.claude*</c> folders, then <paramref name="sources"/> (defaults to the
    /// built-in claude-envs source rooted at <paramref name="home"/>). Deduped by real path, comparing the way
    /// <see cref="ClaudeConfigDir.SameRoot"/> does. Recommends only; never changes the set.
    /// </summary>
    public static IReadOnlyList<ConfigDirSuggestion> Suggest(
        string home,
        IReadOnlyList<ClaudeConfigDir> existing,
        IReadOnlyList<string>? reportedRoots = null,
        Func<string, string>? linkResolver = null,
        IReadOnlyList<IConfigDirSource>? sources = null)
    {
        linkResolver ??= ClaudeConfigSet.ResolveReal;
        sources ??= new IConfigDirSource[] { new ClaudeEnvsSource(home) };

        var result = new List<ConfigDirSuggestion>();
        var seen = new HashSet<string>(
            existing.Select(d => ClaudeConfigDir.RootKey(d.RealRoot)), ClaudeConfigDir.PathComparer);

        void Consider(string? root, string? slug, ConfigDirSuggestionSource source)
        {
            if (string.IsNullOrWhiteSpace(root) || SafeFullPath(root) is not { } full) return;
            if (!LooksLikeConfigDir(full)) return;
            var dir = new ClaudeConfigDir(full, linkResolver(full), slug);
            if (seen.Add(ClaudeConfigDir.RootKey(dir.RealRoot)))
                result.Add(new ConfigDirSuggestion(dir.Root, dir.RealRoot, dir.Label, source));
        }

        foreach (var root in reportedRoots ?? [])
            Consider(root, null, ConfigDirSuggestionSource.ReportedBySession);
        foreach (var candidate in ScanHomeSiblings(home))
            Consider(candidate, null, ConfigDirSuggestionSource.HomeFolder);
        foreach (var source in sources)
            foreach (var (root, slug) in SafeEnumerate(source))
                Consider(root, slug, ConfigDirSuggestionSource.Scheme);

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
