using Avalonia.Threading;
using Perch.Platform;

namespace Perch.Avalonia.Services;

/// <summary>
/// Bridges the platform <see cref="IMediaController"/> to the overlay: subscribes to its (off-thread)
/// change event and marshals each fresh <see cref="MediaSnapshot"/> onto the UI thread so the owner-drawn
/// media strip can be fed safely — the <c>Task.Run → Dispatcher.UIThread.Post</c> idiom the metrics host
/// uses, here with the controller's WinRT events standing in for the sampler.
///
/// Lifecycle mirrors the usage/status hosts: <see cref="Start"/> begins listening (and pushes the current
/// reading immediately, so a strip enabled while something's already playing shows at once);
/// <see cref="Stop"/> releases the subscription and clears the strip. The transport commands go straight to
/// the <see cref="Controller"/>.
/// </summary>
internal sealed class MediaMonitorHost : IDisposable
{
    private readonly IMediaController _controller;
    private readonly Action<MediaSnapshot?> _onChanged;
    private bool _started;

    public MediaMonitorHost(IMediaController controller, Action<MediaSnapshot?> onChanged)
    {
        _controller = controller;
        _onChanged = onChanged;
        _controller.Changed += OnControllerChanged;
    }

    /// <summary>The underlying controller, so the App can route the overlay's transport-button events
    /// (play/pause, next, previous) straight to it.</summary>
    public IMediaController Controller => _controller;

    /// <summary>Begins listening for the system media session and pushes the current reading. Idempotent;
    /// call on the UI thread.</summary>
    public void Start()
    {
        if (_started) return;
        _started = true;
        _controller.Start();
        Push(); // seed with whatever's already playing (may be null until the first async read lands)
    }

    /// <summary>Stops listening and clears the strip.</summary>
    public void Stop()
    {
        if (!_started) return;
        _started = false;
        _controller.Stop();
        _onChanged(null);
    }

    // Fires on a WinRT thread — hop to the UI thread before touching the canvas.
    private void OnControllerChanged() => Push();

    private void Push()
    {
        var snapshot = _controller.Current;
        if (Dispatcher.UIThread.CheckAccess()) _onChanged(snapshot);
        else Dispatcher.UIThread.Post(() => _onChanged(snapshot));
    }

    public void Dispose()
    {
        _controller.Changed -= OnControllerChanged;
        _controller.Dispose();
    }
}
