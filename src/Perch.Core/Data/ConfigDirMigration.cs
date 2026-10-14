namespace Perch.Data;

/// <summary>
/// The one-time move from auto-discovered config dirs to an explicit list. Older versions put every
/// <c>~/.claude*</c> folder, claude-envs env and session-reported dir into the set on their own; now only
/// declared dirs are members. So an upgrade doesn't silently drop dirs a user relied on, each one the old
/// discovery would have shown (i.e. not one the user had removed) is declared, keeping its hooks state:
/// a session-reported dir was hooked by default, a scanned one only if the user had opted it in. The legacy
/// <see cref="AppSettings.HiddenConfigDirs"/> / <see cref="AppSettings.HooksEnabledDirs"/> lists are then
/// cleared. Pure — the caller supplies what the old discovery would have found
/// (<c>ClaudeConfigSet.Suggestions(asOldDiscovery: true)</c>) and saves.
/// </summary>
internal static class ConfigDirMigration
{
    /// <summary>Migrates <paramref name="settings"/> unless it's already explicit. Returns true when it ran
    /// (so the caller should save).</summary>
    public static bool Apply(AppSettings settings, IReadOnlyList<ConfigDirSuggestion> found)
    {
        if (settings.ConfigDirsExplicit) return false;

        foreach (var f in found)
        {
            if (Lists(settings.HiddenConfigDirs, f.RealRoot)) continue;
            // Already declared: it was a member (hooked by default) all along — leave it and its hooks be.
            if (Lists(settings.DeclaredConfigDirs, f.Root) || Lists(settings.DeclaredConfigDirs, f.RealRoot)) continue;

            (settings.DeclaredConfigDirs ??= new()).Add(f.Root);

            bool wasHooked = f.Source == ConfigDirSuggestionSource.ReportedBySession
                || Lists(settings.HooksEnabledDirs, f.RealRoot);
            if (!wasHooked && !Lists(settings.HooksDisabledDirs, f.RealRoot))
                (settings.HooksDisabledDirs ??= new()).Add(f.RealRoot);
        }

        settings.HiddenConfigDirs = null;
        settings.HooksEnabledDirs = null;
        settings.ConfigDirsExplicit = true;
        return true;
    }

    private static bool Lists(List<string>? list, string root) =>
        list?.Any(x => ClaudeConfigDir.SameRoot(x, root)) ?? false;
}
