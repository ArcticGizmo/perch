using Perch.Data;
using Xunit;

namespace Perch.Tests;

/// <summary>
/// <see cref="CmdShim"/> (review fixes CP12): reading an Electron-style <c>.cmd</c> shim down to the <c>.exe</c> it
/// runs, so Perch's arguments never pass through cmd. The shims below copy VS Code's and GitKraken's real ones. Every
/// planted <c>.exe</c> is an EMPTY file: the tests only build start infos, and nothing is ever started.
/// </summary>
public sealed class CmdShimTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("perch-cmdshim-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private string Plant(string relative, string content = "")
    {
        var path = Path.Combine(_root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return path;
    }

    // VS Code's bin\code.cmd, verbatim apart from the build folder name.
    private string VsCodeShim()
    {
        Plant(@"vscode\Code.exe");
        return Plant(@"vscode\bin\code.cmd",
            "@echo off\r\nsetlocal\r\nset VSCODE_DEV=\r\nset ELECTRON_RUN_AS_NODE=1\r\n" +
            "\"%~dp0..\\Code.exe\" \"%~dp0..\\abc123\\resources\\app\\out\\cli.js\" %*\r\n" +
            "IF %ERRORLEVEL% NEQ 0 EXIT /b %ERRORLEVEL%\r\nendlocal\r\n");
    }

    [Fact]
    public void VS_Codes_shim_resolves_to_Code_exe_with_its_cli_script_and_environment()
    {
        var shim = VsCodeShim();
        var t = CmdShim.Resolve(shim);

        Assert.NotNull(t);
        Assert.Equal(Path.Combine(_root, @"vscode\Code.exe"), t!.Exe);
        Assert.Equal([Path.Combine(_root, @"vscode\abc123\resources\app\out\cli.js")], t.LeadingArgs);
        Assert.Equal([("VSCODE_DEV", ""), ("ELECTRON_RUN_AS_NODE", "1")], t.Env);
    }

    [Fact]
    public void A_file_name_that_would_run_a_command_through_cmd_is_passed_to_Code_exe_as_one_argument()
    {
        if (!OperatingSystem.IsWindows()) return;
        var psi = CmdShim.StartInfo(VsCodeShim(), ["-g", @"C:\repo\x&calc&.md:3"]);

        Assert.NotNull(psi);
        Assert.Equal(Path.Combine(_root, @"vscode\Code.exe"), psi!.FileName);   // not cmd.exe
        Assert.False(psi.UseShellExecute);
        Assert.Equal([Path.Combine(_root, @"vscode\abc123\resources\app\out\cli.js"), "-g", @"C:\repo\x&calc&.md:3"],
            psi.ArgumentList);
        Assert.Equal("1", psi.Environment["ELECTRON_RUN_AS_NODE"]);
        Assert.False(psi.Environment.ContainsKey("VSCODE_DEV"));
    }

    [Fact]
    public void GitKrakens_two_hop_shim_resolves_to_gitkraken_exe()
    {
        // bin\gitkraken.cmd hops to the versioned app's own shim, whose script lives inside app.asar (not a real
        // directory, so the script isn't required to exist).
        Plant(@"gk\app-12.5.0\gitkraken.exe");
        Plant(@"gk\app-12.5.0\resources\bin\gitkraken.cmd",
            "@echo off\r\nsetlocal\r\nset ELECTRON_RUN_AS_NODE=1\r\n" +
            "\"%~dp0..\\..\\gitkraken.exe\" \"%~dp0..\\app.asar\\src\\main\\static\\cli.js\" %*\r\nendlocal\r\n");
        var shim = Plant(@"gk\bin\gitkraken.cmd",
            "@echo off\r\n\"%~dp0\\..\\app-12.5.0\\resources\\bin\\gitkraken.cmd\" %*\r\n");

        var t = CmdShim.Resolve(shim);

        Assert.NotNull(t);
        Assert.Equal(Path.Combine(_root, @"gk\app-12.5.0\gitkraken.exe"), t!.Exe);
        Assert.Equal([Path.Combine(_root, @"gk\app-12.5.0\resources\app.asar\src\main\static\cli.js")], t.LeadingArgs);
        Assert.Equal([("ELECTRON_RUN_AS_NODE", "1")], t.Env);
    }

    [Theory]
    [InlineData("call \"%~dp0..\\x.exe\" %*")]                       // an unfamiliar command
    [InlineData("\"C:\\elsewhere\\x.exe\" %*")]                        // not relative to the shim
    [InlineData("\"%~dp0..\\x.exe\" \"%APPDATA%\\x.js\" %*")]           // another variable
    [InlineData("\"%~dp0..\\x.exe\" %1")]                              // not a plain pass-through
    [InlineData("set X=%PATH%\r\n\"%~dp0..\\x.exe\" %*")]               // an expanding set
    [InlineData("\"%~dp0..\\x.exe\" %*\r\n\"%~dp0..\\x.exe\" %*")]      // two launches
    [InlineData("\"%~dp0..\\missing.exe\" %*")]                        // the exe isn't there
    public void A_shim_it_cannot_read_exactly_is_not_guessed_at(string body)
    {
        Plant(@"odd\x.exe");
        var shim = Plant(@"odd\bin\tool.cmd", "@echo off\r\n" + body + "\r\n");
        Assert.Null(CmdShim.Resolve(shim));
    }

    [Fact]
    public void A_shim_loop_ends()
    {
        var a = Plant(@"loop\a.cmd", "\"%~dp0b.cmd\" %*\r\n");
        Plant(@"loop\b.cmd", "\"%~dp0a.cmd\" %*\r\n");
        Assert.Null(CmdShim.Resolve(a));
    }

    [Fact]
    public void An_unreadable_shim_falls_back_to_cmd_with_every_argument_quoted()
    {
        if (!OperatingSystem.IsWindows()) return;
        var shim = Plant(@"odd\bin\tool.cmd", "@echo off\r\ncall other.cmd %*\r\n");

        var psi = CmdShim.StartInfo(shim, ["-p", @"C:\work\my repo"]);

        Assert.NotNull(psi);
        Assert.Equal(ExecutableResolver.SystemTool("cmd.exe"), psi!.FileName);
        Assert.Equal($"/d /s /c \"\"{shim}\" \"-p\" \"C:\\work\\my repo\"\"", psi.Arguments);
    }

    [Theory]
    [InlineData(@"C:\work\R&D")]
    [InlineData(@"C:\work\a|b")]
    [InlineData(@"C:\work\%PATH%")]
    [InlineData(@"C:\work\wow!")]
    [InlineData(@"C:\work\a^b")]
    [InlineData(@"C:\work\a<b>")]
    [InlineData("C:\\work\\\"quoted\"")]
    public void The_cmd_fallback_refuses_what_quoting_cannot_neutralise(string arg)
    {
        Assert.Null(CmdShim.CmdCommandLine(@"C:\tools\tool.cmd", ["-p", arg]));
        if (!OperatingSystem.IsWindows()) return;
        var shim = Plant(@"odd\bin\tool.cmd", "@echo off\r\ncall other.cmd %*\r\n");
        Assert.Null(CmdShim.StartInfo(shim, ["-p", arg]));   // so the caller doesn't launch at all
    }

    // The installed shims, when this host has them: catches a VS Code or GitKraken update that changes the shim's
    // shape (Perch would then fall back to the quoted cmd route). Only reads the shim; nothing is started.
    [Theory]
    [InlineData("code")]
    [InlineData("gitkraken")]
    public void The_installed_shim_on_this_host_resolves_to_its_exe(string tool)
    {
        if (!OperatingSystem.IsWindows()) return;
        var shim = ExecutableResolver.Find(tool);
        if (shim is null || !shim.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase)) return;

        var t = CmdShim.Resolve(shim);
        Assert.NotNull(t);
        Assert.True(File.Exists(t!.Exe), t.Exe);
        Assert.Contains(("ELECTRON_RUN_AS_NODE", "1"), t.Env);
    }

    [Fact]
    public void A_real_exe_is_started_directly()
    {
        var exe = Plant(@"plain\tool.exe");
        var psi = CmdShim.StartInfo(exe, ["-p", @"C:\work\R&D"]);

        Assert.NotNull(psi);
        Assert.Equal(exe, psi!.FileName);
        Assert.Equal(["-p", @"C:\work\R&D"], psi.ArgumentList);
    }
}
