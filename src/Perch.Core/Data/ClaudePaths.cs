namespace Perch.Data;

/// <summary>
/// The paths under the <em>primary</em> config directory — <c>CLAUDE_CONFIG_DIR</c>, else
/// <c>~/.claude</c>.
///
/// <para>There can be several config dirs (see <see cref="ClaudeConfigSet"/>). Anything belonging to
/// a <em>particular session</em> must use the dir that owns it —
/// <see cref="ClaudeSession.ConfigDir"/> — because writing a session's sidecar to the wrong config
/// dir is a silent no-op; those APIs take the owning directory explicitly. What stays here is what is
/// global to this Perch, and the primary is the default <c>~/.claude</c> whenever the variable is
/// unset, so it resolves where it always did.</para>
/// </summary>
internal static class ClaudePaths
{
    /// <summary>The current user's profile directory (e.g. <c>C:\Users\me</c>).</summary>
    public static string Home { get; } =
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    /// <summary>The primary Claude Code config directory. See <see cref="ClaudeConfigSet.Primary"/>.</summary>
    public static string ClaudeDir => ClaudeConfigSet.Primary.Root;

    /// <summary>The <em>primary's</em> live session sidecars. Session discovery reads every config
    /// dir's — see <see cref="ClaudeConfigSet.All"/>.</summary>
    public static string SessionsDir => ClaudeConfigSet.Primary.SessionsDir;

    /// <summary>Per-project transcript directories holding the <c>{sessionId}.jsonl</c> files. Config
    /// dirs commonly share one physical tree via links, so enumerating across the set goes through
    /// <see cref="ClaudeConfigSet.DistinctProjectsDirs"/>. See <see cref="TranscriptLocator"/>.</summary>
    public static string ProjectsDir => ClaudeConfigSet.Primary.ProjectsDir;

    /// <summary><c>~/.claude/plugins</c> — installed-plugin state and marketplace clones.</summary>
    public static string PluginsDir => ClaudeConfigSet.Primary.PluginsDir;

    /// <summary>The background daemon's state directory. Only the primary/default dir ever has one.
    /// See <see cref="DaemonRosterReader"/>.</summary>
    public static string DaemonDir => ClaudeConfigSet.Primary.DaemonDir;

    /// <summary><c>~/.claude/daemon/roster.json</c> — the daemon supervisor's registry of the headless
    /// worker sessions it is currently hosting.</summary>
    public static string DaemonRosterFile => ClaudeConfigSet.Primary.DaemonRosterFile;

    /// <summary>The primary's OAuth tokens. Per-account and never shared, so usage polling reads each
    /// dir's own blob rather than this one.</summary>
    public static string CredentialsFile => ClaudeConfigSet.Primary.CredentialsFile;

    /// <summary>The primary's user-scope settings. Per config dir by design, so never merged.</summary>
    public static string UserSettingsFile => ClaudeConfigSet.Primary.UserSettingsFile;
}
