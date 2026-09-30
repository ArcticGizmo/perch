namespace Perch.Data;

using System.Diagnostics;

/// <summary>
/// Starts a PATH tool that may be a Windows <c>.cmd</c> shim without letting cmd.exe parse Perch's arguments
/// (review fixes CP12). Running a shim means running cmd, and .NET only quotes an argument that holds whitespace or
/// a quote. So a file named <c>x&amp;calc&amp;.md</c> passed to "Open in VS Code" became <c>code.cmd x&amp;calc&amp;.md</c>,
/// and cmd ran <c>calc</c>. A harmless <c>C:\work\R&amp;D</c> broke the GitKraken launch the same way.
///
/// The CLIs Perch drives (VS Code's <c>code</c>, GitKraken's <c>gitkraken</c>) are Electron shims of one shape:
/// set <c>ELECTRON_RUN_AS_NODE=1</c>, then <c>"%~dp0..\App.exe" "%~dp0..\cli.js" %*</c>, sometimes via one more
/// <c>.cmd</c> hop. <see cref="StartInfo"/> reads such a shim and starts the <c>.exe</c> directly with an argument
/// list and the shim's environment, so cmd never sees the arguments. A shim it can't read falls back to cmd with
/// every argument quoted, and refuses outright when an argument holds a cmd metacharacter that quoting can't
/// neutralise.
/// </summary>
public static class CmdShim
{
    // Characters cmd acts on in a command line. Inside quotes &, |, <, > and ^ are literal, but % still expands
    // (and ! does under delayed expansion), and a quote would end the quoting. So the fallback refuses them all.
    private static readonly char[] CmdMeta = ['%', '!', '"', '&', '|', '^', '<', '>'];

    private const int MaxHops = 3;

    /// <summary>
    /// The start info for running <paramref name="tool"/> (an absolute path, as <see cref="ExecutableResolver"/>
    /// resolves it) with <paramref name="args"/>, windowless and without a shell. Null when the tool is a shim Perch
    /// can't see through and an argument can't be passed safely through cmd; the caller then skips the launch.
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

        if (Resolve(tool) is { } target)
        {
            psi.FileName = target.Exe;
            foreach (var a in target.LeadingArgs) psi.ArgumentList.Add(a);
            foreach (var a in args) psi.ArgumentList.Add(a);
            foreach (var (name, value) in target.Env)
            {
                if (value.Length == 0) psi.Environment.Remove(name);
                else psi.Environment[name] = value;
            }
            return psi;
        }

        if (CmdCommandLine(tool, args) is not { } line)
            return null;
        psi.FileName = ExecutableResolver.SystemTool("cmd.exe");
        psi.Arguments = line;
        return psi;
    }

    /// <summary>
    /// The cmd fallback's argument string: <c>/d /s /c ""tool" "arg" …"</c>. <c>/d</c> skips AutoRun, and <c>/s</c>
    /// makes cmd strip exactly the outer pair of quotes, so each inner quoted token survives. Null when the tool or
    /// an argument holds a character cmd would still act on. Internal for unit testing.
    /// </summary>
    internal static string? CmdCommandLine(string tool, IReadOnlyList<string> args)
    {
        if (tool.IndexOfAny(CmdMeta) >= 0 || args.Any(a => a.IndexOfAny(CmdMeta) >= 0 || a.Contains('\n') || a.Contains('\r')))
            return null;
        var inner = string.Join(" ", args.Prepend(tool).Select(a => $"\"{a}\""));
        return $"/d /s /c \"{inner}\"";
    }

    /// <summary>What a shim ultimately runs: an <c>.exe</c>, the arguments it puts before the caller's, and the
    /// environment it sets (an empty value means unset).</summary>
    internal sealed record Target(string Exe, IReadOnlyList<string> LeadingArgs, IReadOnlyList<(string Name, string Value)> Env);

    /// <summary>
    /// Reads an Electron-style shim, following <c>.cmd</c> hops, down to the <c>.exe</c> it runs. Only a shim made
    /// entirely of lines Perch understands qualifies: <c>@echo off</c>, <c>setlocal</c>/<c>endlocal</c>,
    /// <c>set NAME=VALUE</c>, comments, error-level checks, and exactly one launch line of quoted tokens followed by
    /// <c>%*</c>, whose only variable is <c>%~dp0</c>. Anything else is null, so nothing is guessed. Internal for
    /// unit testing.
    /// </summary>
    internal static Target? Resolve(string shim) => Resolve(shim, 0, []);

    private static Target? Resolve(string shim, int hop, List<(string, string)> env)
    {
        if (hop >= MaxHops || !File.Exists(shim)) return null;
        string[] lines;
        try { lines = File.ReadAllLines(shim); }
        catch { return null; }

        var dir = Path.GetDirectoryName(Path.GetFullPath(shim)) + Path.DirectorySeparatorChar;
        var myEnv = new List<(string, string)>(env);
        List<string>? launch = null;

        foreach (var raw in lines)
        {
            var line = raw.Trim();
            var lower = line.ToLowerInvariant();
            if (line.Length == 0 || lower is "@echo off" or "setlocal" or "endlocal"
                || lower.StartsWith("rem ") || lower.StartsWith("::")
                || lower.StartsWith("if %errorlevel%") || lower.StartsWith("exit /b"))
                continue;

            if (lower.StartsWith("set ") && launch is null)
            {
                var assign = line[4..].Trim().Trim('"');
                int eq = assign.IndexOf('=');
                if (eq <= 0 || assign.IndexOf('%') >= 0) return null;
                myEnv.Add((assign[..eq], assign[(eq + 1)..]));
                continue;
            }

            if (launch is null && ParseLaunch(line, dir) is { } tokens)
            {
                launch = tokens;
                continue;
            }
            return null;   // a line we don't understand: don't guess what the shim does
        }

        if (launch is null) return null;
        var first = launch[0];
        if (launch.Count == 1 && IsScript(first))
            return Resolve(first, hop + 1, myEnv);
        if (!first.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) || !File.Exists(first))
            return null;
        // The script argument (VS Code's cli.js, or GitKraken's inside app.asar, which isn't a real directory) is
        // only handed to the exe, so it isn't required to exist on disk.
        return new Target(first, launch.Skip(1).ToList(), myEnv);
    }

    // `"tok" "tok" %*` → the tokens with %~dp0 expanded and made absolute; null for any other shape.
    private static List<string>? ParseLaunch(string line, string dir)
    {
        if (!line.EndsWith("%*", StringComparison.Ordinal)) return null;
        var body = line[..^2].TrimEnd();
        var tokens = new List<string>();
        int i = 0;
        while (i < body.Length)
        {
            if (body[i] == ' ') { i++; continue; }
            if (body[i] != '"') return null;
            int close = body.IndexOf('"', i + 1);
            if (close < 0) return null;
            var tok = body[(i + 1)..close];
            if (!tok.StartsWith("%~dp0", StringComparison.OrdinalIgnoreCase)) return null;
            tok = dir + tok[5..].TrimStart('\\', '/');
            if (tok.IndexOf('%') >= 0) return null;
            try { tokens.Add(Path.GetFullPath(tok)); }
            catch { return null; }
            i = close + 1;
        }
        return tokens.Count is >= 1 and <= 2 ? tokens : null;
    }

    private static bool IsScript(string path) =>
        path.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".bat", StringComparison.OrdinalIgnoreCase);
}
