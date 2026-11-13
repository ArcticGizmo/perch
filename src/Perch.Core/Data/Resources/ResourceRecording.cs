namespace Perch.Data.Resources;

/// <summary>
/// A resource recording in memory, built record by record: from the recorder while it runs, or from a file.
/// Not thread-safe; the UI owns one and feeds it on the UI thread.
/// </summary>
public sealed class ResourceRecording
{
    private readonly Dictionary<int, ResourceProc> _procs = new();
    private readonly List<ResourceSession> _sessions = new();
    private readonly List<ResourceTick> _ticks = new();
    private readonly Dictionary<int, List<ResourceProc>> _procsByPid = new();
    private readonly List<HookDef> _hooks = new();

    /// <summary>The hooks registered while the recording ran (empty for recordings made before they were recorded).</summary>
    public IReadOnlyList<HookDef> Hooks => _hooks;

    public ResourceHeader? Header { get; private set; }
    public ResourceEnd? End { get; private set; }
    public string? FilePath { get; init; }

    public IReadOnlyDictionary<int, ResourceProc> Procs => _procs;
    public IReadOnlyList<ResourceSession> Sessions => _sessions;
    public IReadOnlyList<ResourceTick> Ticks => _ticks;

    public int Cores => Header is { Cores: > 0 } h ? h.Cores : Environment.ProcessorCount;
    public long TotalRam => Header?.TotalRam ?? 0;

    /// <summary>Bumped on every record, so a view can tell its cached analysis is stale.</summary>
    public int Version { get; private set; }

    public void Add(ResourceRecord record)
    {
        switch (record)
        {
            case ResourceHeader h: Header = h; break;
            case ResourceProc p:
                _procs[p.Id] = p;
                if (!_procsByPid.TryGetValue(p.Pid, out var list))
                    _procsByPid[p.Pid] = list = new List<ResourceProc>();
                list.Add(p);
                break;
            case ResourceSession s:
                if (!_sessions.Any(x => x.Pid == s.Pid && x.SessionId == s.SessionId))
                    _sessions.Add(s);
                break;
            case ResourceTick t: _ticks.Add(t); break;
            case ResourceEnd e: End = e; break;
            case ResourceHook h: _hooks.Add(h.Hook); break;
        }
        Version++;
    }

    /// <summary>The process that had <paramref name="pid"/> when a process that started at
    /// <paramref name="notAfter"/> was created: the latest one with that pid that started no later. A parent
    /// can't start after its child, so a later holder of the pid is a recycled stranger.</summary>
    public ResourceProc? ProcByPid(int pid, DateTime? notAfter)
    {
        if (!_procsByPid.TryGetValue(pid, out var list)) return null;
        ResourceProc? best = null;
        foreach (var p in list)
        {
            if (notAfter is { } limit && p.StartUtc is { } start && start > limit.AddSeconds(1)) continue;
            if (best is null || (p.StartUtc ?? DateTime.MinValue) >= (best.StartUtc ?? DateTime.MinValue))
                best = p;
        }
        return best;
    }

    /// <summary>A copy that stops here, for building an analysis on a worker while the live recording keeps growing on
    /// the UI thread. The records themselves are immutable and shared; only the collections are copied.</summary>
    public ResourceRecording Snapshot()
    {
        var copy = new ResourceRecording { FilePath = FilePath, Header = Header, End = End, Version = Version };
        foreach (var (id, p) in _procs) copy._procs[id] = p;
        foreach (var (pid, list) in _procsByPid) copy._procsByPid[pid] = [.. list];
        copy._sessions.AddRange(_sessions);
        copy._ticks.AddRange(_ticks);
        copy._hooks.AddRange(_hooks);
        return copy;
    }

    public static ResourceRecording Load(string path)
    {
        var recording = new ResourceRecording { FilePath = path };
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream);
        while (reader.ReadLine() is { } line)
        {
            if (ResourceRecordCodec.Parse(line) is { } record)
                recording.Add(record);
        }
        return recording;
    }
}
