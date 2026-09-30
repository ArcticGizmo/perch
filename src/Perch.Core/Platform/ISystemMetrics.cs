using System.Diagnostics;

namespace Perch.Platform;

/// <summary>
/// The platform-specific whole-machine sampling behind a seam, so Perch.Core's <c>MetricsMonitor</c>
/// keeps the delta arithmetic and process-tree roll-up cross-platform while the raw OS reads live in a
/// per-platform implementation (on Windows: <c>GetSystemTimes</c> / <c>GlobalMemoryStatusEx</c> / a
/// Toolhelp snapshot). Every read is point-in-time and stateless — the monitor owns the deltas — and
/// best-effort: an implementation returns null/empty rather than throwing when a counter is unavailable.
/// </summary>
public interface ISystemMetrics
{
    /// <summary>The whole-machine CPU-time counters in 100ns ticks — idle, kernel (which, matching the
    /// Windows <c>GetSystemTimes</c> convention the maths expects, <em>includes</em> idle), and user —
    /// or null when unavailable. The monitor deltas successive readings into a busy fraction.</summary>
    (ulong idle, ulong kernel, ulong user)? ReadCpuTimes();

    /// <summary>Physical RAM as (used, total) bytes, or (0, 0) when unavailable.</summary>
    (long used, long total) ReadMemory();

    /// <summary>A pid → parent-pid map of every process on the machine, for rolling a session up over
    /// its process tree. An empty map (the snapshot failed, or the platform can't enumerate) collapses
    /// each session to its own root pid for that tick.</summary>
    IReadOnlyDictionary<int, int> ReadParentMap();

    /// <summary>One process's working set (bytes) and cumulative CPU time, or null when it has gone or
    /// can't be opened. Called once per measured pid per tick, so it must be a cheap per-process read,
    /// never a whole-machine snapshot. The default goes through <see cref="Process"/>, which is per-pid on
    /// macOS; Windows overrides it because <c>Process.WorkingSet64</c> there takes a snapshot of every
    /// process on the machine each time it's read.</summary>
    (long workingSet, TimeSpan cpu)? ReadProcess(int pid)
    {
        try
        {
            using var proc = Process.GetProcessById(pid);
            return (proc.WorkingSet64, proc.TotalProcessorTime);
        }
        catch
        {
            return null;
        }
    }
}
