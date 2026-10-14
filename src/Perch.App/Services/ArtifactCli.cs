using Perch.Data;

namespace Perch.Avalonia.Services;

/// <summary>
/// <c>perch artifact publish &lt;file&gt;</c>: publishes a local page as a private claude.ai artifact through a
/// background Claude Code session (<see cref="ArtifactPublisher"/>) and prints its link. It's what the bundled
/// <c>publish-artifact</c> skill runs from inside a Perch-controlled session, whose headless CLI has no Artifact tool.
/// Runs in the current directory under the inherited <c>CLAUDE_CONFIG_DIR</c> (the calling session's account). Never
/// starts the tray. Exit code 0 with the URL on stdout; 1 with the reason on stderr; 2 for a usage error.
/// </summary>
internal static class ArtifactCli
{
    private const string Usage = "usage: perch artifact publish <file>   (an .html, .htm, .md, .markdown or .svg page)";

    public static int Run(string[] args)
    {
        if (args.Length != 3 || !string.Equals(args[1], "publish", StringComparison.OrdinalIgnoreCase))
        {
            Console.Error.WriteLine(Usage);
            return 2;
        }

        var cwd = Environment.CurrentDirectory;
        string path;
        try { path = Path.GetFullPath(args[2], cwd); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            Console.Error.WriteLine($"not a usable path: {args[2]}");
            return 2;
        }
        if (!ArtifactPublish.IsPublishable(path))
        {
            Console.Error.WriteLine(Usage);
            return 2;
        }

        var configDir = Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR");
        // Inside a Perch-controlled session: the session this publish is for (its row and artifact glyph).
        var parent = Environment.GetEnvironmentVariable(PerchSessionPlugin.SessionEnvVar);
        var outcome = ArtifactPublisher.PublishAsync(cwd, path, string.IsNullOrWhiteSpace(configDir) ? null : configDir,
            ClaudeCli.IsSessionId(parent) ? parent : null).GetAwaiter().GetResult();
        if (outcome.Artifact is { } a)
        {
            Console.WriteLine(a.Url);
            return 0;
        }
        Console.Error.WriteLine($"couldn't publish {path}: {outcome.Error}" +
                                (outcome.BackgroundId is { } bg ? $" (claude attach {bg} shows what happened)" : ""));
        return 1;
    }
}
