using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Shapes;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using Perch.Avalonia.Rendering;
using Perch.Avalonia.Theming;
using Perch.Data;
using Path = System.IO.Path;   // disambiguate from Avalonia.Controls.Shapes.Path (we use Ellipse from there)

namespace Perch.Avalonia.Windows;

/// <summary>
/// The Markdown viewer/editor. Opened from a session's right-click "Markdown files…" item, it lists the
/// <c>.md</c> files that session produced (rose) or referenced (muted) plus a <c>.gitignore</c>-respecting
/// tree of the project's Markdown, and edits the selected file in a split view — a source editor on the
/// left, a live rendered preview on the right — with save.
///
/// A single reused instance via <c>WindowHost.ShowOrFocus</c>; <see cref="Retarget"/> re-points it at a
/// different session without reopening. File IO runs off the UI thread and marshals back guarded by
/// <see cref="Visual.IsVisible"/> and a generation token, so a result arriving after the window closed or
/// was re-pointed is dropped (the <c>StatsWindow</c>/<c>GitTreeWindow</c> idiom). Built in code, themed
/// through <see cref="Palette"/>. Save guards an on-disk change (an mtime conflict — the session may still
/// be editing the file) behind a confirm; an external edit while the buffer is clean reloads silently.
/// </summary>
internal sealed class MarkdownWindow : Window
{
    private static readonly FontFamily Mono = new("Cascadia Code, Consolas, Menlo, monospace");
    // The rose that marks "produced" files, matching the overlay's Markdown glyph.
    private static readonly IBrush ProducedDotBrush = new SolidColorBrush(Color.FromRgb(244, 114, 182));

    private readonly AppSettings _settings;

    private readonly TextBlock _titleText;
    private readonly TextBlock _subText;
    private readonly Border _filePaneHost;         // left: session groups + project tree
    private readonly TreeView _tree;
    private readonly TextBlock _filesPlaceholder;
    private readonly Border _editorHost;           // right: split editor / placeholder

    // Editor (right pane), built once and swapped in on first file open.
    private readonly Control _editorRoot;
    private readonly TextBlock _editorPlaceholder;
    private readonly TextBlock _editorFileLabel;
    private readonly TextBlock _editorStatus;
    private readonly Button _saveBtn;
    private readonly TextBox _sourceBox;
    private readonly SelectableTextBlock _previewBlock;
    private readonly DispatcherTimer _previewTimer;

    private string? _cwd;
    private string? _sessionId;
    private bool _isActive;   // the session was working (Running/AwaitingInput) at last Retarget
    // Bumped on every Retarget/close so an in-flight off-thread load knows its results are stale and drops
    // them rather than painting into a window that has moved on.
    private int _gen;

    // Open-file / edit state.
    private string? _openFilePath;
    private DateTime? _openFileMtimeUtc;   // the file's mtime when loaded/last saved — for conflict detection
    private string _loadedText = "";       // the on-load buffer, to detect dirtiness
    private bool _dirty;
    private bool _loading;                 // set while programmatically filling the source box (don't mark dirty)
    private bool _externalChange;          // the file changed on disk while the buffer had unsaved edits
    private FileSystemWatcher? _watcher;
    private FileNode? _currentFileNode;    // the file leaf currently open, for the discard-on-switch prompt
    private bool _suppressSelect;          // reverting a tree selection shouldn't re-trigger the handler
    private bool _closeConfirmed;          // discard already confirmed / programmatic close — skip the prompt

    public MarkdownWindow(AppSettings settings)
    {
        _settings = settings;

        Title = "Markdown";
        Width = 1040;
        Height = 720;
        MinWidth = 720;
        MinHeight = 460;
        Background = Palette.SurfaceSunkenBrush;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;

        // ── Header: title + subtitle (the targeted directory) ──────────────────────────────────────
        _titleText = new TextBlock
        {
            Text = "Markdown", FontSize = 15, FontWeight = FontWeight.SemiBold,
            Foreground = Palette.TitleBrush, VerticalAlignment = VerticalAlignment.Center,
        };
        _subText = new TextBlock
        {
            Text = "", FontSize = 11.5, Foreground = Palette.MutedBrush, FontFamily = Mono,
            VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 0, 0),
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        var header = new Border
        {
            [DockPanel.DockProperty] = Dock.Top,
            Background = Palette.FormBgBrush,
            BorderBrush = Palette.SeparatorBrush,
            BorderThickness = new Thickness(0, 0, 0, 1),
            Padding = new Thickness(16, 10),
            Child = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Children = { _titleText, _subText },
            },
        };

        // ── Left: the file tree ────────────────────────────────────────────────────────────────────
        _tree = new TreeView
        {
            Background = Brushes.Transparent,
            ItemTemplate = new FuncTreeDataTemplate<FileNode>(
                _ => true, (n, _) => BuildNodeVisual(n), n => n.Children),
        };
        // Groups (and folders) open by default; a click on the expander still collapses them (a local
        // value beats the style), so this only sets the initial state.
        _tree.Styles.Add(new Style(x => x.OfType<TreeViewItem>())
        {
            Setters = { new Setter(TreeViewItem.IsExpandedProperty, true) },
        });
        _tree.SelectionChanged += OnTreeSelectionChanged;

        _filesPlaceholder = new TextBlock
        {
            Text = "Loading Markdown files…", FontSize = 12, Foreground = Palette.MutedBrush,
            Margin = new Thickness(14), TextWrapping = TextWrapping.Wrap,
        };
        _filePaneHost = new Border
        {
            Background = Palette.FormBgBrush,
            BorderBrush = Palette.SeparatorBrush,
            BorderThickness = new Thickness(0, 0, 1, 0),
            Child = _filesPlaceholder,
        };

        // ── Right: the split editor (built once, shown on first file open) ─────────────────────────
        _editorPlaceholder = new TextBlock
        {
            Text = "Select a file to view it.", FontSize = 12.5, Foreground = Palette.MutedBrush,
            Margin = new Thickness(18), HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        _editorFileLabel = new TextBlock
        {
            Text = "", FontSize = 12, Foreground = Palette.MutedBrush, FontFamily = Mono,
            VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis,
        };
        _editorStatus = new TextBlock
        {
            Text = "", FontSize = 11.5, Foreground = Palette.MutedBrush,
            VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0),
        };
        _saveBtn = SettingsUi.FlatButton("Save");
        _saveBtn.IsEnabled = false;
        _saveBtn.Click += (_, _) => Save();

        _sourceBox = new TextBox
        {
            AcceptsReturn = true, AcceptsTab = true, TextWrapping = TextWrapping.Wrap,
            FontFamily = Mono, FontSize = 12.5,
            Background = Palette.SurfaceSunkenBrush, Foreground = Palette.FgBrush,
            BorderThickness = new Thickness(0), CornerRadius = new CornerRadius(0),
            Padding = new Thickness(12, 10), VerticalContentAlignment = VerticalAlignment.Top,
            [ScrollViewer.VerticalScrollBarVisibilityProperty] = ScrollBarVisibility.Auto,
        };
        _sourceBox.TextChanged += OnSourceTextChanged;

        _previewBlock = new SelectableTextBlock
        {
            TextWrapping = TextWrapping.Wrap, Foreground = Palette.FgBrush, FontSize = 13,
            Margin = new Thickness(18, 14),
        };
        _previewTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(180) };
        _previewTimer.Tick += (_, _) => { _previewTimer.Stop(); RenderPreview(_sourceBox.Text ?? ""); };

        _editorRoot = BuildEditorRoot();
        _editorHost = new Border { Background = Palette.SurfaceSunkenBrush, Child = _editorPlaceholder };

        // ── Body: file pane | splitter | editor ────────────────────────────────────────────────────
        var body = new Grid { ColumnDefinitions = new ColumnDefinitions("300,Auto,*") };
        Grid.SetColumn(_filePaneHost, 0);
        var splitter = new GridSplitter
        {
            Width = 4, Background = Palette.SeparatorBrush,
            ResizeDirection = GridResizeDirection.Columns,
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        Grid.SetColumn(splitter, 1);
        Grid.SetColumn(_editorHost, 2);
        body.Children.Add(_filePaneHost);
        body.Children.Add(splitter);
        body.Children.Add(_editorHost);

        Content = new DockPanel { LastChildFill = true, Children = { header, body } };
    }

    // The editor's own layout: a thin toolbar (file name + status + Save) over a source | splitter | preview
    // split. Built once; its controls are mutated as files are opened.
    private Control BuildEditorRoot()
    {
        var toolbarPanel = new DockPanel();
        DockPanel.SetDock(_saveBtn, Dock.Right);
        DockPanel.SetDock(_editorStatus, Dock.Right);
        toolbarPanel.Children.Add(_saveBtn);
        toolbarPanel.Children.Add(_editorStatus);
        toolbarPanel.Children.Add(_editorFileLabel);   // fills the remaining width
        var toolbar = new Border
        {
            [DockPanel.DockProperty] = Dock.Top,
            Background = Palette.FormBgBrush,
            BorderBrush = Palette.SeparatorBrush,
            BorderThickness = new Thickness(0, 0, 0, 1),
            Padding = new Thickness(12, 6),
            Child = toolbarPanel,
        };

        var split = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto,*") };
        Grid.SetColumn(_sourceBox, 0);
        var innerSplit = new GridSplitter
        {
            Width = 4, Background = Palette.SeparatorBrush,
            ResizeDirection = GridResizeDirection.Columns,
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        Grid.SetColumn(innerSplit, 1);
        var previewScroll = new ScrollViewer
        {
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Content = _previewBlock,
        };
        Grid.SetColumn(previewScroll, 2);
        split.Children.Add(_sourceBox);
        split.Children.Add(innerSplit);
        split.Children.Add(previewScroll);

        return new DockPanel { LastChildFill = true, Children = { toolbar, split } };
    }

    /// <summary>
    /// Re-point the window at a session's working directory and reload. Called on first open and every
    /// reuse via <c>WindowHost.ShowOrFocus</c>. <paramref name="isActive"/> is true when the session may
    /// still be writing to these files (Running/AwaitingInput), which the save-conflict prompt notes.
    /// </summary>
    public void Retarget(string cwd, string sessionId, string displayName, bool isActive)
    {
        _cwd = cwd;
        _sessionId = sessionId;
        _isActive = isActive;
        _gen++;   // invalidate any in-flight load from a previous target

        Title = $"Markdown — {displayName}";
        _titleText.Text = displayName;
        _subText.Text = cwd;

        Reload();
    }

    // Scan the session's produced/referenced .md sets and the project .md tree off the UI thread, then
    // build the file pane guarded by IsVisible + the generation token.
    private void Reload()
    {
        var cwd = _cwd;
        var sid = _sessionId ?? "";
        if (string.IsNullOrEmpty(cwd))
            return;

        // Drop any open file / watcher — a different project is being loaded.
        StopWatcher();
        _openFilePath = null;
        _currentFileNode = null;
        _dirty = false;
        _externalChange = false;
        _editorPlaceholder.Text = "Select a file to view it.";
        _editorHost.Child = _editorPlaceholder;

        int gen = _gen;
        _tree.ItemsSource = null;
        _filesPlaceholder.Text = "Loading Markdown files…";
        _filePaneHost.Child = _filesPlaceholder;

        Task.Run(() =>
        {
            var sets = new MarkdownFilesReader().GetFileSets(sid, cwd);
            var project = MarkdownProjectScan.Scan(cwd);
            return (sets, project);
        }).ContinueWith(t =>
        {
            if (!t.IsCompletedSuccessfully)
                return;
            Dispatcher.UIThread.Post(() =>
            {
                if (!IsVisible || gen != _gen)
                    return;
                BuildFilePane(cwd, t.Result.sets, t.Result.project);
            });
        });
    }

    private void BuildFilePane(string cwd, MarkdownFileSets sets, MarkdownProjectFiles project)
    {
        var roots = new List<FileNode>();

        if (sets.Produced.Count > 0)
            roots.Add(SessionGroup($"Produced ({sets.Produced.Count})", cwd, sets.Produced, NodeKind.ProducedFile));
        if (sets.Referenced.Count > 0)
            roots.Add(SessionGroup($"Referenced ({sets.Referenced.Count})", cwd, sets.Referenced, NodeKind.ReferencedFile));

        roots.Add(BuildProjectTree(cwd, project));

        // Nothing anywhere (empty project, no session files) — say so rather than showing an empty tree.
        if (sets.IsEmpty && project.RelativePaths.Count == 0)
        {
            _filesPlaceholder.Text = "No Markdown files in this project.";
            _filePaneHost.Child = _filesPlaceholder;
            return;
        }

        _tree.ItemsSource = roots;
        _filePaneHost.Child = _tree;
    }

    // A flat group of session files (produced/referenced). Labels are relative to cwd when the file lives
    // under it (so "docs/plan.md"), else the bare filename; the full path is what gets opened.
    private static FileNode SessionGroup(string label, string cwd, IReadOnlyList<string> paths, NodeKind kind)
    {
        var group = new FileNode { Label = label, Kind = NodeKind.Group };
        foreach (var p in paths)
            group.Children.Add(new FileNode { Label = RelativeLabel(cwd, p), Kind = kind, FullPath = p });
        return group;
    }

    // The project's Markdown as a folder hierarchy built from the scan's relative paths.
    private static FileNode BuildProjectTree(string cwd, MarkdownProjectFiles project)
    {
        var root = new FileNode { Label = $"Project ({project.RelativePaths.Count})", Kind = NodeKind.Group };
        foreach (var rel in project.RelativePaths)
        {
            var parts = rel.Split('/');
            var cur = root;
            for (int i = 0; i < parts.Length; i++)
            {
                if (i == parts.Length - 1)
                {
                    var full = Path.Combine(cwd, rel.Replace('/', Path.DirectorySeparatorChar));
                    cur.Children.Add(new FileNode { Label = parts[i], Kind = NodeKind.ProjectFile, FullPath = full });
                }
                else
                {
                    var folder = cur.Children.FirstOrDefault(c => c.Kind == NodeKind.Folder && c.Label == parts[i]);
                    if (folder == null)
                    {
                        folder = new FileNode { Label = parts[i], Kind = NodeKind.Folder };
                        cur.Children.Add(folder);
                    }
                    cur = folder;
                }
            }
        }
        if (project.Truncated)
            root.Children.Add(new FileNode { Label = "…more (list truncated)", Kind = NodeKind.Info });
        return root;
    }

    private static string RelativeLabel(string cwd, string absolutePath)
    {
        try
        {
            var rel = Path.GetRelativePath(cwd, absolutePath);
            // GetRelativePath returns the input unchanged when it can't relativise (different root/OS), and
            // prefixes ".." when the file is outside cwd — in both cases fall back to the bare filename.
            if (rel.StartsWith("..") || Path.IsPathRooted(rel))
                return Path.GetFileName(absolutePath);
            return rel.Replace('\\', '/');
        }
        catch
        {
            return Path.GetFileName(absolutePath);
        }
    }

    private Control BuildNodeVisual(FileNode n)
    {
        var text = new TextBlock
        {
            Text = n.Label, FontSize = 12.5, VerticalAlignment = VerticalAlignment.Center,
            Foreground = n.Kind switch
            {
                NodeKind.Group          => Palette.TitleBrush,
                NodeKind.ReferencedFile => Palette.MutedBrush,
                NodeKind.Info           => Palette.MutedBrush,
                _                       => Palette.FgBrush,
            },
            FontWeight = n.Kind == NodeKind.Group ? FontWeight.SemiBold : FontWeight.Normal,
            FontStyle = n.Kind == NodeKind.Info ? FontStyle.Italic : FontStyle.Normal,
        };

        // File leaves carry a small bullet: rose for produced, muted for referenced, faint for project.
        if (n.Kind is NodeKind.ProducedFile or NodeKind.ReferencedFile or NodeKind.ProjectFile)
        {
            var dot = new Ellipse
            {
                Width = 6, Height = 6, Margin = new Thickness(0, 0, 7, 0),
                VerticalAlignment = VerticalAlignment.Center,
                Fill = n.Kind switch
                {
                    NodeKind.ProducedFile   => ProducedDotBrush,
                    NodeKind.ReferencedFile => Palette.MutedBrush,
                    _                       => Palette.BorderBrush,
                },
            };
            var sp = new StackPanel { Orientation = Orientation.Horizontal, Children = { dot, text } };
            ToolTip.SetTip(sp, n.FullPath);
            return sp;
        }

        return text;
    }

    // Selecting a different file while the buffer is dirty asks before discarding; if kept, the previous
    // selection is restored. Group/folder rows never load a file.
    private async void OnTreeSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_suppressSelect || e.AddedItems.Count == 0 || e.AddedItems[0] is not FileNode node)
            return;
        if (node.FullPath is not { } path)
            return;   // a group/folder header — leave the open file as-is

        if (_dirty && _currentFileNode is { } prev && !ReferenceEquals(prev, node))
        {
            bool discard = await ConfirmDialog.ShowAsync(this, "Discard changes?",
                $"'{Path.GetFileName(_openFilePath ?? "this file")}' has unsaved changes. Discard them?",
                "Discard changes", "Keep editing");
            if (!discard)
            {
                _suppressSelect = true;
                _tree.SelectedItem = prev;
                _suppressSelect = false;
                return;
            }
        }

        _currentFileNode = node;
        LoadFile(path);
    }

    // Read the selected file off the UI thread, then open it in the editor. Guarded by the generation token
    // so a slow read into a since-retargeted/closed window is dropped.
    private void LoadFile(string path)
    {
        int gen = _gen;
        Task.Run<(string? Text, DateTime? Mtime)>(() =>
        {
            try { return (File.ReadAllText(path), File.GetLastWriteTimeUtc(path)); }
            catch { return (null, null); }
        }).ContinueWith(t =>
        {
            if (!t.IsCompletedSuccessfully)
                return;
            Dispatcher.UIThread.Post(() =>
            {
                if (!IsVisible || gen != _gen)
                    return;
                OpenInEditor(path, t.Result.Text, t.Result.Mtime);
            });
        });
    }

    private void OpenInEditor(string path, string? text, DateTime? mtimeUtc)
    {
        StopWatcher();

        if (text is null)
        {
            _openFilePath = null;
            _dirty = false;
            _editorPlaceholder.Text = $"Couldn't read {Path.GetFileName(path)}.";
            _editorHost.Child = _editorPlaceholder;
            return;
        }

        _openFilePath = path;
        _openFileMtimeUtc = mtimeUtc;
        _loadedText = text;
        _dirty = false;
        _externalChange = false;

        _loading = true;
        _sourceBox.Text = text;
        _loading = false;

        _editorFileLabel.Text = RelativeLabel(_cwd ?? "", path);
        UpdateEditorChrome();
        RenderPreview(text);
        _editorHost.Child = _editorRoot;

        StartWatcher(path);
    }

    private void OnSourceTextChanged(object? sender, TextChangedEventArgs e)
    {
        if (_loading)
            return;
        _dirty = (_sourceBox.Text ?? "") != _loadedText;
        UpdateEditorChrome();
        _previewTimer.Stop();
        _previewTimer.Start();   // debounce: re-render the preview once typing settles
    }

    private void RenderPreview(string md)
    {
        var inlines = new InlineCollection();
        MarkdownRender.Append(inlines, md,
            Palette.FgBrush, Palette.MutedBrush, Palette.TealBrush, Palette.AccentBrush, Palette.TitleBrush);
        _previewBlock.Inlines = inlines;
    }

    private void UpdateEditorChrome()
    {
        _saveBtn.IsEnabled = _dirty;
        (_editorStatus.Text, _editorStatus.Foreground) = _externalChange
            ? ("changed on disk", Palette.WarnBrush)
            : (_dirty ? "● unsaved changes" : "", Palette.MutedBrush);
    }

    private async void Save()
    {
        if (_openFilePath is not { } path || !_dirty)
            return;

        // Conflict guard: if the file changed on disk since we loaded/last-saved it (the session may still
        // be editing it), confirm before overwriting.
        DateTime? current = null;
        try { current = File.GetLastWriteTimeUtc(path); } catch { }
        if (current is { } cur && _openFileMtimeUtc is { } loaded && cur > loaded)
        {
            bool overwrite = await ConfirmDialog.ShowAsync(this, "File changed on disk",
                $"'{Path.GetFileName(path)}' changed on disk since you opened it" +
                (_isActive ? " (the session may still be editing it)" : "") +
                ". Overwrite it with your version?",
                "Overwrite", "Cancel");
            if (!overwrite)
                return;
        }

        var text = _sourceBox.Text ?? "";
        int gen = _gen;
        bool ok = await Task.Run(() =>
        {
            try { File.WriteAllText(path, text); return true; }
            catch { return false; }
        });
        if (gen != _gen || !IsVisible || _openFilePath != path)
            return;

        if (ok)
        {
            _loadedText = text;
            _dirty = false;
            _externalChange = false;
            try { _openFileMtimeUtc = File.GetLastWriteTimeUtc(path); } catch { }   // so our own write doesn't read as an external change
            UpdateEditorChrome();
        }
        else
        {
            _editorStatus.Text = "Save failed.";
            _editorStatus.Foreground = Palette.ErrorBrush;
        }
    }

    private void StartWatcher(string path)
    {
        try
        {
            var dir = Path.GetDirectoryName(path);
            if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir))
                return;
            _watcher = new FileSystemWatcher(dir, Path.GetFileName(path))
            {
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size,
                EnableRaisingEvents = true,
            };
            _watcher.Changed += OnFileChangedOnDisk;
        }
        catch
        {
            _watcher = null;   // best-effort; the editor works without the reload nudge
        }
    }

    private void StopWatcher()
    {
        if (_watcher is { } w)
        {
            try { w.EnableRaisingEvents = false; w.Changed -= OnFileChangedOnDisk; w.Dispose(); } catch { }
            _watcher = null;
        }
    }

    // A live Claude session (or the user's own editor) wrote the open file. Marshalled to the UI thread:
    // if the buffer is clean, reload silently to the latest; if it has unsaved edits, flag it so the user
    // decides (and Save's conflict guard catches an overwrite).
    private void OnFileChangedOnDisk(object? sender, FileSystemEventArgs e)
    {
        var path = _openFilePath;
        if (path is null)
            return;
        Dispatcher.UIThread.Post(() =>
        {
            if (!IsVisible || _openFilePath != path)
                return;
            DateTime? cur = null;
            try { cur = File.GetLastWriteTimeUtc(path); } catch { }
            if (cur is not { } c || (_openFileMtimeUtc is { } loaded && c <= loaded))
                return;   // no real change (or our own just-saved write)

            if (_dirty)
            {
                _externalChange = true;
                UpdateEditorChrome();
            }
            else
            {
                LoadFile(path);   // clean buffer — safe to show the newest content
            }
        });
    }

    /// <summary>Render/verification seam: populate the file pane and open one file in the split editor
    /// synchronously (no async load) from the given data, in place. The caller then <c>Show()</c>s the
    /// window and captures a real rendered frame — which realises the tree/templates a detached one-shot
    /// bitmap can't. Exercises the real pane-building and editor code paths.</summary>
    internal void SeedForRender(string cwd, MarkdownFileSets sets, MarkdownProjectFiles project,
        string samplePath, string sampleMarkdown)
    {
        _cwd = cwd;
        BuildFilePane(cwd, sets, project);
        OpenInEditor(samplePath, sampleMarkdown, null);
    }

    /// <summary>Closes without the unsaved-changes prompt — for programmatic teardown (app exit, the update
    /// flow via <c>CloseAuxWindows</c>) where popping a modal confirm would be inappropriate.</summary>
    public void CloseWithoutPrompt()
    {
        _closeConfirmed = true;
        Close();
    }

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        base.OnClosing(e);
        if (e.Cancel)
            return;
        if (_closeConfirmed || !_dirty)
            return;
        e.Cancel = true;
        _ = ConfirmDiscardThenClose();
    }

    private async Task ConfirmDiscardThenClose()
    {
        bool discard = await ConfirmDialog.ShowAsync(this, "Discard changes?",
            $"'{Path.GetFileName(_openFilePath ?? "this file")}' has unsaved changes. Close without saving?",
            "Discard changes", "Keep editing");
        if (!discard)
            return;
        _closeConfirmed = true;
        Close();
    }

    protected override void OnClosed(EventArgs e)
    {
        _gen++;   // drop any results still in flight
        StopWatcher();
        _previewTimer.Stop();
        base.OnClosed(e);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.S && e.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            Save();
            e.Handled = true;
            return;
        }
        if (e.Key == Key.Escape)
        {
            Close();
            e.Handled = true;
        }
        base.OnKeyDown(e);
    }

    private enum NodeKind { Group, Folder, ProducedFile, ReferencedFile, ProjectFile, Info }

    private sealed class FileNode
    {
        public required string Label { get; init; }
        public required NodeKind Kind { get; init; }
        public string? FullPath { get; init; }   // absolute path for a file leaf; null for group/folder/info
        public List<FileNode> Children { get; } = new();
    }
}
