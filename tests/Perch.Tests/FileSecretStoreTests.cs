using Perch.Platform;
using Xunit;

namespace Perch.Tests;

/// <summary>
/// The portable secret-store mechanics (set / get / delete / overwrite / persistence), independent of the
/// per-OS protector — Windows layers DPAPI on top, but the file-and-dictionary contract is what everything
/// relies on. Uses the identity protector and a throwaway temp file so it runs on any host.
/// </summary>
public sealed class FileSecretStoreTests : IDisposable
{
    private readonly string _file =
        Path.Combine(Path.GetTempPath(), $"perch-secrets-{Guid.NewGuid():N}.json");

    private FileSecretStore NewStore() => new(protector: null, filePath: _file);

    [Fact]
    public void Get_missing_key_is_null()
    {
        Assert.Null(NewStore().Get("nope"));
    }

    [Fact]
    public void Set_then_get_round_trips()
    {
        var store = NewStore();
        store.Set("refresh_token", "abc123");
        Assert.Equal("abc123", store.Get("refresh_token"));
    }

    [Fact]
    public void Set_overwrites_existing_value()
    {
        var store = NewStore();
        store.Set("k", "first");
        store.Set("k", "second");
        Assert.Equal("second", store.Get("k"));
    }

    [Fact]
    public void Delete_removes_the_value()
    {
        var store = NewStore();
        store.Set("k", "v");
        store.Delete("k");
        Assert.Null(store.Get("k"));
    }

    [Fact]
    public void Values_persist_across_store_instances()
    {
        NewStore().Set("k", "durable");
        Assert.Equal("durable", NewStore().Get("k"));   // a fresh instance reads the same file
    }

    [Fact]
    public void Multiple_keys_are_independent()
    {
        var store = NewStore();
        store.Set("a", "1");
        store.Set("b", "2");
        store.Delete("a");
        Assert.Null(store.Get("a"));
        Assert.Equal("2", store.Get("b"));
    }

    public void Dispose()
    {
        try { if (File.Exists(_file)) File.Delete(_file); } catch { }
    }
}
