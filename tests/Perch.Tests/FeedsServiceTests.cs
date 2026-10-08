using System.Net;
using Perch.Feeds;
using Xunit;
using static Perch.Tests.FeedTestSupport;

namespace Perch.Tests;

/// <summary>
/// <see cref="FeedsService"/> end to end: stub HTTP (no network), a temp store and a hand-driven clock — priming,
/// arrivals, read state, ordering, failures, the launch stagger, re-pointing and removal, icons.
/// </summary>
public class FeedsServiceTests
{
    private const string UrlA = "https://a.example/atom";
    private const string UrlB = "https://b.example/atom";

    private sealed class Rig : IDisposable
    {
        public DateTime Now = T0;
        public readonly RouteHandler Http = new();
        public readonly string Root = TempDir();
        public readonly List<(string Id, string Title, DateTime Updated)> A = [("a1", "First", T0.AddHours(-2))];
        public readonly List<(string Id, string Title, DateTime Updated)> B = [("b1", "Bee", T0.AddHours(-3))];
        public FeedsService Service;

        public Rig(string? iconForA = null)
        {
            Http.OnXml(UrlA, () => Atom(A, "Feed A", iconForA), etag: null)
                .OnXml(UrlB, () => Atom(B, "Feed B"));
            Service = NewService();
        }

        public FeedsService NewService() =>
            new(new FeedStore(Root), new FeedFetcher(Http, Http, Resolver()), () => Now);

        public static FeedSubscription Sub(string id, string url, string? title = null) =>
            new() { Id = id, Url = url, TitleOverride = title, AddedUtc = T0 };

        public void Dispose() => Service.Dispose();
    }

    private static FeedHead Head(FeedsService s, string id) => s.Snapshot().Heads.Single(h => h.SubId == id);

    // Feeds started together are staggered a few seconds apart; tick past the stagger so every feed has primed.
    private static async Task PrimeAll(Rig rig)
    {
        await rig.Service.TickAsync();
        rig.Now = T0.AddMinutes(1);
        await rig.Service.TickAsync();
    }

    private static List<string> FeedRequests(Rig rig) =>
        rig.Http.Requests.Select(r => r.RequestUri!.AbsoluteUri).Where(u => u.EndsWith("/atom")).ToList();

    [Fact]
    public async Task Adding_a_feed_is_quiet()
    {
        using var rig = new Rig();
        rig.Service.SetSubscriptions([Rig.Sub("a", UrlA)]);
        rig.Service.Start(atLaunch: false);
        var tick = await rig.Service.TickAsync();
        Assert.True(tick.Changed);
        Assert.Empty(tick.Arrivals);
        var h = Head(rig.Service, "a");
        Assert.True(h.HasFetched);
        Assert.Equal(0, h.UnreadCount);
        Assert.Equal("Feed A", h.Title);
        Assert.Equal("First", h.LatestTitle);
    }

    [Fact]
    public async Task New_entries_light_the_ring_and_arrive_once()
    {
        using var rig = new Rig();
        rig.Service.SetSubscriptions([Rig.Sub("a", UrlA)]);
        rig.Service.Start(atLaunch: false);
        await rig.Service.TickAsync();

        rig.A.Insert(0, ("a2", "Second", T0.AddMinutes(10)));
        rig.Now = T0.AddMinutes(10);
        Assert.Empty((await rig.Service.TickAsync()).Arrivals);    // not due yet: interval is 30 min

        rig.Now = T0.AddMinutes(40);
        var tick = await rig.Service.TickAsync();
        var arrival = Assert.Single(tick.Arrivals);
        Assert.Equal("Feed A", arrival.FeedTitle);
        Assert.Equal(["a2"], arrival.Entries.Select(e => e.Id));
        Assert.Equal(1, Head(rig.Service, "a").UnreadCount);

        rig.Now = T0.AddMinutes(80);
        Assert.Empty((await rig.Service.TickAsync()).Arrivals);   // still unread, but not new again
    }

    [Fact]
    public async Task Reading_clears_the_ring_and_persists()
    {
        using var rig = new Rig();
        rig.Service.SetSubscriptions([Rig.Sub("a", UrlA)]);
        rig.Service.Start(atLaunch: false);
        await rig.Service.TickAsync();
        rig.A.Insert(0, ("a2", "Second", T0.AddMinutes(10)));
        rig.Now = T0.AddMinutes(40);
        await rig.Service.TickAsync();

        Assert.True(rig.Service.MarkRead("a", "a2"));
        Assert.False(rig.Service.MarkRead("a", "a2"));
        Assert.Equal(0, Head(rig.Service, "a").UnreadCount);
        rig.Service.SaveReadIfDirty();

        using var again = rig.NewService();
        again.SetSubscriptions([Rig.Sub("a", UrlA)]);
        Assert.Equal(0, Head(again, "a").UnreadCount);
        Assert.Equal("Second", Head(again, "a").LatestTitle);   // painted from the cache before any fetch
    }

    [Fact]
    public async Task Unread_feeds_sort_first_newest_unread_first()
    {
        using var rig = new Rig();
        rig.Service.SetSubscriptions([Rig.Sub("a", UrlA), Rig.Sub("b", UrlB)]);
        rig.Service.Start(atLaunch: false);
        await PrimeAll(rig);
        Assert.Equal(["a", "b"], rig.Service.Snapshot().Heads.Select(h => h.SubId));

        rig.B.Insert(0, ("b2", "New bee", T0.AddMinutes(5)));
        rig.Now = T0.AddHours(1);
        await rig.Service.TickAsync();
        var snap = rig.Service.Snapshot();
        Assert.Equal(["b", "a"], snap.Heads.Select(h => h.SubId));
        Assert.Equal(["b", "a"], snap.Stories.Select(s => s.SubId));
        Assert.Equal(1, snap.TotalUnread);

        rig.Service.MarkAllRead();
        Assert.Equal(["a", "b"], rig.Service.Snapshot().Heads.Select(h => h.SubId));
    }

    [Fact]
    public async Task A_failure_keeps_the_last_good_doc_and_backs_off()
    {
        using var rig = new Rig();
        rig.Service.SetSubscriptions([Rig.Sub("a", UrlA)]);
        rig.Service.Start(atLaunch: false);
        await rig.Service.TickAsync();

        rig.Http.On(UrlA, _ => new HttpResponseMessage(HttpStatusCode.InternalServerError));
        rig.Now = T0.AddHours(1);
        await rig.Service.TickAsync();
        var h = Head(rig.Service, "a");
        Assert.Equal("500 Internal Server Error", h.Error);
        Assert.Equal("First", h.LatestTitle);

        int before = rig.Http.Requests.Count;
        rig.Now = T0.AddHours(1).AddMinutes(4);
        await rig.Service.TickAsync();
        Assert.Equal(before, rig.Http.Requests.Count);   // backing off (5 min)
    }

    [Fact]
    public async Task Startup_waits_then_staggers()
    {
        using var rig = new Rig();
        rig.Service.SetSubscriptions([Rig.Sub("a", UrlA), Rig.Sub("b", UrlB)]);
        rig.Service.Start(atLaunch: true);
        await rig.Service.TickAsync();
        Assert.Empty(rig.Http.Requests);

        rig.Now = T0 + FeedSchedule.LaunchDelay;
        await rig.Service.TickAsync();
        Assert.Equal([UrlA], FeedRequests(rig));

        rig.Now += FeedSchedule.Stagger;
        await rig.Service.TickAsync();
        Assert.Equal([UrlA, UrlB], FeedRequests(rig));
    }

    [Fact]
    public async Task Repointing_a_feed_primes_it_afresh()
    {
        using var rig = new Rig();
        rig.Service.SetSubscriptions([Rig.Sub("a", UrlA)]);
        rig.Service.Start(atLaunch: false);
        await rig.Service.TickAsync();
        rig.Service.SetSubscriptions([Rig.Sub("a", UrlB)]);
        Assert.False(Head(rig.Service, "a").HasFetched);
        await rig.Service.TickAsync();   // due immediately
        var h = Head(rig.Service, "a");
        Assert.Equal("Feed B", h.Title);
        Assert.Equal(0, h.UnreadCount);  // primed, not lit
    }

    [Fact]
    public async Task Removing_a_feed_forgets_it_on_disk_but_disabling_keeps_the_cache()
    {
        using var rig = new Rig();
        rig.Service.SetSubscriptions([Rig.Sub("a", UrlA), Rig.Sub("b", UrlB)]);
        rig.Service.Start(atLaunch: false);
        await PrimeAll(rig);
        var cacheA = Path.Combine(rig.Root, "cache", "a.json");
        var cacheB = Path.Combine(rig.Root, "cache", "b.json");
        Assert.True(File.Exists(cacheA) && File.Exists(cacheB));

        var disabledB = Rig.Sub("b", UrlB);
        disabledB.Enabled = false;
        rig.Service.SetSubscriptions([disabledB]);
        Assert.False(File.Exists(cacheA));
        Assert.True(File.Exists(cacheB));
        Assert.Empty(rig.Service.Snapshot().Heads);
    }

    [Fact]
    public async Task Title_override_wins_and_is_cleaned()
    {
        using var rig = new Rig();
        rig.Service.SetSubscriptions([Rig.Sub("a", UrlA, "  My\u202E\nfeed  ")]);
        rig.Service.Start(atLaunch: false);
        await rig.Service.TickAsync();
        Assert.Equal("My feed", Head(rig.Service, "a").Title);
    }

    [Fact]
    public void Bad_subscriptions_are_ignored()
    {
        using var rig = new Rig();
        rig.Service.SetSubscriptions([
            Rig.Sub("../x", UrlA), Rig.Sub("ok", "file:///C:/x.xml"), Rig.Sub("js", "javascript:alert(1)"), Rig.Sub("good", UrlA),
        ]);
        Assert.Equal(["good"], rig.Service.Snapshot().Heads.Select(h => h.SubId));
    }

    [Fact]
    public async Task Insecure_http_is_flagged()
    {
        using var rig = new Rig();
        rig.Http.OnXml("http://plain.example/atom", () => Atom([("p", "P", T0)]));
        rig.Service.SetSubscriptions([Rig.Sub("p", "http://plain.example/atom")]);
        rig.Service.Start(atLaunch: false);
        await rig.Service.TickAsync();
        Assert.True(Head(rig.Service, "p").InsecureHttp);
    }

    [Fact]
    public async Task Icon_is_fetched_once_and_cached()
    {
        using var rig = new Rig(iconForA: "/icon.png");
        rig.Http.On("https://a.example/icon.png", _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Png()) });
        rig.Service.SetSubscriptions([Rig.Sub("a", UrlA)]);
        rig.Service.Start(atLaunch: false);
        await rig.Service.TickAsync();
        Assert.EndsWith("a.png", Head(rig.Service, "a").IconPath);

        rig.Now = T0.AddHours(1);
        await rig.Service.TickAsync();
        Assert.Single(rig.Http.Requests, r => r.RequestUri!.AbsolutePath == "/icon.png");
    }

    [Fact]
    public async Task Missing_icon_falls_back_to_favicon_and_does_not_retry_every_poll()
    {
        using var rig = new Rig();
        rig.Service.SetSubscriptions([Rig.Sub("a", UrlA)]);
        rig.Service.Start(atLaunch: false);
        await rig.Service.TickAsync();
        rig.Now = T0.AddHours(1);
        await rig.Service.TickAsync();
        Assert.Single(rig.Http.Requests, r => r.RequestUri!.AbsoluteUri == "https://site.example/favicon.ico");
        Assert.Null(Head(rig.Service, "a").IconPath);
    }

    [Fact]
    public async Task Not_modified_keeps_the_cache()
    {
        using var rig = new Rig();
        rig.Http.OnXml(UrlA, () => Atom(rig.A, "Feed A"), etag: "\"v1\"");
        rig.Service.SetSubscriptions([Rig.Sub("a", UrlA)]);
        rig.Service.Start(atLaunch: false);
        await rig.Service.TickAsync();
        rig.Now = T0.AddHours(1);
        await rig.Service.TickAsync();
        var last = rig.Http.Requests.Last(r => r.RequestUri!.AbsoluteUri == UrlA);
        Assert.Contains(last.Headers.IfNoneMatch, t => t.Tag == "\"v1\"");
        Assert.Equal("First", Head(rig.Service, "a").LatestTitle);
        Assert.Null(Head(rig.Service, "a").Error);
    }

    [Fact]
    public async Task Stopped_service_never_fetches()
    {
        using var rig = new Rig();
        rig.Service.SetSubscriptions([Rig.Sub("a", UrlA)]);
        await rig.Service.TickAsync();
        rig.Service.RefreshNow();
        await rig.Service.TickAsync();
        Assert.Empty(rig.Http.Requests);
    }
}
