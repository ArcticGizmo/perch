using System.Diagnostics;

namespace Perch.Platform;

/// <summary>
/// A portable <see cref="IProcessTable"/> over <see cref="Process"/> plus the platform's parent map. It has no
/// private working set, I/O counters or command lines, and <see cref="Process.GetProcesses()"/> is heavy, so it
/// stands in only until a head has a native implementation (the macOS head, for now).
/// </summary>
public sealed class ManagedProcessTable(ISystemMetrics metrics) : IProcessTable
{
    public IReadOnlyList<ProcessEntry> Enumerate()
    {
        var parents = metrics.ReadParentMap();
        var list = new List<ProcessEntry>();
        Process[] all;
        try { all = Process.GetProcesses(); }
        catch { return list; }
        foreach (var p in all)
        {
            try
            {
                int threads = 0;
                try { threads = p.Threads.Count; } catch { }
                list.Add(new ProcessEntry(p.Id, parents.GetValueOrDefault(p.Id), p.ProcessName, threads));
            }
            catch { }
            finally { p.Dispose(); }
        }
        return list;
    }

    public ProcessCounters? Read(int pid)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            DateTime? start = null;
            try { start = p.StartTime.ToUniversalTime(); } catch { }
            int handles = 0;
            try { handles = p.HandleCount; } catch { }
            long ws = p.WorkingSet64;
            return new ProcessCounters(start, p.TotalProcessorTime, ws, ws, p.PrivateMemorySize64, 0, 0, handles);
        }
        catch
        {
            return null;
        }
    }

    public string? ReadCommandLine(int pid) => null;

    public (long used, long limit) ReadCommit() => (0, 0);
}
