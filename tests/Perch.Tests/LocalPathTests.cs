using Perch.Data;
using Perch.Statusline;
using Xunit;

namespace Perch.Tests;

/// <summary>
/// <see cref="LocalPath"/> / <see cref="FileRefResolver"/> are the CP9 fix (docs/review-fixes-plan.md): a path that
/// came from a transcript or a repo's <c>.git</c> file is never probed if it's UNC, a device path, or on a network
/// drive — on Windows the probe alone opens an SMB connection (the user's NTLM hash to the attacker, and a UI stall
/// for the timeout). Every UNC host here is unresolvable (<c>.invalid</c>) and, more to the point, is asserted to be
/// refused <em>without</em> a probe.
/// </summary>
public class LocalPathTests
{
    [Theory]
    [InlineData(@"\\perch-cp9.invalid\s\a.md")]
    [InlineData("//perch-cp9.invalid/s/a.md")]
    [InlineData(@"\/perch-cp9.invalid/s/a.md")]
    [InlineData(@"/\perch-cp9.invalid\s\a.md")]
    [InlineData(@"\\?\C:\x.md")]
    [InlineData(@"\\?\UNC\perch-cp9.invalid\s\a.md")]
    [InlineData(@"\\.\pipe\perch")]
    [InlineData("")]
    [InlineData(null)]
    public void Network_and_device_paths_are_never_safe(string? path) =>
        Assert.False(LocalPath.IsSafeToProbe(path));

    [Theory]
    [InlineData(@"\\perch-cp9.invalid\s\a.md", true)]
    [InlineData(@"\\a\b", true)]
    [InlineData(@"\\?\C:\x", true)]
    [InlineData("//h/s", true)]
    [InlineData(@"C:\x", false)]
    [InlineData("/usr/x", false)]
    [InlineData("docs/x.md", false)]
    public void IsNetworkShaped_matches_every_double_slash_spelling(string path, bool expected) =>
        Assert.Equal(expected, LocalPath.IsNetworkShaped(path));

    [Fact]
    public void A_local_absolute_path_is_safe() =>
        Assert.True(LocalPath.IsSafeToProbe(Path.Combine(Path.GetTempPath(), "x.md")));

    [Theory]
    [InlineData("docs/plan.md")]
    [InlineData(@"..\..\x.md")]
    public void A_relative_path_is_safe(string path) =>
        Assert.True(LocalPath.IsSafeToProbe(path, @"\\perch-cp9.invalid\s\repo"));

    [Fact]
    public void A_share_is_safe_only_when_the_trusted_root_is_on_it()
    {
        // A UNC cwd only exists on Windows (on macOS '\' isn't a separator, so it isn't rooted and vouches for nothing).
        if (!OperatingSystem.IsWindows()) return;
        // The user working on \\srv\s may open files on \\srv\s — no host they didn't choose is contacted.
        const string cwd = @"\\perch-cp9.invalid\Share\repo";
        Assert.True(LocalPath.IsSafeToProbe(@"\\PERCH-CP9.invalid\share\other\a.md", cwd));
        Assert.True(LocalPath.IsSafeToProbe("//perch-cp9.invalid/share/a.md", cwd));
        Assert.False(LocalPath.IsSafeToProbe(@"\\perch-cp9.invalid\other\a.md", cwd));      // another share
        Assert.False(LocalPath.IsSafeToProbe(@"\\attacker.invalid\share\a.md", cwd));       // another host
        Assert.False(LocalPath.IsSafeToProbe(@"\\?\UNC\perch-cp9.invalid\Share\a.md", cwd)); // device form never vouched
        Assert.False(LocalPath.IsSafeToProbe(@"\\perch-cp9.invalid\s\a.md", "relative\\root"));
    }

    [Fact]
    public void A_fixed_drive_path_is_safe_on_windows()
    {
        if (!OperatingSystem.IsWindows()) return;
        var sys = Environment.GetFolderPath(Environment.SpecialFolder.Windows);   // always a fixed disk
        Assert.True(LocalPath.IsSafeToProbe(Path.Combine(sys, "win.ini")));
    }
}

public class FileRefResolverTests
{
    private readonly List<string> _probed = new();
    private bool Exists(string p) { _probed.Add(p); return true; }

    [Theory]
    [InlineData(@"\\perch-cp9.invalid\s\a.md")]
    [InlineData("//perch-cp9.invalid/s/a.md")]
    [InlineData(@"\\?\C:\a.md")]
    [InlineData(@"\\.\pipe\x.md")]
    public void An_unsafe_span_is_refused_without_a_probe(string text)
    {
        Assert.Null(FileRefResolver.Resolve(@"C:\work\repo", text, Exists));
        Assert.Empty(_probed);
    }

    [Fact]
    public void A_relative_span_resolves_under_the_cwd()
    {
        var cwd = Path.Combine(Path.GetTempPath(), "perch-cp9-repo");
        var abs = FileRefResolver.Resolve(cwd, "docs/plan.md", Exists);
        Assert.Equal(Path.GetFullPath(Path.Combine(cwd, "docs/plan.md")), abs);
        Assert.Single(_probed);
    }

    [Fact]
    public void A_span_on_the_cwds_own_share_is_probed()
    {
        if (!OperatingSystem.IsWindows()) return;   // a UNC cwd is Windows-only (see LocalPathTests)
        const string cwd = @"\\perch-cp9.invalid\s\repo";
        Assert.Equal(@"\\perch-cp9.invalid\s\repo\a.md", FileRefResolver.Resolve(cwd, "a.md", Exists));
        Assert.Equal(@"\\perch-cp9.invalid\s\b.md", FileRefResolver.Resolve(cwd, @"\\perch-cp9.invalid\s\b.md", Exists));
        Assert.Equal(2, _probed.Count);
    }

    [Theory]
    [InlineData("true")]
    [InlineData("SessionStart")]
    [InlineData("")]
    public void Non_path_code_is_never_probed(string text)
    {
        Assert.Null(FileRefResolver.Resolve(@"C:\work\repo", text, Exists));
        Assert.Empty(_probed);
    }

    [Fact]
    public void Repeated_resolves_are_served_from_the_cache()
    {
        // A streaming reply re-renders the same span ~25x/s; only the first render should touch the disk.
        var cwd = Path.Combine(Path.GetTempPath(), "perch-cp9-cache-" + Guid.NewGuid().ToString("N"));
        for (int i = 0; i < 25; i++)
            Assert.NotNull(FileRefResolver.Resolve(cwd, "a.md", Exists, cache: true));
        Assert.Single(_probed);
    }
}

public sealed class GitHeadTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "perch-githead-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private (string Wt, string GitDir) Worktree()
    {
        var gitDir = Directory.CreateDirectory(Path.Combine(_root, "realgit", "worktrees", "wt")).FullName;
        File.WriteAllText(Path.Combine(gitDir, "HEAD"), "ref: refs/heads/wt-branch\n");
        var wt = Directory.CreateDirectory(Path.Combine(_root, "wt")).FullName;
        return (wt, gitDir);
    }

    [Fact]
    public void Follows_a_relative_worktree_gitdir()
    {
        var (wt, gitDir) = Worktree();
        File.WriteAllText(Path.Combine(wt, ".git"), $"gitdir: {Path.GetRelativePath(wt, gitDir)}\n");
        Assert.Equal("wt-branch", GitHead.ReadBranch(wt));
    }

    [Theory]
    [InlineData(@"\\perch-cp9.invalid\s\realgit")]
    [InlineData("//perch-cp9.invalid/s/realgit")]
    public void Does_not_follow_a_gitdir_onto_a_share(string target)
    {
        var (wt, _) = Worktree();
        File.WriteAllText(Path.Combine(wt, ".git"), $"gitdir: {target}\n");
        Assert.Null(GitHead.ReadBranch(wt));
    }

    // Discriminating: the device-path spelling of the real local git dir was followed by the old code.
    [Fact]
    public void Does_not_follow_a_device_path_gitdir()
    {
        if (!OperatingSystem.IsWindows()) return;
        var (wt, gitDir) = Worktree();
        File.WriteAllText(Path.Combine(wt, ".git"), $@"gitdir: \\?\{gitDir}" + "\n");
        Assert.Null(GitHead.ReadBranch(wt));
    }
}
