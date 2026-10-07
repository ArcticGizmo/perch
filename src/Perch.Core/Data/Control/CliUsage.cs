namespace Perch.Data.Control;

/// <summary>
/// The informational half of the <c>perch</c> command line: <c>--help</c> and <c>--version</c>, which print and exit
/// without starting the tray or opening a session. Checked before anything else in <c>Program.Main</c>, except that
/// a subcommand (<c>perch statusline --help</c>) keeps its own arguments and its own help.
/// </summary>
internal static class CliUsage
{
    /// <summary>The verbs <c>Program.Main</c> dispatches before any session parsing. Their arguments are theirs.</summary>
    private static readonly HashSet<string> Subcommands = new(StringComparer.OrdinalIgnoreCase)
    {
        "render", "bench-roost", "configdirs", "statusline", "handle", "uninstall", "replay",
    };

    /// <summary>True for <c>perch --help</c>, <c>-h</c>, <c>-?</c>, <c>/?</c> or <c>perch help</c>, unless a subcommand leads.</summary>
    public static bool IsHelp(IReadOnlyList<string> args) =>
        args.Count > 0 && !Subcommands.Contains(args[0])
        && (string.Equals(args[0], "help", StringComparison.OrdinalIgnoreCase)
            || args.Any(a => a is "--help" or "-h" or "-?" or "/?"));

    /// <summary>True for <c>perch --version</c> or <c>-v</c>, unless a subcommand leads.</summary>
    public static bool IsVersion(IReadOnlyList<string> args) =>
        args.Count > 0 && !Subcommands.Contains(args[0])
        && args.Any(a => a is "--version" or "-v" or "-V");

    /// <summary><c>perch 1.2.3</c>, marked when this is the dev profile (its own tray, settings and pipe).</summary>
    public static string VersionLine(string version) => $"perch {version}" + (AppProfile.IsDev ? " (dev)" : "");

    public static string Help(string version) => VersionLine(version) + """
         - watches your Claude Code sessions from the tray, and opens them in its own window.

        Usage:
          perch [dir] [options]       open a new session in dir (default: the current directory),
                                      starting the tray in the background if it isn't running
          perch -c, --continue        continue the most recent session in this directory
          perch -r, --resume [id]     resume a session by id, or pick one from the recent list
          perch --session-id <id>     resume the session with this id

        Session options:
          --model <name>              model to start the session with
          --permission-mode <mode>    default | auto | plan | acceptEdits | bypassPermissions

        Tray:
          perch --tray                start the tray only, without opening a session

        Commands:
          perch statusline [verb]     design and switch Claude Code status lines (perch statusline help)
          perch configdirs            list the Claude config directories Perch has discovered
          perch uninstall             remove the PATH entry, login item and Claude Code hooks
                                      (for a portable copy, before deleting its folder)

        Diagnostics:
          perch render <dir> [theme]  render every owner-drawn surface to PNG
          perch replay <recording>    drive the app through a recorded session
          perch bench-roost [turns]   time the Roost's drag/drop/refresh passes

          perch -h, --help            show this help
          perch -v, --version         show the version
        """;
}
