namespace Perch.Platform;

/// <summary>
/// Registers (and removes) Perch as a login item, so the OS starts the tray when the user logs in —
/// the <c>StartMode.OnLogin</c> half of the start-mode setting. Windows uses the per-user
/// <c>HKCU\…\CurrentVersion\Run</c> key; macOS writes a <c>~/Library/LaunchAgents</c> plist. Neither
/// needs elevation, and both are per-profile so a dev instance never fights the installed one.
///
/// Implementations are best-effort and must never throw: a locked registry or a read-only home just means
/// Perch doesn't start at login. <see cref="IsRegistered"/> reports the OS's view (which the user can
/// change behind our back in Task Manager / System Settings), so callers can re-assert it at startup.
/// </summary>
public interface ILoginItem
{
    /// <summary>Registers the running executable to launch at login. Idempotent — also refreshes the
    /// recorded path, which an app update or a move can leave stale.</summary>
    void Register();

    /// <summary>Removes the registration. No-op when there isn't one.</summary>
    void Unregister();

    /// <summary>True when a login registration for this profile currently exists.</summary>
    bool IsRegistered();
}
