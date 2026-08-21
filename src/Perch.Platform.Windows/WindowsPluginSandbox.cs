using System.Diagnostics;
using System.Runtime.InteropServices;
using Perch.Plugins;

namespace Perch.Platform.Windows;

/// <summary>
/// The hardened Windows plugin launcher. It starts the child exactly like the cross-platform
/// <see cref="ProcessPluginSandbox"/> (same redirected UTF-8 stdio) and then wraps it in a Windows
/// <b>Job Object</b> that bounds what a misbehaving plugin can do to the machine:
/// <list type="bullet">
///   <item>a job-wide memory cap, so a runaway plugin can't exhaust RAM;</item>
///   <item>an active-process cap, so it can't fork-bomb;</item>
///   <item><c>KILL_ON_JOB_CLOSE</c>, so closing the job (on dispose, or if Perch itself dies) terminates
///         the plugin and every child it spawned — no orphaned plugin processes ever survive the tray.</item>
/// </list>
/// Every hardening step is best-effort: if the OS refuses any of it, the plugin still launches (unsandboxed
/// but process-isolated), because a plugin that won't run is worse than one that runs less confined — the
/// capability grants are still enforced at the message layer regardless. Network/filesystem confinement
/// (AppContainer) is a further step tracked in docs/pluggability-plan.md.
/// </summary>
internal sealed class WindowsPluginSandbox : IPluginSandbox
{
    // Generous enough that powershell + git sit well under it, tight enough to stop a multi-GB runaway.
    private const ulong JobMemoryLimitBytes = 1024UL * 1024 * 1024;   // 1 GiB
    private const uint ActiveProcessLimit = 16;

    public IPluginProcess Launch(PluginLaunchSpec spec)
    {
        var proc = Process.Start(ProcessPluginSandbox.BuildStartInfo(spec))
            ?? throw new InvalidOperationException($"failed to start plugin process '{spec.Command}'.");

        IntPtr job = IntPtr.Zero;
        try
        {
            job = CreateJobObject(IntPtr.Zero, null);
            if (job != IntPtr.Zero && ConfigureJob(job))
                AssignProcessToJobObject(job, proc.Handle); // best-effort; nested jobs are fine on Win8+
            else if (job != IntPtr.Zero) { CloseHandle(job); job = IntPtr.Zero; }
        }
        catch
        {
            if (job != IntPtr.Zero) { CloseHandle(job); job = IntPtr.Zero; }
        }

        return new JobProcessHandle(proc, job);
    }

    private static bool ConfigureJob(IntPtr job)
    {
        var info = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION();
        info.BasicLimitInformation.LimitFlags =
            JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE | JOB_OBJECT_LIMIT_ACTIVE_PROCESS | JOB_OBJECT_LIMIT_JOB_MEMORY;
        info.BasicLimitInformation.ActiveProcessLimit = ActiveProcessLimit;
        info.JobMemoryLimit = (UIntPtr)JobMemoryLimitBytes;

        int len = Marshal.SizeOf<JOBOBJECT_EXTENDED_LIMIT_INFORMATION>();
        IntPtr buf = Marshal.AllocHGlobal(len);
        try
        {
            Marshal.StructureToPtr(info, buf, fDeleteOld: false);
            return SetInformationJobObject(job, JobObjectExtendedLimitInformation, buf, (uint)len);
        }
        finally { Marshal.FreeHGlobal(buf); }
    }

    private sealed class JobProcessHandle(Process proc, IntPtr job) : IPluginProcess
    {
        public TextWriter StandardInput => proc.StandardInput;
        public TextReader StandardOutput => proc.StandardOutput;

        public async Task<int> WaitForExitAsync(CancellationToken ct)
        {
            await proc.WaitForExitAsync(ct);
            return proc.ExitCode;
        }

        public void Kill()
        {
            try { proc.Kill(entireProcessTree: true); } catch { }
        }

        public void Dispose()
        {
            try { if (!proc.HasExited) proc.Kill(entireProcessTree: true); } catch { }
            proc.Dispose();
            // Closing the job terminates any process still in it (KILL_ON_JOB_CLOSE) — the backstop against
            // a child the tree-kill missed.
            if (job != IntPtr.Zero) { try { CloseHandle(job); } catch { } }
        }
    }

    // ── Interop ──────────────────────────────────────────────────────────────────────
    private const int JobObjectExtendedLimitInformation = 9;
    private const uint JOB_OBJECT_LIMIT_ACTIVE_PROCESS = 0x00000008;
    private const uint JOB_OBJECT_LIMIT_JOB_MEMORY = 0x00000200;
    private const uint JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE = 0x00002000;

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateJobObject(IntPtr lpJobAttributes, string? lpName);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AssignProcessToJobObject(IntPtr hJob, IntPtr hProcess);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetInformationJobObject(
        IntPtr hJob, int jobObjectInformationClass, IntPtr lpJobObjectInformation, uint cbJobObjectInformationLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr hObject);

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_BASIC_LIMIT_INFORMATION
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public UIntPtr MinimumWorkingSetSize;
        public UIntPtr MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public UIntPtr Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IO_COUNTERS
    {
        public ulong ReadOperationCount, WriteOperationCount, OtherOperationCount;
        public ulong ReadTransferCount, WriteTransferCount, OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
    {
        public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
        public IO_COUNTERS IoInfo;
        public UIntPtr ProcessMemoryLimit;
        public UIntPtr JobMemoryLimit;
        public UIntPtr PeakProcessMemoryUsed;
        public UIntPtr PeakJobMemoryUsed;
    }
}
