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
}
