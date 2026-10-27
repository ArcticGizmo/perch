using System.Diagnostics;

namespace Perch.Data;

/// <summary>
/// Small helpers for constructing Claude Code CLI invocations, shared so the terminal launcher
/// (<see cref="Perch.Platform.ISessionLauncher"/>) and the "copy resume command" clipboard path can never
/// drift apart.
/// </summary>
public static class ClaudeCli
{
    /// <summary>True for a session id: 8+ ASCII letters, digits, <c>-</c> or <c>_</c>, so it's safe as a bare token on
    /// any command line Perch builds (review fixes CP13).</summary>
    public static bool IsSessionId(string? s) =>
        s is { Length: >= 8 } && s.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_');

    /// <summary>The command that resumes an existing session by id: <c>claude --resume &lt;sessionId&gt;</c>.</summary>
    public static string ResumeCommand(string sessionId) => $"claude --resume {sessionId}";

    /// <summary>A general <c>claude &lt;args&gt;</c> invocation (e.g. <c>args = "auth login"</c>).</summary>
    public static string Command(string args) => $"claude {args}";

    /// <summary>True for an opening prompt that can ride a cmd, Windows Terminal or PowerShell command line as one
    /// quoted argument: letters, digits, spaces and <c>. , - _ / \ : ( )</c> only, not ending in <c>\</c> (it would
    /// escape the closing quote). Nothing a shell interprets inside quotes, and no <c>;</c>, which wt splits on even
    /// there.</summary>
    public static bool IsSafePrompt(string? s) =>
        s is { Length: > 0 } && !s.EndsWith('\\') &&
        s.All(c => char.IsLetterOrDigit(c) || c is ' ' or '.' or ',' or '-' or '_' or '/' or '\\' or ':' or '(' or ')');

    /// <summary>An <see cref="IsSafePrompt"/> string (a prompt, or a plain path) quoted as one argument for
    /// <see cref="CreateStartInfo"/>: double
    /// quotes for the Windows argument string, single quotes inside the POSIX <c>sh -lc "…"</c>.</summary>
    public static string QuotePrompt(string prompt) =>
        OperatingSystem.IsWindows() ? $"\"{prompt}\"" : $"'{prompt}'";

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
    /// under the wrong account. <paramref name="prompt"/>, when set, is the session's opening prompt, appended as one
    /// quoted argument; null too when it isn't <see cref="IsSafePrompt"/>.</summary>
    public static string? WindowsCmdLine(string claudePath, string args, string? configDir, string? prompt = null)
    {
        // cmd keeps a quoted first token when it's the only quoted pair; with a `set "…"` prefix the line no longer
        // starts with a quote, so cmd strips nothing either way. The args are safe tokens.
        var run = $"{QuoteIfSpaced(claudePath)} {args}";
        if (prompt is not null)
        {
            if (!IsSafePrompt(prompt)) return null;
            // A quoted claude path plus a quoted prompt is two quoted pairs on a line that starts with a quote, and
            // `cmd /k` then strips the first and last quote, mangling both. Rare (a space in the install path), so refuse.
            if (string.IsNullOrEmpty(configDir) && claudePath.Contains(' ')) return null;
            run += $" \"{prompt}\"";
        }
        if (string.IsNullOrEmpty(configDir)) return run;
        return SetConfigDir(configDir) is { } set ? $"{set}&& {run}" : null;
    }

    // `set VAR=value&&` — unquoted, with no space before `&&` — because Windows Terminal re-tokenises its command
    // line and drops the quotes of `set "VAR=value" && …`, which leaves cmd storing `value ` WITH the trailing
    // space before the `&&` (a config dir that doesn't exist, so claude starts first-run setup). Unquoted, cmd
    // takes everything up to the `&&`, spaces inside the path included, and wt re-joining the pieces with single
    // spaces reproduces the same line. That's also why every character cmd would interpret is refused.
    private static string? SetConfigDir(string configDir) =>
        configDir.IndexOfAny(['%', '"', ';', '&', '|', '<', '>', '^']) >= 0 ? null : $"set CLAUDE_CONFIG_DIR={configDir}";

    /// <summary>The PowerShell <c>-Command</c> script for the same launch; single-quoted literals, so nothing in the
    /// paths is interpreted (<c>'</c> is doubled). <paramref name="prompt"/> as for <see cref="WindowsCmdLine"/> (the
    /// caller checks <see cref="IsSafePrompt"/>, since no <c>"</c> may reach the surrounding <c>-Command "…"</c>).</summary>
    public static string WindowsPowerShellScript(string claudePath, string args, string? configDir, string? prompt = null)
    {
        var run = $"& {PsLiteral(claudePath)} {args}";
        if (prompt is not null) run += $" {PsLiteral(prompt)}";
        // Single quotes only — the whole script rides inside the -Command "…" argument.
        return string.IsNullOrEmpty(configDir) ? run : $"$env:CLAUDE_CONFIG_DIR = {PsLiteral(configDir)}; {run}";
    }

    private static string PsLiteral(string s) => "'" + s.Replace("'", "''") + "'";

    private static string QuoteIfSpaced(string path) => path.Contains(' ') ? $"\"{path}\"" : path;

    /// <summary>
    /// Windows Terminal's quoted <c>-d</c> value for <paramref name="cwd"/> (review fixes CP13). A trailing
    /// backslash would escape the closing quote (<c>-d "C:\"</c> reads as <c>C:"</c> and swallows the rest of the
    /// line), so the folder gets a harmless trailing <c>.</c> instead. wt splits its command line into subcommands
    /// at every <c>;</c>, even inside quotes, so each one is escaped as <c>\;</c>.
    /// </summary>
    public static string WindowsTerminalStartDir(string cwd)
    {
        var dir = cwd.EndsWith('\\') ? cwd + "." : cwd;
        return "\"" + dir.Replace(";", "\\;") + "\"";
    }

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
