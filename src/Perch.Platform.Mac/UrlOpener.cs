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

    // NOTE (Phase 3): /usr/bin/open has no generic "private window" flag — that needs the default browser's
    // bundle id plus its own --incognito/--inprivate/-private-window arg via `open -na <app> --args …`,
    // which is browser-specific and unverified here. Until that's resolved, fall back to a fresh window so
    // sign-in still works; it just won't force a private session on macOS yet.
    public void OpenPrivate(string url) => OpenInNewWindow(url);

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
