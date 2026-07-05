namespace Perch.Data;

using Microsoft.Win32;

/// <summary>
/// Tracks whether the Windows session is currently locked, so notifications can fire externally when
/// the user is away from the keyboard. Windows raises <see cref="SystemEvents.SessionSwitch"/> on lock
/// and unlock (and on RDP disconnect/connect); we fold those transitions into a single
/// <see cref="IsLocked"/> flag.
/// </summary>
/// <remarks>
/// <see cref="SystemEvents"/> handlers are static and fire on a dedicated system thread, so the flag
/// is <c>volatile</c> and the subscription must be released on <see cref="Dispose"/> to avoid leaking
/// this instance. The app can only be started from an unlocked session, so the initial state is
/// unlocked; we never miss the first lock because the event covers every transition thereafter.
/// </remarks>
internal sealed class LockMonitor : IDisposable
{
    private volatile bool _locked;
    private bool _disposed;

    public bool IsLocked => _locked;

    public LockMonitor()
    {
        SystemEvents.SessionSwitch += OnSessionSwitch;
    }

    private void OnSessionSwitch(object sender, SessionSwitchEventArgs e)
    {
        switch (e.Reason)
        {
            // Workstation locked, or the remote session disconnected (which also leaves the
            // console locked) — treat both as "away".
            case SessionSwitchReason.SessionLock:
            case SessionSwitchReason.RemoteDisconnect:
                _locked = true;
                break;
            case SessionSwitchReason.SessionUnlock:
            case SessionSwitchReason.RemoteConnect:
                _locked = false;
                break;
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        SystemEvents.SessionSwitch -= OnSessionSwitch;
    }
}
