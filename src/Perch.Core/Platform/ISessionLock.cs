namespace Perch.Platform;

/// <summary>
/// Tracks whether the desktop session is currently locked, so alerts can be pushed externally when the
/// user is away from the keyboard. Windows folds SessionSwitch (lock/unlock, RDP connect/disconnect)
/// into the flag; other platforms use their own idle/lock signal. Dispose to release the subscription.
/// </summary>
public interface ISessionLock : IDisposable
{
    bool IsLocked { get; }
}
