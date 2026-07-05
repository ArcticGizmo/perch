using Microsoft.Win32;
using Perch.Platform;

namespace Perch.Platform.Windows;

/// <summary>
/// Windows <see cref="ISessionLock"/>: tracks whether the session is locked so notifications can fire
/// externally when the user is away. Windows raises <see cref="SystemEvents.SessionSwitch"/> on lock
/// and unlock (and on RDP disconnect/connect); those transitions fold into a single
/// <see cref="IsLocked"/> flag. (Moved from the WinForms app's LockMonitor.)
/// </summary>
/// <remarks>
/// <see cref="SystemEvents"/> handlers fire on a dedicated system thread, so the flag is
/// <c>volatile</c> and the subscription must be released on <see cref="Dispose"/> to avoid leaking this
/// instance. The app can only start from an unlocked session, so the initial state is unlocked; we
/// never miss the first lock because the event covers every transition thereafter.
/// </remarks>
public sealed class SessionLock : ISessionLock
{
    private volatile bool _locked;
    private bool _disposed;

    public bool IsLocked => _locked;

    public SessionLock()
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
