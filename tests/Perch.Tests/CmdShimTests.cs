using Perch.Data;
using Xunit;

namespace Perch.Tests;

/// <summary>
/// <see cref="CmdShim"/> (review fixes CP12): a <c>.cmd</c> tool runs through cmd with every argument quoted, and is
/// refused when an argument holds a character quoting can't neutralise. Nothing is ever started: the tests only build
/// start infos, and the planted tools are empty files.
/// </summary>
public sealed class CmdShimTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("perch-cmdshim-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private string Plant(string name)
    {
        var path = Path.Combine(_root, name);
        File.WriteAllText(path, "");
        return path;
    }

    [Fact]
    public void A_shim_runs_through_cmd_with_every_argument_quoted()
    {
        if (!OperatingSystem.IsWindows()) return;
        var shim = Plant("code.cmd");

        var psi = CmdShim.StartInfo(shim, ["-g", @"C:\work\my repo\notes.md:3"]);

        Assert.NotNull(psi);
        Assert.Equal(ExecutableResolver.SystemTool("cmd.exe"), psi!.FileName);
        Assert.False(psi.UseShellExecute);
        Assert.Equal($"/d /s /c \"\"{shim}\" \"-g\" \"C:\\work\\my repo\\notes.md:3\"\"", psi.Arguments);
    }

    [Theory]
    [InlineData(@"C:\repo\x&calc&.md")]
    [InlineData(@"C:\work\R&D")]
    [InlineData(@"C:\work\a|b")]
    [InlineData(@"C:\work\%PATH%")]
    [InlineData(@"C:\work\wow!")]
    [InlineData(@"C:\work\a^b")]
    [InlineData(@"C:\work\a<b>")]
    [InlineData("C:\\work\\\"quoted\"")]
    [InlineData("C:\\work\\two\nlines")]
    public void A_shim_launch_is_refused_when_an_argument_could_escape_the_quoting(string arg)
    {
        Assert.Null(CmdShim.CmdCommandLine(@"C:\tools\code.cmd", ["-g", arg]));
        if (!OperatingSystem.IsWindows()) return;
        Assert.Null(CmdShim.StartInfo(Plant("code.cmd"), ["-g", arg]));   // so the caller doesn't launch at all
    }

    [Fact]
    public void A_shim_whose_own_path_holds_a_metacharacter_is_refused()
    {
        Assert.Null(CmdShim.CmdCommandLine(@"C:\R&D tools\code.cmd", ["-g", @"C:\a.md"]));
    }

    [Fact]
    public void A_real_exe_is_started_directly_with_an_argument_list()
    {
        var exe = Plant("tool.exe");
        var psi = CmdShim.StartInfo(exe, ["-p", @"C:\work\R&D"]);

        Assert.NotNull(psi);
        Assert.Equal(exe, psi!.FileName);
        Assert.Equal(["-p", @"C:\work\R&D"], psi.ArgumentList);   // no cmd involved, so & is just a character
    }
}
