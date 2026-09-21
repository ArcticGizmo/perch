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

    /// <summary>Writes the profile into <c>settings.json</c>. A Perch profile is compiled to the standalone
    /// Node script (returned in <paramref name="scriptPath"/>) that settings.json runs directly; an external
    /// profile's command is written unchanged. Returns false if the script or settings write failed.</summary>
    public static bool Apply(StatuslineProfile profile, out string? scriptPath)
    {
        scriptPath = null;
        if (profile.IsPerch)
        {
            if (string.IsNullOrEmpty(profile.Template)) return false;
            var path = StatuslineScript.DefaultScriptPath;
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllText(path, StatuslineScript.Generate(profile));
            }
            catch
            {
                return false;
            }
            scriptPath = path;
            return ClaudeUserSettings.SetStatusLine(StatuslineScript.CommandFor(path), profile.Padding);
        }

        return !string.IsNullOrWhiteSpace(profile.Command)
            && ClaudeUserSettings.SetStatusLine(profile.Command!, profile.Padding);
    }

    /// <summary>The command currently in <c>settings.json → statusLine</c> (whatever wrote it), or null.</summary>
    public static string? CurrentCommand() => ClaudeUserSettings.ReadStatusLineCommand();
}
