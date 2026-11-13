using System.Text.RegularExpressions;

namespace Perch.Data.Resources;

public enum ResourceGroupKind { Session, Perch, App, Rest, System, Unattributed }

/// <summary>Who a process's resources are charged to: a Claude session's tree, Perch, or an application family.</summary>
public sealed record ResourceGroup(string Key, string Label, ResourceGroupKind Kind);

/// <summary>A process in the breakdown at one tick, with its depth in its group's tree.</summary>
public readonly record struct BreakdownProc(ResourceProc Proc, ProcSample Sample, int Depth);

/// <summary>One group's totals at one tick, with its processes in tree order (empty for Rest / Unattributed).</summary>
public sealed record BreakdownGroup(ResourceGroup Group, double Cpu, long Memory, IReadOnlyList<BreakdownProc> Procs);

public enum FindingKind
{
    CpuPeak, SustainedCpu, MemoryPressure, Runaway, MemoryGrowth, DuplicatedTooling, Orphan, KernelPools, CompressedMemory,
    HookFanOut, StatusLine, DuplicateHook, SubagentFanOut, BuildChurn, ShortLivedProcesses, Unattributed,
}

public enum FindingSeverity { Info, Warning, Critical }

/// <summary>A point of interest: where it is (tick indexes), how bad, and a sentence naming what drove it.
/// <see cref="Advice"/> is what the user could change, from the evidence; <see cref="ActionPath"/> a local file the
/// viewer can open for them (labelled <see cref="ActionLabel"/>).</summary>
public sealed record ResourceFinding(
    FindingKind Kind,
    FindingSeverity Severity,
    int StartTick,
    int EndTick,
    int PeakTick,
    string Title,
    string Detail,
    string? GroupKey = null,
    int? ProcId = null)
{
    public string? Advice { get; init; }
    public string? ActionPath { get; init; }
    public string? ActionLabel { get; init; }

    /// <summary>The launch cause of a hook finding, so the viewer can open that hook's runs.</summary>
    public string? HookCauseKey { get; init; }
}

/// <summary>
/// Turns a <see cref="ResourceRecording"/> into what the viewer shows: each process attributed to a group, the
/// charted groups' CPU and memory per tick, the breakdown at any tick, and the findings
/// (docs/resource-diagnostics-plan.md, "Attribution" and "Findings"). Pure: built once per recording version.
/// </summary>
public sealed partial class ResourceAnalysis
{
    /// <summary>How many groups get their own band in the charts; the rest share "Everything else".</summary>
    public const int ChartedGroups = 7;

    public static readonly ResourceGroup EverythingElse = new("rest", "Everything else", ResourceGroupKind.Rest);
    public static readonly ResourceGroup UnattributedGroup = new("unattributed", "Unattributed", ResourceGroupKind.Unattributed);
    public static readonly ResourceGroup PerchGroup = new("perch", "Perch", ResourceGroupKind.Perch);

    // The OS's own memory and CPU, from the tick's kernel figures rather than any process.
    public static readonly ResourceGroup PagedPool = new("sys:paged", "Kernel paged pool", ResourceGroupKind.System);
    public static readonly ResourceGroup NonPagedPool = new("sys:nonpaged", "Kernel non-paged pool & drivers", ResourceGroupKind.System);
    public static readonly ResourceGroup FileCache = new("sys:cache", "System file cache", ResourceGroupKind.System);
    public static readonly ResourceGroup ModifiedPages = new("sys:modified", "Modified pages (being written out)", ResourceGroupKind.System);
    public static readonly ResourceGroup Interrupts = new("sys:irq", "Interrupts & DPCs", ResourceGroupKind.System);

    private static readonly ResourceGroup[] SystemGroups = [PagedPool, NonPagedPool, FileCache, ModifiedPages, Interrupts];

    // Memory (bytes) and CPU (%) of one system group in one tick.
    private static (long memory, double cpu) SystemValue(ResourceGroup g, ResourceTick t) => g.Key switch
    {
        "sys:paged" => (t.Kernel?.PagedPoolResident ?? 0, 0),
        "sys:nonpaged" => ((t.Kernel?.NonPagedPool ?? 0) + (t.Kernel?.DriverResident ?? 0), 0),
        "sys:cache" => (t.Kernel?.SystemCacheResident ?? 0, 0),
        "sys:modified" => (t.Kernel?.ModifiedList ?? 0, 0),
        "sys:irq" => (0, t.InterruptCpu),
        _ => (0, 0),
    };

    private readonly ResourceRecording _recording;
    private readonly Dictionary<int, ResourceGroup> _groupOfProc = new();
    private readonly Dictionary<string, int> _chartIndex = new();

    public ResourceRecording Recording => _recording;
    public int TickCount => _recording.Ticks.Count;
    public TimeSpan TickInterval { get; }

    /// <summary>Every group seen, most significant first.</summary>
    public IReadOnlyList<ResourceGroup> Groups { get; }

    /// <summary>The chart bands, bottom to top: the top groups, then Everything else, then Unattributed.</summary>
    public IReadOnlyList<ResourceGroup> Charted { get; }

    /// <summary>[tick][band] CPU % of the machine, bands as <see cref="Charted"/>.</summary>
    public IReadOnlyList<double[]> Cpu { get; }

    /// <summary>[tick][band] memory in bytes, bands as <see cref="Charted"/>.</summary>
    public IReadOnlyList<long[]> Memory { get; }

    public IReadOnlyList<ResourceFinding> Findings { get; }

    private ResourceAnalysis(ResourceRecording recording, ResourceContext context)
    {
        _recording = recording;
        _context = context;
        TickInterval = TimeSpan.FromMilliseconds(recording.Header is { IntervalMs: > 0 } h ? h.IntervalMs : 5000);

        foreach (var proc in recording.Procs.Values)
            _groupOfProc[proc.Id] = Attribute(proc);

        Groups = RankGroups();
        var charted = Groups.Take(ChartedGroups).ToList();
        charted.Add(EverythingElse);
        // Only the system bands this recording has figures for: an older recording has none.
        bool hasKernel = recording.Ticks.Any(t => t.Kernel is not null);
        bool hasInterrupts = recording.Ticks.Any(t => t.InterruptCpu > 0);
        charted.AddRange(SystemGroups.Where(g => g == Interrupts ? hasInterrupts : hasKernel));
        charted.Add(UnattributedGroup);
        Charted = charted;
        for (int i = 0; i < charted.Count; i++) _chartIndex[charted[i].Key] = i;

        (Cpu, Memory) = BuildSeries();
        BuildCauses();
        BuildActivity();
        Findings = FindAll();
    }

    public static ResourceAnalysis Build(ResourceRecording recording, ResourceContext? context = null) =>
        new(recording, context ?? ResourceContext.None);

    private readonly ResourceContext _context;

    public ResourceGroup GroupOf(int procId) =>
        _groupOfProc.TryGetValue(procId, out var g) ? g : new ResourceGroup("app:?", "Unknown", ResourceGroupKind.App);

    public int ChartIndexOf(string groupKey) =>
        _chartIndex.TryGetValue(groupKey, out var i) ? i : _chartIndex[EverythingElse.Key];

    // ── Attribution ─────────────────────────────────────────────────────────────

    private HashSet<int>? _sessionPids;

    private ResourceGroup Attribute(ResourceProc proc)
    {
        var sessionPids = _sessionPids ??= _recording.Sessions.Select(s => s.Pid).ToHashSet();
        int selfPid = _recording.Header?.SelfPid ?? -1;

        var cur = proc;
        for (int depth = 0; cur is not null && depth < 32; depth++)
        {
            if (sessionPids.Contains(cur.Pid)) return SessionGroup(cur.Pid);
            if (cur.Pid == selfPid) return PerchGroup;
            cur = Parent(cur);
        }
        return AppGroup(FamilyOf(proc));
    }

    private ResourceProc? Parent(ResourceProc proc) =>
        proc.ParentPid == proc.Pid || proc.ParentPid == 0 ? null : _recording.ProcByPid(proc.ParentPid, proc.StartUtc);

    private string FamilyOf(ResourceProc proc)
    {
        if (!ProcessFamilies.IsGenericHost(proc.Name) && ProcessFamilies.Family(proc.Name) is { } own)
            return own;

        var cur = Parent(proc);
        for (int depth = 0; cur is not null && depth < 32; depth++)
        {
            if (ProcessFamilies.IsStopPoint(cur.Name)) break;
            if (!ProcessFamilies.IsGenericHost(cur.Name) && ProcessFamilies.Family(cur.Name) is { } inherited)
                return inherited;
            cur = Parent(cur);
        }
        return ProcessFamilies.Stem(proc.Name) is { Length: > 0 } stem ? stem : "Unknown";
    }

    private ResourceGroup SessionGroup(int pid)
    {
        var label = _recording.Sessions.FirstOrDefault(s => s.Pid == pid)?.Label;
        return new ResourceGroup("session:" + pid, "Session · " + (string.IsNullOrWhiteSpace(label) ? pid.ToString() : label),
            ResourceGroupKind.Session);
    }

    private static ResourceGroup AppGroup(string family) =>
        family == "Perch" ? new ResourceGroup("app:Perch", "Perch (other copy)", ResourceGroupKind.App)
            : new ResourceGroup("app:" + family, family, ResourceGroupKind.App);

    // Ranks by the larger of each group's peak CPU share and peak memory share, plus the same for its averages,
    // so a group that pins the CPU briefly and one that sits on RAM all along both make the chart.
    private List<ResourceGroup> RankGroups()
    {
        var stats = new Dictionary<string, (ResourceGroup g, double peakCpu, double sumCpu, long peakMem, double sumMem)>();
        foreach (var tick in _recording.Ticks)
        {
            var cpu = new Dictionary<string, double>();
            var mem = new Dictionary<string, long>();
            var groups = new Dictionary<string, ResourceGroup>();
            foreach (var s in tick.Procs)
            {
                var g = GroupOf(s.Id);
                groups[g.Key] = g;
                cpu[g.Key] = cpu.GetValueOrDefault(g.Key) + s.Cpu;
                mem[g.Key] = mem.GetValueOrDefault(g.Key) + s.Memory;
            }
            foreach (var (key, g) in groups)
            {
                (ResourceGroup g, double peakCpu, double sumCpu, long peakMem, double sumMem) st =
                    stats.TryGetValue(key, out var x) ? x : (g, 0, 0, 0, 0);
                stats[key] = (g, Math.Max(st.peakCpu, cpu[key]), st.sumCpu + cpu[key], Math.Max(st.peakMem, mem[key]), st.sumMem + mem[key]);
            }
        }

        int n = Math.Max(1, _recording.Ticks.Count);
        double ram = Math.Max(1, _recording.TotalRam);
        return stats.Values
            .OrderByDescending(s => Math.Max(s.peakCpu / 100.0, s.peakMem / ram) + Math.Max(s.sumCpu / n / 100.0, s.sumMem / n / ram))
            .ThenBy(s => s.g.Label, StringComparer.OrdinalIgnoreCase)
            .Select(s => s.g)
            .ToList();
    }

    private (List<double[]>, List<long[]>) BuildSeries()
    {
        int bands = Charted.Count;
        int rest = _chartIndex[EverythingElse.Key];
        int unattributed = _chartIndex[UnattributedGroup.Key];
        var cpu = new List<double[]>(_recording.Ticks.Count);
        var mem = new List<long[]>(_recording.Ticks.Count);
        foreach (var tick in _recording.Ticks)
        {
            var c = new double[bands];
            var m = new long[bands];
            foreach (var s in tick.Procs)
            {
                int band = ChartIndexOf(GroupOf(s.Id).Key);
                c[band] += s.Cpu;
                m[band] += s.Memory;
            }
            c[rest] += tick.Rest.Cpu;
            m[rest] += tick.Rest.Memory;
            foreach (var g in SystemGroups)
            {
                if (!_chartIndex.TryGetValue(g.Key, out int band)) continue;
                var (memory, cpuShare) = SystemValue(g, tick);
                m[band] = memory;
                c[band] = cpuShare;
            }
            c[unattributed] = Math.Max(0, tick.Cpu - c.Sum());
            m[unattributed] = Math.Max(0, tick.RamUsed - m.Sum());
            cpu.Add(c);
            mem.Add(m);
        }
        return (cpu, mem);
    }

    // ── Breakdown ───────────────────────────────────────────────────────────────

    /// <summary>Every group's totals at <paramref name="tick"/>, largest first by CPU or memory, each with its
    /// processes as a tree. Rest and Unattributed come last.</summary>
    public IReadOnlyList<BreakdownGroup> BreakdownAt(int tick, bool byMemory)
    {
        if (tick < 0 || tick >= _recording.Ticks.Count) return [];
        var t = _recording.Ticks[tick];

        var byGroup = new Dictionary<string, (ResourceGroup g, List<ProcSample> samples)>();
        foreach (var s in t.Procs)
        {
            var g = GroupOf(s.Id);
            if (!byGroup.TryGetValue(g.Key, out var entry))
                byGroup[g.Key] = entry = (g, new List<ProcSample>());
            entry.samples.Add(s);
        }

        var result = byGroup.Values
            .Select(e => new BreakdownGroup(e.g, e.samples.Sum(s => s.Cpu), e.samples.Sum(s => s.Memory), TreeOrder(e.samples)))
            .OrderByDescending(b => byMemory ? b.Memory : b.Cpu)
            .ThenByDescending(b => byMemory ? b.Cpu : b.Memory)
            .ToList();

        int unattributed = _chartIndex[UnattributedGroup.Key];
        result.Add(new BreakdownGroup(EverythingElse with { Label = $"Everything else ({t.Rest.Count} processes)" },
            t.Rest.Cpu, t.Rest.Memory, []));
        foreach (var g in SystemGroups)
        {
            if (!_chartIndex.TryGetValue(g.Key, out int band)) continue;
            result.Add(new BreakdownGroup(g, Cpu[tick][band], Memory[tick][band], []));
        }
        result.Add(new BreakdownGroup(UnattributedGroup, Cpu[tick][unattributed], Memory[tick][unattributed], []));
        return result;
    }

    private List<BreakdownProc> TreeOrder(List<ProcSample> samples)
    {
        var procs = samples.Where(s => _recording.Procs.ContainsKey(s.Id))
            .Select(s => (proc: _recording.Procs[s.Id], sample: s)).ToList();
        var ids = procs.Select(p => p.proc.Id).ToHashSet();
        var children = new Dictionary<int, List<(ResourceProc proc, ProcSample sample)>>();
        var roots = new List<(ResourceProc proc, ProcSample sample)>();
        foreach (var p in procs)
        {
            var parent = Parent(p.proc);
            if (parent is not null && ids.Contains(parent.Id))
            {
                if (!children.TryGetValue(parent.Id, out var list)) children[parent.Id] = list = new();
                list.Add(p);
            }
            else roots.Add(p);
        }

        var ordered = new List<BreakdownProc>(procs.Count);
        var seen = new HashSet<int>();
        void Visit((ResourceProc proc, ProcSample sample) node, int depth)
        {
            if (!seen.Add(node.proc.Id)) return;
            ordered.Add(new BreakdownProc(node.proc, node.sample, depth));
            if (!children.TryGetValue(node.proc.Id, out var kids)) return;
            foreach (var kid in kids.OrderByDescending(k => k.sample.Cpu + k.sample.Memory / 1e9))
                Visit(kid, depth + 1);
        }
        foreach (var root in roots.OrderByDescending(r => SubtreeWeight(r.proc.Id, r.sample, children)))
            Visit(root, 0);
        return ordered;
    }

    private static double SubtreeWeight(int id, ProcSample sample,
        Dictionary<int, List<(ResourceProc proc, ProcSample sample)>> children)
    {
        double w = sample.Cpu + sample.Memory / 1e9;
        if (children.TryGetValue(id, out var kids))
            foreach (var k in kids) w += SubtreeWeight(k.proc.Id, k.sample, children);
        return w;
    }

    // ── Findings ────────────────────────────────────────────────────────────────

    private int TicksFor(TimeSpan span) => Math.Max(1, (int)Math.Ceiling(span / TickInterval));

    private List<ResourceFinding> FindAll()
    {
        var findings = new List<ResourceFinding>();
        if (_recording.Ticks.Count == 0) return findings;

        var sustained = SustainedCpu();
        findings.AddRange(sustained);
        findings.AddRange(CpuPeaks(sustained));
        findings.AddRange(MemoryPressure());
        findings.AddRange(Runaways());
        findings.AddRange(MemoryGrowth());
        findings.AddRange(DuplicatedTooling());
        findings.AddRange(Orphans());
        findings.AddRange(KernelPools());
        findings.AddRange(CompressedMemory());
        findings.AddRange(CauseFindings());
        findings.AddRange(SubagentFanOut());
        findings.AddRange(ShortLivedProcesses());
        findings.AddRange(UnattributedMemory());

        return findings
            .OrderByDescending(f => f.Severity)
            .ThenBy(f => f.Kind)
            .ThenBy(f => f.StartTick)
            .ToList();
    }

    private IEnumerable<(int start, int end)> Runs(Func<int, bool> condition, int minTicks)
    {
        int n = _recording.Ticks.Count, start = -1;
        for (int i = 0; i <= n; i++)
        {
            bool on = i < n && condition(i);
            if (on && start < 0) start = i;
            else if (!on && start >= 0)
            {
                if (i - start >= minTicks) yield return (start, i - 1);
                start = -1;
            }
        }
    }

    private int PeakOf(int start, int end, Func<int, double> value)
    {
        int best = start;
        for (int i = start; i <= end; i++)
            if (value(i) > value(best)) best = i;
        return best;
    }

    // Each band's average over a tick range, largest first.
    private List<(int band, double avg)> BandAverages(int start, int end, bool memory)
    {
        var sum = new double[Charted.Count];
        for (int i = start; i <= end; i++)
            for (int b = 0; b < sum.Length; b++) sum[b] += memory ? Memory[i][b] : Cpu[i][b];
        int span = end - start + 1;
        return Enumerable.Range(0, sum.Length).Select(b => (b, sum[b] / span)).OrderByDescending(x => x.Item2).ToList();
    }

    // The top bands over a tick range as "Docker / WSL 41%, Chrome 22%".
    private string TopCpu(int start, int end, int count = 3) =>
        string.Join(", ", BandAverages(start, end, memory: false).Where(x => x.avg >= 1).Take(count)
            .Select(x => $"{Charted[x.band].Label} {x.avg:0}%"));

    private string TopMemory(int start, int end, int count = 3) =>
        string.Join(", ", BandAverages(start, end, memory: true).Where(x => x.avg >= 64L * 1024 * 1024).Take(count)
            .Select(x => $"{Charted[x.band].Label} {ResourceFormat.Bytes((long)x.avg)}"));

    // The group a machine-wide finding should open: its biggest real contributor, never the catch-all bands.
    private string? TopGroupKey(int start, int end, bool memory) =>
        BandAverages(start, end, memory)
            .Select(x => Charted[x.band])
            .FirstOrDefault(g => g.Kind is not (ResourceGroupKind.Rest or ResourceGroupKind.System or ResourceGroupKind.Unattributed))?.Key;

    private List<ResourceFinding> SustainedCpu()
    {
        var list = new List<ResourceFinding>();
        foreach (var (start, end) in Runs(i => _recording.Ticks[i].Cpu >= 85, TicksFor(TimeSpan.FromSeconds(30))))
        {
            var length = TickInterval * (end - start + 1);
            int peak = PeakOf(start, end, i => _recording.Ticks[i].Cpu);
            list.Add(new ResourceFinding(FindingKind.SustainedCpu,
                length >= TimeSpan.FromMinutes(2) ? FindingSeverity.Critical : FindingSeverity.Warning,
                start, end, peak,
                $"CPU saturated for {ResourceFormat.Duration(length)}",
                $"Above 85% the whole time, peaking at {_recording.Ticks[peak].Cpu:0}%. Mostly: {TopCpu(start, end)}.",
                TopGroupKey(start, end, memory: false))
            {
                Advice = "Open the top group at the peak. For a session, its causes say whether it's Claude's own work, a build, " +
                         "or hooks firing on every tool call; for an app, pausing or closing it frees the CPU.",
            });
        }
        return list;
    }

    private List<ResourceFinding> CpuPeaks(List<ResourceFinding> sustained)
    {
        var ticks = _recording.Ticks;
        int gap = TicksFor(TimeSpan.FromSeconds(60));
        var candidates = Enumerable.Range(0, ticks.Count)
            .Where(i => ticks[i].Cpu >= 70
                        && (i == 0 || ticks[i].Cpu >= ticks[i - 1].Cpu)
                        && (i == ticks.Count - 1 || ticks[i].Cpu >= ticks[i + 1].Cpu)
                        && !sustained.Any(s => i >= s.StartTick && i <= s.EndTick))
            .OrderByDescending(i => ticks[i].Cpu)
            .ToList();

        var picked = new List<int>();
        foreach (var i in candidates)
        {
            if (picked.Count == 3) break;
            if (picked.All(p => Math.Abs(p - i) >= gap)) picked.Add(i);
        }

        return picked.Select(i => new ResourceFinding(FindingKind.CpuPeak,
            ticks[i].Cpu >= 90 ? FindingSeverity.Warning : FindingSeverity.Info,
            i, i, i,
            $"CPU peak {ticks[i].Cpu:0}%",
            $"Top: {TopCpu(i, i)}.",
            TopGroupKey(i, i, memory: false))).ToList();
    }

    private List<ResourceFinding> MemoryPressure()
    {
        long total = _recording.TotalRam;
        bool Pressured(int i)
        {
            var t = _recording.Ticks[i];
            return (total > 0 && t.RamUsed >= 0.9 * total) || (t.CommitLimit > 0 && t.CommitUsed >= 0.9 * t.CommitLimit);
        }

        var list = new List<ResourceFinding>();
        foreach (var (start, end) in Runs(Pressured, TicksFor(TimeSpan.FromSeconds(15))))
        {
            int peak = PeakOf(start, end, i => _recording.Ticks[i].RamUsed);
            var t = _recording.Ticks[peak];
            bool commit = t.CommitLimit > 0 && t.CommitUsed >= 0.9 * t.CommitLimit;
            string what = commit
                ? $"commit at {ResourceFormat.Bytes(t.CommitUsed)} of {ResourceFormat.Bytes(t.CommitLimit)}"
                : $"RAM at {ResourceFormat.Bytes(t.RamUsed)} of {ResourceFormat.Bytes(total)}";
            list.Add(new ResourceFinding(FindingKind.MemoryPressure, FindingSeverity.Critical, start, end, peak,
                $"Memory pressure for {ResourceFormat.Duration(TickInterval * (end - start + 1))}",
                $"Peaked with {what}; the OS starts paging to disk here and everything slows. Mostly: {TopMemory(start, end)}.",
                TopGroupKey(start, end, memory: true))
            {
                Advice = "Close or restart the largest groups listed. Idle Claude sessions still hold their runtime and MCP " +
                         "servers; ending ones you're done with frees them.",
            });
        }
        return list;
    }

    // Per-process sample series, in tick order.
    private Dictionary<int, List<(int tick, ProcSample sample)>> SeriesByProc()
    {
        var series = new Dictionary<int, List<(int, ProcSample)>>();
        for (int i = 0; i < _recording.Ticks.Count; i++)
            foreach (var s in _recording.Ticks[i].Procs)
            {
                if (!series.TryGetValue(s.Id, out var list)) series[s.Id] = list = new();
                list.Add((i, s));
            }
        return series;
    }

    private string ProcLabel(int id) =>
        _recording.Procs.TryGetValue(id, out var p) ? $"{HostLabel(p) ?? ProcessFamilies.Stem(p.Name)} ({p.Pid})" : $"process {id}";

    // A service host's exe names nothing ("svchost"), so it goes by the services it runs.
    private static string? HostLabel(ResourceProc p) =>
        p.ServiceLabel is { } services && ProcessFamilies.Stem(p.Name).Equals("svchost", StringComparison.OrdinalIgnoreCase)
            ? $"svchost · {services}"
            : null;

    private List<ResourceFinding> Runaways()
    {
        int cores = Math.Max(1, _recording.Cores);
        double oneCore = 100.0 / cores;
        double threshold = Math.Min(25, 0.9 * oneCore);
        int minTicks = TicksFor(TimeSpan.FromSeconds(60));

        var found = new List<(ResourceFinding finding, double weight)>();
        foreach (var (id, series) in SeriesByProc())
        {
            foreach (var (start, end, peak, avg) in BusyRuns(series, threshold, minTicks))
            {
                var length = TickInterval * (end - start + 1);
                var group = GroupOf(id);
                // One core of many is worth knowing about; two or more, or a quarter of the machine, is a problem.
                var severity = avg >= 2 * oneCore || avg >= 25 ? FindingSeverity.Warning : FindingSeverity.Info;
                found.Add((new ResourceFinding(FindingKind.Runaway, severity, start, end, peak,
                    $"{ProcLabel(id)} busy for {ResourceFormat.Duration(length)}",
                    $"Averaged {avg / oneCore:0.0} cores ({avg:0}% of the machine). Belongs to {group.Label}.",
                    group.Key, id)
                {
                    Advice = RunawayAdvice(id, group),
                }, avg * (end - start + 1)));
            }
        }
        return found.OrderByDescending(f => f.weight).Take(5).Select(f => f.finding).ToList();
    }

    /// <summary>
    /// The runs of consecutive ticks where a process's CPU is at least <paramref name="threshold"/>, at least
    /// <paramref name="minTicks"/> long, with each run's first highest tick and its average. Walks the process's own
    /// samples (a tick it's absent from counts as idle, ending a run), not every tick of the recording for every
    /// process: that was O(processes × ticks) on each rebuild, about 5 s apart for a 4-hour recording (perf review PF16).
    /// </summary>
    internal static IEnumerable<(int Start, int End, int Peak, double Avg)> BusyRuns(
        IReadOnlyList<(int tick, ProcSample sample)> series, double threshold, int minTicks)
    {
        int start = -1, prev = -1, peak = -1;
        double sum = 0, peakCpu = double.NegativeInfinity;
        foreach (var (tick, sample) in series)
        {
            bool on = sample.Cpu >= threshold;
            if (start >= 0 && (!on || tick != prev + 1))
            {
                if (prev - start + 1 >= minTicks) yield return (start, prev, peak, sum / (prev - start + 1));
                start = -1;
            }
            if (!on) continue;
            if (start < 0) { start = tick; sum = 0; peakCpu = double.NegativeInfinity; }
            sum += sample.Cpu;
            if (sample.Cpu > peakCpu) { peakCpu = sample.Cpu; peak = tick; }
            prev = tick;
        }
        if (start >= 0 && prev - start + 1 >= minTicks) yield return (start, prev, peak, sum / (prev - start + 1));
    }

    // Never "end it" for the kernel, Windows services or security agents: those can't (or mustn't) be ended, and
    // their load is a symptom of what other programs ask of them.
    private string RunawayAdvice(int id, ResourceGroup group)
    {
        var proc = _recording.Procs.GetValueOrDefault(id);
        string stem = ProcessFamilies.Stem(proc?.Name ?? "").ToLowerInvariant();
        if (group.Kind == ResourceGroupKind.Session)
            return "Work a session started (a build, tests, a search) is busy by design and ends by itself; if it ran long, ask " +
                   "Claude what it was doing. If it shouldn't still be running, end it.";
        if (stem == "system")
            return "System is the Windows kernel and can't be ended. Its CPU is driver work: security scanning of process starts " +
                   "and file writes, network and file-system filters, storage. It tends to follow the launch churn, so the " +
                   "short-lived process and hook findings are where to cut it.";
        if (stem == "wmiprvse")
            return "WMI Provider Host answers queries from other programs (management and monitoring agents, vendor " +
                   "utilities); steady load means something is polling it. Ending it only makes Windows restart it.";
        if (group.Label is "Defender" or "CrowdStrike")
            return "A security agent scans what other programs do, mostly process starts and file writes, so fewer launches " +
                   "means less scanning. Don't stop it; scanning exclusions for build output are a conversation with IT.";
        if (group.Label == ProcessFamilies.Windows)
            return proc?.Services is { Count: > 0 } services
                ? ServiceAdvice(services)
                : "A Windows service. Task Manager → Details → right-click → Go to service(s) shows which one, and whether it " +
                  "belongs to something you can pause.";
        return $"If you don't expect it to be busy, end it (Task Manager → Details → PID {proc?.Pid}).";
    }

    // What the services that most often keep a svchost busy are doing, and the lever the user actually has.
    private static readonly Dictionary<string, string> KnownServiceAdvice = new(StringComparer.OrdinalIgnoreCase)
    {
        ["wuauserv"] = "Windows Update is checking for, downloading or installing updates. It finishes by itself; " +
                       "Settings → Windows Update → Pause updates defers it to a quieter time.",
        ["UsoSvc"] = "The Update Orchestrator schedules Windows Update scans and installs. It finishes by itself; " +
                     "Settings → Windows Update → Pause updates defers it.",
        ["BITS"] = "Background Intelligent Transfer downloads in the background for Windows Update, Store apps and some " +
                   "installers. It eases off when other programs need the network, and stops when the download completes.",
        ["DoSvc"] = "Delivery Optimization downloads updates and can upload them to other PCs. Settings → Windows Update → " +
                    "Advanced options → Delivery Optimization turns the uploads off or caps them.",
        ["SysMain"] = "SysMain preloads the apps it expects you to open into spare RAM. It's busiest after a restart and " +
                      "settles by itself; it gives the memory back when programs need it.",
        ["DiagTrack"] = "Connected User Experiences and Telemetry collects and uploads diagnostics in bursts. Settings → " +
                        "Privacy & security → Diagnostics & feedback reduces how much it sends.",
        ["Winmgmt"] = "WMI answers queries from management and monitoring agents and vendor utilities; steady load means " +
                      "something is polling it.",
        ["EventLog"] = "Event Log is busy because something is writing a lot of events. Event Viewer shows which log and " +
                       "source is growing.",
        ["CryptSvc"] = "Cryptographic Services checks signatures and certificates, mostly as programs start and install; " +
                       "fewer launches means less of it.",
    };

    private static string ServiceAdvice(IReadOnlyList<Perch.Platform.ServiceInfo> services)
    {
        string names = string.Join(", ", services.Take(4).Select(s => s.DisplayName == s.Name ? s.Name : $"{s.DisplayName} ({s.Name})")) +
                       (services.Count > 4 ? $" and {services.Count - 4} more" : "");
        string runs = $"This svchost runs {names}.";
        foreach (var s in services)
            if (KnownServiceAdvice.TryGetValue(s.Name, out var advice))
                return $"{runs} {advice}";
        return $"{runs} Windows services shouldn't be ended. If it belongs to an app you don't use, Services (services.msc) " +
               "can set it to Manual so it only starts when needed.";
    }

    private List<ResourceFinding> MemoryGrowth()
    {
        const long MinGrowth = 500L * 1024 * 1024;
        int minSamples = TicksFor(TimeSpan.FromSeconds(60));
        var list = new List<ResourceFinding>();
        foreach (var (id, series) in SeriesByProc())
        {
            if (series.Count < minSamples) continue;
            long first = series[0].sample.Memory;
            var max = series.MaxBy(x => x.sample.Memory);
            long growth = max.sample.Memory - first;
            if (growth < MinGrowth || max.sample.Memory < first * 1.5) continue;
            var group = GroupOf(id);
            list.Add(new ResourceFinding(FindingKind.MemoryGrowth, FindingSeverity.Warning,
                series[0].tick, max.tick, max.tick,
                $"{ProcLabel(id)} grew by {ResourceFormat.Bytes(growth)}",
                $"From {ResourceFormat.Bytes(first)} to {ResourceFormat.Bytes(max.sample.Memory)} over " +
                $"{ResourceFormat.Duration(TickInterval * (max.tick - series[0].tick))}. Belongs to {group.Label}.",
                group.Key, id)
            {
                Advice = "Restarting it gives the memory back. Steady growth over hours is usually a leak, worth reporting to " +
                         "whoever maintains it.",
            });
        }
        return list.OrderByDescending(f => f.PeakTick - f.StartTick).Take(5).ToList();
    }

    private static readonly HashSet<string> Transient = new(StringComparer.OrdinalIgnoreCase)
    {
        "claude", "cmd", "powershell", "pwsh", "bash", "sh", "zsh", "conhost", "git", "openconsole",
    };

    // The same tool started by two or more sessions at once: every session paying for its own copy of an MCP
    // server is the usual way N sessions cost N times the memory.
    private List<ResourceFinding> DuplicatedTooling()
    {
        int minTicks = TicksFor(TimeSpan.FromSeconds(15));
        var stats = new Dictionary<string, (int maxSessions, int tick, long memory, int ticks, int sampleId)>();
        for (int i = 0; i < _recording.Ticks.Count; i++)
        {
            var perKey = new Dictionary<string, (HashSet<string> sessions, long memory, int id)>();
            foreach (var s in _recording.Ticks[i].Procs)
            {
                var g = GroupOf(s.Id);
                if (g.Kind != ResourceGroupKind.Session || !_recording.Procs.TryGetValue(s.Id, out var p)) continue;
                if (Transient.Contains(ProcessFamilies.Stem(p.Name))) continue;
                string key = ToolKey(p);
                if (!perKey.TryGetValue(key, out var e)) perKey[key] = e = (new HashSet<string>(), 0, s.Id);
                e.sessions.Add(g.Key);
                perKey[key] = (e.sessions, e.memory + s.Memory, e.id);
            }
            foreach (var (key, e) in perKey)
            {
                if (e.sessions.Count < 2) continue;
                (int maxSessions, int tick, long memory, int ticks, int sampleId) st =
                    stats.TryGetValue(key, out var x) ? x : (0, i, 0, 0, e.id);
                st.ticks++;
                if (e.sessions.Count > st.maxSessions || (e.sessions.Count == st.maxSessions && e.memory > st.memory))
                    st = (e.sessions.Count, i, e.memory, st.ticks, e.id);
                stats[key] = st;
            }
        }

        return stats
            .Where(kv => kv.Value.ticks >= minTicks)
            .OrderByDescending(kv => kv.Value.memory)
            .Take(5)
            .Select(kv => new ResourceFinding(FindingKind.DuplicatedTooling,
                kv.Value.memory >= 500L * 1024 * 1024 ? FindingSeverity.Warning : FindingSeverity.Info,
                kv.Value.tick, kv.Value.tick, kv.Value.tick,
                $"{ToolLabel(_recording.Procs[kv.Value.sampleId])} running ×{kv.Value.maxSessions}, one per session",
                $"{kv.Value.maxSessions} sessions each started their own copy, {ResourceFormat.Bytes(kv.Value.memory)} together.",
                null, kv.Value.sampleId)
            {
                Advice = "Turn the server off in projects that don't use it (its entry in .mcp.json, or /mcp), or run it once as a " +
                         "shared HTTP server the sessions connect to.",
            })
            .ToList();
    }

    [GeneratedRegex(@"\d{4,}")]
    private static partial Regex LongNumber();

    // Ports, pids and temp-dir suffixes differ between copies of the same tool; collapse them.
    private static string ToolKey(ResourceProc p) =>
        LongNumber().Replace((p.CommandLine ?? p.Name).Trim().ToLowerInvariant(), "#");

    /// <summary>A short name for what a process is running: the script or MCP package named in its command line
    /// when there is one, else the exe.</summary>
    public static string ToolLabel(ResourceProc p)
    {
        if (HostLabel(p) is { } host) return host;
        string stem = ProcessFamilies.Stem(p.Name);
        if (p.CommandLine is not { Length: > 0 } cmd) return stem;
        var tokens = cmd.Split(' ', StringSplitOptions.RemoveEmptyEntries).Skip(1).Select(t => t.Trim('"', '\''));
        foreach (var token in tokens)
        {
            if (token.StartsWith('-')) continue;
            if (token.Contains("mcp", StringComparison.OrdinalIgnoreCase) || token.StartsWith('@')
                || token.EndsWith(".js", StringComparison.OrdinalIgnoreCase) || token.EndsWith(".mjs", StringComparison.OrdinalIgnoreCase)
                || token.EndsWith(".cjs", StringComparison.OrdinalIgnoreCase) || token.EndsWith(".py", StringComparison.OrdinalIgnoreCase)
                || token.EndsWith(".ts", StringComparison.OrdinalIgnoreCase))
            {
                var name = token.Replace('\\', '/');
                // A package path keeps its scope/package ("@scope/server-x"); a script path keeps its folder and
                // file ("jira/server.js") since "index.js" alone names nothing.
                int nm = name.LastIndexOf("node_modules/", StringComparison.OrdinalIgnoreCase);
                if (nm >= 0) name = name[(nm + "node_modules/".Length)..];
                var parts = name.Split('/', StringSplitOptions.RemoveEmptyEntries);
                string label = parts.Length switch
                {
                    0 => token,
                    1 => parts[0],
                    _ when parts[0].StartsWith('@') => parts[0] + "/" + parts[1],
                    _ => parts[^2] + "/" + parts[^1],
                };
                return $"{stem} {label}";
            }
        }
        return stem;
    }

    private List<ResourceFinding> Orphans()
    {
        int lastTick = _recording.Ticks.Count - 1;
        int grace = TicksFor(TimeSpan.FromSeconds(30));
        var series = SeriesByProc();

        // When each session's own claude process was last seen.
        var rootLast = new Dictionary<string, int>();
        foreach (var (id, s) in series)
        {
            if (!_recording.Procs.TryGetValue(id, out var p)) continue;
            if (!_recording.Sessions.Any(x => x.Pid == p.Pid)) continue;
            var key = "session:" + p.Pid;
            rootLast[key] = Math.Max(rootLast.GetValueOrDefault(key, -1), s[^1].tick);
        }

        var list = new List<ResourceFinding>();
        foreach (var (id, s) in series)
        {
            var group = GroupOf(id);
            if (group.Kind != ResourceGroupKind.Session) continue;
            if (!_recording.Procs.TryGetValue(id, out var p) || _recording.Sessions.Any(x => x.Pid == p.Pid)) continue;
            if (!rootLast.TryGetValue(group.Key, out var ended) || ended >= lastTick) continue;
            int procLast = s[^1].tick;
            if (procLast - ended < grace) continue;
            var lastSample = s[^1].sample;
            bool stillRunning = procLast == lastTick;
            list.Add(new ResourceFinding(FindingKind.Orphan, FindingSeverity.Warning, ended + 1, procLast, procLast,
                $"{ToolLabel(p)} outlived its session",
                $"{group.Label} ended, but this kept running for {ResourceFormat.Duration(TickInterval * (procLast - ended))}" +
                (stillRunning ? " and was still running when the recording stopped" : "") +
                $", using {ResourceFormat.Bytes(lastSample.Memory)}.",
                group.Key, id)
            {
                Advice = $"Unless you started it on purpose, end it (Task Manager → Details → PID {p.Pid}). Asking Claude to stop " +
                         "background servers before it finishes avoids this.",
            });
        }
        return list.OrderByDescending(f => f.EndTick - f.StartTick).Take(5).ToList();
    }

    // Kernel pools this big are a driver holding memory, which no process view will ever show.
    private List<ResourceFinding> KernelPools()
    {
        var withKernel = Enumerable.Range(0, _recording.Ticks.Count).Where(i => _recording.Ticks[i].Kernel is not null).ToList();
        if (withKernel.Count == 0) return [];
        double paged = withKernel.Average(i => (double)_recording.Ticks[i].Kernel!.Value.PagedPoolResident);
        double nonPaged = withKernel.Average(i => (double)_recording.Ticks[i].Kernel!.Value.NonPagedPool);
        double floor = Math.Max(4.0 * (1L << 30), 0.1 * _recording.TotalRam);
        if (paged + nonPaged < floor && nonPaged < 1.5 * (1L << 30)) return [];

        int peak = PeakOf(withKernel[0], withKernel[^1], i =>
            _recording.Ticks[i].Kernel is { } k ? k.PagedPoolResident + k.NonPagedPool : 0);
        string share = _recording.TotalRam > 0 ? $" ({(paged + nonPaged) / _recording.TotalRam:P0} of RAM)" : "";
        return
        [
            new ResourceFinding(FindingKind.KernelPools, FindingSeverity.Warning, withKernel[0], withKernel[^1], peak,
                $"Kernel pools hold {ResourceFormat.Bytes((long)(paged + nonPaged))}{share}",
                $"Paged pool {ResourceFormat.Bytes((long)paged)}, non-paged {ResourceFormat.Bytes((long)nonPaged)} on average. " +
                "This is memory drivers allocated, not any program, so closing apps won't free it. Pools this large usually " +
                "mean a driver is holding (or leaking) memory.",
                NonPagedPool.Key)
            {
                Advice = "VPN and network filters, security agents, virtualisation and storage drivers are the usual suspects. " +
                         "A reboot resets the pools; if they climb back, pool tags name the driver (poolmon or RAMMap's Pool tab, " +
                         "both need admin), which is what to send to IT or the vendor.",
            },
        ];
    }

    private List<ResourceFinding> CompressedMemory()
    {
        string key = "app:" + ProcessFamilies.CompressedMemory;
        if (!_chartIndex.TryGetValue(key, out int band) || Memory.Count == 0) return [];
        double avg = Memory.Average(m => (double)m[band]);
        if (avg < 1L << 30) return [];
        int peak = PeakOf(0, Memory.Count - 1, i => Memory[i][band]);
        return
        [
            new ResourceFinding(FindingKind.CompressedMemory, FindingSeverity.Info, 0, Memory.Count - 1, peak,
                $"{ResourceFormat.Bytes((long)avg)} of other programs' memory is compressed",
                "Windows compresses pages it would otherwise push to disk. They belong to the programs above, so the real " +
                "footprint of the big consumers is larger than their own figures, and memory is tighter than \"in use\" suggests.",
                key)
            {
                Advice = "The cure is the same as for memory pressure: close or restart the biggest groups, starting with idle ones.",
            },
        ];
    }

    private List<ResourceFinding> UnattributedMemory()
    {
        if (_recording.TotalRam <= 0 || Memory.Count == 0) return [];
        int band = _chartIndex[UnattributedGroup.Key];
        double avg = Memory.Average(m => (double)m[band]);
        if (avg < 0.15 * _recording.TotalRam) return [];
        int peak = PeakOf(0, Memory.Count - 1, i => Memory[i][band]);

        bool hasKernel = _recording.Ticks.Any(t => t.Kernel is not null);
        var p = _recording.Ticks[peak];
        long shared = p.Procs.Sum(s => s.Shared);
        string detail = hasKernel
            ? "Resident memory no process owns privately and the kernel figures don't cover. Mostly pages processes " +
              "share (DLLs, memory-mapped files, shared memory between a browser's processes), which count once here " +
              (shared > 0 ? $"while the processes above map {ResourceFormat.Bytes(shared)} of shared pages between them; " : "; ") +
              "plus page tables and driver-locked memory."
            : "Memory in use that no process accounts for. This recording predates kernel figures, so it also holds the " +
              "kernel pools, file cache and any process Perch couldn't read; record again to split it out.";
        return
        [
            new ResourceFinding(FindingKind.Unattributed, FindingSeverity.Info, 0, Memory.Count - 1, peak,
                $"{ResourceFormat.Bytes((long)avg)} of RAM isn't charged to any process",
                detail, UnattributedGroup.Key)
            {
                Advice = hasKernel
                    ? "Nothing to change directly: it shrinks as the programs sharing those pages close. Splitting it further " +
                      "needs admin rights (RAMMap's Use Counts)."
                    : null,
            },
        ];
    }
}
