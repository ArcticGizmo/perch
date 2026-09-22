namespace Perch.Statusline;

using System.IO;
using Perch.Data;

/// <summary>
/// Applies a profile to Claude Code's <c>settings.json</c> — the one place that knows a Perch profile
/// becomes a generated standalone script while an external profile is written verbatim. Shared by the
/// <c>perch statusline</c> CLI and the designer window so both do exactly the same thing.
/// </summary>
internal static class StatuslineInstaller
{
    public static string ScriptPath => StatuslineScript.DefaultScriptPath;

    /// <summary>Writes the profile into the PRIMARY config dir's <c>settings.json</c> (<c>~/.claude</c> or
    /// <c>CLAUDE_CONFIG_DIR</c>). A Perch profile is compiled to the standalone Node script (returned in
    /// <paramref name="scriptPath"/>) that settings.json runs directly; an external profile's command is
    /// written unchanged. Returns false if the script or settings write failed.</summary>
    public static bool Apply(StatuslineProfile profile, out string? scriptPath) =>
        Apply(profile, ClaudePaths.UserSettingsFile, StatuslineScript.DefaultScriptPath, out scriptPath);

    /// <summary>Applies the profile into a SPECIFIC config dir — its own <c>settings.json</c>, with the
    /// generated script placed beside it (<c>{root}/perch-statusline.mjs</c>) so the dir is self-contained.
    /// This is how a multi-config-dir setup targets one org's dir rather than the process-wide primary.</summary>
    public static bool Apply(StatuslineProfile profile, ClaudeConfigDir dir, out string? scriptPath) =>
        Apply(profile, dir.UserSettingsFile, StatuslineScript.ScriptPathFor(dir.Root), out scriptPath);

    private static bool Apply(StatuslineProfile profile, string settingsFile, string scriptDest, out string? scriptPath)
    {
        scriptPath = null;
        if (profile.IsPerch)
        {
            if (string.IsNullOrEmpty(profile.Template)) return false;
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(scriptDest)!);
                File.WriteAllText(scriptDest, StatuslineScript.Generate(profile));
            }
            catch
            {
                return false;
            }
            scriptPath = scriptDest;
            return ClaudeUserSettings.SetStatusLine(settingsFile, StatuslineScript.CommandFor(scriptDest), profile.Padding);
        }

        return !string.IsNullOrWhiteSpace(profile.Command)
            && ClaudeUserSettings.SetStatusLine(settingsFile, profile.Command!, profile.Padding);
    }

    /// <summary>The command currently in the primary <c>settings.json → statusLine</c>, or null.</summary>
    public static string? CurrentCommand() => ClaudeUserSettings.ReadStatusLineCommand();

    /// <summary>The command currently in a specific config dir's <c>settings.json → statusLine</c>, or null.</summary>
    public static string? CurrentCommand(ClaudeConfigDir dir) => ClaudeUserSettings.ReadStatusLineCommand(dir.UserSettingsFile);
}
