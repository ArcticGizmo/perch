using System.Buffers.Binary;
using System.Text;
using System.Text.Json;
using Perch.Feeds;
using Xunit;
using static Perch.Tests.FeedTestSupport;

namespace Perch.Tests;

/// <summary><see cref="FeedReadState"/>, <see cref="FeedSchedule"/>, <see cref="FeedIcon"/> and
/// <see cref="FeedStore"/> — the pure rules and the on-disk side of feeds.</summary>
public class FeedStateTests
{
    // ── Read state ──────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void First_fetch_primes_everything_as_read()
    {
        var rec = new FeedReadRecord();
        var doc = Doc(Entry("b", T0), Entry("a", T0.AddHours(-1)));
        Assert.Empty(FeedReadState.ApplyFetch(rec, null, doc, T0));
        Assert.Equal(0, FeedReadState.UnreadCount(rec, doc));
        Assert.Equal(T0, rec.PrimedUtc);
    }

    [Fact]
    public void New_ids_after_priming_are_unread_and_reported_once()
    {
        var rec = new FeedReadRecord();
        var v1 = Doc(Entry("a", T0));
        FeedReadState.ApplyFetch(rec, null, v1, T0);
        var v2 = Doc(Entry("b", T0.AddHours(1)), Entry("a", T0));
        Assert.Equal(["b"], FeedReadState.ApplyFetch(rec, v1, v2, T0.AddHours(1)).Select(e => e.Id));
        Assert.Equal(1, FeedReadState.UnreadCount(rec, v2));
        // Still unread on the next fetch, but not "new" again.
        Assert.Empty(FeedReadState.ApplyFetch(rec, v2, v2, T0.AddHours(2)));
        Assert.Equal(1, FeedReadState.UnreadCount(rec, v2));
    }

    [Fact]
    public void Edits_do_not_relight_a_read_entry()
    {
        var rec = new FeedReadRecord();
        FeedReadState.ApplyFetch(rec, null, Doc(Entry("a", T0, "Old title")), T0);
        var edited = Doc(Entry("a", T0.AddDays(1), "New title"));
        FeedReadState.ApplyFetch(rec, Doc(Entry("a", T0)), edited, T0.AddDays(1));
        Assert.Equal(0, FeedReadState.UnreadCount(rec, edited));
    }

    [Fact]
    public void A_temporarily_empty_feed_cannot_wipe_the_markers()
    {
        var rec = new FeedReadRecord();
        var full = Doc(Entry("a", T0), Entry("b", T0));
        FeedReadState.ApplyFetch(rec, null, full, T0);
        FeedReadState.ApplyFetch(rec, full, Doc(), T0.AddHours(1));                 // server hiccup
        Assert.Empty(FeedReadState.ApplyFetch(rec, Doc(), full, T0.AddHours(2)));  // recovers
        Assert.Equal(0, FeedReadState.UnreadCount(rec, full));
    }

    [Fact]
    public void Markers_for_departed_entries_expire_after_retention()
    {
        var rec = new FeedReadRecord();
        FeedReadState.ApplyFetch(rec, null, Doc(Entry("old", T0)), T0);
        FeedReadState.ApplyFetch(rec, null, Doc(Entry("new", T0)), T0 + FeedReadState.Retention + TimeSpan.FromDays(1));
        Assert.False(rec.Read.ContainsKey("old"));
    }

    [Fact]
    public void Mark_read_and_mark_all()
    {
        var rec = new FeedReadRecord();
        FeedReadState.ApplyFetch(rec, null, Doc(), T0);
        var doc = Doc(Entry("a", T0), Entry("b", T0));
        FeedReadState.ApplyFetch(rec, Doc(), doc, T0);
        FeedReadState.MarkRead(rec, "a", T0);
        Assert.Equal(1, FeedReadState.UnreadCount(rec, doc));
        FeedReadState.MarkAllRead(rec, doc, T0);
        Assert.Equal(0, FeedReadState.UnreadCount(rec, doc));
    }

    // ── Schedule ────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Launch_is_delayed_and_staggered()
    {
        var s = new FeedSchedule();
        s.Start(["a", "b", "c"], T0, atLaunch: true);
        Assert.Empty(s.Due(T0, 10));
        Assert.Equal(["a"], s.Due(T0 + FeedSchedule.LaunchDelay, 10));
        Assert.Equal(["a", "b"], s.Due(T0 + FeedSchedule.LaunchDelay + FeedSchedule.Stagger, 10));
        Assert.Equal(3, s.Due(T0 + TimeSpan.FromMinutes(1), 10).Count);
    }

    [Fact]
    public void Runtime_start_staggers_without_the_delay_and_added_feeds_are_due_now()
    {
        var s = new FeedSchedule();
        s.Start(["a", "b"], T0, atLaunch: false);
        Assert.Equal(["a"], s.Due(T0, 10));
        s.Add("new", T0);
        Assert.Contains("new", s.Due(T0, 10));
    }

    [Fact]
    public void Success_waits_the_interval_plus_bounded_jitter()
    {
        var s = new FeedSchedule();
        s.Start(["a"], T0, atLaunch: false);
        var interval = TimeSpan.FromMinutes(30);
        s.Succeeded("a", T0, interval);
        var due = s.DueAt("a")!.Value;
        Assert.InRange(due, T0 + interval, T0 + interval * 1.1);
    }

    [Fact]
    public void Failures_back_off_exponentially_to_a_cap_and_respect_retry_after()
    {
        var s = new FeedSchedule();
        s.Start(["a"], T0, atLaunch: false);
        s.Failed("a", T0, null);
        Assert.Equal(T0 + TimeSpan.FromMinutes(5), s.DueAt("a"));
        s.Failed("a", T0, null);
        Assert.Equal(T0 + TimeSpan.FromMinutes(10), s.DueAt("a"));
        for (int i = 0; i < 20; i++) s.Failed("a", T0, null);
        Assert.Equal(T0 + FeedSchedule.BackoffCap, s.DueAt("a"));
        s.Failed("a", T0, TimeSpan.FromHours(6));
        Assert.Equal(T0 + TimeSpan.FromHours(6), s.DueAt("a"));
        s.Succeeded("a", T0, TimeSpan.FromMinutes(30));
        s.Failed("a", T0, null);
        Assert.Equal(T0 + TimeSpan.FromMinutes(5), s.DueAt("a"));   // success reset the backoff
    }

    // ── Icons ───────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Raster_icons_pass_and_svg_or_huge_fail()
    {
        Assert.Equal(".png", FeedIcon.Validate(Png()));
        Assert.Null(FeedIcon.Validate(Png(5000, 5000)));
        Assert.Null(FeedIcon.Validate(Encoding.UTF8.GetBytes("<svg xmlns=\"http://www.w3.org/2000/svg\"/>")));
        Assert.Null(FeedIcon.Validate(Encoding.UTF8.GetBytes("<html>")));
        Assert.Null(FeedIcon.Validate([1, 2, 3]));
    }

    [Fact]
    public void Ico_entries_are_checked_including_embedded_png()
    {
        Assert.Equal(".ico", FeedIcon.Validate(Ico(Bmp(16, 16))));
        Assert.Equal(".ico", FeedIcon.Validate(Ico(Png(32, 32))));
        Assert.Null(FeedIcon.Validate(Ico(Png(9000, 9000))));     // an ICO wrapping a decompression bomb
        Assert.Null(FeedIcon.Validate(Ico(Bmp(5000, 16))));

        var broken = Ico(Bmp(16, 16));
        BinaryPrimitives.WriteUInt32LittleEndian(broken.AsSpan(6 + 12), 999_999);   // offset past the end
        Assert.Null(FeedIcon.Validate(broken));
    }

    private static byte[] Bmp(int w, int h)
    {
        var dib = new byte[40];
        BinaryPrimitives.WriteInt32LittleEndian(dib.AsSpan(0), 40);
        BinaryPrimitives.WriteInt32LittleEndian(dib.AsSpan(4), w);
        BinaryPrimitives.WriteInt32LittleEndian(dib.AsSpan(8), h * 2);
        return dib;
    }

    private static byte[] Ico(byte[] image)
    {
        var b = new byte[6 + 16 + image.Length];
        b[2] = 1;
        b[4] = 1;
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(6 + 8), (uint)image.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(6 + 12), 22);
        image.CopyTo(b, 22);
        return b;
    }

    // ── Store ───────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Cache_round_trips()
    {
        var store = new FeedStore(TempDir());
        var doc = new FeedDoc("Feed", "https://site.example/", null, T0,
            [new FeedEntry("e1", "Title", "https://site.example/e1", "Ann", T0, T0, "<p>hi</p>", "https://site.example/", "hi")]);
        store.SaveCache("abc", new FeedCacheEntry { SourceUrl = "https://site.example/atom", Doc = doc, ETag = "\"x\"" });
        var back = store.LoadCache("abc")!;
        Assert.Equal("\"x\"", back.ETag);
        Assert.Equal(doc.Entries[0], back.Doc!.Entries[0]);
        Assert.Equal(DateTimeKind.Utc, back.Doc.Entries[0].Updated.Kind);
    }

    [Fact]
    public void A_tampered_cache_is_recleaned_on_load()
    {
        var root = TempDir();
        var store = new FeedStore(root);
        var dirty = new FeedCacheEntry
        {
            SourceUrl = "https://site.example/atom",
            ETag = "x\r\nInjected: 1",
            Error = "\u202Eboom\nline",
            IconFile = "..\\..\\evil.dll",
            Doc = new FeedDoc("T\u202Eitle\n\nfake", "javascript:alert(1)", "file:///C:/x.png", null,
                [new FeedEntry("e", "A\u200Bb\nc", "javascript:alert(1)", "\u2066Eve", null, T0, "<script>x</script>",
                    "file:///C:/", "s\u0007")]),
        };
        Directory.CreateDirectory(Path.Combine(root, "cache"));
        File.WriteAllText(Path.Combine(root, "cache", "abc.json"), JsonSerializer.Serialize(dirty));

        var e = store.LoadCache("abc")!;
        Assert.Null(e.ETag);
        Assert.Equal("boom line", e.Error);
        Assert.Null(e.IconFile);
        Assert.Equal("Title fake", e.Doc!.Title);
        Assert.Null(e.Doc.SiteUrl);
        Assert.Null(e.Doc.IconUrl);
        var x = e.Doc.Entries.Single();
        Assert.Equal("Ab c", x.Title);
        Assert.Null(x.Url);
        Assert.Equal("Eve", x.Author);
        Assert.Null(x.ContentBase);
        Assert.Equal("s", x.SummaryText);
    }

    [Theory]
    [InlineData("..\\..\\x")]
    [InlineData("../x")]
    [InlineData("a/b")]
    [InlineData("C:")]
    [InlineData("")]
    [InlineData("con.json")]
    public void Ids_that_could_escape_the_folder_are_refused(string id)
    {
        var root = TempDir();
        var store = new FeedStore(root);
        Assert.False(FeedStore.IsValidId(id));
        store.SaveCache(id, new FeedCacheEntry());
        Assert.Null(store.LoadCache(id));
        Assert.Null(store.SaveIcon(id, Png()));
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(root)!, "*.json", SearchOption.TopDirectoryOnly)
            .Where(f => Path.GetFileName(f) == "x.json"));
    }

    [Fact]
    public void Read_state_round_trips_and_drops_bad_keys()
    {
        var root = TempDir();
        var store = new FeedStore(root);
        var recs = new Dictionary<string, FeedReadRecord>
        {
            ["good"] = new() { PrimedUtc = T0, Read = { ["e1"] = T0 } },
            ["../bad"] = new() { PrimedUtc = T0 },
        };
        store.WriteRead(FeedStore.SerializeRead(recs));
        var back = store.LoadRead();
        Assert.Equal(["good"], back.Keys);
        Assert.True(back["good"].Read.ContainsKey("e1"));
    }

    [Fact]
    public void Icons_are_stored_only_when_valid_and_served_only_by_expected_name()
    {
        var store = new FeedStore(TempDir());
        Assert.Null(store.SaveIcon("abc", Encoding.UTF8.GetBytes("<svg/>")));
        Assert.Equal("abc.png", store.SaveIcon("abc", Png()));
        Assert.NotNull(store.IconPath("abc", "abc.png"));
        Assert.Null(store.IconPath("abc", "other.png"));
        Assert.Equal("abc.ico", store.SaveIcon("abc", Ico(Bmp(16, 16))));
        Assert.Null(store.IconPath("abc", "abc.png"));   // the old format was replaced
    }
}
