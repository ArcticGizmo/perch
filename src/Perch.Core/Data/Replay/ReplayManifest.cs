using System.Text.Json;
using System.Text.Json.Serialization;

namespace Perch.Data.Replay;

/// <summary>
/// The <c>manifest.json</c> at the root of a <c>.perchreplay</c> recording: the format version, whether
/// the content was redacted, and one <see cref="ReplayTimeline"/> per captured session. The projector
/// reads this to know which timelines to materialise and how they sit on the shared replay clock.
/// </summary>
internal sealed class ReplayManifest
{
    public int Version { get; init; } = ReplayFormat.Version;

    /// <summary>Wall-clock stamp of when the recording was exported. Informational.</summary>
    public DateTime CreatedUtc { get; init; }

    /// <summary>True when the content redaction pass ran (the default for shareable exports).</summary>
    public bool Redacted { get; init; }

    public List<ReplayTimeline> Timelines { get; init; } = [];

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public string ToJson() => JsonSerializer.Serialize(this, Options);

    public static ReplayManifest? FromJson(string json)
    {
        try { return JsonSerializer.Deserialize<ReplayManifest>(json, Options); }
        catch { return null; }
    }
}

/// <summary>One recorded session within a recording. The transcript is the master timeline; every other
/// field is metadata the projector needs to place it on the clock and stand up a self-consistent tree.</summary>
internal sealed class ReplayTimeline
{
    /// <summary>Stable, human-readable id — also the folder name under <c>timelines/</c>.</summary>
    public required string Id { get; init; }

    public required string SessionId { get; init; }

    /// <summary>The working directory this session ran in — the original path, or a redacted
    /// placeholder (<see cref="ReplayFormat.PlaceholderCwd"/>). Drives the enc-project-dir the projector
    /// rebuilds the transcript under.</summary>
    public required string Cwd { get; init; }

    /// <summary>An assigned pid the projector's process-probe reports alive within this timeline's active
    /// window. Kept distinct from any real pid so a replay can't collide with a live session.</summary>
    public required int SyntheticPid { get; init; }

    /// <summary>Real timestamp of the first transcript record — this timeline's zero.</summary>
    public DateTime T0Utc { get; init; }

    /// <summary>Span from the first to the last transcript record, in milliseconds.</summary>
    public long DurationMs { get; init; }

    /// <summary>Offset of this timeline's zero onto the shared replay clock, so a scene can stage several
    /// sessions starting at different moments. Defaults to 0.</summary>
    public long StartOffsetMs { get; init; }
}
