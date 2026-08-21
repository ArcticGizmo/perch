using Perch.Plugins;
using Xunit;

namespace Perch.Tests;

public class GitHubReleaseParserTests
{
    private const string ReleaseJson = """
        {
          "tag_name": "v1.2.0",
          "html_url": "https://github.com/o/r/releases/tag/v1.2.0",
          "assets": [
            { "name": "weather-1.2.0.zip", "browser_download_url": "https://x/weather.zip" },
            { "name": "SHA256SUMS.txt", "browser_download_url": "https://x/sums.txt" }
          ]
        }
        """;

    [Fact]
    public void Parses_tag_and_assets()
    {
        var r = GitHubReleaseParser.Parse(ReleaseJson);
        Assert.NotNull(r);
        Assert.Equal("v1.2.0", r!.TagName);
        Assert.Equal(2, r.Assets.Count);
        Assert.Equal("https://x/sums.txt", r.FindAsset("SHA256SUMS.txt")!.DownloadUrl);
    }

    [Fact]
    public void Finds_the_single_zip_payload()
    {
        var r = GitHubReleaseParser.Parse(ReleaseJson)!;
        Assert.Equal("weather-1.2.0.zip", r.FindPayloadZip()!.Name);
    }

    [Fact]
    public void Ambiguous_or_absent_zip_is_null()
    {
        var none = GitHubReleaseParser.Parse("""{"tag_name":"v1","assets":[]}""")!;
        Assert.Null(none.FindPayloadZip());

        var two = GitHubReleaseParser.Parse("""
            {"tag_name":"v1","assets":[
              {"name":"a.zip","browser_download_url":"u1"},
              {"name":"b.zip","browser_download_url":"u2"}]}
            """)!;
        Assert.Null(two.FindPayloadZip());
    }

    [Fact]
    public void Garbage_or_missing_tag_returns_null()
    {
        Assert.Null(GitHubReleaseParser.Parse("not json"));
        Assert.Null(GitHubReleaseParser.Parse("""{"assets":[]}"""));   // no tag_name
    }
}
