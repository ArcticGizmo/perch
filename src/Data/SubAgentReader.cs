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
    // Legacy parent-transcript scan, memoised by the parent transcript's (length, last-write).
    private readonly MtimeCache<IReadOnlyList<SubAgent>> _legacy = new();
    // 2.1+ per-agent turn classification (working/idle + current activity), memoised by each agent
    // file's mtime — the expensive transcript parse only re-runs when that agent's file grows.
    private readonly MtimeCache<Classification> _agentState = new();
    // Per-agent meta sidecar, cached by path: a .meta.json is written once and never changes, so
    // re-reading it every poll (now for idle teammates too, not just running agents) is wasted IO.
    private readonly Dictionary<string, AgentMeta> _meta = new();

    /// <summary>
    /// Returns the sub-agents currently working under the given session, or an empty list if the
    /// transcript can't be located or read. Only direct children of the session are reported (a
    /// sub-agent's own sub-agents live in that sub-agent's transcript).
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

    // The sub-agents under {sessionId}/subagents/ to surface for the parent session, each described
    // from its sibling agent-{id}.meta.json. Two lifecycles share this directory:
    //   • ordinary sub-agents (Task/Agent) are transient — surfaced only while actively working;
    //   • teammates (Agent Teams) are persistent — surfaced whenever alive, idle or working, so the
    //     roster doesn't flicker as members go quiet between messages from the lead.
    // The turn classification is cached per agent file by (length, last-write) so an unchanged
    // transcript costs a stat, not a parse.
    private IReadOnlyList<SubAgent> ScanBackground(string dir)
    {
        var result = new List<SubAgent>();
        foreach (var file in Directory.EnumerateFiles(dir, "agent-*.jsonl"))
        {
            try
            {
                var meta = ReadAgentMeta(Path.ChangeExtension(file, null) + ".meta.json");
                var state = _agentState.GetOrCompute(file, Classify, default);

                if (meta.IsTeammate)
                {
                    // Persistent: idle teammates stay on the roster, just marked waiting.
                    bool idle = !state.Working;
                    result.Add(new SubAgent(
                        AgentIdFromFile(file), meta.Description, meta.AgentType,
                        IsTeammate: true,
                        Name: meta.Name,
                        TeamName: meta.TeamName,
                        Color: meta.Color,
                        Activity: idle ? null : state.Activity,
                        IsIdle: idle));
                }
                else if (state.Working)
                {
                    // Transient: an ordinary sub-agent only matters while it's still working.
                    result.Add(new SubAgent(AgentIdFromFile(file), meta.Description, meta.AgentType));
                }
            }
            catch
            {
                // Skip an agent transcript/meta that vanished or couldn't be read mid-scan.
            }
        }
        return result;
    }

    // agent-{id}.jsonl -> {id} (the teammate's agentId, or the plain sub-agent's hash).
    private static string AgentIdFromFile(string file)
    {
        var id = Path.GetFileNameWithoutExtension(file);
        return id.StartsWith("agent-", StringComparison.Ordinal) ? id["agent-".Length..] : id;
    }

    // What a sub-agent's meta sidecar tells us. A teammate's meta carries taskKind ==
    // "in_process_teammate" plus a human name/team/colour; an ordinary sub-agent's does not.
    private readonly record struct AgentMeta(
        string Description, string AgentType, bool IsTeammate, string? Name, string? TeamName, string? Color);

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
                Color: node?["color"]?.GetValue<string>());
            _meta[metaPath] = meta; // immutable once written — safe to cache forever
            return meta;
        }
        catch
        {
            return default;
        }
    }

    // A sub-agent's turn state, parsed from the tail of its own transcript.
    private readonly record struct Classification(bool Working, string? Activity);

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
                        }
                    }
                }
            }
        }

        if (!sawTurn)
            return default;                  // nothing yet / just spawned — idle, no work to surface
        bool working = lastWasUser           // an injected prompt or a tool_result awaiting the next step
            || lastAssistantHadToolUse;      // assistant ended on a tool_use -> awaiting its result
        string? activity = working && !string.IsNullOrEmpty(lastToolName)
            ? ToolSummary.Describe(lastToolName!, lastToolInput)
            : null;
        return new Classification(working, activity);
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
