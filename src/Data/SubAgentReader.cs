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
    // 2.1+ per-agent "is this sub-agent still running" check, memoised by each agent file's mtime.
    private readonly MtimeCache<bool> _agentRunning = new();

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

    // The sub-agents under {sessionId}/subagents/ whose own transcript shows an in-progress turn,
    // each described from its sibling agent-{id}.meta.json. The running check is cached per agent
    // file by (length, last-write) so an unchanged transcript costs a stat, not a parse.
    private IReadOnlyList<SubAgent> ScanBackground(string dir)
    {
        var running = new List<SubAgent>();
        foreach (var file in Directory.EnumerateFiles(dir, "agent-*.jsonl"))
        {
            try
            {
                if (!IsAgentRunning(file))
                    continue;

                // agent-{id}.jsonl -> {id}; its description/type live in agent-{id}.meta.json.
                var id = Path.GetFileNameWithoutExtension(file);
                if (id.StartsWith("agent-", StringComparison.Ordinal))
                    id = id["agent-".Length..];

                var (desc, type) = ReadAgentMeta(Path.ChangeExtension(file, null) + ".meta.json");
                running.Add(new SubAgent(id, desc, type));
            }
            catch
            {
                // Skip an agent transcript/meta that vanished or couldn't be read mid-scan.
            }
        }
        return running;
    }

    // Reads description/agentType from an agent's tiny meta sidecar; blanks if it's missing or bad.
    private static (string Desc, string Type) ReadAgentMeta(string metaPath)
    {
        try
        {
            if (!File.Exists(metaPath))
                return ("", "");
            var node = JsonNode.Parse(File.ReadAllText(metaPath));
            return (
                node?["description"]?.GetValue<string>() ?? "",
                node?["agentType"]?.GetValue<string>() ?? ""
            );
        }
        catch
        {
            return ("", "");
        }
    }

    private bool IsAgentRunning(string path) =>
        _agentRunning.GetOrCompute(path, ClassifyRunning, false);

    // A sub-agent's own transcript ends in a completed assistant turn — a final assistant message
    // with no pending tool call — only while it is idle, waiting for its next instruction. While it
    // is working, the tail is either an assistant tool_use awaiting its result, or a freshly-injected
    // user/tool_result record with no assistant reply yet. We classify off the last assistant/user
    // record (ignoring trailing "system" bookkeeping records), which also keeps a long, silent shell
    // command correctly pegged as running rather than guessing from file mtime.
    private static bool ClassifyRunning(string path)
    {
        bool sawTurn = false;
        bool lastWasUser = false;
        bool lastAssistantHadToolUse = false;

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
                            break;
                        }
                    }
                }
            }
        }

        if (!sawTurn)
            return false;            // nothing yet / just spawned — no work to surface
        if (lastWasUser)
            return true;             // an injected prompt or a tool_result awaiting the next step
        return lastAssistantHadToolUse; // assistant ended on a tool_use -> awaiting its result
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
