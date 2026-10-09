namespace Perch.Data;

/// <summary>Which shell a copied command is written for.</summary>
public enum CommandShell { PowerShell, Posix }

/// <summary>
/// "Copy command" in the start-a-session dialog: the command that starts the same session in a terminal. It changes
/// to the folder, then runs <c>claude -n &lt;name&gt; --permission-mode &lt;mode&gt; &lt;prompt&gt;</c>, with the
/// account's <c>CLAUDE_CONFIG_DIR</c> when one is pinned. With <see cref="PrSetupStep"/>s (a worktree to fetch and
/// add, a clone, a scratch folder), those come first, one per line, and the first that fails stops the rest: POSIX
/// chains the lines with <c>&amp;&amp;</c>; PowerShell runs them in a <c>&amp; { … }</c> block that returns when
/// <c>$?</c> is false (the folder change still sticks, since the location isn't scoped). Without steps it's one
/// line. Pure.
///
/// <para>Every value is single-quoted, which is literal in both PowerShell and POSIX sh, so a <c>$</c>, a backtick
/// or a <c>"</c> in the prompt stays text. PowerShell also treats the curly single quotes as quotes, so those are
/// doubled too. The prompt's line breaks become spaces, so the paste is a single command. The folder goes through
/// <c>Set-Location -LiteralPath</c> on PowerShell, where a plain <c>cd</c> would read <c>[</c> in a path as a
/// wildcard. One known gap: Windows PowerShell 5.1 strips <c>"</c> from arguments it passes to a program. Perch's
/// own prompt text has none, so only a quote the user typed is affected.</para>
/// </summary>
public static class PrSessionCommand
{
    /// <summary>The shell a terminal opens by default on this OS.</summary>
    public static CommandShell DefaultShell => OperatingSystem.IsWindows() ? CommandShell.PowerShell : CommandShell.Posix;

    public static string Build(string cwd, string prompt, string permissionMode, string? configDir, string title, CommandShell shell,
        IReadOnlyList<PrSetupStep>? setup = null)
    {
        Func<string, string> q = shell == CommandShell.PowerShell ? PsQuote : ShQuote;
        var claude = $"claude -n {q(Flat(title))} --permission-mode {permissionMode} {q(Flat(prompt))}";
        string start;
        if (shell == CommandShell.PowerShell)
        {
            var env = configDir is { Length: > 0 } ? $"$env:CLAUDE_CONFIG_DIR = {q(configDir)}; " : "";
            start = $"Set-Location -LiteralPath {q(cwd)}; {env}{claude}";
        }
        else
        {
            var prefix = configDir is { Length: > 0 } ? $"CLAUDE_CONFIG_DIR={q(configDir)} " : "";
            start = $"cd {q(cwd)} && {prefix}{claude}";
        }
        if (setup is not { Count: > 0 }) return start;

        var lines = setup.Select(s => Step(s, shell)).ToList();
        if (shell == CommandShell.PowerShell)
            return "& {\n" + string.Concat(lines.Select(l => $"  {l}; if (-not $?) {{ return }}\n")) + $"  {start}\n}}";
        return string.Concat(lines.Select(l => $"{l} &&\n")) + start;
    }

    // One setup step as a shell line.
    private static string Step(PrSetupStep step, CommandShell shell)
    {
        bool ps = shell == CommandShell.PowerShell;
        Func<string, string> q = ps ? PsQuote : ShQuote;
        return step switch
        {
            PrGitStep g => $"git -C {q(g.Dir)} {string.Join(' ', g.Args.Select(a => Arg(a, shell)))}",
            PrMakeDirStep m => ps ? $"New-Item -ItemType Directory -Force -Path {q(m.Path)} | Out-Null" : $"mkdir -p {q(m.Path)}",
            PrAppendLineStep a => ps
                ? $"Add-Content -LiteralPath {q(a.File)} -Value {(a.NewlineFirst ? "''," : "")}{q(a.Line)}"
                : $"printf '{(a.NewlineFirst ? "\\n" : "")}%s\\n' {q(a.Line)} >> {q(a.File)}",
            _ => throw new ArgumentOutOfRangeException(nameof(step)),
        };
    }

    // An argument bare when it's a plain word ("fetch", "pull/12/head:perch/pr-12"), else quoted. A leading "-" is
    // always quoted: PowerShell eats a bare "--" before a native program, and a quoted one still reads as an option
    // to git.
    private static string Arg(string a, CommandShell shell) =>
        a.Length > 0 && char.IsAsciiLetterOrDigit(a[0]) && a.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '/' or ':' or '=' or '+' or '-')
            ? a
            : shell == CommandShell.PowerShell ? PsQuote(a) : ShQuote(a);

    // Line breaks and runs of whitespace become single spaces.
    private static string Flat(string text) => PrSessionPrompts.OneLine(text, int.MaxValue);

    // PowerShell: inside '…' the only special character is the quote itself (straight or curly), escaped by doubling.
    private static string PsQuote(string s)
    {
        var sb = new System.Text.StringBuilder(s.Length + 2).Append('\'');
        foreach (var c in s)
        {
            if (c is '\'' or '‘' or '’' or '‚' or '‛') sb.Append(c);
            sb.Append(c);
        }
        return sb.Append('\'').ToString();
    }

    // POSIX sh: nothing is special inside '…', and a quote is closed, escaped, reopened.
    private static string ShQuote(string s) => "'" + s.Replace("'", "'\\''") + "'";
}
