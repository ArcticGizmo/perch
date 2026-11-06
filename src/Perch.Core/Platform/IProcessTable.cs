namespace Perch.Platform;

/// <summary>One process from a whole-machine snapshot: enough to build the tree and name it, nothing that needs
/// a handle to the process.</summary>
public readonly record struct ProcessEntry(int Pid, int ParentPid, string Name, int Threads);

/// <summary>One process's counters at a point in time. <see cref="Cpu"/> and the I/O byte counts are cumulative
/// since the process started, so the reader deltas successive readings. <see cref="PrivateWorkingSet"/> is the
/// figure Task Manager's Memory column shows (0 when the OS can't report it); <see cref="Commit"/> is private
/// bytes.</summary>
public readonly record struct ProcessCounters(
    DateTime? StartUtc,
    TimeSpan Cpu,
    long WorkingSet,
    long PrivateWorkingSet,
    long Commit,
    ulong IoReadBytes,
    ulong IoWriteBytes,
    int Handles);

/// <summary>A process with its counters, or null counters when the OS wouldn't give them.</summary>
public readonly record struct ProcessSnapshot(ProcessEntry Entry, ProcessCounters? Counters);

/// <summary>Resident memory the OS holds outside any process's private working set, in bytes. The modified list
/// is dirty pages waiting to be written out; it counts as "in use" until they are.</summary>
public readonly record struct KernelMemory(
    long PagedPoolResident,
    long NonPagedPool,
    long SystemCacheResident,
    long DriverResident,
    long ModifiedList);

/// <summary>Cumulative CPU time across all processors: the total (busy + idle) and the part spent servicing
/// interrupts and deferred procedure calls, which no process is charged for.</summary>
public readonly record struct ProcessorTimes(TimeSpan Total, TimeSpan InterruptAndDpc);

/// <summary>An OS service running in a process: its short name (<c>wuauserv</c>) and the name people know it by
/// (<c>Windows Update</c>).</summary>
public readonly record struct ServiceInfo(string Name, string DisplayName);

/// <summary>
/// Whole-machine process sampling for the resource diagnostics recorder (docs/resource-diagnostics-plan.md).
/// Separate from <see cref="ISystemMetrics"/> because it reads far more per process, and runs only while the
/// user is recording. Every read is best-effort and never throws.
/// </summary>
public interface IProcessTable
{
    /// <summary>Every process on the machine, from one snapshot. Empty when the snapshot fails.</summary>
    IReadOnlyList<ProcessEntry> Enumerate();

    /// <summary>One process's counters, or null when it has exited or can't be opened (protected, elevated).
    /// Must be a cheap per-process read.</summary>
    ProcessCounters? Read(int pid);

    /// <summary>Every process with its counters. A platform that can read them all at once without opening each
    /// process (Windows) overrides this, which also covers processes the user may not open.</summary>
    IReadOnlyList<ProcessSnapshot> Snapshot() =>
        Enumerate().Select(e => new ProcessSnapshot(e, Read(e.Pid))).ToList();

    /// <summary>The process's command line, or null when it can't be read.</summary>
    string? ReadCommandLine(int pid);

    /// <summary>The system commit charge as (used, limit) bytes, or (0, 0) when unavailable.</summary>
    (long used, long limit) ReadCommit();

    /// <summary>Kernel and cache memory, or null when the platform can't say.</summary>
    KernelMemory? ReadKernelMemory() => null;

    /// <summary>Cumulative processor times including interrupt/DPC time, or null when the platform can't say.</summary>
    ProcessorTimes? ReadProcessorTimes() => null;

    /// <summary>The running services by the pid hosting them, so a generic service host (<c>svchost</c>) can be
    /// named by what it runs. Empty when the platform has no such notion or the lookup fails.</summary>
    IReadOnlyDictionary<int, IReadOnlyList<ServiceInfo>> ReadServices() => new Dictionary<int, IReadOnlyList<ServiceInfo>>();
}
