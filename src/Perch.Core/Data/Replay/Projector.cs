using Perch.Platform;

namespace Perch.Data.Replay;

/// <summary>
/// Turns a scrub position <c>T</c> into a consistent on-disk world: it rebuilds the sandbox
/// <c>~/.claude</c> tree so it contains exactly the records with scene-position ≤ T, synthesises each
/// session's status from its transcript position, and stamps every file's mtime to its virtual time —
/// then the real, unmodified app scans that tree as if it were live.
///
/// <para>A <b>pure function of T</b>: every call rebuilds from the immutable recording, so scrubbing
/// backward is identical to scrubbing forward (regenerate, never undo). Also the replay
/// <see cref="IProcessProbe"/>: a timeline's synthetic pid reads alive once its window has begun, which
/// is what makes sessions appear at the right moment.</para>
///
/// <para><b>Not yet optimised:</b> playback rebuilds the whole tree each tick rather than applying only
/// the forward delta. Correct and simple; the delta optimisation in the plan is a later refinement.</para>
/// </summary>
internal sealed class Projector : IProcessProbe
{
    private readonly Recording _recording;
    private readonly ReplayClock _clock;
    private readonly string _sandboxDir;
    private long _currentT = -1;

    // Full paths written during the current MaterialiseAt pass. Everything the pass didn't (re)write is
    // stale and gets pruned afterwards. Tracked so files are overwritten IN PLACE rather than
    // deleted-and-recreated — a delete-then-recreate briefly empties the sessions dir, and a
    // watcher-triggered scan landing in that gap reads zero sessions and collapses the overlay for good.
    private readonly HashSet<string> _written = new(StringComparer.OrdinalIgnoreCase);

    public Projector(Recording recording, ReplayClock clock, string sandboxDir)
    {
        _recording = recording;
        _clock = clock;
        _sandboxDir = sandboxDir;
    }

    private string SessionsDir => Path.Combine(_sandboxDir, "sessions");
    private string ProjectsDir => Path.Combine(_sandboxDir, "projects");

    /// <summary>Rebuilds the sandbox to be consistent with scene position <paramref name="t"/> (ms) and
    /// advances the virtual clock to match. Call off the UI thread; follow with a scan.</summary>
    public void MaterialiseAt(long t)
    {
        t = Math.Clamp(t, 0, _recording.SceneDurationMs);
        Interlocked.Exchange(ref _currentT, t);
        _clock.SetUtc(_recording.VirtualInstant(t));

        Directory.CreateDirectory(SessionsDir);
        Directory.CreateDirectory(ProjectsDir);

        _written.Clear();
        foreach (var timeline in _recording.Manifest.Timelines)
        {
            if (Recording.StartScenePos(timeline) <= t)
                MaterialiseTimeline(timeline, t);
        }

        // Remove only what this position doesn't include (a timeline before its start, sub-agents not yet
        // spawned, a shrunk transcript on a back-scrub). Everything current was overwritten in place above,
        // so it's absent from this prune — the sessions dir is never momentarily emptied.
        PruneExcept(SessionsDir);
        PruneExcept(ProjectsDir);
    }

    /// <summary>Replay liveness: a synthetic pid is "alive" once its timeline's window has begun at the
    /// current scrub position. Returns false for any real pid.</summary>
    public bool IsAlive(int pid)
    {
        var t = Interlocked.Read(ref _currentT);
        foreach (var timeline in _recording.Manifest.Timelines)
            if (timeline.SyntheticPid == pid && Recording.StartScenePos(timeline) <= t)
                return true;
        return false;
    }

    private void MaterialiseTimeline(ReplayTimeline timeline, long t)
    {
        var encDir = Path.Combine(ProjectsDir, TranscriptLocator.EncodeProjectDir(timeline.Cwd));
        Directory.CreateDirectory(encDir);

        // Transcript (the master timeline): keep every record whose scene position has been reached.
        var included = new List<string>();
        long lastPos = Recording.StartScenePos(timeline);
        foreach (var (pos, line) in _recording.Records(timeline, _recording.TranscriptPath(timeline)))
        {
            if (pos <= t)
            {
                included.Add(line);
                lastPos = pos;
            }
        }

        var transcriptOut = Path.Combine(encDir, $"{timeline.SessionId}.jsonl");
        WriteLines(transcriptOut, included, lastPos);

        MaterialiseSubagents(timeline, encDir, t);
        WriteSessionFile(timeline, included, lastPos);
        CopySidecars(timeline);
    }

    // Per-agent transcripts (truncated to T), their meta sidecars, and — once an agent's recorded
    // activity is fully in the past — its end marker, so a teammate flips from working to idle/stopped
    // as the scrub crosses the end of its turn.
    private void MaterialiseSubagents(ReplayTimeline timeline, string encDir, long t)
    {
        var sourceDir = _recording.SubagentsDir(timeline);
        if (!Directory.Exists(sourceDir))
            return;

        var outDir = Path.Combine(encDir, timeline.SessionId, "subagents");

        foreach (var file in Directory.EnumerateFiles(sourceDir, "agent-*.jsonl"))
        {
            var included = new List<string>();
            long lastPos = Recording.StartScenePos(timeline);
            int total = 0;
            foreach (var (pos, line) in _recording.Records(timeline, file))
            {
                total++;
                if (pos <= t)
                {
                    included.Add(line);
                    lastPos = pos;
                }
            }
            if (included.Count == 0)
                continue; // agent not spawned yet at this position

            Directory.CreateDirectory(outDir);
            var name = Path.GetFileName(file);
            WriteLines(Path.Combine(outDir, name), included, lastPos);

            var basePath = file[..^".jsonl".Length];
            var metaSource = basePath + ".meta.json";
            if (File.Exists(metaSource))
                CopyStamped(metaSource, Path.Combine(outDir, Path.GetFileName(metaSource)), lastPos);

            // The agent's recorded activity is entirely behind us → surface its end marker (mtime at/after
            // the agent file's, so HasFreshEndMarker treats the turn as cleanly ended).
            bool fullyDone = included.Count == total;
            if (fullyDone)
            {
                foreach (var ext in new[] { ".stopped", ".idle" })
                {
                    var markerSource = basePath + ext;
                    if (File.Exists(markerSource))
                        CopyStamped(markerSource, Path.Combine(outDir, Path.GetFileName(markerSource)), lastPos);
                }
            }
        }
    }

    // The synthesised sessions/{pid}.json: static fields from the captured snapshot (when present),
    // status reconstructed from the transcript position, updatedAt + mtime at the last virtual instant.
    private void WriteSessionFile(ReplayTimeline timeline, List<string> included, long lastPos)
    {
        var status = SessionStatusSynthesiser.Synthesise(included);
        var updatedAtMs = new DateTimeOffset(_recording.VirtualInstant(lastPos)).ToUnixTimeMilliseconds();

        var obj = new System.Text.Json.Nodes.JsonObject
        {
            ["pid"] = timeline.SyntheticPid,
            ["sessionId"] = timeline.SessionId,
            ["cwd"] = timeline.Cwd,
            ["status"] = status,
            ["updatedAt"] = updatedAtMs,
        };

        // Carry static fields (entrypoint / remote-control marker) from the captured snapshot when present.
        if (_recording.SnapshotPath(timeline) is { } snapshotPath)
        {
            try
            {
                if (System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(snapshotPath)) is
                    System.Text.Json.Nodes.JsonObject snap)
                {
                    if (snap["entrypoint"] is { } ep) obj["entrypoint"] = ep.DeepClone();
                    if (snap["bridgeSessionId"] is { } bridge) obj["bridgeSessionId"] = bridge.DeepClone();
                }
            }
            catch { }
        }

        var path = Path.Combine(SessionsDir, $"{timeline.SyntheticPid}.json");
        File.WriteAllText(path, obj.ToJsonString());
        StampMtime(path, lastPos);
        Track(path);
    }

    // .mode / .notify / .note sidecars are ambient (not time-series) — present them verbatim in sessions/.
    private void CopySidecars(ReplayTimeline timeline)
    {
        var sidecarsDir = _recording.SidecarsDir(timeline);
        if (!Directory.Exists(sidecarsDir))
            return;
        foreach (var file in Directory.EnumerateFiles(sidecarsDir))
        {
            var dest = Path.Combine(SessionsDir, Path.GetFileName(file));
            try { File.Copy(file, dest, overwrite: true); Track(dest); }
            catch { }
        }
    }

    private void WriteLines(string path, IEnumerable<string> lines, long scenePos)
    {
        using (var writer = new StreamWriter(path)) // overwrites in place; no delete → no Deleted event
            foreach (var line in lines)
                writer.WriteLine(line);
        StampMtime(path, scenePos);
        Track(path);
    }

    private void CopyStamped(string source, string dest, long scenePos)
    {
        try
        {
            File.Copy(source, dest, overwrite: true);
            StampMtime(dest, scenePos);
            Track(dest);
        }
        catch { }
    }

    private void Track(string path)
    {
        try { _written.Add(Path.GetFullPath(path)); } catch { }
    }

    // The one place the projection and the clock must agree: a materialised file's mtime is its record's
    // virtual time, so the mtime-vs-Clock.UtcNow comparisons (sub-agent staleness, stats pre-filters)
    // stay coherent as T moves.
    private void StampMtime(string path, long scenePos)
    {
        try { File.SetLastWriteTimeUtc(path, _recording.VirtualInstant(scenePos)); }
        catch { }
    }

    // Deletes every file under dir that this pass didn't (re)write — the stale remnants of a position we
    // scrubbed away from. Files that are still current were overwritten in place and are in _written, so
    // they're left untouched (crucially, the live session file is never deleted). Empty dirs left behind
    // are harmless — the readers tolerate an empty subagents dir.
    private void PruneExcept(string dir)
    {
        try
        {
            foreach (var file in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
            {
                if (!_written.Contains(Path.GetFullPath(file)))
                    try { File.Delete(file); } catch { }
            }
        }
        catch { }
    }
}
