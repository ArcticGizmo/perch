using System.Runtime.InteropServices;
using Perch.Platform;

namespace Perch.Platform.Windows;

/// <summary>
/// Windows <see cref="IProcessTable"/>. <see cref="Snapshot"/> reads every process's counters from one
/// <c>NtQuerySystemInformation(SystemProcessInformation)</c> call, the source Task Manager and <c>Get-Process</c>
/// use: it opens no process, so SYSTEM services, protected processes (Defender, CrowdStrike) and the WSL/Docker
/// VM (<c>vmmemWSL</c>) are all counted without elevation. Only the command line needs a
/// <c>PROCESS_QUERY_LIMITED_INFORMATION</c> handle, answered by
/// <c>NtQueryInformationProcess(ProcessCommandLineInformation)</c> without reading the process's memory.
/// </summary>
public sealed class WindowsProcessTable : IProcessTable, IDisposable
{
    private readonly Lock _gate = new();
    private IntPtr _buffer;
    private int _bufferSize;
    private IntPtr _pdhQuery;
    private IntPtr[]? _pdhCounters;
    private bool _pdhFailed;

    public IReadOnlyList<ProcessSnapshot> Snapshot()
    {
        // The offsets below are the 64-bit SYSTEM_PROCESS_INFORMATION layout.
        if (IntPtr.Size != 8) return Enumerate().Select(e => new ProcessSnapshot(e, Read(e.Pid))).ToList();
        lock (_gate)
        {
            try
            {
                if (!Query(SystemProcessInformation)) return Enumerate().Select(e => new ProcessSnapshot(e, Read(e.Pid))).ToList();
                var list = new List<ProcessSnapshot>(512);
                long offset = 0;
                while (offset >= 0 && offset < _returned)
                {
                    IntPtr e = _buffer + (int)offset;
                    int next = Marshal.ReadInt32(e, 0);
                    int threads = Marshal.ReadInt32(e, 4);
                    long privateWs = Marshal.ReadInt64(e, 8);
                    long created = Marshal.ReadInt64(e, 32);
                    long user = Marshal.ReadInt64(e, 40);
                    long kernel = Marshal.ReadInt64(e, 48);
                    int nameBytes = (ushort)Marshal.ReadInt16(e, 56);
                    IntPtr namePtr = Marshal.ReadIntPtr(e, 64);
                    int pid = (int)Marshal.ReadInt64(e, 80);
                    int ppid = (int)Marshal.ReadInt64(e, 88);
                    int handles = Marshal.ReadInt32(e, 96);
                    long workingSet = Marshal.ReadInt64(e, 144);
                    long commit = Marshal.ReadInt64(e, 184);
                    long ioRead = Marshal.ReadInt64(e, 232);
                    long ioWrite = Marshal.ReadInt64(e, 240);

                    string name = nameBytes > 0 && namePtr != IntPtr.Zero ? Marshal.PtrToStringUni(namePtr, nameBytes / 2)
                        : pid == 0 ? "System Idle Process" : pid == 4 ? "System" : "";
                    DateTime? start = null;
                    if (created > 0)
                    {
                        try { start = DateTime.FromFileTimeUtc(created); } catch { }
                    }
                    var counters = new ProcessCounters(start, TimeSpan.FromTicks(user + kernel), workingSet, privateWs, commit,
                        (ulong)Math.Max(0, ioRead), (ulong)Math.Max(0, ioWrite), handles);
                    list.Add(new ProcessSnapshot(new ProcessEntry(pid, ppid, name, threads), counters));

                    if (next <= 0) break;
                    offset += next;
                }
                return list;
            }
            catch
            {
                return [];
            }
        }
    }

    public ProcessorTimes? ReadProcessorTimes()
    {
        lock (_gate)
        {
            // SYSTEM_PROCESSOR_PERFORMANCE_INFORMATION, one per processor in this group: Idle, Kernel (includes idle),
            // User, Dpc, Interrupt (100 ns ticks), InterruptCount, padded to 48 bytes. The kernel rejects a buffer that
            // isn't a whole number of entries, so this one is sized exactly.
            const int Stride = 48;
            int processors = Math.Max(1, Environment.ProcessorCount);
            IntPtr buffer = Marshal.AllocHGlobal(processors * Stride);
            try
            {
                if (NtQuerySystemInformation(SystemProcessorPerformanceInformation, buffer, processors * Stride, out int returned) != 0)
                    return null;
                int count = Math.Min(processors, returned / Stride);
                long total = 0, interrupt = 0;
                for (int i = 0; i < count; i++)
                {
                    IntPtr p = buffer + i * Stride;
                    total += Marshal.ReadInt64(p, 8) + Marshal.ReadInt64(p, 16);
                    interrupt += Marshal.ReadInt64(p, 24) + Marshal.ReadInt64(p, 32);
                }
                return count == 0 ? null : new ProcessorTimes(TimeSpan.FromTicks(total), TimeSpan.FromTicks(interrupt));
            }
            catch
            {
                return null;
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }
    }

    private static readonly string[] KernelCounterPaths =
    [
        @"\Memory\Pool Paged Resident Bytes",
        @"\Memory\Pool Nonpaged Bytes",
        @"\Memory\System Cache Resident Bytes",
        @"\Memory\System Driver Resident Bytes",
        @"\Memory\Modified Page List Bytes",
    ];

    // Performance counters rather than SYSTEM_PERFORMANCE_INFORMATION: the counters are documented and stable
    // across Windows versions, and readable by any user.
    public KernelMemory? ReadKernelMemory()
    {
        lock (_gate)
        {
            try
            {
                if (_pdhFailed) return null;
                if (_pdhCounters is null)
                {
                    if (PdhOpenQueryW(null, IntPtr.Zero, out _pdhQuery) != 0) { _pdhFailed = true; return null; }
                    var counters = new IntPtr[KernelCounterPaths.Length];
                    for (int i = 0; i < counters.Length; i++)
                    {
                        if (PdhAddEnglishCounterW(_pdhQuery, KernelCounterPaths[i], IntPtr.Zero, out counters[i]) != 0)
                        {
                            _pdhFailed = true;
                            return null;
                        }
                    }
                    _pdhCounters = counters;
                }
                if (PdhCollectQueryData(_pdhQuery) != 0) return null;
                var v = new long[_pdhCounters.Length];
                for (int i = 0; i < v.Length; i++)
                {
                    if (PdhGetFormattedCounterValue(_pdhCounters[i], PDH_FMT_LARGE, out _, out var value) != 0 || value.CStatus > 1)
                        return null;
                    v[i] = value.LargeValue;
                }
                return new KernelMemory(v[0], v[1], v[2], v[3], v[4]);
            }
            catch
            {
                return null;
            }
        }
    }

    private int _returned;

    // Fills _buffer with one NtQuerySystemInformation class, growing it as the kernel asks. Kept between calls:
    // the process list is a few hundred KB and every tick needs it.
    private bool Query(int infoClass)
    {
        int size = Math.Max(_bufferSize, 512 * 1024);
        for (int attempt = 0; attempt < 6; attempt++)
        {
            if (_buffer == IntPtr.Zero || size > _bufferSize)
            {
                if (_buffer != IntPtr.Zero) Marshal.FreeHGlobal(_buffer);
                _buffer = Marshal.AllocHGlobal(size);
                _bufferSize = size;
            }
            int status = NtQuerySystemInformation(infoClass, _buffer, _bufferSize, out int needed);
            if (status == 0)
            {
                _returned = needed;
                return true;
            }
            if (status != STATUS_INFO_LENGTH_MISMATCH) return false;
            size = Math.Max(needed + 64 * 1024, _bufferSize * 2);
        }
        return false;
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_pdhQuery != IntPtr.Zero) PdhCloseQuery(_pdhQuery);
            _pdhQuery = IntPtr.Zero;
            _pdhCounters = null;
            if (_buffer != IntPtr.Zero) Marshal.FreeHGlobal(_buffer);
            _buffer = IntPtr.Zero;
            _bufferSize = 0;
        }
    }

    public IReadOnlyList<ProcessEntry> Enumerate()
    {
        var list = new List<ProcessEntry>(400);
        var snapshot = CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0);
        if (snapshot == IntPtr.Zero || snapshot == INVALID_HANDLE_VALUE)
            return list;
        try
        {
            var entry = new PROCESSENTRY32 { dwSize = (uint)Marshal.SizeOf<PROCESSENTRY32>() };
            if (!Process32First(snapshot, ref entry)) return list;
            do
            {
                list.Add(new ProcessEntry((int)entry.th32ProcessID, (int)entry.th32ParentProcessID,
                    entry.szExeFile ?? "", (int)entry.cntThreads));
            }
            while (Process32Next(snapshot, ref entry));
        }
        catch { }
        finally
        {
            CloseHandle(snapshot);
        }
        return list;
    }

    public ProcessCounters? Read(int pid)
    {
        if (pid == 0) return null;
        var handle = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, (uint)pid);
        if (handle == IntPtr.Zero) return null;
        try
        {
            if (!GetProcessTimes(handle, out var creation, out _, out var kernel, out var user))
                return null;

            var mem = new PROCESS_MEMORY_COUNTERS_EX2 { cb = (uint)Marshal.SizeOf<PROCESS_MEMORY_COUNTERS_EX2>() };
            if (!K32GetProcessMemoryInfo(handle, ref mem, mem.cb))
            {
                // Before Windows 10 1809 only the EX layout exists, which lacks the private working set.
                mem = new PROCESS_MEMORY_COUNTERS_EX2 { cb = ExSize };
                if (!K32GetProcessMemoryInfo(handle, ref mem, mem.cb))
                    return null;
            }

            ulong ioRead = 0, ioWrite = 0;
            if (GetProcessIoCounters(handle, out var io))
            {
                ioRead = io.ReadTransferCount;
                ioWrite = io.WriteTransferCount;
            }
            GetProcessHandleCount(handle, out uint handles);

            DateTime? start = null;
            ulong created = ToTicks(creation);
            if (created > 0)
            {
                try { start = DateTime.FromFileTimeUtc((long)created); } catch { }
            }

            return new ProcessCounters(
                start,
                TimeSpan.FromTicks((long)(ToTicks(kernel) + ToTicks(user))),
                (long)(ulong)mem.WorkingSetSize,
                (long)(ulong)mem.PrivateWorkingSetSize,
                (long)(ulong)mem.PrivateUsage,
                ioRead, ioWrite, (int)handles);
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

    public string? ReadCommandLine(int pid)
    {
        if (pid is 0 or 4) return null;
        var handle = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, (uint)pid);
        if (handle == IntPtr.Zero) return null;
        IntPtr buffer = IntPtr.Zero;
        try
        {
            int size = 1024;
            for (int attempt = 0; attempt < 3; attempt++)
            {
                buffer = Marshal.AllocHGlobal(size);
                int status = NtQueryInformationProcess(handle, ProcessCommandLineInformation, buffer, size, out int needed);
                if (status == 0)
                {
                    // The buffer starts with a UNICODE_STRING whose Buffer points just past it, inside our allocation.
                    ushort lengthBytes = (ushort)Marshal.ReadInt16(buffer);
                    IntPtr chars = Marshal.ReadIntPtr(buffer, IntPtr.Size);
                    return lengthBytes == 0 || chars == IntPtr.Zero ? "" : Marshal.PtrToStringUni(chars, lengthBytes / 2);
                }
                Marshal.FreeHGlobal(buffer);
                buffer = IntPtr.Zero;
                if ((status != STATUS_INFO_LENGTH_MISMATCH && status != STATUS_BUFFER_TOO_SMALL) || needed <= size)
                    return null;
                size = needed;
            }
            return null;
        }
        catch
        {
            return null;
        }
        finally
        {
            if (buffer != IntPtr.Zero) Marshal.FreeHGlobal(buffer);
            CloseHandle(handle);
        }
    }

    // The Service Control Manager, not svchost's command line: a SYSTEM service's command line needs elevation to
    // read, while any user may enumerate services with their pids (it's what Task Manager's Services tab shows).
    public IReadOnlyDictionary<int, IReadOnlyList<ServiceInfo>> ReadServices()
    {
        var result = new Dictionary<int, IReadOnlyList<ServiceInfo>>();
        IntPtr scm = OpenSCManagerW(null, null, SC_MANAGER_ENUMERATE_SERVICE);
        if (scm == IntPtr.Zero) return result;
        IntPtr buffer = IntPtr.Zero;
        try
        {
            var byPid = new Dictionary<int, List<ServiceInfo>>();
            int stride = Marshal.SizeOf<ENUM_SERVICE_STATUS_PROCESS>();
            int size = 64 * 1024, resume = 0;
            for (int attempt = 0; attempt < 8; attempt++)
            {
                if (buffer == IntPtr.Zero) buffer = Marshal.AllocHGlobal(size);
                bool ok = EnumServicesStatusExW(scm, SC_ENUM_PROCESS_INFO, SERVICE_WIN32, SERVICE_ACTIVE, buffer, size,
                    out int needed, out int returned, ref resume, null);
                int error = ok ? 0 : Marshal.GetLastWin32Error();
                if (!ok && error != ERROR_MORE_DATA) break;
                for (int i = 0; i < returned; i++)
                {
                    var s = Marshal.PtrToStructure<ENUM_SERVICE_STATUS_PROCESS>(buffer + i * stride);
                    if (s.ProcessId == 0) continue;
                    string name = Marshal.PtrToStringUni(s.ServiceName) ?? "";
                    string display = Marshal.PtrToStringUni(s.DisplayName) ?? "";
                    if (name.Length == 0) continue;
                    if (!byPid.TryGetValue((int)s.ProcessId, out var list)) byPid[(int)s.ProcessId] = list = new();
                    list.Add(new ServiceInfo(name, display.Length > 0 ? display : name));
                }
                if (ok) break;
                // More remain: carry on from the resume handle, with room for at least the next entry.
                if (needed > size)
                {
                    Marshal.FreeHGlobal(buffer);
                    buffer = IntPtr.Zero;
                    size = needed;
                }
            }
            foreach (var (pid, list) in byPid) result[pid] = list;
        }
        catch { }
        finally
        {
            if (buffer != IntPtr.Zero) Marshal.FreeHGlobal(buffer);
            CloseServiceHandle(scm);
        }
        return result;
    }

    public (long used, long limit) ReadCommit()
    {
        var info = new PERFORMANCE_INFORMATION { cb = (uint)Marshal.SizeOf<PERFORMANCE_INFORMATION>() };
        if (!K32GetPerformanceInfo(ref info, info.cb)) return (0, 0);
        long page = (long)(ulong)info.PageSize;
        return ((long)(ulong)info.CommitTotal * page, (long)(ulong)info.CommitLimit * page);
    }

    private static ulong ToTicks(FILETIME ft) => ((ulong)ft.High << 32) | ft.Low;

    // ── P/Invoke ──────────────────────────────────────────────────────────────────
    private const uint TH32CS_SNAPPROCESS = 0x00000002;
    private const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
    private const int ProcessCommandLineInformation = 60;
    private const int STATUS_INFO_LENGTH_MISMATCH = unchecked((int)0xC0000004);
    private const int STATUS_BUFFER_TOO_SMALL = unchecked((int)0xC0000023);
    private static readonly IntPtr INVALID_HANDLE_VALUE = new(-1);

    // PROCESS_MEMORY_COUNTERS_EX: the EX2 layout minus its last two SIZE_T fields.
    private static readonly uint ExSize = (uint)(Marshal.SizeOf<PROCESS_MEMORY_COUNTERS_EX2>() - 2 * IntPtr.Size);

    [StructLayout(LayoutKind.Sequential)]
    private struct FILETIME { public uint Low; public uint High; }

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

    [StructLayout(LayoutKind.Sequential)]
    private struct PROCESS_MEMORY_COUNTERS_EX2
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
        public UIntPtr PrivateUsage;
        public UIntPtr PrivateWorkingSetSize;
        public UIntPtr SharedCommitUsage;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IO_COUNTERS
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PERFORMANCE_INFORMATION
    {
        public uint cb;
        public UIntPtr CommitTotal;
        public UIntPtr CommitLimit;
        public UIntPtr CommitPeak;
        public UIntPtr PhysicalTotal;
        public UIntPtr PhysicalAvailable;
        public UIntPtr SystemCache;
        public UIntPtr KernelTotal;
        public UIntPtr KernelPaged;
        public UIntPtr KernelNonpaged;
        public UIntPtr PageSize;
        public uint HandleCount;
        public uint ProcessCount;
        public uint ThreadCount;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr CreateToolhelp32Snapshot(uint dwFlags, uint th32ProcessID);

    // CharSet.Auto must match the struct's so szExeFile marshals as Unicode (see IdeHostDetector).
    [DllImport("kernel32.dll", CharSet = CharSet.Auto)]
    private static extern bool Process32First(IntPtr hSnapshot, ref PROCESSENTRY32 lppe);

    [DllImport("kernel32.dll", CharSet = CharSet.Auto)]
    private static extern bool Process32Next(IntPtr hSnapshot, ref PROCESSENTRY32 lppe);

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr hObject);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint dwDesiredAccess, bool bInheritHandle, uint dwProcessId);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetProcessTimes(IntPtr hProcess, out FILETIME creation, out FILETIME exit,
        out FILETIME kernel, out FILETIME user);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool K32GetProcessMemoryInfo(IntPtr hProcess, ref PROCESS_MEMORY_COUNTERS_EX2 counters, uint cb);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetProcessIoCounters(IntPtr hProcess, out IO_COUNTERS counters);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetProcessHandleCount(IntPtr hProcess, out uint count);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool K32GetPerformanceInfo(ref PERFORMANCE_INFORMATION info, uint cb);

    private const int SystemProcessInformation = 5;
    private const int SystemProcessorPerformanceInformation = 8;
    private const uint PDH_FMT_LARGE = 0x00000400;

    [StructLayout(LayoutKind.Explicit)]
    private struct PDH_FMT_COUNTERVALUE
    {
        [FieldOffset(0)] public uint CStatus;
        [FieldOffset(8)] public long LargeValue;
    }

    [DllImport("ntdll.dll")]
    private static extern int NtQuerySystemInformation(int systemInformationClass, IntPtr systemInformation,
        int systemInformationLength, out int returnLength);

    [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
    private static extern int PdhOpenQueryW(string? dataSource, IntPtr userData, out IntPtr query);

    [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
    private static extern int PdhAddEnglishCounterW(IntPtr query, string fullCounterPath, IntPtr userData, out IntPtr counter);

    [DllImport("pdh.dll")]
    private static extern int PdhCollectQueryData(IntPtr query);

    [DllImport("pdh.dll")]
    private static extern int PdhGetFormattedCounterValue(IntPtr counter, uint format, out uint type, out PDH_FMT_COUNTERVALUE value);

    [DllImport("pdh.dll")]
    private static extern int PdhCloseQuery(IntPtr query);

    private const uint SC_MANAGER_ENUMERATE_SERVICE = 0x0004;
    private const int SC_ENUM_PROCESS_INFO = 0;
    private const uint SERVICE_WIN32 = 0x30;
    private const uint SERVICE_ACTIVE = 0x1;
    private const int ERROR_MORE_DATA = 234;

    [StructLayout(LayoutKind.Sequential)]
    private struct ENUM_SERVICE_STATUS_PROCESS
    {
        public IntPtr ServiceName;
        public IntPtr DisplayName;
        public uint ServiceType;
        public uint CurrentState;
        public uint ControlsAccepted;
        public uint Win32ExitCode;
        public uint ServiceSpecificExitCode;
        public uint CheckPoint;
        public uint WaitHint;
        public uint ProcessId;
        public uint ServiceFlags;
    }

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr OpenSCManagerW(string? machineName, string? databaseName, uint desiredAccess);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumServicesStatusExW(IntPtr scManager, int infoLevel, uint serviceType, uint serviceState,
        IntPtr services, int bufSize, out int bytesNeeded, out int servicesReturned, ref int resumeHandle, string? groupName);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseServiceHandle(IntPtr handle);

    [DllImport("ntdll.dll")]
    private static extern int NtQueryInformationProcess(IntPtr processHandle, int processInformationClass,
        IntPtr processInformation, int processInformationLength, out int returnLength);
}
