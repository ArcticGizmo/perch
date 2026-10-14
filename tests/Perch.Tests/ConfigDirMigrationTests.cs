using Perch.Data;
using Xunit;

namespace Perch.Tests;

/// <summary>
/// The one-time <see cref="ConfigDirMigration"/> from auto-discovered config dirs to an explicit list: every
/// dir the old discovery showed becomes declared (so an update drops nothing), a removed (hidden) one doesn't,
/// hooks state carries over, and it runs once.
/// </summary>
public sealed class ConfigDirMigrationTests
{
    private static ConfigDirSuggestion Found(string root, ConfigDirSuggestionSource source) =>
        new(root, root, Path.GetFileName(root), source);

    private static readonly string Scanned = Path.Combine(Path.GetTempPath(), "home", ".claude-work");
    private static readonly string Reported = Path.Combine(Path.GetTempPath(), "envs", "acme");

    [Fact]
    public void Declares_found_dirs_and_marks_explicit()
    {
        var s = new AppSettings();

        Assert.True(ConfigDirMigration.Apply(s, new[]
        {
            Found(Scanned, ConfigDirSuggestionSource.HomeFolder),
            Found(Reported, ConfigDirSuggestionSource.ReportedBySession),
        }));

        Assert.Equal(new[] { Scanned, Reported }, s.DeclaredConfigDirs);
        Assert.True(s.ConfigDirsExplicit);
    }

    [Fact]
    public void Keeps_hooks_state_scanned_off_reported_on()
    {
        // A scanned dir wasn't hooked by default, so it lands opted out; a session-reported one was, so it doesn't.
        var s = new AppSettings();

        ConfigDirMigration.Apply(s, new[]
        {
            Found(Scanned, ConfigDirSuggestionSource.HomeFolder),
            Found(Reported, ConfigDirSuggestionSource.ReportedBySession),
        });

        Assert.Equal(new[] { Scanned }, s.HooksDisabledDirs);
    }

    [Fact]
    public void A_scanned_dir_the_user_opted_in_stays_hooked()
    {
        var s = new AppSettings { HooksEnabledDirs = new() { Scanned } };

        ConfigDirMigration.Apply(s, new[] { Found(Scanned, ConfigDirSuggestionSource.HomeFolder) });

        Assert.Null(s.HooksDisabledDirs);
        Assert.Null(s.HooksEnabledDirs);   // the legacy opt-in list is cleared
    }

    [Fact]
    public void A_hidden_dir_is_not_carried_over_even_with_a_stray_trailing_space()
    {
        // The real-world case: the hidden entry was recorded as "…\incontrol " while the scan finds "…\incontrol".
        if (!OperatingSystem.IsWindows()) return;
        var s = new AppSettings { HiddenConfigDirs = new() { Reported + " " } };

        ConfigDirMigration.Apply(s, new[] { Found(Reported, ConfigDirSuggestionSource.ReportedBySession) });

        Assert.Null(s.DeclaredConfigDirs);
        Assert.Null(s.HiddenConfigDirs);   // the legacy hidden list is cleared
    }

    [Fact]
    public void An_already_declared_dir_keeps_its_hooks()
    {
        // Declared dirs were always hooked by default; finding one again in the scan mustn't opt it out.
        var s = new AppSettings { DeclaredConfigDirs = new() { Scanned } };

        ConfigDirMigration.Apply(s, new[] { Found(Scanned, ConfigDirSuggestionSource.HomeFolder) });

        Assert.Equal(new[] { Scanned }, s.DeclaredConfigDirs);
        Assert.Null(s.HooksDisabledDirs);
    }

    [Fact]
    public void Runs_once()
    {
        var s = new AppSettings { ConfigDirsExplicit = true };

        Assert.False(ConfigDirMigration.Apply(s, new[] { Found(Scanned, ConfigDirSuggestionSource.HomeFolder) }));
        Assert.Null(s.DeclaredConfigDirs);
    }
}
