namespace Perch.Data;

/// <summary>
/// The single owner of the <b>primary</b> Claude Code config-directory location Perch reads.
/// Centralised so the rule "where does Claude Code keep X" lives in one place. Since config-dir
/// discovery (Layer 1) landed, this is a thin delegate to <see cref="ClaudeConfigSet.Primary"/> — the
/// one dir resolved from <c>CLAUDE_CONFIG_DIR</c>-or-<c>~/.claude</c> at start-up. Readers that need
/// to span <i>every</i> config dir go through <see cref="ClaudeConfigSet"/> instead; the rest keep
/// using these primary paths unchanged. Nothing here touches the disk.
/// </summary>
internal static class ClaudePaths
{
    private static ClaudeConfigDir Primary => ClaudeConfigSet.Instance.Primary;

    /// <summary>The current user's profile directory (e.g. <c>C:\Users\me</c>).</summary>
    public static string Home => ClaudeConfigSet.Home;

    /// <summary>
    /// The primary Claude Code config directory. Honours the <c>CLAUDE_CONFIG_DIR</c> environment
    /// variable that Claude Code itself respects; falls back to the default <c>~/.claude</c> when it
    /// is unset or blank.
    /// </summary>
    public static string ClaudeDir => Primary.Root;

    /// <summary><c>~/.claude/sessions</c> — live session sidecars (<c>{pid}.json</c> and the
    /// <c>.mode</c> / <c>.notify</c> / <c>.history</c> markers that ride alongside them).</summary>
    public static string SessionsDir => Primary.SessionsDir;

    /// <summary><c>~/.claude/projects</c> — per-project transcript directories, each holding the
    /// session <c>{sessionId}.jsonl</c> files. See <see cref="TranscriptLocator"/>.</summary>
    public static string ProjectsDir => Primary.ProjectsDir;

    /// <summary><c>~/.claude/plugins</c> — installed-plugin state and marketplace clones.</summary>
    public static string PluginsDir => Primary.PluginsDir;

    /// <summary><c>~/.claude/daemon</c> — the Claude Code background daemon's state directory
    /// (its worker roster plus named-pipe keys). See <see cref="DaemonRosterReader"/>.</summary>
    public static string DaemonDir => Primary.DaemonDir;

    /// <summary><c>~/.claude/daemon/roster.json</c> — the daemon supervisor's registry of the headless
    /// worker sessions it is currently hosting.</summary>
    public static string DaemonRosterFile => Primary.DaemonRosterFile;

    /// <summary><c>~/.claude/.credentials.json</c> — the OAuth tokens the usage poll reads.</summary>
    public static string CredentialsFile => Primary.CredentialsFile;

    /// <summary><c>~/.claude/settings.json</c> — the user-scope Claude Code settings.</summary>
    public static string UserSettingsFile => Primary.UserSettingsFile;
}
