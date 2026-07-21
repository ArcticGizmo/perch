using System.Text.Json.Nodes;

namespace Perch.Data.Replay;

/// <summary>
/// Reconstructs a session's live status from its transcript position, because on-disk we only ever have
/// the <em>final</em> <c>sessions/{pid}.json</c> — its status history is gone. Given the transcript
/// records up to the scrub point, decide whether the session reads busy, idle, or (approximately)
/// waiting, reusing the same discriminators <see cref="TranscriptReader"/>/<see cref="SessionMonitor"/>
/// already reason about.
///
/// <para><b>Known limitation:</b> the exact busy/idle timing is an approximation of what the live hook
/// wrote — good for demos and most repros, but a bug that hinges on precise session-file status timing
/// needs the future live recorder. Notably, permission-prompt "waiting" isn't reliably encoded in the
/// transcript, so this never synthesises it (a blocked turn reads busy).</para>
/// </summary>
internal static class SessionStatusSynthesiser
{
    /// <summary>The raw <c>status</c> string a synthesised session file should carry at this position.</summary>
    public static string Synthesise(IEnumerable<string> recordsUpToT)
    {
        // Track tool_use / tool_result pairing (an unmatched tool_use = a tool still in flight = busy)
        // and the last meaningful (user/assistant) record, which decides the resting state.
        var openToolUses = new HashSet<string>();
        string? lastType = null;
        string? lastUserContent = null;

        foreach (var line in recordsUpToT)
        {
            if (line.Length == 0 || (!line.Contains("assistant") && !line.Contains("user")))
                continue;

            JsonNode? node;
            try { node = JsonNode.Parse(line); }
            catch { continue; }

            var type = node?["type"]?.GetValue<string>();
            if (type is not ("user" or "assistant"))
                continue;

            lastType = type;
            lastUserContent = type == "user" ? FirstStringContent(node) : null;

            if (TranscriptJson.ContentArray(node) is { } content)
            {
                foreach (var block in content)
                {
                    switch (TranscriptJson.BlockType(block))
                    {
                        case "tool_use" when block!["id"]?.GetValue<string>() is { } id:
                            openToolUses.Add(id);
                            break;
                        case "tool_result" when block!["tool_use_id"]?.GetValue<string>() is { } rid:
                            openToolUses.Remove(rid);
                            break;
                    }
                }
            }
        }

        // A deliberate cancel or an interactive built-in as the final record leaves the session idle —
        // and it abandons any dangling tool_use, so this wins over the open-tool-use check below.
        if (lastUserContent is { } c
            && (c.Contains("[Request interrupted by user", StringComparison.Ordinal)
                || c.StartsWith("<command-name>", StringComparison.Ordinal)
                || c.StartsWith("<local-command-stdout>", StringComparison.Ordinal)))
            return "idle";

        // A tool is still running (its result hasn't landed yet) → the session is working.
        if (openToolUses.Count > 0)
            return "busy";

        // Otherwise the resting state is set by the last meaningful record.
        if (lastType == null)
            return "idle"; // nothing yet — the session has only just begun

        if (lastType == "assistant")
            return "idle"; // a completed assistant turn — the session is done for now

        // A genuine user prompt or a tool_result means the model is about to (or is) responding.
        return "busy";
    }

    // The record's message content when it's a plain string (a genuine prompt, a command echo, or the
    // interrupt marker); null when the content is a block array (tool_result etc.).
    private static string? FirstStringContent(JsonNode? node)
    {
        try { return node?["message"]?["content"]?.GetValue<string>(); }
        catch { return null; }
    }
}
