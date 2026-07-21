namespace Perch.Data.Replay;

/// <summary>
/// The on-disk shape of a <c>.perchreplay</c> recording: the entry names inside the zip and the format
/// version. Kept in one place so the exporter (writer) and the future projector (reader) can't drift.
/// A recording is a plain zip — raw Claude Code files kept verbatim (so replay exercises the exact same
/// parsers the live app uses) plus a <see cref="ReplayManifest"/>.
/// </summary>
internal static class ReplayFormat
{
    /// <summary>Bumped when the layout below changes in a way a reader must branch on.</summary>
    public const int Version = 1;

    public const string ManifestEntry = "manifest.json";

    /// <summary>Root folder holding one sub-folder per captured timeline.</summary>
    public const string TimelinesDir = "timelines";

    // Per-timeline entry names (under timelines/{id}/).
    public const string SessionSnapshot = "session.json";   // the final sessions/{pid}.json, if captured
    public const string Transcript = "transcript.jsonl";    // the master timeline
    public const string SubagentsDir = "subagents";         // agent-*.jsonl + .meta.json + .stopped/.idle
    public const string SidecarsDir = "sidecars";           // .mode / .notify / .note captured for the session

    /// <summary>The token every redacted string value collapses to.</summary>
    public const string RedactionToken = "[redacted]";

    /// <summary>Builds the stable placeholder cwd a redacted timeline is rewritten onto
    /// (<c>C:\demo\project-a</c>, <c>…project-b</c>, …), so the tree stays self-consistent while leaking
    /// no real path. Index 0 → "a".</summary>
    public static string PlaceholderCwd(int timelineIndex) =>
        $@"C:\demo\project-{(char)('a' + timelineIndex % 26)}";
}
