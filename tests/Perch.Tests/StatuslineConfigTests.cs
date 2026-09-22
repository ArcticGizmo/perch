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
    public void Load_merges_saved_user_profiles_after_the_code_builtins()
    {
        File.WriteAllText(_path,
            """{ "activeName": "mine", "profiles": [ { "name": "mine", "kind": "External", "command": "foo" } ] }""");
        var cfg = StatuslineStore.Load(_path);

        Assert.NotNull(cfg.Find(StatuslineDefaults.PerchDefaultName));   // built-in, from code
        Assert.NotNull(cfg.Find("mine"));                               // user profile, from the file
        Assert.Equal("mine", cfg.Active!.Name);
    }

    [Fact]
    public void A_new_code_builtin_appears_even_with_an_existing_saved_file()
    {
        // A file written before "Rate-aware verbose" existed in code (only an unrelated user profile).
        File.WriteAllText(_path,
            """{ "activeName": "mine", "profiles": [ { "name": "mine", "kind": "Perch", "template": "x" } ] }""");
        var cfg = StatuslineStore.Load(_path);

        // The code built-in shows up with no migration step.
        Assert.NotNull(cfg.Find("Rate-aware verbose"));
        Assert.NotNull(cfg.Find("mine"));
    }

    [Fact]
    public void Unedited_builtins_are_not_persisted_but_edits_are()
    {
        var cfg = StatuslineStore.Seeded();
        StatuslineStore.Save(_path, cfg);
        Assert.DoesNotContain("\"Name\"", File.ReadAllText(_path));   // nothing but ActiveName — built-ins live in code

        cfg.Find("Minimal")!.Template = "EDITED {{model.display_name}}";
        StatuslineStore.Save(_path, cfg);
        Assert.Contains("EDITED", File.ReadAllText(_path));           // an edited built-in is persisted as an override

        var loaded = StatuslineStore.Load(_path);
        Assert.Equal("EDITED {{model.display_name}}", loaded.Find("Minimal")!.Template);
        Assert.NotNull(loaded.Find(StatuslineDefaults.PerchDefaultName));   // the others still come from code
        Assert.NotNull(loaded.Find("Rate-aware verbose"));
    }

    // ── config-dir-targeted apply (multi config dir) ────────────────────────────────────
    [Fact]
    public void Apply_targets_a_specific_config_dirs_settings_and_script()
    {
        var dir = new Perch.Data.ClaudeConfigDir(_dir);
        var profile = new StatuslineProfile { Name = "t", Kind = ProfileKind.Perch, Template = "{{model.display_name}}" };

        Assert.True(StatuslineInstaller.Apply(profile, dir, out var scriptPath));

        // the generated script lands INSIDE this config dir, and its own settings.json points at it
        Assert.Equal(Path.Combine(_dir, "perch-statusline.mjs"), scriptPath);
        Assert.True(File.Exists(scriptPath));
        Assert.True(File.Exists(Path.Combine(_dir, "settings.json")));
        Assert.Equal(StatuslineScript.CommandFor(scriptPath!), StatuslineInstaller.CurrentCommand(dir));
    }

    [Fact]
    public void Apply_to_a_config_dir_writes_an_external_command_verbatim()
    {
        var dir = new Perch.Data.ClaudeConfigDir(_dir);
        var profile = new StatuslineProfile { Name = "ext", Kind = ProfileKind.External, Command = "npx ccstatusline" };

        Assert.True(StatuslineInstaller.Apply(profile, dir, out var scriptPath));
        Assert.Null(scriptPath);
        Assert.Equal("npx ccstatusline", StatuslineInstaller.CurrentCommand(dir));
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
