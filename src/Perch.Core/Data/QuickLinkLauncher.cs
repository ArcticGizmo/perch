using System.Diagnostics;
using Perch.Platform;

namespace Perch.Data;

/// <summary>Whether a quick-link click opened something; <see cref="Missing"/> lets the app say so instead of
/// failing silently (a file link whose file has gone).</summary>
internal enum QuickLinkOpenResult { Opened, Missing, Nothing }

/// <summary>
/// Resolves a <see cref="QuickLink"/>'s icon and opens it, per its <see cref="QuickLink.Kind"/>, on the platform
/// seams: <see cref="IWindowActivator"/> to focus a running app, <see cref="IAppIconProvider"/> for icon files
/// and Store-app launches, <see cref="IFileRevealer"/> for files and folders, and <see cref="IUrlOpener"/> for web
/// links. Icons come back as PNG file paths the UI can load directly; process enumeration and plain-exe
/// launching use the cross-platform <see cref="Process"/> APIs, so only the genuinely shell-specific bits live
/// behind the seam. A web link's icon is a favicon, fetched separately by <see cref="QuickLinkFavicons"/>.
/// </summary>
internal sealed class QuickLinkLauncher
{
    private readonly IWindowActivator _activator;
    private readonly IAppIconProvider _icons;
    private readonly IFileRevealer _files;
    private readonly IUrlOpener _urls;

    public QuickLinkLauncher(IWindowActivator activator, IAppIconProvider icons, IFileRevealer files, IUrlOpener urls)
    {
        _activator = activator;
        _icons = icons;
        _files = files;
        _urls = urls;
    }

    /// <summary>The PNG file path for an app link's icon at the given pixel size, or null (draw initials). Null for
    /// a file link, which draws a <see cref="QuickLinkGlyphs"/> mark, and for a web link, whose favicon comes from
    /// <see cref="QuickLinkFavicons"/>.</summary>
    public string? IconFile(QuickLink link, int size) =>
        link.Kind == QuickLinkKind.App ? _icons.GetIconFile(link.Name, link.ExePath, link.ResolveExe(), size) : null;

    /// <summary>Opens the link for a left or middle click. Never throws.</summary>
    public QuickLinkOpenResult Open(QuickLink link, bool middleClick)
    {
        try
        {
            var action = QuickLinkRouting.Resolve(link, middleClick);
            switch (action.Kind)
            {
                case QuickLinkActionKind.FocusOrLaunchApp:
                    LaunchOrFocus(link);
                    return QuickLinkOpenResult.Opened;
                case QuickLinkActionKind.OpenFolder:
                    // Revealing a folder opens it directly.
                    _files.RevealInFileManager(action.Target!);
                    return QuickLinkOpenResult.Opened;
                case QuickLinkActionKind.OpenFile:
                    if (!File.Exists(action.Target)) return QuickLinkOpenResult.Missing;
                    // A type its default handler only views opens; a program or script is revealed instead, so a
                    // file link can never run code (that's what an App link is for).
                    _files.OpenWithDefault(action.Target!);
                    return QuickLinkOpenResult.Opened;
                case QuickLinkActionKind.OpenUrl:
                    _urls.Open(action.Target!);
                    return QuickLinkOpenResult.Opened;
                case QuickLinkActionKind.OpenUrlInNewWindow:
                    _urls.OpenInNewWindow(action.Target!);
                    return QuickLinkOpenResult.Opened;
                default:
                    return QuickLinkOpenResult.Nothing;
            }
        }
        catch
        {
            return QuickLinkOpenResult.Nothing;
        }
    }

    // Focuses the app's existing window if it's running, otherwise launches it. An explicit path is launched
    // directly; with no path we launch by shell identity (reliable for Store/MSIX apps that have no real exe to
    // point at), falling back to a discovered well-known exe.
    private void LaunchOrFocus(QuickLink link)
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

    private static void StartFile(string path) =>
        Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
}
