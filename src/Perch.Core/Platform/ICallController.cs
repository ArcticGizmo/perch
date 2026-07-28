namespace Perch.Platform;

/// <summary>
/// How far Perch has got with a call app's own control channel. The distinction matters to the UI: only
/// <see cref="Connected"/> may show in-app controls, while <see cref="Unavailable"/> and
/// <see cref="AwaitingApproval"/> are actionable states the user can fix, and saying so beats a button
/// that silently does nothing.
/// </summary>
public enum CallLinkState
{
    /// <summary>The integration is switched off in settings — nothing has been attempted, no socket
    /// opened, and no approval prompt will appear.</summary>
    Disabled,

    /// <summary>Enabled, but the app isn't reachable: it isn't running, or its local API is turned off in
    /// its own settings. Perch keeps retrying quietly.</summary>
    Unavailable,

    /// <summary>A connection attempt is in flight.</summary>
    Connecting,

    /// <summary>Connected, but the app hasn't granted access yet — for Teams, its in-app authorisation
    /// prompt is on screen waiting to be accepted. Nothing can be driven until it is.</summary>
    AwaitingApproval,

    /// <summary>Connected and authorised: state is live and commands will be honoured.</summary>
    Connected,
}

/// <summary>
/// The call app's own view of the call — authoritative in a way the microphone never is, because it knows
/// the difference between "in a meeting but muted" and "not in a meeting", which a capture stream cannot
/// distinguish. Immutable; a whole new snapshot is published on each change.
/// </summary>
/// <param name="IsInMeeting">Whether the user is in a call/meeting right now.</param>
/// <param name="IsMuted">Whether the user is muted <em>inside the app</em> — the state other participants
/// see, and the one the app's own UI shows.</param>
/// <param name="IsCameraOn">Whether the camera is on.</param>
/// <param name="IsHandRaised">Whether the user's hand is raised.</param>
/// <param name="IsRecording">Whether the meeting is being recorded.</param>
/// <param name="IsSharing">Whether the user is sharing their screen.</param>
/// <param name="CanToggleMute">Whether the app currently accepts a mute toggle (false when, for instance,
/// a hard mute is in force or there's no call).</param>
/// <param name="CanLeave">Whether the app currently accepts a leave-call command.</param>
public sealed record CallSnapshot(
    bool IsInMeeting,
    bool IsMuted,
    bool IsCameraOn = false,
    bool IsHandRaised = false,
    bool IsRecording = false,
    bool IsSharing = false,
    bool CanToggleMute = false,
    bool CanLeave = false);

/// <summary>
/// A recognised call app's own control channel — the product-specific layer that sits <em>on top of</em>
/// the app-agnostic <see cref="IMicrophoneMonitor"/>. Detection never depends on this: Perch names the app
/// holding the mic, offers to jump to its window, and can mute the capture device, all without any
/// integration. This interface only adds what a generic path cannot do — mute the user inside the app so
/// the app's UI and the other participants agree, report real meeting state, and leave the call.
///
/// Currently only Microsoft Teams has an implementation
/// (<see cref="Perch.Data.TeamsCallController"/>, over its local WebSocket API); every other app resolves
/// to <see cref="NullCallController"/> and the UI falls back to the generic affordances. New integrations
/// slot in behind the same shape without the detection or UI layers learning about them.
///
/// Event-driven like <see cref="IMediaController"/>: <see cref="Changed"/> fires when <see cref="State"/>
/// or <see cref="Current"/> moves, and may arrive on an arbitrary thread. Every member is best-effort.
/// </summary>
public interface ICallController : IDisposable
{
    /// <summary>How far the control channel has got. Only <see cref="CallLinkState.Connected"/> means the
    /// commands below will do anything.</summary>
    CallLinkState State { get; }

    /// <summary>The app's latest report of the call, or null when not connected / nothing reported yet.</summary>
    CallSnapshot? Current { get; }

    /// <summary>Raised (possibly off the UI thread) when <see cref="State"/> or <see cref="Current"/> changes.</summary>
    event Action? Changed;

    /// <summary>Begin trying to reach the app. Idempotent. May cause the app to show an authorisation
    /// prompt the very first time, which is why this is only ever called behind an explicit opt-in.</summary>
    void Start();

    /// <summary>Stop trying and drop the connection. <see cref="State"/> becomes
    /// <see cref="CallLinkState.Disabled"/>.</summary>
    void Stop();

    /// <summary>Toggle mute inside the app. No-op unless <see cref="State"/> is
    /// <see cref="CallLinkState.Connected"/>.</summary>
    void ToggleMute();

    /// <summary>Leave the current call. No-op unless connected.</summary>
    void LeaveCall();
}

/// <summary>The do-nothing controller used for every app Perch has no integration for, and on heads with
/// no implementation. Permanently <see cref="CallLinkState.Disabled"/>, so callers need no null checks and
/// the UI naturally shows only the generic affordances.</summary>
public sealed class NullCallController : ICallController
{
    public CallLinkState State => CallLinkState.Disabled;
    public CallSnapshot? Current => null;
    public event Action? Changed { add { } remove { } }
    public void Start() { }
    public void Stop() { }
    public void ToggleMute() { }
    public void LeaveCall() { }
    public void Dispose() { }
}
