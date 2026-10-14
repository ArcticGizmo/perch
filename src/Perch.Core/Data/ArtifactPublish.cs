namespace Perch.Data;

/// <summary>
/// "Publish as artifact" for a file a Perch-controlled session wrote. The <c>Artifact</c> tool (like the other
/// claude.ai-backed tools) is only registered in an <em>interactive</em> Claude Code session, never in the headless
/// <c>claude -p</c> ones Perch drives over stream-json, so the publish is handed to a session that has it, opened with
/// <see cref="Prompt"/>: a background one (<see cref="ArtifactPublisher"/>), else an interactive one in a terminal
/// (<see cref="Perch.Platform.ISessionLauncher.RunClaudeCommand"/>).
/// </summary>
public static class ArtifactPublish
{
    private static readonly string[] Extensions = [".html", ".htm", ".md", ".markdown", ".svg"];

    /// <summary>Whether <paramref name="path"/> is a page-like file worth offering to publish.</summary>
    public static bool IsPublishable(string path) =>
        Extensions.Any(e => path.EndsWith(e, StringComparison.OrdinalIgnoreCase));

    /// <summary>The opening prompt for the publishing session (the terminal hand-off). The file is named relative to
    /// <paramref name="cwd"/> (where the terminal opens) when it lives under it, else by its full path. The text
    /// deliberately avoids quotes and other shell syntax so it passes <see cref="ClaudeCli.IsSafePrompt"/> for any
    /// ordinary path.</summary>
    public static string Prompt(string cwd, string absPath)
    {
        var rel = Path.GetRelativePath(cwd, absPath);
        var shown = Path.IsPathRooted(rel) || rel.StartsWith("..", StringComparison.Ordinal) ? absPath : rel;
        return $"Load the Artifact tool with ToolSearch, then publish the file {shown} as a private artifact " +
               "and reply with its link";
    }

    /// <summary>The prompt for a background publish (<see cref="ArtifactPublisher"/>). Always the absolute path: a
    /// background session may be moved into a git worktree, where a new, untracked file doesn't exist, and a relative
    /// name would then point at nothing (or at a copy it made there).</summary>
    public static string BackgroundPrompt(string absPath) =>
        $"Load the Artifact tool with ToolSearch, then publish the file at the absolute path {absPath} as a private " +
        "artifact and reply with its link. Publish that exact path. Do not copy, create or edit any files.";
}
