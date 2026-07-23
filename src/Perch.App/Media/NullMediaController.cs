using Perch.Platform;

namespace Perch.Avalonia.Media;

/// <summary>
/// The no-op <see cref="IMediaController"/> used wherever there's no native now-playing source wired up:
/// the cross-platform (macOS/Linux) head, and — mirroring the notifier's dual guard — a Windows head that
/// finds itself running on a non-Windows OS. <see cref="Current"/> is always null, so the overlay's media
/// strip simply never appears. A macOS implementation (over <c>MPNowPlayingInfoCenter</c> /
/// <c>MPRemoteCommandCenter</c>) can replace this on that head later.
/// </summary>
internal sealed class NullMediaController : IMediaController
{
    public MediaSnapshot? Current => null;
    public event Action? Changed { add { } remove { } }

    public void Start() { }
    public void Stop() { }
    public void TogglePlayPause() { }
    public void Next() { }
    public void Previous() { }
    public void Dispose() { }
}
