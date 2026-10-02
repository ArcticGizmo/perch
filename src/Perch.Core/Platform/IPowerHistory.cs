namespace Perch.Platform;

/// <summary>
/// The OS's own record of when the device shut down or restarted — the fallback for finding a shutdown Perch didn't
/// stamp itself (it wasn't running, or was killed first). Windows reads the System event log; Fast Startup means
/// boot time and uptime can't be used (docs/session-recovery-plan.md, D5). Read off the UI thread: it can be slow.
/// </summary>
public interface IPowerHistory
{
    /// <summary>False on a head with no readable power history. <c>ShutdownClock</c> then falls back to Perch's own
    /// last heartbeat, which can't tell a power-off from a Perch crash.</summary>
    bool IsSupported { get; }

    /// <summary>The most recent shutdown or restart strictly after <paramref name="after"/> and before
    /// <paramref name="before"/> (local times), or null when there was none or it can't be read.</summary>
    DateTime? LastShutdownBetween(DateTime after, DateTime before);
}
