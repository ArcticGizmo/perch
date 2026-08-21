using System.IO.Compression;
using System.Text;
using Perch.Plugins;
using Xunit;

namespace Perch.Tests;

public class PluginInstallerTests : IDisposable
{
    private readonly string _pluginsDir;

    public PluginInstallerTests()
    {
        _pluginsDir = Path.Combine(Path.GetTempPath(), "perch-install-test-" + Guid.NewGuid().ToString("N"));
    }

    public void Dispose()
    {
        try { Directory.Delete(_pluginsDir, recursive: true); } catch { }
    }

    private const string ValidManifest = """
        { "schema":1, "id":"dev.test.weather", "name":"Weather", "version":"1.0.0",
          "entry":{"type":"process","command":"powershell","args":["-File","w.ps1"]},
          "extensionPoints":["poll","overlay.glyph"],
          "capabilities":{"read.cwd":true} }
        """;

    // Builds a zip in memory from (entryName, content) pairs.
    private static byte[] Zip(params (string name, string content)[] entries)
    {
        using var ms = new MemoryStream();
        using (var z = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
            foreach (var (name, content) in entries)
            {
                var e = z.CreateEntry(name);
                using var w = new StreamWriter(e.Open());
                w.Write(content);
            }
        return ms.ToArray();
    }

    private PluginInstaller NewInstaller(IPluginDownloader? dl = null) =>
        new(dl ?? new FakePluginDownloader(), _pluginsDir);

    [Fact]
    public void Installs_a_verified_zip_and_places_it_by_id()
    {
        var zip = Zip(("perch-plugin.json", ValidManifest), ("w.ps1", "echo hi"));
        var hash = Sha256Sums.Hash(zip);

        var r = NewInstaller().InstallVerifiedZip("o/r", "v1.0.0", zip, hash, "weather.zip");

        Assert.True(r.Ok, r.Error);
        Assert.Equal("dev.test.weather", r.Record!.Id);
        Assert.Equal(hash, r.Record.AssetSha256);
        Assert.False(r.Record.Enabled);                       // pending consent
        Assert.Empty(r.Record.GrantedCapabilities);
        Assert.True(File.Exists(Path.Combine(_pluginsDir, "dev.test.weather", "perch-plugin.json")));
        Assert.True(File.Exists(Path.Combine(_pluginsDir, "dev.test.weather", "w.ps1")));
    }

    [Fact]
    public void Refuses_a_checksum_mismatch_and_writes_nothing()
    {
        var zip = Zip(("perch-plugin.json", ValidManifest));
        var wrong = new string('0', 64);

        var r = NewInstaller().InstallVerifiedZip("o/r", "v1", zip, wrong, "weather.zip");

        Assert.False(r.Ok);
        Assert.Contains("checksum mismatch", r.Error);
        Assert.False(Directory.Exists(Path.Combine(_pluginsDir, "dev.test.weather")));
    }

    [Fact]
    public void Refuses_a_zip_slip_entry()
    {
        // An entry whose name climbs out of the extraction directory.
        var zip = Zip(("perch-plugin.json", ValidManifest), ("../escape.txt", "pwned"));
        var hash = Sha256Sums.Hash(zip);

        var r = NewInstaller().InstallVerifiedZip("o/r", "v1", zip, hash, "weather.zip");

        Assert.False(r.Ok);
        Assert.Contains("zip-slip", r.Error);
    }

    [Fact]
    public void Resolves_a_manifest_nested_in_a_single_top_level_folder()
    {
        var zip = Zip(("weather-1.0.0/perch-plugin.json", ValidManifest), ("weather-1.0.0/w.ps1", "x"));
        var hash = Sha256Sums.Hash(zip);

        var r = NewInstaller().InstallVerifiedZip("o/r", "v1", zip, hash, "weather.zip");

        Assert.True(r.Ok, r.Error);
        Assert.True(File.Exists(Path.Combine(_pluginsDir, "dev.test.weather", "perch-plugin.json")));
    }

    [Fact]
    public void Rejects_a_zip_with_an_invalid_manifest()
    {
        var zip = Zip(("perch-plugin.json", """{ "schema":1, "id":"BAD ID" }"""));
        var hash = Sha256Sums.Hash(zip);

        var r = NewInstaller().InstallVerifiedZip("o/r", "v1", zip, hash, "weather.zip");

        Assert.False(r.Ok);
        Assert.Contains("invalid manifest", r.Error);
    }

    [Fact]
    public void Rejects_a_zip_with_no_manifest()
    {
        var zip = Zip(("readme.txt", "nothing here"));
        var hash = Sha256Sums.Hash(zip);

        var r = NewInstaller().InstallVerifiedZip("o/r", "v1", zip, hash, "weather.zip");

        Assert.False(r.Ok);
        Assert.Contains("does not contain", r.Error);
    }

    [Fact]
    public void Installs_from_a_local_directory_leaving_the_source_intact()
    {
        var src = Path.Combine(Path.GetTempPath(), "perch-sideload-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(src);
        File.WriteAllText(Path.Combine(src, "perch-plugin.json"), ValidManifest);
        File.WriteAllText(Path.Combine(src, "w.ps1"), "echo hi");
        try
        {
            var r = NewInstaller().InstallFromDirectory(src);

            Assert.True(r.Ok, r.Error);
            Assert.Equal("(local)", r.Record!.Source);
            Assert.Equal("dev.test.weather", r.Record.Id);
            Assert.False(r.Record.Enabled);
            Assert.True(File.Exists(Path.Combine(_pluginsDir, "dev.test.weather", "w.ps1")));
            Assert.True(File.Exists(Path.Combine(src, "perch-plugin.json"))); // source untouched
        }
        finally { try { Directory.Delete(src, recursive: true); } catch { } }
    }

    [Fact]
    public void Local_install_rejects_a_folder_without_a_manifest()
    {
        var src = Path.Combine(Path.GetTempPath(), "perch-sideload-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(src);
        try
        {
            var r = NewInstaller().InstallFromDirectory(src);
            Assert.False(r.Ok);
            Assert.Contains("no perch-plugin.json", r.Error);
        }
        finally { try { Directory.Delete(src, recursive: true); } catch { } }
    }

    [Fact]
    public async Task Full_network_path_resolves_verifies_and_installs()
    {
        var zip = Zip(("perch-plugin.json", ValidManifest), ("w.ps1", "x"));
        var hash = Sha256Sums.Hash(zip);
        var sums = $"{hash}  weather-1.0.0.zip\n";

        var dl = new FakePluginDownloader();
        var source = PluginInstallSource.TryParse("owner/weather")!;
        dl.Texts[source.ReleaseApiUrl] = """
            {
              "tag_name": "v1.0.0",
              "html_url": "https://github.com/owner/weather/releases/tag/v1.0.0",
              "assets": [
                { "name": "weather-1.0.0.zip", "browser_download_url": "https://dl/weather.zip" },
                { "name": "SHA256SUMS.txt", "browser_download_url": "https://dl/sums.txt" }
              ]
            }
            """;
        dl.Bytes["https://dl/sums.txt"] = Encoding.UTF8.GetBytes(sums);
        dl.Bytes["https://dl/weather.zip"] = zip;

        var r = await NewInstaller(dl).InstallAsync(source);

        Assert.True(r.Ok, r.Error);
        Assert.Equal("dev.test.weather", r.Record!.Id);
        Assert.Equal("v1.0.0", r.Record.Tag);
        Assert.Equal("owner/weather", r.Record.Source);
    }

    [Fact]
    public async Task Full_network_path_fails_cleanly_on_a_tampered_payload()
    {
        var realZip = Zip(("perch-plugin.json", ValidManifest));
        var tamperedZip = Zip(("perch-plugin.json", ValidManifest), ("extra.txt", "injected"));
        var honestHash = Sha256Sums.Hash(realZip);           // sums advertise the real zip...
        var sums = $"{honestHash}  weather-1.0.0.zip\n";

        var dl = new FakePluginDownloader();
        var source = PluginInstallSource.TryParse("owner/weather")!;
        dl.Texts[source.ReleaseApiUrl] = """
            {"tag_name":"v1.0.0","assets":[
              {"name":"weather-1.0.0.zip","browser_download_url":"https://dl/weather.zip"},
              {"name":"SHA256SUMS.txt","browser_download_url":"https://dl/sums.txt"}]}
            """;
        dl.Bytes["https://dl/sums.txt"] = Encoding.UTF8.GetBytes(sums);
        dl.Bytes["https://dl/weather.zip"] = tamperedZip;    // ...but the bytes served are different

        var r = await NewInstaller(dl).InstallAsync(source);

        Assert.False(r.Ok);
        Assert.Contains("checksum mismatch", r.Error);
    }
}
