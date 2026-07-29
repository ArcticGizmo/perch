using Avalonia.Threading;
using Perch.Platform;

namespace Perch.Avalonia.Services;

/// <summary>
/// Bridges <see cref="IMicrophoneMonitor"/> to the overlay's microphone strip. The monitor raises its change
/// event off the UI thread, so it is marshalled with the <c>Dispatcher.UIThread.Post</c> idiom the media and
/// metrics hosts use.
///
/// Thin by design: the strip reports who holds the microphone and offers to jump to it, and that is all. A
/// product-specific control layer (Teams' local API, for real in-app mute and meeting state) lived here once and
/// was removed — see the remarks on <see cref="Views.OverlayCanvas"/>'s mic strip for why.
/// </summary>
internal sealed class MicMonitorHost : IDisposable
{
    private readonly IMicrophoneMonitor _mic;
    private readonly Action<MicSnapshot?> _onMic;
    private bool _started;

    public MicMonitorHost(IMicrophoneMonitor mic, Action<MicSnapshot?> onMic)
    {
        _mic = mic;
        _onMic = onMic;
        _mic.Changed += OnMicChanged;
    }

    /// <summary>The microphone monitor, for the App to read the current holder (its pid drives jump-to-app).</summary>
    public IMicrophoneMonitor Microphone => _mic;

    /// <summary>Begins watching the microphone and pushes the current reading, so a strip enabled mid-call
    /// populates at once. Idempotent; call on the UI thread.</summary>
    public void Start()
    {
        if (_started) return;
        _started = true;
        _mic.Start();
        PushMic();
    }

    /// <summary>Stops watching and clears the strip.</summary>
    public void Stop()
    {
        if (!_started) return;
        _started = false;
        _mic.Stop();
        _onMic(null);
    }

    private void OnMicChanged() => PushMic();

    private void PushMic()
    {
        var snapshot = _mic.Current;
        if (Dispatcher.UIThread.CheckAccess()) _onMic(snapshot);
        else Dispatcher.UIThread.Post(() => _onMic(snapshot));
    }

    public void Dispose()
    {
        _mic.Changed -= OnMicChanged;
        _mic.Dispose();
    }
}
