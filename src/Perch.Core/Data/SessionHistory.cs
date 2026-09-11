using System.Text.Json.Nodes;
using Perch.Data;

namespace Perch.Data;

/// <summary>One selectable session in the viewer's dropdown: a transcript file on disk, its project,
/// when it last changed, whether its process is currently alive, and how big it is (so the viewer can
/// warn before loading a multi-megabyte transcript).</summary>
internal sealed record HistoryEntry(
    string SessionId,
    string ProjectName,
    string Cwd,
    string Path,
    DateTime LastUpdated,
    bool IsActive,
    long SizeBytes = 0,
    string? Title = null
)
{
    public string RelativeTime => SessionHistory.Relative(LastUpdated);

    /// <summary>The label to show the user: the explicit <c>/rename</c> <see cref="Title"/> when one was
    /// set, otherwise the <see cref="ProjectName"/> derived from the cwd — mirrors
    /// <see cref="ClaudeSession.DisplayName"/> so a session reads the same live or closed.</summary>
    public string DisplayName => string.IsNullOrWhiteSpace(Title) ? ProjectName : Title!;

    /// <summary>Human-readable transcript size (e.g. "1.4 MB"), shown in the dropdown.</summary>
    public string SizeLabel => SessionHistory.FormatSize(SizeBytes);

    /// <summary>True for transcripts big enough to lag or risk exhausting memory; the viewer gates
    /// these behind an explicit "load anyway" confirmation rather than rendering them on selection.</summary>
    public bool IsLarge => SizeBytes >= SessionHistory.LargeTranscriptBytes;

    /// <summary>True for the synthetic "(none)" row that heads the dropdown — selecting it shows the
    /// empty placeholder rather than loading a transcript.</summary>
    public bool IsPlaceholder { get; init; }

    /// <summary>The synthetic "(none)" entry the viewer prepends to the dropdown so a user can open the
    /// window without loading anything and deliberately clear the current selection.</summary>
    public static HistoryEntry Placeholder { get; } =
        new("", "(none — select a session)", "", "", DateTime.MinValue, false) { IsPlaceholder = true };
}

/// <summary>
/// Static helpers for enumerating session transcripts on disk. Active sessions (their process still
/// running, per <see cref="SessionMonitor"/>) are listed first, then everything else newest-first.
/// </summary>
internal static class SessionHistory
{
    // Project name + cwd + /rename title keyed by transcript path — none change materially for a given
    // transcript, so caching avoids reopening every file on each (frequent) re-list. All three are cached
    // (a cache hit must return the cwd/title too, or a re-list would surface empty values). The title is a
    // tail-first scan of the file, so it's read once per transcript and reused. A /rename made mid-session
    // won't refresh until restart, which is fine: the live overlay and the switcher's *active* rows read the
    // title from SessionMonitor, not from here — this cache only backs the *closed* (static) rows. Listing
    // runs on a background thread, so guard the cache.
    // Keyed by transcript file; the timestamp is the file's last-write time when resolved, so a session that was
    // renamed (a /rename title record, or a moved cwd) after being cached is re-read once the file grows.
    private static readonly Dictionary<string, (string Project, string Cwd, string? Title, DateTime Stamp, long Length)> _projectCache = new();
    private static readonly object _cacheLock = new();

    /// <summary>Transcripts at or above this size are flagged "large": the viewer shows their size in an
    /// alert colour and asks before loading them, since parsing/rendering a multi-megabyte transcript can
    /// lag badly or run the UI out of memory.</summary>
    public const long LargeTranscriptBytes = 10L * 1024 * 1024; // 10 MB

    /// <summary>Lists every session transcript across all projects, active first, then newest-first.</summary>
    public static List<HistoryEntry> ListAll(IReadOnlySet<string> activeSessionIds)
    {
        // Each transcript needs two best-effort reads (cwd from the head, title from the tail), and there can
        // be many hundreds of them — so fan the per-file work across cores. The reads are independent and the
        // shared caches are locked, so this is safe; the sort below re-imposes a deterministic order, so the
        // unordered parallel enumeration is fine.
        var entries = TranscriptLocator.EnumerateTranscripts()
            .AsParallel()
            .Select(file =>
            {
                try
                {
                    var fi = new FileInfo(file);
                    var sessionId = System.IO.Path.GetFileNameWithoutExtension(file);
                    var (project, cwd, title) = ResolveProject(file, System.IO.Path.GetDirectoryName(file) ?? "", fi.LastWriteTime, fi.Length);
                    return new HistoryEntry(
                        sessionId, project, cwd, file, fi.LastWriteTime,
                        activeSessionIds.Contains(sessionId), fi.Length, title);
                }
                catch { return null; }
            })
            .Where(e => e is not null)
            .Select(e => e!);

        return entries
            .OrderByDescending(e => e.IsActive)
            .ThenByDescending(e => e.LastUpdated)
            .ToList();
    }

    /// <summary>The distinct project folders across the given session entries, in the entries' own order
    /// (active-first, newest-first when they come from <see cref="ListAll"/>) — the suggestion list for
    /// launching a fresh session in a project you've worked in before. Deduplicated case-insensitively and
    /// filtered to folders that still exist on disk, so a since-deleted/renamed project never surfaces.</summary>
    public static List<string> DistinctFolders(IEnumerable<HistoryEntry> entries)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var folders = new List<string>();
        foreach (var e in entries)
        {
            var cwd = e.Cwd;
            if (string.IsNullOrEmpty(cwd) || !seen.Add(cwd)) continue;
            if (Directory.Exists(cwd)) folders.Add(cwd);
        }
        return folders;
    }

    // Derives a friendly project name from the transcript's cwd, plus the /rename title, cached together.
    // Because the transcript is append-only, a rename can be caught by scanning only the bytes appended since
    // the last read — so a grown-but-cached file re-reads just its new records, never the whole thing again.
    private static (string project, string cwd, string? title) ResolveProject(string file, string dir, DateTime lastWrite, long length)
    {
        (string Project, string Cwd, string? Title, DateTime Stamp, long Length) cached = default;
        bool haveCached;
        lock (_cacheLock) haveCached = _projectCache.TryGetValue(file, out cached);

        // Unchanged since we last looked — reuse everything.
        if (haveCached && cached.Stamp >= lastWrite)
            return (cached.Project, cached.Cwd, cached.Title);

        // Grew (append-only): project/cwd don't change, so only the newly-appended records need a look — for a
        // /rename that landed since. A shrink means the file was replaced → fall through to a full re-read.
        if (haveCached && length >= cached.Length)
        {
            string? grownTitle = TranscriptReader.ReadTitleFrom(file, cached.Length) ?? cached.Title;
            lock (_cacheLock)
                _projectCache[file] = (cached.Project, cached.Cwd, grownTitle, lastWrite, length);
            return (cached.Project, cached.Cwd, grownTitle);
        }

        string cwd = "";
        try
        {
            using var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(fs);
            for (int i = 0; i < 8 && reader.ReadLine() is { } line; i++)
            {
                if (!line.Contains("\"cwd\"")) continue;
                try
                {
                    var c = JsonNode.Parse(line)?["cwd"]?.GetValue<string>();
                    if (!string.IsNullOrEmpty(c)) { cwd = c; break; }
                }
                catch { }
            }
        }
        catch { }

        string project = !string.IsNullOrEmpty(cwd)
            ? PathLeaf.Of(cwd)
            : System.IO.Path.GetFileName(dir);

        if (string.IsNullOrEmpty(project))
            project = "session";

        // The explicit /rename name, if any — a tail-only scan (a /rename record lands at the tail): the head
        // fallback would re-read a 32KB window of every untitled multi-MB transcript, which dominated the scan.
        string? title = TranscriptReader.ReadTitle(file, tailOnly: true);

        lock (_cacheLock)
            _projectCache[file] = (project, cwd, title, lastWrite, length);
        return (project, cwd, title);
    }

    /// <summary>Formats a byte count as a short, human-readable size (e.g. "812 KB", "1.4 MB").</summary>
    public static string FormatSize(long bytes)
    {
        if (bytes < 1024) return $"{bytes} B";
        double kb = bytes / 1024.0;
        if (kb < 1024) return $"{kb:0} KB";
        double mb = kb / 1024.0;
        if (mb < 1024) return $"{mb:0.0} MB";
        return $"{mb / 1024.0:0.0} GB";
    }

    public static string Relative(DateTime t)
    {
        var d = DateTime.Now - t;
        if (d.TotalSeconds < 60) return "just now";
        if (d.TotalMinutes < 60) return $"{(int)d.TotalMinutes}m ago";
        if (d.TotalHours < 24) return $"{(int)d.TotalHours}h ago";
        if (d.TotalDays < 7) return $"{(int)d.TotalDays}d ago";
        return t.ToString("MMM d");
    }
}
