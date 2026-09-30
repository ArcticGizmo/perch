namespace Perch.Data;

using System.Diagnostics;

/// <summary>
/// Starts a PATH tool that may be a Windows <c>.cmd</c> shim, such as VS Code's <c>code</c> or GitKraken's
/// <c>gitkraken</c>, without letting cmd.exe act on Perch's arguments (review fixes CP12). Windows can only run a
/// batch file through cmd, which re-parses the whole command line: <c>&amp;</c> and <c>|</c> split it into separate
/// commands, and <c>%VAR%</c> expands. .NET only quotes an argument that holds whitespace or a quote, so a file named
/// <c>x&amp;calc&amp;.md</c> passed to "Open in VS Code" ran <c>calc</c>. A harmless <c>C:\work\R&amp;D</c> broke the
/// GitKraken launch the same way.
///
/// So a shim runs as <c>cmd /d /s /c ""tool" "arg" …"</c>, with every argument quoted, which makes <c>&amp; | &lt; &gt; ^</c>
/// literal. The launch is <b>refused</b> when the tool or an argument holds a character quoting can't neutralise:
/// <c>%</c> still expands inside quotes, <c>!</c> does under delayed expansion, and a <c>"</c> would end the
/// quoting. The simpler rule refuses all of them, so a file with one in its name isn't opened this way; callers fall
/// back or skip. A real <c>.exe</c> is started directly with an argument list.
/// </summary>
public static class CmdShim
{
    private static readonly char[] CmdMeta = ['%', '!', '"', '&', '|', '^', '<', '>'];

    /// <summary>
    /// The start info for running <paramref name="tool"/> (an absolute path, as <see cref="ExecutableResolver"/>
    /// resolves it) with <paramref name="args"/>, windowless. Null when the tool is a shim and an argument can't
    /// pass through cmd safely; the caller then doesn't launch.
    /// </summary>
    public static ProcessStartInfo? StartInfo(string tool, IReadOnlyList<string> args)
    {
        var psi = new ProcessStartInfo { UseShellExecute = false, CreateNoWindow = true };

        if (!OperatingSystem.IsWindows() || !IsScript(tool))
        {
            psi.FileName = tool;
            foreach (var a in args) psi.ArgumentList.Add(a);
            return psi;
        }

        if (CmdCommandLine(tool, args) is not { } line)
            return null;
        psi.FileName = ExecutableResolver.SystemTool("cmd.exe");
        psi.Arguments = line;
        return psi;
    }

    /// <summary>
    /// cmd's argument string: <c>/d /s /c ""tool" "arg" …"</c>. <c>/d</c> skips AutoRun, and <c>/s</c> makes cmd strip
    /// exactly the outer pair of quotes, so each inner quoted token survives. Null when the tool or an argument holds
    /// a character cmd would still act on, or a line break. Internal for unit testing.
    /// </summary>
    internal static string? CmdCommandLine(string tool, IReadOnlyList<string> args)
    {
        if (tool.IndexOfAny(CmdMeta) >= 0 || args.Any(a => a.IndexOfAny(CmdMeta) >= 0 || a.Contains('\n') || a.Contains('\r')))
            return null;
        var inner = string.Join(" ", args.Prepend(tool).Select(a => $"\"{a}\""));
        return $"/d /s /c \"{inner}\"";
    }

    private static bool IsScript(string path) =>
        path.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".bat", StringComparison.OrdinalIgnoreCase);
}
