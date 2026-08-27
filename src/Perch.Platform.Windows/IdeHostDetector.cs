using System.Runtime.InteropServices;
using Perch.Data;

namespace Perch.Platform.Windows;

/// <summary>
/// Windows implementation of <see cref="IIdeHostDetector"/>: walks a session process's ancestry (via a
/// Toolhelp process snapshot) and reports the first ancestor whose executable identifies an editor/IDE
/// (see <see cref="IdeHost.FromExecutable"/>). This catches both an IDE's own integrated terminal and a
/// terminal a user launched from the IDE — in either case the IDE is an ancestor of the claude process.
///
/// <para>The snapshot is cached for <see cref="SnapshotTtlMs"/> so a scan touching every session takes at
/// most one enumeration. Everything is best-effort and swallows failures — detection never blocks or breaks
/// a scan.</para>
/// </summary>
public sealed class IdeHostDetector : IIdeHostDetector
{
    // A whole session scan runs well inside this window, so all sessions share one process enumeration;
    // long enough to be cheap, short enough that a session moving in/out of an IDE is picked up promptly.
    private const int SnapshotTtlMs = 1500;
    // Defensive cap on the ancestry walk — a real chain is a handful deep; this only guards against a
    // pathological or cyclic parent-pid graph (pid reuse can, in theory, produce a loop).
    private const int MaxHops = 32;

    private readonly Lock _gate = new();
    private Dictionary<int, (int ParentPid, string ExeFile)> _snapshot = new();
    private long _snapshotAtTick = long.MinValue;

    public IdeHost? Detect(int pid)
    {
        if (pid <= 0)
            return null;

        try
        {
            var procs = Snapshot();

            var current = pid;
            var visited = new HashSet<int>();
            for (int hop = 0; hop < MaxHops && current > 0 && visited.Add(current); hop++)
            {
                if (!procs.TryGetValue(current, out var info))
                    break;

                var host = IdeHost.FromExecutable(info.ExeFile);
                if (host != null)
                {
                    // Prefer the ancestor's full image path so the app icon can be rendered from the real
                    // executable (Executable then feeds IAppIconProvider). Falls back to the base name from
                    // the snapshot when the full path can't be read.
                    var fullPath = FullImagePath(current);
                    return fullPath != null ? host with { Executable = fullPath } : host;
                }

                if (info.ParentPid == current) // self-parenting guard (System Idle / pid reuse)
                    break;
                current = info.ParentPid;
            }
        }
        catch
        {
            // best-effort — a failed snapshot or walk just means "no IDE", never a thrown scan.
        }

        return null;
    }

    private Dictionary<int, (int ParentPid, string ExeFile)> Snapshot()
    {
        lock (_gate)
        {
            var now = Environment.TickCount64;
            if (now - _snapshotAtTick < SnapshotTtlMs && _snapshot.Count > 0)
                return _snapshot;

            _snapshot = TakeSnapshot();
            _snapshotAtTick = now;
            return _snapshot;
        }
    }

    private static Dictionary<int, (int ParentPid, string ExeFile)> TakeSnapshot()
    {
        var map = new Dictionary<int, (int, string)>();
        var snapshot = CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0);
        if (snapshot == IntPtr.Zero || snapshot == INVALID_HANDLE_VALUE)
            return map;
        try
        {
            var entry = new PROCESSENTRY32 { dwSize = (uint)Marshal.SizeOf<PROCESSENTRY32>() };
            if (!Process32First(snapshot, ref entry))
                return map;
            do
            {
                map[(int)entry.th32ProcessID] = ((int)entry.th32ParentProcessID, entry.szExeFile ?? "");
            }
            while (Process32Next(snapshot, ref entry));
            return map;
        }
        finally
        {
            CloseHandle(snapshot);
        }
    }

    // The full path of a process's executable, via a limited-information handle (works across integrity
    // levels for a same-user process). Null when the process is gone or inaccessible.
    private static string? FullImagePath(int pid)
    {
        IntPtr h = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (h == IntPtr.Zero)
            return null;
        try
        {
            var sb = new System.Text.StringBuilder(1024);
            int cap = sb.Capacity;
            return QueryFullProcessImageName(h, 0, sb, ref cap) ? sb.ToString() : null;
        }
        finally { CloseHandle(h); }
    }

    private const uint TH32CS_SNAPPROCESS = 0x00000002;
    private const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
    private static readonly IntPtr INVALID_HANDLE_VALUE = new(-1);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint dwDesiredAccess, bool bInheritHandle, int dwProcessId);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool QueryFullProcessImageName(
        IntPtr hProcess, uint dwFlags, System.Text.StringBuilder lpExeName, ref int lpdwSize);

    [DllImport("kernel32.dll")]
    private static extern IntPtr CreateToolhelp32Snapshot(uint dwFlags, uint th32ProcessID);

    // CharSet.Auto must match the struct's CharSet.Auto: it selects the Unicode (…W) entry points so
    // szExeFile marshals as Unicode. See the identical note in WindowActivator — mismatching it fills the
    // exe name with garbage and silently breaks every comparison.
    [DllImport("kernel32.dll", CharSet = CharSet.Auto)]
    private static extern bool Process32First(IntPtr hSnapshot, ref PROCESSENTRY32 lppe);

    [DllImport("kernel32.dll", CharSet = CharSet.Auto)]
    private static extern bool Process32Next(IntPtr hSnapshot, ref PROCESSENTRY32 lppe);

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr hObject);

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
}
