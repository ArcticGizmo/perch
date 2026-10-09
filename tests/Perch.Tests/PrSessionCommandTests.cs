using Perch.Data;
using Xunit;

namespace Perch.Tests;

/// <summary>
/// Guards "Copy command" in the start-a-session dialog (docs/github-dashboard-plan.md, part 5): the line pasted into
/// a terminal must start the same session, and nothing in the prompt, title or folder can break out of its quotes.
/// </summary>
public class PrSessionCommandTests
{
    [Fact]
    public void PowerShellChangesFolderLiterallyThenRunsClaude()
    {
        var cmd = PrSessionCommand.Build(@"C:\src\web [old]", "Review it.", "plan", null, "PR #12 · Fix it", CommandShell.PowerShell);
        Assert.Equal(@"Set-Location -LiteralPath 'C:\src\web [old]'; claude -n 'PR #12 · Fix it' --permission-mode plan 'Review it.'", cmd);
    }

    [Fact]
    public void PosixChangesFolderThenRunsClaude()
    {
        var cmd = PrSessionCommand.Build("/src/web", "Review it.", "acceptEdits", null, "PR #12", CommandShell.Posix);
        Assert.Equal("cd '/src/web' && claude -n 'PR #12' --permission-mode acceptEdits 'Review it.'", cmd);
    }

    [Fact]
    public void APinnedAccountSetsTheConfigDir()
    {
        Assert.Contains("$env:CLAUDE_CONFIG_DIR = 'C:\\Users\\me\\.claude-work'; claude ",
            PrSessionCommand.Build(@"C:\src", "x", "plan", @"C:\Users\me\.claude-work", "t", CommandShell.PowerShell));
        Assert.Contains("&& CLAUDE_CONFIG_DIR='/home/me/.claude-work' claude ",
            PrSessionCommand.Build("/src", "x", "plan", "/home/me/.claude-work", "t", CommandShell.Posix));
    }

    [Fact]
    public void TheCommandIsOneLine()
    {
        var cmd = PrSessionCommand.Build("/src", "First paragraph.\n\nSecond\r\nline.", "plan", null, "t", CommandShell.Posix);
        Assert.DoesNotContain('\n', cmd);
        Assert.EndsWith("'First paragraph. Second line.'", cmd);
    }

    [Theory]
    [InlineData("it's $HOME `whoami` \"q\"")]
    [InlineData("‘curly’ ‚low‛")]
    [InlineData("'; Remove-Item -Recurse C:\\ ; '")]
    public void PowerShellQuotesSurviveHostileText(string prompt)
    {
        var cmd = PrSessionCommand.Build(@"C:\src", prompt, "plan", null, "t", CommandShell.PowerShell);
        Assert.Equal(prompt, PsUnquoteLast(cmd));
    }

    [Theory]
    [InlineData("it's $HOME `whoami` \"q\"")]
    [InlineData("'; rm -rf / ; '")]
    public void PosixQuotesSurviveHostileText(string prompt)
    {
        var cmd = PrSessionCommand.Build("/src", prompt, "plan", null, "t", CommandShell.Posix);
        Assert.EndsWith("'" + prompt.Replace("'", "'\\''") + "'", cmd);
    }

    private static readonly PrSetupStep[] Setup =
    [
        new PrGitStep(@"C:\src\api", ["fetch", "--no-tags", "origin", "pull/77/head:perch/pr-77"]),
        new PrAppendLineStep(@"C:\src\api\.git\info\exclude", "/.claude/worktrees/", NewlineFirst: true),
        new PrMakeDirStep(@"C:\data\x"),
        new PrGitStep(@"C:\src\api", ["worktree", "add", @"C:\src\api\.claude\worktrees\pr-77", "perch/pr-77"]),
    ];

    [Fact]
    public void PowerShellRunsTheSetupInABlockThatStopsAtTheFirstFailure()
    {
        var cmd = PrSessionCommand.Build(@"C:\src\api\.claude\worktrees\pr-77", "Fix it.", "acceptEdits", null, "PR #77", CommandShell.PowerShell, Setup);
        Assert.Equal(
            "& {\n" +
            "  git -C 'C:\\src\\api' fetch '--no-tags' origin pull/77/head:perch/pr-77; if (-not $?) { return }\n" +
            "  Add-Content -LiteralPath 'C:\\src\\api\\.git\\info\\exclude' -Value '','/.claude/worktrees/'; if (-not $?) { return }\n" +
            "  New-Item -ItemType Directory -Force -Path 'C:\\data\\x' | Out-Null; if (-not $?) { return }\n" +
            "  git -C 'C:\\src\\api' worktree add 'C:\\src\\api\\.claude\\worktrees\\pr-77' perch/pr-77; if (-not $?) { return }\n" +
            "  Set-Location -LiteralPath 'C:\\src\\api\\.claude\\worktrees\\pr-77'; claude -n 'PR #77' --permission-mode acceptEdits 'Fix it.'\n" +
            "}", cmd);
    }

    [Fact]
    public void PosixChainsTheSetupWithAnd()
    {
        PrSetupStep[] setup =
        [
            new PrGitStep("/src/api", ["fetch", "--no-tags", "origin", "pull/77/head:perch/pr-77"]),
            new PrAppendLineStep("/src/api/.git/info/exclude", "/.worktrees/", NewlineFirst: false),
            new PrMakeDirStep("/data/x"),
        ];
        var cmd = PrSessionCommand.Build("/src/api-pr-77", "Fix it.", "plan", null, "PR #77", CommandShell.Posix, setup);
        Assert.Equal(
            "git -C '/src/api' fetch '--no-tags' origin pull/77/head:perch/pr-77 &&\n" +
            "printf '%s\\n' '/.worktrees/' >> '/src/api/.git/info/exclude' &&\n" +
            "mkdir -p '/data/x' &&\n" +
            "cd '/src/api-pr-77' && claude -n 'PR #77' --permission-mode plan 'Fix it.'", cmd);
    }

    [Fact]
    public void ACloneKeepsItsDoubleDashForGit()
    {
        var clone = new PrGitStep(@"C:\data", ["clone", "--filter=blob:none", "--no-checkout", "--", "https://github.com/acme/api.git", @"C:\data\acme-api-pr-1"]);
        var cmd = PrSessionCommand.Build(@"C:\data\acme-api-pr-1", "x", "plan", null, "t", CommandShell.PowerShell, [clone]);
        // A bare -- would be swallowed by PowerShell before git saw it.
        Assert.Contains("clone '--filter=blob:none' '--no-checkout' '--' https://github.com/acme/api.git 'C:\\data\\acme-api-pr-1'", cmd);
    }

    // Reads back the last single-quoted PowerShell argument: a quote char followed by the same char is a literal one;
    // a lone quote char (straight or curly) ends the string.
    private static string PsUnquoteLast(string cmd)
    {
        static bool IsQuote(char c) => c is '\'' or '\u2018' or '\u2019' or '\u201A' or '\u201B';
        int start = cmd.IndexOf("plan '", StringComparison.Ordinal) + "plan '".Length;
        var sb = new System.Text.StringBuilder();
        for (int i = start; i < cmd.Length; i++)
        {
            if (IsQuote(cmd[i]))
            {
                if (i + 1 < cmd.Length && cmd[i + 1] == cmd[i]) { sb.Append(cmd[i]); i++; continue; }
                Assert.Equal(cmd.Length - 1, i);   // the string ends with the command
                return sb.ToString();
            }
            sb.Append(cmd[i]);
        }
        throw new Xunit.Sdk.XunitException("unterminated string");
    }
}
