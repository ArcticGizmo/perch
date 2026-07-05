namespace Perch.Data;

/// <summary>
/// The single owner of every Claude Code config-directory location Perch reads. Centralised so
/// the rule "where does Claude Code keep X" lives in one place rather than being recomputed in each
/// reader (it previously appeared in seven). All paths derive from <see cref="ClaudeDir"/> and are
/// computed once; nothing here touches the disk.
/// </summary>
internal static class ClaudePaths
{
    /// <summary>The current user's profile directory (e.g. <c>C:\Users\me</c>).</summary>
    public static string Home { get; } =
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    /// <summary>
    /// The Claude Code config directory. Honours the <c>CLAUDE_CONFIG_DIR</c> environment variable that
    /// Claude Code itself respects (so a relocated config is followed correctly); falls back to the
    /// default <c>~/.claude</c> when it is unset or blank.
    /// </summary>
    public static string ClaudeDir { get; } = ResolveClaudeDir();

    private static string ResolveClaudeDir()
    {
        var configDir = Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR");
        return string.IsNullOrWhiteSpace(configDir) ? Path.Combine(Home, ".claude") : configDir;
    }

    /// <summary><c>~/.claude/sessions</c> — live session sidecars (<c>{pid}.json</c> and the
    /// <c>.mode</c> / <c>.notify</c> / <c>.history</c> markers that ride alongside them).</summary>
    public static string SessionsDir { get; } = Path.Combine(ClaudeDir, "sessions");

    /// <summary><c>~/.claude/projects</c> — per-project transcript directories, each holding the
    /// session <c>{sessionId}.jsonl</c> files. See <see cref="TranscriptLocator"/>.</summary>
    public static string ProjectsDir { get; } = Path.Combine(ClaudeDir, "projects");

    /// <summary><c>~/.claude/plugins</c> — installed-plugin state and marketplace clones.</summary>
    public static string PluginsDir { get; } = Path.Combine(ClaudeDir, "plugins");

    /// <summary><c>~/.claude/.credentials.json</c> — the OAuth tokens the usage poll reads.</summary>
    public static string CredentialsFile { get; } = Path.Combine(ClaudeDir, ".credentials.json");

    /// <summary><c>~/.claude/settings.json</c> — the user-scope Claude Code settings.</summary>
    public static string UserSettingsFile { get; } = Path.Combine(ClaudeDir, "settings.json");
}
