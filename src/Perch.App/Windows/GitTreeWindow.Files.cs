using Avalonia.Controls;
using Avalonia.Input.Platform;
using Perch.Avalonia.Views;
using Perch.Data;

namespace Perch.Avalonia.Windows;

// Right-click on a file in Changes or Staged: stage/unstage, discard its changes, delete it, open or reveal it, copy
// its path. Discarding and deleting can't be undone, so both ask first.
internal sealed partial class GitTreeWindow
{
    private void ShowFileMenu(GitFileChange fc, bool staged, Control anchor)
    {
        if (_cwd is not { } cwd) return;
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
        items.Add(new Separator());
        if (exists)
        {
            items.Add(Item("Open file", () => PlatformServices.FileRevealer.OpenInEditor(full)));
            items.Add(Item(OperatingSystem.IsMacOS() ? "Reveal in Finder" : "Show in Explorer", () => PlatformServices.FileRevealer.RevealInFileManager(full)));
        }
        items.Add(Item("Copy path", () => Clipboard?.SetTextAsync(path)));
        ShowMenu(anchor, atPointer: true, items.ToArray());
    }

    // Back to the last commit, both sides: a file the commit doesn't have goes, a rename comes back under its old name.
    private async void DiscardEntirely(GitFileChange fc)
    {
        if (_cwd is not { } cwd) return;
        string name = System.IO.Path.GetFileName(fc.Path);
        bool added = fc.Staged == GitChangeKind.Added;
        bool go = await ConfirmDialog.ShowAsync(this, "Discard changes",
            added
                ? $"{name} isn't in the last commit, so discarding its changes deletes it. This can't be undone."
                : $"Discard every change to {name}, staged and not, and put it back as it was in the last commit? This can't be undone.",
            added ? "Delete" : "Discard", "Cancel");
        if (!go || !IsVisible) return;
        RunWorkingTreeOp(_ => _git.DiscardFileEntirely(cwd, fc.Path, fc.OrigPath));
    }

    private async void DeleteFile(GitFileChange fc)
    {
        if (_cwd is not { } cwd) return;
        string name = System.IO.Path.GetFileName(fc.Path.TrimEnd('/'));
        bool go = await ConfirmDialog.ShowAsync(this, "Delete file",
            fc.Untracked
                ? $"Delete {name} from disk? Git has never seen it, so this can't be undone."
                : $"Delete {name} from disk? The deletion is staged, ready to commit; its uncommitted changes are lost.",
            "Delete", "Cancel");
        if (!go || !IsVisible) return;
        RunWorkingTreeOp(_ => _git.DeleteFile(cwd, fc.Path, fc.Untracked));
    }
}
