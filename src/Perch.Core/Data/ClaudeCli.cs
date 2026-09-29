using System.Diagnostics;

namespace Perch.Data;

/// <summary>
/// Small helpers for constructing Claude Code CLI invocations, shared so the terminal launcher
/// (<see cref="Perch.Platform.ISessionLauncher"/>) and the "copy resume command" clipboard path can never
/// drift apart.
/// </summary>
public static class ClaudeCli
{
    /// <summary>The command that resumes an existing session by id: <c>claude --resume &lt;sessionId&gt;</c>.</summary>
    public static string ResumeCommand(string sessionId) => $"claude --resume {sessionId}";

    /// <summary>A general <c>claude &lt;args&gt;</c> invocation (e.g. <c>args = "auth login"</c>).</summary>
    public static string Command(string args) => $"claude {args}";

    /// <summary>The absolute path of the <c>claude</c> CLI on PATH, or <c>null</c> when it isn't installed. Never the
    /// current directory — see <see cref="ExecutableResolver"/>.</summary>
    public static string? Find() => ExecutableResolver.Find("claude");

    /// <summary>A start info running <c>claude &lt;args&gt;</c> (<paramref name="args"/> must already be safe tokens)
    /// without ever resolving <c>claude</c> against a working directory. Windows: a native <c>claude.exe</c> is exec'd
    /// directly; an npm <c>.cmd</c> shim goes through <c>%SystemRoot%\System32\cmd.exe</c> by absolute path, with
    /// <c>NoDefaultCurrentDirectoryInExePath</c> set so the shim's own bare <c>node</c> lookup can't hit the cwd
    /// either. Elsewhere: a login shell, so the user's full PATH applies (a GUI app's PATH is minimal on macOS) — POSIX
    /// shells don't search the cwd. Throws <see cref="FileNotFoundException"/> on Windows when claude isn't on PATH.</summary>
    public static ProcessStartInfo CreateStartInfo(string args)
    {
        if (!OperatingSystem.IsWindows())
            return new ProcessStartInfo { FileName = "/bin/sh", Arguments = $"-lc \"claude {args}\"" };
        var claude = Find() ?? throw new FileNotFoundException("Couldn't find the `claude` CLI on PATH.");
        return CreateWindowsStartInfo(claude, args, ExecutableResolver.SystemTool("cmd.exe"));
    }

    /// <summary>The <c>cmd.exe /k</c> command line that runs <c>claude &lt;args&gt;</c> in a new terminal, optionally under
    /// <paramref name="configDir"/> as <c>CLAUDE_CONFIG_DIR</c>. The variable travels on the command line (<c>set "…" &amp;&amp;</c>)
    /// because a terminal opened through the shell — and a Windows Terminal tab, which takes the running WT's
    /// environment, not ours — can't be handed an environment block. Null when <paramref name="configDir"/> holds a
    /// character cmd would interpret (<c>% " &amp; | &lt; &gt; ^</c> — the value is passed unquoted, see below) or that
    /// Windows Terminal splits on (<c>;</c>), so the caller falls back to copying the command rather than resuming
    /// under the wrong account.</summary>
    public static string? WindowsCmdLine(string claudePath, string args, string? configDir)
    {
        // cmd keeps a quoted first token when it's the only quoted pair; with a `set "…"` prefix the line no longer
        // starts with a quote, so cmd strips nothing either way. The args are safe tokens.
        var run = $"{QuoteIfSpaced(claudePath)} {args}";
        if (string.IsNullOrEmpty(configDir)) return run;
        if (configDir.IndexOfAny(['%', '"', ';', '&', '|', '<', '>', '^']) >= 0) return null;
        // `set VAR=value&&` — unquoted, with no space before `&&` — because Windows Terminal re-tokenises its command
        // line and drops the quotes of `set "VAR=value" && …`, which leaves cmd storing `value ` WITH the trailing
        // space before the `&&` (a config dir that doesn't exist, so claude starts first-run setup). Unquoted, cmd
        // takes everything up to the `&&`, spaces inside the path included, and wt re-joining the pieces with single
        // spaces reproduces the same line. That's also why every character cmd would interpret is refused above.
        return $"set CLAUDE_CONFIG_DIR={configDir}&& {run}";
    }

    /// <summary>The PowerShell <c>-Command</c> script for the same launch; single-quoted literals, so nothing in the
    /// paths is interpreted (<c>'</c> is doubled).</summary>
    public static string WindowsPowerShellScript(string claudePath, string args, string? configDir)
    {
        static string Lit(string s) => "'" + s.Replace("'", "''") + "'";
        var run = $"& {Lit(claudePath)} {args}";
        // Single quotes only — the whole script rides inside the -Command "…" argument.
        return string.IsNullOrEmpty(configDir) ? run : $"$env:CLAUDE_CONFIG_DIR = {Lit(configDir)}; {run}";
    }

    private static string QuoteIfSpaced(string path) => path.Contains(' ') ? $"\"{path}\"" : path;

    /// <summary>The Windows half of <see cref="CreateStartInfo"/>, over an already-resolved path (split out for tests).</summary>
    internal static ProcessStartInfo CreateWindowsStartInfo(string claudePath, string args, string cmdPath)
    {
        if (Path.GetExtension(claudePath).Equals(".exe", StringComparison.OrdinalIgnoreCase))
            return new ProcessStartInfo { FileName = claudePath, Arguments = args };

        // /s strips exactly the outer quote pair, so the quoted path survives intact whatever it contains.
        var psi = new ProcessStartInfo { FileName = cmdPath, Arguments = $"/d /s /c \"\"{claudePath}\" {args}\"" };
        psi.Environment["NoDefaultCurrentDirectoryInExePath"] = "1";
        return psi;
    }
}
