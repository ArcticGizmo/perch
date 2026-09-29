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
    public static string? Resolve(string sessionId, string cwd)
    {
        if (string.IsNullOrEmpty(sessionId))
            return null;

        // Direct hit first, in every projects tree: the cwd-encoded folder under each config dir.
        if (!string.IsNullOrEmpty(cwd))
        {
            var enc = EncodeProjectDir(cwd);
            foreach (var projects in ProjectsDirs())
            {
                var direct = Path.Combine(projects, enc, sessionId + ".jsonl");
                if (File.Exists(direct))
                    return direct;
            }
        }

        // Failing that, scan every project directory across every tree for {sessionId}.jsonl (the
        // sessionId is a UUID, so the match is unambiguous).
        foreach (var dir in EnumerateProjectDirectories())
        {
            var candidate = Path.Combine(dir, sessionId + ".jsonl");
            try { if (File.Exists(candidate)) return candidate; }
            catch { /* skip an unreadable entry */ }
        }

        return null;
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
    /// The <c>CLAUDE_CONFIG_DIR</c> a <c>claude --resume &lt;sessionId&gt;</c> must run under so the CLI finds
    /// the transcript: the root of the config dir that owns it, or <c>null</c> to inherit Perch's own environment
    /// — when the owner is the primary (inheriting <em>is</em> the primary, and pinning it explicitly would move
    /// Claude's <c>.claude.json</c> lookup), or when the transcript can't be found or attributed. A <c>projects/</c>
    /// tree junctioned across several dirs resolves to its first owner, so there it can't tell accounts apart.
    /// </summary>
    public static string? ResumeConfigRoot(string sessionId, string cwd)
    {
        if (OwningConfigDir(Resolve(sessionId, cwd)) is not { } owner) return null;
        var primary = ClaudeConfigSet.Instance.Primary;
        return ClaudeConfigDir.PathComparer.Equals(owner.RealRoot, primary.RealRoot) ? null : owner.Root;
    }

    /// <summary>For <see cref="LaunchLog"/>: how <see cref="ResumeConfigRoot"/> decided, as one log line — the transcript it found, the
    /// owner it attributed it to, the primary, and the whole config-dir set.</summary>
    public static string DescribeResume(string sessionId, string cwd)
    {
        try
        {
            var set = ClaudeConfigSet.Instance;
            var path = Resolve(sessionId, cwd);
            var owner = OwningConfigDir(path);
            var dirs = string.Join(", ", set.All.Select(d => $"{d.Root} (real {d.RealRoot})"));
            return $"session={sessionId} cwd={LaunchLog.Show(cwd)} transcript={LaunchLog.Show(path)} " +
                   $"owner={LaunchLog.Show(owner?.Root)} primary={LaunchLog.Show(set.Primary.Root)} " +
                   $"-> CLAUDE_CONFIG_DIR={LaunchLog.Show(ResumeConfigRoot(sessionId, cwd))} | set: {dirs}";
        }
        catch (Exception ex) { return $"session={sessionId} describe failed: {ex.Message}"; }
    }

    /// <summary>Every project directory under every config dir's <c>projects/</c> tree; empty when none
    /// exist or a directory can't be read. Deduped implicitly by the distinct-projects-dirs set.</summary>
    public static IEnumerable<string> EnumerateProjectDirectories()
    {
        foreach (var projects in ProjectsDirs())
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
