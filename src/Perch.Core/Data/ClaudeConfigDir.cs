namespace Perch.Data;

/// <summary>
/// An <b>org-free</b> Claude Code config directory: an identity-less container of sessions,
/// transcripts and settings, identified purely by its location on disk. This is the value model at
/// the heart of config-dir discovery (Layer 1) — it deliberately carries <b>no</b> notion of org,
/// account, login or credential. Attribution is always to a <i>directory</i>, never to an org.
/// </summary>
/// <remarks>
/// Equality and hashing are by <see cref="RealRoot"/> only (links resolved), so two directories that
/// junction onto one physical tree are the same config dir. Case sensitivity follows the OS
/// (<see cref="StringComparison.Ordinal"/> on Linux, <see cref="StringComparison.OrdinalIgnoreCase"/>
/// elsewhere). The derived paths mirror <see cref="ClaudePaths"/>'s layout so every reader can be
/// pointed at a specific dir rather than the process-wide primary.
/// </remarks>
internal sealed record ClaudeConfigDir
{
    /// <summary>OS-appropriate comparer for config-dir paths: case-insensitive except on Linux.</summary>
    public static readonly StringComparer PathComparer =
        OperatingSystem.IsLinux() ? StringComparer.Ordinal : StringComparer.OrdinalIgnoreCase;

    /// <summary>The directory as declared/discovered (may be a symlink or junction).</summary>
    public string Root { get; }

    /// <summary>The link-resolved real path, normalised (trailing separator trimmed). The sole
    /// identity of the config dir — equality and hashing key off this so link aliases dedup.</summary>
    public string RealRoot { get; }

    /// <summary>An optional scheme slug (e.g. a launcher's per-env folder name). Purely a label hint;
    /// never carries org meaning.</summary>
    public string? Slug { get; }

    /// <summary>Human-facing label: the <see cref="Slug"/> when present, else the directory name of
    /// <see cref="Root"/>.</summary>
    public string Label { get; }

    /// <summary>An optional user-assigned display label that overrides <see cref="Label"/> on the overlay's
    /// per-session directory chip. Null/blank means "use the default". Threaded in during discovery from
    /// <see cref="AppSettings.ConfigDirLabels"/> (keyed by <see cref="RealRoot"/>); it is a display concern
    /// only and — like everything here — carries no org meaning.</summary>
    public string? CustomLabel { get; init; }

    /// <summary>The label to show on the overlay's per-session directory chip: the user's
    /// <see cref="CustomLabel"/> when set; otherwise <b>nothing</b> for the
    /// <see cref="ConfigDirProvenance.Primary"/> dir (the default <c>~/.claude</c> is unremarkable and would
    /// only clutter every row) and the derived <see cref="Label"/> for any other dir. The chip is drawn only
    /// when this is non-empty.</summary>
    public string DisplayLabel =>
        !string.IsNullOrWhiteSpace(CustomLabel) ? CustomLabel!.Trim()
        : Provenance == ConfigDirProvenance.Primary ? ""
        : Label;

    /// <summary>How this dir earned its place in the set. Gates the safe-write policy (M4): only
    /// declared and self-reported dirs are ever written to.</summary>
    public ConfigDirProvenance Provenance { get; init; } = ConfigDirProvenance.Primary;

    /// <summary>Whether the safe-write policy permits writing into this dir (a hook install). True for
    /// <see cref="ConfigDirProvenance.Primary"/>/<see cref="ConfigDirProvenance.Declared"/>/
    /// <see cref="ConfigDirProvenance.SelfReported"/>; false for a <see cref="ConfigDirProvenance.Convention"/>-only
    /// dir, which Perch lists and reads but never writes into until it is promoted by a declaration or a
    /// self-report.</summary>
    public bool IsWritable => Provenance is not ConfigDirProvenance.Convention;

    public ClaudeConfigDir(string root, string? realRoot = null, string? slug = null)
    {
        Root = root;
        RealRoot = Normalize(realRoot ?? root);
        Slug = string.IsNullOrWhiteSpace(slug) ? null : slug;
        Label = Slug ?? DirName(Root);
    }

    /// <summary><c>{root}/sessions</c> — live session sidecars.</summary>
    public string SessionsDir => Path.Combine(Root, "sessions");

    /// <summary><c>{root}/projects</c> — per-project transcript directories.</summary>
    public string ProjectsDir => Path.Combine(Root, "projects");

    /// <summary><c>{root}/plugins</c> — installed-plugin state and marketplace clones.</summary>
    public string PluginsDir => Path.Combine(Root, "plugins");

    /// <summary><c>{root}/daemon</c> — the background daemon's state directory.</summary>
    public string DaemonDir => Path.Combine(Root, "daemon");

    /// <summary><c>{root}/daemon/roster.json</c> — the daemon supervisor's worker registry.</summary>
    public string DaemonRosterFile => Path.Combine(DaemonDir, "roster.json");

    /// <summary><c>{root}/.credentials.json</c> — the OAuth tokens the usage poll reads.</summary>
    public string CredentialsFile => Path.Combine(Root, ".credentials.json");

    /// <summary><c>{root}/settings.json</c> — the user-scope Claude Code settings.</summary>
    public string UserSettingsFile => Path.Combine(Root, "settings.json");

    /// <summary><c>{root}/image-cache</c> — resumed-transcript image cache root (per-session below).</summary>
    public string ImageCacheDir => Path.Combine(Root, "image-cache");

    public bool Equals(ClaudeConfigDir? other) =>
        other is not null && PathComparer.Equals(RealRoot, other.RealRoot);

    public override int GetHashCode() => PathComparer.GetHashCode(RealRoot);

    public override string ToString() => $"ClaudeConfigDir({Label}: {Root})";

    /// <summary>Trims a trailing directory separator (so <c>C:\x\</c> and <c>C:\x</c> unify) while
    /// leaving a bare root like <c>C:\</c> intact.</summary>
    private static string Normalize(string path) =>
        string.IsNullOrEmpty(path) ? path : Path.TrimEndingDirectorySeparator(path);

    /// <summary>The final path segment, falling back to the whole (trimmed) path for a bare root.</summary>
    private static string DirName(string path)
    {
        var trimmed = Normalize(path);
        var name = Path.GetFileName(trimmed);
        return string.IsNullOrEmpty(name) ? trimmed : name;
    }
}

/// <summary>
/// How a <see cref="ClaudeConfigDir"/> entered the set. Ordered by trust: the safe-write policy (M4)
/// permits hook installs into <see cref="Primary"/>, <see cref="Declared"/> and
/// <see cref="SelfReported"/> dirs, but never into a dir found <see cref="Convention"/> only.
/// </summary>
internal enum ConfigDirProvenance
{
    /// <summary>The process-wide primary (<c>CLAUDE_CONFIG_DIR</c> or <c>~/.claude</c>).</summary>
    Primary,
    /// <summary>Explicitly listed by the user in settings.</summary>
    Declared,
    /// <summary>A running session's hook reported this dir (a <c>.configdir</c> marker was seen).</summary>
    SelfReported,
    /// <summary>Matched only by the convention scan (a sibling <c>~/.claude*</c> or a scheme manifest).
    /// Read from, but never written to.</summary>
    Convention,
}
