using Perch.Data;

namespace Perch.Platform;

/// <summary>
/// Plays the short system chime that accompanies a local desktop notification. A local-only
/// affordance (external/ntfy pushes never play sound). Windows maps the kinds to the system
/// Asterisk/Exclamation tones; other platforms pick an equivalent or no-op.
/// </summary>
public interface IAudioCue
{
    /// <summary>Plays the chime matching <paramref name="kind"/>. Fire-and-forget; never throws.</summary>
    void Play(NotificationKind kind);
}
