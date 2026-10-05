namespace Perch.Data;

/// <summary>The kinds of OS power event an <see cref="Perch.Platform.IPowerHistory"/> reads (docs/session-recovery-plan.md, R3).</summary>
internal enum PowerEventKind
{
    /// <summary>A shutdown or restart starting (Windows: <c>User32</c> 1074, <c>EventLog</c> 6006).</summary>
    ShutdownStarted,
    /// <summary>The system entering sleep — also how a Windows Fast Startup shutdown logs (<c>Kernel-Power</c> 42).</summary>
    SleepEntered,
    /// <summary>The system resuming from sleep (<c>Kernel-Power</c> 107).</summary>
    Resumed,
    /// <summary>The system booting (<c>Kernel-Boot</c> 27).</summary>
    Boot,
}

internal readonly record struct PowerEvent(PowerEventKind Kind, DateTime At);

/// <summary>Turns a sequence of OS power events into shutdowns. Pure, so it's tested on every host while the event
/// reading stays in the platform project.</summary>
internal static class PowerTimeline
{
    /// <summary>A resume this soon after "entering sleep" belongs to a Fast Startup shutdown, not a real wake-up: the
    /// kernel hibernates and logs its resume within seconds.</summary>
    public static readonly TimeSpan FastStartupResume = TimeSpan.FromMinutes(2);

    /// <summary>
    /// The newest shutdown strictly between <paramref name="after"/> and <paramref name="before"/>. A shutdown is a
    /// sleep-entered or shutdown-started event that a <b>boot</b> follows. A sleep that properly wakes up (a resume
    /// more than <see cref="FastStartupResume"/> later) isn't one. When both a shutdown-started and a sleep-entered
    /// event come before the same boot, the shutdown-started one (earlier, the moment it began) is kept.
    /// </summary>
    public static DateTime? LastShutdown(IEnumerable<PowerEvent> events, DateTime after, DateTime before)
    {
        DateTime? candidate = null, last = null;
        bool fromShutdown = false;
        foreach (var e in events.OrderBy(e => e.At))
        {
            switch (e.Kind)
            {
                case PowerEventKind.ShutdownStarted:
                    if (!fromShutdown) { candidate = e.At; fromShutdown = true; }
                    break;
                case PowerEventKind.SleepEntered:
                    if (!fromShutdown) candidate = e.At;
                    break;
                case PowerEventKind.Resumed:
                    if (!fromShutdown && candidate is { } c && e.At - c > FastStartupResume) candidate = null;
                    break;
                case PowerEventKind.Boot:
                    if (candidate is { } s && s > after && s < before) last = s;
                    candidate = null;
                    fromShutdown = false;
                    break;
            }
        }
        return last;
    }
}
