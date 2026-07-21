using System.IO.Compression;
using System.Text.Json.Nodes;

namespace Perch.Data.Replay;

/// <summary>A session already on disk that can be captured into a recording — what the picker lists.</summary>
internal sealed record ReplaySessionInfo(
    string SessionId, string Cwd, string TranscriptPath, DateTime LastActivityUtc, long SizeBytes)
{
    /// <summary>Optional human id for the exported timeline (folder name); defaults to the session id.</summary>
    public string? TimelineId { get; init; }
}

/// <summary>
/// Builds a <c>.perchreplay</c> recording from sessions already on disk (no live capture). Raw Claude
/// Code files are kept verbatim so replay exercises the exact same parsers the live app uses; an opt-in
/// redaction pass scrubs content while preserving structure (see <see cref="TranscriptRedactor"/>).
/// Pure of any UI — the App head supplies a picker and the redaction toggle.
/// </summary>
internal static class RecordingExporter
{
    /// <summary>Synthetic pids start here — comfortably clear of real OS pids so a replay can't collide
    /// with a live session, and the projector's probe reports them alive within their window.</summary>
    private const int SyntheticPidBase = 900_000;

    /// <summary>
    /// Enumerates the sessions on disk that can be recorded, newest activity first. Best-effort: an
    /// unreadable transcript is skipped rather than throwing.
    /// </summary>
    public static IReadOnlyList<ReplaySessionInfo> DiscoverSessions()
    {
        var found = new List<ReplaySessionInfo>();
        foreach (var path in TranscriptLocator.EnumerateTranscripts())
        {
            try
            {
                var sessionId = Path.GetFileNameWithoutExtension(path);
                if (string.IsNullOrEmpty(sessionId))
                    continue;
                var info = new FileInfo(path);
                found.Add(new ReplaySessionInfo(
                    sessionId, ReadFirstCwd(path) ?? "", path, info.LastWriteTimeUtc, info.Length));
            }
            catch
            {
                // Skip a transcript that vanished or couldn't be stat'd mid-scan.
            }
        }
        return found.OrderByDescending(s => s.LastActivityUtc).ToList();
    }

    /// <summary>
    /// Writes the chosen sessions to <paramref name="outputPath"/> as a single <c>.perchreplay</c> zip
    /// and returns the manifest that was embedded. When <paramref name="redact"/> is set, all content is
    /// scrubbed and each timeline is rewritten onto a placeholder cwd.
    /// </summary>
    public static ReplayManifest Export(
        IReadOnlyList<ReplaySessionInfo> sessions, string outputPath, bool redact)
    {
        var timelines = new List<ReplayTimeline>();

        // Overwrite any prior recording at this path.
        if (File.Exists(outputPath))
            File.Delete(outputPath);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outputPath))!);

        using (var archive = ZipFile.Open(outputPath, ZipArchiveMode.Create))
        {
            for (int i = 0; i < sessions.Count; i++)
            {
                var timeline = CaptureTimeline(archive, sessions[i], i, redact);
                if (timeline != null)
                    timelines.Add(timeline);
            }

            var manifest = new ReplayManifest
            {
                CreatedUtc = DateTime.UtcNow, // a real wall-clock stamp; the virtual Clock is a replay concern
                Redacted = redact,
                Timelines = timelines,
            };
            WriteEntry(archive, ReplayFormat.ManifestEntry, manifest.ToJson());
            return manifest;
        }
    }

    // Captures one session's files into timelines/{id}/ and returns its manifest entry (null if the
    // transcript couldn't be read at all — a recording with no timeline is useless).
    private static ReplayTimeline? CaptureTimeline(
        ZipArchive archive, ReplaySessionInfo session, int index, bool redact)
    {
        var id = Folderise(session.TimelineId ?? session.SessionId);
        var baseEntry = $"{ReplayFormat.TimelinesDir}/{id}";
        var placeholderCwd = redact ? ReplayFormat.PlaceholderCwd(index) : session.Cwd;
        var syntheticPid = SyntheticPidBase + index;

        // Transcript (master timeline): copy line-by-line, redacting each line and tracking the record
        // timestamps that define this timeline's zero and duration.
        DateTime? t0 = null, tEnd = null;
        var transcriptEntry = archive.CreateEntry($"{baseEntry}/{ReplayFormat.Transcript}");
        using (var writer = new StreamWriter(transcriptEntry.Open()))
        {
            foreach (var line in TranscriptScan.ReadLines(session.TranscriptPath))
            {
                if (ParseTimestamp(line) is { } ts)
                {
                    t0 ??= ts;
                    tEnd = ts;
                }
                writer.WriteLine(redact ? TranscriptRedactor.RedactLine(line, placeholderCwd) : line);
            }
        }
        if (t0 == null)
            return null; // no timestamped records — nothing to place on the clock

        CaptureSubagents(archive, session, baseEntry, placeholderCwd, redact);
        CaptureSidecarsAndSnapshot(archive, session, baseEntry, placeholderCwd, syntheticPid, redact);

        return new ReplayTimeline
        {
            Id = id,
            SessionId = session.SessionId,
            Cwd = placeholderCwd,
            SyntheticPid = syntheticPid,
            T0Utc = t0.Value,
            DurationMs = (long)((tEnd ?? t0.Value) - t0.Value).TotalMilliseconds,
            StartOffsetMs = 0,
        };
    }

    // The per-agent transcripts, meta sidecars, and hook markers under {projectDir}/{sessionId}/subagents/.
    private static void CaptureSubagents(
        ZipArchive archive, ReplaySessionInfo session, string baseEntry, string placeholderCwd, bool redact)
    {
        string subagentsDir;
        try
        {
            subagentsDir = Path.Combine(
                Path.GetDirectoryName(session.TranscriptPath)!, session.SessionId, "subagents");
            if (!Directory.Exists(subagentsDir))
                return;
        }
        catch { return; }

        foreach (var file in Directory.EnumerateFiles(subagentsDir))
        {
            try
            {
                var name = Path.GetFileName(file);
                var entry = $"{baseEntry}/{ReplayFormat.SubagentsDir}/{name}";
                var ext = Path.GetExtension(file);
                if (ext == ".jsonl")
                    CopyTranscript(archive, file, entry, placeholderCwd, redact);
                else if (name.EndsWith(".meta.json", StringComparison.Ordinal))
                    WriteEntry(archive, entry,
                        redact ? TranscriptRedactor.RedactMeta(File.ReadAllText(file), placeholderCwd)
                               : File.ReadAllText(file));
                else
                    // .stopped / .idle hook markers: small, no user content — copy verbatim.
                    WriteEntry(archive, entry, File.ReadAllText(file));
            }
            catch
            {
                // Skip an agent file that vanished or couldn't be read mid-export.
            }
        }
    }

    // The session's .mode / .notify / .note sidecars, plus the final sessions/{pid}.json snapshot if one
    // is still on disk (rewriting its cwd + pid so the captured tree is self-consistent).
    private static void CaptureSidecarsAndSnapshot(
        ZipArchive archive, ReplaySessionInfo session, string baseEntry,
        string placeholderCwd, int syntheticPid, bool redact)
    {
        var sid = session.SessionId;
        TryCopySidecar($"{sid}.mode", $"{baseEntry}/{ReplayFormat.SidecarsDir}/{sid}.mode");
        TryCopySidecar($"{sid}.notify", $"{baseEntry}/{ReplayFormat.SidecarsDir}/{sid}.notify");

        var notePath = Path.Combine(ClaudePaths.SessionsDir, $"{sid}.note");
        if (File.Exists(notePath))
        {
            try
            {
                var text = File.ReadAllText(notePath);
                WriteEntry(archive, $"{baseEntry}/{ReplayFormat.SidecarsDir}/{sid}.note",
                    redact ? TranscriptRedactor.RedactNote(text) : text);
            }
            catch { }
        }

        // The live session file is named by pid, not sessionId, and is deleted when the process ends, so
        // it's often absent for a historical session. Capture it when present for its static fields.
        if (FindSessionSnapshot(sid) is { } snapshot)
            WriteEntry(archive, $"{baseEntry}/{ReplayFormat.SessionSnapshot}",
                RewriteSnapshot(snapshot, placeholderCwd, syntheticPid, sid));

        void TryCopySidecar(string fileName, string entry)
        {
            var path = Path.Combine(ClaudePaths.SessionsDir, fileName);
            try { if (File.Exists(path)) WriteEntry(archive, entry, File.ReadAllText(path)); }
            catch { }
        }
    }

    private static void CopyTranscript(
        ZipArchive archive, string sourcePath, string entryName, string placeholderCwd, bool redact)
    {
        var entry = archive.CreateEntry(entryName);
        using var writer = new StreamWriter(entry.Open());
        foreach (var line in TranscriptScan.ReadLines(sourcePath))
            writer.WriteLine(redact ? TranscriptRedactor.RedactLine(line, placeholderCwd) : line);
    }

    // Finds the on-disk sessions/{pid}.json whose sessionId matches, or null. Best-effort.
    private static string? FindSessionSnapshot(string sessionId)
    {
        try
        {
            if (!Directory.Exists(ClaudePaths.SessionsDir))
                return null;
            foreach (var file in Directory.EnumerateFiles(ClaudePaths.SessionsDir, "*.json"))
            {
                try
                {
                    var node = JsonNode.Parse(File.ReadAllText(file));
                    if (node?["sessionId"]?.GetValue<string>() == sessionId)
                        return File.ReadAllText(file);
                }
                catch { }
            }
        }
        catch { }
        return null;
    }

    // Rewrites a captured session.json onto the placeholder cwd + synthetic pid so the tree is
    // self-consistent; status/waitingFor/updatedAt/entrypoint/bridgeSessionId are non-PII and kept.
    private static string RewriteSnapshot(string json, string placeholderCwd, int syntheticPid, string sessionId)
    {
        try
        {
            if (JsonNode.Parse(json) is JsonObject obj)
            {
                obj["cwd"] = placeholderCwd;
                obj["pid"] = syntheticPid;
                obj["sessionId"] = sessionId;
                return obj.ToJsonString();
            }
        }
        catch { }
        // Fall back to a minimal synthetic snapshot.
        return new JsonObject
        {
            ["pid"] = syntheticPid,
            ["sessionId"] = sessionId,
            ["cwd"] = placeholderCwd,
            ["status"] = "idle",
        }.ToJsonString();
    }

    private static void WriteEntry(ZipArchive archive, string entryName, string content)
    {
        var entry = archive.CreateEntry(entryName);
        using var writer = new StreamWriter(entry.Open());
        writer.Write(content);
    }

    // The first "cwd" recorded in a transcript, read from the head only (cwd is stamped on every record).
    private static string? ReadFirstCwd(string path)
    {
        int scanned = 0;
        foreach (var line in TranscriptScan.ReadLines(path))
        {
            if (++scanned > 50)
                break;
            if (!line.Contains("\"cwd\""))
                continue;
            try
            {
                if (JsonNode.Parse(line)?["cwd"]?.GetValue<string>() is { Length: > 0 } cwd)
                    return cwd;
            }
            catch { }
        }
        return null;
    }

    // The "timestamp" of a transcript record as UTC, or null if absent/unparseable.
    private static DateTime? ParseTimestamp(string line)
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

    // Folder-safe timeline id: the same non-alphanumeric → '-' rule Claude Code uses for project dirs.
    private static string Folderise(string id) =>
        System.Text.RegularExpressions.Regex.Replace(id, "[^A-Za-z0-9_.-]", "-");
}
