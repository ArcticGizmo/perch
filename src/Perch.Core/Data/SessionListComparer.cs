namespace Perch.Data;

/// <summary>
/// Whether two scans produced the same session list, field for field. A scan builds fresh lists every time, so plain
/// record equality (which compares list fields by reference) never matches; this compares every other field with the
/// record's own equality and the lists element by element. The session host uses it to skip posting a scan that
/// changed nothing to the UI thread, where each post re-sorts the overlay, re-measures and resizes it, and syncs the
/// Roost and every session window (perf review PF14). Answering "different" when unsure is always safe.
/// </summary>
internal static class SessionListComparer
{
    public static bool Same(IReadOnlyList<ClaudeSession>? a, IReadOnlyList<ClaudeSession> b)
    {
        if (a is null || a.Count != b.Count) return false;
        for (int i = 0; i < a.Count; i++)
            if (!Same(a[i], b[i])) return false;
        return true;
    }

    private static bool Same(ClaudeSession x, ClaudeSession y) =>
        ReferenceEquals(x, y)
        || (Scalars(x) == Scalars(y)
            && SameAgents(x.SubAgents, y.SubAgents)
            && x.Artifacts.SequenceEqual(y.Artifacts)
            && x.Tasks.SequenceEqual(y.Tasks)
            && x.BackgroundTasks.SequenceEqual(y.BackgroundTasks));

    // The session with its list fields set to one shared empty instance, so record equality covers everything else.
    private static ClaudeSession Scalars(ClaudeSession s) => s with
    {
        SubAgents = Array.Empty<SubAgent>(),
        Artifacts = Array.Empty<Artifact>(),
        Tasks = Array.Empty<TaskItem>(),
        BackgroundTasks = Array.Empty<RunningBackgroundTask>(),
    };

    private static bool SameAgents(IReadOnlyList<SubAgent> a, IReadOnlyList<SubAgent> b)
    {
        if (a.Count != b.Count) return false;
        for (int i = 0; i < a.Count; i++)
            if (a[i] with { Children = Array.Empty<SubAgent>() } != b[i] with { Children = Array.Empty<SubAgent>() }
                || !SameAgents(a[i].Children, b[i].Children))
                return false;
        return true;
    }
}
