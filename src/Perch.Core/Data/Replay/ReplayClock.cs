using Perch.Data;

namespace Perch.Data.Replay;

/// <summary>
/// The virtual clock a replay drives. The projector advances it to the scrub position's absolute instant
/// before each scan, so every <see cref="Clock.Now"/> site the state machine reads (idle/running windows,
/// sub-agent staleness, elapsed timers) sees replay time rather than wall time. Install with
/// <see cref="Clock.SetProvider"/> during the replay bootstrap.
/// </summary>
internal sealed class ReplayClock : IClockProvider
{
    private long _utcTicks;

    public ReplayClock(DateTime initialUtc) => SetUtc(initialUtc);

    /// <summary>Moves the clock to <paramref name="utc"/> (kind coerced to UTC). Set before each scan.</summary>
    public void SetUtc(DateTime utc) =>
        Interlocked.Exchange(ref _utcTicks, DateTime.SpecifyKind(utc, DateTimeKind.Utc).Ticks);

    public DateTime UtcNow => new(Interlocked.Read(ref _utcTicks), DateTimeKind.Utc);
    public DateTime Now => UtcNow.ToLocalTime();
}
