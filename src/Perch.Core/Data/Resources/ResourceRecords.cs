namespace Perch.Data.Resources;

/// <summary>One line of a resource recording (docs/resource-diagnostics-plan.md, "File format"). The recorder
/// raises each record as it writes it, and loading a file parses the same records, so a live recording and a
/// saved one go through one <see cref="ResourceRecording"/> builder.</summary>
public abstract record ResourceRecord;

/// <summary>The first line: what the recording was taken on. <see cref="SelfPid"/> is Perch's own process, so
/// its tree can be attributed to Perch.</summary>
public sealed record ResourceHeader(int Version, DateTime StartedUtc, int IntervalMs, int Cores, long TotalRam, int SelfPid)
    : ResourceRecord
{
    public const int CurrentVersion = 1;
}

/// <summary>A process identity, written once, the first time a tick keeps it. <see cref="Id"/> is the
/// recording's own key: a pid is reused by the OS, an id never is. <see cref="Services"/> are the OS services it
/// hosted when written (what a <c>svchost</c> actually is); null for any other process and in recordings made before
/// they were recorded.</summary>
public sealed record ResourceProc(int Id, int Pid, int ParentPid, string Name, DateTime? StartUtc, string? CommandLine,
    IReadOnlyList<Perch.Platform.ServiceInfo>? Services = null)
    : ResourceRecord
{
    /// <summary>The hosted services as one short label: the first one's display name, "+N" for the others.</summary>
    public string? ServiceLabel => Services is { Count: > 0 } s
        ? s[0].DisplayName + (s.Count > 1 ? $" +{s.Count - 1}" : "")
        : null;
}

/// <summary>A live Claude session's root process and its display label, written when the recorder first sees it.
/// <see cref="Cwd"/> is its working directory with the profile folder masked (<c>~</c>), so the viewer can find the
/// project's hook settings; null in recordings made before it was recorded.</summary>
public sealed record ResourceSession(int Pid, string SessionId, string Label, string? Cwd = null) : ResourceRecord;

/// <summary>One kept process in one tick. <see cref="Memory"/> is the private working set (the working set where
/// the OS can't report that); <see cref="Commit"/> is private bytes. I/O is bytes per second since the last tick.
/// <see cref="WorkingSet"/> is the whole working set, so working set − memory is what the process maps from pages
/// it shares (DLLs, mapped files, shared sections); 0 in recordings made before it was recorded.</summary>
public readonly record struct ProcSample(int Id, double Cpu, long Memory, long Commit, long IoReadPerSec, long IoWritePerSec,
    long WorkingSet = 0)
{
    public long Shared => WorkingSet > Memory ? WorkingSet - Memory : 0;
}

/// <summary>Every process a tick didn't keep, summed.</summary>
public readonly record struct RestSample(double Cpu, long Memory, int Count);

/// <summary>One sample of the whole machine. CPU figures are 0–100 across all cores, like the overlay's.
/// <see cref="Kernel"/> is the OS's own resident memory and <see cref="InterruptCpu"/> the CPU spent on interrupts
/// and DPCs; neither belongs to a process, and both are absent from recordings made before they were recorded.</summary>
public sealed record ResourceTick(
    DateTime AtUtc,
    double Cpu,
    long RamUsed,
    long CommitUsed,
    long CommitLimit,
    IReadOnlyList<ProcSample> Procs,
    RestSample Rest,
    Perch.Platform.KernelMemory? Kernel = null,
    double InterruptCpu = 0,
    int? NewProcesses = null) : ResourceRecord;

/// <summary>A hook (or the status line) registered while the recording ran, so the recording can say which hook started
/// which processes even after the configuration changes. Its command and file have the profile folder masked.</summary>
public sealed record ResourceHook(HookDef Hook) : ResourceRecord;

/// <summary>The last line of a cleanly stopped recording. A recording without one was cut off (crash, power loss).</summary>
public sealed record ResourceEnd(DateTime AtUtc, string Reason) : ResourceRecord;
