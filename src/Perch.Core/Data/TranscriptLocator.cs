using System.Text.RegularExpressions;

namespace Perch.Data;

/// <summary>
/// Locates session transcripts under <c>projects/</c>. Owns the one rule Claude Code uses to
/// map a working directory to its project folder, and the "direct path, else scan" resolution that
/// <see cref="TranscriptReader"/>, <see cref="SubAgentReader"/> and the history/stats scans each used
/// to re-implement. Since config-dir discovery (Layer 1) it searches <b>every</b> distinct
/// <c>projects/</c> tree in the set (<see cref="ClaudeConfigSet.DistinctProjectsDirs"/>), not just the
/// primary, so a transcript in a non-primary config dir is found and attributed. Best-effort and pure:
/// every method tolerates a missing projects directory and never throws.
/// </summary>
internal static class TranscriptLocator
{
    // Every distinct projects/ tree across the config-dir set, primary first, deduped by real path.
    // Under a pinned CLAUDE_CONFIG_DIR (tests, replay) this collapses to the single primary tree, so
    // behaviour there is unchanged.
    private static IReadOnlyList<string> ProjectsDirs() => ClaudeConfigSet.Instance.DistinctProjectsDirs();

    /// <summary>
    /// Encodes a working directory into Claude Code's project-folder name: every non-alphanumeric
    /// character becomes <c>-</c> (e.g. <c>C:\a\b.c</c> → <c>C--a-b-c</c>).
    /// </summary>
    public static string EncodeProjectDir(string cwd) =>
        Regex.Replace(cwd, "[^A-Za-z0-9]", "-");

    /// <summary>
    /// Returns the full path to a session's <c>.jsonl</c> transcript, or <c>null</c> when it can't be
    /// found. Tries the cwd-encoded project directory first; failing that, scans every project
    /// directory for <c>{sessionId}.jsonl</c> (the sessionId is a UUID, so the match is unambiguous
    /// and this covers any cwd-encoding edge case the direct rule misses).
    /// </summary>
    public static string? Resolve(string sessionId, string cwd) => Resolve(ProjectsDirs(), sessionId, cwd, MissTtl);

    // A fallback scan that found nothing isn't repeated for this long (unless a projects/ root changes).
    private static readonly TimeSpan MissTtl = TimeSpan.FromSeconds(30);
    private const int MaxMisses = 4096;

    // sessionId -> when its fallback scan last missed, and the projects roots' mtimes at the time.
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, (long AtMs, string Roots)> Misses = new();

    /// <summary>
    /// <see cref="Resolve(string,string)"/> over explicit <paramref name="projectsDirs"/> (the test seam). The
    /// direct <c>&lt;projects&gt;/&lt;enc-cwd&gt;/&lt;id&gt;.jsonl</c> check always runs — one stat, and how a
    /// brand-new session's transcript is found the moment it appears. The fallback, a scan of every project
    /// folder, is what's expensive: a session with no transcript yet used to trigger it from every reader on
    /// every scan (about ten times per session per scan). So a miss is remembered for <paramref name="missTtl"/>,
    /// and forgotten early when any projects root's mtime changes (a new project folder appeared). Review fixes
    /// CP20.
    /// </summary>
    internal static string? Resolve(IReadOnlyList<string> projectsDirs, string sessionId, string cwd, TimeSpan missTtl)
    {
        if (string.IsNullOrEmpty(sessionId))
            return null;

        // Direct hit first, in every projects tree: the cwd-encoded folder under each config dir.
        if (!string.IsNullOrEmpty(cwd))
        {
            var enc = EncodeProjectDir(cwd);
            foreach (var projects in projectsDirs)
            {
                var direct = Path.Combine(projects, enc, sessionId + ".jsonl");
                if (File.Exists(direct))
                    return direct;
            }
        }

        var roots = RootsSignature(projectsDirs);
        long now = Environment.TickCount64;
        if (Misses.TryGetValue(sessionId, out var miss) && miss.Roots == roots && now - miss.AtMs < missTtl.TotalMilliseconds)
            return null;

        // Failing that, scan every project directory across every tree for {sessionId}.jsonl (the
        // sessionId is a UUID, so the match is unambiguous).
        foreach (var dir in EnumerateProjectDirectories(projectsDirs))
        {
            var candidate = Path.Combine(dir, sessionId + ".jsonl");
            try
            {
                if (File.Exists(candidate))
                {
                    Misses.TryRemove(sessionId, out _);
                    return candidate;
                }
            }
            catch { /* skip an unreadable entry */ }
        }

        if (Misses.Count >= MaxMisses) Misses.Clear();   // crude bound; a miss is cheap to rediscover
        Misses[sessionId] = (now, roots);
        return null;
    }

    // The projects roots and their last-write times: creating a project folder bumps its root's mtime.
    private static string RootsSignature(IReadOnlyList<string> projectsDirs)
    {
        var sb = new System.Text.StringBuilder();
        foreach (var d in projectsDirs)
        {
            long ticks;
            try { ticks = Directory.GetLastWriteTimeUtc(d).Ticks; } catch { ticks = 0; }
            sb.Append(d).Append('|').Append(ticks).Append(';');
        }
        return sb.ToString();
    }

    /// <summary>The config dir whose <c>projects/</c> tree holds <paramref name="transcriptPath"/>
    /// (<c>&lt;root&gt;/projects/&lt;enc-cwd&gt;/&lt;id&gt;.jsonl</c>), or null when no dir in the set owns it.</summary>
    public static ClaudeConfigDir? OwningConfigDir(string? transcriptPath)
    {
        if (string.IsNullOrEmpty(transcriptPath)) return null;
        try
        {
            var projectDir = Path.GetDirectoryName(transcriptPath);        // …/projects/<enc-cwd>
            var projects = Path.GetDirectoryName(projectDir);               // …/projects
            return ClaudeConfigSet.Instance.ForRoot(Path.GetDirectoryName(projects));
        }
        catch { return null; }
    }

    /// <summary>
    /// The <c>CLAUDE_CONFIG_DIR</c> a <c>claude --resume &lt;sessionId&gt;</c> must run under: the account the session
    /// ran under, or <c>null</c> to inherit Perch's own environment — when that's the primary (inheriting <em>is</em>
    /// the primary, and pinning it explicitly would move Claude's <c>.claude.json</c> lookup), or when the transcript
    /// can't be found or attributed. See <see cref="ResumeDir"/> for how the account is chosen.
    /// </summary>
    public static string? ResumeConfigRoot(string sessionId, string cwd)
    {
        if (ResumeDir(sessionId, cwd).Dir is not { } dir) return null;
        return ClaudeConfigDir.PathComparer.Equals(dir.RealRoot, ClaudeConfigSet.Instance.Primary.RealRoot) ? null : dir.Root;
    }

    /// <summary>
    /// The config dir to resume <paramref name="sessionId"/> under, and which source chose it (for the launch log).
    /// The transcript's owner is the dir whose <c>projects/</c> holds it, but a <c>projects/</c> tree junctioned
    /// across several dirs resolves to its first owner, so on its own it can't tell accounts apart. Checked in order:
    /// the account Perch saw the session run under (<see cref="SessionAccounts"/>), the hook's
    /// <c>{sessionId}.configdir</c> marker (present only while the session hasn't ended), then the owner. A candidate
    /// is taken only when its <c>projects/</c> is the same real folder as the owner's, so the CLI can find the
    /// transcript there.
    /// </summary>
    internal static (ClaudeConfigDir? Dir, string Source) ResumeDir(string sessionId, string cwd)
    {
        if (OwningConfigDir(Resolve(sessionId, cwd)) is not { } owner) return (null, "no transcript");
        var set = ClaudeConfigSet.Instance;
        if (SeesTranscript(set.ForRoot(SessionAccounts.Recall(sessionId)), owner) is { } remembered)
            return (remembered, "remembered");
        if (SeesTranscript(ReportedConfigDir(set, sessionId), owner) is { } reported)
            return (reported, "marker");
        return (owner, "transcript owner");
    }

    // The candidate when it reads the owner's projects/ (the same dir, or junctioned onto the same folder), else null.
    private static ClaudeConfigDir? SeesTranscript(ClaudeConfigDir? candidate, ClaudeConfigDir owner) =>
        candidate is not null && ClaudeConfigDir.PathComparer.Equals(
            ClaudeConfigSet.ResolveReal(candidate.ProjectsDir), ClaudeConfigSet.ResolveReal(owner.ProjectsDir))
            ? candidate : null;

    // The dir the hook's {sessionId}.configdir marker names. The hook writes it to the sessions/ of the dir the session
    // ran under, which needn't be the transcript owner's (only projects/ may be shared), so every sessions/ is checked.
    private static ClaudeConfigDir? ReportedConfigDir(ClaudeConfigSet set, string sessionId)
    {
        foreach (var sessions in set.DistinctSessionsDirs())
            if (set.ForRoot(SessionMonitor.ReadMarker(Path.Combine(sessions, sessionId + ".configdir"))) is { } dir)
                return dir;
        return null;
    }

    /// <summary>For <see cref="LaunchLog"/>: how <see cref="ResumeConfigRoot"/> decided, as one log line — the transcript it found, the
    /// owner it attributed it to, the dir it chose and why, the primary, and the whole config-dir set.</summary>
    public static string DescribeResume(string sessionId, string cwd)
    {
        try
        {
            var set = ClaudeConfigSet.Instance;
            var path = Resolve(sessionId, cwd);
            var owner = OwningConfigDir(path);
            var (chosen, source) = ResumeDir(sessionId, cwd);
            var dirs = string.Join(", ", set.All.Select(d => $"{d.Root} (real {d.RealRoot})"));
            return $"session={sessionId} cwd={LaunchLog.Show(cwd)} transcript={LaunchLog.Show(path)} " +
                   $"owner={LaunchLog.Show(owner?.Root)} chosen={LaunchLog.Show(chosen?.Root)} via={source} " +
                   $"primary={LaunchLog.Show(set.Primary.Root)} " +
                   $"-> CLAUDE_CONFIG_DIR={LaunchLog.Show(ResumeConfigRoot(sessionId, cwd))} | set: {dirs}";
        }
        catch (Exception ex) { return $"session={sessionId} describe failed: {ex.Message}"; }
    }

    /// <summary>Every project directory under every config dir's <c>projects/</c> tree; empty when none
    /// exist or a directory can't be read. Deduped implicitly by the distinct-projects-dirs set.</summary>
    public static IEnumerable<string> EnumerateProjectDirectories() => EnumerateProjectDirectories(ProjectsDirs());

    private static IEnumerable<string> EnumerateProjectDirectories(IReadOnlyList<string> projectsDirs)
    {
        foreach (var projects in projectsDirs)
        {
            if (!Directory.Exists(projects)) continue;
            string[] dirs;
            try { dirs = Directory.GetDirectories(projects); }
            catch { continue; }
            foreach (var dir in dirs)
                yield return dir;
        }
    }

    /// <summary>Every session transcript (<c>*.jsonl</c>) across all project directories. Best-effort:
    /// a directory that can't be enumerated is skipped rather than throwing.</summary>
    public static IEnumerable<string> EnumerateTranscripts()
    {
        foreach (var dir in EnumerateProjectDirectories())
        {
            IEnumerable<string> files;
            try { files = Directory.EnumerateFiles(dir, "*.jsonl"); }
            catch { continue; }
            foreach (var file in files)
                yield return file;
        }
    }
}
