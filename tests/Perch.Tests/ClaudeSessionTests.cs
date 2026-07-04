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

    private static ClaudeSession WithEntrypoint(string? entrypoint) =>
        new("1234", "sess", SessionStatus.Running, "C:\\proj", "proj", DateTime.Now,
            Entrypoint: entrypoint);

    [Theory]
    [InlineData("sdk-ts")]
    [InlineData("sdk-py")]
    [InlineData("SDK-TS")]   // some other non-cli entrypoint — still not interactive
    public void IsBackground_TrueForSdkEntrypoints(string entrypoint)
    {
        Assert.True(WithEntrypoint(entrypoint).IsBackground);
    }

    [Theory]
    [InlineData("cli")]
    [InlineData("CLI")]   // case-insensitive: an interactive terminal, however cased
    public void IsBackground_FalseForCli(string entrypoint)
    {
        Assert.False(WithEntrypoint(entrypoint).IsBackground);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void IsBackground_FalseWhenAbsent(string? entrypoint)
    {
        // A missing/blank entrypoint is treated as interactive (the safe default): only an explicit
        // non-cli value flips the background marker on. Blank normalises to null.
        var session = WithEntrypoint(entrypoint);
        Assert.Null(session.Entrypoint);
        Assert.False(session.IsBackground);
    }
}
