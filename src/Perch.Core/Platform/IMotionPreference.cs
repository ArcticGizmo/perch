namespace Perch.Platform;

/// <summary>
/// The OS "reduce motion" accessibility preference — Windows' "Animation effects" off (Settings →
/// Accessibility → Visual effects), macOS' "Reduce motion". When set, Perch swaps its pulsing attention
/// cues (the Roost's needs-you ring, the overlay's wrong-account outline) for a steady, thicker ring. Polled,
/// not event-driven: read <see cref="ReduceMotion"/> when painting (callers cache it briefly — see <c>Pulse</c>).
/// Best-effort: an unreadable preference reads as false (animate), the platform default.
/// </summary>
public interface IMotionPreference
{
    bool ReduceMotion { get; }
}
