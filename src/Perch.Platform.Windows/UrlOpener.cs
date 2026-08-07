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
                psi.ArgumentList.Add(b.IsGecko ? "-new-window" : "--new-window");
                psi.ArgumentList.Add(url);
                Process.Start(psi);
                return;
            }
            catch { /* fall through to the plain shell open below */ }
        }

        Open(url);
    }

    private readonly record struct Browser(string ExePath, bool IsGecko);

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

            bool isGecko = progId.Contains("Firefox", StringComparison.OrdinalIgnoreCase)
                || progId.Contains("Mozilla", StringComparison.OrdinalIgnoreCase)
                || Path.GetFileName(exe).Contains("firefox", StringComparison.OrdinalIgnoreCase);

            return new Browser(exe, isGecko);
        }
        catch { return null; }
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
