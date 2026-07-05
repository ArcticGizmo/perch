using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Perch.Data;

/// <summary>A whole-machine CPU + physical-RAM reading. <see cref="CpuPercent"/> is 0–100 across all
/// cores (the same normalisation <see cref="SessionMetrics"/> uses, so a session's number is directly
/// comparable to the machine total). Empty until the first successful sample.</summary>
public readonly record struct SystemMetrics(double CpuPercent, long UsedRamBytes, long TotalRamBytes)
{
    public static readonly SystemMetrics Empty = new(0, 0, 0);

    /// <summary>Physical RAM in use as a percentage of the total, or 0 when unknown.</summary>
    public double RamPercent => TotalRamBytes > 0 ? 100.0 * UsedRamBytes / TotalRamBytes : 0;

    /// <summary>True once a real reading has landed (a total-RAM figure means the sample succeeded).</summary>
    public bool HasData => TotalRamBytes > 0;
}

/// <summary>
/// A single session's resource use, rolled up over its process tree when subprocess metrics are on
/// (otherwise just the session's own <c>claude</c> process). <see cref="CpuPercent"/> is 0–100 across
/// all cores. <see cref="ProcessCount"/> is how many processes were summed (1 = the root alone), so
/// the UI can show "n procs" when the tree was walked.
/// </summary>
public readonly record struct SessionMetrics(double CpuPercent, long RamBytes, int ProcessCount);

/// <summary>Pure helpers for turning raw counter deltas into percentages — split out so the arithmetic
/// is unit-testable without any live processes or timers.</summary>
internal static class MetricsMath
{
    /// <summary>System CPU busy-fraction from a pair of <c>GetSystemTimes</c> deltas (all in 100ns
    /// ticks). <paramref name="kernelDelta"/> already includes idle, so busy = kernel + user − idle.
    /// Returns 0 when no time elapsed (the priming sample).</summary>
    public static double SystemCpuPercent(ulong idleDelta, ulong kernelDelta, ulong userDelta)
    {
        ulong total = kernelDelta + userDelta;
        if (total == 0) return 0;
        double busy = (double)total - idleDelta;
        return Math.Clamp(busy / total * 100.0, 0, 100);
    }

    /// <summary>A process's CPU use as a percentage of the whole machine (0–100) from the CPU-time it
    /// consumed over a wall-clock window, divided across all cores. Returns 0 for a non-positive
    /// window (the priming sample, or a clock that didn't advance).</summary>
    public static double ProcessCpuPercent(TimeSpan cpuDelta, TimeSpan wallDelta, int cores)
    {
        if (wallDelta <= TimeSpan.Zero || cores <= 0) return 0;
        double pct = cpuDelta.TotalMilliseconds / (wallDelta.TotalMilliseconds * cores) * 100.0;
        return Math.Clamp(pct, 0, 100);
    }
}

/// <summary>Pure process-tree walking, kept separate from the sampling so it can be tested against a
/// hand-built pid→parent map.</summary>
internal static class ProcessTree
{
    /// <summary>The <paramref name="root"/> pid plus every descendant, from a pid→parent-pid map.
    /// Cycle- and self-parent-safe (a visited set bounds it), so a recycled pid can't spin it.</summary>
    public static IReadOnlyList<int> SelfAndDescendants(int root, IReadOnlyDictionary<int, int> parentByPid)
    {
        // Invert to parent→children once, then breadth-first from the root.
        var childrenByParent = new Dictionary<int, List<int>>();
        foreach (var (pid, parent) in parentByPid)
        {
            if (pid == parent) continue; // a process is never its own parent; ignore if the table says so
            if (!childrenByParent.TryGetValue(parent, out var kids))
                childrenByParent[parent] = kids = new List<int>();
            kids.Add(pid);
        }

        var result = new List<int>();
        var seen = new HashSet<int>();
        var queue = new Queue<int>();
        queue.Enqueue(root);
        seen.Add(root);
        while (queue.Count > 0)
        {
            int cur = queue.Dequeue();
            result.Add(cur);
            if (!childrenByParent.TryGetValue(cur, out var kids)) continue;
            foreach (var kid in kids)
                if (seen.Add(kid))
                    queue.Enqueue(kid);
        }
        return result;
    }
}

/// <summary>
/// Samples whole-machine and per-session CPU/RAM on a fixed cadence and raises <see cref="Updated"/>
/// with the results. Runs entirely off the UI thread on its own <see cref="System.Threading.Timer"/>;
/// the owner marshals the event onto the UI thread (the UI layer's dispatch helper).
///
/// CPU needs two samples to produce a delta, so the very first tick reports 0 and the next (one
/// interval later) is the first real reading. Sampling only runs while at least one of the system /
/// per-session switches is on — with both off the timer is stopped and no counters are read at all.
///
/// Per-session numbers are rolled up over the session's process <em>tree</em> when
/// <see cref="IncludeSubprocesses"/> is on (the <c>claude</c> process plus the MCP servers, shells and
/// tools it spawns); off, only the session's own process is measured. Sub-agents (Task/Agent) share
/// the session's process and so can't be measured apart from it — they fold into the session total.
/// </summary>
internal sealed class MetricsMonitor : IDisposable
{
    private const int SampleIntervalMs = 2000;

    private readonly System.Threading.Timer _timer;
    private bool _running;
    private bool _disposed;
    // Guards against a slow sample overlapping the next tick (0 = free, 1 = sampling).
    private int _sampling;

    // Sampling switches, set by the owner from settings via Configure. Each gates its own work so a
    // disabled feature costs nothing: with both off the timer doesn't run at all; with only one on we
    // do just that one's work (per-session still needs the machine's total RAM as its bar denominator,
    // so RAM is read whenever either is on — but system CPU is computed only while the strip is shown).
    private bool _systemEnabled;
    private bool _perSessionEnabled;
    public bool IncludeSubprocesses { get; private set; }

    // The session root pids to measure, swapped atomically from the UI thread (reference assignment)
    // and read on the timer thread.
    private volatile int[] _sessionPids = [];

    // Per-pid cumulative CPU time from the previous sample, so we can delta it. Only touched inside a
    // sample (the _sampling gate serialises those), so it needs no lock.
    private Dictionary<int, TimeSpan> _prevCpu = new();
    private DateTime _prevSampleAt;
    private (ulong idle, ulong kernel, ulong user)? _prevSystemTimes;

    /// <summary>Raised on the timer (thread-pool) thread after each sample with the whole-machine
    /// reading and a per-session-pid map. The owner marshals this onto the UI thread.</summary>
    public event Action<SystemMetrics, IReadOnlyDictionary<string, SessionMetrics>>? Updated;

    public MetricsMonitor() => _timer = new System.Threading.Timer(_ => Sample());

    /// <summary>Applies the three monitoring switches and starts or stops sampling to match. Sampling
    /// runs only while <paramref name="system"/> or <paramref name="perSession"/> is on.</summary>
    public void Configure(bool system, bool perSession, bool subprocess)
    {
        _systemEnabled     = system;
        _perSessionEnabled = perSession;
        IncludeSubprocesses = subprocess;

        bool shouldRun = system || perSession;
        if (shouldRun && !_running)
        {
            _running = true;
            // Prime immediately (yields 0% CPU) then tick on the interval; the second sample is the
            // first real CPU reading.
            _timer.Change(0, SampleIntervalMs);
        }
        else if (!shouldRun && _running)
        {
            _running = false;
            _timer.Change(Timeout.Infinite, Timeout.Infinite);
            // Drop the deltas so a later re-enable starts clean rather than crediting one process a
            // huge jump for all the CPU it burned while we weren't looking.
            _prevCpu = new();
            _prevSystemTimes = null;
        }
    }

    /// <summary>Tells the monitor which session processes to measure. Called from the UI thread on each
    /// scan; the array is swapped in atomically for the timer thread to read.</summary>
    public void SetSessionPids(IEnumerable<string> pids)
    {
        var parsed = new List<int>();
        foreach (var p in pids)
            if (int.TryParse(p, out var id))
                parsed.Add(id);
        _sessionPids = parsed.ToArray();
    }

    private void Sample()
    {
        if (_disposed) return;
        // Skip if a previous sample is still in flight (samples are quick, so this is belt-and-braces).
        if (Interlocked.CompareExchange(ref _sampling, 1, 0) != 0) return;
        try
        {
            var now = DateTime.UtcNow;
            var wallDelta = _prevSampleAt == default ? TimeSpan.Zero : now - _prevSampleAt;

            // System CPU is only computed while the strip is shown; RAM is still read when per-session
            // is on (it's the bar denominator). When the strip is hidden and per-session off the timer
            // isn't running, so we never reach here doing nothing.
            var system = _systemEnabled
                ? SampleSystem(includeCpu: true)
                : (_perSessionEnabled ? SampleSystem(includeCpu: false) : SystemMetrics.Empty);

            var perSession = _perSessionEnabled
                ? SamplePerSession(wallDelta)
                : (IReadOnlyDictionary<string, SessionMetrics>)EmptySessions;

            _prevSampleAt = now;

            Updated?.Invoke(system, perSession);
        }
        catch
        {
            // A transient failure (a process vanished mid-read, a counter hiccup) just skips this tick;
            // the next one recovers.
        }
        finally
        {
            Interlocked.Exchange(ref _sampling, 0);
        }
    }

    private static readonly Dictionary<string, SessionMetrics> EmptySessions = new();

    // Reads the whole-machine RAM (always) and, when <paramref name="includeCpu"/>, the CPU busy-
    // fraction. Skipping the CPU read when the strip is hidden avoids the GetSystemTimes call — and
    // clears the delta baseline so re-enabling the strip starts clean rather than off one stale tick.
    private SystemMetrics SampleSystem(bool includeCpu)
    {
        double cpu = 0;
        if (includeCpu && GetSystemTimes(out var idle, out var kernel, out var user))
        {
            var cur = (ToTicks(idle), ToTicks(kernel), ToTicks(user));
            if (_prevSystemTimes is { } prev)
                cpu = MetricsMath.SystemCpuPercent(
                    cur.Item1 - prev.idle, cur.Item2 - prev.kernel, cur.Item3 - prev.user);
            _prevSystemTimes = cur;
        }
        else if (!includeCpu)
        {
            _prevSystemTimes = null;
        }

        long used = 0, total = 0;
        var mem = new MEMORYSTATUSEX();
        if (GlobalMemoryStatusEx(mem))
        {
            total = (long)mem.ullTotalPhys;
            used  = (long)(mem.ullTotalPhys - mem.ullAvailPhys);
        }

        return new SystemMetrics(cpu, used, total);
    }

    private IReadOnlyDictionary<string, SessionMetrics> SamplePerSession(TimeSpan wallDelta)
    {
        var pids = _sessionPids;
        if (pids.Length == 0)
        {
            _prevCpu = new();
            return EmptySessions;
        }

        // A single fresh CPU-time reading per process this tick, reused across sessions that might
        // share a descendant, and carried into _prevCpu for next tick's delta.
        var curCpu = new Dictionary<int, TimeSpan>();
        var parentByPid = IncludeSubprocesses ? BuildParentMap() : null;
        int cores = Environment.ProcessorCount;

        var result = new Dictionary<string, SessionMetrics>(pids.Length);
        foreach (var root in pids)
        {
            var tree = parentByPid != null
                ? ProcessTree.SelfAndDescendants(root, parentByPid)
                : (IReadOnlyList<int>)[root];

            long ram = 0;
            TimeSpan cpuDelta = TimeSpan.Zero;
            int counted = 0;

            foreach (var pid in tree)
            {
                if (!TryReadProcess(pid, curCpu, out var procRam, out var procCpu))
                    continue;
                counted++;
                ram += procRam;
                if (_prevCpu.TryGetValue(pid, out var was) && procCpu >= was)
                    cpuDelta += procCpu - was;
            }

            double cpuPct = MetricsMath.ProcessCpuPercent(cpuDelta, wallDelta, cores);
            // Report a session even when its process momentarily can't be read (counted == 0) so the
            // row's bar doesn't flicker out; it simply shows zero that tick.
            result[root.ToString()] = new SessionMetrics(cpuPct, ram, counted);
        }

        _prevCpu = curCpu;
        return result;
    }

    // Reads a process's working set and total CPU time, recording the CPU time into curCpu for the
    // next delta. Returns false if the process has gone or is inaccessible.
    private static bool TryReadProcess(int pid, Dictionary<int, TimeSpan> curCpu, out long ram, out TimeSpan cpu)
    {
        ram = 0;
        cpu = TimeSpan.Zero;
        try
        {
            using var proc = Process.GetProcessById(pid);
            ram = proc.WorkingSet64;
            cpu = proc.TotalProcessorTime;
            curCpu[pid] = cpu;
            return true;
        }
        catch
        {
            return false;
        }
    }

    // A pid → parent-pid map of every process on the machine, via a Toolhelp snapshot. Best-effort:
    // an empty map (snapshot failed) just means the tree collapses to the root pid this tick.
    private static Dictionary<int, int> BuildParentMap()
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

    private static ulong ToTicks(FILETIME ft) => ((ulong)ft.High << 32) | ft.Low;

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _timer.Dispose();
    }

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
}
