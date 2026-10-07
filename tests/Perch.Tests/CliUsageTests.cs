using Perch.Data.Control;
using Xunit;

namespace Perch.Tests;

/// <summary>
/// <c>perch --help</c> / <c>--version</c>: recognised anywhere on a session-shaped command line (so they win over
/// opening a session), never on a tray launch, and left alone when a subcommand owns the arguments.
/// </summary>
public class CliUsageTests
{
    [Theory]
    [InlineData("--help")]
    [InlineData("-h")]
    [InlineData("-?")]
    [InlineData("/?")]
    [InlineData("help")]
    [InlineData("HELP")]
    public void HelpForms_AreHelp(string arg) => Assert.True(CliUsage.IsHelp([arg]));

    [Theory]
    [InlineData("--version")]
    [InlineData("-v")]
    [InlineData("-V")]
    public void VersionForms_AreVersion(string arg) => Assert.True(CliUsage.IsVersion([arg]));

    [Fact]
    public void Help_WinsAnywhereOnASessionCommandLine()
    {
        // Otherwise `perch -c --help` would open a session.
        Assert.True(CliUsage.IsHelp(["-c", "--help"]));
        Assert.True(CliUsage.IsVersion([@"C:\src", "--version"]));
    }

    [Fact]
    public void TrayAndSessionLaunches_AreNeither()
    {
        string[][] launches = [[], ["--autostarted"], ["--tray"], ["-c"], ["--resume"], ["--open-intent-file", "x.json"]];
        foreach (var args in launches)
        {
            Assert.False(CliUsage.IsHelp(args));
            Assert.False(CliUsage.IsVersion(args));
        }
    }

    [Fact]
    public void Subcommands_KeepTheirOwnArguments()
    {
        // `perch statusline --help` is the statusline verb's own help; `render -v` isn't ours to answer.
        Assert.False(CliUsage.IsHelp(["statusline", "--help"]));
        Assert.False(CliUsage.IsVersion(["render", "-v"]));
        // A positional `help` only counts in first place.
        Assert.False(CliUsage.IsHelp(["-c", "help"]));
    }

    [Fact]
    public void Help_StartsWithTheVersionAndListsTheCommands()
    {
        var help = CliUsage.Help("1.2.3");
        Assert.StartsWith(CliUsage.VersionLine("1.2.3"), help);
        foreach (var expected in new[] { "--continue", "--resume", "--tray", "statusline", "configdirs", "uninstall", "--version" })
            Assert.Contains(expected, help);
    }
}
