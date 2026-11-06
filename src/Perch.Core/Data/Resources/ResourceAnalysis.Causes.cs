using System.Text.RegularExpressions;

namespace Perch.Data.Resources;

public enum LaunchCauseKind { Claude, Hook, StatusLine, BashTool, PowerShellTool, Search, Command, LongRunning, Other }

/// <summary>Why a process in a session's tree exists: the launch that started it (a hook, the status line, a Bash
/// tool call, Claude's search…). <see cref="Hooks"/> are the registrations behind a hook or status-line cause.</summary>
public sealed record LaunchCause(string Key, LaunchCauseKind Kind, string Label, IReadOnlyList<HookDef> Hooks)
{
    public HookDef? Hook => Hooks.Count > 0 ? Hooks[0] : null;
}

/// <summary>One cause's footprint in one session over the whole recording. <see cref="Launches"/> and
/// <see cref="Processes"/> are lower bounds: only processes alive at a sample are ever seen.</summary>
public sealed record CauseStats(
    LaunchCause Cause,
    string SessionKey,
    int Launches,
    int Processes,
    double CpuSeconds,
    long PeakMemory,
    IReadOnlyDictionary<string, int> Exes,
    int FirstTick,
    int LastTick);

/// <summary>A cause's share at one tick, for the breakdown.</summary>
public sealed record CauseAt(CauseStats Stats, double Cpu, long Memory, int Alive);

/// <summary>One launch the sampler caught: the process Claude started (its pid and exact start time) and how many
/// processes ran under it, with the CPU they were seen using.</summary>
public sealed record ObservedLaunch(int ProcId, int Pid, string SessionKey, LaunchCause Cause, DateTime? StartUtc, int Processes, double CpuSeconds);

public sealed partial class ResourceAnalysis
{
    private readonly Dictionary<int, LaunchCause> _causeOfProc = new();
    private readonly Dictionary<string, List<CauseStats>> _causesBySession = new();
    private readonly Dictionary<int, int> _firstTick = new();
    private readonly Dictionary<int, int> _lastTick = new();
    private static readonly IReadOnlyList<HookDef> NoHooks = [];

    // The context's catalog (the recording's own hooks merged with this machine's) when the viewer built one, else
    // just the hooks the recording carries.
    private IReadOnlyList<HookDef> CatalogHooks => _context.Hooks?.Hooks ?? _recording.Hooks;

    /// <summary>Why <paramref name="procId"/> exists, for a process in a session's tree; null otherwise.</summary>
    public LaunchCause? CauseOf(int procId) => _causeOfProc.GetValueOrDefault(procId);

    /// <summary>Every launch in a session tree that was sampled at least once, in start order.</summary>
    public IReadOnlyList<ObservedLaunch> ObservedLaunches { get; private set; } = [];

    /// <summary>The hooks this analysis knows of: the context's catalog, else the recording's own.</summary>
    public IReadOnlyList<HookDef> Hooks => CatalogHooks;

    /// <summary>The cause a registered hook's runs are charged to (one per source and command, across its events).</summary>
    public LaunchCause CauseForHook(HookDef hook) => HookCause(hook);

    /// <summary>A session's causes over the whole recording, most launches first.</summary>
    public IReadOnlyList<CauseStats> CausesFor(string sessionKey) =>
        _causesBySession.TryGetValue(sessionKey, out var list) ? list : [];

    /// <summary>A session's causes with what each was using at <paramref name="tick"/>.</summary>
    public IReadOnlyList<CauseAt> CausesAt(string sessionKey, int tick)
    {
        var stats = CausesFor(sessionKey);
        if (stats.Count == 0 || tick < 0 || tick >= TickCount) return [];
        var at = new Dictionary<string, (double cpu, long mem, int alive)>();
        foreach (var s in _recording.Ticks[tick].Procs)
        {
            if (GroupOf(s.Id).Key != sessionKey || CauseOf(s.Id) is not { } cause) continue;
            var cur = at.GetValueOrDefault(cause.Key);
            at[cause.Key] = (cur.cpu + s.Cpu, cur.mem + s.Memory, cur.alive + 1);
        }
        return stats.Select(st =>
        {
            var v = at.GetValueOrDefault(st.Cause.Key);
            return new CauseAt(st, v.cpu, v.mem, v.alive);
        }).ToList();
    }

    // ── Classification ─────────────────────────────────────────────────────────

    private void BuildCauses()
    {
        for (int i = 0; i < _recording.Ticks.Count; i++)
            foreach (var s in _recording.Ticks[i].Procs)
            {
                _firstTick.TryAdd(s.Id, i);
                _lastTick[s.Id] = i;
            }

        var children = new Dictionary<int, List<ResourceProc>>();
        foreach (var p in _recording.Procs.Values)
        {
            if (Parent(p) is not { } parent) continue;
            if (!children.TryGetValue(parent.Id, out var kids)) children[parent.Id] = kids = new();
            kids.Add(p);
        }

        var signatures = CatalogHooks
            .Select(h => (hook: h, sig: Signature(h.Command)))
            .Where(x => x.sig.Length >= 6)
            .OrderByDescending(x => x.sig.Length)
            .ToList();

        var launchCause = new Dictionary<int, LaunchCause>();
        foreach (var proc in _recording.Procs.Values)
        {
            var group = GroupOf(proc.Id);
            if (group.Kind != ResourceGroupKind.Session) continue;
            int rootPid = int.Parse(group.Key["session:".Length..]);
            if (proc.Pid == rootPid)
            {
                _causeOfProc[proc.Id] = ClaudeCause;
                continue;
            }
            var launch = LaunchOf(proc, rootPid);
            if (!launchCause.TryGetValue(launch.Id, out var cause))
                launchCause[launch.Id] = cause = Classify(launch, children, signatures);
            _causeOfProc[proc.Id] = cause;
        }

        // Footprint per session and cause, over every process that was sampled at least once.
        var acc = new Dictionary<(string session, string cause), (LaunchCause cause, HashSet<int> launches, HashSet<int> procs,
            double cpu, long peak, Dictionary<string, int> exes, int first, int last)>();
        double coreSeconds = Math.Max(1, _recording.Cores) * TickInterval.TotalSeconds / 100.0;
        var memoryAt = new Dictionary<(string, string, int), long>();
        foreach (var (procId, cause) in _causeOfProc)
        {
            if (!_firstTick.TryGetValue(procId, out int first)) continue;
            var proc = _recording.Procs[procId];
            string session = GroupOf(procId).Key;
            var key = (session, cause.Key);
            if (!acc.TryGetValue(key, out var a))
                a = (cause, new HashSet<int>(), new HashSet<int>(), 0, 0, new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase), first, _lastTick[procId]);
            a.procs.Add(procId);
            if (cause.Kind != LaunchCauseKind.Claude) a.launches.Add(LaunchOf(proc, int.Parse(session["session:".Length..])).Id);
            string stem = ProcessFamilies.Stem(proc.Name);
            a.exes[stem] = a.exes.GetValueOrDefault(stem) + 1;
            a.first = Math.Min(a.first, first);
            a.last = Math.Max(a.last, _lastTick[procId]);
            acc[key] = a;
        }
        for (int i = 0; i < _recording.Ticks.Count; i++)
        {
            foreach (var s in _recording.Ticks[i].Procs)
            {
                if (!_causeOfProc.TryGetValue(s.Id, out var cause)) continue;
                var key = (GroupOf(s.Id).Key, cause.Key);
                if (!acc.TryGetValue(key, out var a)) continue;
                a.cpu += s.Cpu * coreSeconds;
                acc[key] = a;
                var mk = (key.Item1, key.Item2, i);
                memoryAt[mk] = memoryAt.GetValueOrDefault(mk) + s.Memory;
            }
        }
        foreach (var ((session, causeKey), a) in acc)
        {
            long peak = 0;
            for (int i = a.first; i <= a.last; i++) peak = Math.Max(peak, memoryAt.GetValueOrDefault((session, causeKey, i)));
            var stats = new CauseStats(a.cause, session, a.launches.Count, a.procs.Count, a.cpu, peak, a.exes, a.first, a.last);
            if (!_causesBySession.TryGetValue(session, out var list)) _causesBySession[session] = list = new();
            list.Add(stats);
        }
        // Each launch with what ran under it, for the hooks page to match against the transcript's events.
        var launches = new Dictionary<int, (string session, LaunchCause cause, int procs, double cpu)>();
        var cpuByProc = new Dictionary<int, double>();
        foreach (var tick in _recording.Ticks)
            foreach (var s in tick.Procs)
                cpuByProc[s.Id] = cpuByProc.GetValueOrDefault(s.Id) + s.Cpu * coreSeconds;
        foreach (var (procId, cause) in _causeOfProc)
        {
            if (cause.Kind == LaunchCauseKind.Claude || !_firstTick.ContainsKey(procId)) continue;
            string session = GroupOf(procId).Key;
            var launch = LaunchOf(_recording.Procs[procId], int.Parse(session["session:".Length..]));
            var cur = launches.TryGetValue(launch.Id, out var l) ? l : (session, cause, 0, 0.0);
            launches[launch.Id] = (session, cause, cur.Item3 + 1, cur.Item4 + cpuByProc.GetValueOrDefault(procId));
        }
        ObservedLaunches = launches
            .Select(kv => new ObservedLaunch(kv.Key, _recording.Procs[kv.Key].Pid, kv.Value.session, kv.Value.cause,
                _recording.Procs[kv.Key].StartUtc, kv.Value.procs, kv.Value.cpu))
            .OrderBy(l => l.StartUtc ?? DateTime.MinValue)
            .ToList();

        foreach (var list in _causesBySession.Values)
            list.Sort((x, y) => x.Cause.Kind == LaunchCauseKind.Claude ? -1 : y.Cause.Kind == LaunchCauseKind.Claude ? 1
                : y.Launches != x.Launches ? y.Launches.CompareTo(x.Launches) : y.CpuSeconds.CompareTo(x.CpuSeconds));
    }

    private static readonly LaunchCause ClaudeCause = new("claude", LaunchCauseKind.Claude, "Claude itself", NoHooks);

    // The process Claude started that led to this one: the ancestor (or the process itself) whose parent is the
    // session's root. A broken chain stops at the highest ancestor still on record.
    private ResourceProc LaunchOf(ResourceProc proc, int rootPid)
    {
        var cur = proc;
        for (int depth = 0; depth < 32; depth++)
        {
            var parent = Parent(cur);
            if (parent is null || parent.Pid == rootPid) return cur;
            cur = parent;
        }
        return cur;
    }

    private LaunchCause Classify(ResourceProc launch, Dictionary<int, List<ResourceProc>> children,
        List<(HookDef hook, string sig)> signatures)
    {
        // The launch and everything under it, nearest first: a hook run through Git Bash is bash → bash → the program.
        var subtree = new List<ResourceProc> { launch };
        for (int i = 0; i < subtree.Count && subtree.Count < 64; i++)
            if (children.TryGetValue(subtree[i].Id, out var kids)) subtree.AddRange(kids);
        var normalized = subtree.Select(p => Normalize(p.CommandLine ?? "")).ToList();

        string stem = ProcessFamilies.Stem(launch.Name).ToLowerInvariant();
        string own = normalized[0];
        // A Bash tool call always sources Claude's shell snapshot and a hook never does, so this goes first: a short
        // hook command ("npm test") must not claim the user's own runs of it.
        if (own.Contains("/shell-snapshots/", StringComparison.Ordinal))
            return Simple(stem is "powershell" or "pwsh" ? LaunchCauseKind.PowerShellTool : LaunchCauseKind.BashTool);

        foreach (var (hook, sig) in signatures)
        {
            if (!normalized.Any(n => n.Contains(sig, StringComparison.Ordinal))) continue;
            return HookCause(hook);
        }
        // Claude's PowerShell tool starts as cmd /c "chcp 65001 >nul & pwsh -NoProfile …".
        if (stem is "powershell" or "pwsh"
            || (stem == "cmd" && (own.Contains("pwsh", StringComparison.Ordinal) || own.Contains("powershell", StringComparison.Ordinal)
                                  || subtree.Any(p => ProcessFamilies.Stem(p.Name).ToLowerInvariant() is "pwsh" or "powershell"))))
            return Simple(LaunchCauseKind.PowerShellTool);
        if (stem == "rg" || own.StartsWith("rg ", StringComparison.Ordinal)) return Simple(LaunchCauseKind.Search);
        // Claude Code runs git itself (repository status for its context and the prompt), outside any tool call.
        if (stem == "git") return new LaunchCause("claude-git", LaunchCauseKind.Other, "git run by Claude Code itself", NoHooks);
        if (stem is "bash" or "sh" or "cmd" or "zsh" && InnerCommand(launch.CommandLine) is { Length: > 0 } inner)
        {
            // The same status line under another config dir spells its path differently; its command name still matches.
            if (CatalogHooks.FirstOrDefault(h => h.IsStatusLine && InnerCommand(h.Command) == inner) is { } statusLine)
                return HookCause(statusLine);
            return new LaunchCause("cmd:" + inner, LaunchCauseKind.Command, inner, NoHooks);
        }
        if (_firstTick.TryGetValue(launch.Id, out int first) && _lastTick[launch.Id] - first >= TicksFor(TimeSpan.FromSeconds(60)))
            return new LaunchCause("long:" + ToolLabel(launch), LaunchCauseKind.LongRunning, ToolLabel(launch), NoHooks);
        return new LaunchCause("other:" + stem, LaunchCauseKind.Other, ToolLabel(launch), NoHooks);
    }

    private readonly Dictionary<string, LaunchCause> _hookCauses = new();

    // Every registration of the same command from the same source is one cause (one hook on 21 events is still one
    // thing to change).
    private LaunchCause HookCause(HookDef hook)
    {
        string key = (hook.IsStatusLine ? "status:" : "hook:") + hook.Source + "|" + Normalize(hook.Command);
        if (_hookCauses.TryGetValue(key, out var cause)) return cause;
        var all = CatalogHooks
            .Where(h => h.Source == hook.Source && h.IsStatusLine == hook.IsStatusLine && Normalize(h.Command) == Normalize(hook.Command))
            .ToList();
        string what = InnerCommand("-c " + hook.Command) ?? ProcessFamilies.Stem(hook.Command);
        cause = hook.IsStatusLine
            ? new LaunchCause(key, LaunchCauseKind.StatusLine, $"Status line ({what})", all)
            : new LaunchCause(key, LaunchCauseKind.Hook, $"{what} hook · {hook.Source}", all);
        _hookCauses[key] = cause;
        return cause;
    }

    private static LaunchCause Simple(LaunchCauseKind kind) => kind switch
    {
        LaunchCauseKind.BashTool => new("bash-tool", kind, "Bash tool commands", NoHooks),
        LaunchCauseKind.PowerShellTool => new("powershell-tool", kind, "PowerShell tool commands", NoHooks),
        LaunchCauseKind.Search => new("search", kind, "Claude's file search (rg)", NoHooks),
        _ => new(kind.ToString(), kind, kind.ToString(), NoHooks),
    };

    /// <summary>A command line in one comparable form: the profile folder masked, escaped quotes and quotes dropped,
    /// separators forward, lower case, whitespace collapsed. Hook registrations and recorded processes spell the same
    /// command differently (quoting, slashes, Git Bash wrappers); this is what they share.</summary>
    internal static string Normalize(string commandLine)
    {
        if (string.IsNullOrEmpty(commandLine)) return "";
        var s = CommandLineRedactor.Redact(commandLine)
            .Replace("\\\"", "", StringComparison.Ordinal)
            .Replace("^", "", StringComparison.Ordinal)
            .Replace('\\', '/')
            .Replace("\"", "", StringComparison.Ordinal)
            .Replace("'", "", StringComparison.Ordinal)
            .ToLowerInvariant();
        s = MultiSlash().Replace(s, "/");
        return Whitespace().Replace(s, " ").Trim();
    }

    // The part of a hook command that identifies it inside a longer process command line.
    private static string Signature(string hookCommand)
    {
        var n = Normalize(hookCommand);
        return n.Length > 100 ? n[..100] : n;
    }

    /// <summary>What a shell was asked to run, as a short name: <c>bash -c "…/gk.exe ai hook run --host x"</c> →
    /// <c>gk ai hook run</c>; <c>node ~/.claude/statusline.mjs</c> → <c>node statusline.mjs</c>.</summary>
    internal static string? InnerCommand(string? commandLine)
    {
        if (string.IsNullOrWhiteSpace(commandLine)) return null;
        var n = Normalize(commandLine);
        var m = ShellFlag().Match(n);
        var body = m.Success ? n[(m.Index + m.Length)..] : n;
        var tokens = body.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Select(t => t.Trim('/', ';', '(', ')'))
            .Where(t => t.Length > 0)
            .ToList();
        if (tokens.Count == 0) return null;
        static string Leaf(string t) => (t.Contains('/') ? t[(t.LastIndexOf('/') + 1)..] : t).TrimEnd(';');
        // F="${ROOT}/scripts/flow.sh"; if [ -f "$F" ] … names its script in an assignment: that value is the command.
        string first = tokens[0];
        bool assignment = first.Contains('=') && first.IndexOf('=') < (first.Contains('/') ? first.IndexOf('/') : int.MaxValue);
        string head = Leaf(assignment ? first[(first.IndexOf('=') + 1)..] : first);
        if (head.EndsWith(".exe", StringComparison.Ordinal)) head = head[..^4];
        var words = new List<string> { head };
        if (assignment || first.EndsWith(';')) return head;
        foreach (var t in tokens.Skip(1))
        {
            if (words.Count == 4 || t.StartsWith('-') || t.Contains('=') || t is "&&" or "||" or "|" or ";" or "&") break;
            words.Add(Leaf(t));
            if (t.EndsWith(';')) break;
        }
        return string.Join(' ', words);
    }

    // The program a command runs, by its file name: robust to unquoted paths with spaces ("…/Perch (Dev)/bin/x.exe").
    internal static string ProgramOf(string command)
    {
        var m = ProgramFile().Match(Normalize(command));
        return m.Success ? m.Groups[1].Value : InnerCommand("-c " + command)?.Split(' ')[0] ?? "";
    }

    [GeneratedRegex(@"([\w.-]+)\.(?:exe|cmd|bat|ps1|sh|js|mjs|cjs|py)\b")]
    private static partial Regex ProgramFile();

    [GeneratedRegex(@"(?:^|\s)[-/]c\s")]
    private static partial Regex ShellFlag();

    [GeneratedRegex(@"/{2,}")]
    private static partial Regex MultiSlash();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();

    // ── Activity ───────────────────────────────────────────────────────────────

    /// <summary>The sessions with tool calls in the recording, in the order of <see cref="ToolCallsPerMinute"/>'s columns.</summary>
    public IReadOnlyList<ResourceGroup> ActivitySessions { get; private set; } = [];

    /// <summary>[tick][session] tool calls in the minute up to that tick, sub-agents included.</summary>
    public IReadOnlyList<double[]> ToolCallsPerMinute { get; private set; } = [];

    /// <summary>[tick][session] distinct agents (main + sub-agents) that made a tool call in the minute up to that tick.</summary>
    public IReadOnlyList<int[]> AgentsPerMinute { get; private set; } = [];

    /// <summary>Processes first seen at each tick: the recorder's count when it has one, else the session-tree processes
    /// first sampled then. A lower bound either way.</summary>
    public IReadOnlyList<int> Launches { get; private set; } = [];

    private void BuildActivity()
    {
        int n = _recording.Ticks.Count;
        var fallback = new int[n];
        foreach (var t in _firstTick.Values) if (t > 0) fallback[t]++;
        Launches = Enumerable.Range(0, n).Select(i => _recording.Ticks[i].NewProcesses ?? fallback[i]).ToList();

        if (_context.Activity is not { Count: > 0 } activity) return;
        var sessions = new List<(ResourceGroup group, IReadOnlyList<ToolCall> calls)>();
        foreach (var s in _recording.Sessions.DistinctBy(s => s.SessionId))
        {
            if (!activity.TryGetValue(s.SessionId, out var a) || a.Calls.Count == 0) continue;
            sessions.Add((SessionGroup(s.Pid), a.Calls));
        }
        ActivitySessions = sessions.Select(s => s.group).ToList();

        var calls = new List<double[]>(n);
        var agents = new List<int[]>(n);
        var cursors = new int[sessions.Count];
        var starts = new int[sessions.Count];
        for (int i = 0; i < n; i++)
        {
            var at = _recording.Ticks[i].AtUtc;
            var c = new double[sessions.Count];
            var g = new int[sessions.Count];
            for (int s = 0; s < sessions.Count; s++)
            {
                var list = sessions[s].calls;
                while (cursors[s] < list.Count && list[cursors[s]].AtUtc <= at) cursors[s]++;
                while (starts[s] < cursors[s] && list[starts[s]].AtUtc <= at.AddMinutes(-1)) starts[s]++;
                c[s] = cursors[s] - starts[s];
                var seen = new HashSet<string>();
                for (int k = starts[s]; k < cursors[s]; k++) seen.Add(list[k].Agent);
                g[s] = seen.Count;
            }
            calls.Add(c);
            agents.Add(g);
        }
        ToolCallsPerMinute = calls;
        AgentsPerMinute = agents;
    }

    // ── Findings from causes ───────────────────────────────────────────────────

    private IEnumerable<CauseStats> AllCauses => _causesBySession.Values.SelectMany(l => l);

    private List<ResourceFinding> CauseFindings()
    {
        var findings = new List<ResourceFinding>();
        int totalLaunches = Math.Max(1, AllCauses.Where(c => c.Cause.Kind != LaunchCauseKind.Claude).Sum(c => c.Launches));

        // Hooks and hook-like commands, summed across sessions: one plugin is one thing to change.
        foreach (var byCause in AllCauses
                     .Where(c => c.Cause.Kind is LaunchCauseKind.Hook or LaunchCauseKind.Command)
                     .GroupBy(c => c.Cause.Key))
        {
            var cause = byCause.First().Cause;
            int launches = byCause.Sum(c => c.Launches);
            int processes = byCause.Sum(c => c.Processes);
            if (launches < 20) continue;
            double share = (double)launches / totalLaunches;
            long estimated = EstimatedRuns(cause);
            int first = byCause.Min(c => c.FirstTick), last = byCause.Max(c => c.LastTick);
            int peak = PeakLaunchTick(cause.Key, first, last);
            double perRun = (double)processes / launches;
            string sessions = string.Join(", ", byCause.OrderByDescending(c => c.Launches).Take(3)
                .Select(c => $"{SessionLabel(c.SessionKey)} {c.Launches}"));

            if (cause.Kind == LaunchCauseKind.Hook && cause.Hook is { } hook)
            {
                var events = cause.Hooks.Select(h => h.Event).Distinct().ToList();
                bool everyTool = cause.Hooks.Any(h => h.Event is "PreToolUse" or "PostToolUse" && (h.Matcher is "" or "*"));
                string reg = $"Registered on {events.Count} event{(events.Count == 1 ? "" : "s")} " +
                             $"({string.Join(", ", events.Take(5))}{(events.Count > 5 ? ", …" : "")})" +
                             (everyTool ? ", for every tool, so it runs before and after each tool call of every sub-agent" : "") + ".";
                string detail = $"{reg} Seen starting {launches}+ times ({perRun:0.#} processes each, {share:P0} of everything your " +
                                $"sessions launched): {sessions}." +
                                (estimated > launches ? $" From the tool calls in the transcripts it ran about {estimated:N0} times; most were too quick to catch." : "");
                string advice = hook.Kind == HookSourceKind.Plugin
                    ? $"If you don't rely on what {hook.Source} does, disable it: /plugin → {hook.PluginId?.Split('@')[0] ?? hook.Source} → Disable " +
                      "(or remove it from enabledPlugins). If you need it, ask its maintainer for a narrower matcher; " +
                      "every run is a process start, which Windows and your security software both pay for."
                    : "Narrow the matcher to the tools it's for (e.g. \"Edit|Write\") or drop events you don't need. " +
                      "If it's written as bash -c \"…\", call the program directly: that saves a shell and a console per run.";
                findings.Add(new ResourceFinding(FindingKind.HookFanOut,
                    share >= 0.3 || launches >= 100 ? FindingSeverity.Warning : FindingSeverity.Info,
                    first, last, peak, $"{cause.Label}: {(estimated > launches ? $"~{estimated:N0}" : $"{launches}+")} runs", detail)
                {
                    Advice = advice,
                    ActionPath = hook.File,
                    ActionLabel = hook.Kind == HookSourceKind.Plugin ? "Open the plugin's hooks.json" : "Open the settings file",
                    HookCauseKey = cause.Key,
                });
            }
            else
            {
                findings.Add(new ResourceFinding(FindingKind.HookFanOut,
                    share >= 0.3 || launches >= 100 ? FindingSeverity.Warning : FindingSeverity.Info,
                    first, last, peak, $"\"{cause.Label}\" started {launches}+ times",
                    $"Claude ran it through a shell {launches}+ times ({perRun:0.#} processes each, {share:P0} of everything your " +
                    $"sessions launched): {sessions}. It isn't a Bash tool call, so it's most likely a hook.")
                {
                    Advice = "Run /hooks in Claude Code to see which hook runs it. If you don't need it, remove it or narrow its " +
                             "matcher; each run is a process start, which Windows and your security software both pay for.",
                    HookCauseKey = cause.Key,
                });
            }
        }

        foreach (var byCause in AllCauses.Where(c => c.Cause.Kind == LaunchCauseKind.StatusLine).GroupBy(c => c.Cause.Key))
        {
            var cause = byCause.First().Cause;
            int launches = byCause.Sum(c => c.Launches);
            if (launches < 20) continue;
            int first = byCause.Min(c => c.FirstTick), last = byCause.Max(c => c.LastTick);
            double perRun = (double)byCause.Sum(c => c.Processes) / launches;
            findings.Add(new ResourceFinding(FindingKind.StatusLine, FindingSeverity.Info, first, last,
                PeakLaunchTick(cause.Key, first, last), $"{cause.Label}: {launches}+ runs",
                $"Claude Code reruns the status line as the conversation updates, in every session; each run here started " +
                $"{perRun:0.#} processes.")
            {
                Advice = "A script that starts a runtime (node, python) pays that start-up every refresh. A compiled program, or a " +
                         "script that caches its slow parts between runs, is far cheaper; or turn the status line off (statusLine " +
                         "in settings.json).",
                ActionPath = cause.Hook?.File,
                ActionLabel = "Open the settings file",
                HookCauseKey = cause.Key,
            });
        }

        // The same program registered twice for the same event in the user's own files (e.g. two copies of a hook
        // installer): every event runs both.
        if (CatalogHooks.Count > 0)
        {
            foreach (var dup in CatalogHooks
                         .Where(h => h.Kind != HookSourceKind.Plugin && !h.IsStatusLine)
                         .GroupBy(h => (h.Event, exe: ProgramOf(h.Command)))
                         .Where(g => g.Key.exe.Length > 0 && g.Select(h => Normalize(h.Command)).Distinct().Count() > 1)
                         .GroupBy(g => g.Key.exe))
            {
                var defs = dup.SelectMany(g => g).ToList();
                var events = dup.Select(g => g.Key.Event).Distinct().ToList();
                var commands = defs.Select(h => h.Command).Distinct().ToList();
                findings.Add(new ResourceFinding(FindingKind.DuplicateHook, FindingSeverity.Info, 0, Math.Max(0, TickCount - 1), 0,
                    $"{dup.Key} is registered {commands.Count} times",
                    $"{string.Join(" and ", commands.Select(c => $"`{c}`"))} are both hooked to {string.Join(", ", events)}, " +
                    "so each of those events starts it twice.")
                {
                    Advice = "Keep the one you use and remove the other from the settings file. (Two copies usually means two " +
                             "installs registered themselves, e.g. a dev build beside the installed one.)",
                    ActionPath = defs[0].File,
                    ActionLabel = "Open the settings file",
                });
            }
        }

        // Builds and package managers started over and over from the Bash tool.
        var buildExes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["dotnet"] = "Building once and then testing with --no-build, keeping builds incremental, and running " +
                         "`dotnet build-server shutdown` when you're done all cut this.",
            ["msbuild"] = "Building once and then testing with --no-build, keeping builds incremental, and running " +
                          "`dotnet build-server shutdown` when you're done all cut this.",
            ["npm"] = "Each npm run starts node and its scripts afresh; ask Claude to batch checks into one run.",
            ["npx"] = "npx resolves the package each time; a locally installed script runs faster.",
            ["tsc"] = "tsc --watch, or one build for several checks, avoids a cold compile each time.",
            ["gradle"] = "Keep the Gradle daemon running and avoid clean builds between checks.",
            ["mvn"] = "Avoid clean builds between checks; Maven's start-up is the cost here.",
            ["cargo"] = "Keep builds incremental and avoid cargo clean between checks.",
        };
        foreach (var st in AllCauses.Where(c => c.Cause.Kind is LaunchCauseKind.BashTool or LaunchCauseKind.PowerShellTool or LaunchCauseKind.Other))
        {
            foreach (var (exe, count) in st.Exes)
            {
                if (count < 10 || !buildExes.TryGetValue(exe, out var tip)) continue;
                findings.Add(new ResourceFinding(FindingKind.BuildChurn, FindingSeverity.Info, st.FirstTick, st.LastTick, st.FirstTick,
                    $"{exe} started {count}+ times in {SessionLabel(st.SessionKey)}",
                    $"From {st.Cause.Label.ToLowerInvariant()}; {st.CpuSeconds:N0} CPU-seconds seen across them.", st.SessionKey)
                {
                    Advice = $"Repeated cold starts are the cost. {tip}",
                });
            }
        }
        return findings;
    }

    // Hook runs implied by the transcripts: each tool call fires every PreToolUse/PostToolUse registration of this
    // cause whose matcher takes the tool.
    private long EstimatedRuns(LaunchCause cause)
    {
        if (_context.Activity is not { Count: > 0 } activity || cause.Hooks.Count == 0) return 0;
        var perTool = cause.Hooks.Where(h => h.Event is "PreToolUse" or "PostToolUse").ToList();
        if (perTool.Count == 0) return 0;
        var cache = new Dictionary<string, int>();
        long runs = 0;
        foreach (var a in activity.Values)
            foreach (var call in a.Calls)
            {
                if (!cache.TryGetValue(call.Tool, out int n))
                    cache[call.Tool] = n = perTool.Count(h => h.MatchesTool(call.Tool));
                runs += n;
            }
        return runs;
    }

    private int PeakLaunchTick(string causeKey, int first, int last)
    {
        var counts = new Dictionary<int, int>();
        foreach (var (id, cause) in _causeOfProc)
            if (cause.Key == causeKey && _firstTick.TryGetValue(id, out int t))
                counts[t] = counts.GetValueOrDefault(t) + 1;
        return counts.Count == 0 ? first : counts.MaxBy(kv => kv.Value).Key;
    }

    private string SessionLabel(string sessionKey) =>
        _recording.Sessions.FirstOrDefault(s => "session:" + s.Pid == sessionKey)?.Label is { Length: > 0 } label ? label : sessionKey;

    // Many sub-agents at once: every one of them runs tools, and every tool call runs the hooks.
    private List<ResourceFinding> SubagentFanOut()
    {
        var findings = new List<ResourceFinding>();
        if (ActivitySessions.Count == 0) return findings;
        int minTicks = TicksFor(TimeSpan.FromSeconds(30));
        for (int s = 0; s < ActivitySessions.Count; s++)
        {
            int col = s;
            foreach (var (start, end) in Runs(i => AgentsPerMinute[i][col] >= 4, minTicks))
            {
                int peak = PeakOf(start, end, i => ToolCallsPerMinute[i][col]);
                int agents = Enumerable.Range(start, end - start + 1).Max(i => AgentsPerMinute[i][col]);
                double callsPeak = ToolCallsPerMinute[peak][col];
                double cpuIn = Enumerable.Range(start, end - start + 1).Average(i => _recording.Ticks[i].Cpu);
                var outside = Enumerable.Range(0, TickCount).Where(i => i < start || i > end).ToList();
                double cpuOut = outside.Count > 0 ? outside.Average(i => _recording.Ticks[i].Cpu) : cpuIn;
                double launchesIn = Enumerable.Range(start, end - start + 1).Average(i => (double)Launches[i]) * (60 / TickInterval.TotalSeconds);
                int hooksPerCall = HooksPerToolCall(ActivitySessions[col].Key);
                var group = ActivitySessions[col];
                findings.Add(new ResourceFinding(FindingKind.SubagentFanOut,
                    cpuIn >= 60 || cpuIn - cpuOut >= 20 ? FindingSeverity.Warning : FindingSeverity.Info,
                    start, end, peak,
                    $"{group.Label.Replace("Session · ", "", StringComparison.Ordinal)} ran {agents} agents at once",
                    $"Up to {callsPeak:0} tool calls a minute for {ResourceFormat.Duration(TickInterval * (end - start + 1))}; CPU averaged " +
                    $"{cpuIn:0}% then against {cpuOut:0}% otherwise, with about {launchesIn:0} new processes seen a minute" +
                    (hooksPerCall > 0 ? $". Each tool call also runs {hooksPerCall} hook{(hooksPerCall == 1 ? "" : "s")}." : "."),
                    group.Key)
                {
                    Advice = "On a busy machine, ask for fewer agents at once (\"use at most 3 sub-agents\"), or run the fan-out when " +
                             "the machine is quieter. Trimming the hooks that run on every tool call shrinks the cost of each agent.",
                });
            }
        }
        return findings;
    }

    private int HooksPerToolCall(string sessionKey)
    {
        var hooks = CausesFor(sessionKey).Where(c => c.Cause.Kind == LaunchCauseKind.Hook && c.Launches > 0)
            .SelectMany(c => c.Cause.Hooks).Where(h => h.Event is "PreToolUse" or "PostToolUse").ToList();
        return hooks.Count(h => h.MatchesTool("Bash"));
    }

    // The CPU no process owns, when it tracks launches, is processes too short-lived to sample.
    private List<ResourceFinding> ShortLivedProcesses()
    {
        if (TickCount < 12 || Launches.Count != TickCount) return [];
        int band = _chartIndex[UnattributedGroup.Key];
        int window = TicksFor(TimeSpan.FromSeconds(60));
        // Single 5 s samples are too noisy to compare (a burst of launches lands a tick before or after its CPU), so
        // both series are compared as one-minute averages.
        var y = Rolling(Enumerable.Range(0, TickCount).Select(i => Cpu[i][band]).ToArray(), window);
        var x = Rolling(Launches.Select(v => (double)v).ToArray(), window);
        double avg = y.Average();
        double r = Correlation(x, y);
        if (avg < 4 || r < 0.4) return [];

        // The kernel's own CPU (security and file-system drivers working on each new process) often follows too.
        var kernel = Rolling(Enumerable.Range(0, TickCount).Select(i => _recording.Ticks[i].Procs
            .Where(s => _recording.Procs.TryGetValue(s.Id, out var p) && p.Pid == 4).Sum(s => s.Cpu)).ToArray(), window);
        double rKernel = Correlation(x, kernel);
        string kernelNote = rKernel >= 0.4 && kernel.Average() >= 3
            ? $" The Windows kernel (System) tracked them too (correlation {rKernel:0.00}, ~{kernel.Average():0}% CPU): that's " +
              "drivers, security scanning above all, working on each new process."
            : "";

        int peak = PeakOf(0, TickCount - 1, i => y[i]);
        double perMinute = 60 / TickInterval.TotalSeconds;
        int from = Math.Max(0, peak - window), to = Math.Min(TickCount - 1, peak + window);
        double peakLaunches = Enumerable.Range(from, to - from + 1).Average(i => x[i]) * perMinute;
        var top = AllCauses
            .Where(c => c.Cause.Kind != LaunchCauseKind.Claude)
            .GroupBy(c => c.Cause.Label)
            .Select(g => (label: g.Key, launches: g.Sum(c => c.Launches)))
            .OrderByDescending(g => g.launches)
            .Take(3)
            .ToList();
        return
        [
            new ResourceFinding(FindingKind.ShortLivedProcesses, avg >= 10 ? FindingSeverity.Warning : FindingSeverity.Info,
                0, TickCount - 1, peak,
                $"~{avg:0}% of CPU went to processes too short-lived to sample",
                $"The CPU no process owns rose and fell with process launches (correlation {r:0.00}), peaking at {y[peak]:0}% " +
                $"with about {peakLaunches:0} new processes seen a minute.{kernelNote} " +
                (top.Count > 0 ? "Most launches came from: " + string.Join(", ", top.Select(t => $"{t.label} ({t.launches}+)")) + "." : ""),
                UnattributedGroup.Key)
            {
                Advice = "Cut the launches rather than the programs: see the hook, status line and sub-agent findings for where they come from.",
            },
        ];
    }

    // Trailing average over the last `window` values.
    private static double[] Rolling(double[] values, int window)
    {
        var result = new double[values.Length];
        double sum = 0;
        for (int i = 0; i < values.Length; i++)
        {
            sum += values[i];
            if (i >= window) sum -= values[i - window];
            result[i] = sum / Math.Min(i + 1, window);
        }
        return result;
    }

    private static double Correlation(double[] x, double[] y)
    {
        double mx = x.Average(), my = y.Average(), sxy = 0, sxx = 0, syy = 0;
        for (int i = 0; i < x.Length; i++)
        {
            sxy += (x[i] - mx) * (y[i] - my);
            sxx += (x[i] - mx) * (x[i] - mx);
            syy += (y[i] - my) * (y[i] - my);
        }
        return sxx <= 0 || syy <= 0 ? 0 : sxy / Math.Sqrt(sxx * syy);
    }
}
