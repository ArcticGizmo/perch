namespace Perch.Data;

/// <summary>
/// Whether a live process is the one a session record describes, judged by start time (review fixes CP25).
/// Session files (<c>sessions/{pid}.json</c>) and the daemon roster are keyed by pid and outlive an unclean exit,
/// so a recycled pid would otherwise keep a dead session "alive" in the overlay, or point a kill at an unrelated
/// process. Windows recycles pids quickly.
/// </summary>
internal static class ProcessIdentity
{
    /// <summary>How far the recorded start may trail the process's real start and still be the same process.
    /// Claude Code writes <c>startedAt</c> a beat after the process starts (1-3 s, measured). A recycled pid
    /// belongs to a process launched minutes or hours later, which this comfortably separates.</summary>
    public static readonly TimeSpan Tolerance = TimeSpan.FromMinutes(2);

    /// <summary>True when a process that started at <paramref name="processStart"/> can't be the one recorded
    /// as starting at <paramref name="recordedStart"/>, because it started after it (beyond
    /// <see cref="Tolerance"/>). One-sided on purpose: a live process <em>older</em> than the record holds the
    /// pid, so it's the one that wrote the record, even if the record's start were ever refreshed mid-session.
    /// Both times are local.</summary>
    public static bool IsRecycled(DateTime processStart, DateTime recordedStart) =>
        processStart - recordedStart > Tolerance;
}
