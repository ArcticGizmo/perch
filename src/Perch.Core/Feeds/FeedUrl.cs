using System.Net;
using System.Net.Sockets;

namespace Perch.Feeds;

/// <summary>
/// The one gate every feed-supplied URL passes through — entry links, the site link, icon/logo, content links,
/// image stubs, redirect hops (docs/feeds-plan.md §3.4.3). Pure; never throws.
///
/// <para><see cref="Perch.Data.OpenTargets.WebUrl"/> (CP8) still guards the final shell open; this is the earlier, stricter
/// layer, so a feed can't even get an unsafe URL into the model or the rendered Markdown.</para>
/// </summary>
internal static class FeedUrl
{
    public const int MaxLength = 2048;

    /// <summary>
    /// The absolute URL <paramref name="raw"/> names, resolved against <paramref name="baseUri"/>, when it's safe
    /// to keep: <c>http</c>/<c>https</c> with a host and no userinfo (or <c>mailto:</c> when
    /// <paramref name="allowMailto"/>). Null for anything else — another scheme, a relative URL with no web base,
    /// a backslash (browsers read one as a slash; real URLs never carry one), over-long input.
    /// </summary>
    public static Uri? Safe(string? raw, Uri? baseUri, bool allowMailto = false)
    {
        if (string.IsNullOrEmpty(raw)) return null;

        // The WHATWG URL parser deletes tabs/newlines anywhere and trims C0 controls + space at the ends, which is
        // how "java\tscript:" slips past naive checks. Do the same before deciding anything.
        var s = raw.Replace("\t", "").Replace("\n", "").Replace("\r", "").Trim(TrimChars);
        if (s.Length == 0 || s.Length > MaxLength || s.Contains('\\')) return null;

        Uri? uri;
        if (HasScheme(s))
        {
            if (!Uri.TryCreate(s, UriKind.Absolute, out uri)) return null;
        }
        else
        {
            if (baseUri is null || !baseUri.IsAbsoluteUri || !IsWeb(baseUri)) return null;
            if (!Uri.TryCreate(baseUri, s, out uri)) return null;
        }

        if (uri.Scheme == Uri.UriSchemeMailto) return allowMailto ? uri : null;
        if (!IsWeb(uri) || uri.Host.Length == 0 || uri.UserInfo.Length > 0) return null;
        return uri;
    }

    /// <summary><see cref="Safe"/> as a string (its <see cref="Uri.AbsoluteUri"/>), for model fields.</summary>
    public static string? SafeString(string? raw, Uri? baseUri, bool allowMailto = false) =>
        Safe(raw, baseUri, allowMailto)?.AbsoluteUri;

    /// <summary>The host as it should be shown: punycode (so a homograph like Cyrillic "аpple.com" reads as
    /// <c>xn--…</c>), lower-case, without a leading <c>www.</c>.</summary>
    public static string DisplayHost(Uri uri)
    {
        string host;
        try { host = uri.IdnHost; } catch { host = uri.Host; }
        host = host.ToLowerInvariant();
        return host.StartsWith("www.", StringComparison.Ordinal) ? host[4..] : host;
    }

    /// <summary>
    /// True for an address a feed-caused request may connect to: not loopback, private (RFC 1918 / ULA), shared
    /// (CGNAT), link-local (incl. the cloud metadata address), unspecified, multicast, broadcast or a
    /// documentation/benchmark range; IPv4-mapped and NAT64 IPv6 are judged by the IPv4 inside. The F1 fetcher
    /// checks this at connect time (after DNS), so rebinding can't get round it.
    /// </summary>
    public static bool IsPublicAddress(IPAddress ip)
    {
        if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();

        if (ip.AddressFamily == AddressFamily.InterNetwork)
        {
            var b = ip.GetAddressBytes();
            return !(b[0] == 0                                   // 0.0.0.0/8 "this network"
                  || b[0] == 10                                  // 10/8
                  || b[0] == 127                                 // loopback
                  || (b[0] == 100 && b[1] >= 64 && b[1] < 128)   // 100.64/10 CGNAT
                  || (b[0] == 169 && b[1] == 254)                // link-local + metadata
                  || (b[0] == 172 && b[1] >= 16 && b[1] < 32)    // 172.16/12
                  || (b[0] == 192 && b[1] == 0 && b[2] == 0)     // 192.0.0/24 protocol assignments
                  || (b[0] == 192 && b[1] == 0 && b[2] == 2)     // TEST-NET-1
                  || (b[0] == 192 && b[1] == 168)                // 192.168/16
                  || (b[0] == 198 && (b[1] == 18 || b[1] == 19)) // benchmarking
                  || (b[0] == 198 && b[1] == 51 && b[2] == 100)  // TEST-NET-2
                  || (b[0] == 203 && b[1] == 0 && b[2] == 113)   // TEST-NET-3
                  || b[0] >= 224);                               // multicast, reserved, broadcast
        }

        if (ip.AddressFamily == AddressFamily.InterNetworkV6)
        {
            if (ip.Equals(IPAddress.IPv6Any) || ip.Equals(IPAddress.IPv6Loopback)) return false;
            if (ip.IsIPv6LinkLocal || ip.IsIPv6SiteLocal || ip.IsIPv6Multicast) return false;
            var b = ip.GetAddressBytes();
            if ((b[0] & 0xFE) == 0xFC) return false;                         // fc00::/7 unique local
            if (b[0] == 0x20 && b[1] == 0x01 && b[2] == 0x0D && b[3] == 0xB8) return false;   // 2001:db8::/32 docs
            // 64:ff9b::/96 NAT64: judge the embedded IPv4.
            if (b[0] == 0 && b[1] == 0x64 && b[2] == 0xFF && b[3] == 0x9B && b.AsSpan(4, 8).IndexOfAnyExcept((byte)0) < 0)
                return IsPublicAddress(new IPAddress(b.AsSpan(12, 4)));
            return true;
        }

        return false;
    }

    private static readonly char[] TrimChars = Enumerable.Range(0, 0x21).Select(i => (char)i).ToArray();

    private static bool IsWeb(Uri u) => u.Scheme == Uri.UriSchemeHttp || u.Scheme == Uri.UriSchemeHttps;

    // "scheme:" per RFC 3986 — a letter, then letters/digits/+-. — before any '/', '?' or '#'. Requires two or
    // more scheme chars so a Windows drive ("C:") never counts as one (it then fails the web-base resolve or the
    // scheme check anyway).
    private static bool HasScheme(string s)
    {
        int colon = s.IndexOf(':');
        if (colon < 2 || !char.IsAsciiLetter(s[0])) return false;
        for (int i = 1; i < colon; i++)
            if (!(char.IsAsciiLetterOrDigit(s[i]) || s[i] is '+' or '-' or '.')) return false;
        return true;
    }
}
