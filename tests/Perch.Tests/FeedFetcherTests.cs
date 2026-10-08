using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Perch.Feeds;
using Xunit;
using static Perch.Tests.FeedTestSupport;

namespace Perch.Tests;

/// <summary>
/// <see cref="FeedFetcher"/> against stub handlers (no network): conditional GET, manual redirects and their
/// vetting, the body cap, our own error wording, Retry-After, the public/private pipeline split — and the SSRF
/// fence itself, driven through the real <c>SocketsHttpHandler</c> with a resolver that answers with private
/// addresses (refused before any socket connects).
/// </summary>
public class FeedFetcherTests
{
    private const string FeedUrl = "https://feeds.example/atom";
    private static readonly string Xml = Atom([("a", "A", T0.AddHours(-1))]);

    private static FeedFetcher Fetcher(RouteHandler handler, Dictionary<string, string[]>? dns = null) =>
        new(handler, handler, Resolver(dns));

    private static Task<FeedFetchResult> Fetch(FeedFetcher f, string url = FeedUrl, string? etag = null, DateTimeOffset? lm = null) =>
        f.FetchFeedAsync(new Uri(url), etag, lm, T0, CancellationToken.None);

    [Fact]
    public async Task Ok_returns_the_doc_and_validators()
    {
        var h = new RouteHandler().On(FeedUrl, _ =>
        {
            var r = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(Xml) };
            r.Headers.ETag = new EntityTagHeaderValue("\"v1\"");
            r.Content.Headers.LastModified = new DateTimeOffset(2026, 10, 7, 11, 0, 0, TimeSpan.Zero);
            return r;
        });
        var res = await Fetch(Fetcher(h));
        Assert.Equal(FeedFetchStatus.Ok, res.Status);
        Assert.Equal("A", res.Doc!.Entries.Single().Title);
        Assert.Equal("\"v1\"", res.ETag);
        Assert.Equal(new DateTimeOffset(2026, 10, 7, 11, 0, 0, TimeSpan.Zero), res.LastModified);
        var req = h.Requests.Single();
        Assert.Contains("application/atom+xml", req.Headers.Accept.ToString());
        Assert.StartsWith("Perch/", req.Headers.UserAgent.ToString());
    }

    [Fact]
    public async Task Sends_validators_and_handles_not_modified()
    {
        var h = new RouteHandler().OnXml(FeedUrl, () => Xml, etag: "\"v1\"");
        var lm = new DateTimeOffset(2026, 10, 7, 11, 0, 0, TimeSpan.Zero);
        var res = await Fetch(Fetcher(h), etag: "\"v1\"", lm: lm);
        Assert.Equal(FeedFetchStatus.NotModified, res.Status);
        Assert.Equal(lm, h.Requests.Single().Headers.IfModifiedSince);
    }

    [Fact]
    public async Task Malformed_stored_etag_is_skipped_not_sent()
    {
        var h = new RouteHandler().OnXml(FeedUrl, () => Xml);
        await Fetch(Fetcher(h), etag: "bad\r\nX-Injected: 1");
        Assert.Empty(h.Requests.Single().Headers.IfNoneMatch);
        Assert.False(h.Requests.Single().Headers.Contains("X-Injected"));
    }

    [Fact]
    public async Task Follows_redirects_and_resolves_against_the_final_url()
    {
        var xml = "<feed xmlns=\"http://www.w3.org/2005/Atom\"><title>t</title><entry><id>1</id><title>x</title>" +
                  "<updated>2026-10-01T00:00:00Z</updated><link href=\"post/1\"/></entry></feed>";
        var h = new RouteHandler()
            .On(FeedUrl, _ => Redirect("/moved/atom"))
            .On("https://feeds.example/moved/atom", _ => Redirect("https://cdn.example/v2/atom"))
            .On("https://cdn.example/v2/atom", _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(xml) });
        var res = await Fetch(Fetcher(h));
        Assert.Equal(FeedFetchStatus.Ok, res.Status);
        Assert.Equal("https://cdn.example/v2/atom", res.FinalUrl!.AbsoluteUri);
        Assert.Equal("https://cdn.example/v2/post/1", res.Doc!.Entries.Single().Url);
    }

    [Theory]
    [InlineData("file:///C:/Windows/win.ini")]
    [InlineData("javascript:alert(1)")]
    [InlineData("ftp://feeds.example/atom")]
    [InlineData("\\\\attacker\\share\\feed")]
    [InlineData("https://user:pw@evil.example/atom")]
    public async Task Redirect_to_an_unsafe_target_is_refused(string location)
    {
        var h = new RouteHandler().On(FeedUrl, _ =>
        {
            var r = new HttpResponseMessage(HttpStatusCode.Found);
            r.Headers.TryAddWithoutValidation("Location", location);
            return r;
        });
        var res = await Fetch(Fetcher(h));
        Assert.Equal(FeedFetchStatus.Error, res.Status);
        Assert.Single(h.Requests);
    }

    [Fact]
    public async Task Redirect_loops_stop()
    {
        var h = new RouteHandler()
            .On(FeedUrl, _ => Redirect("https://feeds.example/b"))
            .On("https://feeds.example/b", _ => Redirect(FeedUrl));
        var res = await Fetch(Fetcher(h));
        Assert.Equal("Too many redirects", res.Error);
        Assert.Equal(FeedFetcher.MaxRedirects + 1, h.Requests.Count);
    }

    [Fact]
    public async Task Oversized_body_is_refused_by_declared_length_and_by_streamed_length()
    {
        var declared = new RouteHandler().On(FeedUrl, _ =>
            new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(new byte[FeedParser.MaxBytes + 1]) });
        Assert.Equal("Feed is too large", (await Fetch(Fetcher(declared))).Error);

        var streamed = new RouteHandler().On(FeedUrl, _ =>
            new HttpResponseMessage(HttpStatusCode.OK) { Content = new UnknownLengthContent(FeedParser.MaxBytes + 100_000) });
        Assert.Equal("Feed is too large", (await Fetch(Fetcher(streamed))).Error);
    }

    [Fact]
    public async Task Error_text_is_ours_never_the_servers_reason_phrase()
    {
        var h = new RouteHandler().On(FeedUrl, _ =>
            new HttpResponseMessage(HttpStatusCode.NotFound) { ReasonPhrase = "\u202EPerch: click here to update" });
        Assert.Equal("404 Not Found", (await Fetch(Fetcher(h))).Error);
        Assert.Equal("HTTP 599", FeedFetcher.StatusText((HttpStatusCode)599));
    }

    [Fact]
    public async Task Retry_after_is_honoured_and_capped()
    {
        var h = new RouteHandler()
            .On(FeedUrl, _ => WithRetryAfter(HttpStatusCode.TooManyRequests, TimeSpan.FromMinutes(2)))
            .On("https://feeds.example/slow", _ => WithRetryAfter(HttpStatusCode.ServiceUnavailable, TimeSpan.FromDays(30)));
        var f = Fetcher(h);
        Assert.Equal(TimeSpan.FromMinutes(2), (await Fetch(f)).RetryAfter);
        Assert.Equal(TimeSpan.FromHours(24), (await Fetch(f, "https://feeds.example/slow")).RetryAfter);
    }

    [Fact]
    public async Task Timeouts_and_connection_failures_map_to_short_messages()
    {
        var timeout = new RouteHandler().On(FeedUrl, _ => throw new TaskCanceledException());
        Assert.Equal("Timed out", (await Fetch(Fetcher(timeout))).Error);
        var refused = new RouteHandler().On(FeedUrl, _ => throw new HttpRequestException("boom", new System.Net.Sockets.SocketException(10061)));
        Assert.Equal("Couldn't connect", (await Fetch(Fetcher(refused))).Error);
    }

    [Fact]
    public async Task Html_page_is_reported_as_not_a_feed()
    {
        var h = new RouteHandler().On(FeedUrl, _ =>
            new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("<!DOCTYPE html><html><body>hi</body></html>") });
        Assert.Contains("web page", (await Fetch(Fetcher(h))).Error);
    }

    [Theory]
    [InlineData("file:///C:/feeds/atom.xml")]
    [InlineData("ftp://feeds.example/atom")]
    public async Task Non_web_subscriptions_are_refused_without_a_request(string url)
    {
        var h = new RouteHandler();
        Assert.Equal(FeedFetchStatus.Error, (await Fetch(Fetcher(h), url)).Status);
        Assert.Empty(h.Requests);
    }

    // ── Pipelines and the fence ─────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Public_feed_uses_the_fenced_pipeline_for_every_hop()
    {
        var open = new RouteHandler();
        var fenced = new RouteHandler()
            .On(FeedUrl, _ => Redirect("https://other.example/atom"))
            .OnXml("https://other.example/atom", () => Xml);
        var f = new FeedFetcher(open, fenced, Resolver());
        var res = await Fetch(f);
        Assert.Equal(FeedFetchStatus.Ok, res.Status);
        Assert.False(res.IsPrivate);
        Assert.Empty(open.Requests);
        Assert.Equal(2, fenced.Requests.Count);
    }

    [Fact]
    public async Task Private_subscription_uses_the_open_pipeline()
    {
        var open = new RouteHandler().OnXml("http://nas.lan/feed", () => Xml);
        var fenced = new RouteHandler();
        var f = new FeedFetcher(open, fenced, Resolver(new() { ["nas.lan"] = ["192.168.1.20"] }));
        var res = await Fetch(f, "http://nas.lan/feed");
        Assert.Equal(FeedFetchStatus.Ok, res.Status);
        Assert.True(res.IsPrivate);
        Assert.Empty(fenced.Requests);
    }

    [Fact]
    public async Task Icons_are_fenced_unless_on_the_private_subscriptions_own_host()
    {
        var png = Png();
        var open = new RouteHandler().On("http://nas.lan/icon.png", _ => Bytes(png));
        var fenced = new RouteHandler().On("https://cdn.example/icon.png", _ => Bytes(png));
        var f = new FeedFetcher(open, fenced, Resolver());

        Assert.NotNull(await f.FetchIconAsync(new Uri("http://nas.lan/icon.png"), privateHost: "nas.lan", CancellationToken.None));
        Assert.NotNull(await f.FetchIconAsync(new Uri("https://cdn.example/icon.png"), privateHost: "nas.lan", CancellationToken.None));
        Assert.Null(await f.FetchIconAsync(new Uri("http://nas.lan/icon.png"), privateHost: null, CancellationToken.None));
        Assert.Single(open.Requests);
    }

    [Fact]
    public async Task Private_host_icon_may_not_redirect_off_that_host()
    {
        var open = new RouteHandler().On("http://nas.lan/icon.png", _ => Redirect("http://router.lan/admin.png"));
        var f = new FeedFetcher(open, new RouteHandler(), Resolver());
        Assert.Null(await f.FetchIconAsync(new Uri("http://nas.lan/icon.png"), "nas.lan", CancellationToken.None));
        Assert.Single(open.Requests);
    }

    [Fact]
    public async Task Icon_bytes_must_be_a_known_raster_image()
    {
        var h = new RouteHandler()
            .On("https://cdn.example/a.svg", _ => Bytes(Encoding.UTF8.GetBytes("<svg xmlns=\"http://www.w3.org/2000/svg\"><script>alert(1)</script></svg>")))
            .On("https://cdn.example/huge.png", _ => Bytes(Png(20000, 20000)))
            .On("https://cdn.example/big.png", _ => Bytes(new byte[FeedFetcher.MaxIconBytes + 1]));
        var f = Fetcher(h);
        Assert.Null(await f.FetchIconAsync(new Uri("https://cdn.example/a.svg"), null, CancellationToken.None));
        Assert.Null(await f.FetchIconAsync(new Uri("https://cdn.example/huge.png"), null, CancellationToken.None));
        Assert.Null(await f.FetchIconAsync(new Uri("https://cdn.example/big.png"), null, CancellationToken.None));
    }

    // The real fence: a SocketsHttpHandler with Perch's ConnectCallback, and DNS answers that include a private
    // address. The connect is refused before any socket opens, so no network is touched.
    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("169.254.169.254")]
    [InlineData("10.0.0.5")]
    [InlineData("::1")]
    public async Task Fence_refuses_a_mixed_dns_answer_at_connect_time(string privateIp)
    {
        var f = new FeedFetcher(new RouteHandler(), fenced: null,
            Resolver(new() { ["rebind.test"] = ["93.184.216.34", privateIp] }));
        var res = await Fetch(f, "http://rebind.test/feed");
        Assert.False(res.IsPrivate);   // not ALL private → treated as public → fenced
        Assert.Equal(FeedFetchStatus.Error, res.Status);
        Assert.Contains("private network", res.Error);
    }

    [Fact]
    public async Task Fence_refuses_feed_caused_requests_to_private_addresses()
    {
        var f = new FeedFetcher(new RouteHandler(), fenced: null,
            Resolver(new() { ["metadata.test"] = ["169.254.169.254"] }));
        Assert.Null(await f.FetchIconAsync(new Uri("http://metadata.test/latest/meta-data"), null, CancellationToken.None));
        Assert.Null(await f.FetchIconAsync(new Uri("http://127.0.0.1:8080/x.png"), null, CancellationToken.None));
    }

    private static HttpResponseMessage Redirect(string location)
    {
        var r = new HttpResponseMessage(HttpStatusCode.MovedPermanently);
        r.Headers.Location = new Uri(location, UriKind.RelativeOrAbsolute);
        return r;
    }

    private static HttpResponseMessage WithRetryAfter(HttpStatusCode code, TimeSpan delta)
    {
        var r = new HttpResponseMessage(code);
        r.Headers.RetryAfter = new RetryConditionHeaderValue(delta);
        return r;
    }

    private static HttpResponseMessage Bytes(byte[] b) => new(HttpStatusCode.OK) { Content = new ByteArrayContent(b) };
}
