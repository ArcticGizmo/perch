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
/// The Markdown viewer/editor. Opened from a session's right-click "Markdown files…" item (or a click on
/// its overlay glyph), it lists the <c>.md</c> files that session produced (rose) or referenced (muted)
/// plus a <c>.gitignore</c>-respecting tree of the project's Markdown, filterable by a search box, and
/// edits the selected file in a split view — a source editor on the left, a live rendered preview on the
/// right — with save.
///
/// It carries its own light/dark theme, independent of the app theme, and a <em>separate</em> theme for the
/// preview pane (defaulting to light, so rendered Markdown reads like paper) — both toggled from the header.
///
/// A single reused instance via <c>WindowHost.ShowOrFocus</c>; <see cref="Retarget"/> re-points it at a
/// different session without reopening. File IO runs off the UI thread and marshals back guarded by
/// <see cref="Visual.IsVisible"/> and a generation token, so a result arriving after the window closed or
/// was re-pointed is dropped (the <c>StatsWindow</c>/<c>GitTreeWindow</c> idiom). Save guards an on-disk
/// change (an mtime conflict — the session may still be editing the file) behind a confirm; an external
/// edit while the buffer is clean reloads silently.
/// </summary>
internal sealed class MarkdownWindow : Window
{
    private static readonly FontFamily Mono = new("Cascadia Code, Consolas, Menlo, monospace");
    // The rose that marks "produced" files, matching the overlay's Markdown glyph. Fixed across themes.
    private static readonly IBrush ProducedDotBrush = new SolidColorBrush(Color.FromRgb(244, 114, 182));

    private readonly AppSettings _settings;

    // Per-window themes. _theme is the window chrome (dark by default, matching the app); _previewTheme is
    // the preview pane's own theme (light by default — Markdown reads better on paper). Both toggle freely.
    private MdTheme _theme = MdTheme.Dark();
    private MdTheme _previewTheme = MdTheme.Light();
    private bool _windowLight;
    private bool _previewLight = true;

    private readonly TextBlock _titleText;
    private readonly TextBlock _subText;
    private readonly Border _header;
    private readonly Button _windowThemeBtn;
    private readonly Button _previewThemeBtn;

    private readonly Border _filePaneHost;         // left: search + tree, or a placeholder
    private readonly Control _paneContent;         // search box over the tree
    private readonly TextBox _searchBox;
    private readonly TreeView _tree;
    private readonly TextBlock _filesPlaceholder;
    private readonly GridSplitter _bodySplitter;

    private readonly Border _editorHost;           // right: split editor / placeholder
    private readonly Control _editorRoot;
    private readonly Border _editorToolbar;
    private readonly GridSplitter _innerSplitter;
    private readonly Border _previewPane;          // wraps the preview scroll so its "paper" bg can retint
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

    // Cached pane data, so the search box can re-filter without re-scanning.
    private string? _paneCwd;
    private MarkdownFileSets? _paneSets;
    private MarkdownProjectFiles? _paneProject;

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
        MinWidth = 760;
        MinHeight = 460;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;

        // ── Header: title + subtitle (left), theme toggles (right) ─────────────────────────────────
        _titleText = new TextBlock
        {
            Text = "Markdown", FontSize = 15, FontWeight = FontWeight.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
        };
        _subText = new TextBlock
        {
            Text = "", FontSize = 11.5, FontFamily = Mono,
            VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 0, 0),
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        _windowThemeBtn = SettingsUi.FlatButton("");
        _windowThemeBtn.Click += (_, _) => { _windowLight = !_windowLight; ApplyWindowTheme(); };
        _previewThemeBtn = SettingsUi.FlatButton("");
        _previewThemeBtn.Click += (_, _) => { _previewLight = !_previewLight; ApplyPreviewTheme(); };

        var titleStack = new StackPanel { Orientation = Orientation.Horizontal, Children = { _titleText, _subText } };
        var toggles = new StackPanel
        {
            Orientation = Orientation.Horizontal, Spacing = 8,
            VerticalAlignment = VerticalAlignment.Center,
            Children = { _windowThemeBtn, _previewThemeBtn },
        };
        var headerPanel = new DockPanel();
        DockPanel.SetDock(toggles, Dock.Right);
        headerPanel.Children.Add(toggles);
        headerPanel.Children.Add(titleStack);
        _header = new Border
        {
            [DockPanel.DockProperty] = Dock.Top,
            BorderThickness = new Thickness(0, 0, 0, 1),
            Padding = new Thickness(16, 8),
            Child = headerPanel,
        };

        // ── Left: search over the file tree ────────────────────────────────────────────────────────
        _searchBox = new TextBox
        {
            [DockPanel.DockProperty] = Dock.Top,
            PlaceholderText = "Search files…", FontSize = 12,
            BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(5),
            Padding = new Thickness(8, 5), Margin = new Thickness(8, 8, 8, 6),
        };
        _searchBox.TextChanged += (_, _) => RebuildPane();

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

        _paneContent = new DockPanel { LastChildFill = true, Children = { _searchBox, _tree } };

        _filesPlaceholder = new TextBlock
        {
            Text = "Loading Markdown files…", FontSize = 12,
            Margin = new Thickness(14), TextWrapping = TextWrapping.Wrap,
        };
        _filePaneHost = new Border
        {
            BorderThickness = new Thickness(0, 0, 1, 0),
            Child = _filesPlaceholder,
        };

        // ── Right: the split editor (built once, shown on first file open) ─────────────────────────
        _editorPlaceholder = new TextBlock
        {
            Text = "Select a file to view it.", FontSize = 12.5,
            Margin = new Thickness(18), HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        _editorFileLabel = new TextBlock
        {
            Text = "", FontSize = 12, FontFamily = Mono,
            VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis,
        };
        _editorStatus = new TextBlock
        {
            Text = "", FontSize = 11.5, VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 10, 0),
        };
        _saveBtn = SettingsUi.FlatButton("Save");
        _saveBtn.IsEnabled = false;
        _saveBtn.Click += (_, _) => Save();

        _sourceBox = new TextBox
        {
            AcceptsReturn = true, AcceptsTab = true, TextWrapping = TextWrapping.Wrap,
            FontFamily = Mono, FontSize = 12.5,
            BorderThickness = new Thickness(0), CornerRadius = new CornerRadius(0),
            Padding = new Thickness(12, 10), VerticalContentAlignment = VerticalAlignment.Top,
            [ScrollViewer.VerticalScrollBarVisibilityProperty] = ScrollBarVisibility.Auto,
        };
        _sourceBox.TextChanged += OnSourceTextChanged;

        _previewBlock = new SelectableTextBlock
        {
            TextWrapping = TextWrapping.Wrap, FontSize = 13, Margin = new Thickness(18, 14),
        };
        _previewTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(180) };
        _previewTimer.Tick += (_, _) => { _previewTimer.Stop(); RenderPreview(_sourceBox.Text ?? ""); };

        _editorToolbar = new Border { [DockPanel.DockProperty] = Dock.Top, BorderThickness = new Thickness(0, 0, 0, 1), Padding = new Thickness(12, 6) };
        _innerSplitter = new GridSplitter { Width = 4, ResizeDirection = GridResizeDirection.Columns, HorizontalAlignment = HorizontalAlignment.Center };
        _previewPane = new Border();
        _editorRoot = BuildEditorRoot();
        _editorHost = new Border { Child = _editorPlaceholder };

        // ── Body: file pane | splitter | editor ────────────────────────────────────────────────────
        var body = new Grid { ColumnDefinitions = new ColumnDefinitions("300,Auto,*") };
        Grid.SetColumn(_filePaneHost, 0);
        _bodySplitter = new GridSplitter { Width = 4, ResizeDirection = GridResizeDirection.Columns, HorizontalAlignment = HorizontalAlignment.Center };
        Grid.SetColumn(_bodySplitter, 1);
        Grid.SetColumn(_editorHost, 2);
        body.Children.Add(_filePaneHost);
        body.Children.Add(_bodySplitter);
        body.Children.Add(_editorHost);

        Content = new DockPanel { LastChildFill = true, Children = { _header, body } };

        ApplyWindowTheme();
        ApplyPreviewTheme();
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
        _editorToolbar.Child = toolbarPanel;

        var previewScroll = new ScrollViewer
        {
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Content = _previewBlock,
        };
        _previewPane.Child = previewScroll;

        var split = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto,*") };
        Grid.SetColumn(_sourceBox, 0);
        Grid.SetColumn(_innerSplitter, 1);
        Grid.SetColumn(_previewPane, 2);
        split.Children.Add(_sourceBox);
        split.Children.Add(_innerSplitter);
        split.Children.Add(_previewPane);

        return new DockPanel { LastChildFill = true, Children = { _editorToolbar, split } };
    }

    // ── Theming ──────────────────────────────────────────────────────────────────────────────────

    // Recolour every chrome control from the current window theme and rebuild the tree (its node visuals
    // capture brushes at build time, so recolouring means re-running the pane).
    private void ApplyWindowTheme()
    {
        _theme = _windowLight ? MdTheme.Light() : MdTheme.Dark();
        var t = _theme;

        Background = t.WindowBg;
        _header.Background = t.PaneBg;
        _header.BorderBrush = t.Separator;
        _titleText.Foreground = t.Title;
        _subText.Foreground = t.Muted;

        _filePaneHost.Background = t.PaneBg;
        _filePaneHost.BorderBrush = t.Separator;
        _filesPlaceholder.Foreground = t.Muted;
        _searchBox.Background = t.EditorBg;
        _searchBox.Foreground = t.Fg;
        _searchBox.BorderBrush = t.Border;

        _bodySplitter.Background = t.Separator;
        _innerSplitter.Background = t.Separator;

        _editorHost.Background = t.WindowBg;
        _editorToolbar.Background = t.PaneBg;
        _editorToolbar.BorderBrush = t.Separator;
        _editorFileLabel.Foreground = t.Muted;
        _editorPlaceholder.Foreground = t.Muted;
        _sourceBox.Background = t.EditorBg;
        _sourceBox.Foreground = t.Fg;

        StyleButton(_windowThemeBtn, t);
        StyleButton(_previewThemeBtn, t);
        StyleButton(_saveBtn, t);
        _windowThemeBtn.Content = _windowLight ? "Window: Light" : "Window: Dark";
        _previewThemeBtn.Content = _previewLight ? "Preview: Light" : "Preview: Dark";

        UpdateEditorChrome();
        RebuildPane();
    }

    // The preview pane has its own theme (default light). Retint its "paper" background and re-render.
    private void ApplyPreviewTheme()
    {
        _previewTheme = _previewLight ? MdTheme.Light() : MdTheme.Dark();
        _previewPane.Background = _previewTheme.Paper;
        _previewThemeBtn.Content = _previewLight ? "Preview: Light" : "Preview: Dark";
        RenderPreview(_sourceBox.Text ?? "");
    }

    private static void StyleButton(Button b, MdTheme t)
    {
        b.Background = t.ButtonBg;
        b.Foreground = t.Fg;
        b.FontSize = 12;
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
        _paneSets = null;
        _paneProject = null;
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
                _paneCwd = cwd;
                _paneSets = t.Result.sets;
                _paneProject = t.Result.project;
                RebuildPane();
            });
        });
    }

    // (Re)build the file tree from the cached scan, applying the current search filter. Cheap — no rescan.
    private void RebuildPane()
    {
        if (_paneSets is not { } sets || _paneProject is not { } project || _paneCwd is not { } cwd)
            return;

        var query = (_searchBox.Text ?? "").Trim();
        var roots = new List<FileNode>();

        var produced = FilterSession(cwd, sets.Produced, query);
        var referenced = FilterSession(cwd, sets.Referenced, query);
        if (produced.Count > 0)
            roots.Add(SessionGroup($"Produced ({produced.Count})", cwd, produced, NodeKind.ProducedFile));
        if (referenced.Count > 0)
            roots.Add(SessionGroup($"Referenced ({referenced.Count})", cwd, referenced, NodeKind.ReferencedFile));

        var projectPaths = query.Length == 0
            ? project.RelativePaths
            : project.RelativePaths.Where(p => p.Contains(query, StringComparison.OrdinalIgnoreCase)).ToList();
        if (projectPaths.Count > 0)
            roots.Add(BuildProjectTree(cwd, projectPaths, project.Truncated && query.Length == 0));

        if (roots.Count == 0)
        {
            _filesPlaceholder.Text = query.Length > 0
                ? $"No Markdown files match “{query}”."
                : "No Markdown files in this project.";
            _filePaneHost.Child = _filesPlaceholder;
            return;
        }

        _tree.ItemsSource = roots;
        _filePaneHost.Child = _paneContent;
    }

    // Session files (absolute paths) matching the query by relative label or full path.
    private static List<string> FilterSession(string cwd, IReadOnlyList<string> paths, string query)
    {
        if (query.Length == 0)
            return paths.ToList();
        return paths.Where(p =>
            RelativeLabel(cwd, p).Contains(query, StringComparison.OrdinalIgnoreCase)
            || p.Contains(query, StringComparison.OrdinalIgnoreCase)).ToList();
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

    // The project's Markdown as a folder hierarchy built from (already-filtered) relative paths. Folders
    // with no surviving files are naturally absent, since only matching paths are inserted.
    private static FileNode BuildProjectTree(string cwd, IReadOnlyList<string> rels, bool truncated)
    {
        var root = new FileNode { Label = $"Project ({rels.Count})", Kind = NodeKind.Group };
        foreach (var rel in rels)
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
        if (truncated)
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
                NodeKind.Group          => _theme.Title,
                NodeKind.ReferencedFile => _theme.Muted,
                NodeKind.Info           => _theme.Muted,
                _                       => _theme.Fg,
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
                    NodeKind.ReferencedFile => _theme.Muted,
                    _                       => _theme.Border,
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
        var p = _previewTheme;
        _previewBlock.Foreground = p.Fg;
        var inlines = new InlineCollection();
        MarkdownRender.Append(inlines, md, p.Fg, p.Muted, p.Code, p.Accent, p.Title);
        _previewBlock.Inlines = inlines;
    }

    private void UpdateEditorChrome()
    {
        _saveBtn.IsEnabled = _dirty;
        (_editorStatus.Text, _editorStatus.Foreground) = _externalChange
            ? ("changed on disk", _theme.Warn)
            : (_dirty ? "● unsaved changes" : "", _theme.Muted);
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
            _editorStatus.Foreground = _theme.Error;
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
        string samplePath, string sampleMarkdown, bool windowLight = false, bool previewLight = true)
    {
        _windowLight = windowLight;
        _previewLight = previewLight;
        ApplyWindowTheme();
        ApplyPreviewTheme();
        _cwd = cwd;
        _paneCwd = cwd;
        _paneSets = sets;
        _paneProject = project;
        RebuildPane();
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
            // Don't steal Escape from the search box (clearing a filter shouldn't close the window).
            if (_searchBox.IsFocused && !string.IsNullOrEmpty(_searchBox.Text))
            {
                _searchBox.Text = "";
                e.Handled = true;
                return;
            }
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

    // A minimal per-window palette. Two instances (Dark/Light) drive the window chrome and, independently,
    // the preview pane. Kept local so the toggles retint just this window rather than the app theme.
    private readonly record struct MdTheme(
        IBrush WindowBg, IBrush PaneBg, IBrush EditorBg, IBrush Paper, IBrush Separator, IBrush Border,
        IBrush Fg, IBrush Muted, IBrush Title, IBrush Accent, IBrush Code, IBrush Warn, IBrush Error,
        IBrush ButtonBg)
    {
        private static SolidColorBrush B(byte r, byte g, byte b) => new(Color.FromRgb(r, g, b));

        public static MdTheme Dark() => new(
            WindowBg: B(0x1A, 0x1B, 0x24), PaneBg: B(0x22, 0x24, 0x2E), EditorBg: B(0x1E, 0x1F, 0x29),
            Paper: B(0x1E, 0x1F, 0x29), Separator: B(0x33, 0x35, 0x40), Border: B(0x44, 0x47, 0x54),
            Fg: B(0xE6, 0xE8, 0xF0), Muted: B(0x9A, 0x9E, 0xAD), Title: B(0xF2, 0xF4, 0xFA),
            Accent: B(0x6E, 0x9B, 0xF0), Code: B(0x5E, 0xD6, 0xC5), Warn: B(0xF5, 0x9E, 0x0B),
            Error: B(0xEF, 0x44, 0x44), ButtonBg: B(0x2A, 0x2C, 0x38));

        public static MdTheme Light() => new(
            WindowBg: B(0xEC, 0xED, 0xF1), PaneBg: B(0xF4, 0xF5, 0xF8), EditorBg: B(0xFB, 0xFB, 0xFD),
            Paper: B(0xFF, 0xFF, 0xFF), Separator: B(0xD6, 0xD8, 0xDF), Border: B(0xC2, 0xC6, 0xD0),
            Fg: B(0x22, 0x24, 0x2B), Muted: B(0x66, 0x6A, 0x76), Title: B(0x14, 0x16, 0x1C),
            Accent: B(0x2B, 0x63, 0xC7), Code: B(0x0E, 0x7C, 0x66), Warn: B(0xB4, 0x6A, 0x00),
            Error: B(0xC0, 0x2B, 0x2B), ButtonBg: B(0xE2, 0xE4, 0xEA));
    }
}
