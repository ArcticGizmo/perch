using System.IO.Compression;
using System.Text.Json.Nodes;

namespace Perch.Data.Replay;

/// <summary>
/// A loaded <c>.perchreplay</c> recording: the manifest plus its raw files, extracted once to a scratch
/// directory the projector reads from. Also owns the scene geometry — the single shared clock every
/// timeline is placed on (<see cref="SceneEpochUtc"/>, <see cref="SceneDurationMs"/>) — and the
/// per-record "scene position" that makes <c>materialise state at T</c> a lazy filter over the raw lines.
/// Immutable once loaded; <see cref="Dispose"/> removes the scratch copy.
/// </summary>
internal sealed class Recording : IDisposable
{
    public ReplayManifest Manifest { get; }
    public string SourceDir { get; }

    /// <summary>The absolute instant scene-position 0 maps to (the earliest timeline's t0). A record's
    /// virtual timestamp is <see cref="SceneEpochUtc"/> + its scene position.</summary>
    public DateTime SceneEpochUtc { get; }

    /// <summary>The full length of the scene — the latest end across all timelines' offsets.</summary>
    public long SceneDurationMs { get; }

    private Recording(ReplayManifest manifest, string sourceDir)
    {
        Manifest = manifest;
        SourceDir = sourceDir;
        SceneEpochUtc = manifest.Timelines.Count == 0
            ? DateTime.UnixEpoch
            : manifest.Timelines.Min(t => t.T0Utc);
        SceneDurationMs = manifest.Timelines.Count == 0
            ? 0
            : manifest.Timelines.Max(t => t.StartOffsetMs + t.DurationMs);
    }

    /// <summary>Extracts <paramref name="recordingPath"/> to a fresh scratch dir and parses its manifest.
    /// Returns null if the file isn't a readable recording. The caller owns disposal.</summary>
    public static Recording? Load(string recordingPath)
    {
        if (!File.Exists(recordingPath))
            return null;
        var dir = Path.Combine(Path.GetTempPath(), "perch-replay-src-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(dir);
            ZipFile.ExtractToDirectory(recordingPath, dir);
            var manifestPath = Path.Combine(dir, ReplayFormat.ManifestEntry);
            if (!File.Exists(manifestPath))
                return Fail(dir);
            var manifest = ReplayManifest.FromJson(File.ReadAllText(manifestPath));
            if (manifest == null || manifest.Timelines.Count == 0)
                return Fail(dir);
            return new Recording(manifest, dir);
        }
        catch
        {
            return Fail(dir);
        }
    }

    private static Recording? Fail(string dir)
    {
        try { Directory.Delete(dir, recursive: true); } catch { }
        return null;
    }

    // ----- Per-timeline file locations (under SourceDir/timelines/{id}/) ---------------------------

    private string TimelineDir(ReplayTimeline t) =>
        Path.Combine(SourceDir, ReplayFormat.TimelinesDir, t.Id);

    public string TranscriptPath(ReplayTimeline t) =>
        Path.Combine(TimelineDir(t), ReplayFormat.Transcript);

    public string SubagentsDir(ReplayTimeline t) =>
        Path.Combine(TimelineDir(t), ReplayFormat.SubagentsDir);

    public string SidecarsDir(ReplayTimeline t) =>
        Path.Combine(TimelineDir(t), ReplayFormat.SidecarsDir);

    /// <summary>The captured session.json snapshot for a timeline, or null when none was recorded.</summary>
    public string? SnapshotPath(ReplayTimeline t)
    {
        var path = Path.Combine(TimelineDir(t), ReplayFormat.SessionSnapshot);
        return File.Exists(path) ? path : null;
    }

    /// <summary>When (in scene ms) a timeline first becomes live — its offset onto the shared clock.</summary>
    public static long StartScenePos(ReplayTimeline t) => t.StartOffsetMs;

    /// <summary>
    /// Reads a transcript's records paired with their scene position (offset + elapsed-from-t0). A record
    /// with no parseable timestamp inherits the previous one's position, so it materialises alongside its
    /// neighbour rather than being dropped or misplaced.
    /// </summary>
    public IEnumerable<(long ScenePos, string Line)> Records(ReplayTimeline t, string transcriptPath)
    {
        long last = t.StartOffsetMs;
        foreach (var line in TranscriptScan.ReadLines(transcriptPath))
        {
            if (ParseTimestamp(line) is { } ts)
                last = t.StartOffsetMs + (long)(ts - t.T0Utc).TotalMilliseconds;
            yield return (last, line);
        }
    }

    /// <summary>The virtual absolute instant a scene position maps to.</summary>
    public DateTime VirtualInstant(long scenePos) => SceneEpochUtc.AddMilliseconds(scenePos);

    // The "timestamp" of a transcript record as UTC, or null if absent/unparseable.
    internal static DateTime? ParseTimestamp(string line)
    {
        if (string.IsNullOrEmpty(line) || !line.Contains("\"timestamp\""))
            return null;
        try
        {
            if (JsonNode.Parse(line)?["timestamp"]?.GetValue<string>() is { } ts
                && DateTimeOffset.TryParse(ts, out var dto))
                return dto.UtcDateTime;
        }
        catch { }
        return null;
    }

    public void Dispose()
    {
        try { Directory.Delete(SourceDir, recursive: true); } catch { }
    }
}
