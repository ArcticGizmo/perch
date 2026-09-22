namespace Perch.Data;

/// <summary>
/// Pure policy for whether Perch's hooks are installed into a given config directory, from its provenance
/// plus the user's opt-in / opt-out overrides (both keyed by resolved real path). One place so the settings
/// UI's status line, the add/edit dialog and <c>HookInstaller.ReconcileAll</c> can never disagree.
/// </summary>
/// <remarks>
/// The rules: the <b>primary</b> is always hooked (the accept-edits badge, session control and auto-start
/// depend on it). A <b>declared</b> or <b>self-reported</b> directory defaults ON — the user brought it in,
/// so Perch treats it as a first-class target — and can be opted out. An <b>auto-discovered</b> (convention)
/// directory defaults OFF: Perch doesn't hook a directory it merely pattern-matched until the user turns it
/// on, but nothing stops them turning it on. An override list always wins over the default.
/// </remarks>
internal static class HookPolicy
{
    /// <summary>The default (no override) hooks state for a provenance: on for everything except an
    /// auto-discovered (convention-only) directory.</summary>
    public static bool DefaultEnabled(ConfigDirProvenance provenance) =>
        provenance != ConfigDirProvenance.Convention;

    /// <summary>Whether hooks are installed into a directory, resolving overrides against the default. The
    /// primary is always on regardless of the lists.</summary>
    public static bool IsEnabled(
        ConfigDirProvenance provenance, string realRoot,
        IReadOnlyCollection<string> disabledRealRoots, IReadOnlyCollection<string> enabledRealRoots)
    {
        if (provenance == ConfigDirProvenance.Primary) return true;
        var key = Path.TrimEndingDirectorySeparator(realRoot);
        if (Contains(enabledRealRoots, key)) return true;
        if (Contains(disabledRealRoots, key)) return false;
        return DefaultEnabled(provenance);
    }

    private static bool Contains(IReadOnlyCollection<string> roots, string key)
    {
        foreach (var r in roots)
            if (ClaudeConfigDir.PathComparer.Equals(Path.TrimEndingDirectorySeparator(r), key))
                return true;
        return false;
    }
}
