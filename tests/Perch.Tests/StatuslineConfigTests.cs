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
    public void Builtins_are_never_persisted_even_when_mutated_or_active()
    {
        var cfg = StatuslineStore.Seeded();
        StatuslineStore.Save(_path, cfg);
        var json = File.ReadAllText(_path);
        Assert.DoesNotContain("\"Name\"", json);       // no profile content — built-ins live in code
        Assert.DoesNotContain("\"Template\"", json);   // and the active built-in's template is not dumped either

        // Code-first: a built-in is read-only, so even a direct in-memory mutation must not reach disk, and on
        // reload the template comes back from code unchanged.
        cfg.Find("Minimal")!.Template = "EDITED {{model.display_name}}";
        StatuslineStore.Save(_path, cfg);
        Assert.DoesNotContain("EDITED", File.ReadAllText(_path));

        var loaded = StatuslineStore.Load(_path);
        Assert.NotEqual("EDITED {{model.display_name}}", loaded.Find("Minimal")!.Template);
        Assert.NotNull(loaded.Find(StatuslineDefaults.PerchDefaultName));
        Assert.NotNull(loaded.Find("Rate-aware verbose"));
    }

    [Fact]
    public void A_stale_saved_builtin_override_is_ignored_and_the_code_version_wins()
    {
        // A legacy file that saved a built-in ("Rate-aware verbose") verbatim — with its soft breaks stripped.
        // It must NOT shadow the code built-in; the code template (with soft breaks) always wins.
        File.WriteAllText(_path,
            """{ "activeName": "Rate-aware verbose", "profiles": [ { "name": "Rate-aware verbose", "kind": "Perch", "template": "collapsed no soft breaks" } ] }""");

        var loaded = StatuslineStore.Load(_path);
        var codeTemplate = StatuslineDefaults.All.First(p => p.Name == "Rate-aware verbose").Template;
        Assert.Equal(codeTemplate, loaded.Find("Rate-aware verbose")!.Template);   // from code, not the stale disk copy

        // And re-saving drops the stale entry entirely.
        StatuslineStore.Save(_path, loaded);
        Assert.DoesNotContain("collapsed no soft breaks", File.ReadAllText(_path));
        Assert.DoesNotContain("\"Name\"", File.ReadAllText(_path));
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
    public void ProfileNameFromScript_reads_the_baked_in_header_name()
    {
        var script = StatuslineScript.Generate(new StatuslineProfile { Name = "My Status Line", Template = "{{model.display_name}}" });
        var path = Path.Combine(_dir, "s.mjs");
        File.WriteAllText(path, script);

        Assert.Equal("My Status Line", StatuslineScript.ProfileNameFromScript(path));
        Assert.Null(StatuslineScript.ProfileNameFromScript(Path.Combine(_dir, "does-not-exist.mjs")));
        File.WriteAllText(path, "// not a perch script");
        Assert.Null(StatuslineScript.ProfileNameFromScript(path));
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
