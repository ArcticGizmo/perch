using Perch.Data.Resources;

namespace Perch.Avalonia.Rendering;

/// <summary>A synthetic 15-minute resource recording for the headless render of the resource monitor: three
/// sessions sharing a duplicated MCP server, a build that drags Defender along, Docker's VM, Chrome, a growing
/// editor and a server left behind by an ended session, so every chart band and most findings show.</summary>
internal static class SampleResourceRecording
{
    private const long MB = 1L << 20, GB = 1L << 30;

    public static ResourceRecording Build()
    {
        var t0 = DateTime.UtcNow.AddMinutes(-15);
        var r = new ResourceRecording();
        r.Add(new ResourceHeader(1, t0, 5000, 16, 32 * GB, 900));

        int nextId = 1;
        var procs = new Dictionary<string, int>();
        int Proc(string key, int pid, int ppid, string name, string? cmd = null, Perch.Platform.ServiceInfo[]? services = null)
        {
            int id = nextId++;
            procs[key] = id;
            r.Add(new ResourceProc(id, pid, ppid, name, t0.AddMinutes(-20), cmd, services));
            return id;
        }

        Proc("explorer", 100, 4, "explorer.exe");
        Proc("wt", 200, 100, "WindowsTerminal.exe");
        Proc("perch", 900, 100, "perch.exe");
        foreach (var (name, pid) in new[] { ("perch", 1000), ("hypertree", 2000), ("billing-api", 3000) })
        {
            Proc("claude-" + name, pid, 200, "claude.exe", "claude");
            Proc("jira-" + name, pid + 1, pid, "node.exe",
                @"node ~\AppData\Roaming\npm\node_modules\@acme\mcp-jira\dist\index.js --port " + (51000 + pid));
            Proc("fs-" + name, pid + 2, pid, "node.exe", @"node ~\.mcp\filesystem\server.js");
            r.Add(new ResourceSession(pid, "sid-" + pid, name));
        }
        Proc("build", 3010, 3000, "dotnet.exe", "dotnet build Billing.sln");
        Proc("vbcs", 3011, 3010, "VBCSCompiler.exe");
        Proc("vmmem", 400, 4, "vmmem");
        Proc("dockerd", 410, 100, "com.docker.backend.exe");
        for (int i = 0; i < 4; i++) Proc("chrome" + i, 500 + i, i == 0 ? 100 : 500, "chrome.exe");
        Proc("code", 600, 100, "Code.exe");
        Proc("defender", 700, 4, "MsMpEng.exe");
        Proc("compression", 800, 4, "Memory Compression");
        Proc("services", 250, 4, "services.exe");
        Proc("wu", 260, 250, "svchost.exe", services: [new("wuauserv", "Windows Update")]);
        Proc("dcom", 261, 250, "svchost.exe", services:
        [
            new("BrokerInfrastructure", "Background Tasks Infrastructure Service"), new("DcomLaunch", "DCOM Server Process Launcher"),
            new("PlugPlay", "Plug and Play"), new("Power", "Power"),
        ]);

        var rng = new Random(7);
        for (int i = 0; i < 180; i++)
        {
            double Jitter(double v) => Math.Max(0, v + (rng.NextDouble() - 0.5) * v * 0.3);
            bool building = i is >= 60 and < 96;
            bool dockerBusy = i is >= 120 and < 140;
            var samples = new List<ProcSample>();
            void S(string key, double cpu, long mem) =>
                samples.Add(new ProcSample(procs[key], Jitter(cpu), mem, mem, 0, 0, mem + mem / 3));

            S("perch", 1.5, 260 * MB);
            foreach (var name in new[] { "perch", "hypertree", "billing-api" })
            {
                if (name == "hypertree" && i >= 140) continue;
                S("claude-" + name, 2.5, 420 * MB);
                S("fs-" + name, 0.2, 90 * MB);
            }
            S("jira-perch", 0.4, 310 * MB);
            S("jira-billing-api", 0.4, 310 * MB);
            S("jira-hypertree", 0.4, 310 * MB);   // outlives its session past tick 140
            if (building)
            {
                S("build", 38, 1400 * MB);
                S("vbcs", 22, 900 * MB);
            }
            S("vmmem", dockerBusy ? 45 : 4, 7 * GB);
            S("dockerd", 0.8, 220 * MB);
            for (int c = 0; c < 4; c++) S("chrome" + c, c == 1 ? 6 : 1.2, (c == 0 ? 600 : 450) * MB);
            S("code", 2, 700 * MB + i * 9 * MB);
            S("defender", building ? 24 : 1, 380 * MB);
            S("compression", 0.3, 2 * GB);
            S("wu", i is >= 20 and < 50 ? 8 : 0.2, 180 * MB);
            S("dcom", 0.5, 60 * MB);

            double procCpu = samples.Sum(s => s.Cpu);
            long procMem = samples.Sum(s => s.Memory);
            var rest = new RestSample(Jitter(4), 3 * GB, 312);
            var kernel = new Perch.Platform.KernelMemory(4600 * MB, 2600 * MB, GB, 40 * MB, 400 * MB);
            double irq = Jitter(1.5);
            double cpu = Math.Min(100, procCpu + rest.Cpu + irq + 1);
            long ram = procMem + rest.Memory + 4600 * MB + 2640 * MB + GB + 400 * MB + 3 * GB;
            r.Add(new ResourceTick(t0.AddSeconds(5 * (i + 1)), cpu, ram, ram + 6 * GB, 48 * GB, samples, rest, kernel, irq));
        }
        r.Add(new ResourceEnd(t0.AddSeconds(5 * 181), "stopped"));
        return r;
    }
}
