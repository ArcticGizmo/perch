using Perch.Data;
using Xunit;

namespace Perch.Tests;

public class IdeHostTests
{
    [Theory]
    // VS Code family — with/without .exe, any case, and full paths all resolve.
    [InlineData("Code.exe", IdeHostKind.VsCode, "Visual Studio Code")]
    [InlineData("code", IdeHostKind.VsCode, "Visual Studio Code")]
    [InlineData(@"C:\Users\me\AppData\Local\Programs\Microsoft VS Code\Code.exe", IdeHostKind.VsCode, "Visual Studio Code")]
    [InlineData("Code - Insiders.exe", IdeHostKind.VsCode, "VS Code (Insiders)")]
    [InlineData("VSCodium.exe", IdeHostKind.VsCode, "VSCodium")]
    [InlineData("code-oss", IdeHostKind.VsCode, "VSCodium")]
    // Forks.
    [InlineData("Cursor.exe", IdeHostKind.Cursor, "Cursor")]
    [InlineData("Windsurf.exe", IdeHostKind.Windsurf, "Windsurf")]
    // JetBrains launchers — the "64" arch suffix and case don't matter (prefix match).
    [InlineData("pycharm64.exe", IdeHostKind.JetBrains, "PyCharm")]
    [InlineData("idea64.exe", IdeHostKind.JetBrains, "IntelliJ IDEA")]
    [InlineData("rider64.exe", IdeHostKind.JetBrains, "Rider")]
    [InlineData("webstorm64.exe", IdeHostKind.JetBrains, "WebStorm")]
    [InlineData("goland64.exe", IdeHostKind.JetBrains, "GoLand")]
    // Recognised-but-generic hosts fall under Other with their real name.
    [InlineData("zed.exe", IdeHostKind.Other, "Zed")]
    [InlineData("devenv.exe", IdeHostKind.Other, "Visual Studio")]
    public void FromExecutable_MapsKnownHosts(string exe, IdeHostKind kind, string display)
    {
        var host = IdeHost.FromExecutable(exe);
        Assert.NotNull(host);
        Assert.Equal(kind, host!.Kind);
        Assert.Equal(display, host.DisplayName);
    }

    [Theory]
    // Plain shells, terminals and unrelated processes must NOT light the glyph.
    [InlineData("powershell.exe")]
    [InlineData("pwsh.exe")]
    [InlineData("cmd.exe")]
    [InlineData("bash.exe")]
    [InlineData("WindowsTerminal.exe")]
    [InlineData("OpenConsole.exe")]
    [InlineData("explorer.exe")]
    [InlineData("node.exe")]
    [InlineData("claude.exe")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void FromExecutable_ReturnsNullForNonIde(string? exe)
    {
        Assert.Null(IdeHost.FromExecutable(exe));
    }

    [Fact]
    public void NullDetector_ReportsNoIde()
    {
        Assert.Null(Perch.Platform.NullIdeHostDetector.Instance.Detect(1234));
    }
}
