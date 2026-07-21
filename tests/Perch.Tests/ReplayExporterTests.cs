using System.IO.Compression;
using System.Text.Json.Nodes;
using Perch.Data.Replay;
using Xunit;

namespace Perch.Tests;

/// <summary>
/// Round-trips a fixture session through <see cref="RecordingExporter"/>: export to a .perchreplay,
/// re-open the zip, and assert the manifest + captured transcript. Covers both the raw and redacted
/// passes — redaction must scrub content while keeping the token counts + model the stats consume.
/// </summary>
public class ReplayExporterTests : IDisposable
{
    private readonly string _dir = Path.Combine(
        Path.GetTempPath(), "perch-replay-test-" + Guid.NewGuid().ToString("N"));

    public ReplayExporterTests() => Directory.CreateDirectory(_dir);

    private static ReplaySessionInfo Session(string sessionId) =>
        Assert.Single(RecordingExporter.DiscoverSessions(), s => s.SessionId == sessionId);

    private static string ReadEntry(string zipPath, string entry)
    {
        using var zip = ZipFile.OpenRead(zipPath);
        using var reader = new StreamReader(zip.GetEntry(entry)!.Open());
        return reader.ReadToEnd();
    }

    [Fact]
    public void Discover_FindsFixtureSessions()
    {
        var ids = RecordingExporter.DiscoverSessions().Select(s => s.SessionId).ToList();
        Assert.Contains("sessBurn", ids);
        Assert.Contains("sessParser", ids);
    }

    [Fact]
    public void Export_Raw_ProducesManifestAndVerbatimTranscript()
    {
        var outPath = Path.Combine(_dir, "raw.perchreplay");
        var manifest = RecordingExporter.Export([Session("sessBurn")], outPath, redact: false);

        var timeline = Assert.Single(manifest.Timelines);
        Assert.Equal("sessBurn", timeline.SessionId);
        Assert.False(manifest.Redacted);
        Assert.True(timeline.DurationMs > 0);            // sessBurn spans ~7 minutes
        Assert.True(timeline.SyntheticPid >= 900_000);
        Assert.Equal(new DateTime(2026, 7, 1, 9, 59, 0, DateTimeKind.Utc), timeline.T0Utc);

        // Raw pass keeps original content verbatim.
        var transcript = ReadEntry(outPath, $"timelines/{timeline.Id}/transcript.jsonl");
        Assert.Contains("Burst start.", transcript);
        Assert.Contains("40000", transcript);
    }

    [Fact]
    public void Export_Redacted_ScrubsContent_KeepsTokensAndModel()
    {
        var outPath = Path.Combine(_dir, "redacted.perchreplay");
        var manifest = RecordingExporter.Export([Session("sessBurn")], outPath, redact: true);
        var timeline = Assert.Single(manifest.Timelines);

        Assert.True(manifest.Redacted);
        Assert.StartsWith(@"C:\demo\project-", timeline.Cwd); // rewritten onto a placeholder

        var transcript = ReadEntry(outPath, $"timelines/{timeline.Id}/transcript.jsonl");
        // Content gone…
        Assert.DoesNotContain("Burst start.", transcript);
        Assert.DoesNotContain("Older isolated turn.", transcript);
        Assert.DoesNotContain(@"C:\\fixtures\\proj", transcript);
        // …but the numbers + model the stats read survive.
        Assert.Contains("40000", transcript);
        Assert.Contains("claude-opus-4-8", transcript);
        // Timestamps drive the timeline, so they must survive too.
        Assert.Contains("2026-07-01T10:05:00", transcript);
    }

    [Fact]
    public void Export_Redacted_CapturesAndScrubsSubagentTree()
    {
        // sessTeam has a subagents/ dir with per-agent transcripts + meta sidecars.
        var outPath = Path.Combine(_dir, "team.perchreplay");
        var manifest = RecordingExporter.Export([Session("sessTeam")], outPath, redact: true);
        var id = Assert.Single(manifest.Timelines).Id;

        using var zip = ZipFile.OpenRead(outPath);
        var metaEntry = zip.Entries.Single(e =>
            e.FullName == $"timelines/{id}/subagents/agent-aux-explorer-1111.meta.json");
        using var reader = new StreamReader(metaEntry.Open());
        var meta = JsonNode.Parse(reader.ReadToEnd())!;

        // The type signal the roster/tree need survives; the free-text description does not.
        Assert.Equal("ux-explorer", meta["agentType"]!.GetValue<string>());
        Assert.Equal("[redacted]", meta["description"]!.GetValue<string>());
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }
}
