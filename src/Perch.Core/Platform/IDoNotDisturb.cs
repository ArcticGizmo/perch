namespace Perch.Platform;

/// <summary>
/// Whether the OS is currently in a "do not disturb" state — Windows Do Not Disturb / Focus Assist quiet
/// hours, macOS Focus, etc. Perch uses it to go quiet (collapse the friends region, hold off social toasts)
/// when the user has told the system they don't want to be bothered. Polled, not event-driven — read
/// <see cref="IsActive"/> on a timer.
/// </summary>
public interface IDoNotDisturb
{
    bool IsActive { get; }

    /// <summary>Whether this platform can switch Do Not Disturb on and off (<see cref="TrySetActive"/>). Gates
    /// the overlay header's DND button.</summary>
    bool CanToggle { get; }

    /// <summary>Turns Do Not Disturb on or off. Best-effort: returns false if the OS refused or the platform
    /// can't do it, and never throws. May block on a cross-process call, so keep it off the UI thread.</summary>
    bool TrySetActive(bool active);
}
