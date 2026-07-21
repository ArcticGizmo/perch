using System.Text.Json.Nodes;
using Perch.Data;

namespace Perch.Data;

/// <summary>
/// Discovers the sub-agents (Agent/Task tool invocations) currently running under a session.
///
/// Sub-agents do not get their own <c>~/.claude/sessions/*.json</c> file — they run
/// in the parent's process. The only live record is the parent's transcript at
/// <c>~/.claude/projects/{enc-cwd}/{sessionId}.jsonl</c>, where each sub-agent appears as
/// an assistant <c>tool_use</c> block named "Agent" (or "Task" in older builds). A sub-agent is still running while that
/// tool_use has no matching <c>tool_result</c> yet — a signal that holds even when the
/// sub-agent is sitting in a long, silent shell command (where a file-mtime heuristic would
/// wrongly report it as finished).
///
/// Results are cached per transcript by (length, last-write) so the common case — a scan while
/// a sub-agent runs, during which the parent transcript does not change — costs a stat, not a parse.
/// </summary>
internal sealed class SubAgentReader
{
    // A teammate (or its sub-agents) whose own transcript classifies as mid-turn "working" but hasn't
    // been written to for this long is treated as quiesced rather than working. An interrupt leaves a
    // teammate frozen mid-turn — a dangling tool_use, or a tool_result the lead never answered — which
    // is, record-for-record, indistinguishable from one genuinely in flight; the only tell is that a
    // live agent keeps appending and a frozen one goes silent. The window is generous enough that a
    // teammate streaming tokens never trips it, yet short enough that an interrupted team stops pegging
    // its parent session as Running within a minute or two. Self-healing: if the file grows again the
    // agent flips straight back to working.
    private static readonly TimeSpan DefaultStaleAfter = TimeSpan.FromSeconds(90);

    private readonly TimeSpan _staleAfter;

    // Legacy parent-transcript scan, memoised by the parent transcript's (length, last-write).
    private readonly MtimeCache<IReadOnlyList<SubAgent>> _legacy = new();
    // 2.1+ per-agent turn classification (working/idle + current activity), memoised by each agent
    // file's mtime — the expensive transcript parse only re-runs when that agent's file grows.
    private readonly MtimeCache<Classification> _agentState = new();
    // Per-agent meta sidecar, cached by path: a .meta.json is written once and never changes, so
    // re-reading it every poll (now for idle teammates too, not just running agents) is wasted IO.
    private readonly Dictionary<string, AgentMeta> _meta = new();

    /// <param name="staleAfter">How long an agent's transcript may sit untouched before a "working"
    /// classification is treated as a frozen/interrupted turn. Defaults to 90s; tests override it.</param>
    public SubAgentReader(TimeSpan? staleAfter = null) => _staleAfter = staleAfter ?? DefaultStaleAfter;

    /// <summary>
    /// Returns the sub-agents currently working under the given session as a tree (each
    /// <see cref="SubAgent.Children"/> holds the agents it has itself spawned), or an empty list if the
    /// transcript can't be located or read. The returned list is the session's direct children; deeper
    /// nesting hangs off <see cref="SubAgent.Children"/> and is rare in practice.
    /// </summary>
    public IReadOnlyList<SubAgent> GetRunning(string sessionId, string cwd)
    {
        if (string.IsNullOrEmpty(sessionId))
            return [];

        var path = TranscriptLocator.Resolve(sessionId, cwd);
        if (path == null)
            return [];

        // Claude Code 2.1+ gives every sub-agent its own transcript under
        // {sessionId}/subagents/agent-*.jsonl and resolves the launching tool_use in the PARENT
        // transcript the moment the agent returns its first result — so the parent "open tool_use"
        // heuristic below can't see a sub-agent that is still working, and can never see one that
        // was re-driven via SendMessage. When that directory is present it is the source of truth:
        // a sub-agent is running while its own transcript's latest turn is unfinished.
        try
        {
            var subagentsDir = Path.Combine(Path.GetDirectoryName(path)!, sessionId, "subagents");
            if (Directory.Exists(subagentsDir))
                return ScanBackground(subagentsDir);
        }
        catch
        {
            // Fall through to the legacy parent-transcript scan.
        }

        // Legacy (synchronous Task/Agent) model: a sub-agent is running while its tool_use in the
        // parent transcript has no matching tool_result.
        return _legacy.GetOrCompute(path, Parse, []);
    }

    // The sub-agents under {sessionId}/subagents/ to surface for the parent session, assembled into a
    // parent → child tree. Every agent (however deep) lives flat in this one directory, described by its
    // sibling agent-{id}.meta.json. Two lifecycles share it:
    //   • ordinary sub-agents (Task/Agent) are transient — surfaced only while actively working;
    //   • teammates (Agent Teams) are persistent — surfaced whenever alive, idle or working, so the
    //     roster doesn't flicker as members go quiet between messages from the lead.
    // The turn classification is cached per agent file by (length, last-write) so an unchanged
    // transcript costs a stat, not a parse.
    private IReadOnlyList<SubAgent> ScanBackground(string dir)
    {
        var nowUtc = Clock.UtcNow;

        // Pass 1 — build every *surfaced* agent as a childless node, remembering the tool_use that
        // spawned it (meta.ToolUseId) and the tool_use ids it launched (state.LaunchedIds), the two ends
        // of the parent → child link reconstructed in pass 2.
        var surfaced = new List<AgentNode>();
        foreach (var file in Directory.EnumerateFiles(dir, "agent-*.jsonl"))
        {
            try
            {
                var meta = ReadAgentMeta(Path.ChangeExtension(file, null) + ".meta.json");
                var state = _agentState.GetOrCompute(file, Classify, default);

                // An explicit SubagentStop / TeammateIdle hook marker (written by the perch plugin)
                // retires a "working"-looking tail the instant the turn really ended, instead of waiting
                // out the staleness window — the precise event that replaces the timer guess. It denotes a
                // clean turn-end, not an interrupt, so it idles the agent rather than flagging it stale.
                bool ended = state.Working && HasFreshEndMarker(file);

                // A "working" classification only holds while the transcript is still advancing: an agent
                // left frozen mid-turn by an interrupt keeps that tail forever, so demote it to not-working
                // once its file has gone silent past the staleness window (see DefaultStaleAfter). The
                // explicit marker above wins, so a cleanly-stopped agent never has to wait for this.
                bool stale = state.Working && !ended && IsStale(file, nowUtc);
                bool working = state.Working && !ended && !stale;

                var agentId = AgentIdFromFile(file);
                SubAgent? node = null;
                if (meta.IsTeammate)
                {
                    // Persistent: idle (and stale/interrupted) teammates stay on the roster, just marked
                    // waiting. The stale flag lets SessionMonitor tell an interrupted team from a clean
                    // completion and skip the "done" alert in that case.
                    bool idle = !working;
                    node = new SubAgent(agentId, meta.Description, meta.AgentType,
                        IsTeammate: true, Name: meta.Name, TeamName: meta.TeamName, Color: meta.Color,
                        Activity: idle ? null : state.Activity, IsIdle: idle, IsStale: stale);
                }
                else if (working)
                {
                    // Transient: an ordinary sub-agent only matters while it's still working; a stale one
                    // drops off the roster like any finished one.
                    node = new SubAgent(agentId, meta.Description, meta.AgentType);
                }

                if (node != null)
                    surfaced.Add(new AgentNode(agentId, meta.ToolUseId, state.LaunchedIds ?? [], node));
            }
            catch
            {
                // Skip an agent transcript/meta that vanished or couldn't be read mid-scan.
            }
        }

        return BuildForest(surfaced);
    }

    // A surfaced agent before nesting: its id, the tool_use that spawned it, the Task/Agent tool_use ids
    // it launched (its future children's spawn ids), and the display node itself.
    private readonly record struct AgentNode(
        string AgentId, string? SpawnedBy, IReadOnlyList<string> Launched, SubAgent Node);

    // Pass 2 — reconstruct nesting into a forest of roots. Nested agents live flat in the same directory;
    // the only link is that a child's spawning tool_use (meta.ToolUseId) is the id of a Task/Agent
    // tool_use recorded in its parent's transcript. So map each launched tool_use id to the agent that
    // launched it, wire every agent whose spawn id is owned by another *surfaced* agent up as that agent's
    // child, and return the rest as roots — their spawn id lives in the session transcript, not here, so
    // they are the session's own direct children.
    private static IReadOnlyList<SubAgent> BuildForest(List<AgentNode> surfaced)
    {
        if (surfaced.Count == 0)
            return [];

        var launchOwner = new Dictionary<string, string>();     // launched tool_use id -> launching agentId
        foreach (var a in surfaced)
            foreach (var id in a.Launched)
                launchOwner[id] = a.AgentId;

        var byId = new Dictionary<string, SubAgent>();
        foreach (var a in surfaced)
            byId[a.AgentId] = a.Node;

        var childIds = new Dictionary<string, List<string>>();  // parent agentId -> child agentIds
        var hasParent = new HashSet<string>();
        foreach (var a in surfaced)
        {
            if (a.SpawnedBy != null
                && launchOwner.TryGetValue(a.SpawnedBy, out var parent)
                && parent != a.AgentId
                && byId.ContainsKey(parent))
            {
                if (!childIds.TryGetValue(parent, out var list))
                    childIds[parent] = list = new List<string>();
                list.Add(a.AgentId);
                hasParent.Add(a.AgentId);
            }
        }

        // Assemble top-down, grafting each agent's children via `with`. The visited guard keeps a
        // pathological cycle (shouldn't arise — tool_use ownership is acyclic) from recursing forever.
        var visiting = new HashSet<string>();
        SubAgent Assemble(string id)
        {
            var node = byId[id];
            if (!childIds.TryGetValue(id, out var kids) || !visiting.Add(id))
                return node;
            var built = kids.Select(Assemble).ToList();
            visiting.Remove(id);
            return node with { Children = built };
        }

        var roots = new List<SubAgent>();
        foreach (var a in surfaced)
            if (!hasParent.Contains(a.AgentId))
                roots.Add(Assemble(a.AgentId));
        return roots;
    }

    // True when an agent's transcript hasn't been written to within the staleness window — the signal
    // that a "working" tail is actually frozen (interrupted) rather than in flight. If the file can't be
    // stat'd we don't force it idle; the transcript's own verdict stands.
    private bool IsStale(string file, DateTime nowUtc)
    {
        try { return nowUtc - File.GetLastWriteTimeUtc(file) > _staleAfter; }
        catch { return false; }
    }

    // True when an authoritative "turn ended" marker sits beside this agent's transcript and is at
    // least as new as it. The perch plugin's hooks drop one when Claude Code fires the matching event:
    //   • agent-{id}.stopped — SubagentStop (a sub-agent finished, or a teammate ended its turn)
    //   • agent-{id}.idle    — TeammateIdle  (a teammate went idle, waiting for the lead)
    // The reader treats both identically: the agent has stopped working as of the marker's timestamp.
    // We compare timestamps rather than just existence so a re-tasked teammate self-heals — the lead's
    // new prompt appends to the transcript, pushing its mtime past the (now stale) marker, and the
    // agent flips straight back to working. Best-effort: any IO error just defers to the tail/timer.
    private static bool HasFreshEndMarker(string agentFile)
    {
        try
        {
            var basePath = Path.ChangeExtension(agentFile, null); // …/agent-{id}
            var fileTime = File.GetLastWriteTimeUtc(agentFile);
            foreach (var ext in MarkerExtensions)
            {
                var marker = basePath + ext;
                if (File.Exists(marker) && File.GetLastWriteTimeUtc(marker) >= fileTime)
                    return true;
            }
        }
        catch { }
        return false;
    }

    private static readonly string[] MarkerExtensions = [".stopped", ".idle"];

    // agent-{id}.jsonl -> {id} (the teammate's agentId, or the plain sub-agent's hash).
    private static string AgentIdFromFile(string file)
    {
        var id = Path.GetFileNameWithoutExtension(file);
        return id.StartsWith("agent-", StringComparison.Ordinal) ? id["agent-".Length..] : id;
    }

    // What a sub-agent's meta sidecar tells us. A teammate's meta carries taskKind ==
    // "in_process_teammate" plus a human name/team/colour; an ordinary sub-agent's does not. ToolUseId is
    // the id of the Task/Agent tool_use that spawned this agent — the key that links it to its parent's
    // transcript when reconstructing the tree (see BuildForest); absent for the top-level teammates.
    private readonly record struct AgentMeta(
        string Description, string AgentType, bool IsTeammate, string? Name, string? TeamName, string? Color,
        string? ToolUseId);

    // Reads (and caches) an agent's tiny meta sidecar; blank/non-teammate if it's missing or bad.
    private AgentMeta ReadAgentMeta(string metaPath)
    {
        if (_meta.TryGetValue(metaPath, out var cached))
            return cached;
        try
        {
            if (!File.Exists(metaPath))
                return default; // not cached: the sidecar may still be written by Claude Code

            var node = JsonNode.Parse(File.ReadAllText(metaPath));
            bool isTeammate = node?["taskKind"]?.GetValue<string>() == "in_process_teammate";
            var meta = new AgentMeta(
                Description: node?["description"]?.GetValue<string>() ?? "",
                AgentType: node?["agentType"]?.GetValue<string>() ?? "",
                IsTeammate: isTeammate,
                Name: node?["name"]?.GetValue<string>(),
                TeamName: node?["teamName"]?.GetValue<string>(),
                Color: node?["color"]?.GetValue<string>(),
                ToolUseId: node?["toolUseId"]?.GetValue<string>());
            _meta[metaPath] = meta; // immutable once written — safe to cache forever
            return meta;
        }
        catch
        {
            return default;
        }
    }

    // A sub-agent's turn state, parsed from its own transcript: whether it's mid-turn (Working) with a
    // present-tense Activity phrase, plus the Task/Agent tool_use ids it has launched (LaunchedIds) — the
    // spawn ids of any nested sub-agents, used to reconstruct the tree in BuildForest.
    private readonly record struct Classification(bool Working, string? Activity, IReadOnlyList<string>? LaunchedIds);

    // A sub-agent's transcript ends in a completed assistant turn — a final assistant message with no
    // pending tool call — only while it is idle, waiting for its next instruction. While it is working,
    // the tail is either an assistant tool_use awaiting its result, or a freshly-injected user/tool_result
    // record with no assistant reply yet. We classify off the last assistant/user record (ignoring
    // trailing "system" bookkeeping), which also keeps a long, silent shell command correctly pegged as
    // working rather than guessing from file mtime. When working, the most recent tool_use also yields a
    // present-tense activity phrase ("Reading Foo.cs") for the teammate row.
    private static Classification Classify(string path)
    {
        bool sawTurn = false;
        bool lastWasUser = false;
        bool lastAssistantHadToolUse = false;
        string? lastToolName = null;
        JsonNode? lastToolInput = null;
        List<string>? launched = null;  // Task/Agent tool_use ids this transcript has spawned

        foreach (var line in TranscriptScan.ReadLines(path))
        {
            // Only assistant/user records carry the turn boundary we need; skip the rest (system,
            // summary, file-history snapshots) without the cost of parsing them as JSON.
            if (line.Length == 0 || (!line.Contains("assistant") && !line.Contains("user")))
                continue;

            JsonNode? node;
            try { node = JsonNode.Parse(line); }
            catch { continue; }

            var type = node?["type"]?.GetValue<string>();
            if (type == "user")
            {
                sawTurn = true;
                lastWasUser = true;
            }
            else if (type == "assistant")
            {
                sawTurn = true;
                lastWasUser = false;
                lastAssistantHadToolUse = false;
                if (TranscriptJson.ContentArray(node) is { } content)
                {
                    foreach (var block in content)
                    {
                        if (TranscriptJson.BlockType(block) == "tool_use")
                        {
                            lastAssistantHadToolUse = true;
                            lastToolName = block!["name"]?.GetValue<string>();
                            lastToolInput = block["input"];
                            // A Task/Agent tool_use here spawned a nested sub-agent; remember its id so the
                            // child (whose meta.ToolUseId matches) can be wired up under this agent.
                            if (lastToolName is "Agent" or "Task"
                                && block["id"]?.GetValue<string>() is { } spawnId)
                                (launched ??= new List<string>()).Add(spawnId);
                        }
                    }
                }
            }
        }

        if (!sawTurn)
            return new Classification(false, null, launched);  // nothing yet / just spawned — idle
        bool working = lastWasUser           // an injected prompt or a tool_result awaiting the next step
            || lastAssistantHadToolUse;      // assistant ended on a tool_use -> awaiting its result
        string? activity = working && !string.IsNullOrEmpty(lastToolName)
            ? ToolSummary.Describe(lastToolName!, lastToolInput)
            : null;
        return new Classification(working, activity, launched);
    }

    private static IReadOnlyList<SubAgent> Parse(string path)
    {
        // Collect every Task tool_use and the set of tool_use ids that already have a result;
        // a Task whose id never gets a result is a sub-agent still running.
        var taskUses = new Dictionary<string, (string Desc, string Type)>();
        var resultIds = new HashSet<string>();

        foreach (var line in TranscriptScan.ReadLines(path))
        {
            // Cheap pre-filter: only the (rare) lines that could carry a sub-agent tool_use or
            // any tool_result are worth parsing as JSON. Most transcript lines match neither.
            if (!line.Contains("\"Agent\"") && !line.Contains("\"Task\"") && !line.Contains("tool_result"))
                continue;

            try
            {
                if (TranscriptJson.ContentArray(JsonNode.Parse(line)) is not { } content)
                    continue;

                foreach (var block in content)
                {
                    var type = TranscriptJson.BlockType(block);
                    // The sub-agent launcher is the "Agent" tool (older Claude Code called it "Task").
                    var name = type == "tool_use" ? block!["name"]?.GetValue<string>() : null;
                    if (name is "Agent" or "Task")
                    {
                        var id = block!["id"]?.GetValue<string>();
                        if (id == null)
                            continue;
                        var input = block["input"];
                        var desc = input?["description"]?.GetValue<string>() ?? "";
                        var atype = input?["subagent_type"]?.GetValue<string>() ?? "";
                        taskUses[id] = (desc, atype);
                    }
                    else if (type == "tool_result")
                    {
                        var rid = block!["tool_use_id"]?.GetValue<string>();
                        if (rid != null)
                            resultIds.Add(rid);
                    }
                }
            }
            catch
            {
                // Malformed/partial line (transcripts are appended live) — skip it.
            }
        }

        var running = new List<SubAgent>();
        foreach (var (id, info) in taskUses)
        {
            if (resultIds.Contains(id))
                continue;
            // Store the raw description and type; the overlay row owns the display fallback so both
            // the legacy and background paths read the same.
            running.Add(new SubAgent(id, info.Desc, info.Type));
        }
        return running;
    }
}
