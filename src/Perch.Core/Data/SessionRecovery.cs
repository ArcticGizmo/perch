namespace Perch.Data;

/// <summary>The fixed numbers behind session recovery (docs/session-recovery-plan.md), in one place to tune.</summary>
internal static class SessionRecovery
{
    /// <summary>A session that ended this long (or less) before a shutdown is "just before shutdown" (D6).</summary>
    public static readonly TimeSpan ShutdownWindow = TimeSpan.FromMinutes(10);

    /// <summary>A session may finish its exit flush a little <em>after</em> the shutdown was stamped (the OS asks apps
    /// to close in no fixed order), so an ending this soon after a shutdown still counts as just before it.</summary>
    public static readonly TimeSpan ShutdownGrace = TimeSpan.FromMinutes(2);

    /// <summary>How far back the Recent list looks (Q1). Shutdowns older than this are pruned from the ledger too.</summary>
    public static readonly TimeSpan RecentWindow = TimeSpan.FromDays(3);

    /// <summary>How many Recent rows the overlay and the Roost rail show (Q1); "More…" opens the full list.</summary>
    public const int RecentRows = 5;

    /// <summary>How often Perch stamps itself alive in the ledger. Also the slack allowed when matching a logged
    /// shutdown against the last heartbeat, since heartbeats can keep landing while the OS is shutting down.</summary>
    public static readonly TimeSpan HeartbeatInterval = TimeSpan.FromSeconds(60);

    /// <summary>True when <paramref name="endedAt"/> falls in the window just before (or the grace just after) any of
    /// <paramref name="shutdowns"/>.</summary>
    public static bool JustBeforeShutdown(DateTime endedAt, IEnumerable<DateTime> shutdowns) =>
        shutdowns.Any(s => endedAt >= s - ShutdownWindow && endedAt <= s + ShutdownGrace);
}
