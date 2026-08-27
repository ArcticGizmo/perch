namespace Perch.Platform;

/// <summary>
/// Resolves which editor/IDE (if any) hosts a session, by inspecting the OS process ancestry of the
/// session's process — a terminal running under an IDE, or the IDE's own integrated terminal, both surface
/// as an IDE ancestor. Abstracted because walking a process tree is OS-specific (Win32 toolhelp on Windows,
/// libproc/sysctl on macOS); the executable→host mapping it applies is shared
/// (<see cref="Perch.Data.IdeHost.FromExecutable"/>). Heads without an implementation use
/// <see cref="NullIdeHostDetector"/>, which simply reports "no IDE" so the glyph never appears.
/// </summary>
public interface IIdeHostDetector
{
    /// <summary>
    /// The IDE hosting the process with the given pid, or null when it isn't running under a recognised IDE
    /// (or the ancestry can't be read). Called once per session per scan, so implementations should be cheap
    /// — e.g. cache the process snapshot for a short window. Must never throw.
    /// </summary>
    Perch.Data.IdeHost? Detect(int pid);
}

/// <summary>The no-op detector: always reports "no IDE". The default when a head has no real implementation
/// (the macOS stub, replay, tests).</summary>
public sealed class NullIdeHostDetector : IIdeHostDetector
{
    public static readonly NullIdeHostDetector Instance = new();

    public Perch.Data.IdeHost? Detect(int pid) => null;
}
