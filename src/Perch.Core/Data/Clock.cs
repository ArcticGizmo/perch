namespace Perch.Data;

/// <summary>
/// Supplies "now" to the parts of Perch that compare against on-disk timestamps. Abstracted so replay
/// can drive the app under a virtual clock (scrub forward and back) and so time-dependent behaviour is
/// unit-testable. See <see cref="Clock"/>.
/// </summary>
public interface IClockProvider
{
    DateTime Now { get; }
    DateTime UtcNow { get; }
}

/// <summary>The real wall clock — the default provider. Stateless singleton.</summary>
public sealed class SystemClock : IClockProvider
{
    public static readonly SystemClock Instance = new();
    private SystemClock() { }
    public DateTime Now => DateTime.Now;
    public DateTime UtcNow => DateTime.UtcNow;
}

/// <summary>
/// Ambient source of "now". A static owner mirroring the <see cref="ClaudePaths"/> precedent: the
/// codebase has no DI container and time is read from ~30 scattered sites (including record methods and
/// static helpers), so constructor-injecting a clock everywhere would be far more invasive than the
/// problem warrants. Backed by <see cref="SystemClock"/> unless a replay bootstrap or test swaps in
/// another provider via <see cref="SetProvider"/>.
/// </summary>
public static class Clock
{
    private static IClockProvider _provider = SystemClock.Instance;

    public static DateTime Now => _provider.Now;
    public static DateTime UtcNow => _provider.UtcNow;

    /// <summary>Installs the ambient clock. Intended for the replay bootstrap and tests only; production
    /// code leaves the default <see cref="SystemClock"/> in place.</summary>
    public static void SetProvider(IClockProvider provider) => _provider = provider;

    /// <summary>Restores the real wall clock. Tests call this to undo <see cref="SetProvider"/>.</summary>
    public static void Reset() => _provider = SystemClock.Instance;
}
