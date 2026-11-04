using Avalonia;
using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Perch.Avalonia.Views;
using Perch.Data;
using Perch.Data.GitGraph;

namespace Perch.Avalonia.Windows;

// Right-click on a file in Changes or Staged: stage/unstage, discard its changes, delete it, stash just it, open or
// reveal it, copy its path. Discarding and deleting can't be undone, so both ask first.
internal sealed partial class GitTreeWindow
{
    private void ShowFileMenu(GitFileChange fc, bool staged, Control anchor)
    {
        if (_cwd is not { } cwd) return;
        // A right-click on one of several picked files acts on them all; on any other row, just that row.
        if (Picked(staged ? _stagedList : _unstagedList) is { Count: > 1 } picked && picked.Any(p => p.Path == fc.Path))
        {
            ShowFilesMenu(picked, staged, anchor, cwd);
            return;
        }
        string path = fc.Path, name = System.IO.Path.GetFileName(path.TrimEnd('/'));
        string full = System.IO.Path.Combine(cwd, path);
        bool exists = System.IO.File.Exists(full) || System.IO.Directory.Exists(full);
        var items = new List<Control>
        {
            Item(staged ? $"Unstage {name}" : $"Stage {name}", () => ToggleStage(path, !staged)),
            new Separator(),
        };
        if (staged)
            items.Add(Item($"Discard all changes to {name}…", () => DiscardEntirely(fc)));
        else if (!fc.Untracked)
            items.Add(Item($"Discard changes to {name}…", () => OnFileStage(new FileStageRequest(HunkStageAction.Discard, path))));
        if (exists)
            items.Add(Item($"Delete {name}…", () => DeleteFile(fc)));
        items.Add(Item($"Stash {name}", () => StashFiles([fc])));
        items.Add(new Separator());
        if (exists)
        {
            items.Add(Item("Open file", () => PlatformServices.FileRevealer.OpenInEditor(full)));
            items.Add(Item(OperatingSystem.IsMacOS() ? "Reveal in Finder" : "Show in Explorer", () => PlatformServices.FileRevealer.RevealInFileManager(full)));
        }
        items.Add(Item("Copy path", () => Clipboard?.SetTextAsync(path)));
        ShowMenu(anchor, atPointer: true, items.ToArray());
    }

    // ---- several files at once ----

    // Stage/unstage them, then the three things that change them: discard, delete, stash (just these files). Each says
    // how many it will touch, since not every picked file can be discarded or deleted.
    private void ShowFilesMenu(List<GitFileChange> files, bool staged, Control anchor, string cwd)
    {
        int n = files.Count;
        var discardable = staged ? files : files.Where(f => !f.Untracked).ToList();
        var deletable = files.Where(f => OnDisk(cwd, f.Path!)).ToList();
        var items = new List<Control>
        {
            Item(staged ? $"Unstage {n} files" : $"Stage {n} files", () => StagePaths(files, !staged)),
            new Separator(),
            Enabled(Item(Count(discardable.Count, staged ? "Discard all changes to {0}…" : "Discard changes to {0}…", n),
                () => DiscardFiles(discardable, staged, cwd)), discardable.Count > 0),
            Enabled(Item(Count(deletable.Count, "Delete {0}…", n), () => DeleteFiles(deletable, cwd)), deletable.Count > 0),
            Item($"Stash {n} files", () => StashFiles(files)),
        };
        if (!staged && discardable.Count < n)
            items.Add(Note($"{n - discardable.Count} new file{S(n - discardable.Count)} ha{(n - discardable.Count == 1 ? "s" : "ve")} nothing to discard: Delete removes {(n - discardable.Count == 1 ? "it" : "them")}."));
        if (deletable.Count < n)
            items.Add(Note($"{n - deletable.Count} {(n - deletable.Count == 1 ? "is" : "are")} already deleted, so Delete leaves {(n - deletable.Count == 1 ? "it" : "them")} alone."));
        items.Add(new Separator());
        items.Add(Item("Copy paths", () => Clipboard?.SetTextAsync(string.Join(Environment.NewLine, files.Select(f => f.Path)))));
        ShowMenu(anchor, atPointer: true, items.ToArray());
    }

    // "Discard changes to 3 files…" when it's all of them, "… to 2 of the 3 files…" when it isn't.
    private static string Count(int some, string template, int all) =>
        string.Format(template, some == all ? $"{all} files" : $"{some} of the {all} files");

    private static bool OnDisk(string cwd, string path)
    {
        string full = System.IO.Path.Combine(cwd, path);
        return System.IO.File.Exists(full) || System.IO.Directory.Exists(full);
    }

    private static string Names(IReadOnlyList<GitFileChange> files) =>
        string.Join(", ", files.Take(4).Select(f => System.IO.Path.GetFileName(f.Path!.TrimEnd('/'))))
        + (files.Count > 4 ? $" and {files.Count - 4} more" : "");

    // Changes: each tracked file back to what's staged (git restore --worktree). Staged: each file back to the last
    // commit, both sides, as the single-file "Discard all changes" does.
    private async void DiscardFiles(List<GitFileChange> files, bool staged, string cwd)
    {
        bool go = await ConfirmAsync("Discard changes",
            staged
                ? $"Put {Names(files)} back as they were in the last commit, staged and unstaged changes alike? Files the commit doesn't have are deleted. This can't be undone."
                : $"Discard the unstaged changes to {Names(files)}? What's staged stays. This can't be undone.",
            "Discard", "Cancel");
        if (!go || !IsVisible) return;
        RunWorkingTreeOp(_ => EachFile(files, f => staged
            ? _git.DiscardFileEntirely(cwd, f.Path!, f.OrigPath)
            : _git.DiscardFile(cwd, f.Path!, untracked: false)));
    }

    private async void DeleteFiles(List<GitFileChange> files, string cwd)
    {
        bool go = await ConfirmAsync("Delete files",
            $"Delete {Names(files)} from disk? Tracked files become staged deletions; new files are simply gone, which can't be undone.",
            "Delete", "Cancel");
        if (!go || !IsVisible) return;
        RunWorkingTreeOp(_ => EachFile(files, f => _git.DeleteFile(cwd, f.Path!, f.Untracked)));
    }

    // One file at a time (each can need a different git call); stops at the first failure and says which.
    private static (bool Ok, string Error) EachFile(IEnumerable<GitFileChange> files, Func<GitFileChange, (bool Ok, string Error)> op)
    {
        foreach (var f in files)
            if (op(f) is { Ok: false } r)
                return (false, $"{f.Path}: {r.Error}");
        return (true, "");
    }

    // A partial stash: just these files (or this one), staged and unstaged, new ones included. Undo pops it back.
    private void StashFiles(List<GitFileChange> files)
    {
        var paths = files.SelectMany(f => f.OrigPath is { } o ? [o, f.Path!] : new[] { f.Path! }).Distinct().ToList();
        string what = files.Count == 1 ? System.IO.Path.GetFileName(files[0].Path!.TrimEnd('/')) : $"{files.Count} files";
        string label = $"WIP on {_snap?.Current?.Branch ?? "detached HEAD"}: {what}";
        RunHereAction($"Stashing {what}…", cover: true, cwd =>
        {
            var r = GitOps.StashPushPaths(cwd, label, paths);
            if (!r.Ok || GitOps.StashTop(cwd) is not { } made) return (r, null);
            return (r, () => GitOps.StashPop(cwd, made));
        }, _ => $"Stashed {what}", conflict: null);
    }

    // Back to the last commit, both sides: a file the commit doesn't have goes, a rename comes back under its old name.
    private async void DiscardEntirely(GitFileChange fc)
    {
        if (_cwd is not { } cwd) return;
        string name = System.IO.Path.GetFileName(fc.Path);
        bool added = fc.Staged == GitChangeKind.Added;
        bool go = await ConfirmAsync("Discard changes",
            added
                ? $"{name} isn't in the last commit, so discarding its changes deletes it. This can't be undone."
                : $"Discard every change to {name}, staged and not, and put it back as it was in the last commit? This can't be undone.",
            added ? "Delete" : "Discard", "Cancel");
        if (!go || !IsVisible) return;
        RunWorkingTreeOp(_ => _git.DiscardFileEntirely(cwd, fc.Path, fc.OrigPath));
    }

    // ---- headless render hooks ----

    /// <summary>For the headless render: a spot in a file list below its last row, a point on the Files heading, and one
    /// in the collapsed strip, to click with synthetic pointer input.</summary>
    internal Point? PreviewBlankListSpot()
    {
        foreach (var list in new[] { _filesList, _unstagedList, _stagedList })
        {
            if (!list.IsEffectivelyVisible || list.Bounds.Height < 30) continue;
            double bottom = list.GetRealizedContainers().Select(c => c.TranslatePoint(new Point(0, c.Bounds.Height), list)?.Y ?? 0)
                .DefaultIfEmpty(0).Max();
            if (list.Bounds.Height - bottom > 24)
                return list.TranslatePoint(new Point(list.Bounds.Width / 2, (bottom + list.Bounds.Height) / 2), this);
        }
        return null;
    }

    internal string PreviewShownFile => CurrentFilePath() ?? "(none)";

    /// <summary>For the headless render: selects this window's uncommitted-changes row, so the Files pane shows its lists.</summary>
    internal void PreviewSelectWip()
    {
        if (_snap?.Current is { } here && _snap.RowOf(here.WipHash) is var row and >= 0) SelectRow(row);
    }

    /// <summary>For the headless render: picks the first <paramref name="n"/> files in Changes (as Ctrl+clicks would),
    /// opens their right-click menu, and says what the header button and rows show.</summary>
    internal string PreviewPickFiles(int n)
    {
        if (_unstagedList.ItemCount < n) return $"only {_unstagedList.ItemCount} files in Changes";
        _unstagedList.SelectedItems!.Clear();
        for (int i = 0; i < n; i++) _unstagedList.SelectedItems.Add(_unstagedList.Items[i]);
        Dispatcher.UIThread.RunJobs();
        int buttons = _unstagedList.GetRealizedContainers().SelectMany(c => c.GetVisualDescendants().OfType<Button>())
            .Count(b => Equals(b.Tag, RowStageTag) && b.IsVisible);
        if (_unstagedList.ContainerFromIndex(0) is { } row && _cwd is { } cwd)
            ShowFilesMenu(Picked(_unstagedList), staged: false, row, cwd);
        return $"header = \"{_stageAllBtn.Content}\", row + buttons showing = {buttons}";
    }

    internal Point? PreviewFilesHeading() =>
        _filesLabel.TranslatePoint(new Point(_filesLabel.Bounds.Width / 2, _filesLabel.Bounds.Height / 2), this);

    internal Point? PreviewFilesStrip() => _filesStrip.IsVisible
        ? _filesStrip.TranslatePoint(new Point(_filesStrip.Bounds.Width / 2, _filesStrip.Bounds.Height * 0.6), this) : null;

    internal bool PreviewDiffOpen => _diffOverlay.IsVisible;
    internal bool PreviewFilesCollapsed => !_filesPane.IsVisible;

    private async void DeleteFile(GitFileChange fc)
    {
        if (_cwd is not { } cwd) return;
        string name = System.IO.Path.GetFileName(fc.Path.TrimEnd('/'));
        bool go = await ConfirmAsync("Delete file",
            fc.Untracked
                ? $"Delete {name} from disk? Git has never seen it, so this can't be undone."
                : $"Delete {name} from disk? The deletion is staged, ready to commit; its uncommitted changes are lost.",
            "Delete", "Cancel");
        if (!go || !IsVisible) return;
        RunWorkingTreeOp(_ => _git.DeleteFile(cwd, fc.Path, fc.Untracked));
    }
}
