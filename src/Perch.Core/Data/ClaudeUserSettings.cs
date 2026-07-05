namespace Perch.Data;

using System.Text.Json;
using System.Text.Json.Nodes;

/// <summary>
/// Read/modify/write helper for the user-scope Claude Code settings file
/// (<c>~/.claude/settings.json</c>, see <see cref="ClaudePaths.UserSettingsFile"/>). Used to flip the
/// experimental feature env vars Claude Code reads on launch while preserving every other key in the
/// file. Best-effort: a missing or unreadable file reads as "unset", and any write failure is
/// swallowed (the toggle simply doesn't stick). Comments in the file, if any, are dropped on rewrite.
/// </summary>
internal static class ClaudeUserSettings
{
    // The env var Claude Code reads to turn on the experimental Agent Teams feature. "1" enables it;
    // the key's absence is treated as off, and disabling removes it rather than writing "0".
    private const string AgentTeamsEnvKey = "CLAUDE_CODE_EXPERIMENTAL_AGENT_TEAMS";

    // Tolerant parse: ~/.claude/settings.json is hand-editable, so allow the slips Claude Code itself
    // tolerates (trailing commas, // comments) rather than failing the read.
    private static readonly JsonDocumentOptions ReadOptions = new()
    {
        AllowTrailingCommas = true,
        CommentHandling     = JsonCommentHandling.Skip,
    };

    /// <summary>True when <c>env.CLAUDE_CODE_EXPERIMENTAL_AGENT_TEAMS</c> is set to "1".</summary>
    public static bool IsAgentTeamsEnabled()
    {
        try
        {
            var path = ClaudePaths.UserSettingsFile;
            if (!File.Exists(path))
                return false;

            var root = JsonNode.Parse(File.ReadAllText(path), documentOptions: ReadOptions) as JsonObject;
            // ToString() rather than GetValue<string>() so a numeric 1 doesn't throw — either reads "1".
            return (root?["env"] as JsonObject)?[AgentTeamsEnvKey]?.ToString() == "1";
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Sets or clears <c>env.CLAUDE_CODE_EXPERIMENTAL_AGENT_TEAMS</c> in <c>~/.claude/settings.json</c>,
    /// preserving every other setting. Enabling writes "1"; disabling removes the key (and the env
    /// object if that empties it). Returns true on a successful write.
    /// </summary>
    public static bool SetAgentTeamsEnabled(bool enabled)
    {
        try
        {
            var path = ClaudePaths.UserSettingsFile;
            var root = File.Exists(path)
                ? JsonNode.Parse(File.ReadAllText(path), documentOptions: ReadOptions) as JsonObject ?? new JsonObject()
                : new JsonObject();

            if (root["env"] is not JsonObject env)
            {
                if (!enabled) return true;   // already absent — nothing to write
                env = new JsonObject();
                root["env"] = env;
            }

            if (enabled)
            {
                env[AgentTeamsEnvKey] = "1";
            }
            else
            {
                env.Remove(AgentTeamsEnvKey);
                if (env.Count == 0) root.Remove("env");
            }

            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            return true;
        }
        catch
        {
            return false;
        }
    }
}
