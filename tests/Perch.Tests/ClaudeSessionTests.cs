using Perch.Data;
using Xunit;

namespace Perch.Tests;

/// <summary>
/// Covers the pure elapsed-time helpers on <see cref="ClaudeSession"/> — the compact "run time" and
/// "waiting on you" labels the overlay paints. They read <see cref="System.DateTime.Now"/>, so the
/// tests seed a start well in the past and assert the most-significant-unit formatting.
/// </summary>
public class ClaudeSessionTests
{
    private static ClaudeSession Awaiting(DateTime? since) =>
        new("1234", "sess", SessionStatus.AwaitingInput, "C:\\proj", "proj", DateTime.Now,
            AwaitingSince: since);

    private static ClaudeSession Running(DateTime? since) =>
        new("1234", "sess", SessionStatus.Running, "C:\\proj", "proj", DateTime.Now,
            RunningSince: since);

    [Fact]
    public void AwaitingElapsedLabel_NullWhenNotWaiting()
    {
        Assert.Null(Awaiting(null).AwaitingElapsedLabel());
        Assert.Null(Awaiting(null).AwaitingElapsed());
    }

    [Fact]
    public void AwaitingElapsedLabel_ShowsMostSignificantUnit()
    {
        Assert.Equal("5s", Awaiting(DateTime.Now.AddSeconds(-5)).AwaitingElapsedLabel());
        Assert.Equal("3m", Awaiting(DateTime.Now.AddMinutes(-3)).AwaitingElapsedLabel());
        Assert.Equal("2h", Awaiting(DateTime.Now.AddHours(-2)).AwaitingElapsedLabel());
    }

    [Fact]
    public void AwaitingElapsed_ClampsNegativeToZero()
    {
        // A start stamped slightly in the future (clock skew) must not surface as a negative wait.
        Assert.Equal(TimeSpan.Zero, Awaiting(DateTime.Now.AddSeconds(30)).AwaitingElapsed());
    }

    [Fact]
    public void RunningElapsedLabel_StillFormatsAsBefore()
    {
        Assert.Null(Running(null).RunningElapsedLabel());
        Assert.Equal("4m", Running(DateTime.Now.AddMinutes(-4)).RunningElapsedLabel());
    }
}
