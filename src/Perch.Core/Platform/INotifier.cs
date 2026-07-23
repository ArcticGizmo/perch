namespace Perch.Platform;

/// <summary>Severity of a desktop notification, driving the toast's accent (and, on platforms with a
/// native toast, its icon).</summary>
public enum ToastLevel
{
    Info,
    Warning,
    Error,
}

/// <summary>
/// The desktop-notification seam: shows a local toast for a session event (or a plain info toast for the
/// update / plugin / ntfy-test flows). UI-toolkit-bound — the Avalonia head implements it with an
/// owner-drawn toast window; a future per-OS native-toast implementation can slot in behind the same
/// interface. Toolkit-neutral <see cref="Data"/> code (the notification dispatcher) depends only on this.
/// </summary>
public interface INotifier
{
    /// <summary>
    /// Shows a toast. When <paramref name="pid"/> is non-null the toast is tied to a session, so clicking
    /// it raises <see cref="SessionActivated"/> with that pid (and <paramref name="project"/>, used to
    /// disambiguate a terminal host that owns several windows). A null pid is a plain info toast — clicking
    /// it just dismisses. Called on the UI thread; never throws.
    /// </summary>
    void Show(string title, string body, ToastLevel level, string? pid, string? project);

    /// <summary>
    /// Shows the actionable "update available" toast. Clicking it raises <see cref="UpdateActivated"/> so
    /// the click starts the update — exactly what the update button does. Called on the UI thread; never
    /// throws.
    /// </summary>
    void ShowUpdate(string title, string body);

    /// <summary>Raised (on the UI thread) when a session toast is clicked, carrying its pid and project so
    /// the owner can focus that terminal and acknowledge the alert.</summary>
    event Action<string, string?>? SessionActivated;

    /// <summary>Raised (on the UI thread) when the "update available" toast is clicked, so the owner can
    /// start the update — the same action as the update button.</summary>
    event Action? UpdateActivated;
}
