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
}

/// <summary>The real probe: a pid is alive iff the OS still has a non-exited process for it. Wraps the
/// logic <see cref="Perch.Data.SessionMonitor"/> previously inlined. <c>Process.GetProcessById</c> is
/// cross-platform, so this lives in the core rather than the platform heads.</summary>
public sealed class SystemProcessProbe : IProcessProbe
{
    public static readonly SystemProcessProbe Instance = new();

    public bool IsAlive(int pid)
    {
        try
        {
            return !Process.GetProcessById(pid).HasExited;
        }
        catch
        {
            return false;
        }
    }
}
