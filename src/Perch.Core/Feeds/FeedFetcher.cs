using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using Perch.Data;

namespace Perch.Feeds;

internal enum FeedFetchStatus { Ok, NotModified, Error }

/// <summary>The outcome of one feed fetch. <see cref="FinalUrl"/> is where the document actually came from (after
/// redirects) — the base its relative links resolve against. <see cref="IsPrivate"/> records that the user's own
/// subscription URL points into a private network (so its same-host icon may be fetched too).</summary>
internal sealed record FeedFetchResult(
    FeedFetchStatus Status,
    FeedDoc? Doc = null,
    string? Error = null,
    string? ETag = null,
    DateTimeOffset? LastModified = null,
    TimeSpan? RetryAfter = null,
    Uri? FinalUrl = null,
    bool IsPrivate = false);

/// <summary>Thrown by the fenced connect when a feed-caused request would reach a non-public address.</summary>
internal sealed class FeedFenceException(string host)
    : HttpRequestException($"Refused: {host} points at a private network address");

/// <summary>
/// Fetches feeds and their icons (docs/feeds-plan.md §3.2, §3.4.3). Never throws: every failure is a
/// <see cref="FeedFetchStatus.Error"/> with a short user-facing reason.
///
/// <para><b>Two pipelines.</b> Requests a feed <em>causes</em> (its icon/logo), and the user's subscription when
/// it resolves to public addresses, go through the <b>fenced</b> handler. Its <c>ConnectCallback</c> resolves
/// the host itself and refuses to connect if any address is loopback/private/link-local/etc.
/// (<see cref="FeedUrl.IsPublicAddress"/>). The check happens at connect time, after DNS, so rebinding can't slip
/// past it. A subscription that is itself on a private network (a self-hosted feed — legitimate) uses the open
/// handler, and so may its same-host icon; nothing else may.</para>
///
/// <para><b>Redirects</b> are followed by hand, at most <see cref="MaxRedirects"/>, each hop re-vetted by
/// <see cref="FeedUrl.Safe"/> — and a public feed stays on the fenced handler for every hop, so it can't bounce
/// Perch into the LAN. <b>Bodies</b> are capped after decompression (<see cref="FeedParser.MaxBytes"/>, 256 KB for
/// icons). Server-supplied text (reason phrases, validators) never reaches the UI unvetted.</para>
///
/// <para>Behind a system proxy the callback connects to the proxy, not the target, so the fence can only check
/// the target's own resolution (best effort — the proxy's network is the proxy's business).</para>
/// </summary>
internal sealed class FeedFetcher : IDisposable
{
    public const int MaxRedirects = 5;
    public const int MaxIconBytes = 256 * 1024;
    private static readonly TimeSpan MaxRetryAfter = TimeSpan.FromHours(24);

    private readonly HttpClient _open;
    private readonly HttpClient _fenced;
    private readonly Func<string, CancellationToken, Task<IPAddress[]>> _resolve;

    /// <summary>Production: real handlers with system DNS.</summary>
    public FeedFetcher() : this(null, null, null) { }

    /// <summary>Tests inject handlers (no network) and a resolver for the public/private decision.</summary>
    internal FeedFetcher(HttpMessageHandler? open, HttpMessageHandler? fenced,
        Func<string, CancellationToken, Task<IPAddress[]>>? resolve)
    {
        _resolve = resolve ?? ((host, ct) => Dns.GetHostAddressesAsync(host, ct));
        _open = NewClient(open ?? NewHandler(fenced: false));
        _fenced = NewClient(fenced ?? NewHandler(fenced: true));
    }

    private HttpClient NewClient(HttpMessageHandler handler)
    {
        var c = new HttpClient(handler, disposeHandler: true) { Timeout = TimeSpan.FromSeconds(20) };
        c.DefaultRequestHeaders.UserAgent.ParseAdd($"Perch/{SafeVersion()} (+{AppInfo.RepoUrl})");
        return c;
    }

    private static string SafeVersion()
    {
        var v = AppInfo.Version;
        return v.All(ch => char.IsAsciiLetterOrDigit(ch) || ch is '.' or '-' or '+') && v.Length is > 0 and < 40 ? v : "0";
    }

    private SocketsHttpHandler NewHandler(bool fenced)
    {
        var h = new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            AutomaticDecompression = DecompressionMethods.All,
            ConnectTimeout = TimeSpan.FromSeconds(10),
            MaxResponseHeadersLength = 64,   // KB
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        };
        if (fenced) h.ConnectCallback = (ctx, ct) => FencedConnectAsync(ctx, ct, _resolve);
        return h;
    }

    // The SSRF fence. Resolves the target itself and refuses if ANY address is non-public (a mixed answer is how
    // rebinding hides a private address), then connects only to the vetted addresses.
    internal static async ValueTask<Stream> FencedConnectAsync(SocketsHttpConnectionContext ctx, CancellationToken ct,
        Func<string, CancellationToken, Task<IPAddress[]>> resolve)
    {
        var endpoint = ctx.DnsEndPoint;
        var target = ctx.InitialRequestMessage.RequestUri;
        bool viaProxy = target is not null && !string.Equals(target.IdnHost, endpoint.Host, StringComparison.OrdinalIgnoreCase);

        if (viaProxy)
        {
            // Connecting to the proxy: vet the real target as far as local DNS can see it, then let the proxy go.
            IPAddress[] targetAddrs;
            try { targetAddrs = await ResolveAsync(target!.IdnHost, resolve, ct); }
            catch (Exception e) when (e is not OperationCanceledException) { targetAddrs = []; }
            if (targetAddrs.Any(a => !FeedUrl.IsPublicAddress(a))) throw new FeedFenceException(target!.IdnHost);
            return await ConnectAsync(await ResolveAsync(endpoint.Host, resolve, ct), endpoint.Port, ct);
        }

        var addrs = await ResolveAsync(endpoint.Host, resolve, ct);
        if (addrs.Length == 0) throw new HttpRequestException($"Couldn't find {endpoint.Host}");
        if (addrs.Any(a => !FeedUrl.IsPublicAddress(a))) throw new FeedFenceException(endpoint.Host);
        return await ConnectAsync(addrs, endpoint.Port, ct);
    }

    private static async Task<IPAddress[]> ResolveAsync(string host, Func<string, CancellationToken, Task<IPAddress[]>> resolve,
        CancellationToken ct) =>
        IPAddress.TryParse(host.Trim('[', ']'), out var literal) ? [literal] : await resolve(host, ct);

    private static async ValueTask<Stream> ConnectAsync(IPAddress[] addrs, int port, CancellationToken ct)
    {
        var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        try
        {
            await socket.ConnectAsync(addrs, port, ct);
            return new NetworkStream(socket, ownsSocket: true);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Whether the user's subscription URL points into a private network: every address its host resolves to is
    /// non-public. A resolution failure counts as public (the fenced fetch then reports the real error).
    /// </summary>
    internal async Task<bool> IsPrivateHostAsync(Uri url, CancellationToken ct)
    {
        try
        {
            var addrs = await ResolveAsync(url.IdnHost, _resolve, ct);
            return addrs.Length > 0 && addrs.All(a => !FeedUrl.IsPublicAddress(a));
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            return false;
        }
    }

    /// <summary>Fetches and parses a subscription, conditionally (ETag / Last-Modified).</summary>
    public async Task<FeedFetchResult> FetchFeedAsync(Uri url, string? etag, DateTimeOffset? lastModified,
        DateTime nowUtc, CancellationToken ct)
    {
        if (FeedUrl.Safe(url.OriginalString, null) is not { } start)
            return new(FeedFetchStatus.Error, Error: "Only http and https feed addresses are supported");

        bool isPrivate = await IsPrivateHostAsync(start, ct);
        var client = isPrivate ? _open : _fenced;

        try
        {
            var (resp, final, err) = await SendFollowingRedirectsAsync(client, start, etag, lastModified, ct);
            if (resp is null) return new(FeedFetchStatus.Error, Error: err, IsPrivate: isPrivate);
            using (resp)
            {
                if (resp.StatusCode == HttpStatusCode.NotModified)
                    return new(FeedFetchStatus.NotModified, ETag: etag, LastModified: lastModified, FinalUrl: final, IsPrivate: isPrivate);

                if (!resp.IsSuccessStatusCode)
                    return new(FeedFetchStatus.Error, Error: StatusText(resp.StatusCode), RetryAfter: RetryAfter(resp, nowUtc),
                        FinalUrl: final, IsPrivate: isPrivate);

                var body = await ReadCappedAsync(resp, FeedParser.MaxBytes, ct);
                if (body is null) return new(FeedFetchStatus.Error, Error: "Feed is too large", IsPrivate: isPrivate);

                var parsed = FeedParser.Parse(new MemoryStream(body), final, nowUtc);
                if (parsed.Doc is null) return new(FeedFetchStatus.Error, Error: parsed.Error, FinalUrl: final, IsPrivate: isPrivate);

                return new(FeedFetchStatus.Ok, parsed.Doc,
                    ETag: resp.Headers.ETag?.ToString() is { Length: <= 256 } tag ? tag : null,
                    LastModified: resp.Content.Headers.LastModified,
                    FinalUrl: final, IsPrivate: isPrivate);
            }
        }
        catch (Exception e)
        {
            return new(FeedFetchStatus.Error, Error: Describe(e, ct), IsPrivate: isPrivate);
        }
    }

    /// <summary>
    /// Fetches an icon/logo the feed named. Always fenced, except that a private subscription may fetch an icon
    /// from its own host (<paramref name="privateHost"/>). Returns the bytes only if <see cref="FeedIcon"/>
    /// accepts them; null on any failure.
    /// </summary>
    public async Task<byte[]?> FetchIconAsync(Uri iconUrl, string? privateHost, CancellationToken ct)
    {
        if (FeedUrl.Safe(iconUrl.OriginalString, null) is not { } start) return null;
        var client = privateHost is not null && string.Equals(start.IdnHost, privateHost, StringComparison.OrdinalIgnoreCase)
            ? _open : _fenced;
        try
        {
            var (resp, _, _) = await SendFollowingRedirectsAsync(client, start, null, null, ct,
                sameHostOnly: client == _open ? privateHost : null);
            if (resp is null) return null;
            using (resp)
            {
                if (!resp.IsSuccessStatusCode) return null;
                var bytes = await ReadCappedAsync(resp, MaxIconBytes, ct);
                return bytes is not null && FeedIcon.Validate(bytes) is not null ? bytes : null;
            }
        }
        catch
        {
            return null;
        }
    }

    // GET with manual redirects. Every hop is re-vetted; the open client may only stay on the private host.
    private static async Task<(HttpResponseMessage? Resp, Uri Final, string? Error)> SendFollowingRedirectsAsync(
        HttpClient client, Uri start, string? etag, DateTimeOffset? lastModified, CancellationToken ct,
        string? sameHostOnly = null)
    {
        var url = start;
        for (int hop = 0; ; hop++)
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.Accept.ParseAdd("application/atom+xml, application/rss+xml, application/xml;q=0.9, text/xml;q=0.8, */*;q=0.1");
            if (etag is not null && EntityTagHeaderValue.TryParse(etag, out var tag)) req.Headers.IfNoneMatch.Add(tag);
            if (lastModified is not null) req.Headers.IfModifiedSince = lastModified;

            var resp = await client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
            if ((int)resp.StatusCode is not (301 or 302 or 303 or 307 or 308)) return (resp, url, null);

            var location = resp.Headers.Location;
            resp.Dispose();
            if (hop >= MaxRedirects) return (null, url, "Too many redirects");
            var next = location is null ? null
                : FeedUrl.Safe(location.IsAbsoluteUri ? location.AbsoluteUri : location.OriginalString, url);
            if (next is null) return (null, url, "Redirected to an unsupported address");
            if (sameHostOnly is not null && !string.Equals(next.IdnHost, sameHostOnly, StringComparison.OrdinalIgnoreCase))
                return (null, url, "Redirected off the private host");
            url = next;
        }
    }

    // Reads the (already decompressed) body, aborting as soon as it passes the cap. Null when over.
    private static async Task<byte[]?> ReadCappedAsync(HttpResponseMessage resp, int cap, CancellationToken ct)
    {
        if (resp.Content.Headers.ContentLength > cap) return null;
        await using var s = await resp.Content.ReadAsStreamAsync(ct);
        using var ms = new MemoryStream();
        var buf = new byte[81920];
        int read;
        while ((read = await s.ReadAsync(buf, ct)) > 0)
        {
            if (ms.Length + read > cap) return null;
            ms.Write(buf, 0, read);
        }
        return ms.ToArray();
    }

    private static TimeSpan? RetryAfter(HttpResponseMessage resp, DateTime nowUtc)
    {
        if ((int)resp.StatusCode is not (429 or 503)) return null;
        var ra = resp.Headers.RetryAfter;
        TimeSpan? d = ra?.Delta ?? (ra?.Date is { } at ? at.UtcDateTime - nowUtc : null);
        if (d is null || d <= TimeSpan.Zero) return null;
        return d > MaxRetryAfter ? MaxRetryAfter : d;
    }

    // Our own wording for the status — never the server's reason phrase, which is untrusted text.
    internal static string StatusText(HttpStatusCode code)
    {
        int n = (int)code;
        string name = Enum.IsDefined(code) ? SplitWords(code.ToString()) : "";
        return name.Length > 0 ? $"{n} {name}" : $"HTTP {n}";
    }

    private static string SplitWords(string s)
    {
        var sb = new System.Text.StringBuilder(s.Length + 4);
        for (int i = 0; i < s.Length; i++)
        {
            if (i > 0 && char.IsUpper(s[i]) && !char.IsUpper(s[i - 1])) sb.Append(' ');
            sb.Append(s[i]);
        }
        return sb.ToString();
    }

    private static string Describe(Exception e, CancellationToken ct) => e switch
    {
        FeedFenceException fence => fence.Message,
        OperationCanceledException when ct.IsCancellationRequested => "Cancelled",
        OperationCanceledException or TimeoutException => "Timed out",
        HttpRequestException { InnerException: FeedFenceException inner } => inner.Message,
        HttpRequestException { InnerException: SocketException } => "Couldn't connect",
        HttpRequestException => "Couldn't connect",
        _ => "Couldn't fetch the feed",
    };

    public void Dispose()
    {
        _open.Dispose();
        _fenced.Dispose();
    }
}
