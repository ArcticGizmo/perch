using System.Diagnostics;
using Perch.Platform;

namespace Perch.Platform.Mac;

/// <summary>
/// macOS <see cref="IUrlOpener"/>: shells out to <c>/usr/bin/open</c> (ships with every macOS install).
/// <see cref="Open"/> hands the URL to the default handler, reusing a running app. <see cref="OpenInNewWindow"/>
/// adds <c>-n</c>, which opens a new instance of the default app — the closest one-flag analogue to the
/// Windows "new window" path.
///
/// NOTE (Phase 3): written against documented behaviour but not yet verified on a Mac. A future refinement
/// is to resolve the default browser (LaunchServices) and pass its own new-window flag rather than <c>-n</c>,
/// mirroring the Windows implementation.
/// </summary>
public sealed class UrlOpener : IUrlOpener
{
    public void Open(string url) => Run(url, newInstance: false);

    public void OpenInNewWindow(string url) => Run(url, newInstance: true);

    private static void Run(string url, bool newInstance)
    {
        if (string.IsNullOrWhiteSpace(url)) return;
        try
        {
            var psi = new ProcessStartInfo("/usr/bin/open") { UseShellExecute = false, CreateNoWindow = true };
            if (newInstance) psi.ArgumentList.Add("-n");
            psi.ArgumentList.Add(url);
            using var _ = Process.Start(psi);
        }
        catch { /* best-effort — no handler, sandbox denial, etc. */ }
    }
}
