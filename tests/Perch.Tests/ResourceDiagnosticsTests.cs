using Perch.Data.Resources;
using Perch.Platform;
using Xunit;

namespace Perch.Tests;

/// <summary>Resource diagnostics (docs/resource-diagnostics-plan.md): the recording format, the command-line
/// redactor, the recorder against a fake process table, and the analysis over synthetic recordings.</summary>
public sealed class ResourceRecordCodecTests
{
    [Fact]
    public void Every_record_kind_round_trips()
    {
        var start = new DateTime(2026, 11, 6, 3, 12, 0, DateTimeKind.Utc);
        ResourceRecord[] records =
        [
            new ResourceHeader(1, start, 5000, 16, 32L << 30, 1234),
            new ResourceProc(7, 4120, 3988, "node.exe", start.AddMinutes(-3), "node server.js --token ***"),
            new ResourceProc(8, 4121, 3988, "conhost.exe", null, null),
            new ResourceProc(9, 1300, 900, "svchost.exe", start.AddDays(-1), null,
                [new ServiceInfo("RpcEptMapper", "RPC Endpoint Mapper"), new ServiceInfo("RpcSs", "Remote Procedure Call (RPC)")]),
            new ResourceSession(3988, "6f1e", "perch"),
            new ResourceTick(start.AddSeconds(5), 71.2, 30L << 30, 40L << 30, 50L << 30,
                [new ProcSample(7, 3.1, 100L << 20, 200L << 20, 1024, 2048, 150L << 20)], new RestSample(4.2, 2L << 30, 311),
                new KernelMemory(5L << 30, 3L << 30, 1L << 30, 40L << 20, 400L << 20), 1.5),
            new ResourceEnd(start.AddHours(1), "stopped"),
        ];

        foreach (var record in records)
        {
            var line = ResourceRecordCodec.Write(record);
            Assert.DoesNotContain('\n', line);
            var back = ResourceRecordCodec.Parse(line);
            if (record is ResourceTick t)
            {
                var bt = Assert.IsType<ResourceTick>(back);
                Assert.Equal(t.AtUtc, bt.AtUtc);
                Assert.Equal(t.Cpu, bt.Cpu);
                Assert.Equal(t.RamUsed, bt.RamUsed);
                Assert.Equal(t.CommitLimit, bt.CommitLimit);
                Assert.Equal(t.Procs, bt.Procs);
                Assert.Equal(t.Rest, bt.Rest);
                Assert.Equal(t.Kernel, bt.Kernel);
                Assert.Equal(t.InterruptCpu, bt.InterruptCpu);
            }
            else if (record is ResourceProc { Services: { } services } p)
            {
                var bp = Assert.IsType<ResourceProc>(back);
                Assert.Equal(services, bp.Services);
                Assert.Equal(p with { Services = null }, bp with { Services = null });
            }
            else
            {
                Assert.Equal(record, back);
            }
        }
    }

    [Fact]
    public void A_first_version_tick_still_parses()
    {
        var tick = Assert.IsType<ResourceTick>(ResourceRecordCodec.Parse(
            "{\"t\":\"tick\",\"at\":\"2026-11-06T01:10:16.919Z\",\"cpu\":60,\"ram\":100,\"commit\":5,\"commitLimit\":9," +
            "\"p\":[[7,3.1,104857600,201326592,0,0]],\"rest\":[1.3,50,211]}"));
        Assert.Equal(0, tick.Procs[0].WorkingSet);
        Assert.Null(tick.Kernel);
        Assert.Equal(0, tick.InterruptCpu);
    }

    [Fact]
    public void A_malformed_service_list_keeps_the_process()
    {
        var p = Assert.IsType<ResourceProc>(ResourceRecordCodec.Parse(
            "{\"t\":\"proc\",\"id\":1,\"pid\":2,\"ppid\":3,\"name\":\"svchost.exe\",\"svc\":[[1,2],\"x\",[\"BITS\",null]]}"));
        Assert.Equal([new ServiceInfo("BITS", "BITS")], p.Services!);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("{\"t\":\"tick\",\"at\":\"2026-11-06T03:12:05Z\",\"cpu\":71.2,\"p\":[[7,3.1")]
    [InlineData("{\"t\":\"mystery\"}")]
    [InlineData("[1,2,3]")]
    public void Malformed_lines_parse_to_null(string line) => Assert.Null(ResourceRecordCodec.Parse(line));
}

public sealed class CommandLineRedactorTests
{
    private const string Profile = @"C:\Users\someone";

    [Theory]
    [InlineData("node server.js --token abc123", "node server.js --token ***")]
    [InlineData("node server.js --api-key=abc123", "node server.js --api-key=***")]
    [InlineData("tool -password \"a b c\" next", "tool -password *** next")]
    [InlineData("tool /auth:secretvalue", "tool /auth:***")]
    [InlineData("env GITHUB_TOKEN=ghp_abc run", "env GITHUB_TOKEN=*** run")]
    [InlineData("git clone https://user:hunter2@example.com/repo.git", "git clone https://***@example.com/repo.git")]
    [InlineData("curl -H Bearer eyJhbGciOi", "curl -H Bearer ***")]
    public void Secrets_are_masked(string input, string expected) =>
        Assert.Equal(expected, CommandLineRedactor.Redact(input, Profile));

    [Fact]
    public void The_profile_directory_becomes_a_tilde() =>
        Assert.Equal(@"node ~\.mcp\jira\server.js",
            CommandLineRedactor.Redact(@"node C:\Users\SOMEONE\.mcp\jira\server.js", Profile));

    [Theory]
    [InlineData(@"""C:/Users/someone/AppData/Local/GitKrakenCLI/gk.exe"" ai hook run", @"""~/AppData/Local/GitKrakenCLI/gk.exe"" ai hook run")]
    [InlineData("bash -c \"source /c/Users/someone/.claude/shell-snapshots/s.sh\"", "bash -c \"source ~/.claude/shell-snapshots/s.sh\"")]
    [InlineData(@"node ""C:\\Users\\someone\\.claude\\perch-statusline.mjs""", @"node ""~\\.claude\\perch-statusline.mjs""")]
    [InlineData(@"C:\Users\someoneelse\x.exe", @"C:\Users\someoneelse\x.exe")]
    public void Every_spelling_of_the_profile_is_masked(string input, string expected) =>
        Assert.Equal(expected, CommandLineRedactor.Redact(input, Profile));

    [Fact]
    public void Long_mixed_runs_are_masked_but_guids_and_paths_are_kept()
    {
        Assert.Equal("tool ***", CommandLineRedactor.Redact("tool abcdef0123456789abcdef0123456789abcd", Profile));
        Assert.Equal("claude --resume 6f1e2a3b-1111-2222-3333-444455556666",
            CommandLineRedactor.Redact("claude --resume 6f1e2a3b-1111-2222-3333-444455556666", Profile));
        Assert.Equal(@"node C:\tools\oauth\server.js", CommandLineRedactor.Redact(@"node C:\tools\oauth\server.js", Profile));
        Assert.Equal("perch render captures/20261106-085017-resource-monitor-render",
            CommandLineRedactor.Redact("perch render captures/20261106-085017-resource-monitor-render", Profile));
        Assert.Equal("gh --x ***", CommandLineRedactor.Redact("gh --x ghp_abcdefghijklmnopqrstuvwxyz0123456789", Profile));
    }

    [Fact]
    public void Long_lines_are_capped()
    {
        var redacted = CommandLineRedactor.Redact("x " + new string('a', 5000), Profile);
        Assert.True(redacted.Length <= CommandLineRedactor.MaxLength + 1);
    }
}

public sealed class ResourceRecorderTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "perch-tests", "resources-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private sealed class FakeTable : IProcessTable
    {
        public readonly Dictionary<int, (ProcessEntry entry, ProcessCounters counters)> Procs = new();
        public readonly Dictionary<int, string> CommandLines = new();
        public readonly Dictionary<int, int> CommandLineReads = new();

        public void Set(int pid, int ppid, string name, DateTime start, double cpuSeconds, long memory) =>
            Procs[pid] = (new ProcessEntry(pid, ppid, name, 1),
                new ProcessCounters(start, TimeSpan.FromSeconds(cpuSeconds), memory, memory, memory, 0, 0, 10));

        public IReadOnlyList<ProcessEntry> Enumerate() => Procs.Values.Select(p => p.entry).ToList();
        public ProcessCounters? Read(int pid) => Procs.TryGetValue(pid, out var p) ? p.counters : null;
        public string? ReadCommandLine(int pid)
        {
            CommandLineReads[pid] = CommandLineReads.GetValueOrDefault(pid) + 1;
            return CommandLines.GetValueOrDefault(pid);
        }
        public (long used, long limit) ReadCommit() => (40, 100);

        public KernelMemory? Kernel;
        public ProcessorTimes? Processor;
        public KernelMemory? ReadKernelMemory() => Kernel;
        public ProcessorTimes? ReadProcessorTimes() => Processor;

        public readonly Dictionary<int, IReadOnlyList<ServiceInfo>> Services = new();
        public int ServiceReads;
        public IReadOnlyDictionary<int, IReadOnlyList<ServiceInfo>> ReadServices()
        {
            ServiceReads++;
            return Services;
        }
    }

    private sealed class FakeSystem : ISystemMetrics
    {
        public ulong Busy, Idle;
        public (ulong idle, ulong kernel, ulong user)? ReadCpuTimes() => (Idle, Idle + Busy, 0);
        public (long used, long total) ReadMemory() => (8L << 30, 16L << 30);
        public IReadOnlyDictionary<int, int> ReadParentMap() => new Dictionary<int, int>();
    }

    private static readonly DateTime T0 = new(2026, 11, 6, 3, 0, 0, DateTimeKind.Utc);

    private (ResourceRecorder recorder, List<ResourceRecord> records, Func<DateTime> advance) Create(
        FakeTable table, FakeSystem system, int selfPid = 999)
    {
        var now = T0;
        var recorder = new ResourceRecorder(table, system, Path.Combine(_dir, "r.jsonl"), selfPid,
            utcNow: () => now, redact: s => s.Replace("hunter2", "***")) { ManualTicks = true };
        var records = new List<ResourceRecord>();
        recorder.RecordWritten += records.Add;
        return (recorder, records, () => now = now.AddSeconds(5));
    }

    [Fact]
    public void Priming_tick_writes_no_sample_and_the_next_computes_cpu_from_the_delta()
    {
        var table = new FakeTable();
        var system = new FakeSystem();
        table.Set(10, 1, "busy.exe", T0.AddHours(-1), cpuSeconds: 100, memory: 1 << 20);
        var (recorder, records, advance) = Create(table, system);

        recorder.Start();
        recorder.SampleOnce();
        Assert.DoesNotContain(records, r => r is ResourceTick);

        advance();
        // 5 s of wall time on every core = 100% of the machine.
        table.Set(10, 1, "busy.exe", T0.AddHours(-1), cpuSeconds: 100 + 5.0 * Environment.ProcessorCount, memory: 1 << 20);
        system.Busy = 50; system.Idle = 50;
        recorder.SampleOnce();
        recorder.Stop();

        var tick = Assert.Single(records.OfType<ResourceTick>());
        var sample = Assert.Single(tick.Procs);
        Assert.Equal(100, sample.Cpu, 1);
        Assert.IsType<ResourceEnd>(records[^1]);
    }

    [Fact]
    public void Session_trees_are_kept_whole_and_the_tail_is_summed_into_rest()
    {
        var table = new FakeTable();
        var system = new FakeSystem();
        table.Set(1, 0, "explorer.exe", T0.AddDays(-1), 0, 10);
        table.Set(500, 1, "claude.exe", T0.AddHours(-1), 0, 10);
        table.Set(501, 500, "node.exe", T0.AddHours(-1), 0, 10);
        // More big idle processes than the keep-set holds.
        for (int i = 0; i < ResourceRecorder.TopN + 10; i++)
            table.Set(2000 + i, 1, $"big{i}.exe", T0.AddHours(-1), 0, (100 + i) << 20);
        var (recorder, records, advance) = Create(table, system);
        recorder.SetSessions([new SessionTag(500, "sid-1", "perch")]);

        recorder.Start();
        recorder.SampleOnce();
        advance();
        recorder.SampleOnce();
        recorder.Stop();

        var tick = Assert.Single(records.OfType<ResourceTick>());
        var procs = records.OfType<ResourceProc>().ToDictionary(p => p.Id);
        var keptPids = tick.Procs.Select(s => procs[s.Id].Pid).ToHashSet();
        Assert.Contains(500, keptPids);
        Assert.Contains(501, keptPids);
        Assert.Equal(ResourceRecorder.TopN + 2, keptPids.Count);
        Assert.Equal(table.Procs.Count - keptPids.Count, tick.Rest.Count);
        // Ancestors are written so attribution can walk up, even when they aren't sampled.
        Assert.Contains(procs.Values, p => p.Pid == 1);
        Assert.Single(records.OfType<ResourceSession>());
    }

    [Fact]
    public void A_process_is_written_once_and_a_recycled_pid_is_a_new_process()
    {
        var table = new FakeTable();
        var system = new FakeSystem();
        table.Set(10, 1, "a.exe", T0.AddHours(-1), 0, 1 << 20);
        table.CommandLines[10] = "a.exe --password hunter2";
        var (recorder, records, advance) = Create(table, system);

        recorder.Start();
        recorder.SampleOnce();
        for (int i = 0; i < 3; i++) { advance(); recorder.SampleOnce(); }
        // The same pid, now a different process that started later.
        table.Set(10, 1, "b.exe", T0.AddMinutes(1), 0, 1 << 20);
        advance();
        recorder.SampleOnce();
        recorder.Stop();

        var procs = records.OfType<ResourceProc>().Where(p => p.Pid == 10).ToList();
        Assert.Equal(2, procs.Count);
        Assert.NotEqual(procs[0].Id, procs[1].Id);
        Assert.Equal("a.exe --password ***", procs[0].CommandLine);
        Assert.Equal(2, table.CommandLineReads[10]);   // once for a.exe, once for b.exe
    }

    [Fact]
    public void Kernel_memory_and_interrupt_time_are_recorded()
    {
        var table = new FakeTable { Kernel = new KernelMemory(1, 2, 3, 4, 5) };
        var system = new FakeSystem();
        table.Set(10, 1, "a.exe", T0.AddHours(-1), 0, 1 << 20);
        var (recorder, records, advance) = Create(table, system);
        table.Processor = new ProcessorTimes(TimeSpan.FromSeconds(100), TimeSpan.FromSeconds(1));
        recorder.Start();
        recorder.SampleOnce();
        advance();
        // 80 s of processor time passed, 2 s of it on interrupts and DPCs.
        table.Processor = new ProcessorTimes(TimeSpan.FromSeconds(180), TimeSpan.FromSeconds(3));
        recorder.SampleOnce();
        recorder.Stop();

        var tick = Assert.Single(records.OfType<ResourceTick>());
        Assert.Equal(new KernelMemory(1, 2, 3, 4, 5), tick.Kernel);
        Assert.Equal(2.5, tick.InterruptCpu, 3);
    }

    [Fact]
    public void The_catalog_reads_settings_projects_installed_and_cache_only_plugins()
    {
        var root = Path.Combine(_dir, "config");
        var project = Path.Combine(_dir, "proj");
        string HooksJson(string ev, string cmd) =>
            "{\"hooks\":{\"" + ev + "\":[{\"matcher\":\"\",\"hooks\":[{\"type\":\"command\",\"command\":\"" + cmd + "\"}]}]}}";
        void Write(string path, string text)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, text);
        }
        Write(Path.Combine(root, "settings.json"),
            HooksJson("Stop", "notify-me")[..^1] + ""","statusLine":{"type":"command","command":"node sl.mjs"}}""");
        Write(Path.Combine(project, ".claude", "settings.json"), HooksJson("PreToolUse", "lint-it"));
        var installed = Path.Combine(root, "plugins", "cache", "market", "kept", "1.0");
        Write(Path.Combine(installed, "hooks", "hooks.json"), HooksJson("PostToolUse", "kept-hook"));
        Write(Path.Combine(root, "plugins", "installed_plugins.json"),
            "{\"version\":2,\"plugins\":{\"kept@market\":[{\"installPath\":" + System.Text.Json.JsonSerializer.Serialize(installed) + "}]}}");
        Write(Path.Combine(root, "plugins", "cache", "vendor", "sneaky-hooks", "3.1", "hooks", "hooks.json"), HooksJson("PreToolUse", "sneaky run"));

        var catalog = HookCatalog.Load(root, [project]);
        string Source(string cmd) => Assert.Single(catalog.Hooks, h => h.Command == cmd).Source;

        Assert.Equal("your settings.json", Source("notify-me"));
        Assert.Equal("proj/.claude/settings.json", Source("lint-it"));
        Assert.Equal("the kept plugin", Source("kept-hook"));
        Assert.Equal("the sneaky-hooks plugin", Source("sneaky run"));
        Assert.Contains(catalog.Hooks, h => h.IsStatusLine && h.Command == "node sl.mjs");
    }

    [Fact]
    public void Hooks_session_cwd_and_new_process_counts_are_recorded()
    {
        var table = new FakeTable();
        var system = new FakeSystem();
        table.Set(500, 1, "claude.exe", T0.AddHours(-1), 0, 1 << 20);
        var now = T0;
        var hook = new HookDef("the x plugin", HookSourceKind.Plugin, @"C:\real\hooks.json", "PreToolUse", "", "x run");
        int sourceCalls = 0;
        var recorder = new ResourceRecorder(table, system, Path.Combine(_dir, "h.jsonl"), 999,
            utcNow: () => now, redact: s => s.Replace(@"C:\real", "~")) { ManualTicks = true, HookSource = _ => { sourceCalls++; return new HookCatalog([hook]); } };
        var records = new List<ResourceRecord>();
        recorder.RecordWritten += records.Add;
        recorder.SetSessions([new SessionTag(500, "sid", "proj", @"C:\real\proj")]);
        recorder.Start();
        recorder.SampleOnce();
        now = now.AddSeconds(5);
        table.Set(600, 500, "bash.exe", now.AddSeconds(-1), 0, 1 << 20);
        table.Set(601, 500, "git.exe", now.AddSeconds(-1), 0, 1 << 20);
        recorder.SampleOnce();
        recorder.Stop();

        var written = Assert.Single(records.OfType<ResourceHook>());   // the same hook is written once
        Assert.Equal(@"~\hooks.json", written.Hook.File);
        Assert.Equal(@"~\proj", Assert.Single(records.OfType<ResourceSession>()).Cwd);
        Assert.Equal(2, Assert.Single(records.OfType<ResourceTick>()).NewProcesses);
        Assert.True(sourceCalls >= 1);
    }

    [Fact]
    public void Service_hosts_are_written_with_their_services_from_one_lookup_a_tick()
    {
        var table = new FakeTable();
        var system = new FakeSystem();
        table.Set(600, 4, "services.exe", T0.AddDays(-1), 0, 1 << 20);
        table.Set(700, 600, "svchost.exe", T0.AddDays(-1), 0, 900 << 20);
        table.Set(701, 600, "svchost.exe", T0.AddDays(-1), 0, 800 << 20);
        table.Set(800, 1, "svchost.exe", T0.AddDays(-1), 0, 700 << 20);   // not started by the service manager
        table.Services[700] = [new ServiceInfo("wuauserv", "Windows Update")];
        table.Services[701] = [new ServiceInfo("BITS", "Background Intelligent Transfer Service")];
        table.Services[800] = [new ServiceInfo("Bogus", "Bogus")];
        var (recorder, records, advance) = Create(table, system);

        recorder.Start();
        recorder.SampleOnce();
        advance(); recorder.SampleOnce();
        advance(); recorder.SampleOnce();
        recorder.Stop();

        var procs = records.OfType<ResourceProc>().ToDictionary(p => p.Pid);
        Assert.Equal("Windows Update", procs[700].ServiceLabel);
        Assert.Equal("BITS", Assert.Single(procs[701].Services!).Name);
        Assert.Null(procs[800].Services);
        Assert.Null(procs[600].Services);
        Assert.Equal(1, table.ServiceReads);   // both hosts were written in the same tick; nothing new after
    }

    [Fact]
    public void The_file_loads_back_into_the_same_recording()
    {
        var table = new FakeTable();
        var system = new FakeSystem();
        table.Set(10, 1, "a.exe", T0.AddHours(-1), 0, 1 << 20);
        var (recorder, records, advance) = Create(table, system);
        recorder.Start();
        recorder.SampleOnce();
        advance(); recorder.SampleOnce();
        advance(); recorder.SampleOnce();
        recorder.Stop();

        var loaded = ResourceRecording.Load(recorder.FilePath);
        Assert.NotNull(loaded.Header);
        Assert.Equal(2, loaded.Ticks.Count);
        Assert.Equal(records.OfType<ResourceProc>().Count(), loaded.Procs.Count);
        Assert.Equal("stopped", loaded.End?.Reason);
    }
}

/// <summary>Builds recordings by hand for the analysis tests.</summary>
internal sealed class RecordingBuilder
{
    public static readonly DateTime T0 = new(2026, 11, 6, 3, 0, 0, DateTimeKind.Utc);
    public const long GB = 1L << 30;
    public const long MB = 1L << 20;

    private readonly ResourceRecording _r = new();
    private int _nextId = 1;
    private readonly Dictionary<int, int> _idByPid = new();

    public RecordingBuilder(int cores = 4, long totalRam = 16 * GB, int selfPid = 999) =>
        _r.Add(new ResourceHeader(1, T0, 5000, cores, totalRam, selfPid));

    public int Proc(int pid, int ppid, string name, string? cmd = null, DateTime? start = null,
        IReadOnlyList<ServiceInfo>? services = null)
    {
        int id = _nextId++;
        _idByPid[pid] = id;
        _r.Add(new ResourceProc(id, pid, ppid, name, start ?? T0.AddHours(-1), cmd, services));
        return id;
    }

    public RecordingBuilder Session(int pid, string label)
    {
        _r.Add(new ResourceSession(pid, "sid-" + pid, label));
        return this;
    }

    public RecordingBuilder Tick(double systemCpu, long ramUsed, params (int id, double cpu, long memory)[] samples) =>
        Tick(systemCpu, ramUsed, 0, 0, samples);

    public RecordingBuilder Tick(double systemCpu, long ramUsed, long commit, long commitLimit,
        params (int id, double cpu, long memory)[] samples)
    {
        int n = _r.Ticks.Count;
        _r.Add(new ResourceTick(T0.AddSeconds(5 * (n + 1)), systemCpu, ramUsed, commit, commitLimit,
            samples.Select(s => new ProcSample(s.id, s.cpu, s.memory, s.memory, 0, 0)).ToList(), new RestSample(0, 0, 0)));
        return this;
    }

    public RecordingBuilder KernelTick(double systemCpu, long ramUsed, KernelMemory kernel, double interrupts,
        params (int id, double cpu, long memory)[] samples)
    {
        int n = _r.Ticks.Count;
        _r.Add(new ResourceTick(T0.AddSeconds(5 * (n + 1)), systemCpu, ramUsed, 0, 0,
            samples.Select(s => new ProcSample(s.id, s.cpu, s.memory, s.memory, 0, 0, s.memory * 2)).ToList(),
            new RestSample(0, 0, 0), kernel, interrupts));
        return this;
    }

    public RecordingBuilder Repeat(int count, Func<int, RecordingBuilder> tick)
    {
        for (int i = 0; i < count; i++) tick(i);
        return this;
    }

    public ResourceAnalysis Analyse(ResourceContext? context = null) => ResourceAnalysis.Build(_r, context);

    public RecordingBuilder Hook(HookDef hook)
    {
        _r.Add(new ResourceHook(hook));
        return this;
    }

    /// <summary>Ticks where every listed process is sampled (tiny CPU so it's kept), for cause tests.</summary>
    public RecordingBuilder Alive(int count, params int[] ids) =>
        Repeat(count, _ => Tick(10, GB, ids.Select(id => (id, 0.1, 10 * MB)).ToArray()));
}

public sealed class ResourceAnalysisTests
{
    private const long GB = RecordingBuilder.GB, MB = RecordingBuilder.MB;

    [Fact]
    public void Processes_are_charged_to_their_session_perch_or_application()
    {
        var b = new RecordingBuilder(selfPid: 999);
        b.Proc(1, 0, "explorer.exe");
        int claude = b.Proc(500, 1, "claude.exe");
        int mcp = b.Proc(501, 500, "node.exe", "node jira/server.js");
        int perch = b.Proc(999, 1, "perch.exe");
        int perchChild = b.Proc(1000, 999, "git.exe");
        b.Proc(600, 1, "Code.exe");
        int codeNode = b.Proc(601, 600, "node.exe");
        int looseNode = b.Proc(700, 1, "node.exe");
        int vm = b.Proc(800, 4, "vmmem");
        b.Session(500, "perch");
        b.Tick(10, 4 * GB, (claude, 1, MB), (mcp, 1, MB), (perch, 1, MB), (perchChild, 1, MB), (codeNode, 1, MB),
            (looseNode, 1, MB), (vm, 1, MB));
        var a = b.Analyse();

        Assert.Equal("Session · perch", a.GroupOf(claude).Label);
        Assert.Equal("Session · perch", a.GroupOf(mcp).Label);
        Assert.Equal(ResourceGroupKind.Perch, a.GroupOf(perch).Kind);
        Assert.Equal(ResourceGroupKind.Perch, a.GroupOf(perchChild).Kind);
        Assert.Equal("VS Code", a.GroupOf(codeNode).Label);
        Assert.Equal("node", a.GroupOf(looseNode).Label);   // explorer is a stop-point, never inherited
        Assert.Equal(ProcessFamilies.DockerWsl, a.GroupOf(vm).Label);
    }

    [Fact]
    public void A_recycled_parent_pid_is_not_mistaken_for_the_parent()
    {
        var b = new RecordingBuilder();
        b.Session(500, "s");
        // The session's claude (pid 500) exited and the pid went to an unrelated, later process.
        int child = b.Proc(501, 500, "node.exe", start: RecordingBuilder.T0.AddMinutes(-30));
        b.Proc(500, 1, "chrome.exe", start: RecordingBuilder.T0.AddMinutes(-5));
        b.Tick(10, GB, (child, 1, MB));
        Assert.Equal("node", b.Analyse().GroupOf(child).Label);
    }

    [Fact]
    public void Unattributed_is_what_the_system_total_leaves_over()
    {
        var b = new RecordingBuilder();
        int p = b.Proc(10, 1, "chrome.exe");
        b.Tick(50, 10 * GB, (p, 20, 3 * GB));
        var a = b.Analyse();
        int band = a.ChartIndexOf(ResourceAnalysis.UnattributedGroup.Key);
        Assert.Equal(30, a.Cpu[0][band], 3);
        Assert.Equal(7 * GB, a.Memory[0][band]);
        Assert.Equal(20, a.Cpu[0][a.ChartIndexOf("app:Chrome")], 3);
    }

    [Fact]
    public void Kernel_figures_get_their_own_bands_and_shrink_unattributed()
    {
        var b = new RecordingBuilder(totalRam: 64 * GB);
        int p = b.Proc(10, 1, "chrome.exe");
        b.KernelTick(50, 30 * GB, new KernelMemory(5 * GB, 3 * GB, 1 * GB, 0, GB / 2), 2, (p, 20, 10 * GB));
        var a = b.Analyse();

        Assert.Equal(5 * GB, a.Memory[0][a.ChartIndexOf(ResourceAnalysis.PagedPool.Key)]);
        Assert.Equal(3 * GB, a.Memory[0][a.ChartIndexOf(ResourceAnalysis.NonPagedPool.Key)]);
        Assert.Equal(2, a.Cpu[0][a.ChartIndexOf(ResourceAnalysis.Interrupts.Key)], 3);
        int un = a.ChartIndexOf(ResourceAnalysis.UnattributedGroup.Key);
        Assert.Equal(30 * GB - 10 * GB - (5 + 3 + 1) * GB - GB / 2, a.Memory[0][un]);
        Assert.Equal(28, a.Cpu[0][un], 3);
        Assert.Contains(a.BreakdownAt(0, byMemory: true), g => g.Group == ResourceAnalysis.PagedPool && g.Memory == 5 * GB);
    }

    [Fact]
    public void A_recording_without_kernel_figures_has_no_system_bands()
    {
        var b = new RecordingBuilder();
        int p = b.Proc(10, 1, "chrome.exe");
        b.Tick(50, 10 * GB, (p, 20, 3 * GB));
        Assert.DoesNotContain(b.Analyse().Charted, g => g.Kind == ResourceGroupKind.System);
    }

    [Fact]
    public void Large_kernel_pools_are_a_finding_pointing_at_drivers()
    {
        var b = new RecordingBuilder(totalRam: 64 * GB);
        int p = b.Proc(10, 1, "chrome.exe");
        b.Repeat(5, _ => b.KernelTick(20, 30 * GB, new KernelMemory(4600 * MB, 2600 * MB, GB, 40 * MB, 400 * MB), 1, (p, 1, GB)));
        var f = Assert.Single(b.Analyse().Findings, f => f.Kind == FindingKind.KernelPools);
        Assert.Contains("driver", f.Detail);
        Assert.Equal(ResourceAnalysis.NonPagedPool.Key, f.GroupKey);
    }

    [Fact]
    public void Compressed_memory_is_its_own_group_and_a_finding()
    {
        var b = new RecordingBuilder();
        int mc = b.Proc(800, 4, "Memory Compression");
        b.Repeat(3, _ => b.Tick(10, 8 * GB, (mc, 0.2, 2 * GB)));
        var a = b.Analyse();
        Assert.Equal(ProcessFamilies.CompressedMemory, a.GroupOf(mc).Label);
        Assert.Single(a.Findings, f => f.Kind == FindingKind.CompressedMemory);
    }

    [Fact]
    public void Sustained_cpu_names_the_top_contributor()
    {
        var b = new RecordingBuilder();
        int vm = b.Proc(800, 4, "vmmem");
        b.Repeat(5, _ => b.Tick(20, GB, (vm, 5, GB)));
        b.Repeat(10, _ => b.Tick(95, GB, (vm, 80, GB)));
        b.Repeat(5, _ => b.Tick(20, GB, (vm, 5, GB)));
        var f = Assert.Single(b.Analyse().Findings, f => f.Kind == FindingKind.SustainedCpu);
        Assert.Equal(5, f.StartTick);
        Assert.Equal(14, f.EndTick);
        Assert.Contains(ProcessFamilies.DockerWsl, f.Detail);
        Assert.Equal("app:" + ProcessFamilies.DockerWsl, f.GroupKey);   // so picking it opens the culprit
    }

    [Fact]
    public void One_busy_core_of_sixteen_is_info_not_a_warning()
    {
        var b = new RecordingBuilder(cores: 16);
        int p = b.Proc(10, 1, "chrome.exe");
        b.Repeat(14, _ => b.Tick(20, GB, (p, 6.25, 100 * MB)));
        var f = Assert.Single(b.Analyse().Findings, f => f.Kind == FindingKind.Runaway);
        Assert.Equal(FindingSeverity.Info, f.Severity);
    }

    [Fact]
    public void Memory_pressure_is_found_from_ram_or_commit()
    {
        var b = new RecordingBuilder(totalRam: 16 * GB);
        int p = b.Proc(10, 1, "chrome.exe");
        b.Repeat(5, _ => b.Tick(10, 15 * GB, (p, 1, 12 * GB)));
        b.Repeat(5, _ => b.Tick(10, 8 * GB, 95, 100, (p, 1, 6 * GB)));
        var found = b.Analyse().Findings.Where(f => f.Kind == FindingKind.MemoryPressure).ToList();
        Assert.Single(found);   // the two runs touch, so they read as one stretch
        Assert.Equal(FindingSeverity.Critical, found[0].Severity);
    }

    [Fact]
    public void One_mcp_server_per_session_is_reported_as_duplicated()
    {
        var b = new RecordingBuilder();
        b.Session(500, "a").Session(600, "b");
        int c1 = b.Proc(500, 1, "claude.exe");
        int c2 = b.Proc(600, 1, "claude.exe");
        int m1 = b.Proc(501, 500, "node.exe", @"node ~\AppData\Roaming\npm\node_modules\@acme\mcp-jira\dist\index.js --port 51001");
        int m2 = b.Proc(601, 600, "node.exe", @"node ~\AppData\Roaming\npm\node_modules\@acme\mcp-jira\dist\index.js --port 51002");
        b.Repeat(6, _ => b.Tick(10, 4 * GB, (c1, 1, 300 * MB), (c2, 1, 300 * MB), (m1, 1, 400 * MB), (m2, 1, 400 * MB)));
        var f = Assert.Single(b.Analyse().Findings, f => f.Kind == FindingKind.DuplicatedTooling);
        Assert.Contains("@acme/mcp-jira", f.Title);
        Assert.Contains("×2", f.Title);
        Assert.Equal(FindingSeverity.Warning, f.Severity);
    }

    [Fact]
    public void A_child_that_outlives_its_session_is_an_orphan()
    {
        var b = new RecordingBuilder();
        b.Session(500, "a");
        int claude = b.Proc(500, 1, "claude.exe");
        int server = b.Proc(501, 500, "node.exe", "node server.js");
        b.Repeat(4, _ => b.Tick(10, 4 * GB, (claude, 1, MB), (server, 1, 200 * MB)));
        b.Repeat(10, _ => b.Tick(10, 4 * GB, (server, 1, 200 * MB)));
        var f = Assert.Single(b.Analyse().Findings, f => f.Kind == FindingKind.Orphan);
        Assert.Equal(server, f.ProcId);
        Assert.Contains("still running", f.Detail);
    }

    [Fact]
    public void A_process_holding_a_core_for_a_minute_is_a_runaway()
    {
        var b = new RecordingBuilder(cores: 4);
        int p = b.Proc(10, 1, "python.exe");
        b.Repeat(14, _ => b.Tick(40, GB, (p, 25, 100 * MB)));
        var f = Assert.Single(b.Analyse().Findings, f => f.Kind == FindingKind.Runaway);
        Assert.Equal(p, f.ProcId);
        Assert.Contains("1.0 cores", f.Detail);
    }

    [Fact]
    public void Steady_growth_is_reported()
    {
        var b = new RecordingBuilder();
        int p = b.Proc(10, 1, "Code.exe");
        b.Repeat(20, i => b.Tick(10, 4 * GB, (p, 1, 500 * MB + i * 100 * MB)));
        var f = Assert.Single(b.Analyse().Findings, f => f.Kind == FindingKind.MemoryGrowth);
        Assert.Equal(19, f.PeakTick);
    }

    [Fact]
    public void Breakdown_lists_groups_with_their_process_tree()
    {
        var b = new RecordingBuilder();
        b.Session(500, "a");
        int claude = b.Proc(500, 1, "claude.exe");
        int shell = b.Proc(501, 500, "bash.exe");
        int build = b.Proc(502, 501, "dotnet.exe");
        int chrome = b.Proc(10, 1, "chrome.exe");
        b.Tick(60, 8 * GB, (claude, 2, 300 * MB), (shell, 0, 5 * MB), (build, 40, GB), (chrome, 10, 2 * GB));
        var groups = b.Analyse().BreakdownAt(0, byMemory: false);

        Assert.Equal("Session · a", groups[0].Group.Label);
        Assert.Equal(42, groups[0].Cpu, 3);
        Assert.Equal(new[] { 0, 1, 2 }, groups[0].Procs.Select(p => p.Depth));
        Assert.Equal("Chrome", groups[1].Group.Label);
        Assert.Equal(ResourceGroupKind.Rest, groups[^2].Group.Kind);
        Assert.Equal(ResourceGroupKind.Unattributed, groups[^1].Group.Kind);
    }

    // ── R7: launch causes and advice ───────────────────────────────────────────

    private static readonly HookDef GkPre = new("the gitkraken-hooks plugin", HookSourceKind.Plugin, @"~\plugins\gk\hooks\hooks.json",
        "PreToolUse", "", "\"~/AppData/Local/GitKrakenCLI/gk.exe\" ai hook run --host claude-code", "gitkraken-hooks@gitkraken");
    private static readonly HookDef GkPost = GkPre with { Event = "PostToolUse" };
    private static readonly HookDef StatusLine = new("your settings.json", HookSourceKind.UserSettings, @"~\.claude\settings.json",
        HookCatalog.StatusLineEvent, "", @"node ""~\.claude\perch-statusline.mjs""");

    private const string GitBash = @"""C:\Program Files\Git\bin\bash.exe""";

    [Fact]
    public void Session_processes_are_explained_by_what_launched_them()
    {
        var b = new RecordingBuilder();
        b.Session(500, "busy").Hook(GkPre).Hook(GkPost).Hook(StatusLine);
        int claude = b.Proc(500, 1, "claude.exe", "claude");
        int hookShell = b.Proc(510, 500, "bash.exe", GitBash + " -c \"\\\"~/AppData/Local/GitKrakenCLI/gk.exe\\\" ai hook run --host claude-code\"");
        int hookConhost = b.Proc(511, 510, "conhost.exe", "conhost.exe 0x4");
        int gk = b.Proc(512, 510, "gk.exe", "\"~/AppData/Local/GitKrakenCLI/gk.exe\" ai hook run --host claude-code");
        int tool = b.Proc(520, 500, "bash.exe", GitBash + " -c \"source ~/.claude/shell-snapshots/snapshot-bash.sh && dotnet test\"");
        int dotnet = b.Proc(521, 520, "dotnet.exe", "dotnet test");
        int ps = b.Proc(530, 500, "cmd.exe", "cmd.exe /d /s /c \"chcp 65001 >nul & pwsh -NoProfile -Command Get-Date\"");
        int rg = b.Proc(540, 500, "claude.exe", "rg --no-config --files --glob *.cs");
        int git = b.Proc(550, 500, "git.exe", "git status --porcelain");
        int status = b.Proc(560, 500, "bash.exe", GitBash + " -c \"node \\\"~\\.claude\\perch-statusline.mjs\\\"\"");
        b.Alive(2, claude, hookShell, hookConhost, gk, tool, dotnet, ps, rg, git, status);
        var a = b.Analyse();

        Assert.Equal(LaunchCauseKind.Claude, a.CauseOf(claude)!.Kind);
        foreach (var id in new[] { hookShell, hookConhost, gk })
        {
            Assert.Equal(LaunchCauseKind.Hook, a.CauseOf(id)!.Kind);
            Assert.Equal("gk ai hook run hook · the gitkraken-hooks plugin", a.CauseOf(id)!.Label);
        }
        Assert.Equal(2, a.CauseOf(gk)!.Hooks.Count);   // PreToolUse and PostToolUse, one cause
        Assert.Equal(LaunchCauseKind.BashTool, a.CauseOf(dotnet)!.Kind);
        Assert.Equal(LaunchCauseKind.PowerShellTool, a.CauseOf(ps)!.Kind);
        Assert.Equal(LaunchCauseKind.Search, a.CauseOf(rg)!.Kind);
        Assert.Equal("git run by Claude Code itself", a.CauseOf(git)!.Label);
        Assert.Equal(LaunchCauseKind.StatusLine, a.CauseOf(status)!.Kind);

        var hookStats = Assert.Single(a.CausesFor("session:500"), c => c.Cause.Kind == LaunchCauseKind.Hook);
        Assert.Equal(1, hookStats.Launches);
        Assert.Equal(3, hookStats.Processes);
    }

    [Fact]
    public void A_hook_on_every_tool_call_is_a_finding_with_advice_and_an_estimate_from_the_transcript()
    {
        var b = new RecordingBuilder();
        b.Session(500, "busy").Hook(GkPre).Hook(GkPost);
        int claude = b.Proc(500, 1, "claude.exe", "claude");
        var shells = Enumerable.Range(0, 25).Select(i =>
            b.Proc(600 + i, 500, "bash.exe", GitBash + " -c \"\\\"~/AppData/Local/GitKrakenCLI/gk.exe\\\" ai hook run --host claude-code\"")).ToList();
        for (int i = 0; i < 25; i++) b.Tick(30, GB, (claude, 1, MB), (shells[i], 0.5, MB));

        var calls = Enumerable.Range(0, 100).Select(i => new ToolCall(RecordingBuilder.T0.AddSeconds(i), "main", i % 2 == 0 ? "Read" : "Bash")).ToList();
        var context = new ResourceContext(null, new Dictionary<string, SessionActivity> { ["sid-500"] = new("sid-500", null, calls) });
        var f = Assert.Single(b.Analyse(context).Findings, f => f.Kind == FindingKind.HookFanOut);

        Assert.Contains("~200 runs", f.Title);   // 100 tool calls × (PreToolUse + PostToolUse)
        Assert.Contains("every tool", f.Detail);
        Assert.Contains("/plugin", f.Advice);
        Assert.Equal(GkPre.File, f.ActionPath);
        Assert.Equal(FindingSeverity.Warning, f.Severity);
    }

    [Fact]
    public void The_same_program_hooked_twice_is_a_duplicate()
    {
        var b = new RecordingBuilder();
        var installed = new HookDef("your settings.json", HookSourceKind.UserSettings, "s.json", "SessionStart", "", @"~\AppData\Local\Perch\bin\perch-hook.exe");
        b.Hook(installed).Hook(installed with { Command = @"~\AppData\Local\Perch (Dev)\bin\perch-hook.exe" });
        int p = b.Proc(10, 1, "chrome.exe");
        b.Tick(10, GB, (p, 1, MB));
        var f = Assert.Single(b.Analyse().Findings, f => f.Kind == FindingKind.DuplicateHook);
        Assert.Contains("perch-hook is registered 2 times", f.Title);
    }

    [Fact]
    public void Many_agents_at_once_is_a_fan_out_finding()
    {
        var b = new RecordingBuilder();
        b.Session(500, "fan");
        int claude = b.Proc(500, 1, "claude.exe", "claude");
        b.Repeat(30, _ => b.Tick(40, GB, (claude, 5, MB)));
        // Five agents (main + four sub-agents) calling tools through ticks 12–29.
        var calls = Enumerable.Range(60, 90).Select(s => new ToolCall(RecordingBuilder.T0.AddSeconds(s), s % 5 == 0 ? "main" : $"agent-{s % 5}", "Grep")).ToList();
        var context = new ResourceContext(null, new Dictionary<string, SessionActivity> { ["sid-500"] = new("sid-500", null, calls) });
        var a = b.Analyse(context);
        var f = Assert.Single(a.Findings, f => f.Kind == FindingKind.SubagentFanOut);
        Assert.Contains("5 agents at once", f.Title);
        Assert.Contains("at most 3 sub-agents", f.Advice);
        Assert.True(a.ToolCallsPerMinute[20][0] > 0);
    }

    [Fact]
    public void Cpu_that_tracks_launches_is_called_short_lived_processes()
    {
        var b = new RecordingBuilder(cores: 8);
        int claude = b.Proc(500, 1, "claude.exe", "claude");
        b.Session(500, "s");
        // Bursts of new processes (each seen once) with CPU no process owns rising alongside them.
        int next = 1000;
        for (int i = 0; i < 48; i++)
        {
            bool burst = i / 12 % 2 == 1;
            var samples = new List<(int, double, long)> { (claude, 1, MB) };
            for (int k = 0; k < (burst ? 6 : 0); k++)
                samples.Add((b.Proc(next++, 500, "bash.exe", GitBash + " -c \"tool x\""), 0, MB));
            b.Tick(burst ? 40 : 10, GB, samples.ToArray());
        }
        var f = Assert.Single(b.Analyse().Findings, f => f.Kind == FindingKind.ShortLivedProcesses);
        Assert.Contains("too short-lived", f.Title);
        Assert.Contains("tool x", f.Detail);
    }

    [Fact]
    public void Kernel_and_security_agents_are_never_told_to_end()
    {
        var b = new RecordingBuilder(cores: 4);
        int system = b.Proc(4, 0, "System");
        int defender = b.Proc(700, 4, "MsMpEng.exe");
        b.Repeat(14, _ => b.Tick(60, GB, (system, 30, MB), (defender, 30, MB)));
        var advice = b.Analyse().Findings.Where(f => f.Kind == FindingKind.Runaway).Select(f => f.Advice!).ToList();
        Assert.Equal(2, advice.Count);
        Assert.All(advice, a => Assert.DoesNotContain("end it", a));
    }

    [Fact]
    public void A_busy_svchost_is_named_by_its_services_and_stays_in_windows()
    {
        var b = new RecordingBuilder(cores: 4);
        b.Proc(600, 4, "services.exe");
        int wu = b.Proc(700, 600, "svchost.exe", services: [new ServiceInfo("wuauserv", "Windows Update")]);
        int other = b.Proc(701, 600, "svchost.exe", services:
            [new ServiceInfo("VendorSvc", "Vendor Helper"), new ServiceInfo("VendorSvc2", "Vendor Helper 2")]);
        b.Repeat(14, _ => b.Tick(60, GB, (wu, 30, MB), (other, 30, MB)));
        var a = b.Analyse();

        var runaways = a.Findings.Where(f => f.Kind == FindingKind.Runaway).ToDictionary(f => f.ProcId!.Value);
        Assert.Equal("svchost · Windows Update (700) busy for 1m 10s", runaways[wu].Title);
        Assert.Contains("Pause updates", runaways[wu].Advice);
        Assert.Contains("Vendor Helper (VendorSvc), Vendor Helper 2 (VendorSvc2)", runaways[other].Advice);
        Assert.Contains("services.msc", runaways[other].Advice);
        Assert.All(runaways.Values, f => Assert.Equal("Windows", a.GroupOf(f.ProcId!.Value).Label));
        Assert.Equal("svchost · Vendor Helper +1", ResourceAnalysis.ToolLabel(a.Recording.Procs[other]));
    }

    [Theory]
    [InlineData(GitBash + " -c \"\\\"C:/x/GitKrakenCLI/gk.exe\\\" ai hook run --host claude-code\"", "gk ai hook run")]
    [InlineData("bash -c \"F=\\\"${CLAUDE_PLUGIN_ROOT}/scripts/flow.sh\\\"; if [ -f \\\"$F\\\" ]; then bun \\\"$F\\\"; fi\"", "flow.sh")]
    [InlineData(@"node ""~\.claude\perch-statusline.mjs""", "node perch-statusline.mjs")]
    [InlineData("cmd.exe /d /s /c \"chcp 65001 >nul & pwsh -NoProfile\"", "chcp 65001 >nul")]
    public void Inner_commands_name_what_a_shell_ran(string commandLine, string expected) =>
        Assert.Equal(expected, ResourceAnalysis.InnerCommand(commandLine));

    // ── Hooks page ─────────────────────────────────────────────────────────────

    private static readonly DateTime At = RecordingBuilder.T0.AddSeconds(20);

    private static SessionActivity Activity(params HookMoment[] moments) =>
        new("sid-500", null, moments.Where(m => m.Event == "PreToolUse").Select(m => new ToolCall(m.AtUtc, m.Agent, m.Tool!)).ToList())
        {
            Moments = moments.OrderBy(m => m.AtUtc).ToList(),
        };

    [Fact]
    public void Hook_runs_are_expected_per_event_and_tool_and_caught_processes_pinned_to_theirs()
    {
        var b = new RecordingBuilder();
        b.Session(500, "busy").Hook(GkPre).Hook(GkPost);
        int claude = b.Proc(500, 1, "claude.exe", "claude");
        // One gk run caught, started 200 ms after the Bash call.
        int shell = b.Proc(600, 500, "bash.exe", GitBash + " -c \"\\\"~/AppData/Local/GitKrakenCLI/gk.exe\\\" ai hook run --host claude-code\"",
            start: At.AddMilliseconds(200));
        b.Repeat(4, i => i == 0 ? b.Tick(10, GB, (claude, 1, MB)) : b.Tick(10, GB, (claude, 1, MB), (shell, 0.2, MB)));
        var activity = Activity(
            new HookMoment(At, "main", "PreToolUse", "Bash"),
            new HookMoment(At.AddSeconds(1), "main", "PostToolUse", "Bash"),
            new HookMoment(At.AddSeconds(30), "agent-abcdef1234", "PreToolUse", "Read"),
            new HookMoment(At.AddSeconds(31), "agent-abcdef1234", "PostToolUse", "Read"));
        var context = new ResourceContext(null, new Dictionary<string, SessionActivity> { ["sid-500"] = activity });
        var analysis = b.Analyse(context);

        var report = Assert.Single(HookActivity.Build(analysis, context));
        Assert.Equal(4, report.Runs);
        Assert.Equal(1, report.Seen);
        var pre = Assert.Single(report.Events, e => e.Event == "PreToolUse");
        Assert.Equal(2, pre.Runs);
        Assert.Equal(1, pre.Seen);
        Assert.Equal(["(every tool)"], pre.Matchers.Select(m => m == "" ? "(every tool)" : m));
        Assert.Contains(pre.ByTool, t => t.Name == "Read" && t.Runs == 1);
        Assert.Equal(600, pre.Recent.Single(r => r.Seen is not null).Seen!.Pid);
        Assert.False(pre.Inferred);
        Assert.Contains(report.Advice, a => a.Contains("only read"));
        Assert.Contains(report.Advice, a => a.Contains("/plugin"));
    }

    [Fact]
    public void A_hook_no_catalog_knows_is_pinned_to_the_nearest_event_and_marked_inferred()
    {
        var b = new RecordingBuilder();
        b.Session(500, "busy");
        int claude = b.Proc(500, 1, "claude.exe", "claude");
        int shell = b.Proc(600, 500, "bash.exe", GitBash + " -c \"tool-x hook\"", start: At.AddSeconds(1.2));
        b.Repeat(3, _ => b.Tick(10, GB, (claude, 1, MB), (shell, 0.2, MB)));
        var context = new ResourceContext(null, new Dictionary<string, SessionActivity>
        {
            ["sid-500"] = Activity(new HookMoment(At, "main", "PreToolUse", "Edit"), new HookMoment(At.AddSeconds(1), "main", "PostToolUse", "Edit")),
        });

        var report = Assert.Single(HookActivity.Build(b.Analyse(context), context));
        var ev = Assert.Single(report.Events);
        Assert.Equal("PostToolUse", ev.Event);   // 0.2 s after the result, 1.2 s after the call
        Assert.True(ev.Inferred);
    }

    [Fact]
    public void Transcripts_yield_hook_moments_and_recorded_runs()
    {
        var dir = Path.Combine(Path.GetTempPath(), "perch-tests", "activity-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(Path.Combine(dir, "sid", "subagents"));
            string L(int s, string json) => json.Replace("@TS@", At.AddSeconds(s).ToString("yyyy-MM-ddTHH:mm:ss.fffZ"));
            File.WriteAllLines(Path.Combine(dir, "sid.jsonl"),
            [
                L(0, """{"type":"user","timestamp":"@TS@","cwd":"C:\\proj","message":{"role":"user","content":"do it"}}"""),
                L(1, """{"type":"assistant","timestamp":"@TS@","message":{"content":[{"type":"tool_use","id":"t1","name":"Bash","input":{}}]}}"""),
                L(2, """{"type":"user","timestamp":"@TS@","message":{"content":[{"type":"tool_result","tool_use_id":"t1","is_error":true}]}}"""),
                L(3, """{"type":"attachment","timestamp":"@TS@","attachment":{"type":"hook_non_blocking_error","hookEvent":"PostToolUseFailure","command":"gk hook","stderr":"boom\nmore","exitCode":1}}"""),
                L(4, """{"type":"system","subtype":"turn_duration","timestamp":"@TS@"}"""),
                L(4, """{"type":"system","subtype":"stop_hook_summary","timestamp":"@TS@","hookInfos":[{"command":"gk hook","durationMs":460}]}"""),
            ]);
            File.WriteAllLines(Path.Combine(dir, "sid", "subagents", "agent-a1.jsonl"),
            [
                L(5, """{"type":"user","timestamp":"@TS@","message":{"content":"sub task"}}"""),
                L(6, """{"type":"assistant","timestamp":"@TS@","message":{"content":[{"type":"tool_use","id":"t2","name":"Grep","input":{}}]}}"""),
            ]);

            var a = SessionActivity.Read("sid", Path.Combine(dir, "sid.jsonl"), At.AddMinutes(-1), At.AddMinutes(5));
            var events = a.Moments.Select(m => $"{m.Agent}:{m.Event}:{m.Tool}").ToList();
            Assert.Contains("main:UserPromptSubmit:", events);
            Assert.Contains("main:PreToolUse:Bash", events);
            Assert.Contains("main:PostToolUseFailure:Bash", events);
            Assert.Contains("main:Stop:", events);
            Assert.Contains("main:SessionStart:", events);
            Assert.Contains("agent-a1:SubagentStart:", events);
            Assert.Contains("agent-a1:PreToolUse:Grep", events);
            Assert.DoesNotContain("agent-a1:UserPromptSubmit:", events);   // a sub-agent's task isn't a user prompt
            Assert.Equal(@"C:\proj", a.Cwd);
            Assert.Contains(a.Traces, t => t.Event == "PostToolUseFailure" && t.Error == "boom" && t.ExitCode == 1);
            Assert.Contains(a.Traces, t => t.Event == "Stop" && t.DurationMs == 460);
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }

    [Fact]
    public void Matchers_are_regexes_over_the_tool_name()
    {
        Assert.True(GkPre.MatchesTool("Read"));
        var narrow = GkPre with { Matcher = "Edit|Write" };
        Assert.True(narrow.MatchesTool("Write"));
        Assert.False(narrow.MatchesTool("Read"));
        Assert.False(narrow.MatchesTool("NotebookEdit"));
    }

    [Theory]
    [InlineData("node.exe", @"node ~\.mcp\jira\server.js", "node jira/server.js")]
    [InlineData("node.exe", @"node C:\x\node_modules\@scope\server-github\dist\index.js", "node @scope/server-github")]
    [InlineData("python.exe", "python -m uvicorn app:main", "python")]
    [InlineData("chrome.exe", null, "chrome")]
    public void Tool_labels_name_the_script_or_package(string name, string? cmd, string expected) =>
        Assert.Equal(expected, ResourceAnalysis.ToolLabel(new ResourceProc(1, 1, 0, name, null, cmd)));
}
