namespace Perch.Data.Resources;

/// <summary>One hook run: the event that fired it, when, in which session and agent, for which tool. <see cref="Seen"/>
/// is set when the sampler caught its process (and gives the pid); <see cref="DurationMs"/> and <see cref="Error"/> when
/// Claude Code recorded the run.</summary>
public sealed record HookRun(
    DateTime AtUtc,
    string SessionKey,
    string SessionLabel,
    string Agent,
    string Event,
    string? Tool,
    ObservedLaunch? Seen,
    int? DurationMs,
    string? Error)
{
    /// <summary>The event was inferred from the nearest transcript moment rather than a registration for it: the hook
    /// isn't in any catalog (removed since), so only its timing says what fired it.</summary>
    public bool Inferred { get; init; }
}

/// <summary>One hook's runs for one event (or <see cref="HookActivity.Unidentified"/>: processes caught with no event
/// near enough to pin them on).</summary>
public sealed record HookEventStats(
    string Event,
    IReadOnlyList<string> Matchers,
    int Runs,
    int Seen,
    int Errors,
    IReadOnlyList<(string Name, int Runs)> ByTool,
    IReadOnlyList<(string Name, int Runs)> ByAgent,
    double? AverageMs,
    IReadOnlyList<HookRun> Recent)
{
    /// <summary>Every run here was pinned on this event by timing alone (see <see cref="HookRun.Inferred"/>).</summary>
    public bool Inferred => Recent.Count > 0 && Recent.All(r => r.Inferred);
}

/// <summary>Everything one hook did in a recording, by event, with what to change.</summary>
public sealed record HookReport(
    LaunchCause Cause,
    int Runs,
    int Seen,
    int Errors,
    double CpuSecondsSeen,
    IReadOnlyList<HookEventStats> Events,
    IReadOnlyList<(string Message, int Count)> ErrorMessages,
    IReadOnlyList<int[]> PerMinute,
    IReadOnlyList<string> PerMinuteEvents,
    DateTime StartUtc,
    IReadOnlyList<string> Advice);

/// <summary>
/// Reconstructs hook runs for the hooks page (docs/resource-diagnostics-plan.md, "Hooks page"). Claude Code doesn't log
/// successful hook runs, so a run is <em>expected</em> wherever a transcript shows its event (a tool call, its result,
/// a prompt, a turn ending, a sub-agent starting or stopping…) and the hook is registered for that event and tool.
/// Hook processes the sampler caught are matched to the nearest expected run by their exact start time, which is what
/// pins a process with one command line on 21 events to the event that ran it. Pure: built from an analysis and its
/// context.
/// </summary>
public static class HookActivity
{
    public const string Unidentified = "Event not identified";

    // A hook starts just after its event is written (PreToolUse after the tool_use, PostToolUse around the result).
    private static readonly TimeSpan Before = TimeSpan.FromSeconds(3), After = TimeSpan.FromSeconds(6);

    private static readonly HashSet<string> ToolEvents = new(StringComparer.Ordinal)
        { "PreToolUse", "PostToolUse", "PostToolUseFailure", "PermissionRequest", "PermissionDenied" };

    // Tools that only read: a hook that guards or records changes rarely needs to see them.
    private static readonly HashSet<string> ReadOnlyTools = new(StringComparer.Ordinal)
        { "Read", "Grep", "Glob", "LS", "WebFetch", "WebSearch", "TodoWrite", "ToolSearch", "NotebookRead", "TaskOutput", "BashOutput" };

    public static IReadOnlyList<HookReport> Build(ResourceAnalysis analysis, ResourceContext context)
    {
        var recording = analysis.Recording;
        var hooks = analysis.Hooks.Where(h => !h.IsStatusLine).ToList();
        if (hooks.Count == 0 && analysis.ObservedLaunches.All(l => l.Cause.Kind is not (LaunchCauseKind.Hook or LaunchCauseKind.Command))) return [];

        var sessionKeyById = recording.Sessions
            .GroupBy(s => s.SessionId)
            .ToDictionary(g => g.Key, g => (key: "session:" + g.First().Pid, label: g.First().Label));
        var seenByCause = analysis.ObservedLaunches
            .Where(l => l.Cause.Kind is LaunchCauseKind.Hook or LaunchCauseKind.Command)
            .GroupBy(l => (l.SessionKey, l.Cause.Key))
            .ToDictionary(g => g.Key, g => g.ToList());

        // Expected runs: every moment × every registered hook for its event (and tool) that applies to the session.
        var runs = new Dictionary<string, List<HookRun>>();
        var causes = new Dictionary<string, LaunchCause>();
        foreach (var (sessionId, activity) in context.Activity ?? new Dictionary<string, SessionActivity>())
        {
            if (!sessionKeyById.TryGetValue(sessionId, out var session)) continue;
            var applicable = hooks.Where(h => Applies(h, session.key, activity, analysis, seenByCause)).ToList();
            foreach (var m in activity.Moments)
            {
                foreach (var h in applicable)
                {
                    if (h.Event != m.Event) continue;
                    if (ToolEvents.Contains(m.Event) && m.Tool is { } tool && !h.MatchesTool(tool)) continue;
                    var cause = analysis.CauseForHook(h);
                    causes[cause.Key] = cause;
                    if (!runs.TryGetValue(cause.Key, out var list)) runs[cause.Key] = list = new();
                    list.Add(new HookRun(m.AtUtc, session.key, session.label, m.Agent, m.Event, m.Tool, null, null, null));
                }
            }

            // Runs Claude Code recorded: Stop durations and failures, attached to the run they belong to.
            foreach (var t in activity.Traces)
            {
                var h = hooks.FirstOrDefault(x => ResourceAnalysis.Normalize(x.Command) == ResourceAnalysis.Normalize(t.Command));
                // A hook no catalog knows (removed since) is still the command its caught processes ran.
                var cause = h is not null
                    ? analysis.CauseForHook(h)
                    : analysis.ObservedLaunches.Select(l => l.Cause)
                        .FirstOrDefault(c => c.Kind == LaunchCauseKind.Command && c.Key == "cmd:" + ResourceAnalysis.InnerCommand("-c " + t.Command));
                if (cause is null) continue;
                causes[cause.Key] = cause;
                if (!runs.TryGetValue(cause.Key, out var list)) runs[cause.Key] = list = new();
                int best = -1;
                for (int i = 0; i < list.Count; i++)
                {
                    var r = list[i];
                    if (r.SessionKey != session.key || r.Event != t.Event || r.DurationMs is not null || r.Error is not null) continue;
                    if ((r.AtUtc - t.AtUtc).Duration() > TimeSpan.FromSeconds(30)) continue;
                    if (best < 0 || (r.AtUtc - t.AtUtc).Duration() < (list[best].AtUtc - t.AtUtc).Duration()) best = i;
                }
                if (best >= 0) list[best] = list[best] with { DurationMs = t.DurationMs, Error = t.Error };
                else list.Add(new HookRun(t.AtUtc, session.key, session.label, t.Agent, t.Event, null, null, t.DurationMs, t.Error));
            }
        }

        // Caught processes, matched to the nearest unmatched run of the same hook in the same session; failing that (a
        // hook no catalog knows), to the nearest event of any kind in that session's transcript, which is the best
        // evidence of what fired it.
        var activityByKey = (context.Activity ?? new Dictionary<string, SessionActivity>())
            .Where(kv => sessionKeyById.ContainsKey(kv.Key))
            .ToDictionary(kv => sessionKeyById[kv.Key].key, kv => kv.Value);
        foreach (var ((sessionKey, causeKey), seen) in seenByCause)
        {
            var cause = seen[0].Cause;
            causes[causeKey] = cause;
            if (!runs.TryGetValue(causeKey, out var list)) runs[causeKey] = list = new();
            string label = recording.Sessions.FirstOrDefault(s => "session:" + s.Pid == sessionKey)?.Label ?? sessionKey;
            var moments = activityByKey.TryGetValue(sessionKey, out var act) ? act.Moments : [];
            foreach (var launch in seen)
            {
                if (launch.StartUtc is not { } start) continue;
                int best = -1;
                for (int i = 0; i < list.Count; i++)
                {
                    var r = list[i];
                    if (r.SessionKey != sessionKey || r.Seen is not null) continue;
                    if (start < r.AtUtc - Before || start > r.AtUtc + After) continue;
                    if (best < 0 || (start - r.AtUtc).Duration() < (start - list[best].AtUtc).Duration()) best = i;
                }
                if (best >= 0)
                {
                    list[best] = list[best] with { Seen = launch };
                    continue;
                }
                HookMoment? nearest = null;
                foreach (var m in moments)
                {
                    if (start < m.AtUtc - Before || start > m.AtUtc + After) continue;
                    if (nearest is null || (start - m.AtUtc).Duration() < (start - nearest.Value.AtUtc).Duration()) nearest = m;
                }
                list.Add(nearest is { } n
                    ? new HookRun(n.AtUtc, sessionKey, label, n.Agent, n.Event, n.Tool, launch, null, null) { Inferred = true }
                    : new HookRun(start, sessionKey, label, "?", Unidentified, null, launch, null, null));
            }
        }

        var startUtc = recording.Ticks.Count > 0 ? recording.Ticks[0].AtUtc : DateTime.UtcNow;
        var endUtc = recording.Ticks.Count > 0 ? recording.Ticks[^1].AtUtc : startUtc;
        return runs
            .Where(kv => kv.Value.Count > 0)
            .Select(kv => Report(causes[kv.Key], kv.Value, hooks, startUtc, endUtc))
            .OrderByDescending(r => r.Runs)
            .ToList();
    }

    // Which registrations fire in a session: the user's own hooks always; a project's only in that project; a plugin's
    // only where its runs were actually caught (an installed plugin may not be enabled there, and counting it anyway
    // would invent runs).
    private static bool Applies(HookDef h, string sessionKey, SessionActivity activity, ResourceAnalysis analysis,
        Dictionary<(string, string), List<ObservedLaunch>> seenByCause)
    {
        switch (h.Kind)
        {
            case HookSourceKind.UserSettings:
                return true;
            case HookSourceKind.ProjectSettings:
                if (activity.Cwd is not { Length: > 0 } cwd) return false;
                var dir = ResourceAnalysis.Normalize(cwd).TrimEnd('/');
                return ResourceAnalysis.Normalize(h.File).StartsWith(dir + "/.claude/", StringComparison.Ordinal);
            default:
                return seenByCause.ContainsKey((sessionKey, analysis.CauseForHook(h).Key))
                       || activity.Traces.Any(t => ResourceAnalysis.Normalize(t.Command) == ResourceAnalysis.Normalize(h.Command));
        }
    }

    private static HookReport Report(LaunchCause cause, List<HookRun> runs, List<HookDef> hooks, DateTime startUtc, DateTime endUtc)
    {
        runs.Sort((a, b) => a.AtUtc.CompareTo(b.AtUtc));
        var registrations = hooks.Where(h => cause.Hooks.Any(c => c.Source == h.Source
                                                                  && ResourceAnalysis.Normalize(c.Command) == ResourceAnalysis.Normalize(h.Command)))
            .ToList();

        var events = runs
            .GroupBy(r => r.Event)
            .Select(g =>
            {
                var list = g.ToList();
                var durations = list.Where(r => r.DurationMs is not null).Select(r => (double)r.DurationMs!.Value).ToList();
                return new HookEventStats(
                    g.Key,
                    registrations.Where(h => h.Event == g.Key).Select(h => h.Matcher).Distinct().ToList(),
                    list.Count,
                    list.Count(r => r.Seen is not null),
                    list.Count(r => r.Error is not null),
                    list.Where(r => r.Tool is not null).GroupBy(r => r.Tool!).Select(t => (t.Key, t.Count())).OrderByDescending(t => t.Item2).ToList(),
                    list.GroupBy(r => r.SessionLabel + (r.Agent is "main" or "?" ? "" : " · " + ShortAgent(r.Agent)))
                        .Select(t => (t.Key, t.Count())).OrderByDescending(t => t.Item2).ToList(),
                    durations.Count > 0 ? durations.Average() : null,
                    list.TakeLast(200).Reverse().ToList());
            })
            .OrderBy(e => e.Event == Unidentified)
            .ThenByDescending(e => e.Runs)
            .ToList();

        // Runs per minute by event, for the page's strip.
        var eventNames = events.Select(e => e.Event).ToList();
        int minutes = Math.Max(1, (int)Math.Ceiling((endUtc - startUtc).TotalMinutes) + 1);
        var perMinute = Enumerable.Range(0, minutes).Select(_ => new int[eventNames.Count]).ToList();
        foreach (var r in runs)
        {
            int m = (int)Math.Floor((r.AtUtc - startUtc).TotalMinutes);
            if (m < 0 || m >= minutes) continue;
            perMinute[m][eventNames.IndexOf(r.Event)]++;
        }

        var errors = runs.Where(r => r.Error is not null)
            .GroupBy(r => r.Error!)
            .Select(g => (g.Key, g.Count()))
            .OrderByDescending(e => e.Item2)
            .ToList();

        return new HookReport(cause, runs.Count, runs.Count(r => r.Seen is not null), runs.Count(r => r.Error is not null),
            runs.Where(r => r.Seen is not null).Sum(r => r.Seen!.CpuSeconds),
            events, errors, perMinute, eventNames, startUtc, Advice(cause, events, errors));
    }

    private static string ShortAgent(string agent) =>
        agent.StartsWith("agent-", StringComparison.Ordinal) && agent.Length > 12 ? "agent " + agent[6..12] : agent;

    // What the runs say to change, from the counts rather than guesses.
    private static List<string> Advice(LaunchCause cause, List<HookEventStats> events, List<(string Message, int Count)> errors)
    {
        var advice = new List<string>();
        var toolEvents = events.Where(e => e.Event is "PreToolUse" or "PostToolUse" or "PostToolUseFailure").ToList();
        int toolRuns = toolEvents.Sum(e => e.Runs);
        int total = events.Where(e => e.Event != HookActivity.Unidentified).Sum(e => e.Runs);
        if (toolRuns > 0 && total > 0)
        {
            int readOnly = toolEvents.SelectMany(e => e.ByTool).Where(t => ReadOnlyTools.Contains(t.Name)).Sum(t => t.Runs);
            bool anyTool = toolEvents.Any(e => e.Matchers.Any(m => m is "" or "*"));
            advice.Add($"{toolRuns} of {total} runs ({(double)toolRuns / total:P0}) came from tool calls (PreToolUse/PostToolUse)" +
                       (anyTool ? ", registered for every tool." : "."));
            if (readOnly > 0)
                advice.Add($"{readOnly} of those were for tools that only read (Read, Grep, Glob…). If the hook only cares about " +
                           $"changes, a matcher such as \"Bash|Edit|Write|MultiEdit|NotebookEdit\" would skip them: {(double)readOnly / total:P0} fewer runs.");
        }
        var agents = events.SelectMany(e => e.ByAgent).Where(a => a.Name.Contains(" · agent ", StringComparison.Ordinal)).Sum(a => a.Runs);
        if (agents > 0 && total > 0 && agents >= total / 4)
            advice.Add($"{agents} runs ({(double)agents / total:P0}) came from sub-agents: every sub-agent's tool calls run the hook too, " +
                       "so fan-out multiplies it.");
        foreach (var (message, count) in errors.Take(3))
        {
            bool missing = message.Contains("not exist", StringComparison.OrdinalIgnoreCase) || message.Contains("not found", StringComparison.OrdinalIgnoreCase)
                                                                                               || message.Contains("cannot find", StringComparison.OrdinalIgnoreCase);
            advice.Add($"Failed {count}×: \"{message}\"" + (missing
                ? ". Its program or plugin is gone, but sessions started before it was removed keep running it until they restart."
                : "."));
        }
        if (cause.Hook is { Kind: HookSourceKind.Plugin } h)
            advice.Add($"To stop it: /plugin → {h.PluginId?.Split('@')[0] ?? h.Source} → Disable, then restart sessions that are already open.");
        return advice;
    }
}
