using System.Diagnostics.Eventing.Reader;
using Perch.Data;
using Perch.Platform;

namespace Perch.Platform.Windows;

/// <summary>
/// Windows <see cref="IPowerHistory"/>, read from the System event log (readable without admin). With Fast Startup
/// (the default) a shutdown doesn't reset boot time or uptime and logs none of the classic 1074/6006 events, so the
/// shutdown is read off the power transitions instead (docs/session-recovery-plan.md, R0): <c>Kernel-Power</c> 42
/// "entering sleep" / 107 "resumed", <c>Kernel-Boot</c> 27 "boot type", plus <c>User32</c> 1074 and
/// <c>EventLog</c> 6006 for a full shutdown or restart. <see cref="PowerTimeline"/> turns them into shutdowns.
/// <para>Event 27 is also used by unrelated providers (a network driver logs it at every power transition), so each
/// event is matched on its provider as well as its id. Best-effort: any failure reads as "no shutdown".</para>
/// </summary>
public sealed class PowerHistory : IPowerHistory
{
    public bool IsSupported => OperatingSystem.IsWindows();

    public DateTime? LastShutdownBetween(DateTime after, DateTime before)
    {
        if (!OperatingSystem.IsWindows() || before <= after) return null;
        try
        {
            // The read runs past `before` so the boot that follows a shutdown is seen.
            return PowerTimeline.LastShutdown(ReadPowerEvents(after), after, before);
        }
        catch
        {
            return null;
        }
    }

    private static List<PowerEvent> ReadPowerEvents(DateTime since)
    {
        var utc = since.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'");
        var query = new EventLogQuery("System", PathType.LogName,
            "*[System[(EventID=27 or EventID=42 or EventID=107 or EventID=1074 or EventID=6006)" +
            $" and TimeCreated[@SystemTime>='{utc}']]]");
        var events = new List<PowerEvent>();
        using var reader = new EventLogReader(query);
        for (var record = reader.ReadEvent(); record is not null; record = reader.ReadEvent())
        {
            using (record)
            {
                if (record.TimeCreated is { } at && Classify(record.ProviderName, record.Id) is { } kind)
                    events.Add(new PowerEvent(kind, at));
            }
        }
        return events;
    }

    private static PowerEventKind? Classify(string? provider, int id) => (provider, id) switch
    {
        ("Microsoft-Windows-Kernel-Power", 42) => PowerEventKind.SleepEntered,
        ("Microsoft-Windows-Kernel-Power", 107) => PowerEventKind.Resumed,
        ("Microsoft-Windows-Kernel-Boot", 27) => PowerEventKind.Boot,
        ("User32", 1074) or ("EventLog", 6006) => PowerEventKind.ShutdownStarted,
        _ => null,
    };
}
