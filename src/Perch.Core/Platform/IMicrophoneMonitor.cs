namespace Perch.Platform;

/// <summary>
/// One application currently holding the microphone, as reported by the platform's capture stack. Kept
/// deliberately app-agnostic: Teams, Slack, Zoom, a browser tab and OBS all arrive through the same shape,
/// and nothing in this record knows which of them it is describing. <see cref="Perch.Data.MicApps"/> is
/// where an <see cref="Identity"/> is turned into a display name or recognised as a specific product, so
/// that knowledge stays in one testable place instead of leaking into each platform implementation.
///
/// <see cref="Identity"/> is the stable key — on Windows a package family name (<c>MSTeams_8wekyb3d8bbwe</c>)
/// or a full executable path — and is the only field guaranteed non-empty.
/// </summary>
/// <param name="Identity">The platform's stable identifier for the app (package family name or exe path).</param>
/// <param name="DisplayName">A human-readable name, resolved best-effort; falls back to something derived
/// from <paramref name="Identity"/> rather than being blank.</param>
/// <param name="ProcessId">The process holding the capture stream, or 0 when only the privacy ledger knows
/// about this app (it recorded the use, but no live stream is attributable to a pid). Note this can be a
/// media/helper <em>child</em> process rather than the one owning the app's windows — see
/// <see cref="IWindowActivator.FocusAppWindowForProcess"/>.</param>
/// <param name="IsStreaming">Whether audio is actually flowing right now, as opposed to the app merely
/// holding the device open. Every user in a snapshot holds the mic; this distinguishes the app with a live
/// stream from one that has opened the device and gone quiet. Treat it as a detail for the tooltip, not as
/// the presence test — <see cref="MicSnapshot.InUse"/> is the presence test.</param>
/// <param name="Since">When this app most recently opened the mic, when the platform records it.</param>
public sealed record MicUser(
    string Identity,
    string DisplayName,
    int ProcessId,
    bool IsStreaming,
    DateTimeOffset? Since);

/// <summary>
/// A point-in-time picture of microphone use across the machine, plus the state of the capture device's
/// own mute. Immutable; the monitor swaps a whole new snapshot in so a reader never sees a half-updated
/// one — the same discipline as <see cref="MediaSnapshot"/>.
/// </summary>
/// <param name="Users">The apps holding the microphone <em>right now</em>, most-recently-started first.
/// Empty when the mic is idle — history is deliberately not reported, so a reader never has to work out
/// which entries are stale.</param>
/// <param name="DeviceMuted">Whether the default capture endpoint is muted at the OS level. This is the
/// "hardware" mute — an app in a call generally does <em>not</em> know about it, which is exactly the
/// "you're on mute" trap, so any UI driving it has to say plainly that's what it is.</param>
/// <param name="DeviceName">The default capture device's friendly name, or null when unavailable.</param>
public sealed record MicSnapshot(
    IReadOnlyList<MicUser> Users,
    bool DeviceMuted,
    string? DeviceName)
{
    /// <summary>The app to talk about in the UI, or null when the mic is idle. Several apps can hold the mic
    /// at once (a Teams call while OBS records); the most recently started one is the one the strip names
    /// and the buttons act on.</summary>
    public MicUser? Primary => Users.Count > 0 ? Users[0] : null;

    /// <summary>Whether anything at all holds the microphone right now.</summary>
    public bool InUse => Users.Count > 0;

    // Hand-written equality because the compiler-generated record version compares Users by *reference*,
    // which would make every freshly-built snapshot unequal to the last and defeat the "skip no-op
    // updates" check in the monitor and the host. MicUser is a record, so SequenceEqual is a value compare.
    public bool Equals(MicSnapshot? other) =>
        other is not null
        && DeviceMuted == other.DeviceMuted
        && DeviceName == other.DeviceName
        && Users.SequenceEqual(other.Users);

    public override int GetHashCode() => HashCode.Combine(DeviceMuted, DeviceName, Users.Count);
}

/// <summary>
/// The platform's "who is using the microphone" seam. Intentionally generic — it reports whatever the OS
/// attributes capture to, with no product-specific behaviour anywhere in the implementation, so a Zoom or
/// Slack call surfaces exactly as well as a Teams one. Product-specific <em>extras</em> (real in-app
/// mute, meeting state) live behind <see cref="ICallController"/> and are layered on top only once the
/// app has been recognised.
///
/// Event-driven from the consumer's point of view: <see cref="Changed"/> fires when the picture moves and
/// <see cref="Current"/> reads the latest snapshot (null when the platform can't report at all — as
/// distinct from a snapshot whose <see cref="MicSnapshot.InUse"/> is false, which means "nothing is using
/// the mic"). The event may arrive on an arbitrary thread; the host marshals to the UI thread.
/// Every member is best-effort — an implementation swallows failures rather than throwing.
/// </summary>
public interface IMicrophoneMonitor : IDisposable
{
    /// <summary>The latest snapshot, or null when nothing has been read yet / the platform can't report.</summary>
    MicSnapshot? Current { get; }

    /// <summary>Raised (possibly off the UI thread) whenever <see cref="Current"/> changes.</summary>
    event Action? Changed;

    /// <summary>Begin watching microphone use. Idempotent and best-effort — a second call, or one on a
    /// platform without support, is a no-op.</summary>
    void Start();

    /// <summary>Stop watching and release any OS subscription. <see cref="Current"/> reverts to null.</summary>
    void Stop();

    /// <summary>Mutes or unmutes the default capture <em>device</em> — the blunt, universal instrument that
    /// works for any app but that the app in the call knows nothing about. Best-effort; no-op when
    /// unsupported. Prefer <see cref="ICallController.ToggleMute"/> when it is available.</summary>
    void SetDeviceMuted(bool muted);
}
