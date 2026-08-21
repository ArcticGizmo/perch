using System.Diagnostics;
using Perch.Plugins;
using Xunit;

namespace Perch.Tests;

/// <summary>
/// End-to-end coverage of the real out-of-process path — <see cref="ProcessPluginSandbox"/> launching an
/// actual PowerShell plugin, exchanging JSON over real stdio, enforced by <see cref="PluginService"/>. The
/// unit suite covers the pieces with in-memory fakes; this proves the pipe actually connects. Skipped when
/// <c>powershell</c> isn't on PATH so a non-Windows host doesn't fail the build.
/// </summary>
public sealed class PluginProcessIntegrationTests : IDisposable
{
    private readonly string _dir;
    private readonly bool _ready;

    public PluginProcessIntegrationTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "perch-plugin-it-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _ready = HasPowerShell();
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private static bool HasPowerShell()
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo("powershell", "-NoProfile -Command exit")
            { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true });
            if (p == null) return false;
            p.WaitForExit(10_000);
            return p.HasExited && p.ExitCode == 0;
        }
        catch { return false; }
    }

    // Writes a plugin folder with a manifest pointing at the given script body.
    private DiscoveredPlugin WritePlugin(string script, string[] extensionPoints, PluginCapabilities caps)
    {
        File.WriteAllText(Path.Combine(_dir, "plugin.ps1"), script);
        var m = new PluginManifest
        {
            Schema = 1, Id = "dev.test.it", Name = "IT", Version = "1.0.0",
            Entry = new PluginEntry
            {
                Type = "process",
                Command = "powershell",
                Args = ["-NoProfile", "-ExecutionPolicy", "Bypass", "-File", "plugin.ps1"],
                Mode = PluginEntryMode.OneShot,
            },
            ExtensionPoints = extensionPoints,
            Capabilities = caps,
        };
        return new DiscoveredPlugin(_dir, m, []);
    }

    [Fact]
    public async Task Real_powershell_plugin_round_trips_the_request_and_renders()
    {
        if (!_ready) return;

        // Reads the request line, echoes the Perch version it received back as the glyph text — proving both
        // directions of the pipe and the JSON (de)serialisation.
        const string script = """
            $line = [Console]::In.ReadLine()
            $req = $line | ConvertFrom-Json
            $out = '{"type":"render","glyph":{"glyph":"OK","text":"' + $req.perch + '"}}'
            [Console]::Out.WriteLine($out)
            """;

        var plugin = WritePlugin(script, [PluginExtensionPoints.OverlayGlyph, PluginExtensionPoints.Poll],
            new PluginCapabilities());
        var svc = new PluginService(new ProcessPluginSandbox(), "0.9.0", TimeSpan.FromSeconds(20));

        var result = await svc.PollAsync(plugin, PluginPollContext.Empty);

        Assert.True(result.Ok, $"timedOut={result.TimedOut} exit={result.ExitCode} denied={string.Join(",", result.DeniedActions)}");
        Assert.NotNull(result.Glyph);
        Assert.Equal("0.9.0", result.Glyph!.Text);
    }

    [Fact]
    public async Task Read_cwd_grant_delivers_the_directory_to_the_plugin()
    {
        if (!_ready) return;

        // Emits the cwd it was handed as the glyph text, built via ConvertTo-Json so backslashes in a
        // Windows path are escaped correctly (a naive string concat would emit invalid JSON).
        const string script = """
            $req = [Console]::In.ReadLine() | ConvertFrom-Json
            $cwd = if ($req.context.cwd) { $req.context.cwd } else { 'NONE' }
            $obj = @{ type = 'render'; glyph = @{ text = $cwd } }
            [Console]::Out.WriteLine(($obj | ConvertTo-Json -Compress))
            """;

        var plugin = WritePlugin(script, [PluginExtensionPoints.OverlayGlyph, PluginExtensionPoints.Poll],
            new PluginCapabilities { ReadCwd = true });
        var svc = new PluginService(new ProcessPluginSandbox(), "0.9.0", TimeSpan.FromSeconds(20));

        var result = await svc.PollAsync(plugin, new PluginPollContext(Cwd: @"C:\some\proj"));

        Assert.NotNull(result.Glyph);
        Assert.Equal(@"C:\some\proj", result.Glyph!.Text);  // last render wins
    }

    [Fact]
    public async Task A_unicode_glyph_survives_the_utf8_pipe()
    {
        if (!_ready) return;

        // Emit a sun glyph (U+2600) and a degree sign (U+00B0) as raw UTF-8 bytes, the way the weather
        // sample does — proving ProcessPluginSandbox's UTF-8 stdio survives non-ASCII output.
        const string script = """
            $obj = @{ type = 'render'; glyph = @{ glyph = [char]0x2600; text = ('7' + [char]0x00B0) } }
            $json = ($obj | ConvertTo-Json -Compress)
            $bytes = [System.Text.Encoding]::UTF8.GetBytes($json + "`n")
            $out = [Console]::OpenStandardOutput()
            $out.Write($bytes, 0, $bytes.Length); $out.Flush()
            """;

        var plugin = WritePlugin(script, [PluginExtensionPoints.OverlayGlyph, PluginExtensionPoints.Poll],
            new PluginCapabilities());
        var svc = new PluginService(new ProcessPluginSandbox(), "0.9.0", TimeSpan.FromSeconds(20));

        var result = await svc.PollAsync(plugin, PluginPollContext.Empty);

        Assert.NotNull(result.Glyph);
        Assert.Equal("☀", result.Glyph!.Glyph);
        Assert.Equal("7°", result.Glyph.Text);
    }

    [Fact]
    public async Task A_plugin_that_never_exits_is_killed_at_the_timeout()
    {
        if (!_ready) return;

        const string script = "Start-Sleep -Seconds 120";  // hang, never write, never exit
        var plugin = WritePlugin(script, [PluginExtensionPoints.Poll], new PluginCapabilities());
        var svc = new PluginService(new ProcessPluginSandbox(), "0.9.0", TimeSpan.FromSeconds(2));

        var result = await svc.PollAsync(plugin, PluginPollContext.Empty);

        Assert.True(result.TimedOut);
        Assert.Null(result.Glyph);
    }
}
