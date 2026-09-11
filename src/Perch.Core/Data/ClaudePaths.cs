namespace Perch.Data;

/// <summary>
/// The paths under the <em>primary</em> config directory — <c>CLAUDE_CONFIG_DIR</c>, else
/// <c>~/.claude</c>. What is left here is global to this Perch; anything belonging to a particular
/// session goes through <see cref="ClaudeSession.ConfigDir"/> instead.
/// </summary>
internal static class ClaudePaths
{
    /// <summary>The primary Claude Code config directory. See <see cref="ClaudeConfigSet.Primary"/>.</summary>
    public static string ClaudeDir => ClaudeConfigSet.Primary.Root;

    /// <summary>The <em>primary's</em> session sidecars; discovery reads every config dir's — see
    /// <see cref="ClaudeConfigSet.DistinctSessionsDirs"/>.</summary>
    public static string SessionsDir => ClaudeConfigSet.Primary.SessionsDir;

    /// <summary>The <em>primary's</em> transcript tree; config dirs commonly share one physical tree,
    /// so enumeration goes through <see cref="ClaudeConfigSet.DistinctProjectsDirs"/>.</summary>
    public static string ProjectsDir => ClaudeConfigSet.Primary.ProjectsDir;

    /// <summary><c>~/.claude/plugins</c> — installed-plugin state and marketplace clones.</summary>
    public static string PluginsDir => ClaudeConfigSet.Primary.PluginsDir;

    /// <summary>The background daemon's state directory. Only the default dir ever has one.</summary>
    public static string DaemonDir => ClaudeConfigSet.Primary.DaemonDir;

    /// <summary><c>~/.claude/daemon/roster.json</c> — the daemon supervisor's registry of the headless
    /// worker sessions it is currently hosting.</summary>
    public static string DaemonRosterFile => ClaudeConfigSet.Primary.DaemonRosterFile;

    /// <summary>The primary's user-scope settings. Per config dir by design, so never merged.</summary>
    public static string UserSettingsFile => ClaudeConfigSet.Primary.UserSettingsFile;
}
