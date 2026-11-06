using Perch.Platform;

namespace Perch.Data.Resources;

/// <summary>A live Claude session's root process, as the recorder is told about it.</summary>
/// <summary>A live Claude session's root process, as the recorder is told about it. <see cref="ConfigRoot"/> is the
/// session's Claude config dir, for reading its hooks; it isn't recorded.</summary>
public readonly record struct SessionTag(int Pid, string SessionId, string Label, string? Cwd = null, string? ConfigRoot = null);

/// <summary>
/// Samples the whole machine every <see cref="Interval"/> while recording and appends each record to a JSONL file
/// (docs/resource-diagnostics-plan.md). Separate from the overlay's <c>MetricsMonitor</c>: it reads every process,
/// and it runs only between an explicit <see cref="Start"/> and <see cref="Stop"/>, never by itself.
/// </summary>
public sealed class ResourceRecorder : IDisposable
{
    public static readonly TimeSpan Interval = TimeSpan.FromSeconds(5);
    public static readonly TimeSpan MaxDuration = TimeSpan.FromHours(4);

    /// <summary>How many of the top CPU and top memory consumers each tick keeps, beyond the session and Perch trees.</summary>
    public const int TopN = 15;

    // Ancestors are written so attribution can walk a kept process up to the app that launched it; deeper than
    // this is a loop in a recycled-pid table, not a real tree.
    private const int MaxAncestors = 24;

    private readonly IProcessTable _table;
    private readonly ISystemMetrics _system;
    private readonly int _selfPid;
    private readonly TimeSpan _interval;
    private readonly Func<DateTime> _utcNow;
    private readonly Func<string, string> _redact;
    private readonly Lock _gate = new();
    private System.Threading.Timer? _timer;
    private StreamWriter? _writer;
    private DateTime _startedAt;
    private bool _stopped;

    private volatile SessionTag[] _sessions = [];
    private readonly HashSet<(int, string)> _sessionsWritten = new();

    private readonly Dictionary<(int pid, long start), int> _ids = new();
    private readonly HashSet<int> _written = new();
    private Dictionary<int, (TimeSpan cpu, ulong read, ulong write)> _prev = new();
    private DateTime _prevAt;
    private (ulong idle, ulong kernel, ulong user)? _prevSystem;
    private ProcessorTimes? _prevProcessor;

    public string FilePath { get; }
    public int TickCount { get; private set; }
    public bool IsRecording => _writer is not null && !_stopped;

    /// <summary>Start without the timer, so a test drives every tick through <see cref="SampleOnce"/>.</summary>
    internal bool ManualTicks { get; init; }

    /// <summary>Reads the hooks that apply to the given sessions (user, project and plugin hooks, and the status line).
    /// Called on the sampling thread at the start and whenever a session in a new project appears; its hooks are
    /// written into the recording so they still explain the launches after the configuration changes.</summary>
    public Func<IReadOnlyList<SessionTag>, HookCatalog>? HookSource { get; init; }

    private readonly HashSet<(string, string, string, string)> _hooksWritten = new();
    private readonly HashSet<string> _hookScopes = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Raised on the sampling thread for every record, just after it is written.</summary>
    public event Action<ResourceRecord>? RecordWritten;

    /// <summary>Raised once, on whichever thread stopped the recording, with the reason.</summary>
    public event Action<string>? Stopped;

    public ResourceRecorder(IProcessTable table, ISystemMetrics system, string filePath, int selfPid,
        TimeSpan? interval = null, Func<DateTime>? utcNow = null, Func<string, string>? redact = null)
    {
        _table = table;
        _system = system;
        FilePath = filePath;
        _selfPid = selfPid;
        _interval = interval ?? Interval;
        _utcNow = utcNow ?? (() => DateTime.UtcNow);
        _redact = redact ?? CommandLineRedactor.Redact;
    }

    /// <summary>The live sessions, from each monitor scan. Read on the sampling thread.</summary>
    public void SetSessions(IEnumerable<SessionTag> sessions) => _sessions = sessions.ToArray();

    /// <summary>Opens the file, writes the header and starts sampling. The first tick only primes the deltas, so
    /// the first sample lands one interval later.</summary>
    public void Start()
    {
        lock (_gate)
        {
            if (_writer is not null || _stopped) return;
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            var stream = new FileStream(FilePath, FileMode.CreateNew, FileAccess.Write, FileShare.Read | FileShare.Delete);
            _writer = new StreamWriter(stream, new System.Text.UTF8Encoding(false));
            _startedAt = _utcNow();
            var (_, totalRam) = _system.ReadMemory();
            Emit(new ResourceHeader(ResourceHeader.CurrentVersion, _startedAt, (int)_interval.TotalMilliseconds,
                Environment.ProcessorCount, totalRam, _selfPid));
            WriteHooks(force: true);
            _writer.Flush();
            if (!ManualTicks)
                _timer = new System.Threading.Timer(_ => SampleOnce(), null, TimeSpan.Zero, _interval);
        }
    }

    public void Stop(string reason = "stopped")
    {
        string? stoppedReason = null;
        lock (_gate)
        {
            if (_stopped || _writer is null) return;
            StopCore(reason);
            stoppedReason = reason;
        }
        Stopped?.Invoke(stoppedReason);
    }

    private void StopCore(string reason)
    {
        _stopped = true;
        _timer?.Change(Timeout.Infinite, Timeout.Infinite);
        try
        {
            Emit(new ResourceEnd(_utcNow(), reason));
            _writer?.Flush();
        }
        catch { }
        try { _writer?.Dispose(); } catch { }
        _writer = null;
    }

    /// <summary>One sampling pass. Driven by the timer; tests call it directly.</summary>
    internal void SampleOnce()
    {
        string? autoStopped = null;
        lock (_gate)
        {
            if (_stopped || _writer is null) return;
            try
            {
                Sample();
                if (_utcNow() - _startedAt >= MaxDuration)
                {
                    StopCore("time limit");
                    autoStopped = "time limit";
                }
            }
            catch
            {
                // A vanished process or a failed write skips the tick; the next one recovers.
            }
        }
        if (autoStopped is not null) Stopped?.Invoke(autoStopped);
    }

    private readonly record struct Live(ProcessEntry Entry, int Id, ProcessCounters Counters, double Cpu, long Memory,
        long ReadPerSec, long WritePerSec);

    private void Sample()
    {
        var now = _utcNow();
        bool priming = _prevAt == default;
        double wallSeconds = priming ? 0 : (now - _prevAt).TotalSeconds;
        int cores = Math.Max(1, Environment.ProcessorCount);

        double systemCpu = 0;
        if (_system.ReadCpuTimes() is { } times)
        {
            if (_prevSystem is { } prev)
                systemCpu = MetricsMath.SystemCpuPercent(times.idle - prev.idle, times.kernel - prev.kernel, times.user - prev.user);
            _prevSystem = times;
        }
        var (ramUsed, _) = _system.ReadMemory();
        var (commitUsed, commitLimit) = _table.ReadCommit();
        var kernel = _table.ReadKernelMemory();

        double interruptCpu = 0;
        if (_table.ReadProcessorTimes() is { } pt)
        {
            if (_prevProcessor is { } was && pt.Total > was.Total && pt.InterruptAndDpc >= was.InterruptAndDpc)
                interruptCpu = Math.Clamp(100.0 * (pt.InterruptAndDpc - was.InterruptAndDpc) / (pt.Total - was.Total), 0, 100);
            _prevProcessor = pt;
        }

        _tickServices = null;
        var snapshot = _table.Snapshot();
        var byPid = new Dictionary<int, ProcessEntry>(snapshot.Count);
        var countersByPid = new Dictionary<int, ProcessCounters>(snapshot.Count);
        foreach (var s in snapshot)
        {
            if (s.Entry.Pid == 0) continue;
            byPid[s.Entry.Pid] = s.Entry;
            if (s.Counters is { } sc) countersByPid[s.Entry.Pid] = sc;
        }

        var live = new List<Live>(byPid.Count);
        var liveByPid = new Dictionary<int, Live>(byPid.Count);
        var current = new Dictionary<int, (TimeSpan, ulong, ulong)>(byPid.Count);
        foreach (var entry in byPid.Values)
        {
            if (!countersByPid.TryGetValue(entry.Pid, out var c)) continue;
            int id = IdFor(entry.Pid, c.StartUtc);
            current[id] = (c.Cpu, c.IoReadBytes, c.IoWriteBytes);

            double cpu = 0;
            long readRate = 0, writeRate = 0;
            if (!priming && wallSeconds > 0)
            {
                // A process that started since the last tick spent all its CPU and I/O inside this interval.
                (TimeSpan, ulong, ulong)? was = null;
                if (_prev.TryGetValue(id, out var p)) was = p;
                else if (c.StartUtc is { } s && s >= _prevAt) was = (TimeSpan.Zero, 0UL, 0UL);
                if (was is { } w)
                {
                    var (wasCpu, wasRead, wasWrite) = w;
                    if (c.Cpu >= wasCpu)
                        cpu = MetricsMath.ProcessCpuPercent(c.Cpu - wasCpu, TimeSpan.FromSeconds(wallSeconds), cores);
                    if (c.IoReadBytes >= wasRead) readRate = (long)((c.IoReadBytes - wasRead) / wallSeconds);
                    if (c.IoWriteBytes >= wasWrite) writeRate = (long)((c.IoWriteBytes - wasWrite) / wallSeconds);
                }
            }
            long memory = c.PrivateWorkingSet > 0 ? c.PrivateWorkingSet : c.WorkingSet;
            var l = new Live(entry, id, c, cpu, memory, readRate, writeRate);
            live.Add(l);
            liveByPid[entry.Pid] = l;
        }

        // Processes born since the last tick that are still alive now: a lower bound on launches, since anything
        // that started and exited in between is never seen. The analysis correlates it with the CPU no process owns.
        int fresh = 0;
        foreach (var id in current.Keys)
            if (!_prev.ContainsKey(id)) fresh++;

        _prev = current;
        _prevAt = now;
        if (priming) return;

        var keep = KeepSet(live, byPid);

        foreach (var s in _sessions)
        {
            if (!liveByPid.ContainsKey(s.Pid)) continue;
            if (_sessionsWritten.Add((s.Pid, s.SessionId)))
                Emit(new ResourceSession(s.Pid, s.SessionId, s.Label, s.Cwd is { Length: > 0 } cwd ? _redact(cwd) : null));
        }
        WriteHooks(force: false);

        var samples = new List<ProcSample>(keep.Count);
        double restCpu = 0;
        long restMemory = 0;
        int restCount = 0;
        foreach (var l in live)
        {
            if (keep.Contains(l.Entry.Pid))
            {
                EnsureWritten(l, byPid, liveByPid);
                samples.Add(new ProcSample(l.Id, l.Cpu, l.Memory, l.Counters.Commit, l.ReadPerSec, l.WritePerSec,
                    l.Counters.WorkingSet));
            }
            else
            {
                restCpu += l.Cpu;
                restMemory += l.Memory;
                restCount++;
            }
        }

        Emit(new ResourceTick(now, systemCpu, ramUsed, commitUsed, commitLimit, samples,
            new RestSample(restCpu, restMemory, restCount), kernel, interruptCpu, fresh));
        _writer!.Flush();
        TickCount++;
    }

    private HashSet<int> KeepSet(List<Live> live, Dictionary<int, ProcessEntry> byPid)
    {
        var parents = new Dictionary<int, int>(byPid.Count);
        foreach (var e in byPid.Values) parents[e.Pid] = e.ParentPid;

        var keep = new HashSet<int>();
        foreach (var s in _sessions)
            if (byPid.ContainsKey(s.Pid))
                keep.UnionWith(ProcessTree.SelfAndDescendants(s.Pid, parents));
        if (byPid.ContainsKey(_selfPid))
            keep.UnionWith(ProcessTree.SelfAndDescendants(_selfPid, parents));

        foreach (var l in live.Where(l => l.Cpu > 0).OrderByDescending(l => l.Cpu).Take(TopN))
            keep.Add(l.Entry.Pid);
        foreach (var l in live.OrderByDescending(l => l.Memory).Take(TopN))
            keep.Add(l.Entry.Pid);
        return keep;
    }

    // Writes the proc record for a kept process the first time, and for each ancestor not yet written, so the
    // analysis can always walk up to whatever launched it.
    private void EnsureWritten(Live l, Dictionary<int, ProcessEntry> byPid, Dictionary<int, Live> liveByPid)
    {
        if (_written.Contains(l.Id)) return;
        WriteProc(l.Id, l.Entry, l.Counters.StartUtc, byPid);

        var childStart = l.Counters.StartUtc;
        int pid = l.Entry.ParentPid;
        var seen = new HashSet<int> { l.Entry.Pid };
        for (int depth = 0; depth < MaxAncestors && pid != 0 && seen.Add(pid); depth++)
        {
            if (!byPid.TryGetValue(pid, out var parent)) break;
            DateTime? start = liveByPid.TryGetValue(pid, out var pl) ? pl.Counters.StartUtc : null;
            // A "parent" that started after its child is a recycled pid: the real parent has gone.
            if (start is { } ps && childStart is { } cs && ps > cs.AddSeconds(1)) break;
            int id = liveByPid.TryGetValue(pid, out var known) ? known.Id : IdFor(pid, start);
            if (_written.Contains(id)) break;
            WriteProc(id, parent, start, byPid);
            childStart = start;
            pid = parent.ParentPid;
        }
    }

    private void WriteProc(int id, ProcessEntry entry, DateTime? start, Dictionary<int, ProcessEntry> byPid)
    {
        _written.Add(id);
        string? cmd = null;
        if (_table.ReadCommandLine(entry.Pid) is { Length: > 0 } raw)
        {
            try { cmd = _redact(raw); } catch { cmd = null; }
        }
        IReadOnlyList<ServiceInfo>? services = null;
        // Every service process is started by the service manager, so only its children are worth the lookup.
        if (byPid.TryGetValue(entry.ParentPid, out var parent)
            && ProcessFamilies.Stem(parent.Name).Equals("services", StringComparison.OrdinalIgnoreCase))
        {
            try { _tickServices ??= _table.ReadServices(); } catch { _tickServices = new Dictionary<int, IReadOnlyList<ServiceInfo>>(); }
            services = _tickServices.GetValueOrDefault(entry.Pid);
        }
        Emit(new ResourceProc(id, entry.Pid, entry.ParentPid, entry.Name, start, cmd, services));
    }

    // The service table, read at most once a tick and only when a service process is written.
    private IReadOnlyDictionary<int, IReadOnlyList<ServiceInfo>>? _tickServices;

    // Re-reads the hooks when a session in a project (or config dir) not yet read appears, and writes the new ones.
    private void WriteHooks(bool force)
    {
        if (HookSource is null) return;
        var sessions = _sessions;
        bool fresh = false;
        foreach (var s in sessions)
            fresh |= _hookScopes.Add((s.Cwd ?? "") + "|" + (s.ConfigRoot ?? ""));
        if (!fresh && !force) return;
        HookCatalog catalog;
        try { catalog = HookSource(sessions); }
        catch { return; }
        foreach (var h in catalog.Hooks)
        {
            if (!_hooksWritten.Add((h.Source, h.Event, h.Matcher, h.Command))) continue;
            Emit(new ResourceHook(h with { File = _redact(h.File) }));
        }
    }

    private int IdFor(int pid, DateTime? start)
    {
        var key = (pid, start?.Ticks ?? 0);
        if (!_ids.TryGetValue(key, out var id))
            _ids[key] = id = _ids.Count + 1;
        return id;
    }

    private void Emit(ResourceRecord record)
    {
        _writer!.WriteLine(ResourceRecordCodec.Write(record));
        try { RecordWritten?.Invoke(record); } catch { }
    }

    public void Dispose()
    {
        Stop("closed");
        lock (_gate)
        {
            _timer?.Dispose();
            _timer = null;
        }
    }
}
