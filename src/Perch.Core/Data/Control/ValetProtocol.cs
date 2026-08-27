using System.Text.Json.Nodes;

namespace Perch.Data.Control;

/// <summary>
/// The wire contract between <c>perch-hook valet</c> (a PreToolUse hook) and the tray's
/// <see cref="ValetServer"/> — the "permission valet" that lets the user answer a session's permission
/// prompts from Perch (session-control M2, docs/session-control-plan.md).
///
/// <para>Transport: a local named pipe (<see cref="PipeName"/>), newline-delimited JSON, one
/// request/reply pair per connection. The hook forwards Claude Code's raw PreToolUse payload (newlines
/// blanked — they can only be inter-token whitespace in the compact payload) and waits briefly for one
/// reply line: <c>{"decision":"pass"|"allow"|"deny","reason":…}</c>. <b>pass</b> means Perch has no
/// opinion — the hook exits with no output and Claude Code's normal permission flow (allowlists, the
/// terminal prompt) proceeds untouched. The tray answers pass <em>immediately</em> unless it is
/// actually going to show UI, so an unarmed valet adds only a pipe round-trip to a tool call, and no
/// tray at all costs one failed 200 ms connect.</para>
///
/// <para>The pipe name is baked into the hook registration by the profile that wrote it (see
/// <see cref="ClaudeUserSettings"/>), so a dev tray and an installed release tray never intercept each
/// other's sessions.</para>
/// </summary>
internal static class ValetProtocol
{
    /// <summary>This profile's pipe name — <c>perch-valet</c> or <c>perch-valet-dev</c>.</summary>
    public static string PipeName => AppProfile.IsDev ? "perch-valet-dev" : "perch-valet";

    public const string Pass = "pass";
    public const string Allow = "allow";
    public const string Deny = "deny";

    /// <summary>Tools the valet never prompts for even when armed — read-only/no-side-effect calls that
    /// interactive sessions overwhelmingly auto-allow. PoC heuristic: the hook cannot see whether a tool
    /// <em>would</em> have prompted (allowlist evaluation lives inside Claude Code), so the valet
    /// approximates with a mutating-vs-reading split; refine or replace at the M6 gate.</summary>
    public static bool IsReadOnlyTool(string tool) => tool is
        "Read" or "Glob" or "Grep" or "LS" or "NotebookRead" or "WebSearch" or "TodoWrite" or
        "Task" or "Agent" or "AskUserQuestion" or "Skill" or "ToolSearch" or "TaskOutput" or
        "EnterPlanMode" or "ExitPlanMode" or "ListMcpResourcesTool" or "ReadMcpResourceTool";

    /// <summary>Builds the reply line for a decision.</summary>
    public static string ReplyJson(string decision, string? reason = null)
    {
        var o = new JsonObject { ["decision"] = decision };
        if (!string.IsNullOrEmpty(reason)) o["reason"] = reason;
        return o.ToJsonString();
    }
}

/// <summary>One permission request forwarded by the hook: the identifying fields plus the tool input.</summary>
internal sealed record ValetRequest(
    string SessionId, string ToolName, string Cwd, string PermissionMode, JsonNode? ToolInput)
{
    /// <summary>Parses the hook's forwarded PreToolUse payload; null when it isn't usable.</summary>
    public static ValetRequest? Parse(string line)
    {
        try
        {
            if (JsonNode.Parse(line) is not JsonObject o) return null;
            var tool = TranscriptJson.AsString(o["tool_name"]);
            if (string.IsNullOrEmpty(tool)) return null;
            return new ValetRequest(
                TranscriptJson.AsString(o["session_id"]) ?? "",
                tool,
                TranscriptJson.AsString(o["cwd"]) ?? "",
                TranscriptJson.AsString(o["permission_mode"]) ?? "",
                o["tool_input"]);
        }
        catch
        {
            return null;
        }
    }
}

/// <summary>The tray's verdict on one <see cref="ValetRequest"/>.</summary>
internal readonly record struct ValetDecision(string Kind, string? Reason = null)
{
    public static readonly ValetDecision Pass = new(ValetProtocol.Pass);
    public static ValetDecision Allowed(string? reason = null) => new(ValetProtocol.Allow, reason);
    public static ValetDecision Denied(string? reason = null) => new(ValetProtocol.Deny, reason);
}
