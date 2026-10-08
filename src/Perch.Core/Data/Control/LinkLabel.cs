using System.Globalization;
using System.Text.RegularExpressions;

namespace Perch.Data.Control;

/// <summary>How a link's visible text relates to where it actually goes.</summary>
internal enum LinkLabelMatch
{
    /// <summary>The text is the address (give or take the scheme, <c>www.</c>, a trailing slash and case).</summary>
    Same,
    /// <summary>The text is something else ("the docs", a different page on the same site).</summary>
    Different,
    /// <summary>The text reads as an address on a <em>different</em> site — the phishing shape.</summary>
    OtherSite,
}

/// <summary>The outcome of <see cref="LinkLabel.Check"/>: the match, and for <see cref="LinkLabelMatch.OtherSite"/>
/// the site the text shows and the one the link goes to.</summary>
internal sealed record LinkLabelCheck(LinkLabelMatch Match, string? ShownHost = null, string? Host = null);

/// <summary>
/// Decides whether following a link needs a look first: a link whose text isn't its address opens a popup showing
/// both (<c>LinkText</c>). Feed posts and session text are untrusted, and "text says one site, link goes to
/// another" is the classic trick. UI-free and never throws.
/// </summary>
internal static partial class LinkLabel
{
    // A bare host: dot-separated labels, ending in an alphabetic TLD of two or more letters.
    [GeneratedRegex(@"^(?:[\p{L}\p{N}](?:[\p{L}\p{N}-]*[\p{L}\p{N}])?\.)+\p{L}{2,}$")]
    private static partial Regex HostRegex();

    public static LinkLabelCheck Check(string? label, string url)
    {
        try
        {
            if (string.Equals(Normalize(label), Normalize(url), StringComparison.OrdinalIgnoreCase))
                return new(LinkLabelMatch.Same);

            if (ShownHost(label) is { } shown && Uri.TryCreate(url, UriKind.Absolute, out var u) && u.IdnHost.Length > 0)
            {
                var host = StripWww(u.IdnHost);
                if (!string.Equals(Ascii(shown), host, StringComparison.OrdinalIgnoreCase))
                    return new(LinkLabelMatch.OtherSite, shown, StripWww(u.Host));
            }
            return new(LinkLabelMatch.Different);
        }
        catch
        {
            return new(LinkLabelMatch.Different);   // can't tell: show the popup rather than open blind
        }
    }

    // "https://www.Example.com/a/" → "example.com/a": the parts people leave off when they write an address.
    private static string Normalize(string? s)
    {
        s = (s ?? "").Trim();
        foreach (var scheme in (string[])["https://", "http://", "mailto:"])
            if (s.StartsWith(scheme, StringComparison.OrdinalIgnoreCase)) { s = s[scheme.Length..]; break; }
        s = StripWww(s).TrimEnd('/');
        try { s = Uri.UnescapeDataString(s); } catch { }
        return s;
    }

    // The site the text names, when it reads as an address ("github.com", "https://github.com/x"), else null.
    private static string? ShownHost(string? label)
    {
        var s = (label ?? "").Trim();
        int scheme = s.IndexOf("://", StringComparison.Ordinal);
        if (scheme >= 0) s = s[(scheme + 3)..];
        int end = s.IndexOfAny(['/', '?', '#', ':']);
        if (end >= 0) s = s[..end];
        s = StripWww(s.TrimEnd('.'));
        if (s.Length == 0 || !HostRegex().IsMatch(s)) return null;
        // "README.md", "Program.cs": a file name, not a site (some of these are real TLDs, but as link text they're
        // nearly always files). Such a link still gets the popup; it just isn't called another site.
        return FileExtensions.Contains(s[(s.LastIndexOf('.') + 1)..]) ? null : s;
    }

    private static readonly HashSet<string> FileExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        "md", "txt", "json", "yml", "yaml", "xml", "toml", "ini", "cfg", "lock", "log", "csv",
        "html", "htm", "css", "js", "jsx", "ts", "tsx", "mjs", "py", "rb", "rs", "go", "java", "kt", "swift",
        "cs", "csproj", "sln", "slnx", "fs", "vb", "c", "h", "cpp", "hpp", "sh", "ps1", "bat", "cmd",
        "png", "jpg", "jpeg", "gif", "svg", "webp", "pdf", "zip",
    };

    private static string StripWww(string s) =>
        s.StartsWith("www.", StringComparison.OrdinalIgnoreCase) ? s[4..] : s;

    private static string Ascii(string host)
    {
        try { return new IdnMapping().GetAscii(host); } catch { return host; }
    }
}
