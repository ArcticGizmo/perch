using System;
using System.IO;
using System.Linq;
using Perch.Statusline;
using Xunit;

namespace Perch.Tests;

/// <summary>The statusline profile library: token-catalogue integrity against the sample payload, the
/// config round-trip through <see cref="StatuslineStore"/>, and profile bookkeeping.</summary>
public sealed class StatuslineConfigTests : IDisposable
{
    private readonly string _dir;
    private readonly string _path;

    public StatuslineConfigTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "perch-statusline-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _path = Path.Combine(_dir, "statusline.json");
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    // ── token catalogue ────────────────────────────────────────────────────────────────
    [Fact]
    public void Every_catalogue_token_resolves_against_the_sample()
    {
        var data = StatuslineSample.Data();
        foreach (var group in StatuslineTokens.Groups)
        foreach (var tok in group.Tokens)
            Assert.True(data.TryGet(tok.Path, out _), $"sample payload is missing {tok.Path}");
    }

    [Fact]
    public void All_named_colours_parse_back_to_a_non_default_role()
    {
        foreach (var name in StatusColors.Names)
            Assert.NotEqual(StatusColor.Default, StatusColors.Parse(name));
    }

    // ── store round-trip ────────────────────────────────────────────────────────────────
    [Fact]
    public void Load_of_a_missing_file_seeds_defaults()
    {
        var cfg = StatuslineStore.Load(_path);
        Assert.Contains(cfg.Profiles, p => p.Name == StatuslineDefaults.PerchDefaultName);
        Assert.Equal(StatuslineDefaults.PerchDefaultName, cfg.Active!.Name);
    }

    [Fact]
    public void Save_then_load_round_trips_profiles_and_active()
    {
        var cfg = StatuslineStore.Seeded();
        cfg.Upsert(new StatuslineProfile { Name = "ccstatusline", Kind = ProfileKind.External, Command = "npx ccstatusline" });
        cfg.ActiveName = "Minimal";
        Assert.True(StatuslineStore.Save(_path, cfg));

        var loaded = StatuslineStore.Load(_path);
        Assert.Equal("Minimal", loaded.Active!.Name);
        var ext = loaded.Find("ccstatusline");
        Assert.NotNull(ext);
        Assert.Equal(ProfileKind.External, ext!.Kind);
        Assert.Equal("npx ccstatusline", ext.Command);
    }

    [Fact]
    public void Kind_serialises_as_a_string()
    {
        var cfg = new StatuslineConfig();
        cfg.Upsert(new StatuslineProfile { Name = "x", Kind = ProfileKind.External, Command = "c" });
        StatuslineStore.Save(_path, cfg);
        Assert.Contains("\"External\"", File.ReadAllText(_path));
    }

    [Fact]
    public void Load_backfills_active_from_first_profile_when_unset()
    {
        File.WriteAllText(_path,
            """{ "profiles": [ { "name": "A", "kind": "Perch", "template": "{{model.display_name}}" } ] }""");
        var cfg = StatuslineStore.Load(_path);
        Assert.Equal("A", cfg.Active!.Name);
    }

    [Fact]
    public void Upsert_replaces_by_name_case_insensitively()
    {
        var cfg = new StatuslineConfig();
        cfg.Upsert(new StatuslineProfile { Name = "Minimal", Template = "a" });
        cfg.Upsert(new StatuslineProfile { Name = "minimal", Template = "b" });
        Assert.Single(cfg.Profiles);
        Assert.Equal("b", cfg.Profiles[0].Template);
    }
}
