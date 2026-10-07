using System.Net;
using System.Text;
using Perch.Feeds;

namespace Perch.Tests;

/// <summary>Shared fakes for the feed tests: a routing HTTP handler, a resolver, Atom builders, a temp store.</summary>
internal static class FeedTestSupport
{
    public static readonly DateTime T0 = new(2026, 10, 7, 12, 0, 0, DateTimeKind.Utc);

    public static string Atom(IEnumerable<(string Id, string Title, DateTime Updated)> entries, string title = "Feed",
        string? icon = null, string extraEntryXml = "")
    {
        var sb = new StringBuilder("<?xml version=\"1.0\" encoding=\"utf-8\"?><feed xmlns=\"http://www.w3.org/2005/Atom\">");
        sb.Append("<title>").Append(WebUtility.HtmlEncode(title)).Append("</title>");
        sb.Append("<link rel=\"alternate\" href=\"https://site.example/\"/>");
        if (icon is not null) sb.Append("<icon>").Append(WebUtility.HtmlEncode(icon)).Append("</icon>");
        foreach (var (id, t, u) in entries)
            sb.Append($"<entry><id>{WebUtility.HtmlEncode(id)}</id><title>{WebUtility.HtmlEncode(t)}</title>" +
                      $"<updated>{u:yyyy-MM-ddTHH:mm:ssZ}</updated><link href=\"https://site.example/{WebUtility.HtmlEncode(id)}\"/>" +
                      extraEntryXml + "</entry>");
        sb.Append("</feed>");
        return sb.ToString();
    }

    public static FeedEntry Entry(string id, DateTime updated, string? title = null) =>
        new(id, title ?? id, null, null, null, updated, null, null, null);

    public static FeedDoc Doc(params FeedEntry[] newestFirst) => new("Feed", null, null, null, newestFirst);

    /// <summary>A 16×16 PNG header — enough for <see cref="Perch.Data.ImageHeader"/>.</summary>
    public static byte[] Png(int w = 16, int h = 16)
    {
        var b = new byte[64];
        new byte[] { 0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 13, (byte)'I', (byte)'H', (byte)'D', (byte)'R' }.CopyTo(b, 0);
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(b.AsSpan(16), w);
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(b.AsSpan(20), h);
        return b;
    }

    public static Func<string, CancellationToken, Task<IPAddress[]>> Resolver(Dictionary<string, string[]>? map = null) =>
        (host, _) => Task.FromResult(
            map is not null && map.TryGetValue(host, out var ips)
                ? ips.Select(IPAddress.Parse).ToArray()
                : [IPAddress.Parse("93.184.216.34")]);

    public static string TempDir()
    {
        var d = Path.Combine(Path.GetTempPath(), "perch-feeds-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(d);
        return d;
    }
}

/// <summary>Routes requests by absolute URL to a responder; records every request it sees.</summary>
internal sealed class RouteHandler : HttpMessageHandler
{
    private readonly Dictionary<string, Func<HttpRequestMessage, HttpResponseMessage>> _routes = new(StringComparer.Ordinal);
    public List<HttpRequestMessage> Requests { get; } = [];

    public RouteHandler On(string url, Func<HttpRequestMessage, HttpResponseMessage> responder)
    {
        _routes[url] = responder;
        return this;
    }

    public RouteHandler OnXml(string url, Func<string> xml, string? etag = null) => On(url, req =>
    {
        if (etag is not null && req.Headers.IfNoneMatch.Any(t => t.Tag == etag))
            return new HttpResponseMessage(HttpStatusCode.NotModified);
        var r = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(xml(), Encoding.UTF8, "application/atom+xml") };
        if (etag is not null) r.Headers.ETag = new System.Net.Http.Headers.EntityTagHeaderValue(etag);
        return r;
    });

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        Requests.Add(request);
        var key = request.RequestUri!.AbsoluteUri;
        var resp = _routes.TryGetValue(key, out var f) ? f(request) : new HttpResponseMessage(HttpStatusCode.NotFound);
        resp.RequestMessage = request;
        return Task.FromResult(resp);
    }
}

/// <summary>Content with no known length, streamed — so the read-side cap (not Content-Length) is what's tested.</summary>
internal sealed class UnknownLengthContent(int bytes) : HttpContent
{
    protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context)
    {
        var chunk = new byte[64 * 1024];
        Array.Fill(chunk, (byte)'a');
        for (int left = bytes; left > 0; left -= chunk.Length)
            await stream.WriteAsync(chunk.AsMemory(0, Math.Min(chunk.Length, left)));
    }

    protected override bool TryComputeLength(out long length)
    {
        length = -1;
        return false;
    }
}
