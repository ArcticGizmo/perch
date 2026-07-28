using System.Text.Json;
using Perch.Data;
using Perch.Platform;
using Xunit;

namespace Perch.Tests;

/// <summary>
/// Covers <see cref="TeamsCallController.Merge"/> — the partial-update fold that the whole integration hangs
/// on. Teams sends <em>incomplete</em> <c>meetingUpdate</c> frames, so a parser that rebuilds the snapshot
/// from each one silently resets whatever the frame omitted; that is what made Perch miss a mute made inside
/// Teams. The frames below are the real shapes observed against Teams 26163.405.4842.717.
/// </summary>
public class TeamsCallControllerTests
{
    // Feeds a raw frame through the same path the read loop uses.
    private static CallSnapshot Apply(CallSnapshot? previous, string frameJson)
    {
        using var doc = JsonDocument.Parse(frameJson);
        var update = doc.RootElement.GetProperty("meetingUpdate");
        var state = update.TryGetProperty("meetingState", out var s) ? s : default;
        var permissions = update.TryGetProperty("meetingPermissions", out var p) ? p : default;
        return TeamsCallController.Merge(previous, state, permissions);
    }

    // Captured verbatim: what Teams sends on connecting while not in a call. Note there is no meetingState at
    // all — the omission that broke the naive parser.
    private const string IdleFrame = """
    {"meetingUpdate":{"meetingPermissions":{"canReact":false,"canToggleVideo":false,"canToggleMute":false,
    "canToggleHand":false,"canToggleShareTray":false,"canLeave":false,"canToggleBlur":false,
    "canToggleChat":false,"canStopSharing":false,"canPair":false}}}
    """;

    private const string InCallFrame = """
    {"meetingUpdate":{"meetingState":{"isMuted":false,"isVideoOn":true,"isHandRaised":false,
    "isInMeeting":true,"isRecordingOn":false,"isBackgroundBlurred":false,"isSharing":false,
    "hasUnreadMessages":false},"meetingPermissions":{"canReact":true,"canToggleVideo":true,
    "canToggleMute":true,"canToggleHand":true,"canToggleShareTray":true,"canLeave":true,
    "canToggleBlur":true,"canToggleChat":true,"canStopSharing":false,"canPair":false}}}
    """;

    [Fact]
    public void Merge_IdleFrameReportsNoMeeting()
    {
        var snapshot = Apply(null, IdleFrame);

        Assert.False(snapshot.IsInMeeting);
        Assert.False(snapshot.CanToggleMute);
        Assert.False(snapshot.CanLeave);
    }

    [Fact]
    public void Merge_InCallFrameReadsTheRealFieldNames()
    {
        var snapshot = Apply(null, InCallFrame);

        Assert.True(snapshot.IsInMeeting);
        Assert.False(snapshot.IsMuted);
        Assert.True(snapshot.IsCameraOn);   // Teams' own model spells this isVideoOn
        Assert.True(snapshot.CanToggleMute);
        Assert.True(snapshot.CanLeave);
    }

    // The regression this whole merge exists for: mute inside Teams, then let any frame that doesn't mention
    // isMuted arrive. Rebuilding from scratch would report unmuted again.
    [Fact]
    public void Merge_KeepsMuteThroughAFrameThatOmitsIt()
    {
        var inCall = Apply(null, InCallFrame);
        var muted = Apply(inCall, """{"meetingUpdate":{"meetingState":{"isMuted":true}}}""");
        Assert.True(muted.IsMuted);
        Assert.True(muted.IsInMeeting);      // carried over, not reset by the partial frame
        Assert.True(muted.CanToggleMute);    // ditto for the permissions the frame didn't mention

        // A permissions-only frame that still shows an active call must not clear the mute.
        var later = Apply(muted, """
        {"meetingUpdate":{"meetingPermissions":{"canToggleMute":true,"canLeave":true}}}
        """);
        Assert.True(later.IsMuted);
        Assert.True(later.IsInMeeting);
    }

    [Fact]
    public void Merge_UnmuteIsAnExplicitFalseNotAnOmission()
    {
        var muted = Apply(Apply(null, InCallFrame), """{"meetingUpdate":{"meetingState":{"isMuted":true}}}""");
        var unmuted = Apply(muted, """{"meetingUpdate":{"meetingState":{"isMuted":false}}}""");
        Assert.False(unmuted.IsMuted);
    }

    // The other half of the merge risk: with fields carried forward, IsInMeeting must not stick at true after
    // the call ends. Teams' end-of-call frame is the idle one above — permissions all false, no state — so the
    // permissions are used as the in-call signal when the state doesn't say.
    [Fact]
    public void Merge_LeavingTheCallClearsInMeeting()
    {
        var inCall = Apply(null, InCallFrame);
        Assert.True(inCall.IsInMeeting);

        var afterHangUp = Apply(inCall, IdleFrame);
        Assert.False(afterHangUp.IsInMeeting);
        Assert.False(afterHangUp.CanToggleMute);
    }

    // A call starting is sometimes announced by permissions alone; the strip has to come up for that too.
    [Fact]
    public void Merge_InfersAnActiveCallFromPermissionsAlone()
    {
        var snapshot = Apply(Apply(null, IdleFrame), """
        {"meetingUpdate":{"meetingPermissions":{"canToggleMute":true,"canLeave":true}}}
        """);
        Assert.True(snapshot.IsInMeeting);
    }

    // An explicit isInMeeting always wins over the inference — including the odd case of a call Teams says
    // you're in but won't let you mute (an organiser hard-mute).
    [Fact]
    public void Merge_ExplicitStateBeatsTheInference()
    {
        var snapshot = Apply(null, """
        {"meetingUpdate":{"meetingState":{"isInMeeting":true,"isMuted":true},
         "meetingPermissions":{"canToggleMute":false,"canLeave":false}}}
        """);

        Assert.True(snapshot.IsInMeeting);
        Assert.True(snapshot.IsMuted);
        Assert.False(snapshot.CanToggleMute);

        // Which is precisely the state where the overlay must disable its mute button rather than no-op:
        // the link applies, so the generic device-mute fallback is not used.
        var mic = new MicSnapshot(
            [new MicUser("MSTeams_8wekyb3d8bbwe", "Microsoft Teams", 1, true, null)],
            DeviceMuted: false, DeviceName: "Mic");
        Assert.True(MicApps.CallLinkApplies(mic, snapshot, CallLinkState.Connected));
    }

    [Fact]
    public void Merge_EmptyUpdateChangesNothing()
    {
        var inCall = Apply(null, InCallFrame);
        Assert.Equal(inCall, Apply(inCall, """{"meetingUpdate":{}}"""));
    }
}
