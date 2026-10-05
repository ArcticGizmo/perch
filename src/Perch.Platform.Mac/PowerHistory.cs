using Perch.Platform;

namespace Perch.Platform.Mac;

/// <summary>
/// macOS <see cref="IPowerHistory"/> — stub. macOS keeps shutdown/boot history in the unified log (<c>log show</c>,
/// <c>last reboot</c>); reading it is a later port item. Until then this reports "unsupported", so
/// <c>ShutdownClock</c> falls back to Perch's own shutdown stamp and heartbeat.
/// </summary>
public sealed class PowerHistory : IPowerHistory
{
    public bool IsSupported => false;   // TODO(mac): read shutdown history from the unified log

    public DateTime? LastShutdownBetween(DateTime after, DateTime before) => null;
}
