using System.Text.Json.Nodes;
using Perch.Data.Replay;
using Xunit;

namespace Perch.Tests;

/// <summary>
/// Golden checks over <see cref="Projector"/>: export a fixture session, load it, and materialise the
/// sandbox at several scrub positions. The projector is a pure function of T, so these assert the tree
/// grows/shrinks with T, that file mtimes are stamped to virtual time, that back-scrub is identical to
/// forward-scrub, and that the replay process-probe reports the synthetic pid alive.
/// </summary>
public class ProjectorTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "perch-projector-test-" + Guid.NewGuid().ToString("N"));
    private readonly string _sandbox;
    private readonly Recording _recording;
    private readonly Projector _projector;
    private readonly ReplayTimeline _timeline;

    public ProjectorTests()
    {
        Directory.CreateDirectory(_root);
        _sandbox = Path.Combine(_root, "sandbox");
        Directory.CreateDirectory(_sandbox);

        // sessParser: user(12:00:00) → assistant(12:00:05) → assistant sidechain(12:00:06) → user(12:00:07).
        var recordingPath = Path.Combine(_root, "rec.perchreplay");
        var session = Assert.Single(RecordingExporter.DiscoverSessions(), s => s.SessionId == "sessParser");
        RecordingExporter.Export([session], recordingPath, redact: false);

        _recording = Recording.Load(recordingPath)!;
        Assert.NotNull(_recording);
        _timeline = _recording.Manifest.Timelines[0];
        _projector = new Projector(_recording, new ReplayClock(_recording.SceneEpochUtc), _sandbox);
    }

    private string TranscriptPath =>
        Path.Combine(_sandbox, "projects", "C--fixtures-proj", "sessParser.jsonl");

    private string SessionFilePath =>
        Path.Combine(_sandbox, "sessions", $"{_timeline.SyntheticPid}.json");

    private int TranscriptLineCount() =>
        File.Exists(TranscriptPath)
            ? File.ReadAllLines(TranscriptPath).Count(l => l.Length > 0)
            : 0;

    [Fact]
    public void SceneGeometry_MatchesTheRecording()
    {
        Assert.Equal(7000, _recording.SceneDurationMs); // 12:00:00 → 12:00:07
        Assert.Equal(new DateTime(2025, 3, 8, 12, 0, 0, DateTimeKind.Utc), _recording.SceneEpochUtc);
    }

    [Fact]
    public void MaterialiseAt_GrowsTheTranscriptWithT()
    {
        _projector.MaterialiseAt(0);
        Assert.Equal(1, TranscriptLineCount());       // only the opening user prompt

        _projector.MaterialiseAt(5000);
        Assert.Equal(2, TranscriptLineCount());       // + the 12:00:05 assistant turn

        _projector.MaterialiseAt(7000);
        Assert.Equal(4, TranscriptLineCount());       // the whole transcript
    }

    [Fact]
    public void BackScrub_IsIdenticalToForwardScrub()
    {
        _projector.MaterialiseAt(7000);
        Assert.Equal(4, TranscriptLineCount());
        _projector.MaterialiseAt(0);
        Assert.Equal(1, TranscriptLineCount());       // regenerated, not undone
    }

    [Fact]
    public void MaterialiseAt_StampsTranscriptMtimeToVirtualTime()
    {
        _projector.MaterialiseAt(7000);
        // Last record is 12:00:07; its file mtime must match so mtime-vs-clock comparisons stay coherent.
        Assert.Equal(new DateTime(2025, 3, 8, 12, 0, 7, DateTimeKind.Utc),
            File.GetLastWriteTimeUtc(TranscriptPath));
    }

    [Fact]
    public void SessionFile_IsSynthesised_WithSyntheticPidAndCwd()
    {
        _projector.MaterialiseAt(0);
        var node = JsonNode.Parse(File.ReadAllText(SessionFilePath))!;
        Assert.Equal(_timeline.SyntheticPid, node["pid"]!.GetValue<int>());
        Assert.Equal("sessParser", node["sessionId"]!.GetValue<string>());
        Assert.Equal(@"C:\fixtures\proj", node["cwd"]!.GetValue<string>());
        Assert.Equal("busy", node["status"]!.GetValue<string>()); // opens on a user prompt → model working
    }

    [Fact]
    public void Probe_ReportsSyntheticPidAlive_OnceStarted()
    {
        _projector.MaterialiseAt(0);
        Assert.True(_projector.IsAlive(_timeline.SyntheticPid));
        Assert.False(_projector.IsAlive(4242)); // a real/unknown pid is never alive under replay
    }

    public void Dispose()
    {
        _recording.Dispose();
        try { Directory.Delete(_root, recursive: true); } catch { }
    }
}
