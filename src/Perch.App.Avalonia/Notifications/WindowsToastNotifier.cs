using Avalonia.Threading;
using Microsoft.Toolkit.Uwp.Notifications;
using Perch.Platform;

namespace Perch.Avalonia.Notifications;

/// <summary>
/// Real Windows Action Center toasts via the UWP notifications compat shim. Works for an unpackaged
/// Win32 app: <see cref="ToastNotificationManagerCompat"/> auto-registers an AppUserModelId + a COM
/// activator on first use, so toasts appear in the Action Center (with history, and honouring Focus
/// Assist / Do-Not-Disturb) rather than as an in-app window. Session toasts carry the pid/project as
/// toast arguments so a click routes back through <see cref="SessionActivated"/> (marshalled to the UI
/// thread). The <see cref="Notifications.AvaloniaToastNotifier"/> remains the non-Windows fallback.
/// </summary>
internal sealed class WindowsToastNotifier : INotifier
{
    public event Action<string, string?>? SessionActivated;

    public WindowsToastNotifier()
    {
        // Subscribing stands up the COM activator so a toast click reaches this (already-running) tray
        // instance; the callback fires on a background thread, so it hops to the UI thread below.
        ToastNotificationManagerCompat.OnActivated += OnToastActivated;
    }

    public void Show(string title, string body, ToastLevel level, string? pid, string? project)
    {
        try
        {
            var builder = new ToastContentBuilder();
            if (!string.IsNullOrEmpty(pid))
            {
                builder.AddArgument("pid", pid);
                if (!string.IsNullOrEmpty(project)) builder.AddArgument("project", project);
            }
            builder.AddText(title).AddText(body);
            builder.Show();
        }
        catch { /* best-effort — a toast failure must never break the monitor callback */ }
    }

    private void OnToastActivated(ToastNotificationActivatedEventArgsCompat e)
    {
        try
        {
            var args = ToastArguments.Parse(e.Argument);
            if (!args.TryGetValue("pid", out var pid) || string.IsNullOrEmpty(pid)) return;
            args.TryGetValue("project", out var project);
            Dispatcher.UIThread.Post(() => SessionActivated?.Invoke(pid, project));
        }
        catch { }
    }
}
