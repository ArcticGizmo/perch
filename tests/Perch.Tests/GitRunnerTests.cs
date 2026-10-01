using System.Diagnostics;
using Perch.Data;
using Xunit;

namespace Perch.Tests;

/// <summary>
/// <see cref="GitRunner"/> (review fixes CP11): the argument hardening and the repo-config parser as pure units,
/// then end to end against a real repo whose own config names a command for each vector a read-only git run can
/// trigger (fsmonitor, a filter driver, an external diff, textconv). Each command only writes a marker file;
/// it's a config string that git's own shell runs, never an executable file planted in the repo. A control run
/// of plain git proves each vector really fires on this host before the hardened run is shown not to.
/// </summary>
public sealed class GitRunnerTests
{
    // ---- HardenedArgs --------------------------------------------------------------------------------

    [Fact]
    public void Automatic_runs_turn_off_fsmonitor_and_signatures_and_blank_the_repos_filters()
    {
        var args = GitRunner.HardenedArgs(["--no-optional-locks", "status"], GitRunner.Trust.Automatic, ["evil", "a.b"]);

        Assert.Equal(
            ["-c", "core.fsmonitor=false", "-c", "log.showSignature=false",
             "-c", "filter.evil.clean=", "-c", "filter.evil.smudge=", "-c", "filter.evil.process=", "-c", "filter.evil.required=false",
             "-c", "filter.a.b.clean=", "-c", "filter.a.b.smudge=", "-c", "filter.a.b.process=", "-c", "filter.a.b.required=false",
             "--no-optional-locks", "status"],
            args);
    }

    [Theory]
    [InlineData("diff")]
    [InlineData("show")]
    [InlineData("log")]
    public void Automatic_diffing_commands_get_no_ext_diff_and_no_textconv_right_after_the_subcommand(string sub)
    {
        var args = GitRunner.HardenedArgs(["--no-optional-locks", sub, "--numstat", "--", "a.txt"], GitRunner.Trust.Automatic, []);

        int i = args.IndexOf(sub);
        Assert.Equal(["--no-ext-diff", "--no-textconv", "--numstat", "--", "a.txt"], args.Skip(i + 1));
    }

    [Fact]
    public void A_path_named_like_a_subcommand_is_not_mistaken_for_one()
    {
        var args = GitRunner.HardenedArgs(["--no-optional-locks", "ls-files", "--", "diff"], GitRunner.Trust.Automatic, []);
        Assert.DoesNotContain("--no-ext-diff", args);
    }

    [Fact]
    public void User_actions_keep_the_repos_own_behaviour_apart_from_fsmonitor()
    {
        var args = GitRunner.HardenedArgs(["commit", "-m", "x"], GitRunner.Trust.UserAction, ["evil"]);
        Assert.Equal(["-c", "core.fsmonitor=false", "commit", "-m", "x"], args);

        var diff = GitRunner.HardenedArgs(["diff"], GitRunner.Trust.UserAction, []);
        Assert.DoesNotContain("--no-textconv", diff);
    }

    // ---- ParseConfigList -----------------------------------------------------------------------------

    private static string Entry(string scope, string origin, string key, string value) => $"{scope}\0{origin}\0{key}\n{value}\0";

    [Fact]
    public void Only_the_repos_own_filter_drivers_are_picked_up()
    {
        var dir = Directory.CreateTempSubdirectory("perch-gitcfg-").FullName;
        try
        {
            Directory.CreateDirectory(Path.Combine(dir, ".git"));
            File.WriteAllText(Path.Combine(dir, ".git", "config"), "[core]\n");
            string output =
                Entry("system", "file:C:/Git/etc/gitconfig", "filter.lfs.clean", "git-lfs clean -- %f") +
                Entry("global", "file:C:/Users/u/.gitconfig", "filter.mine.smudge", "x") +
                Entry("local", "file:.git/config", "core.bare", "false") +
                Entry("local", "file:.git/config", "filter.evil.clean", "sh -c 'echo pwned'") +
                Entry("local", "file:.git/config", "filter.My.Driver.process", "y");

            var probe = GitRunner.ParseConfigList(output, dir);

            Assert.Equal(["My.Driver", "evil"], probe.Filters);   // subsection case and dots preserved
            Assert.True(probe.Cacheable);
            Assert.Equal(Path.Combine(dir, ".git", "config"), Assert.Single(probe.Stamps).Path);
            Assert.True(probe.StillValid());

            File.AppendAllText(Path.Combine(dir, ".git", "config"), "[filter \"new\"]\n");
            Assert.False(probe.StillValid());                      // any config change means probe again
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void A_repo_local_stock_git_lfs_is_left_alone_but_any_other_value_blanks_it()
    {
        var dir = Directory.CreateTempSubdirectory("perch-gitcfg-").FullName;
        try
        {
            Directory.CreateDirectory(Path.Combine(dir, ".git"));
            File.WriteAllText(Path.Combine(dir, ".git", "config"), "[core]\n");
            string Lfs(string name, string smudge = "git-lfs smudge -- %f", string process = "git-lfs filter-process") =>
                Entry("local", "file:.git/config", $"filter.{name}.clean", "git-lfs clean -- %f") +
                Entry("local", "file:.git/config", $"filter.{name}.smudge", smudge) +
                Entry("local", "file:.git/config", $"filter.{name}.process", process) +
                Entry("local", "file:.git/config", $"filter.{name}.required", "true");

            // What `git lfs install --local` writes (and its --skip-smudge form).
            Assert.Empty(GitRunner.ParseConfigList(Lfs("lfs"), dir).Filters);
            Assert.Empty(GitRunner.ParseConfigList(Lfs("lfs", "git-lfs smudge --skip -- %f", "git-lfs filter-process --skip"), dir).Filters);

            // One non-stock entry, or the stock commands under another name, and the driver is blanked.
            Assert.Equal(["lfs"], GitRunner.ParseConfigList(Lfs("lfs", process: "sh -c 'echo pwned'"), dir).Filters);
            Assert.Equal(["lfs"], GitRunner.ParseConfigList(
                Lfs("lfs") + Entry("local", "file:.git/config", "filter.lfs.process", "git-lfs filter-process; calc"), dir).Filters);
            Assert.Equal(["LFS"], GitRunner.ParseConfigList(Lfs("LFS"), dir).Filters);
            Assert.Equal(["evil"], GitRunner.ParseConfigList(Lfs("evil"), dir).Filters);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Theory]
    [InlineData("local", "include.path", "../extra.cfg")]
    [InlineData("local", "includeIf.gitdir:~/x/.path", "y")]
    [InlineData("local", "extensions.worktreeConfig", "true")]
    [InlineData("worktree", "core.sparseCheckout", "true")]
    public void Config_that_can_pull_in_other_files_is_never_cached(string scope, string key, string value)
    {
        var dir = Directory.CreateTempSubdirectory("perch-gitcfg-").FullName;
        try
        {
            Directory.CreateDirectory(Path.Combine(dir, ".git"));
            File.WriteAllText(Path.Combine(dir, ".git", "config"), "[core]\n");
            var probe = GitRunner.ParseConfigList(
                Entry("local", "file:.git/config", "core.bare", "false") + Entry(scope, "file:.git/config", key, value), dir);
            Assert.False(probe.Cacheable);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    // ---- end to end ------------------------------------------------------------------------------------

    [Fact]
    public void Repo_supplied_commands_never_run_through_Perchs_git()
    {
        var git = ExecutableResolver.Find("git");
        if (git is null) return;   // needs git on the host

        var dir = Directory.CreateTempSubdirectory("perch-gitrunner-").FullName;
        var repo = Directory.CreateDirectory(Path.Combine(dir, "repo")).FullName;
        string Marker(string name) => Path.Combine(dir, "mark_" + name);
        string ShPath(string name) => Marker(name).Replace('\\', '/');
        try
        {
            Git(git, repo, "init", "-q");
            Git(git, repo, "config", "user.email", "t@example.com");
            Git(git, repo, "config", "user.name", "t");
            Git(git, repo, "config", "commit.gpgsign", "false");
            File.WriteAllText(Path.Combine(repo, ".gitattributes"), "*.txt filter=evil diff=evil\n");
            File.WriteAllText(Path.Combine(repo, "a.txt"), "hello\n");
            Git(git, repo, "add", "-A");
            Git(git, repo, "commit", "-q", "-m", "init");

            // Warm GitRunner's config probe BEFORE the repo turns hostile, so the run below also proves a config
            // change invalidates the cache.
            Assert.Equal(0, GitRunner.Run(repo, 10_000, "--no-optional-locks", "status", "--porcelain=v2").Exit);

            Git(git, repo, "config", "core.fsmonitor", $"echo x > '{ShPath("fsmonitor")}'");
            Git(git, repo, "config", "filter.evil.clean", $"sh -c 'echo x > \"{ShPath("filter")}\"; cat'");
            Git(git, repo, "config", "diff.evil.textconv", $"sh -c 'echo x > \"{ShPath("textconv")}\"; cat \"$0\"'");
            Git(git, repo, "config", "diff.external", $"sh -c 'echo x > \"{ShPath("external")}\"'");
            File.AppendAllText(Path.Combine(repo, "a.txt"), "changed\n");
            File.SetLastWriteTimeUtc(Path.Combine(repo, "a.txt"), new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc));
            File.WriteAllText(Path.Combine(repo, "new.txt"), "one\ntwo\nthree");

            // Control: plain git fires every vector on this host (otherwise the checks below would prove nothing).
            Git(git, repo, "diff");
            Git(git, repo, "show", "HEAD");
            foreach (var m in new[] { "fsmonitor", "filter", "external", "textconv" })
                Assert.True(File.Exists(Marker(m)), $"control: plain git didn't run the {m} command");
            foreach (var f in Directory.GetFiles(dir, "mark_*")) File.Delete(f);

            // Every Automatic path Perch has.
            var svc = new GitRepoService();
            var status = svc.GetStatus(repo);
            var diff = svc.GetWorkingDiff(repo, "a.txt", staged: false);
            var tree = svc.GetWorkingTreeDiff(repo, staged: false);
            var commit = svc.GetCommitDiff(repo, svc.GetLog(repo, 1)[0].Hash);
            var stats = svc.GetChangeStats(repo);
            var numstat = GitRunner.Run(repo, 10_000, "--no-optional-locks", "diff", "--numstat");
            var files = ProjectFileScan.Scan(repo);

            Assert.Empty(Directory.GetFiles(dir, "mark_*"));

            // ...and the answers are still right.
            Assert.Contains(status!.Value.Changes, c => c.Path == "a.txt");
            Assert.Contains(diff!.Value.Files[0].Hunks[0].Lines, l => l.Kind == GitDiffLineKind.Added && l.Text == "changed");
            Assert.Single(tree!.Value.Files);
            Assert.NotEmpty(commit!.Value.Files);
            Assert.Equal("1\t0\ta.txt", numstat.Stdout.Trim());
            Assert.Contains(stats, s => s.Path == "new.txt" && s.Untracked && s.Added == 3);
            Assert.Contains("new.txt", files.RelativePaths);
        }
        finally
        {
            foreach (var f in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
                try { File.SetAttributes(f, FileAttributes.Normal); } catch { }
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }

    // Dogfood regression: with git-lfs installed in the repo's own config (`git lfs install --local`), blanking the
    // lfs filter made every stat-dirty LFS file read as modified (the pointer in the index against the real content).
    [Fact]
    public void A_repo_local_git_lfs_install_still_reads_its_files_as_clean()
    {
        var git = ExecutableResolver.Find("git");
        if (git is null || ExecutableResolver.Find("git-lfs") is null) return;   // needs git and git-lfs on the host

        var dir = Directory.CreateTempSubdirectory("perch-gitlfs-").FullName;
        try
        {
            Git(git, dir, "init", "-q");
            Git(git, dir, "config", "user.email", "t@example.com");
            Git(git, dir, "config", "user.name", "t");
            Git(git, dir, "config", "commit.gpgsign", "false");
            Git(git, dir, "lfs", "install", "--local");
            File.WriteAllText(Path.Combine(dir, ".gitattributes"), "*.bin filter=lfs diff=lfs merge=lfs -text\n");
            File.WriteAllText(Path.Combine(dir, "data.bin"), "payload\n");
            Git(git, dir, "add", "-A");
            Git(git, dir, "commit", "-q", "-m", "init");
            File.SetLastWriteTimeUtc(Path.Combine(dir, "data.bin"), new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc));

            // Control: with the lfs filter blanked (the old behaviour) the untouched file reads as modified.
            var blanked = GitRunner.Run(dir, 10_000, "-c", "filter.lfs.clean=", "-c", "filter.lfs.process=",
                "-c", "filter.lfs.required=false", "--no-optional-locks", "status", "--porcelain");
            Assert.Contains("data.bin", blanked.Stdout);

            var status = new GitRepoService().GetStatus(dir);
            Assert.DoesNotContain(status!.Value.Changes, c => c.Path == "data.bin");
            Assert.Equal("", GitRunner.Run(dir, 10_000, "--no-optional-locks", "diff", "--numstat").Stdout.Trim());
        }
        finally
        {
            foreach (var f in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
                try { File.SetAttributes(f, FileAttributes.Normal); } catch { }
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }

    // ---- GitRepoService.CountUntrackedLines ------------------------------------------------------------

    [Theory]
    [InlineData("", 0)]
    [InlineData("a\n", 1)]
    [InlineData("a\nb\n", 2)]
    [InlineData("a\nb", 2)]          // a last line without a newline still counts, as in git's diff
    [InlineData("\n\n", 2)]
    public void An_untracked_files_lines_count_like_gits_added_file_diff(string content, int expected)
    {
        var path = Path.Combine(Path.GetTempPath(), "perch-lines-" + Guid.NewGuid().ToString("N") + ".txt");
        try
        {
            File.WriteAllText(path, content);
            Assert.Equal((expected, false), GitRepoService.CountUntrackedLines(path));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void A_binary_untracked_file_counts_no_lines_and_a_missing_one_is_null()
    {
        var path = Path.Combine(Path.GetTempPath(), "perch-lines-" + Guid.NewGuid().ToString("N") + ".bin");
        try
        {
            File.WriteAllBytes(path, [(byte)'a', (byte)'\n', 0, (byte)'b', (byte)'\n']);
            Assert.Equal((0, true), GitRepoService.CountUntrackedLines(path));
        }
        finally { File.Delete(path); }
        Assert.Null(GitRepoService.CountUntrackedLines(path));
    }

    private static void Git(string git, string cwd, params string[] args)
    {
        var psi = new ProcessStartInfo(git)
        {
            WorkingDirectory = cwd, UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        _ = p.StandardOutput.ReadToEndAsync();
        _ = p.StandardError.ReadToEndAsync();
        p.WaitForExit(15_000);
    }
}
