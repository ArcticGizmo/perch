using Perch.Data;
using Xunit;

namespace Perch.Tests;

/// <summary>The pure <see cref="HookPolicy"/> that decides whether Perch's hooks are installed into a config
/// dir: the primary is always on; an added dir defaults on and the opt-out list turns it off.</summary>
public sealed class HookPolicyTests
{
    private static readonly string[] None = System.Array.Empty<string>();

    [Fact]
    public void Primary_defaults_on() =>
        Assert.True(HookPolicy.IsEnabled(ConfigDirProvenance.Primary, @"C:\dir", None));

    [Fact]
    public void Declared_defaults_on() =>
        Assert.True(HookPolicy.IsEnabled(ConfigDirProvenance.Declared, @"C:\dir", None));

    [Fact]
    public void Primary_ignores_the_opt_out_list()
    {
        // Even if a stray entry names the primary, it stays hooked.
        Assert.True(HookPolicy.IsEnabled(ConfigDirProvenance.Primary, @"C:\p", new[] { @"C:\p" }));
    }

    [Fact]
    public void Disabled_opts_a_declared_dir_out()
    {
        Assert.False(HookPolicy.IsEnabled(ConfigDirProvenance.Declared, @"C:\d", new[] { @"C:\d" }));
    }

    [Fact]
    public void Matching_tolerates_trailing_separator()
    {
        var dir = Path.Combine(Path.GetTempPath(), "d");   // native separators: '\' isn't one on macOS
        Assert.False(HookPolicy.IsEnabled(ConfigDirProvenance.Declared, dir + Path.DirectorySeparatorChar, new[] { dir }));
    }
}
