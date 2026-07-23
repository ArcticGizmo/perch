namespace Perch.Platform;

/// <summary>
/// A point-in-time snapshot of the system's current media session — what's playing, which transport
/// commands the source supports, and (when the source reports it) how far through the track it is —
/// surfaced by the platform's now-playing service (on Windows the System Media Transport Controls).
/// Immutable; the controller swaps a whole new snapshot in on each change so a reader never sees a
/// half-updated one. <see cref="Title"/> is the only field guaranteed non-empty when a snapshot exists;
/// <see cref="Artist"/> may be blank (a browser tab, a podcast, …), and <see cref="Duration"/> is
/// <see cref="TimeSpan.Zero"/> when the source doesn't report a timeline (a live stream, most browser
/// media) — in which case there's no progress to draw.
/// </summary>
public sealed record MediaSnapshot(
    string Title,
    string Artist,
    bool IsPlaying,
    bool CanPlayPause,
    bool CanNext,
    bool CanPrevious,
    TimeSpan Position = default,
    TimeSpan Duration = default);

/// <summary>
/// The platform-specific "what's playing" service behind a seam, so the overlay can show a currently-playing
/// strip with previous / play-pause / next controls without referencing any OS media API directly. On
/// Windows this wraps the System Media Transport Controls (the same session the volume flyout drives);
/// other heads supply a no-op until a native implementation lands.
///
/// Event-driven rather than polled: <see cref="Changed"/> fires whenever the current session, its metadata,
/// or its playback state moves, and <see cref="Current"/> reads the latest snapshot (null when nothing is
/// playing or the platform can't report). The event may arrive on an arbitrary thread — the host marshals
/// to the UI thread. Every member is best-effort: an implementation swallows failures rather than throwing.
/// </summary>
public interface IMediaController : IDisposable
{
    /// <summary>The current now-playing snapshot, or null when nothing is playing / the platform can't report.</summary>
    MediaSnapshot? Current { get; }

    /// <summary>Raised (possibly off the UI thread) whenever <see cref="Current"/> changes.</summary>
    event Action? Changed;

    /// <summary>Begin listening for the system media session. Idempotent and best-effort — a second call,
    /// or one on a platform without media support, is a no-op.</summary>
    void Start();

    /// <summary>Stop listening and release the subscription. <see cref="Current"/> reverts to null.</summary>
    void Stop();

    /// <summary>Toggle play/pause on the current session (no-op when unsupported or nothing is playing).</summary>
    void TogglePlayPause();

    /// <summary>Skip to the next track on the current session.</summary>
    void Next();

    /// <summary>Skip to the previous track on the current session.</summary>
    void Previous();
}
