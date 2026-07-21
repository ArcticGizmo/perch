using System.Text.Json.Nodes;

namespace Perch.Data.Replay;

internal enum ReplayMarkerKind { Prompt, ToolUse, SubagentSpawn, Interrupt }

/// <summary>A notable frame in a recording, at a scene position, that the controller can jump between.</summary>
internal readonly record struct ReplayMarker(long ScenePos, ReplayMarkerKind Kind, string Label);

/// <summary>
/// Scans a recording for the notable frames worth jumping between — turn boundaries (user prompts),
/// tool calls, sub-agent spawns, and interrupts — so the controller's prev/next-marker transport lands
/// on "interesting" moments rather than arbitrary time. Derived from the raw transcripts (the same
/// source the projector replays), across every timeline, sorted by scene position. Pure; never throws.
/// </summary>
internal static class MarkerExtractor
{
    public static IReadOnlyList<ReplayMarker> Extract(Recording recording)
    {
        var markers = new List<ReplayMarker>();
        foreach (var timeline in recording.Manifest.Timelines)
        {
            foreach (var (pos, line) in recording.Records(timeline, recording.TranscriptPath(timeline)))
                AddFrom(markers, pos, line);
        }
        markers.Sort((a, b) => a.ScenePos.CompareTo(b.ScenePos));
        return markers;
    }

    private static void AddFrom(List<ReplayMarker> markers, long pos, string line)
    {
        if (line.Length == 0)
            return;

        // Interrupt marker (kept intact even in redacted recordings), cheap-prefiltered.
        if (line.Contains("[Request interrupted by user", StringComparison.Ordinal))
        {
            markers.Add(new ReplayMarker(pos, ReplayMarkerKind.Interrupt, "interrupt"));
            return;
        }

        JsonNode? node;
        try { node = JsonNode.Parse(line); }
        catch { return; }

        var type = node?["type"]?.GetValue<string>();
        if (type == "user")
        {
            // A genuine typed prompt (a plain string that isn't a slash-command echo) is a turn boundary.
            var content = TryString(node?["message"]?["content"]);
            if (content is { } s
                && !s.StartsWith("<command-name>", StringComparison.Ordinal)
                && !s.StartsWith("<local-command-stdout>", StringComparison.Ordinal))
                markers.Add(new ReplayMarker(pos, ReplayMarkerKind.Prompt, "prompt"));
        }
        else if (type == "assistant" && TranscriptJson.ContentArray(node) is { } blocks)
        {
            foreach (var block in blocks)
            {
                if (TranscriptJson.BlockType(block) != "tool_use")
                    continue;
                var name = block!["name"]?.GetValue<string>() ?? "tool";
                if (name is "Agent" or "Task")
                    markers.Add(new ReplayMarker(pos, ReplayMarkerKind.SubagentSpawn, "sub-agent"));
                else
                    markers.Add(new ReplayMarker(pos, ReplayMarkerKind.ToolUse, name));
            }
        }
    }

    private static string? TryString(JsonNode? node)
    {
        try { return node?.GetValue<string>(); }
        catch { return null; }
    }
}
