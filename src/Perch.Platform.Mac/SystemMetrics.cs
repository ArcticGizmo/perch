using Perch.Platform;

namespace Perch.Platform.Mac;

/// <summary>
/// macOS <see cref="ISystemMetrics"/>. Phase 3: CPU via <c>host_statistics(HOST_CPU_LOAD_INFO)</c>,
/// memory via <c>host_statistics64</c>/<c>sysctl</c>, and the parent-pid map via <c>libproc</c>
/// (<c>proc_listpids</c> + <c>proc_pidinfo</c>) — returning the same tuples the Windows impl does so
/// <c>MetricsMonitor</c>'s delta maths is unchanged. Stub for now: reports "unavailable", which the
/// monitor tolerates (each session collapses to its own root pid).
/// </summary>
public sealed class SystemMetrics : ISystemMetrics
{
    public (ulong idle, ulong kernel, ulong user)? ReadCpuTimes() => null;
    public (long used, long total) ReadMemory() => (0, 0);
    public IReadOnlyDictionary<int, int> ReadParentMap() => new Dictionary<int, int>();
}
