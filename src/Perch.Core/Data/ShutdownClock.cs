using Perch.Platform;

namespace Perch.Data;

/// <summary>
/// Works out when the device last shut down, for the "just before shutdown" flag (docs/session-recovery-plan.md, D5).
/// The sources, best first:
/// <list type="number">
/// <item>Perch's own stamp, written when the OS said it was shutting down or logging off. Exact.</item>
/// <item>The OS's power history (<see cref="IPowerHistory"/>), for a shutdown Perch didn't stamp: it wasn't running,
/// or it was killed first.</item>
/// <item>Perch's last heartbeat, only when there's no power history to ask (and Perch didn't exit cleanly). That
/// can't tell a power-off from a Perch crash, which is why the power history outranks it wherever it exists.</item>
/// </list>
/// Boot time is deliberately not a source: with Windows Fast Startup it doesn't change across a shutdown.
/// </summary>
internal static class ShutdownClock
{
    /// <summary>The shutdown that ended <paramref name="previous"/> (Perch's last run), or the latest one in the
    /// Recent window when there is no previous run. Null when there was none or it can't be told.</summary>
    public static DateTime? Resolve(LedgerRun? previous, IPowerHistory? power, DateTime now)
    {
        bool hasPower = power is { IsSupported: true };
        if (previous is null)
            return hasPower ? power!.LastShutdownBetween(now - SessionRecovery.RecentWindow, now) : null;

        if (previous.ShutdownAt is { } stamped) return stamped;

        if (hasPower)
        {
            // Only a shutdown after the previous run was last seen alive ended it. Heartbeats can still land while the
            // OS is shutting down, so allow one interval of slack before that.
            var lastSeen = previous.LastAlive ?? previous.StartedAt;
            return power!.LastShutdownBetween(lastSeen - SessionRecovery.HeartbeatInterval, now);
        }

        return previous.CleanExitAt is null ? previous.LastAlive : null;
    }
}
