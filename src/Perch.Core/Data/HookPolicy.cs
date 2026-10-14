namespace Perch.Data;

/// <summary>
/// Pure policy for whether Perch's hooks are installed into a given config directory, from its provenance
/// plus the user's opt-outs (keyed by resolved real path). One place so the settings UI's status line, the
/// add/edit dialog and <c>HookInstaller.ReconcileAll</c> can never disagree.
/// </summary>
/// <remarks>
/// The rules: the <b>primary</b> is always hooked (the accept-edits badge, session control and auto-start
/// depend on it). Every other dir is one the user added, so it defaults ON and can be opted out.
/// </remarks>
internal static class HookPolicy
{
    /// <summary>Whether hooks are installed into a directory. The primary is always on regardless of the
    /// list; anything else is on unless the list names it.</summary>
    public static bool IsEnabled(
        ConfigDirProvenance provenance, string realRoot, IReadOnlyCollection<string> disabledRealRoots)
    {
        if (provenance == ConfigDirProvenance.Primary) return true;
        var key = Path.TrimEndingDirectorySeparator(realRoot);
        foreach (var r in disabledRealRoots)
            if (ClaudeConfigDir.PathComparer.Equals(Path.TrimEndingDirectorySeparator(r), key))
                return false;
        return true;
    }
}
