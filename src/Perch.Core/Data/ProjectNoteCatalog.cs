using System.Text.Json.Nodes;

namespace Perch.Data;

/// <summary>
/// One project Perch knows about, for the "add a note to a project I'm not working in" picker: the real
/// working directory (recovered from a transcript, since the project-folder name is a <em>lossy</em>
/// encoding of the cwd and can't be decoded back), its leaf name, when the project was last active, and
/// the project note it already carries (null when none). Built by <see cref="ProjectNoteCatalog"/>.
/// </summary>
internal sealed record ProjectEntry(string Cwd, string ProjectName, DateTime LastActivity, string? Note)
{
    public bool HasNote => !string.IsNullOrEmpty(Note);
}

/// <summary>
/// Enumerates the projects under <c>~/.claude/projects</c> so a note can be attached to one that has no
/// live session — the whole point of the note picker. The project-folder name is a lossy encoding of the
/// working directory (see <see cref="TranscriptLocator.EncodeProjectDir"/>), and a project note is keyed by
/// the <em>real</em> cwd (<see cref="SessionMonitor.SetProjectNote"/>), so each project's cwd is recovered
/// from the head of its newest transcript (every record stamps a <c>cwd</c>). Best-effort and pure: a
/// project whose cwd can't be recovered is skipped rather than throwing, and a missing/unreadable projects
/// directory yields an empty list.
/// </summary>
internal static class ProjectNoteCatalog
{
    /// <summary>Every project with a recoverable working directory, most-recently-active first.</summary>
    public static IReadOnlyList<ProjectEntry> Enumerate()
    {
        var entries = new List<ProjectEntry>();
        foreach (var dir in TranscriptLocator.EnumerateProjectDirectories())
            if (ReadEntry(dir) is { } entry)
                entries.Add(entry);
        entries.Sort((a, b) => b.LastActivity.CompareTo(a.LastActivity));
        return entries;
    }

    private static ProjectEntry? ReadEntry(string dir)
    {
        // The newest transcript in the directory: its head carries the real cwd, and its write time stands
        // in for the project's last activity.
        string? newest = null;
        DateTime newestTime = DateTime.MinValue;
        IEnumerable<string> files;
        try { files = Directory.EnumerateFiles(dir, "*.jsonl"); }
        catch { return null; }
        foreach (var file in files)
        {
            DateTime t;
            try { t = File.GetLastWriteTime(file); }
            catch { continue; }
            if (newest is null || t > newestTime) { newest = file; newestTime = t; }
        }
        if (newest is null) return null; // no transcript → no cwd to recover, nothing to key a note on

        var cwd = ReadFirstCwd(newest);
        if (string.IsNullOrEmpty(cwd)) return null;

        return new ProjectEntry(cwd, PathLeaf.Of(cwd), newestTime, SessionMonitor.ReadProjectNote(cwd));
    }

    // The first "cwd" recorded in a transcript, read from the head only (cwd is stamped on every record).
    // Mirrors RecordingExporter's reader; bounded so a huge transcript never turns the enumeration into a
    // full scan.
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
}
