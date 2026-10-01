using System.Diagnostics;

namespace Perch.Platform;

/// <summary>
/// Tests whether an OS process is still alive. Abstracted so replay can report recorded (long-dead)
/// pids as "alive" while their session's active window covers the current scrub position — without a
/// probe, <see cref="Perch.Data.SessionMonitor"/> drops every session whose pid isn't a live process,
/// which would silently discard an entire recording. Production uses <see cref="SystemProcessProbe"/>.
/// </summary>
public interface IProcessProbe
{
    bool IsAlive(int pid);

    /// <summary>As <see cref="IsAlive(int)"/>, but also false when the pid has been recycled: the live process
    /// started well after <paramref name="startedAt"/>, the start the session record carries (null = unknown,
    /// don't check). The default ignores the start time, which suits replay's synthetic pids.</summary>
    bool IsAlive(int pid, DateTime? startedAt) => IsAlive(pid);
}

/// <summary>The real probe: a pid is alive iff the OS still has a non-exited process for it, and, when the
/// caller knows when its process started, that process isn't a newer one that inherited the pid
/// (<see cref="Perch.Data.ProcessIdentity"/>). <c>Process.GetProcessById</c> is cross-platform, so this lives
/// in the core rather than the platform heads.</summary>
public sealed class SystemProcessProbe : IProcessProbe
{
    public static readonly SystemProcessProbe Instance = new();

    public bool IsAlive(int pid) => IsAlive(pid, null);

    public bool IsAlive(int pid, DateTime? startedAt)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            if (process.HasExited) return false;
            if (startedAt is not { } recorded) return true;
            DateTime processStart;
            try { processStart = process.StartTime; }
            catch { return true; }   // can't read its start time: keep the pid-only answer rather than hide a session
            return !Perch.Data.ProcessIdentity.IsRecycled(processStart, recorded);
        }
        catch
        {
            return false;
        }
    }
}
