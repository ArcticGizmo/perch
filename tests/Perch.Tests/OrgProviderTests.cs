using Perch.Data;
using Xunit;

namespace Perch.Tests;

/// <summary>
/// Covers the Layer-2 M1 <see cref="OrgProvider"/> mtime cache: a repeat read is a cache hit (no
/// re-parse), a change to the file's stamp (a <c>/login</c> rewriting <c>.claude.json</c>) invalidates
/// it, signed-out is cached and re-checked when the file appears, and <see cref="OrgProvider.Invalidate"/>
/// clears everything. The change-stamp and read are injected so the cache logic is tested without real
/// files; one integration test drives a real file with an explicit last-write time.
/// </summary>
public class OrgProviderTests
{
    private static ClaudeConfigDir Dir(string root = @"C:\envs\a\.claude") => new(root);

    [Fact]
    public void RepeatRead_SameStamp_IsCacheHit()
    {
        var reads = 0;
        long stamp = 100;
        var provider = new OrgProvider(_ => { reads++; return new Org("u1"); }, _ => stamp);

        var first = provider.GetLive(Dir());
        var second = provider.GetLive(Dir());

        Assert.Equal("u1", first!.Uuid);
        Assert.Equal("u1", second!.Uuid);
        Assert.Equal(1, reads); // second call served from cache
    }

    [Fact]
    public void StampChange_ReReads()
    {
        var reads = 0;
        long stamp = 100;
        var provider = new OrgProvider(_ => { reads++; return new Org("u" + reads); }, _ => stamp);

        Assert.Equal("u1", provider.GetLive(Dir())!.Uuid);
        stamp = 200; // /login rewrote .claude.json
        Assert.Equal("u2", provider.GetLive(Dir())!.Uuid);
        Assert.Equal(2, reads);
    }

    [Fact]
    public void SignedOut_IsCached_ThenSignInReReads()
    {
        var reads = 0;
        long stamp = 0; // no file
        var provider = new OrgProvider(_ => { reads++; return stamp == 0 ? null : new Org("u1"); }, _ => stamp);

        Assert.Null(provider.GetLive(Dir()));
        Assert.Null(provider.GetLive(Dir()));
        Assert.Equal(1, reads); // null cached, not re-read

        stamp = 500; // signed in — file now exists
        Assert.Equal("u1", provider.GetLive(Dir())!.Uuid);
        Assert.Equal(2, reads);
    }

    [Fact]
    public void Invalidate_ForcesReRead()
    {
        var reads = 0;
        var provider = new OrgProvider(_ => { reads++; return new Org("u1"); }, _ => 100);

        provider.GetLive(Dir());
        provider.Invalidate();
        provider.GetLive(Dir());

        Assert.Equal(2, reads);
    }

    [Fact]
    public void AliasDirs_SameRealRoot_ShareCacheEntry()
    {
        var reads = 0;
        var provider = new OrgProvider(_ => { reads++; return new Org("u1"); }, _ => 100);

        var a = new ClaudeConfigDir(@"C:\envs\alpha\.claude", realRoot: @"C:\shared\store");
        var b = new ClaudeConfigDir(@"C:\envs\beta\.claude", realRoot: @"C:\shared\store");
        provider.GetLive(a);
        provider.GetLive(b);

        Assert.Equal(1, reads); // one physical dir, one read
    }

    // CP23: the overlay asks several times a second, so within the recheck window a cached answer is returned
    // without touching the file system at all (no stamp), and the stamp is looked at again once it passes.
    [Fact]
    public void WithinRecheckWindow_TheFileIsNotStatted()
    {
        long now = 0, stamp = 100;
        int stamps = 0, reads = 0;
        var provider = new OrgProvider(
            _ => { reads++; return new Org("u" + stamp); },
            _ => { stamps++; return stamp; },
            () => now, TimeSpan.FromSeconds(2));

        Assert.Equal("u100", provider.GetLive(Dir())!.Uuid);
        Assert.Equal((1, 1), (stamps, reads));

        now = 1_500; stamp = 200;                       // a /login lands, but inside the window
        Assert.Equal("u100", provider.GetLive(Dir())!.Uuid);
        Assert.Equal((1, 1), (stamps, reads));          // no stat, no read

        now = 2_100;                                    // window passed: stat again, see the change
        Assert.Equal("u200", provider.GetLive(Dir())!.Uuid);
        Assert.Equal((2, 2), (stamps, reads));

        now = 4_300;                                    // unchanged file: a stat, no re-read
        Assert.Equal("u200", provider.GetLive(Dir())!.Uuid);
        Assert.Equal((3, 2), (stamps, reads));

        provider.Invalidate();                          // a manual refresh still forces a read
        Assert.Equal("u200", provider.GetLive(Dir())!.Uuid);
        Assert.Equal((4, 3), (stamps, reads));
    }

    [Fact]
    public void RealFile_ReReadsWhenLastWriteChanges()
    {
        var tmp = Path.Combine(Path.GetTempPath(), "perch-orgprov-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tmp);
        try
        {
            var path = Path.Combine(tmp, ".claude.json");
            File.WriteAllText(path, """{ "oauthAccount": { "organizationUuid": "u1", "organizationName": "Org A" } }""");
            File.SetLastWriteTimeUtc(path, new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));

            // Real reader + real mtime stamp, without the recheck window (covered above) so the edit is seen at once.
            var provider = new OrgProvider(ClaudeJsonReader.ReadLiveOrg, OrgProvider.DefaultStamp);
            Assert.Equal("Org A", provider.GetLive(new ClaudeConfigDir(tmp))!.Name);

            File.WriteAllText(path, """{ "oauthAccount": { "organizationUuid": "u2", "organizationName": "Org B" } }""");
            File.SetLastWriteTimeUtc(path, new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc));
            Assert.Equal("Org B", provider.GetLive(new ClaudeConfigDir(tmp))!.Name);
        }
        finally
        {
            try { Directory.Delete(tmp, recursive: true); } catch { /* best effort */ }
        }
    }
}
