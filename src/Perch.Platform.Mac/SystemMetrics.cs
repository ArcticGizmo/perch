using System.Runtime.InteropServices;
using Perch.Platform;

namespace Perch.Platform.Mac;

/// <summary>
/// macOS <see cref="ISystemMetrics"/>: whole-machine CPU-time counters via mach
/// <c>host_statistics(HOST_CPU_LOAD_INFO)</c>, physical-RAM figures via <c>sysctlbyname(hw.memsize)</c>
/// + mach <c>host_statistics64(HOST_VM_INFO64)</c>, and the pid→parent-pid map via
/// <c>libproc</c> (<c>proc_listpids</c> + <c>proc_pidinfo(PROC_PIDTBSDINFO)</c>). The delta arithmetic
/// and process-tree roll-up stay in Perch.Core's <c>MetricsMonitor</c>; only these raw reads live here.
/// Every read is best-effort and never throws — an unavailable counter returns null / zero / an empty map,
/// which the monitor tolerates.
///
/// NOTE (Phase 3): the constants and struct offsets are taken from the XNU headers
/// (mach/machine.h, mach/host_info.h, bsd/sys/proc_info.h) but the interop is not yet verified on a Mac.
/// Symbols are imported from the libSystem umbrella (it re-exports the mach, libc, and libproc symbols).
/// </summary>
public sealed class SystemMetrics : ISystemMetrics
{
    // mach_host_self() takes a send right on every call, so cache the host port once rather than leaking
    // one per sample. Runs on first use of this type — which only happens on the macOS head.
    private static readonly uint Host = mach_host_self();

    public (ulong idle, ulong kernel, ulong user)? ReadCpuTimes()
    {
        // HOST_CPU_LOAD_INFO → cpu_ticks[CPU_STATE_MAX] as natural_t (uint32): user, system, idle, nice.
        var info = new uint[CPU_STATE_MAX];
        uint count = (uint)info.Length; // == HOST_CPU_LOAD_INFO_COUNT
        if (host_statistics(Host, HOST_CPU_LOAD_INFO, info, ref count) != KERN_SUCCESS)
            return null;

        ulong user = info[CPU_STATE_USER];
        ulong system = info[CPU_STATE_SYSTEM];
        ulong idle = info[CPU_STATE_IDLE];
        ulong nice = info[CPU_STATE_NICE];

        // Match the Windows GetSystemTimes convention the monitor's delta maths expects: "kernel" INCLUDES
        // idle, and the busy fraction is (kernel + user - idle) / (kernel + user). Fold nice into user and
        // put idle inside kernel, so kernel+user == total ticks and total-idle == busy (= user+nice+system).
        return (idle, system + idle, user + nice);
    }

    public (long used, long total) ReadMemory()
    {
        long total = 0;
        nuint len = (nuint)sizeof(long);
        if (sysctlbyname("hw.memsize", out long mem, ref len, IntPtr.Zero, (nuint)0) == 0)
            total = mem;

        // HOST_VM_INFO64 → vm_statistics64. We read only the leading natural_t (uint32) page counts —
        // free_count[0] and inactive_count[2] — which sit at the stable start of the struct. Pass an
        // oversized buffer + count; host_statistics64 requires count >= HOST_VM_INFO64_COUNT and copies
        // exactly that many entries, so a larger buffer is safe and spares us modelling the whole struct.
        long used = 0;
        if (total > 0)
        {
            var vm = new uint[64];
            uint vcount = (uint)vm.Length;
            if (host_statistics64(Host, HOST_VM_INFO64, vm, ref vcount) == KERN_SUCCESS && vcount >= 3)
            {
                long pageSize = Environment.SystemPageSize;
                long free = (long)vm[0] * pageSize;      // free_count
                long inactive = (long)vm[2] * pageSize;  // inactive_count (largely reclaimable file cache)
                // Approximate "in use" as everything that isn't free or inactive (active + wired +
                // compressed). Under-reports vs Activity Monitor but is stable and reads only offsets 0/2.
                used = total - free - inactive;
                if (used < 0) used = 0;
                if (used > total) used = total;
            }
        }
        return (used, total);
    }

    public IReadOnlyDictionary<int, int> ReadParentMap()
    {
        var map = new Dictionary<int, int>();

        // proc_listpids(PROC_ALL_PIDS): a null buffer returns the size needed (bytes); the second call
        // fills an int32[] of pids and returns the bytes written.
        int bytes = proc_listpids(PROC_ALL_PIDS, 0, IntPtr.Zero, 0);
        if (bytes <= 0) return map;

        int capacity = bytes / sizeof(int) + 16; // slack for processes spawned between the two calls
        var pids = new int[capacity];
        int got;
        var pinned = GCHandle.Alloc(pids, GCHandleType.Pinned);
        try
        {
            got = proc_listpids(PROC_ALL_PIDS, 0, pinned.AddrOfPinnedObject(), capacity * sizeof(int));
        }
        finally { pinned.Free(); }
        if (got <= 0) return map;
        int n = got / sizeof(int);

        // proc_pidinfo(PROC_PIDTBSDINFO) fills a proc_bsdinfo (~152 bytes). Rather than model the whole
        // struct, allocate a comfortably larger buffer and read pbi_ppid — the 5th uint32, at offset 16.
        const int bufSize = 256;
        const int ppidOffset = 16;
        IntPtr buf = Marshal.AllocHGlobal(bufSize);
        try
        {
            for (int i = 0; i < n; i++)
            {
                int pid = pids[i];
                if (pid <= 0) continue;
                int r = proc_pidinfo(pid, PROC_PIDTBSDINFO, 0, buf, bufSize);
                if (r < ppidOffset + sizeof(int)) continue; // no ppid written (dead pid / access denied)
                map[pid] = Marshal.ReadInt32(buf, ppidOffset);
            }
        }
        finally { Marshal.FreeHGlobal(buf); }
        return map;
    }

    // ── constants (XNU headers) ─────────────────────────────────────────────────────
    private const int KERN_SUCCESS = 0;
    private const int HOST_CPU_LOAD_INFO = 3;
    private const int HOST_VM_INFO64 = 4;
    private const int CPU_STATE_MAX = 4, CPU_STATE_USER = 0, CPU_STATE_SYSTEM = 1, CPU_STATE_IDLE = 2, CPU_STATE_NICE = 3;
    private const uint PROC_ALL_PIDS = 1;
    private const int PROC_PIDTBSDINFO = 3;

    // ── P/Invoke (libSystem umbrella: mach host stats, libc sysctl, libproc process listing) ──────────
    [DllImport("libSystem.dylib")]
    private static extern uint mach_host_self();

    [DllImport("libSystem.dylib")]
    private static extern int host_statistics(uint host, int flavor, uint[] info, ref uint count);

    [DllImport("libSystem.dylib")]
    private static extern int host_statistics64(uint host, int flavor, uint[] info, ref uint count);

    [DllImport("libSystem.dylib")]
    private static extern int sysctlbyname(string name, out long oldp, ref nuint oldlenp, IntPtr newp, nuint newlen);

    [DllImport("libSystem.dylib")]
    private static extern int proc_listpids(uint type, uint typeinfo, IntPtr buffer, int buffersize);

    [DllImport("libSystem.dylib")]
    private static extern int proc_pidinfo(int pid, int flavor, ulong arg, IntPtr buffer, int buffersize);
}
