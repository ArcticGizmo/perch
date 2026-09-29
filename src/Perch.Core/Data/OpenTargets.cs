namespace Perch.Data;

/// <summary>
/// Decides what Perch may hand to the OS to "open". Links and file references arrive from places Perch didn't
/// write (transcripts, markdown, <c>gh</c> check URLs), and the OS shell will happily execute most of what it's
/// given: a <c>file:</c> or UNC target runs an executable, <c>search-ms:</c> / <c>ms-*:</c> launch protocol
/// handlers, and a string beginning with <c>-</c> passed to a browser exe becomes a command-line switch.
/// So every open goes through one of these checks (review fixes CP8, docs/review-fixes-plan.md). Pure; never
/// throws.
/// </summary>
public static class OpenTargets
{
    /// <summary>
    /// The normalised form of <paramref name="url"/> when it is safe to open in a browser or mail client — an
    /// absolute <c>http</c>/<c>https</c> URL with a host, or a <c>mailto:</c> — else null. The result always
    /// begins with its scheme, so it can never be read as a command-line switch.
    /// </summary>
    public static string? WebUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return null;
        // Uri.TryCreate also accepts a Windows path ("C:\x") or UNC ("\\h\s") as absolute — as file: URIs, which
        // the scheme check below rejects.
        if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri)) return null;
        return uri.Scheme switch
        {
            "http" or "https" when uri.Host.Length > 0 => uri.AbsoluteUri,
            "mailto" => uri.AbsoluteUri,
            _ => null,
        };
    }

    /// <summary>True for an http(s) URL — the only kind worth handing to a browser executable (a <c>mailto:</c>
    /// goes to the mail client through the plain shell open).</summary>
    public static bool IsHttp(string url) =>
        url.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
        || url.StartsWith("https://", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The local path a markdown link target names, for routing to the existence-gated file viewer — or null when
    /// it names something else. Accepts a relative path (<c>docs/plan.md#cp8</c>: the query/fragment is dropped and
    /// percent-escapes decoded), a drive-rooted path, or a <c>file:</c> URI to one. Rejects any other scheme and
    /// anything network-shaped (UNC, <c>//host</c>, <c>file://host/…</c>), which would reach out over SMB.
    /// </summary>
    public static string? LinkFilePath(string? target)
    {
        if (string.IsNullOrWhiteSpace(target)) return null;
        var t = target.Trim();
        if (IsNetworkShaped(t)) return null;

        if (Uri.TryCreate(t, UriKind.Absolute, out var uri))
        {
            // A drive path ("C:\x.md") parses as a file: URI too; either way LocalPath is the path.
            if (!uri.IsFile || uri.IsUnc) return null;
            return uri.LocalPath;
        }
        // Not absolute but scheme-shaped ("search-ms:x" can fail to parse as a Uri): never a file.
        if (HasScheme(t)) return null;

        int cut = t.IndexOfAny(['#', '?']);
        if (cut >= 0) t = t[..cut];
        if (t.Length == 0) return null;
        try { t = Uri.UnescapeDataString(t); } catch { /* keep the raw text */ }
        return IsNetworkShaped(t) ? null : t;
    }

    /// <summary>
    /// True when <paramref name="path"/> is a fully-qualified local path whose type the default handler only
    /// <em>views</em> (images, text, PDFs, office documents, media) — so a shell open can't run it. Everything
    /// else (executables, scripts, shortcuts, installers, HTML, anything unrecognised) is refused; callers reveal
    /// it in the file manager instead. Existence is the caller's check.
    /// </summary>
    public static bool IsViewerSafeFile(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || IsNetworkShaped(path)) return false;
        try
        {
            if (!Path.IsPathFullyQualified(path)) return false;
            // An NTFS alternate stream ("x.txt:payload") hides the real target behind the visible extension.
            if (path.IndexOf(':', OperatingSystem.IsWindows() ? 2 : 0) >= 0) return false;
            return ViewerSafeExtensions.Contains(Path.GetExtension(path));
        }
        catch { return false; }
    }

    // Deliberately short: a file type only goes here if its default handler displays it and never executes it.
    // HTML/SVG are out (they open in a browser with file: access); macro-capable office formats are out.
    private static readonly HashSet<string> ViewerSafeExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".png", ".jpg", ".jpeg", ".gif", ".bmp", ".webp", ".tif", ".tiff", ".ico",
        ".txt", ".md", ".markdown", ".log", ".json", ".jsonl", ".csv", ".tsv", ".xml", ".yaml", ".yml", ".toml",
        ".pdf", ".docx", ".xlsx", ".pptx",
        ".mp3", ".wav", ".m4a", ".mp4", ".mov", ".webm",
    };

    private static bool IsNetworkShaped(string s) => LocalPath.IsNetworkShaped(s);

    // "scheme:" per RFC 3986 (a letter, then letters/digits/+-.), longer than one char so "C:" stays a drive.
    private static bool HasScheme(string s)
    {
        int colon = s.IndexOf(':');
        if (colon < 2 || !char.IsAsciiLetter(s[0])) return false;
        for (int i = 1; i < colon; i++)
            if (!(char.IsAsciiLetterOrDigit(s[i]) || s[i] is '+' or '-' or '.')) return false;
        return true;
    }
}
