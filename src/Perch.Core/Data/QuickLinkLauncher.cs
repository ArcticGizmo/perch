using System.Diagnostics;
using Perch.Platform;

namespace Perch.Data;

/// <summary>
/// Resolves a <see cref="QuickLink"/>'s icon and launches/focuses its app — the toolkit-neutral core of
/// what the WinForms overlay's quick-links strip carried inline, now sitting on the platform seams
/// (<see cref="IWindowActivator"/> to focus a running window, <see cref="IAppIconProvider"/> for the
/// icon file and Store-app launch). Icons come back as PNG file paths the UI can load directly; process
/// enumeration and plain-exe launching use the cross-platform <see cref="Process"/> APIs, so only the
/// genuinely shell-specific bits live behind the seam.
/// </summary>
internal sealed class QuickLinkLauncher
{
    private readonly IWindowActivator _activator;
    private readonly IAppIconProvider _icons;

    public QuickLinkLauncher(IWindowActivator activator, IAppIconProvider icons)
    {
        _activator = activator;
        _icons = icons;
    }

    /// <summary>The PNG file path for a link's icon at the given pixel size, or null (draw initials).</summary>
    public string? IconFile(QuickLink link, int size)
        => _icons.GetIconFile(link.Name, link.ExePath, link.ResolveExe(), size);

    /// <summary>
    /// Focuses the app's existing window if it's running, otherwise launches it. An explicit path is
    /// launched directly; with no path we launch by shell identity (reliable for Store/MSIX apps that
    /// have no real exe to point at), falling back to a discovered well-known exe.
    /// </summary>
    public void LaunchOrFocus(QuickLink link)
    {
        try
        {
            foreach (var p in Process.GetProcessesByName(link.ProcessName()))
            {
                if (p.MainWindowHandle != IntPtr.Zero)
                {
                    _activator.FocusProcessMainWindow(p.Id);
                    return;
                }
            }

            if (!string.IsNullOrWhiteSpace(link.ExePath)) { StartFile(link.ExePath); return; }

            if (_icons.TryLaunchByName(link.Name)) return;

            var exe = link.ResolveExe();
            if (exe != null) StartFile(exe);
        }
        catch { /* best-effort; a failed launch is silent, same as the WinForms original */ }
    }

    private static void StartFile(string path) =>
        Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
}
