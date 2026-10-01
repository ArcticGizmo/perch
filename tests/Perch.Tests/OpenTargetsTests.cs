using Perch.Data;
using Xunit;

namespace Perch.Tests;

/// <summary>
/// <see cref="OpenTargets"/> is the CP8 fix (docs/review-fixes-plan.md): every link Perch opens passes through
/// <see cref="OpenTargets.WebUrl"/>, so a markdown link, autolink or <c>gh</c> check URL pointing at a
/// <c>file:</c>/UNC/<c>search-ms:</c> target can't execute via the shell, and a <c>--switch</c> can't reach the
/// browser exe. Local-file opens are held to view-only types.
/// </summary>
public class OpenTargetsTests
{
    [Theory]
    [InlineData("file:///C:/x.exe")]
    [InlineData("file://attacker/share/x.exe")]
    [InlineData(@"\\h\s\x")]
    [InlineData(@"\\h\s\x.exe")]
    [InlineData("//h/s/x")]
    [InlineData(@"C:\Windows\System32\calc.exe")]
    [InlineData("search-ms:query=x&crumb=location:\\\\attacker\\s")]
    [InlineData("ms-settings:privacy")]
    [InlineData("ms-msdt:/id PCWDiagnostic")]
    [InlineData("--gpu-launcher=calc.exe")]
    [InlineData("-new-window")]
    [InlineData("javascript:alert(1)")]
    [InlineData("vbscript:msgbox")]
    [InlineData("data:text/html,<script>alert(1)</script>")]
    [InlineData("vscode://file/C:/x")]
    [InlineData("https://")]
    [InlineData("docs/plan.md")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void WebUrl_rejects_anything_but_http_and_mailto(string? url) =>
        Assert.Null(OpenTargets.WebUrl(url));

    [Theory]
    [InlineData("https://example.com", "https://example.com/")]
    [InlineData("https://github.com/o/r/pull/1/checks?check_run_id=9#step:3", "https://github.com/o/r/pull/1/checks?check_run_id=9#step:3")]
    [InlineData("HTTP://Example.COM/a", "http://example.com/a")]
    [InlineData("  https://example.com/x  ", "https://example.com/x")]
    [InlineData("mailto:support@example.com", "mailto:support@example.com")]
    public void WebUrl_accepts_and_normalises_web_links(string url, string expected) =>
        Assert.Equal(expected, OpenTargets.WebUrl(url));

    [Fact]
    public void WebUrl_result_is_always_scheme_led()
    {
        // Whitespace and control characters are escaped, so nothing after the URL can become a separate argument
        // and nothing can precede the scheme.
        var safe = OpenTargets.WebUrl("https://example.com/a b\n--gpu-launcher=calc");
        Assert.NotNull(safe);
        Assert.StartsWith("https://example.com/", safe);
        Assert.DoesNotContain(' ', safe);
        Assert.DoesNotContain('\n', safe);
    }

    [Theory]
    [InlineData("https://example.com", true)]
    [InlineData("http://example.com", true)]
    [InlineData("mailto:a@example.com", false)]
    public void IsHttp_separates_browser_links_from_mailto(string url, bool expected) =>
        Assert.Equal(expected, OpenTargets.IsHttp(url));

    [Theory]
    [InlineData("docs/plan.md", "docs/plan.md")]
    [InlineData("docs/plan.md#cp8", "docs/plan.md")]
    [InlineData("./a%20b.md?x=1", "./a b.md")]
    [InlineData("../README.md", "../README.md")]
    public void LinkFilePath_resolves_relative_link_targets(string target, string expected) =>
        Assert.Equal(expected, OpenTargets.LinkFilePath(target));

    [Fact]
    public void LinkFilePath_unwraps_local_file_uris_and_drive_paths()
    {
        if (!OperatingSystem.IsWindows()) return;   // drive-letter paths are Windows-only
        Assert.Equal(@"C:\work\notes.md", OpenTargets.LinkFilePath("file:///C:/work/notes.md"));
        Assert.Equal(@"C:\work\notes.md", OpenTargets.LinkFilePath(@"C:\work\notes.md"));
    }

    [Theory]
    [InlineData(@"\\attacker\s\a.md")]
    [InlineData("//attacker/s/a.md")]
    [InlineData("file://attacker/s/a.md")]
    [InlineData(@"\\?\C:\x.md")]
    [InlineData(@"\\.\pipe\x")]
    [InlineData("%5C%5Cattacker%5Cs%5Ca.md")]   // decodes to a UNC path
    [InlineData("search-ms:query=x")]
    [InlineData("javascript:alert(1)")]
    [InlineData("ms-settings:privacy")]
    [InlineData("#heading")]
    [InlineData("")]
    [InlineData(null)]
    public void LinkFilePath_rejects_network_and_scheme_targets(string? target) =>
        Assert.Null(OpenTargets.LinkFilePath(target));

    [Theory]
    [InlineData("shot.png")]
    [InlineData("notes.MD")]
    [InlineData("report.pdf")]
    [InlineData("data.json")]
    public void IsViewerSafeFile_accepts_view_only_types(string name) =>
        Assert.True(OpenTargets.IsViewerSafeFile(Path.Combine(Path.GetTempPath(), name)));

    [Theory]
    [InlineData("x.exe")]
    [InlineData("x.bat")]
    [InlineData("x.cmd")]
    [InlineData("x.ps1")]
    [InlineData("x.lnk")]
    [InlineData("x.url")]
    [InlineData("x.msi")]
    [InlineData("x.hta")]
    [InlineData("x.html")]
    [InlineData("x.svg")]
    [InlineData("x.docm")]
    [InlineData("x.command")]
    [InlineData("x.txt.exe")]
    [InlineData("x.exe.")]
    [InlineData("noextension")]
    public void IsViewerSafeFile_refuses_types_that_run(string name) =>
        Assert.False(OpenTargets.IsViewerSafeFile(Path.Combine(Path.GetTempPath(), name)));

    [Theory]
    [InlineData(@"\\attacker\s\shot.png")]
    [InlineData("//attacker/s/shot.png")]
    [InlineData(@"\\?\C:\shot.png")]
    [InlineData("shot.png")]                 // relative: resolves against whatever the cwd is
    [InlineData("")]
    [InlineData(null)]
    public void IsViewerSafeFile_refuses_remote_and_relative_paths(string? path) =>
        Assert.False(OpenTargets.IsViewerSafeFile(path));

    [Fact]
    public void IsViewerSafeFile_refuses_an_alternate_data_stream()
    {
        if (!OperatingSystem.IsWindows()) return;
        Assert.False(OpenTargets.IsViewerSafeFile(@"C:\work\x.exe:payload.txt"));
    }
}
