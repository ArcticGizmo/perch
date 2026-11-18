using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Perch.Data;

namespace Perch.Avalonia.Windows;

// The Files pane's two views: flat path rows (each keeping its file name when the row is too narrow) or a folder tree
// (FileTreeLayout) with collapsible folders. Folder rows sit in the same ListBox as the files, so selection, the
// stage buttons and the right-click menus keep working on file items; every handler that reads a selected item
// already ignores anything that isn't a file.
internal sealed partial class GitTreeWindow
{
    private sealed record FolderRow(string Key, string Label, int Depth, bool Expanded, int Files);

    private const string FolderRowTag = "folder-row";
    private const double TreeIndent = 14;

    private bool _filesAsTree;
    private readonly HashSet<string> _collapsedFolders = new(StringComparer.Ordinal);
    private Button _listViewBtn = null!, _treeViewBtn = null!;

    // The files behind each list, in git's order, so a view switch or a folder toggle can rebuild the rows.
    private IReadOnlyList<GitFileChange> _unstagedFiles = [], _stagedFiles = [];
    private IReadOnlyList<GitDiffFile> _commitFiles = [];

    // A file row's indent in the tree view, per list (a folded chain can differ between lists), keyed by path.
    private readonly Dictionary<string, int> _unstagedDepth = new(), _stagedDepth = new(), _commitDepth = new();

    private Control BuildViewToggle()
    {
        _listViewBtn = SegButton("List");
        _treeViewBtn = SegButton("Tree");
        foreach (var b in new[] { _listViewBtn, _treeViewBtn })
        {
            b.FontSize = 11;
            b.Padding = new Thickness(9, 2);
        }
        ToolTip.SetTip(_listViewBtn, "Show each file as one row with its full path");
        ToolTip.SetTip(_treeViewBtn, "Show the files as a folder tree");
        _listViewBtn.Click += (_, _) => SetFilesAsTree(false);
        _treeViewBtn.Click += (_, _) => SetFilesAsTree(true);
        var seg = Segment(_listViewBtn, _treeViewBtn);
        seg.CornerRadius = new CornerRadius(6);
        seg.Margin = new Thickness(0, 6, 2, 2);
        seg.SetValue(DockPanel.DockProperty, Dock.Right);
        UpdateViewButtons();
        return seg;
    }

    // A tree spends a row per folder, so its rows drop most of the list item's roomy padding.
    private void TightenTreeRows(ListBox list) => list.ContainerPrepared += (_, e) =>
    {
        if (_filesAsTree) e.Container.SetValue(TemplatedControl.PaddingProperty, new Thickness(4, 0));
        else e.Container.ClearValue(TemplatedControl.PaddingProperty);
    };

    private void UpdateViewButtons()
    {
        StyleSeg(_listViewBtn, !_filesAsTree);
        StyleSeg(_treeViewBtn, _filesAsTree);
    }

    private void SetFilesAsTree(bool tree)
    {
        if (_filesAsTree == tree) return;
        _filesAsTree = tree;
        _settings.GitTreeFilesAsTree = tree;
        _settings.Save();
        UpdateViewButtons();
        Rearrange(PickedPaths(_unstagedList), PickedPaths(_stagedList));
    }

    // The rows for one list: the files as they are, or folders and files in tree order with each file's depth noted.
    private List<object> Arrange<T>(IReadOnlyList<T> files, Func<T, string?> pathOf, Dictionary<string, int> depths)
        where T : struct
    {
        depths.Clear();
        if (!_filesAsTree) return files.Select(f => (object)f).ToList();
        var rows = new List<object>(files.Count);
        foreach (var e in FileTreeLayout.Build(files.Select(f => pathOf(f) ?? "").ToList(), _collapsedFolders))
        {
            if (e.IsFolder) rows.Add(new FolderRow(e.Key, e.Label, e.Depth, e.Expanded, e.Files));
            else
            {
                rows.Add(files[e.FileIndex]);
                depths[e.Key] = e.Depth;
            }
        }
        return rows;
    }

    private List<object> ArrangeUnstaged() => Arrange(_unstagedFiles, f => f.Path, _unstagedDepth);
    private List<object> ArrangeStaged() => Arrange(_stagedFiles, f => f.Path, _stagedDepth);
    private List<object> ArrangeCommit() => Arrange(_commitFiles, DiffFilePath, _commitDepth);

    private static string? DiffFilePath(GitDiffFile f) => f.NewPath ?? f.OldPath;

    // Opens every folder above this file, so a file asked for by name (a click elsewhere, a node switch) is on screen.
    private void RevealFile(string? path)
    {
        if (path is null) return;
        foreach (var key in FileTreeLayout.FoldersOf(path)) _collapsedFolders.Remove(key);
    }

    private static int FirstFileIndex(ListBox list)
    {
        int i = 0;
        foreach (var item in list.Items)
        {
            if (item is not FolderRow) return i;
            i++;
        }
        return -1;
    }

    private static int IndexOfItem(ListBox list, object item)
    {
        int i = 0;
        foreach (var x in list.Items)
        {
            if (Equals(x, item)) return i;
            i++;
        }
        return -1;
    }

    private static List<string> PickedPaths(ListBox list) => Picked(list).Select(f => f.Path).ToList();

    // Rebuilds the rows of whichever lists are showing, keeping the shown file and any picked files highlighted
    // without reloading the diff.
    private void Rearrange(List<string> pickedUnstaged, List<string> pickedStaged)
    {
        _suppressSelect = true;
        if (_wipPanel.IsVisible)
        {
            _unstagedList.ItemsSource = ArrangeUnstaged();
            _stagedList.ItemsSource = ArrangeStaged();
            Reselect(_unstagedList, pickedUnstaged, _shownWip is { Staged: false } u ? u.Path : null);
            Reselect(_stagedList, pickedStaged, _shownWip is { Staged: true } s ? s.Path : null);
        }
        else if (_commitAreaHost.IsVisible && _filesList.ItemsSource is not null)
        {
            _filesList.ItemsSource = ArrangeCommit();
            if (_shownCommit is { } c && IndexOfItem(_filesList, c.File) is var i and >= 0) _filesList.SelectedIndex = i;
        }
        _suppressSelect = false;
        UpdateMultiSelect();
        UpdateEmptyStates();
    }

    private static void Reselect(ListBox list, List<string> picked, string? shown)
    {
        list.SelectedItems?.Clear();
        var want = picked.Count > 0 ? picked : shown is null ? [] : [shown];
        foreach (var item in list.Items)
            if (item is GitFileChange fc && want.Contains(fc.Path))
                list.SelectedItems?.Add(item);
    }

    private void ToggleFolder(string key) => ToggleFolder(key, PickedPaths(_unstagedList), PickedPaths(_stagedList));

    private void ToggleFolder(string key, List<string> pickedUnstaged, List<string> pickedStaged)
    {
        if (!_collapsedFolders.Remove(key)) _collapsedFolders.Add(key);
        Rearrange(pickedUnstaged, pickedStaged);
    }

    // ---- rows ----

    private Control FolderRowControl(FolderRow f)
    {
        var count = new TextBlock
        {
            Text = f.Files.ToString(), FontSize = 11, Foreground = _pal.Muted, VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(8, 0, 0, 0), [DockPanel.DockProperty] = Dock.Right,
        };
        var chevron = new TextBlock
        {
            Text = f.Expanded ? "▾" : "▸", FontSize = 12, Foreground = _pal.Muted, Width = 14,
            VerticalAlignment = VerticalAlignment.Center, [DockPanel.DockProperty] = Dock.Left,
        };
        var label = new TextBlock
        {
            Text = f.Label + "/", FontFamily = Mono, FontSize = 12, Foreground = _pal.Fg,
            VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.PrefixCharacterEllipsis,
        };
        var row = new DockPanel
        {
            LastChildFill = true, Margin = new Thickness(8 + f.Depth * TreeIndent, 4, 8, 4),
            Background = Brushes.Transparent, Tag = FolderRowTag, Children = { count, chevron, label },
        };
        ToolTip.SetTip(row, f.Key);
        // The press is taken here so the ListBox never selects a folder: a highlighted row means an open file.
        row.PointerPressed += (_, e) =>
        {
            if (!e.GetCurrentPoint(row).Properties.IsLeftButtonPressed) return;
            e.Handled = true;
            var (u, s) = (PickedPaths(_unstagedList), PickedPaths(_stagedList));
            Dispatcher.UIThread.Post(() => { if (IsVisible) ToggleFolder(f.Key, u, s); });
        };
        row.PointerReleased += (_, e) => e.Handled = true;
        return row;
    }

    // The status letter, then the path with its directory muted and trimmed from the left, so a narrow pane still shows
    // which file it is. In the tree the directory is the folder row above, so only the name (indented) shows.
    private Control FileRowContent(char code, IBrush color, string path, string? origPath, int depth)
    {
        var codeText = new TextBlock
        {
            Text = code.ToString(), FontFamily = Mono, FontSize = 12, Foreground = color, Width = 18,
            VerticalAlignment = VerticalAlignment.Center, [DockPanel.DockProperty] = Dock.Left,
        };
        var norm = path.Replace('\\', '/');
        bool dirLike = norm.EndsWith('/');
        var trimmed = norm.TrimEnd('/');
        int slash = trimmed.LastIndexOf('/');
        string name = trimmed[(slash + 1)..] + (dirLike ? "/" : "");
        string dir = _filesAsTree || slash < 0 ? "" : trimmed[..(slash + 1)];

        var pathText = new TextBlock
        {
            FontFamily = Mono, FontSize = 12, VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.PrefixCharacterEllipsis, Inlines = new InlineCollection(),
        };
        if (origPath is not null) pathText.Inlines.Add(new Run(origPath + " → ") { Foreground = _pal.Muted });
        if (dir.Length > 0) pathText.Inlines.Add(new Run(dir) { Foreground = _pal.Muted });
        pathText.Inlines.Add(new Run(name) { Foreground = color });

        var dock = new DockPanel
        {
            LastChildFill = true, Margin = new Thickness(_filesAsTree ? depth * TreeIndent : 0, 0, 0, 0),
            Children = { codeText, pathText },
        };
        ToolTip.SetTip(dock, origPath is null ? path : $"{origPath} → {path}");
        return dock;
    }

    private int TreeDepth(Dictionary<string, int> depths, string? path) =>
        _filesAsTree && path is not null && depths.TryGetValue(path, out var d) ? d : 0;

    // ---- templates ----

    private FuncDataTemplate<object> WipRowTemplate(bool staged) => new((item, _) => item switch
    {
        FolderRow f => FolderRowControl(f),
        GitFileChange { Path: not null } fc => WipRow(fc, staged),
        _ => new Control(),
    }, supportsRecycling: false);

    private FuncDataTemplate<object> CommitFileTemplate() => new((item, _) => item switch
    {
        FolderRow f => FolderRowControl(f),
        GitDiffFile gf => CommitFileRow(gf),
        _ => new Control(),
    }, supportsRecycling: false);

    private Control CommitFileRow(GitDiffFile gf)
    {
        string? path = DiffFilePath(gf);
        if (path is null) return new Control();
        string? orig = gf.OldPath is { } o && gf.NewPath is { } n && o != n ? o : null;
        var content = FileRowContent(CommitFileCode(gf), CommitFileColor(gf), path, orig, TreeDepth(_commitDepth, path));
        content.Margin = new Thickness(12 + content.Margin.Left, 6, 12, 6);
        return content;
    }
}
