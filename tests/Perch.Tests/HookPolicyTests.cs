using Perch.Data;
using Xunit;

namespace Perch.Tests;

/// <summary>The pure <see cref="HookPolicy"/> that decides whether Perch's hooks are installed into a config
/// dir: the primary is always on; declared / self-reported default on (opt-out); an auto-discovered
/// (convention) dir defaults off (opt-in); an override list always wins.</summary>
public sealed class HookPolicyTests
{
    private static readonly string[] None = System.Array.Empty<string>();

    // With no overrides, the effective state is the provenance default (and the primary is always on).
    [Fact]
    public void Primary_defaults_on() =>
        Assert.True(HookPolicy.IsEnabled(ConfigDirProvenance.Primary, @"C:\dir", None, None));

    [Fact]
    public void Declared_defaults_on() =>
        Assert.True(HookPolicy.IsEnabled(ConfigDirProvenance.Declared, @"C:\dir", None, None));

    [Fact]
    public void SelfReported_defaults_on() =>
        Assert.True(HookPolicy.IsEnabled(ConfigDirProvenance.SelfReported, @"C:\dir", None, None));

    [Fact]
    public void Convention_defaults_off() =>
        Assert.False(HookPolicy.IsEnabled(ConfigDirProvenance.Convention, @"C:\dir", None, None));

    [Fact]
    public void Primary_ignores_the_override_lists()
    {
        // Even if a stray entry names the primary, it stays hooked.
        Assert.True(HookPolicy.IsEnabled(ConfigDirProvenance.Primary, @"C:\p", new[] { @"C:\p" }, None));
    }

    [Fact]
    public void Disabled_opts_a_default_on_dir_out()
    {
        Assert.False(HookPolicy.IsEnabled(ConfigDirProvenance.Declared, @"C:\d", new[] { @"C:\d" }, None));
    }

    [Fact]
    public void Enabled_opts_a_convention_dir_in()
    {
        Assert.True(HookPolicy.IsEnabled(ConfigDirProvenance.Convention, @"C:\c", None, new[] { @"C:\c" }));
    }

    [Fact]
    public void Enabled_wins_over_disabled_for_the_same_dir()
    {
        Assert.True(HookPolicy.IsEnabled(
            ConfigDirProvenance.Convention, @"C:\c", new[] { @"C:\c" }, new[] { @"C:\c" }));
    }

    [Fact]
    public void Matching_tolerates_trailing_separator()
    {
        Assert.False(HookPolicy.IsEnabled(ConfigDirProvenance.Declared, @"C:\d\", new[] { @"C:\d" }, None));
    }
}
