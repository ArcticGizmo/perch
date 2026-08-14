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
/// tree of the project's Markdown, and renders the selected file. Phase 4 turns the read-only preview into
/// an editable split (source + live preview) with save.
///
/// A single reused instance via <c>WindowHost.ShowOrFocus</c>; <see cref="Retarget"/> re-points it at a
/// different session without reopening. File IO runs off the UI thread and marshals back guarded by
/// <see cref="Visual.IsVisible"/> and a generation token, so a result arriving after the window closed or
/// was re-pointed is dropped (the <c>StatsWindow</c>/<c>GitTreeWindow</c> idiom). Built in code, themed
/// through <see cref="Palette"/>.
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
    private readonly Border _editorHost;           // right: rendered preview (Phase 4: split editor)
    private readonly TextBlock _editorPlaceholder;

    private string? _cwd;
    private string? _sessionId;
    private bool _isActive;   // the session was working (Running/AwaitingInput) at last Retarget
    // Bumped on every Retarget/close so an in-flight off-thread load knows its results are stale and drops
    // them rather than painting into a window that has moved on.
    private int _gen;

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
        _tree.SelectionChanged += (_, e) =>
        {
            if (e.AddedItems.Count > 0 && e.AddedItems[0] is FileNode { FullPath: { } path })
                LoadFile(path);
        };

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

        // ── Right: the preview ─────────────────────────────────────────────────────────────────────
        _editorPlaceholder = new TextBlock
        {
            Text = "Select a file to view it.", FontSize = 12.5, Foreground = Palette.MutedBrush,
            Margin = new Thickness(18), HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        _editorHost = new Border { Background = Palette.SurfaceSunkenBrush, Child = _editorPlaceholder };

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

    /// <summary>
    /// Re-point the window at a session's working directory and reload. Called on first open and every
    /// reuse via <c>WindowHost.ShowOrFocus</c>. <paramref name="isActive"/> is true when the session may
    /// still be writing to these files (Running/AwaitingInput), which the editor uses to guard overwrites.
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

        int gen = _gen;
        _tree.ItemsSource = null;
        _filesPlaceholder.Text = "Loading Markdown files…";
        _filePaneHost.Child = _filesPlaceholder;
        _editorHost.Child = _editorPlaceholder;

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

    // Read the selected file off the UI thread and show it rendered (read-only for now). Guarded by the
    // generation token so a slow read into a since-retargeted/closed window is dropped.
    private void LoadFile(string path)
    {
        int gen = _gen;
        Task.Run<string?>(() =>
        {
            try { return File.ReadAllText(path); }
            catch { return null; }
        }).ContinueWith(t =>
        {
            if (!t.IsCompletedSuccessfully)
                return;
            Dispatcher.UIThread.Post(() =>
            {
                if (!IsVisible || gen != _gen)
                    return;
                ShowContent(path, t.Result);
            });
        });
    }

    private void ShowContent(string path, string? text)
    {
        if (text is null)
        {
            _editorPlaceholder.Text = $"Couldn't read {Path.GetFileName(path)}.";
            _editorHost.Child = _editorPlaceholder;
            return;
        }

        var body = new SelectableTextBlock
        {
            TextWrapping = TextWrapping.Wrap, Foreground = Palette.FgBrush, FontSize = 13,
            Margin = new Thickness(22, 18),
        };
        var inlines = new InlineCollection();
        MarkdownRender.Append(inlines, text,
            Palette.FgBrush, Palette.MutedBrush, Palette.TealBrush, Palette.AccentBrush, Palette.TitleBrush);
        body.Inlines = inlines;

        _editorHost.Child = new ScrollViewer
        {
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Content = body,
        };
    }

    /// <summary>Render/verification seam: populate the two-pane body synchronously (no async load) from the
    /// given data, in place. The caller then <c>Show()</c>s the window and captures a real rendered frame —
    /// which realises the tree/templates a detached one-shot bitmap can't. Exercises the real pane-building
    /// and preview code paths.</summary>
    internal void SeedForRender(string cwd, MarkdownFileSets sets, MarkdownProjectFiles project,
        string sampleName, string sampleMarkdown)
    {
        _cwd = cwd;
        BuildFilePane(cwd, sets, project);
        ShowContent(sampleName, sampleMarkdown);
    }

    protected override void OnClosed(EventArgs e)
    {
        _gen++;   // drop any results still in flight
        base.OnClosed(e);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
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
