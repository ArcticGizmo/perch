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
}
