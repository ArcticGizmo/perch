using Avalonia.Threading;
using Perch.Data;
using Perch.Platform;

namespace Perch.Avalonia.Services;

/// <summary>
/// Bridges the two halves of the microphone feature to the overlay: the app-agnostic
/// <see cref="IMicrophoneMonitor"/> (who holds the mic) and a recognised call app's optional
/// <see cref="ICallController"/> (real meeting state and in-app mute). Both raise their change events off
/// the UI thread, so each is marshalled with the <c>Dispatcher.UIThread.Post</c> idiom the media and metrics
/// hosts use.
///
/// The two are deliberately independent: the mic monitor runs whenever the strip is enabled, while the call
/// link is a separate opt-in that can be off, unreachable, or belong to an app that isn't the one currently
/// holding the mic. Which of them a mute click acts on is decided in exactly one place —
/// <see cref="MicApps.CallLinkApplies"/>, the same helper the overlay uses to decide what to draw — so the
/// button can never mean something other than what the strip says.
/// </summary>
internal sealed class MicMonitorHost : IDisposable
{
    private readonly IMicrophoneMonitor _mic;
    private readonly ICallController _call;
    private readonly Action<MicSnapshot?> _onMic;
    private readonly Action<CallSnapshot?, CallLinkState> _onCall;
    private bool _started;
    private bool _callStarted;

    public MicMonitorHost(
        IMicrophoneMonitor mic,
        ICallController call,
        Action<MicSnapshot?> onMic,
        Action<CallSnapshot?, CallLinkState> onCall)
    {
        _mic = mic;
        _call = call;
        _onMic = onMic;
        _onCall = onCall;
        _mic.Changed += OnMicChanged;
        _call.Changed += OnCallChanged;
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

    /// <summary>Stops watching and clears the strip. Leaves the call link alone — it has its own switch.</summary>
    public void Stop()
    {
        if (!_started) return;
        _started = false;
        _mic.Stop();
        _onMic(null);
    }

    /// <summary>Starts or stops the recognised call app's control channel. Separate from
    /// <see cref="Start"/> because connecting can make the app show an authorisation prompt, so it only ever
    /// happens on its own explicit opt-in.</summary>
    public void SetCallControlsEnabled(bool enabled)
    {
        if (_callStarted == enabled) return;
        _callStarted = enabled;
        if (enabled) _call.Start();
        else _call.Stop();
        PushCall();
    }

    /// <summary>
    /// Toggles mute for whatever is holding the microphone. Prefers the call app's own mute — that's the one
    /// the app's UI and the other participants see — and falls back to muting the capture device, which
    /// works for any app but which the app itself knows nothing about (the strip's tooltip says so).
    /// </summary>
    public void ToggleMute()
    {
        if (MicApps.CallLinkApplies(_mic.Current, _call.Current, _call.State))
        {
            _call.ToggleMute();
            return;
        }
        _mic.SetDeviceMuted(!(_mic.Current?.DeviceMuted ?? false));
    }

    private void OnMicChanged() => PushMic();
    private void OnCallChanged() => PushCall();

    private void PushMic()
    {
        var snapshot = _mic.Current;
        Post(() => _onMic(snapshot));
    }

    private void PushCall()
    {
        var snapshot = _call.Current;
        var state = _call.State;
        Post(() => _onCall(snapshot, state));
    }

    private static void Post(Action action)
    {
        if (Dispatcher.UIThread.CheckAccess()) action();
        else Dispatcher.UIThread.Post(action);
    }

    public void Dispose()
    {
        _mic.Changed -= OnMicChanged;
        _call.Changed -= OnCallChanged;
        _mic.Dispose();
        _call.Dispose();
    }
}
