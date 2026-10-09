namespace Perch.Data;

/// <summary>Which shell a copied command is written for.</summary>
public enum CommandShell { PowerShell, Posix }

/// <summary>
/// "Copy command" in the start-a-session dialog: one line that starts the same session in a terminal. It changes to
/// the folder, then runs <c>claude -n &lt;name&gt; --permission-mode &lt;mode&gt; &lt;prompt&gt;</c>, with the account's
/// <c>CLAUDE_CONFIG_DIR</c> when one is pinned. Pure.
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

    public static string Build(string cwd, string prompt, string permissionMode, string? configDir, string title, CommandShell shell)
    {
        Func<string, string> q = shell == CommandShell.PowerShell ? PsQuote : ShQuote;
        var claude = $"claude -n {q(Flat(title))} --permission-mode {permissionMode} {q(Flat(prompt))}";
        if (shell == CommandShell.PowerShell)
        {
            var env = configDir is { Length: > 0 } ? $"$env:CLAUDE_CONFIG_DIR = {q(configDir)}; " : "";
            return $"Set-Location -LiteralPath {q(cwd)}; {env}{claude}";
        }
        var prefix = configDir is { Length: > 0 } ? $"CLAUDE_CONFIG_DIR={q(configDir)} " : "";
        return $"cd {q(cwd)} && {prefix}{claude}";
    }

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
