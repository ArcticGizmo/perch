using Perch.Data.Control;
using Xunit;

namespace Perch.Tests;

/// <summary>The background-permission attention decider (docs/session-composer-enhancements plan / v0
/// punch-list): a paused-turn prompt that arrives while the session window is in the background raises a
/// desktop cue, once per item.</summary>
public class SessionAttentionTests
{
    private static PermissionItem Perm(string tool = "Bash") =>
        new(new PermissionRequestEvent("r", tool, "do a thing", "{}", null));

    [Fact]
    public void Alerts_OncePerPending_WhenBackground()
    {
        var a = new SessionAttention();
        var p = Perm();

        Assert.True(a.Evaluate(p, windowActive: false));    // first time pending & background → alert
        Assert.False(a.Evaluate(p, windowActive: false));   // same item → no repeat
        Assert.False(a.Evaluate(p, windowActive: false));
    }

    [Fact]
    public void NoAlert_WhileWindowActive()
    {
        var a = new SessionAttention();
        Assert.False(a.Evaluate(Perm(), windowActive: true));
    }

    [Fact]
    public void Alerts_WhenUserTabsAwayFromAPendingPrompt()
    {
        var a = new SessionAttention();
        var p = Perm();
        Assert.False(a.Evaluate(p, windowActive: true));    // arrived while focused — no cue yet
        Assert.True(a.Evaluate(p, windowActive: false));    // user tabbed away, still pending → alert now
        Assert.False(a.Evaluate(p, windowActive: false));   // and only once
    }

    [Fact]
    public void ReArmsAfterResolve_SoTheNextPermissionAlerts()
    {
        var a = new SessionAttention();
        var first = Perm();
        Assert.True(a.Evaluate(first, windowActive: false));
        Assert.False(a.Evaluate(null, windowActive: false));  // resolved — clears the latch

        var second = Perm();
        Assert.True(a.Evaluate(second, windowActive: false)); // a fresh permission alerts again
    }

    [Fact]
    public void PlanApprovalInput_ParsesPlanText()
    {
        Assert.Equal("## Plan\n- do the thing", PlanApprovalInput.Parse("""{"plan":"## Plan\n- do the thing"}"""));
        Assert.Null(PlanApprovalInput.Parse("{}"));
        Assert.Null(PlanApprovalInput.Parse("not json"));
    }
}
