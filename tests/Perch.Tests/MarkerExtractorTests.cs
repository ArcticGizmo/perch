using Perch.Data.Replay;
using Xunit;

namespace Perch.Tests;

/// <summary>
/// Covers <see cref="MarkerExtractor"/> — the notable-frame index the controller's prev/next-marker
/// transport jumps between. Built from a real exported recording so it exercises the same scene-position
/// mapping the projector uses.
/// </summary>
public class MarkerExtractorTests : IDisposable
{
    private readonly string _dir = Path.Combine(
        Path.GetTempPath(), "perch-marker-test-" + Guid.NewGuid().ToString("N"));

    public MarkerExtractorTests() => Directory.CreateDirectory(_dir);

    private Recording Export(string sessionId)
    {
        var path = Path.Combine(_dir, $"{sessionId}.perchreplay");
        var session = Assert.Single(RecordingExporter.DiscoverSessions(), s => s.SessionId == sessionId);
        RecordingExporter.Export([session], path, redact: false);
        return Recording.Load(path)!;
    }

    [Fact]
    public void Prompt_IsMarkedAtTurnBoundary()
    {
        // sessParser opens with a genuine user prompt at t0.
        using var rec = Export("sessParser");
        var markers = MarkerExtractor.Extract(rec);
        Assert.Contains(markers, m => m.Kind == ReplayMarkerKind.Prompt && m.ScenePos == 0);
    }

    [Fact]
    public void ToolUses_AreMarkedWithToolName()
    {
        // sessTasks is a run of TaskCreate / TaskUpdate tool_use turns.
        using var rec = Export("sessTasks");
        var markers = MarkerExtractor.Extract(rec);
        Assert.Contains(markers, m => m.Kind == ReplayMarkerKind.ToolUse && m.Label == "TaskCreate");
        Assert.Contains(markers, m => m.Kind == ReplayMarkerKind.ToolUse && m.Label == "TaskUpdate");
    }

    [Fact]
    public void SubagentSpawn_IsMarked()
    {
        // sessB launches a Task/Agent sub-agent.
        using var rec = Export("sessB");
        var markers = MarkerExtractor.Extract(rec);
        Assert.Contains(markers, m => m.Kind == ReplayMarkerKind.SubagentSpawn);
    }

    [Fact]
    public void Markers_AreSortedByScenePosition()
    {
        using var rec = Export("sessTasks");
        var markers = MarkerExtractor.Extract(rec);
        var positions = markers.Select(m => m.ScenePos).ToList();
        Assert.Equal(positions.OrderBy(p => p), positions);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }
}
