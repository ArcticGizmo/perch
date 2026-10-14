namespace Perch.Data;

/// <summary>
/// The Claude Code plugin Perch hands every session it drives (<c>--plugin-dir</c>), so its helpers exist only in
/// Perch-controlled sessions and never touch the user's own <c>~/.claude</c>. Today it carries one skill,
/// <c>perch:publish-artifact</c>: the headless <c>claude -p</c> session has no <c>Artifact</c> tool, so the skill picks
/// up the same cues that tool would (a finished piece someone will read, sharing, "make an artifact") and publishes
/// through <c>perch artifact publish</c> (<see cref="ArtifactPublisher"/>) instead. The session finds that exe through
/// <see cref="ExeEnvVar"/>, set on its environment at launch.
/// </summary>
public static class PerchSessionPlugin
{
    /// <summary>The environment variable naming the running <c>perch</c> executable, for the skill to call.</summary>
    public const string ExeEnvVar = "PERCH_EXE";

    /// <summary>The environment variable carrying the controlled session's own id, so a publish run from inside it
    /// (<c>perch artifact publish</c>) is recorded against it (<see cref="PublishLedger"/>).</summary>
    public const string SessionEnvVar = "PERCH_SESSION_ID";

    /// <summary>
    /// The standing instruction appended to a controlled session's system prompt (<c>--append-system-prompt</c>). A
    /// skill only fires when the model picks its one-line description out of the skill list, and among a few dozen
    /// skills it was passed over. The real Artifact tool is announced in the tool list instead, so this puts the same
    /// cue where the model reliably reads it. It must pass <see cref="ClaudeCli.IsSafePrompt"/> (it rides the command
    /// line), so it uses no quotes or shell syntax.
    /// </summary>
    public const string SystemPromptNote =
        "This Perch-driven session has no Artifact tool, so the perch:publish-artifact skill stands in for it. " +
        "Whenever you produce something a person will read as a finished piece (a report, summary, overview, plan, " +
        "explainer, comparison, dashboard, page or diagram), or the person mentions sharing the work or who else will " +
        "read it, asks for an artifact, artefact, page or link, or refers to an artifact made earlier, load that skill " +
        "before writing the file and follow it. It publishes the piece as a private claude.ai artifact, which you may " +
        "do without being asked.";

    /// <summary>Writes the plugin under Perch's local data folder (only files whose content changed) and returns its
    /// directory, or null when it couldn't be written — the session then simply launches without it.</summary>
    public static string? Ensure() => Ensure(Path.Combine(DataDir, "claude-plugin"));

    /// <summary>Perch's local data folder, where the files it hands to Claude Code live.</summary>
    internal static string DataDir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), AppProfile.DataFolderName);

    internal static string? Ensure(string dir)
    {
        try
        {
            Write(Path.Combine(dir, ".claude-plugin", "plugin.json"), Manifest);
            Write(Path.Combine(dir, "skills", "publish-artifact", "SKILL.md"), PublishArtifactSkill);
            return dir;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }

    /// <summary>Writes <paramref name="content"/> to <paramref name="path"/> unless it already holds exactly that.</summary>
    internal static void Write(string path, string content)
    {
        if (File.Exists(path) && File.ReadAllText(path) == content) return;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    internal const string Manifest = """
        {
          "name": "perch",
          "description": "Helpers Perch provides to the Claude Code sessions it drives.",
          "version": "1.0.0"
        }

        """;

    internal const string PublishArtifactSkill = """
        ---
        name: publish-artifact
        description: Publishes a finished piece as a private claude.ai artifact page. This Perch-driven session has no Artifact tool, so this skill stands in for it. When your next reply would produce something a person will read as a finished piece (a report, summary, overview, plan, explainer, comparison, dashboard, page or diagram), load this skill first, before writing the file. Also load it when the person mentions sharing the work or who else will read it, asks for an artifact, artefact, page or link, or refers to an artifact made earlier. You may publish your own work without being asked, because artifacts start private to the user. Skip it when an Artifact tool is available, and for quick answers that belong in chat.
        ---

        # Publish an artifact from a Perch session

        This session runs headless under Perch, so Claude Code's `Artifact` tool isn't registered. Perch publishes for
        you instead, through a short-lived background Claude Code session that does have it.

        1. **Check for the real tool first.** If `ToolSearch` with `select:Artifact` returns a schema, use the Artifact
           tool directly and ignore the rest of this skill.
        2. **Write the piece to a file** in the project's `artefacts/` folder (create it if missing), named in
           lowercase kebab-case with only letters, digits, `-`, `_` and `.` (no spaces or quotes), for example
           `artefacts/q3-latency-report.html`.
           - Prefer one self-contained HTML file: inline CSS, no external scripts, fonts or images, readable in both
             light and dark (`prefers-color-scheme`), with a clear `<title>` and heading. Markdown (`.md`) also works.
           - Never include secrets, credentials or personal data. Anonymise real names, emails and IDs.
           - Keep the file: it's the local copy, and the source if the piece needs changing.
        3. **Publish it without asking first.** An artifact starts private to the user's account, so publishing your
           own work needs no confirmation: the user decides later whether anyone else gets the link. The exception is
           content that could mislead or cause harm if shared further, meaning anything that imitates a real
           organization, person or record, or anything the person presented as sensitive. For those, write the file
           only, say so, and ask whether to publish. Otherwise run Perch's command from the project directory. It
           takes about 10 to 30 seconds and prints only the artifact's URL:
           - Bash: `"$PERCH_EXE" artifact publish artefacts/<name>.html`
           - PowerShell: `& $env:PERCH_EXE artifact publish artefacts\<name>.html | Out-String` (the pipe makes
             PowerShell wait for it)
           - If `PERCH_EXE` is empty, use `perch` in its place.
        4. **Reply** with the link and the local path in one line, noting that the link is private until shared.
           If the command fails, report its message and the local path; don't retry it in a different form.

        Publishing the file again creates a new artifact with a new link, so only re-publish when the piece changed
        and say that the earlier link is now stale.

        """;
}
