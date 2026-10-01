using Perch.Data.Roost;
using Xunit;

namespace Perch.Tests;

/// <summary>
/// Guards <see cref="TypingHold"/>: typing = focused + a keystroke within the pause; release on send, blur or the
/// pause.
/// </summary>
public class TypingHoldTests
{
    private static readonly DateTime T0 = new(2026, 9, 25, 10, 0, 0);

    [Fact]
    public void NobodyTypingByDefault()
    {
        var h = new TypingHold();
        Assert.Null(h.TypingIn(T0));
        Assert.Null(h.ReleasesAt(T0));
    }

    [Fact]
    public void FocusAloneIsNotTyping()
    {
        var h = new TypingHold();
        h.Focused("a");
        Assert.Null(h.TypingIn(T0));
    }

    [Fact]
    public void AKeystrokeHoldsUntilThePauseElapses()
    {
        var h = new TypingHold();
        h.Keystroke("a", T0);
        Assert.Equal("a", h.TypingIn(T0.AddSeconds(1.9)));
        Assert.Equal(T0 + TypingHold.Pause, h.ReleasesAt(T0.AddSeconds(1)));
        Assert.Null(h.TypingIn(T0 + TypingHold.Pause));
        Assert.Null(h.ReleasesAt(T0 + TypingHold.Pause));
    }

    [Fact]
    public void EachKeystrokeExtendsTheHold()
    {
        var h = new TypingHold();
        h.Keystroke("a", T0);
        h.Keystroke("a", T0.AddSeconds(1.5));
        Assert.Equal("a", h.TypingIn(T0.AddSeconds(3)));
    }

    [Fact]
    public void SendReleasesAtOnceButKeepsFocus()
    {
        var h = new TypingHold();
        h.Keystroke("a", T0);
        h.Sent("a");
        Assert.Null(h.TypingIn(T0));
        h.Keystroke("a", T0.AddSeconds(1));   // still focused — the next reply holds again
        Assert.Equal("a", h.TypingIn(T0.AddSeconds(1)));
    }

    [Fact]
    public void BlurReleasesAtOnce()
    {
        var h = new TypingHold();
        h.Keystroke("a", T0);
        h.Blurred("a");
        Assert.Null(h.TypingIn(T0));
    }

    [Fact]
    public void BlurOrSendOfAnotherPaneIsIgnored()
    {
        var h = new TypingHold();
        h.Keystroke("a", T0);
        h.Blurred("b");
        h.Sent("b");
        Assert.Equal("a", h.TypingIn(T0));
    }

    [Fact]
    public void FocusMovingToAnotherComposerDropsTheOldHold()
    {
        var h = new TypingHold();
        h.Keystroke("a", T0);
        h.Focused("b");
        Assert.Null(h.TypingIn(T0));
    }
}
