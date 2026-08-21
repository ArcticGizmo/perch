using Perch.Plugins;
using Xunit;

namespace Perch.Tests;

public class PluginInstallSourceTests
{
    [Fact]
    public void Parses_owner_repo_as_latest()
    {
        var s = PluginInstallSource.TryParse("ArcticGizmo/perch-weather");
        Assert.NotNull(s);
        Assert.Equal("ArcticGizmo", s!.Owner);
        Assert.Equal("perch-weather", s.Repo);
        Assert.Null(s.Tag);
        Assert.EndsWith("/releases/latest", s.ReleaseApiUrl);
    }

    [Theory]
    [InlineData("owner/repo@1.2.3", "v1.2.3")]
    [InlineData("owner/repo@v1.2.3", "v1.2.3")]
    [InlineData("owner/repo@2.0", "v2.0")]
    public void Parses_a_pinned_version_and_v_prefixes_the_tag(string reference, string expectedTag)
    {
        var s = PluginInstallSource.TryParse(reference);
        Assert.NotNull(s);
        Assert.Equal(expectedTag, s!.Tag);
        Assert.EndsWith($"/releases/tags/{expectedTag}", s.ReleaseApiUrl);
    }

    [Theory]
    [InlineData("")]
    [InlineData("no-slash")]
    [InlineData("/repo")]
    [InlineData("owner/")]
    [InlineData("owner/repo/extra")]
    [InlineData("../etc/passwd")]
    [InlineData("owner/repo@notaversion")]
    public void Rejects_malformed_references(string reference)
    {
        Assert.Null(PluginInstallSource.TryParse(reference));
    }
}
