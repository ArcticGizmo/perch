using Perch.Platform;

namespace Perch.Platform.Mac;

/// <summary>
/// macOS <see cref="ISessionLock"/>. Phase 3: track screen-lock state via
/// <c>CGSessionCopyCurrentDictionary</c> (<c>kCGSSessionOnConsoleKey</c>) or the
/// <c>com.apple.screenIsLocked</c>/<c>Unlocked</c> distributed notifications. Stub for now: always
/// reports unlocked (external AFK pushes simply won't be gated on lock state until Phase 3).
/// </summary>
public sealed class SessionLock : ISessionLock
{
    public bool IsLocked => false;
    public void Dispose() { }
}
