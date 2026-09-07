using System.Text.Json.Nodes;

namespace Perch.Data.Control;

/// <summary>
/// The <c>ExitPlanMode</c> tool as it crosses the stream-json control channel. Like <c>AskUserQuestion</c> it
/// arrives as an ordinary <c>can_use_tool</c> permission request, but its meaning is "here is my plan — may I
/// leave plan mode and carry it out?". Its <c>input</c> carries the plan as a markdown <c>plan</c> string, so
/// the rich UI shows a plan-approval card (Approve / Approve &amp; accept-edits / Keep planning) rather than a
/// bare permission gate. Answering is the normal permission allow/deny; the CLI's <c>setMode</c> suggestion
/// (usually <c>acceptEdits</c>) rides the allow-with-mode button. This type just pulls the plan text out.
/// </summary>
internal static class PlanApprovalInput
{
    public const string ToolName = "ExitPlanMode";

    /// <summary>The plan markdown from the tool input, or null when absent/malformed.</summary>
    public static string? Parse(string inputJson)
    {
        try { return TranscriptJson.AsString(JsonNode.Parse(inputJson)?["plan"]); }
        catch { return null; }
    }
}
