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

    // ── Classify: only an app with an actual integration is recognised ─────────

    [Theory]
    [InlineData("MSTeams_8wekyb3d8bbwe")]                                    // new Teams, packaged
    [InlineData(@"C:\Program Files\WindowsApps\MSTeams_1.0_x64__8wekyb3d8bbwe\ms-teams.exe")]
    [InlineData("MicrosoftTeams_8wekyb3d8bbwe")]                             // the Win11 personal client
    [InlineData(@"C:\Users\someone\AppData\Local\Microsoft\Teams\current\Teams.exe")] // classic
    public void Classify_RecognisesTeams(string identity)
        => Assert.Equal(MicAppKind.Teams, MicApps.Classify(identity));

    [Theory]
    // Everything Perch has no integration for is Other — the generic path, not a failure.
    [InlineData("91750D7E.Slack_8she8kybcnzg4")]
    [InlineData(@"C:\Program Files\Zoom\bin\Zoom.exe")]
    [InlineData(@"C:\Program Files\Google\Chrome\Application\chrome.exe")]
    [InlineData(@"C:\programs\obs-studio\bin\64bit\obs64.exe")]
    [InlineData("")]
    [InlineData(null)]
    public void Classify_EverythingElseIsOther(string? identity)
        => Assert.Equal(MicAppKind.Other, MicApps.Classify(identity));

    // A near-miss must not be mistaken for Teams: the token match is exact, not a substring, so an unrelated
    // app whose name merely contains "teams" stays on the generic path rather than being offered controls
    // that would silently do nothing.
    [Fact]
    public void Classify_DoesNotMatchOnSubstring()
    {
        Assert.Equal(MicAppKind.Other, MicApps.Classify(@"C:\tools\teamspeak.exe"));
        Assert.Equal(MicAppKind.Other, MicApps.Classify("Acme.TeamsViewer_abcdefghijklm"));
    }

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

    // ── CallLinkApplies: the single gate on the product-specific behaviour ─────

    private static MicSnapshot Holding(string identity) =>
        new([new MicUser(identity, "App", 100, true, null)], DeviceMuted: false, DeviceName: "Mic");

    private static readonly CallSnapshot InMeeting = new(IsInMeeting: true, IsMuted: false, CanToggleMute: true);

    [Fact]
    public void CallLinkApplies_WhenTheRecognisedAppHoldsTheMic()
        => Assert.True(MicApps.CallLinkApplies(
            Holding("MSTeams_8wekyb3d8bbwe"), InMeeting, CallLinkState.Connected));

    // The important negative: a live Teams link must not hijack the strip while a *different* app has the
    // microphone, or the mute button would silence Zoom by toggling Teams.
    [Fact]
    public void CallLinkApplies_NotWhenAnotherAppHoldsTheMic()
        => Assert.False(MicApps.CallLinkApplies(
            Holding(@"C:\Program Files\Zoom\bin\Zoom.exe"), InMeeting, CallLinkState.Connected));

    [Theory]
    [InlineData(CallLinkState.Disabled)]
    [InlineData(CallLinkState.Unavailable)]
    [InlineData(CallLinkState.Connecting)]
    [InlineData(CallLinkState.AwaitingApproval)]
    public void CallLinkApplies_OnlyWhenFullyConnected(CallLinkState state)
        => Assert.False(MicApps.CallLinkApplies(Holding("MSTeams_8wekyb3d8bbwe"), InMeeting, state));

    [Fact]
    public void CallLinkApplies_NotWhenTheAppReportsNoMeeting()
        => Assert.False(MicApps.CallLinkApplies(
            Holding("MSTeams_8wekyb3d8bbwe"),
            new CallSnapshot(IsInMeeting: false, IsMuted: true),
            CallLinkState.Connected));

    [Fact]
    public void CallLinkApplies_NotWithoutCallState()
        => Assert.False(MicApps.CallLinkApplies(Holding("MSTeams_8wekyb3d8bbwe"), null, CallLinkState.Connected));

    // A head whose microphone detection is a stub (macOS today) reports null rather than an idle snapshot, and
    // must still get working call controls from the link alone.
    [Fact]
    public void CallLinkApplies_WithNoMicrophoneReportAtAll()
        => Assert.True(MicApps.CallLinkApplies(null, InMeeting, CallLinkState.Connected));

    // An idle snapshot has no holder either, so the link is all there is to go on — same conclusion.
    [Fact]
    public void CallLinkApplies_WithAnIdleMicrophone()
        => Assert.True(MicApps.CallLinkApplies(
            new MicSnapshot([], DeviceMuted: false, DeviceName: "Mic"), InMeeting, CallLinkState.Connected));

    // ── MicSnapshot: value equality is what suppresses no-op repaints ──────────

    [Fact]
    public void MicSnapshot_EqualityComparesUsersByValue()
    {
        var a = new MicSnapshot([new MicUser("MSTeams_8wekyb3d8bbwe", "Microsoft Teams", 5028, true, null)],
            DeviceMuted: false, DeviceName: "Mic");
        var b = new MicSnapshot([new MicUser("MSTeams_8wekyb3d8bbwe", "Microsoft Teams", 5028, true, null)],
            DeviceMuted: false, DeviceName: "Mic");

        // Distinct list instances: the compiler-generated record equality would call these unequal, which
        // would make every poll tick look like a change and repaint the overlay every two seconds.
        Assert.Equal(a, b);
        Assert.Equal(a.GetHashCode(), b.GetHashCode());
    }

    [Fact]
    public void MicSnapshot_EqualityNoticesRealChanges()
    {
        var users = new MicUser[] { new("MSTeams_8wekyb3d8bbwe", "Microsoft Teams", 5028, true, null) };
        var baseline = new MicSnapshot(users, DeviceMuted: false, DeviceName: "Mic");

        Assert.NotEqual(baseline, baseline with { DeviceMuted = true });
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
        var snapshot = new MicSnapshot([teams, obs], DeviceMuted: false, DeviceName: "Mic");

        Assert.True(snapshot.InUse);
        Assert.Equal(teams, snapshot.Primary);
    }

    [Fact]
    public void MicSnapshot_IdleHasNoPrimary()
    {
        var snapshot = new MicSnapshot([], DeviceMuted: false, DeviceName: "Mic");
        Assert.False(snapshot.InUse);
        Assert.Null(snapshot.Primary);
    }
}
