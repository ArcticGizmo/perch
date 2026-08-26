using System.Diagnostics;
using Microsoft.Win32;
using Perch.Platform;

namespace Perch.Platform.Windows;

/// <summary>
/// Windows <see cref="IUrlOpener"/>. <see cref="Open"/> is the familiar shell open (delegates to the
/// default browser/handler, reusing a running instance). <see cref="OpenInNewWindow"/> resolves the
/// default browser's executable from the per-user URL association and launches it with the browser's
/// "new window" switch, so the fresh window opens on the active virtual desktop rather than the OS
/// activating an existing window on whatever desktop it happens to live on.
/// </summary>
public sealed class UrlOpener : IUrlOpener
{
    public void Open(string url)
    {
        if (string.IsNullOrWhiteSpace(url)) return;
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
        catch { /* best-effort — no default handler, blocked, etc. */ }
    }

    public void OpenInNewWindow(string url)
    {
        if (string.IsNullOrWhiteSpace(url)) return;

        var browser = ResolveDefaultBrowser();
        if (browser is { } b)
        {
            try
            {
                // Chromium family (Chrome/Edge/Brave/Vivaldi/Opera) takes --new-window; Gecko (Firefox &
                // friends) takes -new-window. Launch the exe directly so a new top-level window is created
                // on the current desktop instead of the shell handing the URL to the existing process.
                var psi = new ProcessStartInfo(b.ExePath) { UseShellExecute = false };
                psi.ArgumentList.Add(b.Family == BrowserFamily.Gecko ? "-new-window" : "--new-window");
                psi.ArgumentList.Add(url);
                Process.Start(psi);
                return;
            }
            catch { /* fall through to the plain shell open below */ }
        }

        Open(url);
    }

    public void OpenPrivate(string url)
    {
        if (string.IsNullOrWhiteSpace(url)) return;

        var browser = ResolveDefaultBrowser();
        if (browser is { } b)
        {
            try
            {
                // The private-window switch differs per browser: Chromium uses --incognito, Edge --inprivate,
                // Opera --private, and Gecko -private-window. Each opens a fresh private window, so no extra
                // new-window flag is needed. Launch the exe directly (a shell open would hand off to the
                // running profile and ignore the flag).
                var psi = new ProcessStartInfo(b.ExePath) { UseShellExecute = false };
                psi.ArgumentList.Add(b.Family switch
                {
                    BrowserFamily.Gecko => "-private-window",
                    BrowserFamily.Edge => "--inprivate",
                    BrowserFamily.Opera => "--private",
                    _ => "--incognito",
                });
                psi.ArgumentList.Add(url);
                Process.Start(psi);
                return;
            }
            catch { /* fall through — best-effort, at least open a fresh normal window below */ }
        }

        // Couldn't resolve the browser or launch privately: a normal new window is the honest fallback.
        OpenInNewWindow(url);
    }

    private enum BrowserFamily { Chromium, Edge, Opera, Gecko }

    private readonly record struct Browser(string ExePath, BrowserFamily Family);

    /// <summary>
    /// Reads the default https handler's executable from the registry: the user's UrlAssociations choice
    /// gives a ProgId, whose <c>shell\open\command</c> holds the launch command we parse the exe out of.
    /// Returns null if anything is missing or unparsable, so the caller falls back to a shell open.
    /// </summary>
    private static Browser? ResolveDefaultBrowser()
    {
        try
        {
            string? progId;
            using (var choice = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\Shell\Associations\UrlAssociations\https\UserChoice"))
                progId = choice?.GetValue("ProgId") as string;

            if (string.IsNullOrWhiteSpace(progId)) return null;

            string? command;
            using (var cmd = Registry.ClassesRoot.OpenSubKey($@"{progId}\shell\open\command"))
                command = cmd?.GetValue(null) as string;

            if (string.IsNullOrWhiteSpace(command)) return null;

            string exe = ExtractExePath(command);
            if (exe.Length == 0 || !File.Exists(exe)) return null;

            return new Browser(exe, ClassifyBrowser(progId, Path.GetFileName(exe)));
        }
        catch { return null; }
    }

    /// <summary>Classifies the browser from its ProgId and exe filename so the family-specific new-window
    /// and private switches can be chosen. Anything unrecognised is treated as generic Chromium — the most
    /// common case, and its <c>--incognito</c>/<c>--new-window</c> flags are the widest-supported.</summary>
    private static BrowserFamily ClassifyBrowser(string progId, string exeName)
    {
        bool Match(string needle) =>
            progId.Contains(needle, StringComparison.OrdinalIgnoreCase)
            || exeName.Contains(needle, StringComparison.OrdinalIgnoreCase);

        if (Match("firefox") || Match("mozilla")) return BrowserFamily.Gecko;
        if (Match("edge") || Match("msedge")) return BrowserFamily.Edge;
        if (Match("opera")) return BrowserFamily.Opera;
        return BrowserFamily.Chromium;
    }

    /// <summary>Pulls the executable path out of a shell open command (a leading quoted path, else the
    /// first whitespace-delimited token).</summary>
    private static string ExtractExePath(string command)
    {
        command = command.Trim();
        if (command.StartsWith('"'))
        {
            int end = command.IndexOf('"', 1);
            return end > 1 ? command[1..end] : "";
        }
        int space = command.IndexOf(' ');
        return space > 0 ? command[..space] : command;
    }
}
