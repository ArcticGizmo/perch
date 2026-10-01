using System.Runtime.InteropServices;
using Perch.Platform;

namespace Perch.Platform.Windows;

/// <summary>
/// Windows <see cref="ISystemMetrics"/>: the whole-machine CPU-time counters (<c>GetSystemTimes</c>),
/// physical-RAM figures (<c>GlobalMemoryStatusEx</c>), and the pid→parent-pid map (a Toolhelp process
/// snapshot). Moved verbatim from Perch.Core's <c>MetricsMonitor</c> when the platform seam was
/// introduced — the delta arithmetic and process-tree roll-up stay in Core; only these raw OS reads
/// live here. Every read is best-effort and never throws.
/// </summary>
public sealed class WindowsSystemMetrics : ISystemMetrics
{
    public (ulong idle, ulong kernel, ulong user)? ReadCpuTimes()
    {
        if (!GetSystemTimes(out var idle, out var kernel, out var user))
            return null;
        return (ToTicks(idle), ToTicks(kernel), ToTicks(user));
    }

    public (long used, long total) ReadMemory()
    {
        var mem = new MEMORYSTATUSEX();
        if (!GlobalMemoryStatusEx(mem))
            return (0, 0);
        long total = (long)mem.ullTotalPhys;
        long used  = (long)(mem.ullTotalPhys - mem.ullAvailPhys);
        return (used, total);
    }

    // A pid → parent-pid map of every process on the machine, via a Toolhelp snapshot. Best-effort:
    // an empty map (snapshot failed) just means a session's tree collapses to its root pid this tick.
    public IReadOnlyDictionary<int, int> ReadParentMap()
    {
        var map = new Dictionary<int, int>();
        var snapshot = CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0);
        if (snapshot == IntPtr.Zero || snapshot == INVALID_HANDLE_VALUE)
            return map;
        try
        {
            var entry = new PROCESSENTRY32 { dwSize = (uint)Marshal.SizeOf<PROCESSENTRY32>() };
            if (!Process32First(snapshot, ref entry)) return map;
            do
            {
                map[(int)entry.th32ProcessID] = (int)entry.th32ParentProcessID;
            }
            while (Process32Next(snapshot, ref entry));
        }
        catch { }
        finally
        {
            CloseHandle(snapshot);
        }
        return map;
    }

    // One handle per process, read with GetProcessTimes + GetProcessMemoryInfo. Process.WorkingSet64
    // would take a snapshot of every process on the machine per call, which the monitor used to do
    // for every pid in every session tree every 2s.
    public (long workingSet, TimeSpan cpu)? ReadProcess(int pid)
    {
        var handle = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, (uint)pid);
        if (handle == IntPtr.Zero) return null;
        try
        {
            if (!GetProcessTimes(handle, out _, out _, out var kernel, out var user))
                return null;
            var counters = new PROCESS_MEMORY_COUNTERS { cb = (uint)Marshal.SizeOf<PROCESS_MEMORY_COUNTERS>() };
            if (!K32GetProcessMemoryInfo(handle, ref counters, counters.cb))
                return null;
            return ((long)(ulong)counters.WorkingSetSize, TimeSpan.FromTicks((long)(ToTicks(kernel) + ToTicks(user))));
        }
        catch
        {
            return null;
        }
        finally
        {
            CloseHandle(handle);
        }
    }

    private static ulong ToTicks(FILETIME ft) => ((ulong)ft.High << 32) | ft.Low;

    // ── P/Invoke ──────────────────────────────────────────────────────────────────
    [StructLayout(LayoutKind.Sequential)]
    private struct FILETIME { public uint Low; public uint High; }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetSystemTimes(out FILETIME idleTime, out FILETIME kernelTime, out FILETIME userTime);

    [StructLayout(LayoutKind.Sequential)]
    private sealed class MEMORYSTATUSEX
    {
        public uint dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>();
        public uint dwMemoryLoad;
        public ulong ullTotalPhys;
        public ulong ullAvailPhys;
        public ulong ullTotalPageFile;
        public ulong ullAvailPageFile;
        public ulong ullTotalVirtual;
        public ulong ullAvailVirtual;
        public ulong ullAvailExtendedVirtual;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx([In, Out] MEMORYSTATUSEX lpBuffer);

    private const uint TH32CS_SNAPPROCESS = 0x00000002;
    private static readonly IntPtr INVALID_HANDLE_VALUE = new(-1);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private struct PROCESSENTRY32
    {
        public uint dwSize;
        public uint cntUsage;
        public uint th32ProcessID;
        public IntPtr th32DefaultHeapID;
        public uint th32ModuleID;
        public uint cntThreads;
        public uint th32ParentProcessID;
        public int pcPriClassBase;
        public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string szExeFile;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr CreateToolhelp32Snapshot(uint dwFlags, uint th32ProcessID);

    [DllImport("kernel32.dll")]
    private static extern bool Process32First(IntPtr hSnapshot, ref PROCESSENTRY32 lppe);

    [DllImport("kernel32.dll")]
    private static extern bool Process32Next(IntPtr hSnapshot, ref PROCESSENTRY32 lppe);

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr hObject);

    private const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint dwDesiredAccess, bool bInheritHandle, uint dwProcessId);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetProcessTimes(IntPtr hProcess, out FILETIME creation, out FILETIME exit,
        out FILETIME kernel, out FILETIME user);

    [StructLayout(LayoutKind.Sequential)]
    private struct PROCESS_MEMORY_COUNTERS
    {
        public uint cb;
        public uint PageFaultCount;
        public UIntPtr PeakWorkingSetSize;
        public UIntPtr WorkingSetSize;
        public UIntPtr QuotaPeakPagedPoolUsage;
        public UIntPtr QuotaPagedPoolUsage;
        public UIntPtr QuotaPeakNonPagedPoolUsage;
        public UIntPtr QuotaNonPagedPoolUsage;
        public UIntPtr PagefileUsage;
        public UIntPtr PeakPagefileUsage;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool K32GetProcessMemoryInfo(IntPtr hProcess, ref PROCESS_MEMORY_COUNTERS counters, uint cb);
}
