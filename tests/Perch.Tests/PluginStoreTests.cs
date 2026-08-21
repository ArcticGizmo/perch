using Perch.Plugins;
using Xunit;

namespace Perch.Tests;

public class PluginStoreTests
{
    private static InstalledPluginRecord Rec(string id, bool enabled = true, string version = "1.0.0") =>
        new() { Id = id, Enabled = enabled, Version = version };

    [Fact]
    public void Upsert_adds_then_replaces_by_id()
    {
        var list = new List<InstalledPluginRecord>();
        PluginStore.Upsert(list, Rec("a", version: "1.0.0"));
        PluginStore.Upsert(list, Rec("b"));
        Assert.Equal(2, list.Count);

        PluginStore.Upsert(list, Rec("a", version: "2.0.0")); // update in place
        Assert.Equal(2, list.Count);
        Assert.Equal("2.0.0", PluginStore.Find(list, "a")!.Version);
    }

    [Fact]
    public void Remove_reports_whether_it_removed()
    {
        var list = new List<InstalledPluginRecord> { Rec("a") };
        Assert.True(PluginStore.Remove(list, "a"));
        Assert.False(PluginStore.Remove(list, "a"));
        Assert.Empty(list);
    }

    [Fact]
    public void SetEnabled_toggles_and_clears_faults_on_reenable()
    {
        var rec = Rec("a", enabled: false);
        rec.ConsecutiveFaults = 3;
        var list = new List<InstalledPluginRecord> { rec };

        Assert.True(PluginStore.SetEnabled(list, "a", true));
        Assert.True(rec.Enabled);
        Assert.Equal(0, rec.ConsecutiveFaults);

        Assert.False(PluginStore.SetEnabled(list, "a", true));   // already enabled → no change
        Assert.False(PluginStore.SetEnabled(list, "missing", true));
    }
}
