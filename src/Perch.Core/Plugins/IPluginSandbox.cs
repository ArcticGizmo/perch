namespace Perch.Plugins;

/// <summary>
/// Launches a plugin's entry process. This is the seam where OS-specific hardening lives: the M1 default
/// (<see cref="ProcessPluginSandbox"/>) is a plain separate process — already a real trust boundary — and
/// a later Windows implementation (Perch.Platform.Windows) launches it with a restricted/AppContainer
/// token for defence in depth. Kept behind an interface per the "every OS capability behind a Core
/// interface" rule so the mac head can supply a <c>sandbox-exec</c> variant.
/// </summary>
internal interface IPluginSandbox
{
    /// <summary>Starts the process described by <paramref name="spec"/> with stdio redirected, or throws
    /// if it cannot be started (the caller treats that as a plugin fault).</summary>
    IPluginProcess Launch(PluginLaunchSpec spec);
}

/// <summary>What to launch and where. <see cref="WorkingDirectory"/> is pinned to the plugin's own
/// install folder so a relative <see cref="Command"/> resolves there and the process starts confined to
/// its directory.</summary>
internal sealed record PluginLaunchSpec(
    string WorkingDirectory,
    string Command,
    IReadOnlyList<string> Args);

/// <summary>A launched plugin process, abstracted over <see cref="System.Diagnostics.Process"/> so the
/// protocol driver (<see cref="PluginSession"/>) is unit-testable against in-memory streams.</summary>
internal interface IPluginProcess : IDisposable
{
    /// <summary>The process's stdin (host → plugin).</summary>
    TextWriter StandardInput { get; }

    /// <summary>The process's stdout (plugin → host).</summary>
    TextReader StandardOutput { get; }

    /// <summary>Completes with the exit code when the process ends, or is cancelled by <paramref name="ct"/>
    /// (on which the caller then <see cref="Kill"/>s it).</summary>
    Task<int> WaitForExitAsync(CancellationToken ct);

    /// <summary>Best-effort terminate of the process and its children.</summary>
    void Kill();
}
