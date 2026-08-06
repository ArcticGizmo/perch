using Perch.Data;
using Perch.Platform;
using Xunit;

namespace Perch.Tests;

public class MicAppsTests
{
    // ── Token: reducing a platform identity to something comparable ────────────

    [Theory]
    // Package family names: the publisher hash goes, then the publisher prefix.
    [InlineData("MSTeams_8wekyb3d8bbwe", "MSTeams")]
    [InlineData("91750D7E.Slack_8she8kybcnzg4", "Slack")]
    [InlineData("Microsoft.WindowsCamera_8wekyb3d8bbwe", "WindowsCamera")]
    [InlineData("OpenAI.ChatGPT-Desktop_2p2nqsd0c76g0", "ChatGPT-Desktop")]
    // Executable paths: the file name without its extension, either separator.
    [InlineData(@"C:\Program Files\Google\Chrome\Application\chrome.exe", "chrome")]
    [InlineData(@"C:\programs\obs-studio\bin\64bit\obs64.exe", "obs64")]
    [InlineData("/Applications/Microsoft Teams.app/Contents/MacOS/MSTeams", "MSTeams")]
    // A bare exe name with no path still loses its extension.
    [InlineData("ms-teams.exe", "ms-teams")]
    public void Token_ReducesIdentityToComparableName(string identity, string expected)
        => Assert.Equal(expected, MicApps.Token(identity));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Token_BlankIdentityIsEmpty(string? identity)
        => Assert.Equal("", MicApps.Token(identity));

    // Product *recognition* used to live here too, so a Teams-specific control layer could be offered on top.
    // That layer is gone, and nothing in the microphone path treats one app differently from another any more —
    // naming, below, is all that's left.

    // ── DisplayName ───────────────────────────────────────────────────────────

    [Fact]
    public void DisplayName_PrefersThePlatformsOwnDescription()
        => Assert.Equal("Microsoft Teams",
            MicApps.DisplayName("MSTeams_8wekyb3d8bbwe", "Microsoft Teams"));

    [Fact]
    public void DisplayName_IgnoresBlankDescription()
        => Assert.Equal("Slack", MicApps.DisplayName("91750D7E.Slack_8she8kybcnzg4", "   "));

    [Theory]
    [InlineData("MSTeams_8wekyb3d8bbwe", "Microsoft Teams")]
    [InlineData(@"C:\Program Files\Google\Chrome\Application\chrome.exe", "Google Chrome")]
    [InlineData(@"C:\programs\obs-studio\bin\64bit\obs64.exe", "OBS Studio")]
    public void DisplayName_FallsBackToTheKnownAppTable(string identity, string expected)
        => Assert.Equal(expected, MicApps.DisplayName(identity));

    [Theory]
    // An app with no version info and no table entry still gets something presentable: CamelCase and
    // kebab-case split into words, and runs of capitals stay intact rather than shattering.
    [InlineData(@"C:\tools\SoundRecorder.exe", "Sound Recorder")]
    [InlineData(@"C:\tools\voicemeeter-vban.exe", "Voicemeeter Vban")]
    [InlineData("Acme.OBSLink_abcdefghijklm", "OBSLink")]
    public void DisplayName_PrettifiesAnUnknownApp(string identity, string expected)
        => Assert.Equal(expected, MicApps.DisplayName(identity));

    [Fact]
    public void DisplayName_NeverBlank()
        => Assert.Equal("Unknown app", MicApps.DisplayName(null));

    // ── IdentityMatchesPath: joining a ledger identity to a live process ──────

    [Fact]
    public void IdentityMatchesPath_MatchesAPackageToItsInstallFolder()
        => Assert.True(MicApps.IdentityMatchesPath(
            "MSTeams_8wekyb3d8bbwe",
            @"C:\Program Files\WindowsApps\MSTeams_26163.405.4842.717_x64__8wekyb3d8bbwe\ms-teams.exe"));

    [Fact]
    public void IdentityMatchesPath_NeedsBothPackageNameAndPublisher()
    {
        // Right package name, wrong publisher — a different publisher's identically-named package must not
        // match, or one app's capture would be attributed to another's identity.
        Assert.False(MicApps.IdentityMatchesPath(
            "MSTeams_somebodyelse",
            @"C:\Program Files\WindowsApps\MSTeams_26163.405.4842.717_x64__8wekyb3d8bbwe\ms-teams.exe"));

        // Right publisher, wrong package.
        Assert.False(MicApps.IdentityMatchesPath(
            "MSTeams_8wekyb3d8bbwe",
            @"C:\Program Files\WindowsApps\WindowsCamera_2.0_x64__8wekyb3d8bbwe\camera.exe"));
    }

    [Fact]
    public void IdentityMatchesPath_ComparesPlainPathsCaseInsensitively()
    {
        Assert.True(MicApps.IdentityMatchesPath(
            @"C:\Program Files\Google\Chrome\Application\chrome.exe",
            @"c:\program files\google\chrome\application\CHROME.EXE"));
        Assert.False(MicApps.IdentityMatchesPath(
            @"C:\Program Files\Google\Chrome\Application\chrome.exe",
            @"C:\Program Files\Mozilla Firefox\firefox.exe"));
    }

    [Theory]
    [InlineData(null, @"C:\app\app.exe")]
    [InlineData("MSTeams_8wekyb3d8bbwe", null)]
    [InlineData("", "")]
    [InlineData("NoPublisherHash", @"C:\Program Files\WindowsApps\NoPublisherHash_1.0\app.exe")]
    public void IdentityMatchesPath_RejectsUnusableInput(string? identity, string? path)
        => Assert.False(MicApps.IdentityMatchesPath(identity, path));

    // ── IsSameApp: matching a media session's source app to a mic holder ──────

    [Theory]
    // A media SMTC SourceAppUserModelId (AUMID, "!"-suffixed) vs the mic ledger identity for the same app.
    // Packaged Teams is reported both places by its package family name, so both reduce to "MSTeams".
    [InlineData("MSTeams_8wekyb3d8bbwe!App", "MSTeams_8wekyb3d8bbwe")]
    // Store Slack's AUMID vs its package family name.
    [InlineData("91750D7E.Slack_8she8kybcnzg4!Slack", "91750D7E.Slack_8she8kybcnzg4")]
    // A plain win32 source (Chrome media tab) reported as an exe name vs the mic's exe path.
    [InlineData("chrome.exe", @"C:\Program Files\Google\Chrome\Application\chrome.exe")]
    public void IsSameApp_MatchesTheSameProductAcrossIdentityShapes(string a, string b)
        => Assert.True(MicApps.IsSameApp(a, b));

    [Theory]
    // Different products never match, so a real media player during a call isn't suppressed.
    [InlineData("SpotifyAB.SpotifyMusic_zpdnekdrzrea0!Spotify", "MSTeams_8wekyb3d8bbwe")]
    [InlineData("chrome.exe", @"C:\Program Files\Mozilla Firefox\firefox.exe")]
    // A blank/tokenless source app id must never match — a source that can't report an app id can't suppress.
    [InlineData("", "MSTeams_8wekyb3d8bbwe")]
    [InlineData(null, "MSTeams_8wekyb3d8bbwe")]
    [InlineData("MSTeams_8wekyb3d8bbwe!App", null)]
    public void IsSameApp_RejectsDifferentOrBlankApps(string? a, string? b)
        => Assert.False(MicApps.IsSameApp(a, b));

    // ── MicSnapshot: value equality is what suppresses no-op repaints ──────────

    [Fact]
    public void MicSnapshot_EqualityComparesUsersByValue()
    {
        var a = new MicSnapshot([new MicUser("MSTeams_8wekyb3d8bbwe", "Microsoft Teams", 5028, true, null)],
            DeviceName: "Mic");
        var b = new MicSnapshot([new MicUser("MSTeams_8wekyb3d8bbwe", "Microsoft Teams", 5028, true, null)],
            DeviceName: "Mic");

        // Distinct list instances: the compiler-generated record equality would call these unequal, which
        // would make every poll tick look like a change and repaint the overlay every two seconds.
        Assert.Equal(a, b);
        Assert.Equal(a.GetHashCode(), b.GetHashCode());
    }

    [Fact]
    public void MicSnapshot_EqualityNoticesRealChanges()
    {
        var users = new MicUser[] { new("MSTeams_8wekyb3d8bbwe", "Microsoft Teams", 5028, true, null) };
        var baseline = new MicSnapshot(users, DeviceName: "Mic");

        Assert.NotEqual(baseline, baseline with { DeviceName = "Other mic" });
        Assert.NotEqual(baseline, baseline with { Users = [] });
        Assert.NotEqual(baseline, baseline with
        {
            Users = [new MicUser("MSTeams_8wekyb3d8bbwe", "Microsoft Teams", 5028, IsStreaming: false, null)],
        });
    }

    [Fact]
    public void MicSnapshot_PrimaryIsTheFirstHolder()
    {
        var teams = new MicUser("MSTeams_8wekyb3d8bbwe", "Microsoft Teams", 5028, true, null);
        var obs = new MicUser(@"C:\obs\obs64.exe", "OBS Studio", 77, true, null);
        var snapshot = new MicSnapshot([teams, obs], DeviceName: "Mic");

        Assert.True(snapshot.InUse);
        Assert.Equal(teams, snapshot.Primary);
    }

    [Fact]
    public void MicSnapshot_IdleHasNoPrimary()
    {
        var snapshot = new MicSnapshot([], DeviceName: "Mic");
        Assert.False(snapshot.InUse);
        Assert.Null(snapshot.Primary);
    }
}
