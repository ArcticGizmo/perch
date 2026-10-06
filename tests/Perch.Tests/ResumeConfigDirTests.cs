using Perch.Data;
using Xunit;

namespace Perch.Tests;

/// <summary>
/// Resuming a closed session must run <c>claude --resume</c> under the config dir that owns its transcript: resumed
/// under the primary's environment, a session from another config dir (another account) isn't found.
/// <see cref="TranscriptLocator.ResumeConfigRoot"/> picks that dir; the suite runs under a pinned
/// <c>CLAUDE_CONFIG_DIR</c>, so the multi-dir case is driven through <see cref="ClaudeConfigSet.SetForTesting"/>.
/// </summary>
public sealed class ResumeConfigDirTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("perch-resume-dir-").FullName;
    private const string Cwd = @"C:\proj\app";

    public void Dispose()
    {
        ClaudeConfigSet.ResetForTesting();
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private ClaudeConfigDir MakeDir(string name)
    {
        var root = Path.Combine(_root, name);
        Directory.CreateDirectory(Path.Combine(root, "projects"));
        return new ClaudeConfigDir(root);
    }

    private static string SeedTranscript(ClaudeConfigDir dir, string sessionId, string? projectFolder = null)
    {
        var folder = Path.Combine(dir.ProjectsDir, projectFolder ?? TranscriptLocator.EncodeProjectDir(Cwd));
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, sessionId + ".jsonl");
        File.WriteAllText(path, "{}\n");
        return path;
    }

    [Fact]
    public void A_session_in_a_non_primary_dir_resumes_under_that_dir()
    {
        var primary = MakeDir("primary");
        var work = MakeDir("work");
        SeedTranscript(work, "s-work");
        ClaudeConfigSet.SetForTesting(new[] { primary, work });

        Assert.Equal(work.Root, TranscriptLocator.ResumeConfigRoot("s-work", Cwd));
    }

    [Fact]
    public void A_session_in_the_primary_inherits_the_environment()
    {
        // Null, not the primary's root: pinning CLAUDE_CONFIG_DIR to the primary would move Claude's .claude.json.
        var primary = MakeDir("primary");
        var work = MakeDir("work");
        SeedTranscript(primary, "s-home");
        ClaudeConfigSet.SetForTesting(new[] { primary, work });

        Assert.Null(TranscriptLocator.ResumeConfigRoot("s-home", Cwd));
    }

    [Fact]
    public void Found_by_the_all_folders_scan_when_the_cwd_encoding_misses()
    {
        var primary = MakeDir("primary");
        var work = MakeDir("work");
        SeedTranscript(work, "s-moved", projectFolder: "C--somewhere-else");
        ClaudeConfigSet.SetForTesting(new[] { primary, work });

        Assert.Equal(work.Root, TranscriptLocator.ResumeConfigRoot("s-moved", Cwd));
    }

    [Fact]
    public void A_shared_projects_tree_resumes_under_the_dir_the_hook_reported()
    {
        // projects/ junctioned across every dir: the transcript is found under the primary first, so the tree
        // can't say which account ran the session. The hook's {sid}.configdir marker can.
        var primary = MakeDir("primary");
        var work = MakeDir("work");
        SeedTranscript(primary, "s-shared");
        Directory.CreateDirectory(primary.SessionsDir);
        File.WriteAllText(Path.Combine(primary.SessionsDir, "s-shared.configdir"), work.Root);
        ClaudeConfigSet.SetForTesting(new[] { primary, work });

        Assert.Equal(work.Root, TranscriptLocator.ResumeConfigRoot("s-shared", Cwd));
    }

    [Fact]
    public void An_unknown_session_inherits()
    {
        ClaudeConfigSet.SetForTesting(new[] { MakeDir("primary"), MakeDir("work") });
        Assert.Null(TranscriptLocator.ResumeConfigRoot("nope", Cwd));
        Assert.Null(TranscriptLocator.ResumeConfigRoot("", Cwd));
    }

    [Fact]
    public void OwningConfigDir_maps_a_transcript_to_its_root()
    {
        var primary = MakeDir("primary");
        var work = MakeDir("work");
        var path = SeedTranscript(work, "s1");
        ClaudeConfigSet.SetForTesting(new[] { primary, work });

        Assert.Equal(work, TranscriptLocator.OwningConfigDir(path));
        Assert.Null(TranscriptLocator.OwningConfigDir(Path.Combine(_root, "elsewhere", "projects", "x", "s.jsonl")));
        Assert.Null(TranscriptLocator.OwningConfigDir(null));
    }
}

/// <summary>The terminal command lines <see cref="ClaudeCli"/> builds for a reopen: the account travels as
/// <c>CLAUDE_CONFIG_DIR</c> on the command line (a shell-opened terminal can't be given an environment block).</summary>
public class ClaudeCliTerminalCommandTests
{
    private const string Claude = @"C:\Users\x\AppData\Roaming\npm\claude.cmd";

    [Fact]
    public void Cmd_line_without_a_config_dir_is_just_the_command() =>
        Assert.Equal(Claude + " --resume abc", ClaudeCli.WindowsCmdLine(Claude, "--resume abc", null));

    [Fact]
    public void Cmd_line_sets_the_config_dir_first()
    {
        var line = ClaudeCli.WindowsCmdLine(Claude, "--resume abc", @"C:\Users\x\.claude-work")!;
        // Unquoted and butted against `&&`: no space for cmd to fold into the value.
        Assert.StartsWith(@"set CLAUDE_CONFIG_DIR=C:\Users\x\.claude-work&& ", line);
        Assert.EndsWith("&& " + Claude + " --resume abc", line);
    }

    // The bug this guards: Windows Terminal re-tokenises its command line (CommandLineToArgvW-style) and re-joins the
    // pieces, quoting any that contain a space. `set "X=dir" && …` came out as `set X=dir && …`, so cmd stored "dir "
    // — trailing space — and claude started first-run setup in a folder that doesn't exist.
    [Theory]
    [InlineData(@"C:\Users\x\.claude-work")]
    [InlineData(@"C:\Users\A B\.claude work")]   // spaces inside the path survive too
    public void Cmd_line_survives_windows_terminal_retokenising(string dir)
    {
        var line = ClaudeCli.WindowsCmdLine(Claude, "--resume abc", dir)!;
        var afterWt = string.Join(' ', SplitLikeArgv(line).Select(a => a.Contains(' ') ? $"\"{a}\"" : a));
        Assert.Equal(dir, CmdSetValue(afterWt));
    }

    // The value cmd's `set NAME=value&&` assigns: everything after '=' up to the first unquoted `&&`.
    private static string CmdSetValue(string line)
    {
        var eq = line.IndexOf('=');
        var amp = line.IndexOf("&&", eq, StringComparison.Ordinal);
        return line[(eq + 1)..amp];
    }

    // A minimal CommandLineToArgvW: whitespace splits, double quotes group (and are removed).
    private static List<string> SplitLikeArgv(string s)
    {
        var args = new List<string>();
        var cur = new System.Text.StringBuilder();
        bool inQuotes = false, any = false;
        foreach (var ch in s)
        {
            if (ch == '"') { inQuotes = !inQuotes; any = true; continue; }
            if (char.IsWhiteSpace(ch) && !inQuotes)
            {
                if (any) { args.Add(cur.ToString()); cur.Clear(); any = false; }
                continue;
            }
            cur.Append(ch); any = true;
        }
        if (any) args.Add(cur.ToString());
        return args;
    }

    [Fact]
    public void Cmd_line_quotes_a_spaced_claude_path() =>
        Assert.Equal(@"""C:\A B\claude.cmd"" --resume abc", ClaudeCli.WindowsCmdLine(@"C:\A B\claude.cmd", "--resume abc", null));

    [Theory]
    [InlineData(@"C:\%USERNAME%\.claude")]   // cmd expands % even inside quotes
    [InlineData(@"C:\a;b\.claude")]          // Windows Terminal splits its command line on ;
    [InlineData(@"C:\R&D\.claude")]          // passed unquoted, so cmd would treat & as a separator
    public void Cmd_line_refuses_a_config_dir_it_cant_pass_safely(string dir) =>
        Assert.Null(ClaudeCli.WindowsCmdLine(Claude, "--resume abc", dir));

    // Review fixes CP13: wt's -d value. A trailing backslash escaped the closing quote (`-d "C:\"` read as `C:"` and
    // swallowed the rest of the line), and wt splits subcommands at every `;`, quoted or not.
    [Theory]
    [InlineData(@"C:\work\repo", @"""C:\work\repo""")]
    [InlineData(@"C:\work\my repo", @"""C:\work\my repo""")]
    [InlineData(@"C:\", @"""C:\.""")]
    [InlineData(@"C:\work\repo\", @"""C:\work\repo\.""")]
    [InlineData(@"C:\a;b", @"""C:\a\;b""")]
    public void Windows_terminal_start_dir_survives_a_trailing_backslash_and_semicolons(string cwd, string expected) =>
        Assert.Equal(expected, ClaudeCli.WindowsTerminalStartDir(cwd));

    // Review fixes CP13: a reopen puts the id unquoted on a cmd / PowerShell / wt command line.
    [Theory]
    [InlineData("5b4d131d-dfd4-4103-860c-f5c96094b598", true)]
    [InlineData("abc12345_x", true)]
    [InlineData("short", false)]
    [InlineData("5b4d131d; calc", false)]
    [InlineData("5b4d131d&calc", false)]
    [InlineData("5b4d131d`n", false)]
    [InlineData("$(calc)00", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void Only_a_plain_session_id_is_passed_to_a_terminal(string? id, bool ok) =>
        Assert.Equal(ok, ClaudeCli.IsSessionId(id));

    [Fact]
    public void PowerShell_script_sets_the_env_var_with_literal_quoting()
    {
        Assert.Equal("& '" + Claude + "' --resume abc", ClaudeCli.WindowsPowerShellScript(Claude, "--resume abc", null));
        var script = ClaudeCli.WindowsPowerShellScript(Claude, "--resume abc", @"C:\O'Brien\.claude-work");
        Assert.StartsWith(@"$env:CLAUDE_CONFIG_DIR = 'C:\O''Brien\.claude-work'; ", script);
        Assert.EndsWith("; & '" + Claude + "' --resume abc", script);
        Assert.DoesNotContain("\"", script);   // it rides inside -Command "…"
    }
}
