using Perch.Data.Control;
using Perch.Data.Roost;
using Xunit;

namespace Perch.Tests;

/// <summary>
/// Guards <see cref="AttentionSeen"/> — the Roost's toast de-dup — and how it feeds <see cref="SessionAttention"/>:
/// no cue for a prompt already on screen in an active Roost; cues still fire when the Roost is open but inactive,
/// or the pane is out of view; and a prompt first seen in the Roost still alerts once the user looks away.
/// </summary>
public class AttentionSeenTests
{
    [Theory]
    [InlineData(true, false, false, true)]    // its own window is active
    [InlineData(false, true, true, true)]     // the Roost is active and shows the pane
    [InlineData(false, true, false, false)]   // Roost active, but the pane is paged/filtered/zoomed away
    [InlineData(false, false, true, false)]   // pane shown, but the Roost isn't the active window
    [InlineData(false, false, false, false)]
    public void SeenTruthTable(bool own, bool roostActive, bool onScreen, bool expected) =>
        Assert.Equal(expected, AttentionSeen.Seen(own, roostActive, onScreen));

    private static PermissionItem Pending() =>
        new(new PermissionRequestEvent("r1", "Bash", "dotnet test", "{}", null));

    [Fact]
    public void NoCueForAPromptAlreadyOnScreenInTheRoost()
    {
        var attention = new SessionAttention();
        var p = Pending();
        Assert.False(attention.Evaluate(p, AttentionSeen.Seen(false, roostActive: true, paneOnScreen: true)));
    }

    [Fact]
    public void CueFiresWhenTheRoostIsOpenButInactive()
    {
        var attention = new SessionAttention();
        Assert.True(attention.Evaluate(Pending(), AttentionSeen.Seen(false, roostActive: false, paneOnScreen: true)));
    }

    [Fact]
    public void APromptSeenInTheRoostStillAlertsOnceTheUserLooksAway()
    {
        var attention = new SessionAttention();
        var p = Pending();
        Assert.False(attention.Evaluate(p, AttentionSeen.Seen(false, true, true)));   // on screen — quiet
        Assert.True(attention.Evaluate(p, AttentionSeen.Seen(false, false, true)));   // Roost deactivated — cue
        Assert.False(attention.Evaluate(p, AttentionSeen.Seen(false, false, true)));  // …once
    }
}
