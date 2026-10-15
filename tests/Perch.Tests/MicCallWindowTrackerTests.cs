using Perch.Data;
using Xunit;

namespace Perch.Tests;

public class MicCallWindowTrackerTests
{
    private const string Teams = "MSTeams_8wekyb3d8bbwe";
    private const string Obs = @"C:\obs\obs64.exe";

    [Fact]
    public void FirstPoll_DoesNotLearn_BecauseTheCallStartedAtAnUnknownTime()
    {
        var tracker = new MicCallWindowTracker();
        var asked = false;

        var windows = tracker.Observe([Teams], _ => { asked = true; return 0x1234; });

        Assert.Equal(0, windows[Teams]);
        Assert.False(asked);
    }

    [Fact]
    public void NewHolder_LearnsTheForegroundWindow_AndKeepsItWhileTheMicIsHeld()
    {
        var tracker = new MicCallWindowTracker();
        tracker.Observe([], _ => 0);

        // Joined from the meeting window, so it's in front when the mic goes live.
        Assert.Equal(0x1234, tracker.Observe([Teams], _ => 0x1234)[Teams]);

        // Later the user is in the hub. The foreground moved, but the call's window didn't.
        Assert.Equal(0x1234, tracker.Observe([Teams], _ => 0x9999)[Teams]);
    }

    [Fact]
    public void ForegroundBelongingToAnotherApp_IsRememberedAsUnknown_NotRelearnedLater()
    {
        var tracker = new MicCallWindowTracker();
        tracker.Observe([], _ => 0);

        Assert.Equal(0, tracker.Observe([Teams], _ => 0)[Teams]);

        // A later poll mustn't pick up whatever Teams window happens to be in front by then. That's the
        // most-recently-used guess the activator already falls back to.
        var asked = false;
        Assert.Equal(0, tracker.Observe([Teams], _ => { asked = true; return 0x9999; })[Teams]);
        Assert.False(asked);
    }

    [Fact]
    public void ReleasingTheMic_ForgetsTheWindow_SoTheNextCallLearnsAfresh()
    {
        var tracker = new MicCallWindowTracker();
        tracker.Observe([], _ => 0);
        tracker.Observe([Teams], _ => 0x1234);

        Assert.Empty(tracker.Observe([], _ => 0));
        Assert.Equal(0x5678, tracker.Observe([Teams], _ => 0x5678)[Teams]);
    }

    [Fact]
    public void EachHolder_IsTrackedIndependently()
    {
        var tracker = new MicCallWindowTracker();
        tracker.Observe([Teams], _ => 0);

        var windows = tracker.Observe([Teams, Obs], id => id == Obs ? 0x0B5 : 0x9999);

        Assert.Equal(0, windows[Teams]);    // seen on the first poll, still unknown
        Assert.Equal(0x0B5, windows[Obs]);  // newly arrived, learned
    }

    [Fact]
    public void Reset_MakesTheNextPollAFirstPollAgain()
    {
        var tracker = new MicCallWindowTracker();
        tracker.Observe([], _ => 0);
        tracker.Reset();

        Assert.Equal(0, tracker.Observe([Teams], _ => 0x1234)[Teams]);
    }

    [Fact]
    public void Identities_CompareCaseInsensitively()
    {
        var tracker = new MicCallWindowTracker();
        tracker.Observe([], _ => 0);
        tracker.Observe([@"C:\Zoom\Zoom.exe"], _ => 0x1234);

        Assert.Equal(0x1234, tracker.Observe([@"c:\zoom\zoom.exe"], _ => 0x9999)[@"C:\ZOOM\ZOOM.EXE"]);
    }
}
