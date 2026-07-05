using Perch.Data;
using Perch.Platform;

namespace Perch.Platform.Mac;

/// <summary>
/// macOS <see cref="IAudioCue"/>. Phase 3: play the matching system sound via <c>NSSound</c>
/// (or shell out to <c>afplay</c>). Stub for now: no-op.
/// </summary>
public sealed class AudioCue : IAudioCue
{
    public void Play(NotificationKind kind) { }
}
