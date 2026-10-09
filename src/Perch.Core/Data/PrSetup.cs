namespace Perch.Data;

/// <summary>One step a terminal runs before <c>claude</c>, in the command "Copy command" copies.</summary>
public abstract record PrSetupStep;

/// <summary><c>git -C &lt;Dir&gt; &lt;Args…&gt;</c>.</summary>
public sealed record PrGitStep(string Dir, IReadOnlyList<string> Args) : PrSetupStep;

/// <summary>Make a folder (and its parents) if it isn't there.</summary>
public sealed record PrMakeDirStep(string Path) : PrSetupStep;

/// <summary>Append <see cref="Line"/> to <see cref="File"/>, after a line break when the file doesn't end in one.</summary>
public sealed record PrAppendLineStep(string File, string Line, bool NewlineFirst) : PrSetupStep;

/// <summary>Where the session will run, and what a terminal must do first to make that folder (nothing when it's
/// already there).</summary>
public sealed record PrSetupPlan(string Path, IReadOnlyList<PrSetupStep> Steps);

/// <summary>
/// The setup a PR session's folder needs, as steps rather than actions: the same git commands
/// <see cref="PrWorktree.Ensure"/>, <see cref="PrClone.Ensure"/> and <see cref="PrScratch.Ensure"/> run, for
/// "Copy command" to hand to a terminal so Perch itself changes nothing on disk (part 5 of
/// docs/github-dashboard-plan.md). Pure: the caller reads the repo (worktrees, remotes, the exclude file) and passes
/// what it found.
/// </summary>
public static class PrSetup
{
    /// <summary>A new worktree for PR <paramref name="number"/>, placed by <paramref name="ctx"/>'s layout: fetch the
    /// PR's head from <paramref name="remote"/> onto <c>perch/pr-&lt;n&gt;</c>, keep a nested worktree folder out of
    /// <c>git status</c> through <c>info/exclude</c> (when <paramref name="commonDir"/> is known and
    /// <paramref name="excludeText"/> doesn't already list it), then <c>git worktree add</c>. No steps when
    /// <c>perch/pr-&lt;n&gt;</c> is already checked out somewhere: that worktree is reused, as Ensure does.</summary>
    public static PrSetupPlan? Worktree(PrWorktreeContext ctx, string remote, int number, string? commonDir, string? excludeText)
    {
        if (PrWorktree.PlanFor(ctx, number) is not { } plan) return null;
        if (ctx.Set.PathOfBranch(plan.Branch) is { } existing) return new(existing, []);
        var root = ctx.Set.Root;
        var steps = new List<PrSetupStep>
        {
            new PrGitStep(root, ["fetch", "--no-tags", remote, $"pull/{number}/head:{plan.Branch}"]),
        };
        if (ctx.Layout.ExcludeEntry(ctx.Set.Bare) is { } entry && commonDir is not null
            && PrWorktree.WithExcludeEntry(excludeText, entry) is not null)
            steps.Add(new PrAppendLineStep(Path.Combine(commonDir, "info", "exclude"), entry,
                NewlineFirst: !string.IsNullOrEmpty(excludeText) && !excludeText.EndsWith('\n')));
        steps.Add(new PrGitStep(root, ["worktree", "add", plan.Path, plan.Branch]));
        return new(plan.Path, steps);
    }

    /// <summary>A separate clone in Perch's data folder (<paramref name="baseDir"/>): clone without file contents,
    /// fetch the PR's head onto <c>perch/pr-&lt;n&gt;</c>, check it out. No steps when <paramref name="cloned"/>
    /// (it's there already). Null when the repo's names aren't safe to put in a URL.</summary>
    public static PrSetupPlan? Clone(GitRepoRef repo, int number, string baseDir, bool cloned)
    {
        if (number <= 0 || PrClone.CloneUrl(repo) is not { } url) return null;
        var dir = PrWorkspaceFolders.For(baseDir, "pr-clones", repo, number);
        if (cloned) return new(dir, []);
        var parent = Path.GetDirectoryName(dir)!;
        var branch = PrWorktree.BranchFor(number);
        return new(dir,
        [
            new PrMakeDirStep(parent),
            new PrGitStep(parent, ["clone", "--filter=blob:none", "--no-checkout", "--", url, dir]),
            new PrGitStep(dir, ["fetch", "--no-tags", "origin", $"pull/{number}/head:{branch}"]),
            new PrGitStep(dir, ["checkout", branch]),
        ]);
    }

    /// <summary>The empty scratch folder for a no-checkout session; one step to make it unless it
    /// <paramref name="exists"/>.</summary>
    public static PrSetupPlan? Scratch(GitRepoRef repo, int number, string baseDir, bool exists)
    {
        if (number <= 0) return null;
        var dir = PrWorkspaceFolders.For(baseDir, "pr-scratch", repo, number);
        return new(dir, exists ? [] : [new PrMakeDirStep(dir)]);
    }
}
