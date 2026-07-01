using Perch.Data;
using Xunit;

namespace Perch.Tests;

/// <summary>
/// Covers <see cref="SessionMonitor.IsAwaitingInput"/> — the pure mapping from Claude Code's raw
/// (status, waitingFor) pair to whether the session is genuinely blocked on user input. The full
/// scan can't run against fixtures (it gates every session on a live OS process), so this predicate
/// is extracted and tested in isolation.
/// </summary>
public class SessionStatusTests
{
    [Fact]
    public void PermissionPrompt_IsAwaitingInput()
    {
        // The canonical "the model needs you to respond" case.
        Assert.True(SessionMonitor.IsAwaitingInput("waiting", "permission prompt"));
    }

    [Fact]
    public void WaitingWithoutHint_IsAwaitingInput()
    {
        Assert.True(SessionMonitor.IsAwaitingInput("waiting", null));
    }

    [Fact]
    public void NonEmptyWaitingForWithoutWaitingStatus_IsAwaitingInput()
    {
        // Some flows surface a hint without flipping status to "waiting".
        Assert.True(SessionMonitor.IsAwaitingInput("idle", "tool use"));
    }

    [Fact]
    public void DialogOpen_IsNotAwaitingInput()
    {
        // Regression: /workflows (and /model, /config, …) leave the session at
        // status:"waiting", waitingFor:"dialog open" the whole time the overlay is up. The user is
        // poking at the CLI menu, not being prompted, so it must not read as awaiting input.
        Assert.False(SessionMonitor.IsAwaitingInput("waiting", "dialog open"));
    }

    [Fact]
    public void DialogOpen_IsCaseInsensitive()
    {
        Assert.False(SessionMonitor.IsAwaitingInput("waiting", "Dialog Open"));
    }

    [Fact]
    public void Busy_IsNotAwaitingInput()
    {
        Assert.False(SessionMonitor.IsAwaitingInput("busy", null));
    }

    [Fact]
    public void Idle_IsNotAwaitingInput()
    {
        Assert.False(SessionMonitor.IsAwaitingInput("idle", null));
    }
}
