namespace Perch.Platform;

/// <summary>
/// Opens a URL (or a local file/path — anything the OS shell can resolve) in the user's default handler.
/// Two modes:
/// <list type="bullet">
///   <item><see cref="Open"/> — the plain shell open. The OS reuses whatever browser instance is already
///   running, which activates its last-focused window. On Windows that can yank the user to another
///   virtual desktop if that window lives there.</item>
///   <item><see cref="OpenInNewWindow"/> — force a fresh browser window, which the OS creates on the
///   <em>current</em> virtual desktop. This is the middle-click affordance on link menu options.</item>
/// </list>
/// Both are best-effort and never throw (no default browser, a malformed URL, etc. just no-op).
/// </summary>
public interface IUrlOpener
{
    /// <summary>Opens <paramref name="url"/> in the default handler, reusing an existing window if any.</summary>
    void Open(string url);

    /// <summary>
    /// Opens <paramref name="url"/> in a brand-new default-browser window so it lands on the current
    /// virtual desktop instead of dragging focus to an existing window elsewhere. Falls back to
    /// <see cref="Open"/> when the default browser can't be resolved or the launch fails.
    /// </summary>
    void OpenInNewWindow(string url);
}
