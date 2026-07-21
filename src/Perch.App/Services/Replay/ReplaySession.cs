using Perch.Data.Replay;

namespace Perch.Avalonia.Services.Replay;

/// <summary>
/// Process-wide handle to the active replay, set by <see cref="ReplayBootstrap"/> before the Avalonia
/// app boots and read by <c>App</c> to wire the projector's process-probe into the monitor and to gate
/// out the live-only startup work (hook install, update checks, PATH registration) that must never run
/// against a disposable sandbox. Null in a normal launch.
/// </summary>
internal sealed class ReplaySession
{
    public required Recording Recording { get; init; }
    public required ReplayClock Clock { get; init; }
    public required Projector Projector { get; init; }
    public required string SandboxDir { get; init; }

    public long SceneDurationMs => Recording.SceneDurationMs;

    public static ReplaySession? Current { get; set; }
    public static bool IsActive => Current != null;
}
