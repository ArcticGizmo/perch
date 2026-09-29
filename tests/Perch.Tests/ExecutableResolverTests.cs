using Perch.Data;
using Xunit;

namespace Perch.Tests;

/// <summary>
/// <see cref="ExecutableResolver"/> is the CP7 fix (docs/review-fixes-plan.md): a bare command name must resolve
/// from PATH only — never from the current directory or a relative PATH entry, which is how a <c>git.exe</c> /
/// <c>claude.cmd</c> committed to a repo would otherwise run in place of the real tool. These drive the pure search
/// with a synthetic PATH over temp directories, so they don't depend on the host's PATH.
/// </summary>
public class ExecutableResolverTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "perch-resolver-" + Guid.NewGuid().ToString("N"));
    private readonly string _binA;
    private readonly string _binB;

    public ExecutableResolverTests()
    {
        _binA = Directory.CreateDirectory(Path.Combine(_root, "a")).FullName;
        _binB = Directory.CreateDirectory(Path.Combine(_root, "b")).FullName;
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private static string Touch(string dir, string name)
    {
        var p = Path.Combine(dir, name);
        File.WriteAllText(p, "");
        return p;
    }

    private static string PathOf(params string[] dirs) => string.Join(Path.PathSeparator, dirs);

    [Fact]
    public void Finds_the_first_PATH_entry_that_has_it()
    {
        Touch(_binB, "tool.exe");
        var first = Touch(_binA, "tool.exe");
        Assert.Equal(first, ExecutableResolver.Find("tool", PathOf(_binA, _binB), ".EXE;.CMD", windows: true));
    }

    [Fact]
    public void Windows_tries_PATHEXT_in_order_within_a_directory()
    {
        Touch(_binA, "claude.cmd");
        var exe = Touch(_binA, "claude.exe");
        Assert.Equal(exe, ExecutableResolver.Find("claude", PathOf(_binA), ".COM;.EXE;.BAT;.CMD", windows: true));
    }

    [Fact]
    public void Windows_never_returns_the_extensionless_npm_sh_script()
    {
        // npm drops an extensionless POSIX `claude` script beside claude.cmd; cmd can't run it, so only the shim counts.
        Touch(_binA, "claude");
        var shim = Touch(_binA, "claude.cmd");
        Assert.Equal(shim, ExecutableResolver.Find("claude", PathOf(_binA), ".EXE;.CMD", windows: true));
    }

    [Fact]
    public void A_name_that_already_has_a_PATHEXT_extension_is_tried_as_is()
    {
        var git = Touch(_binA, "git.exe");
        Assert.Equal(git, ExecutableResolver.Find("git.exe", PathOf(_binA), ".EXE;.CMD", windows: true));
    }

    [Fact]
    public void Relative_PATH_entries_are_skipped_even_when_the_file_exists_there()
    {
        // A relative entry resolves against the current directory — the attack surface. Plant the tool in a
        // directory relative to the test's cwd and name it relatively on PATH: it must not be found.
        var relName = "perch-rel-" + Guid.NewGuid().ToString("N");
        var relDir = Directory.CreateDirectory(Path.Combine(Environment.CurrentDirectory, relName)).FullName;
        try
        {
            Touch(relDir, "tool.exe");
            Assert.Null(ExecutableResolver.Find("tool", PathOf(relName, "."), ".EXE", windows: true));
        }
        finally { try { Directory.Delete(relDir, recursive: true); } catch { } }
    }

    [Fact]
    public void The_current_directory_is_never_searched()
    {
        // No "." on PATH, a tool planted in the cwd: not found.
        var planted = Path.Combine(Environment.CurrentDirectory, "perch-planted-" + Guid.NewGuid().ToString("N") + ".exe");
        File.WriteAllText(planted, "");
        try
        {
            var name = Path.GetFileNameWithoutExtension(planted);
            Assert.Null(ExecutableResolver.Find(name, PathOf(_binA), ".EXE", windows: true));
        }
        finally { File.Delete(planted); }
    }

    [Fact]
    public void Quoted_PATH_entries_are_unquoted()
    {
        var exe = Touch(_binA, "tool.exe");
        Assert.Equal(exe, ExecutableResolver.Find("tool", $"\"{_binA}\"", ".EXE", windows: true));
    }

    [Fact]
    public void A_name_with_a_relative_directory_part_is_refused()
    {
        Touch(_binA, "tool.exe");
        Assert.Null(ExecutableResolver.Find(Path.Combine("a", "tool.exe"), PathOf(_root), ".EXE", windows: true));
        Assert.Null(ExecutableResolver.Find(@".\tool.exe", PathOf(_binA), ".EXE", windows: true));
    }

    [Fact]
    public void An_absolute_name_is_returned_when_it_exists()
    {
        var exe = Touch(_binA, "tool.exe");
        Assert.Equal(exe, ExecutableResolver.Find(exe, pathVar: null, ".EXE", windows: true));
        Assert.Null(ExecutableResolver.Find(Path.Combine(_binA, "missing.exe"), pathVar: null, ".EXE", windows: true));
    }

    [Fact]
    public void Missing_everywhere_or_empty_PATH_is_null()
    {
        Assert.Null(ExecutableResolver.Find("nope", PathOf(_binA, _binB), ".EXE;.CMD", windows: true));
        Assert.Null(ExecutableResolver.Find("nope", "", ".EXE", windows: true));
        Assert.Null(ExecutableResolver.Find("", PathOf(_binA), ".EXE", windows: true));
    }

    [Fact]
    public void Posix_mode_uses_the_bare_name()
    {
        var tool = Touch(_binA, "tool");
        Touch(_binA, "tool.exe");
        if (!OperatingSystem.IsWindows())   // a POSIX host also requires the exec bit
            File.SetUnixFileMode(tool, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        Assert.Equal(tool, ExecutableResolver.Find("tool", PathOf(_binA), pathExt: null, windows: false));
    }

    [Fact]
    public void SystemTool_is_absolute_on_Windows()
    {
        if (!OperatingSystem.IsWindows()) return;
        var cmd = ExecutableResolver.SystemTool("cmd.exe");
        Assert.True(Path.IsPathFullyQualified(cmd));
        Assert.True(File.Exists(cmd));
        Assert.True(File.Exists(ExecutableResolver.WindowsTool("explorer.exe")));
    }
}

/// <summary>How <see cref="ClaudeCli"/> builds the Windows start info for a resolved <c>claude</c> (CP7): a native exe
/// is exec'd directly; an npm shim runs through an absolute cmd.exe with the path quoted and the cwd search disabled
/// for the shim's own <c>node</c> lookup.</summary>
public class ClaudeCliStartInfoTests
{
    private const string Cmd = @"C:\Windows\System32\cmd.exe";

    [Fact]
    public void A_native_exe_is_started_directly_with_no_shell()
    {
        var psi = ClaudeCli.CreateWindowsStartInfo(@"C:\Users\x\.local\bin\claude.exe", "-p --verbose", Cmd);
        Assert.Equal(@"C:\Users\x\.local\bin\claude.exe", psi.FileName);
        Assert.Equal("-p --verbose", psi.Arguments);
    }

    [Fact]
    public void A_cmd_shim_runs_through_absolute_cmd_with_the_path_quoted()
    {
        var psi = ClaudeCli.CreateWindowsStartInfo(@"C:\Users\A B\AppData\Roaming\npm\claude.cmd", "-p --verbose", Cmd);
        Assert.Equal(Cmd, psi.FileName);
        Assert.Equal("/d /s /c \"\"C:\\Users\\A B\\AppData\\Roaming\\npm\\claude.cmd\" -p --verbose\"", psi.Arguments);
        Assert.Equal("1", psi.Environment["NoDefaultCurrentDirectoryInExePath"]);
        Assert.DoesNotContain("/c \"claude ", psi.Arguments);   // the old bare-name form
    }
}
