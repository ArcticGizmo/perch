using System.Text.RegularExpressions;

namespace Perch.Data;

/// <summary>
/// Locates session transcripts under <c>~/.claude/projects</c>. Owns the one rule Claude Code uses to
/// map a working directory to its project folder, and the "direct path, else scan" resolution that
/// <see cref="TranscriptReader"/>, <see cref="SubAgentReader"/> and the history/stats scans each used
/// to re-implement. Best-effort and pure: every method tolerates a missing projects directory and
/// never throws.
/// </summary>
internal static class TranscriptLocator
{
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

        if (!string.IsNullOrEmpty(cwd))
        {
            var direct = Path.Combine(ClaudePaths.ProjectsDir, EncodeProjectDir(cwd), sessionId + ".jsonl");
            if (File.Exists(direct))
                return direct;
        }

        try
        {
            foreach (var dir in Directory.EnumerateDirectories(ClaudePaths.ProjectsDir))
            {
                var candidate = Path.Combine(dir, sessionId + ".jsonl");
                if (File.Exists(candidate))
                    return candidate;
            }
        }
        catch
        {
            // Projects dir missing or unreadable — nothing to resolve.
        }

        return null;
    }

    /// <summary>Every project directory under <c>~/.claude/projects</c>; empty when none exist or the
    /// directory can't be read.</summary>
    public static IEnumerable<string> EnumerateProjectDirectories()
    {
        if (!Directory.Exists(ClaudePaths.ProjectsDir))
            return [];
        try { return Directory.EnumerateDirectories(ClaudePaths.ProjectsDir); }
        catch { return []; }
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
