using System.Text.Json;
using Perch.Data;
using Xunit;

namespace Perch.Tests;

public class QuickLinkTests
{
    // Settings files written before the upside-down easter egg was removed still carry its key; they must load
    // whole (not fall through to the salvage path) and keep their links.
    private const string FileWithRetiredKey = """
        {
          "QuickLinks": [ { "Name": "Slack", "ExePath": "", "Enabled": true } ],
          "UpsideDownQuickLinks": true
        }
        """;

    [Fact]
    public void RetiredUpsideDownKey_StillLoads()
    {
        var s = JsonSerializer.Deserialize<AppSettings>(FileWithRetiredKey);
        Assert.NotNull(s);
        var link = Assert.Single(s!.QuickLinks!);
        Assert.Equal("Slack", link.Name);
    }

    [Fact]
    public void RetiredUpsideDownKey_SalvageIgnoresIt()
    {
        var s = AppSettings.SalvageMerge(FileWithRetiredKey);
        Assert.Equal("Slack", Assert.Single(s.QuickLinks!).Name);
    }

    // ── Kinds: persistence ──────────────────────────────────────────────────────

    [Fact]
    public void LinkWithoutKind_ReadsAsApp()
    {
        var s = JsonSerializer.Deserialize<AppSettings>(FileWithRetiredKey)!;
        var link = Assert.Single(s.QuickLinks!);
        Assert.Equal(QuickLinkKind.App, link.Kind);
        Assert.Equal("", link.Target);
    }

    [Theory]
    [InlineData("App", "", "C:\\Tools\\tool.exe")]
    [InlineData("File", "C:\\Docs\\plan.pdf", "")]
    [InlineData("Url", "https://example.com/", "")]
    public void EachKind_RoundTrips(string kindName, string target, string exe)
    {
        var kind = Enum.Parse<QuickLinkKind>(kindName);
        var s = new AppSettings
        {
            QuickLinks = [new QuickLink { Name = "x", Kind = kind, Target = target, ExePath = exe, Enabled = false }],
        };
        var back = JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(s))!;
        var link = Assert.Single(back.QuickLinks!);
        Assert.Equal(kind, link.Kind);
        Assert.Equal(target, link.Target);
        Assert.Equal(exe, link.ExePath);
        Assert.False(link.Enabled);
    }

    [Fact]
    public void Kind_IsWrittenAsAString()
    {
        var json = JsonSerializer.Serialize(new QuickLink { Kind = QuickLinkKind.Url });
        Assert.Contains("\"Kind\":\"Url\"", json);
    }

    // A file from a newer build with a kind this one doesn't know degrades that link to App, not the whole file.
    [Fact]
    public void UnknownKind_DegradesToApp()
    {
        var s = JsonSerializer.Deserialize<AppSettings>(
            """{ "QuickLinks": [ { "Name": "x", "Kind": "Hologram" } ], "ShowUsage": false }""")!;
        Assert.Equal(QuickLinkKind.App, Assert.Single(s.QuickLinks!).Kind);
        Assert.False(s.ShowUsage);
    }

    [Fact]
    public void Clone_CopiesEveryField()
    {
        var link = new QuickLink { Name = "n", Kind = QuickLinkKind.File, ExePath = "e", Target = "t", Enabled = false };
        var copy = link.Clone();
        Assert.NotSame(link, copy);
        Assert.Equal(JsonSerializer.Serialize(link), JsonSerializer.Serialize(copy));
    }

    // ── Kinds: routing ──────────────────────────────────────────────────────────

    private static QuickLinkAction Route(QuickLink link, bool middle = false, bool isDir = false) =>
        QuickLinkRouting.Resolve(link, middle, _ => isDir);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void App_FocusesOrLaunches_ForEitherButton(bool middle)
    {
        var action = Route(new QuickLink { Name = "Slack" }, middle);
        Assert.Equal(QuickLinkActionKind.FocusOrLaunchApp, action.Kind);
    }

    [Fact]
    public void File_OpensTheTrimmedPath()
    {
        var action = Route(new QuickLink { Kind = QuickLinkKind.File, Target = "  C:\\Docs\\plan.pdf " });
        Assert.Equal(new QuickLinkAction(QuickLinkActionKind.OpenFile, "C:\\Docs\\plan.pdf"), action);
    }

    [Fact]
    public void File_ThatIsADirectory_OpensAsAFolder()
    {
        var action = Route(new QuickLink { Kind = QuickLinkKind.File, Target = "C:\\Docs" }, isDir: true);
        Assert.Equal(QuickLinkActionKind.OpenFolder, action.Kind);
    }

    [Fact]
    public void File_MiddleClick_IsAPlainOpen()
    {
        var action = Route(new QuickLink { Kind = QuickLinkKind.File, Target = "C:\\a.png" }, middle: true);
        Assert.Equal(QuickLinkActionKind.OpenFile, action.Kind);
    }

    [Fact]
    public void File_Blank_DoesNothing()
    {
        Assert.Equal(QuickLinkActionKind.None, Route(new QuickLink { Kind = QuickLinkKind.File, Target = "  " }).Kind);
    }

    [Fact]
    public void Url_LeftOpens_MiddleOpensANewWindow()
    {
        var link = new QuickLink { Kind = QuickLinkKind.Url, Target = "https://example.com/a" };
        Assert.Equal(new QuickLinkAction(QuickLinkActionKind.OpenUrl, "https://example.com/a"), Route(link));
        Assert.Equal(new QuickLinkAction(QuickLinkActionKind.OpenUrlInNewWindow, "https://example.com/a"), Route(link, middle: true));
    }

    // A hand-edited settings file must not turn a web link into a shell launch.
    [Theory]
    [InlineData("file:///C:/Windows/System32/calc.exe")]
    [InlineData("javascript:alert(1)")]
    [InlineData("ms-settings:privacy")]
    [InlineData("mailto:someone@example.com")]
    [InlineData("C:\\Windows\\System32\\calc.exe")]
    [InlineData("\\\\server\\share\\x.exe")]
    [InlineData("")]
    public void Url_WithAnythingButHttp_DoesNothing(string target)
    {
        Assert.Equal(QuickLinkActionKind.None, Route(new QuickLink { Kind = QuickLinkKind.Url, Target = target }).Kind);
    }

    [Theory]
    [InlineData("https://example.com", "https://example.com/")]
    [InlineData("http://example.com/a?b=c#d", "http://example.com/a?b=c#d")]
    [InlineData("  example.com/path ", "https://example.com/path")]
    [InlineData("localhost:3000", "https://localhost:3000/")]
    [InlineData("intranet.local:8080/x", "https://intranet.local:8080/x")]
    public void NormalizeUrl_AcceptsWebAddresses(string input, string expected)
    {
        Assert.Equal(expected, QuickLinkRouting.NormalizeUrl(input));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("   ")]
    [InlineData("notes")]
    [InlineData("javascript:alert(1)")]
    [InlineData("mailto:a@b.com")]
    [InlineData("ftp://example.com")]
    [InlineData("C:\\x\\y.txt")]
    [InlineData("/etc/hosts")]
    [InlineData("example.com:abc")]
    public void NormalizeUrl_RefusesEverythingElse(string? input)
    {
        Assert.Null(QuickLinkRouting.NormalizeUrl(input));
    }

    // ── File-link marks ─────────────────────────────────────────────────────────

    [Theory]
    [InlineData(@"C:\files\office-layout.png", "Image")]
    [InlineData(@"C:\files\Photo.JPEG", "Image")]
    [InlineData(@"C:\files\scan.webp ", "Image")]
    [InlineData(@"C:\work\plan.pdf", "Document")]
    [InlineData(@"C:\work\notes", "Document")]
    [InlineData(@"C:\work\archive.tar.gz", "Document")]
    public void FileLink_GetsImageOrPageMark(string target, string expected)
    {
        var link = new QuickLink { Kind = QuickLinkKind.File, Target = target };
        Assert.Equal(Enum.Parse<QuickLinkGlyph>(expected), QuickLinkGlyphs.For(link, isDirectory: false));
    }

    [Fact]
    public void FileLink_ToADirectory_GetsTheFolderMark_EvenWithADotInItsName()
    {
        var link = new QuickLink { Kind = QuickLinkKind.File, Target = @"C:\work\site.assets.png" };
        Assert.Equal(QuickLinkGlyph.Folder, QuickLinkGlyphs.For(link, isDirectory: true));
    }

    [Theory]
    [InlineData("App", @"C:\x.png")]
    [InlineData("Url", "https://example.com/x.png")]
    [InlineData("File", "   ")]
    public void OnlyANonBlankFileLink_GetsAMark(string kind, string target)
    {
        var link = new QuickLink { Kind = Enum.Parse<QuickLinkKind>(kind), Target = target, ExePath = target };
        Assert.Equal(QuickLinkGlyph.None, QuickLinkGlyphs.For(link, isDirectory: false));
    }

    // ── Favicons ────────────────────────────────────────────────────────────────

    // A PNG header: enough for FeedIcon.Validate.
    private static readonly byte[] TinyPng = FeedTestSupport.Png();

    private sealed class FaviconRig : IDisposable
    {
        public readonly string Dir = Path.Combine(Path.GetTempPath(), "perch-favicon-" + Guid.NewGuid().ToString("N"));
        public DateTime Now = new(2026, 11, 27, 12, 0, 0, DateTimeKind.Utc);
        public byte[]? Response = TinyPng;
        public readonly List<(Uri Url, string? PrivateHost)> Calls = [];
        public QuickLinkFavicons Favicons;

        public FaviconRig()
        {
            Favicons = new QuickLinkFavicons((u, host, _) =>
            {
                Calls.Add((u, host));
                return Task.FromResult<byte[]?>(Response);
            }, Dir, () => Now);
        }

        public void Dispose()
        {
            try { Directory.Delete(Dir, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task Favicon_FetchesTheSiteIconOnce_AndAllowsOnlyTheLinksOwnHost()
    {
        using var rig = new FaviconRig();
        var file = await rig.Favicons.GetAsync("https://example.com/some/page");

        Assert.NotNull(file);
        Assert.EndsWith(".png", file);
        Assert.Equal(TinyPng, File.ReadAllBytes(file!));
        var call = Assert.Single(rig.Calls);
        Assert.Equal("https://example.com/favicon.ico", call.Url.AbsoluteUri);
        Assert.Equal("example.com", call.PrivateHost);

        // Another page on the same site, a day later: served from the cache.
        File.SetLastWriteTimeUtc(file!, rig.Now);
        rig.Now = rig.Now.AddDays(1);
        Assert.Equal(file, await rig.Favicons.GetAsync("https://example.com/other"));
        Assert.Single(rig.Calls);
    }

    [Fact]
    public async Task Favicon_RefetchesAfterAWeek()
    {
        using var rig = new FaviconRig();
        var file = await rig.Favicons.GetAsync("https://example.com");
        File.SetLastWriteTimeUtc(file!, rig.Now);
        rig.Now = rig.Now.AddDays(8);
        await rig.Favicons.GetAsync("https://example.com");
        Assert.Equal(2, rig.Calls.Count);
    }

    [Fact]
    public async Task Favicon_AFailedFetch_IsNotRetriedUntilTheRefresh()
    {
        using var rig = new FaviconRig { Response = null };
        Assert.Null(await rig.Favicons.GetAsync("https://example.com"));
        Assert.Null(await rig.Favicons.GetAsync("https://example.com"));
        Assert.Single(rig.Calls);

        rig.Now = rig.Now.AddDays(8);
        rig.Response = TinyPng;
        Assert.NotNull(await rig.Favicons.GetAsync("https://example.com"));
        Assert.Equal(2, rig.Calls.Count);
    }

    [Fact]
    public async Task Favicon_KeepsTheStaleIconWhenARefreshFails()
    {
        using var rig = new FaviconRig();
        var file = await rig.Favicons.GetAsync("https://example.com");
        File.SetLastWriteTimeUtc(file!, rig.Now);
        rig.Now = rig.Now.AddDays(8);
        rig.Response = null;
        Assert.Equal(file, await rig.Favicons.GetAsync("https://example.com"));
    }

    [Fact]
    public async Task Favicon_RefusesBytesThatArentAnImage()
    {
        using var rig = new FaviconRig { Response = "<html>not an icon</html>"u8.ToArray() };
        Assert.Null(await rig.Favicons.GetAsync("https://example.com"));
        Assert.False(Directory.Exists(rig.Dir) && Directory.EnumerateFiles(rig.Dir).Any());
    }

    [Fact]
    public async Task Favicon_IgnoresNonWebLinks()
    {
        using var rig = new FaviconRig();
        Assert.Null(await rig.Favicons.GetAsync("file:///C:/x.png"));
        Assert.Empty(rig.Calls);
    }

    // ── Custom icons ────────────────────────────────────────────────────────────

    private sealed class IconDir : IDisposable
    {
        public readonly string Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "perch-qlicons-" + Guid.NewGuid().ToString("N"));
        public IconDir() => Directory.CreateDirectory(Path);
        public string File(string name) => System.IO.Path.Combine(Path, name);
        public void Dispose()
        {
            try { Directory.Delete(Path, recursive: true); } catch { }
        }
    }

    [Fact]
    public void CustomIcon_RoundTrips_AndIsAbsentByDefault()
    {
        Assert.Null(new QuickLink().CustomIcon);
        var name = QuickLinkIcons.NewName();
        var s = new AppSettings { QuickLinks = [new QuickLink { Name = "x", CustomIcon = name }] };
        var back = JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(s))!;
        Assert.Equal(name, Assert.Single(back.QuickLinks!).CustomIcon);
    }

    [Fact]
    public void NewName_IsValid()
    {
        Assert.True(QuickLinkIcons.IsValidName(QuickLinkIcons.NewName()));
    }

    // A hand-edited settings file can't point the icon loader (or the deleter) anywhere else.
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("icon.png")]
    [InlineData("..\\settings.json")]
    [InlineData("../0123456789abcdef0123456789abcdef.png")]
    [InlineData("C:\\Windows\\0123456789abcdef0123456789abcdef.png")]
    [InlineData("0123456789ABCDEF0123456789ABCDEF.png")]
    [InlineData("0123456789abcdef0123456789abcdef.png.exe")]
    [InlineData("0123456789abcdef0123456789abcdef.svg")]
    public void IsValidName_RefusesAnythingButOurOwnNames(string? name)
    {
        Assert.False(QuickLinkIcons.IsValidName(name));
    }

    [Fact]
    public void Save_ThenPathFor_FindsIt_AndDeleteRemovesIt()
    {
        using var dir = new IconDir();
        var name = QuickLinkIcons.Save(TinyPng, dir.Path);
        Assert.NotNull(name);
        var path = QuickLinkIcons.PathFor(name, dir.Path);
        Assert.Equal(dir.File(name!), path);
        Assert.Equal(TinyPng, File.ReadAllBytes(path!));

        QuickLinkIcons.Delete(name, dir.Path);
        Assert.Null(QuickLinkIcons.PathFor(name, dir.Path));
    }

    // A link whose icon file has gone falls back to the kind's own icon rather than showing nothing.
    [Fact]
    public void PathFor_MissingFile_IsNull()
    {
        using var dir = new IconDir();
        Assert.Null(QuickLinkIcons.PathFor(QuickLinkIcons.NewName(), dir.Path));
    }

    [Fact]
    public void Delete_IgnoresNamesThatArentOurs()
    {
        using var dir = new IconDir();
        File.WriteAllText(dir.File("keep.png"), "x");
        QuickLinkIcons.Delete("keep.png", dir.Path);
        Assert.True(File.Exists(dir.File("keep.png")));
    }

    [Fact]
    public void Sweep_DeletesOnlyUnreferencedIconsOfOurs()
    {
        using var dir = new IconDir();
        var used = QuickLinkIcons.Save(TinyPng, dir.Path)!;
        var orphan = QuickLinkIcons.Save(TinyPng, dir.Path)!;
        File.WriteAllText(dir.File("notes.png"), "not ours");
        File.WriteAllText(dir.File("readme.txt"), "not ours");
        Directory.CreateDirectory(dir.File(QuickLinkIcons.NewName()));   // a folder named like an icon

        int deleted = QuickLinkIcons.Sweep([used, null, "junk"], dir.Path);

        Assert.Equal(1, deleted);
        Assert.True(File.Exists(dir.File(used)));
        Assert.False(File.Exists(dir.File(orphan)));
        Assert.True(File.Exists(dir.File("notes.png")));
        Assert.True(File.Exists(dir.File("readme.txt")));
        Assert.Single(Directory.GetDirectories(dir.Path));
    }

    [Fact]
    public void Sweep_MissingDirectory_IsANoOp()
    {
        Assert.Equal(0, QuickLinkIcons.Sweep([], Path.Combine(Path.GetTempPath(), "perch-none-" + Guid.NewGuid().ToString("N"))));
    }

    // The sweep deletes by what settings don't mention, so it must only trust a cleanly read file.
    [Fact]
    public void LoadedCleanly_IsTrueForFreshSettings()
    {
        Assert.True(new AppSettings().LoadedCleanly);
    }

    [Fact]
    public void Favicon_OneIconPerSite()
    {
        Assert.Equal(QuickLinkFavicons.Stem(new Uri("https://Example.com/a")), QuickLinkFavicons.Stem(new Uri("https://example.com/b")));
        Assert.NotEqual(QuickLinkFavicons.Stem(new Uri("https://example.com")), QuickLinkFavicons.Stem(new Uri("http://example.com")));
        Assert.NotEqual(QuickLinkFavicons.Stem(new Uri("https://example.com")), QuickLinkFavicons.Stem(new Uri("https://example.com:8443")));
    }
}
