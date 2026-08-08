using System.IO;
using System.Linq;
using System.Text;
using Avalonia;
using Avalonia.Collections;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Shapes;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using Perch.Avalonia.Theming;
using Perch.Avalonia.Views;
using Perch.Data;

namespace Perch.Avalonia.Windows;

/// <summary>
/// The git <b>Tree</b> window (Phase 1 of the git-flow feature — see
/// <c>docs/git-tree-window-design.html</c>). It replaces the read-only "Review changes" window with a
/// GitKraken-style, three-pane view scoped to the current branch: a scrollable list of commit nodes on the
/// left (a working-tree "WIP" node at the tip when the tree is dirty, then the branch's own commits back to
/// where it diverged from its base), the files that the selected node touched in the middle, and the diff
/// (<see cref="DiffView"/>) on the right. Selecting a node loads its files; selecting a file shows its diff.
///
/// The branch is scoped against a base ref chosen by <see cref="GitRepoService.GetBaseRefCandidates"/> —
/// preferring an <c>upstream</c> remote (the fork convention) — and overridable from the header's base
/// picker. Phase 2 adds inline commit authoring at the WIP node; Phase 3 the full multi-branch graph
/// (the ghosted "Expand to full tree" button).
///
/// A single reused instance (via <c>WindowHost.ShowOrFocus</c>); <see cref="Retarget"/> re-points it at a
/// different session without reopening. All git work runs off the UI thread and marshals back guarded by
/// <see cref="Visual.IsVisible"/> and a generation token, so a result arriving after the window closed or
/// was re-pointed is dropped (the <c>GitReviewWindow</c>/<c>StatsWindow</c> idiom).
/// </summary>
internal sealed class GitTreeWindow : Window
{
    private static readonly FontFamily Mono = new("Cascadia Code, Consolas, Menlo, monospace");

    // The branch names treated as a trunk: on one of these we show recent history unscoped rather than
    // scoping "trunk..HEAD" (which would only show unpushed commits). Mirrors GitRepoService.TrunkNames.
    private static readonly string[] TrunkNames = ["main", "master", "trunk", "develop"];

    private const int RecentCount = 50;   // unscoped fallback
    private const int ScopedCount = 300;  // bound on a branch's own commits since divergence

    private readonly GitRepoService _git = new();
    private readonly AppSettings _settings;

    private readonly TextBlock _titleText;
    private readonly TextBlock _subText;
    private readonly Button _prChip;
    private readonly Button _baseBtn;
    private readonly MenuFlyout _baseFlyout;
    private readonly Button _unifiedBtn;
    private readonly Button _splitBtn;
    private readonly Button _themeBtn;
    private readonly Button _expandBtn;
    private readonly Button _stageLinesBtn;
    private readonly CheckBox _wrapCheck;
    private readonly Border _headerBorder;
    private readonly TextBlock _graphLabel;
    private readonly TextBlock _filesLabel;
    private readonly TextBlock _composerLabel;
    private readonly ListBox _nodesList;
    private readonly ListBox _filesList;
    private readonly Border _composer;
    private readonly TextBox _msgBox;
    private readonly Button _commitBtn;
    private readonly TextBlock _composerHint;
    private readonly DiffView _diff;
    private readonly ScrollViewer _diffScroll;
    private readonly Border _findBar;
    private readonly TextBox _findBox;
    private readonly TextBlock _findLabel;
    private readonly TextBlock _nodesEmpty;
    private readonly TextBlock _filesEmpty;
    private readonly TextBlock _diffPlaceholder;
    private readonly List<GridSplitter> _splitters = new();

    // Per-window light/dark. _pal is the local palette every colour in this window (and its DiffView) reads
    // from, so the toggle re-themes just this window — the overlay and other windows keep the app theme.
    private TreePalette _pal;
    private bool _light;

    private readonly FuncDataTemplate<TreeNode> _nodeTemplate;
    private readonly FuncDataTemplate<GitFileChange> _wipFileTemplate;
    private readonly FuncDataTemplate<GitDiffFile> _commitFileTemplate;

    private string? _cwd;
    private string? _prUrl;
    private string? _branch;
    private bool _isActive; // the session was working (Running/AwaitingInput) at last Retarget — commit guard
    private bool _split;
    private bool _wrap;
    private int _gen;
    private bool _suppressSelect;

    // Base-ref scoping. _baseOverride: null = auto-pick, "" = explicitly unscoped ("recent"), else a ref.
    private string? _baseOverride;
    private IReadOnlyList<string> _baseCandidates = [];
    private string? _effectiveBase; // what the current node list is actually scoped to (null = unscoped)

    // Auto-refresh (filesystem watcher, debounced).
    private FileSystemWatcher? _watcher;
    private DispatcherTimer? _debounce;

    // What is currently shown, so an auto-refresh updates only what changed and leaves the diff (and its
    // scroll) untouched otherwise.
    private GitRepoStatus? _lastStatus;
    private IReadOnlyList<GitCommit> _lastCommits = [];
    private IReadOnlyList<TreeNode> _nodes = [];
    private GitDiff? _selectedCommitDiff; // the selected commit node's whole diff, so file clicks are instant
    private string _shownDiffSig = "";
    private HashSet<string> _invisiblePaths = new(StringComparer.Ordinal);

    internal enum NodeKind { Wip, Commit, Base }

    /// <summary>A row in the left graph: the working-tree (WIP) node, one commit, or the terminal base node
    /// (the ref the branch is scoped against, e.g. <c>origin/main</c>). <see cref="IsFirst"/>/<see cref="IsLast"/>
    /// trim the lane rail so it doesn't run past the top of the first knot or below the last one. Internal so
    /// the headless render harness can build sample rows via <see cref="NodeRow"/>.</summary>
    internal sealed record TreeNode(
        NodeKind Kind, bool IsHead, bool IsFirst, bool IsLast, GitCommit Commit, int ChangeCount, string? Label)
    {
        public bool IsWip => Kind == NodeKind.Wip;
        public bool IsBase => Kind == NodeKind.Base;
        public string Key => Kind switch
        {
            NodeKind.Wip => "\0wip",
            NodeKind.Base => "\0base",
            _ => Commit.Hash,
        };
    }

    public GitTreeWindow(AppSettings settings)
    {
        _settings = settings;
        _split = settings.GitReviewSplitView;
        _wrap = settings.GitReviewWrap;
        _light = settings.GitTreeLight;
        _pal = _light ? TreePalette.Light() : TreePalette.Dark();
        Title = "Tree";
        Width = 1560;
        Height = 1020;
        MinWidth = 820;
        MinHeight = 460;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;

        _nodeTemplate = NodeTemplate();
        _wipFileTemplate = WipFileTemplate();
        _commitFileTemplate = CommitFileTemplate();

        // ---- header ----
        _titleText = new TextBlock { Foreground = Palette.TitleBrush, FontSize = 15, FontWeight = FontWeight.SemiBold };
        _subText = new TextBlock { Foreground = Palette.MutedBrush, FontSize = 12, Margin = new Thickness(0, 2, 0, 0) };

        _prChip = new Button
        {
            IsVisible = false, VerticalAlignment = VerticalAlignment.Center, Padding = new Thickness(10, 4),
            Background = new SolidColorBrush(Palette.Accent), Foreground = Palette.OnAccentBrush,
            [DockPanel.DockProperty] = Dock.Right,
        };
        _prChip.Click += (_, _) => { if (_prUrl is { } url) PlatformServices.UrlOpener.Open(url); };

        // Base-ref picker: "since ⑂ <base>" (or "recent" when unscoped). Its flyout is rebuilt from the
        // discovered candidates each load.
        _baseFlyout = new MenuFlyout { Placement = PlacementMode.Bottom };
        _baseBtn = new Button
        {
            VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0),
            Flyout = _baseFlyout, [DockPanel.DockProperty] = Dock.Left,
        };

        var refreshBtn = new Button
        {
            Content = "Refresh", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0),
            [DockPanel.DockProperty] = Dock.Right,
        };
        refreshBtn.Click += (_, _) => RefreshStatus(preserveSelection: true);

        _unifiedBtn = ModeButton("Unified", split: false);
        _splitBtn = ModeButton("Split", split: true);
        var modeGroup = new StackPanel
        {
            Orientation = Orientation.Horizontal, Spacing = 4, VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(8, 0, 0, 0), [DockPanel.DockProperty] = Dock.Right,
            Children = { _unifiedBtn, _splitBtn },
        };

        _wrapCheck = new CheckBox
        {
            Content = "Wrap", IsChecked = _wrap, VerticalAlignment = VerticalAlignment.Center,
            Foreground = Palette.FgBrush, Margin = new Thickness(8, 0, 0, 0), [DockPanel.DockProperty] = Dock.Right,
        };
        _wrapCheck.IsCheckedChanged += (_, _) => SetWrap(_wrapCheck.IsChecked == true);

        // Stage/unstage the selected diff lines. Disabled until a line selection sits within one stageable
        // hunk (select line numbers in a working-tree section, then click).
        _stageLinesBtn = new Button
        {
            Content = "Stage lines", IsEnabled = false, VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(8, 0, 0, 0), [DockPanel.DockProperty] = Dock.Right,
        };
        ToolTip.SetTip(_stageLinesBtn, "Select line numbers in the working-tree diff, then stage/unstage just those lines");
        // Click wired after _diff is constructed (below), so it can't dereference a not-yet-set field.

        // Per-window light/dark toggle — reading a lot of diff/commit text is easier when this window can go
        // light without flipping the always-on-top overlay.
        _themeBtn = new Button
        {
            VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0),
            [DockPanel.DockProperty] = Dock.Right,
        };
        _themeBtn.Click += (_, _) => ToggleLight();
        ToolTip.SetTip(_themeBtn, "Light / dark — affects this window only");

        // Ghosted Phase-3 affordance: the full multi-branch graph. Present so the destination is visible; a
        // tooltip says why it's disabled.
        _expandBtn = new Button
        {
            Content = "⤢ Expand to full tree", IsEnabled = false, VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(8, 0, 0, 0), Foreground = Palette.MutedBrush,
            [DockPanel.DockProperty] = Dock.Right,
        };
        ToolTip.SetTip(_expandBtn, "The all-branches graph is coming in a later pass.");

        var headerText = new StackPanel { Orientation = Orientation.Vertical, Children = { _titleText, _subText } };
        _headerBorder = new Border
        {
            Padding = new Thickness(14, 10),
            [DockPanel.DockProperty] = Dock.Top,
            Child = new DockPanel
            {
                LastChildFill = true,
                Children = { _prChip, refreshBtn, modeGroup, _wrapCheck, _stageLinesBtn, _themeBtn, _expandBtn, _baseBtn, headerText },
            },
        };

        // ---- pane 1: graph nodes ----
        _nodesList = MakeList(_nodeTemplate);
        _nodesList.SelectionChanged += OnNodeSelected;
        // The terminal base node is a reference marker, not a destination — disable its container so it can't
        // be clicked or keyboard-selected.
        _nodesList.ContainerPrepared += (_, e) =>
        {
            if (e.Container is ListBoxItem lbi && e.Index >= 0 && e.Index < _nodes.Count)
                lbi.IsEnabled = !_nodes[e.Index].IsBase;
        };
        _nodesEmpty = EmptyHint("No commits");
        _graphLabel = PaneLabel("Commits · this branch");
        var graphPane = new DockPanel
        {
            LastChildFill = true,
            Children = { _graphLabel, new Grid { Children = { _nodesList, _nodesEmpty } } },
        };

        // ---- pane 2: files in the selected node, plus the WIP commit composer docked at the bottom ----
        _filesList = MakeList(_wipFileTemplate);
        _filesList.SelectionChanged += OnFileSelected;
        _filesEmpty = EmptyHint("Select a node");

        _msgBox = new TextBox
        {
            PlaceholderText = "Commit message… (first line = summary)",
            AcceptsReturn = true, AcceptsTab = false, TextWrapping = TextWrapping.Wrap,
            MinHeight = 60, MaxHeight = 160, FontFamily = Mono, FontSize = 12.5,
            VerticalContentAlignment = VerticalAlignment.Top,
            [ScrollViewer.VerticalScrollBarVisibilityProperty] = ScrollBarVisibility.Auto,
        };
        _commitBtn = new Button
        {
            Content = "Commit", Background = new SolidColorBrush(Palette.Accent), Foreground = Palette.OnAccentBrush,
            Padding = new Thickness(14, 5), VerticalAlignment = VerticalAlignment.Center,
        };
        _commitBtn.Click += OnCommitClick;
        _composerHint = new TextBlock
        {
            Text = "", Foreground = Palette.MutedBrush, FontSize = 12,
            VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 0, 0),
            TextWrapping = TextWrapping.Wrap,
        };
        var commitRow = new DockPanel { LastChildFill = true, Margin = new Thickness(0, 8, 0, 0) };
        DockPanel.SetDock(_commitBtn, Dock.Left);
        commitRow.Children.Add(_commitBtn);
        commitRow.Children.Add(_composerHint);
        _composerLabel = new TextBlock
        {
            Text = "Commit staged changes", Foreground = Palette.MutedBrush, FontSize = 11,
            FontWeight = FontWeight.SemiBold, Margin = new Thickness(0, 0, 0, 6),
        };
        _composer = new Border
        {
            Padding = new Thickness(12), IsVisible = false,
            [DockPanel.DockProperty] = Dock.Bottom,
            Child = new StackPanel { Children = { _composerLabel, _msgBox, commitRow } },
        };

        _filesLabel = PaneLabel("Files");
        var filesArea = new Grid { Children = { _filesList, _filesEmpty } };
        var filesPane = new DockPanel { LastChildFill = true, Children = { _filesLabel, _composer, filesArea } };

        // ---- pane 3: find bar + diff ----
        _diff = new DiffView();
        _diff.SetSplit(_split);
        _diff.SetWrap(_wrap);
        _diff.SearchResultsChanged += OnSearchResults;
        _diff.HunkStageRequested += OnHunkStage;
        _diff.LineStageStateChanged += OnLineStageState;
        _diff.LineStageRequested += OnLineStageRequest;
        _stageLinesBtn.Click += (_, _) => _diff.RequestStageSelection();
        UpdateModeButtons();
        _diffScroll = new ScrollViewer
        {
            Content = _diff,
            HorizontalScrollBarVisibility = _wrap ? ScrollBarVisibility.Disabled : ScrollBarVisibility.Auto,
        };

        _findBox = new TextBox { PlaceholderText = "Find in diff", MinWidth = 220, VerticalAlignment = VerticalAlignment.Center };
        _findBox.TextChanged += (_, _) => _diff.SetSearch(_findBox.Text ?? "");
        _findBox.KeyDown += OnFindBoxKeyDown;
        _findLabel = new TextBlock
        {
            Text = "", Foreground = Palette.MutedBrush, VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(10, 0), MinWidth = 48, [DockPanel.DockProperty] = Dock.Right,
        };
        var findPrev = new Button { Content = "‹", VerticalAlignment = VerticalAlignment.Center, [DockPanel.DockProperty] = Dock.Right };
        findPrev.Click += (_, _) => _diff.PrevMatch();
        var findNext = new Button { Content = "›", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(4, 0, 0, 0), [DockPanel.DockProperty] = Dock.Right };
        findNext.Click += (_, _) => _diff.NextMatch();
        var findClose = new Button { Content = "✕", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0), [DockPanel.DockProperty] = Dock.Right };
        findClose.Click += (_, _) => CloseFind();
        _findBar = new Border
        {
            Background = Palette.FormBgBrush, Padding = new Thickness(10, 6), IsVisible = false,
            [DockPanel.DockProperty] = Dock.Top,
            Child = new DockPanel { LastChildFill = true, Children = { findClose, findNext, findPrev, _findLabel, _findBox } },
        };
        _diffPlaceholder = new TextBlock
        {
            Text = "Select a file to see its diff",
            Foreground = Palette.MutedBrush, FontSize = 14,
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
            IsHitTestVisible = false,
        };
        var diffArea = new Grid { Children = { _diffScroll, _diffPlaceholder } };
        var diffPane = new DockPanel { LastChildFill = true, Children = { _findBar, diffArea } };

        // ---- three resizable panes: graph | files | diff, split by GridSplitters ----
        var body = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions
            {
                new(new GridLength(300, GridUnitType.Pixel)) { MinWidth = 200 },
                new(GridLength.Auto),
                new(new GridLength(280, GridUnitType.Pixel)) { MinWidth = 180 },
                new(GridLength.Auto),
                new(new GridLength(1, GridUnitType.Star)) { MinWidth = 240 },
            },
        };
        body.Children.Add(WithColumn(graphPane, 0));
        body.Children.Add(WithColumn(Splitter(), 1));
        body.Children.Add(WithColumn(filesPane, 2));
        body.Children.Add(WithColumn(Splitter(), 3));
        body.Children.Add(WithColumn(diffPane, 4));

        Content = new DockPanel { Children = { _headerBorder, body } };

        ApplyPalette(); // paint every element from the local palette (light or dark)
    }

    // A vertical drag handle between two panes; recorded so the light/dark toggle can recolour it.
    private GridSplitter Splitter()
    {
        var s = new GridSplitter
        {
            Width = 6,
            Background = _pal.Separator,
            ResizeDirection = GridResizeDirection.Columns,
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };
        _splitters.Add(s);
        return s;
    }

    // A pane's section-header label (its colour is applied by ApplyPalette).
    private static TextBlock PaneLabel(string text) => new()
    {
        Text = text, Foreground = Palette.MutedBrush, FontSize = 11, FontWeight = FontWeight.SemiBold,
        Margin = new Thickness(12, 10, 12, 6), [DockPanel.DockProperty] = Dock.Top,
    };

    private static Control WithColumn(Control c, int col)
    {
        c.SetValue(Grid.ColumnProperty, col);
        return c;
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Key == Key.F && e.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            OpenFind();
            e.Handled = true;
        }
        else if (e.Key == Key.C && e.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            if (_diff.TryCopySelection()) e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            if (_findBar.IsVisible) CloseFind();
            else Close();
        }
    }

    // ---- find bar ----

    private void OpenFind()
    {
        _findBar.IsVisible = true;
        _findBox.Focus();
        _findBox.SelectAll();
        if (!string.IsNullOrEmpty(_findBox.Text)) _diff.SetSearch(_findBox.Text!);
    }

    private void CloseFind()
    {
        _findBar.IsVisible = false;
        _diff.ClearSearch();
        _nodesList.Focus();
    }

    private void OnFindBoxKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            if (e.KeyModifiers.HasFlag(KeyModifiers.Shift)) _diff.PrevMatch(); else _diff.NextMatch();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            CloseFind();
            e.Handled = true;
        }
    }

    private void OnSearchResults(int current, int total) =>
        _findLabel.Text = string.IsNullOrEmpty(_findBox.Text) ? "" : total == 0 ? "No results" : $"{current}/{total}";

    /// <summary>Point the window at a session's repo and (re)load it. Safe to call on the already-open
    /// window (the reused-window refresh path); it bumps the generation so any in-flight load is ignored.
    /// Re-targeting a different repo resets the base-ref override to auto.</summary>
    public void Retarget(string cwd, string title, PullRequestInfo? pr, bool isActive)
    {
        if (!string.Equals(cwd, _cwd, StringComparison.Ordinal))
            _baseOverride = null; // a new repo: re-decide the base automatically
        _cwd = cwd;
        _isActive = isActive;
        _titleText.Text = title;
        if (pr is { } p)
        {
            _prUrl = p.Url;
            _prChip.Content = $"PR #{p.Number}";
            _prChip.IsVisible = true;
        }
        else
        {
            _prUrl = null;
            _prChip.IsVisible = false;
        }
        SetupWatcher(cwd);
        RefreshStatus();
    }

    // Resolves the base ref to scope against from the discovered candidates and the user's override. null
    // means "unscoped" (show recent history): the explicit "recent" choice, being on a trunk branch, or no
    // candidate existing.
    private string? ResolveBase(string? branch, IReadOnlyList<string> candidates)
    {
        if (_baseOverride is not null)
            return _baseOverride.Length == 0 ? null : _baseOverride;
        if (branch is null || TrunkNames.Contains(branch))
            return null;
        return candidates.Count > 0 ? candidates[0] : null;
    }

    // Loads status + candidates + (scoped) log + invisible set off the UI thread, then rebuilds the node
    // list. When preserveSelection, the selected node and file are reselected after reload.
    private void RefreshStatus(bool preserveSelection = false)
    {
        if (_cwd is not { } cwd) return;
        int gen = ++_gen;

        string? keepNode = preserveSelection && _nodesList.SelectedItem is TreeNode n ? n.Key : null;
        string? keepFile = preserveSelection ? SelectedFilePath() : null;

        if (!preserveSelection)
        {
            _subText.Text = "Loading…";
            _diff.SetDiff(null, "");
        }

        System.Threading.Tasks.Task.Run(() =>
            {
                var status = _git.GetStatus(cwd);
                var branch = status?.Branch;
                var candidates = _git.GetBaseRefCandidates(cwd, branch);
                var baseRef = ResolveBase(branch, candidates);
                IReadOnlyList<GitCommit> commits = baseRef is null
                    ? _git.GetLog(cwd, RecentCount)
                    : _git.GetBranchLog(cwd, baseRef, ScopedCount) ?? _git.GetLog(cwd, RecentCount);
                return (status, branch, candidates, baseRef, commits, invisible: ComputeInvisible(cwd, status));
            })
            .ContinueWith(t =>
            {
                if (!t.IsCompletedSuccessfully) return;
                Dispatcher.UIThread.Post(() =>
                {
                    if (!IsVisible || gen != _gen) return;
                    var (status, branch, candidates, baseRef, commits, invisible) = t.Result;
                    _branch = branch;
                    _baseCandidates = candidates;
                    _effectiveBase = baseRef;
                    _lastStatus = status;
                    _lastCommits = commits;
                    _invisiblePaths = invisible;

                    ApplyStatus(status, cwd);
                    RebuildBaseMenu();

                    _nodes = BuildNodes(status, commits, baseRef);
                    _suppressSelect = true;
                    _nodesList.ItemsSource = _nodes;
                    _nodesList.SelectedIndex = -1;
                    _suppressSelect = false;

                    int idx = keepNode is not null ? IndexOfNodeKey(_nodes, keepNode) : -1;
                    if (idx < 0) idx = FirstSelectable(_nodes); // default: the tip (never the base node)
                    _pendingFilePath = keepFile;                // OnNodeSelected reselects this file after load
                    _nodesList.SelectedIndex = idx;

                    UpdateEmptyStates();
                });
            });
    }

    private static IReadOnlyList<TreeNode> BuildNodes(
        GitRepoStatus? status, IReadOnlyList<GitCommit> commits, string? baseRef)
    {
        var nodes = new List<TreeNode>();
        if (status is { IsClean: false } s)
            nodes.Add(new TreeNode(NodeKind.Wip, false, false, false, default, s.Changes.Count, null));
        for (int i = 0; i < commits.Count; i++)
            nodes.Add(new TreeNode(NodeKind.Commit, i == 0, false, false, commits[i], 0, null));
        // The terminal base node: where this branch left its base. Non-selectable, no lane below it.
        if (baseRef is { } b)
            nodes.Add(new TreeNode(NodeKind.Base, false, false, false, default, 0, b));

        // Trim the rail at the ends: no line above the first knot, none below the last.
        if (nodes.Count > 0)
        {
            nodes[0] = nodes[0] with { IsFirst = true };
            nodes[^1] = nodes[^1] with { IsLast = true };
        }
        return nodes;
    }

    // The index of the first row a user can actually land on (skips the non-selectable base node).
    private static int FirstSelectable(IReadOnlyList<TreeNode> nodes)
    {
        for (int i = 0; i < nodes.Count; i++)
            if (!nodes[i].IsBase) return i;
        return -1;
    }

    // ---- node selection -> files pane ----

    private string? _pendingFilePath; // file to reselect once a node's files have loaded (refresh path)

    private void OnNodeSelected(object? sender, SelectionChangedEventArgs e)
    {
        if (_suppressSelect || _nodesList.SelectedItem is not TreeNode node || _cwd is not { } cwd) return;
        if (node.IsBase) return; // the base marker isn't a destination

        string? wantFile = _pendingFilePath;
        _pendingFilePath = null;

        int gen = ++_gen;
        _selectedCommitDiff = null;
        _filesList.ItemsSource = null;
        _diff.SetLoading();
        _diffPlaceholder.IsVisible = false;
        _shownDiffSig = "";

        if (node.IsWip)
        {
            var changes = _lastStatus?.Changes ?? [];
            _filesList.ItemTemplate = _wipFileTemplate;
            _composer.IsVisible = true;
            UpdateComposer();
            PopulateFiles(changes, wantFile is null ? null : IndexOfPath(changes, wantFile));
        }
        else
        {
            _composer.IsVisible = false;
            string hash = node.Commit.Hash;
            System.Threading.Tasks.Task.Run(() => _git.GetCommitDiff(cwd, hash)).ContinueWith(t =>
            {
                if (!t.IsCompletedSuccessfully) return;
                Dispatcher.UIThread.Post(() =>
                {
                    if (!IsVisible || gen != _gen) return;
                    _selectedCommitDiff = t.Result;
                    var files = t.Result?.Files ?? [];
                    _filesList.ItemTemplate = _commitFileTemplate;
                    int sel = wantFile is null ? (files.Count > 0 ? 0 : -1) : IndexOfDiffFile(files, wantFile);
                    PopulateFiles(files, sel);
                });
            });
        }
    }

    // Sets the files list and selects a row (default first), which fires OnFileSelected to load the diff.
    private void PopulateFiles<T>(IReadOnlyList<T> items, int? select)
    {
        _suppressSelect = true;
        _filesList.ItemsSource = items;
        _filesList.SelectedIndex = -1;
        _suppressSelect = false;

        int idx = select ?? (items.Count > 0 ? 0 : -1);
        if (idx < 0 && items.Count > 0) idx = 0;
        _filesList.SelectedIndex = idx;

        UpdateEmptyStates();
        if (_filesList.SelectedIndex < 0)
        {
            _diff.SetDiff(null, "");
            _diffPlaceholder.IsVisible = true;
        }
    }

    // ---- file selection -> diff ----

    private void OnFileSelected(object? sender, SelectionChangedEventArgs e)
    {
        if (_suppressSelect || _cwd is not { } cwd) return;
        object? item = _filesList.SelectedItem;
        if (item is null) return;
        _diffPlaceholder.IsVisible = false;

        if (item is GitFileChange fc)
        {
            int gen = ++_gen;
            _diff.SetLoading();
            _shownDiffSig = "";
            System.Threading.Tasks.Task.Run(() => LoadFileDiff(cwd, fc)).ContinueWith(t =>
            {
                if (!t.IsCompletedSuccessfully) return;
                Dispatcher.UIThread.Post(() =>
                {
                    if (!IsVisible || gen != _gen) return;
                    ShowSections(t.Result.sections, t.Result.note);
                });
            });
        }
        else if (item is GitDiffFile gf)
        {
            // The commit's whole diff is already in hand — no git call, just isolate this file.
            var one = new GitDiff([gf]);
            IReadOnlyList<DiffSection> sections = gf.Hunks.Count > 0 || gf.IsBinary
                ? [new DiffSection(null, one)]
                : [];
            string? note = sections.Count > 0 ? null : $"No textual diff for {gf.NewPath ?? gf.OldPath}.";
            ShowSections(sections, note);
        }
    }

    private void ShowSections(IReadOnlyList<DiffSection> sections, string? note)
    {
        _diff.SetSections(sections, note);
        _shownDiffSig = DiffSig(sections);
        _diffPlaceholder.IsVisible = false;
    }

    // Picks the diff(s) for a working-tree change: untracked files show their full contents; a file with
    // BOTH staged and unstaged edits shows two labelled sections; otherwise the single relevant diff. Mirrors
    // GitReviewWindow's proven logic (the WIP node is the old Review window's job).
    private (IReadOnlyList<DiffSection> sections, string? note) LoadFileDiff(string cwd, GitFileChange fc)
    {
        if (fc.Untracked)
            return Single(_git.GetUntrackedDiff(cwd, fc.Path), fc.Path);

        bool hasStaged = fc.Staged != GitChangeKind.None;
        bool hasUnstaged = fc.Unstaged != GitChangeKind.None;

        // Each diff carries whether its hunks can be staged (an unstaged worktree diff) or unstaged (a staged
        // index diff), so the diff pane shows the right per-hunk button.
        var diffs = new List<(string? Label, GitDiff Diff, HunkStageAction Action)>();
        if (hasStaged && hasUnstaged)
        {
            if (_git.GetWorkingDiff(cwd, fc.Path, staged: true) is { } staged) diffs.Add(("Staged", staged, HunkStageAction.Unstage));
            if (_git.GetWorkingDiff(cwd, fc.Path, staged: false) is { } unstaged) diffs.Add(("Unstaged", unstaged, HunkStageAction.Stage));
        }
        else if (_git.GetWorkingDiff(cwd, fc.Path, staged: hasStaged) is { } d)
        {
            diffs.Add((null, d, hasStaged ? HunkStageAction.Unstage : HunkStageAction.Stage));
        }

        if (IsPlainModified(fc) && diffs.Count > 0 && diffs.All(x => GitRepoService.HasNoTextChange(x.Diff)))
            return ([], "Content unchanged — only a line ending or byte-order mark (BOM) differs.");

        var sections = diffs.Where(x => x.Diff.Files.Count > 0)
                            .Select(x => new DiffSection(x.Label, x.Diff, x.Action)).ToList();
        return sections.Count > 0 ? (sections, null) : ([], $"No diff for {fc.Path}.");

        static (IReadOnlyList<DiffSection>, string?) Single(GitDiff? diff, string path) =>
            diff is { Files.Count: > 0 } d
                ? (new[] { new DiffSection(null, d) }, null)
                : ([], $"No diff for {path}.");
    }

    // ---- commit authoring (Phase 2) ----

    private int StagedCount() => _lastStatus?.Changes.Count(c => c.Staged != GitChangeKind.None) ?? 0;

    // Reflects the staged count in the composer hint and enables the Commit button accordingly.
    private void UpdateComposer()
    {
        int staged = StagedCount();
        _commitBtn.IsEnabled = staged > 0;
        SetHint(staged == 0
            ? "Tick files to stage, then commit."
            : $"{staged} file{(staged == 1 ? "" : "s")} staged.", warn: false);
    }

    private void SetHint(string text, bool warn)
    {
        _composerHint.Text = text;
        _composerHint.Foreground = warn ? _pal.Orange : _pal.Muted;
    }

    // Stage or unstage a whole file, then refresh so the checkbox and staged count reflect the new index.
    private void ToggleStage(string path, bool stage)
    {
        if (_cwd is not { } cwd) return;
        System.Threading.Tasks.Task.Run(() => stage ? _git.StageFile(cwd, path) : _git.UnstageFile(cwd, path))
            .ContinueWith(t =>
            {
                if (!t.IsCompletedSuccessfully) return;
                Dispatcher.UIThread.Post(() =>
                {
                    if (!IsVisible) return;
                    if (!t.Result.Ok) SetHint(FirstLine(t.Result.Error), warn: true);
                    RefreshStatus(preserveSelection: true);
                });
            });
    }

    // Stage or unstage a single hunk (from its stage/unstage button in the diff pane), then refresh so the
    // file list, staged count, and the diff's staged/unstaged split reflect the new index.
    private void OnHunkStage(HunkStageRequest req)
    {
        if (_cwd is not { } cwd || req.Path is not { } path) return;
        System.Threading.Tasks.Task.Run(() => req.Action == HunkStageAction.Stage
                ? _git.StageHunk(cwd, path, req.HunkHeader)
                : _git.UnstageHunk(cwd, path, req.HunkHeader))
            .ContinueWith(t =>
            {
                if (!t.IsCompletedSuccessfully) return;
                Dispatcher.UIThread.Post(() =>
                {
                    if (!IsVisible) return;
                    if (!t.Result.Ok) { SetHint(FirstLine(t.Result.Error), warn: true); return; }
                    RefreshStatus(preserveSelection: true);
                });
            });
    }

    // Enables/labels the "Stage lines" button as the diff's line selection changes.
    private void OnLineStageState(LineStageState s)
    {
        _stageLinesBtn.IsEnabled = s.Available;
        _stageLinesBtn.Content = s.Available
            ? $"{(s.Action == HunkStageAction.Stage ? "Stage" : "Unstage")} {s.Count} line{(s.Count == 1 ? "" : "s")}"
            : "Stage lines";
    }

    // Stage or unstage the selected lines of a hunk, then refresh.
    private void OnLineStageRequest(LineStageRequest req)
    {
        if (_cwd is not { } cwd || req.Path is not { } path) return;
        System.Threading.Tasks.Task.Run(() => req.Action == HunkStageAction.Stage
                ? _git.StageLines(cwd, path, req.HunkHeader, req.Indices)
                : _git.UnstageLines(cwd, path, req.HunkHeader, req.Indices))
            .ContinueWith(t =>
            {
                if (!t.IsCompletedSuccessfully) return;
                Dispatcher.UIThread.Post(() =>
                {
                    if (!IsVisible) return;
                    if (!t.Result.Ok) { SetHint(FirstLine(t.Result.Error), warn: true); return; }
                    RefreshStatus(preserveSelection: true);
                });
            });
    }

    private async void OnCommitClick(object? sender, RoutedEventArgs e)
    {
        if (_cwd is not { } cwd) return;
        string msg = _msgBox.Text?.Trim() ?? "";
        if (msg.Length == 0) { SetHint("Write a commit message.", warn: true); _msgBox.Focus(); return; }
        if (StagedCount() == 0) { SetHint("Tick files to stage first.", warn: true); return; }

        // Guard: committing while the session is live can race Claude's own edits/git in this same tree.
        if (_isActive)
        {
            bool go = await ConfirmDialog.ShowAsync(this, "Session is active",
                "Claude may be editing or running git in this working tree right now, so committing can race its writes. Commit anyway?",
                "Commit anyway", "Wait");
            if (!go) { SetHint("Held off — session still active.", warn: false); return; }
        }

        _commitBtn.IsEnabled = false;
        var (ok, err) = await System.Threading.Tasks.Task.Run(() => _git.Commit(cwd, msg));
        if (!IsVisible) return;
        _commitBtn.IsEnabled = true;
        if (!ok) { SetHint(FirstLine(err), warn: true); return; }

        _msgBox.Text = "";
        SetHint("Committed.", warn: false);
        RefreshStatus(preserveSelection: false); // WIP node may vanish → lands on the new HEAD commit
    }

    // Collapses git's (often multi-line) error text to a single tidy line for the inline hint.
    private static string FirstLine(string s)
    {
        var line = s.Replace("\r", "").Split('\n', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        return string.IsNullOrEmpty(line) ? "git command failed." : line;
    }

    // ---- auto-refresh ----

    private void SetupWatcher(string cwd)
    {
        _watcher?.Dispose();
        _watcher = null;
        if (!Directory.Exists(cwd)) return;
        try
        {
            var w = new FileSystemWatcher(cwd)
            {
                IncludeSubdirectories = true,
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.Size,
            };
            void Bump(object? _, FileSystemEventArgs __) => Dispatcher.UIThread.Post(RestartDebounce);
            w.Changed += Bump;
            w.Created += Bump;
            w.Deleted += Bump;
            w.Renamed += (_, _) => Dispatcher.UIThread.Post(RestartDebounce);
            w.EnableRaisingEvents = true;
            _watcher = w;
        }
        catch { /* best-effort */ }
    }

    private void RestartDebounce()
    {
        _debounce ??= CreateDebounce();
        _debounce.Stop();
        _debounce.Start();
    }

    private DispatcherTimer CreateDebounce()
    {
        var t = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(600) };
        t.Tick += (_, _) =>
        {
            _debounce?.Stop();
            if (IsVisible) AutoRefresh();
        };
        return t;
    }

    protected override void OnClosed(EventArgs e)
    {
        _watcher?.Dispose();
        _watcher = null;
        _debounce?.Stop();
        base.OnClosed(e);
    }

    // A debounced filesystem change: reload status/log, rebuild the node list only if it changed (preserving
    // the selected node + file), and re-render the diff only when its content actually changed — so the view
    // and its scroll position stay put when nothing relevant moved. Commit diffs are immutable, so only a
    // selected WIP file's diff is ever re-fetched.
    private void AutoRefresh()
    {
        if (_cwd is not { } cwd) return;
        int gen = ++_gen;

        bool wipSelected = _nodesList.SelectedItem is TreeNode { IsWip: true };
        string? selPath = wipSelected ? SelectedFilePath() : null;

        System.Threading.Tasks.Task.Run(() =>
        {
            var status = _git.GetStatus(cwd);
            var branch = status?.Branch;
            var candidates = _git.GetBaseRefCandidates(cwd, branch);
            var baseRef = ResolveBase(branch, candidates);
            IReadOnlyList<GitCommit> commits = baseRef is null
                ? _git.GetLog(cwd, RecentCount)
                : _git.GetBranchLog(cwd, baseRef, ScopedCount) ?? _git.GetLog(cwd, RecentCount);
            var invisible = ComputeInvisible(cwd, status);
            (IReadOnlyList<DiffSection> sections, string? note)? sel = null;
            if (selPath is not null)
                sel = FindByPath(status, selPath) is { } fc
                    ? LoadFileDiff(cwd, fc)
                    : ((IReadOnlyList<DiffSection>)[], $"No diff for {selPath}.");
            return (status, branch, candidates, baseRef, commits, invisible, sel);
        }).ContinueWith(t =>
        {
            if (!t.IsCompletedSuccessfully) return;
            Dispatcher.UIThread.Post(() =>
            {
                if (!IsVisible || gen != _gen) return;
                var (status, branch, candidates, baseRef, commits, invisible, sel) = t.Result;

                bool changed = !StatusEqual(status, _lastStatus)
                               || !CommitsEqual(commits, _lastCommits)
                               || !invisible.SetEquals(_invisiblePaths)
                               || baseRef != _effectiveBase;

                _branch = branch;
                _baseCandidates = candidates;
                _effectiveBase = baseRef;
                _lastStatus = status;
                _lastCommits = commits;
                _invisiblePaths = invisible;
                ApplyStatus(status, cwd);
                RebuildBaseMenu();

                if (changed)
                {
                    string? keepNode = _nodesList.SelectedItem is TreeNode n ? n.Key : null;
                    string? keepFile = SelectedFilePath();
                    _nodes = BuildNodes(status, commits, baseRef);
                    _suppressSelect = true;
                    _nodesList.ItemsSource = _nodes;
                    _nodesList.SelectedIndex = -1;
                    _suppressSelect = false;
                    int idx = keepNode is not null ? IndexOfNodeKey(_nodes, keepNode) : -1;
                    if (idx < 0) idx = FirstSelectable(_nodes);
                    // If the same WIP node stays selected, let the diff-signature guard below decide whether to
                    // repaint; reselecting would rebuild the files list. Only drive a reselect when the target
                    // node actually changed identity.
                    if (_nodesList.SelectedIndex != idx || keepNode is null)
                    {
                        _pendingFilePath = keepFile;
                        _nodesList.SelectedIndex = idx;
                    }
                    UpdateEmptyStates();
                }

                // Only touch a selected WIP file's diff when its content actually changed.
                if (sel is { } s && wipSelected && DiffSig(s.sections) != _shownDiffSig)
                    ShowSections(s.sections, s.note);
            });
        });
    }

    // ---- header / base menu ----

    private void ApplyStatus(GitRepoStatus? status, string cwd)
    {
        if (status is not { } s)
        {
            _subText.Text = cwd;
            _baseBtn.Content = "since ⑂ …";
            return;
        }
        string branch = s.Branch ?? "(detached)";
        string ab = (s.Ahead, s.Behind) switch { (0, 0) => "", var (a, b) => $"  ·  ↑{a} ↓{b}" };
        string dirty = s.IsClean ? "clean" : $"{s.Changes.Count} change{(s.Changes.Count == 1 ? "" : "s")}";
        string scope = _effectiveBase is { } b2 ? $"since {b2}" : "recent";
        _subText.Text = $"{branch}{ab}  ·  {scope}  ·  {dirty}  ·  {cwd}";
        _baseBtn.Content = _effectiveBase is { } b3 ? $"since ⑂ {b3}" : "recent (all)";
    }

    // Rebuilds the base-ref flyout: "Recent (all commits)" + each discovered candidate, ticking the active one.
    private void RebuildBaseMenu()
    {
        var items = new List<Control> { BaseItem("Recent (all commits)", null) };
        if (_baseCandidates.Count > 0)
        {
            items.Add(new Separator());
            foreach (var c in _baseCandidates)
                items.Add(BaseItem($"since {c}", c));
        }
        _baseFlyout.ItemsSource = items;

        MenuItem BaseItem(string header, string? refName)
        {
            bool active = string.Equals(refName, _effectiveBase, StringComparison.Ordinal);
            var mi = new MenuItem { Header = (active ? "✓  " : "     ") + header };
            mi.Click += (_, _) =>
            {
                _baseOverride = refName ?? ""; // "" = explicitly unscoped
                RefreshStatus(preserveSelection: true);
            };
            return mi;
        }
    }

    // ---- change classification ("≈") — as GitReviewWindow ----

    private HashSet<string> ComputeInvisible(string cwd, GitRepoStatus? status)
    {
        var invisible = new HashSet<string>(StringComparer.Ordinal);
        if (status is not { } s || s.IsClean) return invisible;

        var candidates = new HashSet<string>(StringComparer.Ordinal);
        foreach (var c in s.Changes)
            if (!c.Untracked && IsPlainModified(c)) candidates.Add(c.Path);
        if (candidates.Count == 0) return invisible;

        var visible = new HashSet<string>(StringComparer.Ordinal);
        void ScanForRealChanges(GitDiff? d)
        {
            if (d is not { } diff) return;
            foreach (var f in diff.Files)
                if (!GitRepoService.FileHasNoTextChange(f))
                {
                    if (f.NewPath is { } n) visible.Add(n);
                    if (f.OldPath is { } o) visible.Add(o);
                }
        }
        ScanForRealChanges(_git.GetWorkingTreeDiff(cwd, staged: false));
        ScanForRealChanges(_git.GetWorkingTreeDiff(cwd, staged: true));

        foreach (var p in candidates)
            if (!visible.Contains(p)) invisible.Add(p);
        return invisible;
    }

    private static bool IsPlainModified(GitFileChange c) =>
        !c.Untracked
        && c.Staged is GitChangeKind.None or GitChangeKind.Modified
        && c.Unstaged is GitChangeKind.None or GitChangeKind.Modified
        && (c.Staged == GitChangeKind.Modified || c.Unstaged == GitChangeKind.Modified);

    // ---- change detection ----

    private static bool StatusEqual(GitRepoStatus? a, GitRepoStatus? b)
    {
        if (a is null) return b is null;
        if (b is null) return false;
        var x = a.Value; var y = b.Value;
        return x.Branch == y.Branch && x.Upstream == y.Upstream && x.Ahead == y.Ahead && x.Behind == y.Behind
            && x.Changes.SequenceEqual(y.Changes);
    }

    private static bool CommitsEqual(IReadOnlyList<GitCommit> a, IReadOnlyList<GitCommit> b) => a.SequenceEqual(b);

    private static GitFileChange? FindByPath(GitRepoStatus? status, string path)
    {
        if (status is { } s)
            foreach (var c in s.Changes)
                if (c.Path == path) return c;
        return null;
    }

    private string? SelectedFilePath() => _filesList.SelectedItem switch
    {
        GitFileChange fc => fc.Path,
        GitDiffFile gf => gf.NewPath ?? gf.OldPath,
        _ => null,
    };

    private static int IndexOfNodeKey(IReadOnlyList<TreeNode> nodes, string key)
    {
        for (int i = 0; i < nodes.Count; i++)
            if (nodes[i].Key == key) return i;
        return -1;
    }

    private static int IndexOfPath(IReadOnlyList<GitFileChange> list, string? path)
    {
        if (path is not null)
            for (int i = 0; i < list.Count; i++)
                if (list[i].Path == path) return i;
        return -1;
    }

    private static int IndexOfDiffFile(IReadOnlyList<GitDiffFile> list, string? path)
    {
        if (path is not null)
            for (int i = 0; i < list.Count; i++)
                if ((list[i].NewPath ?? list[i].OldPath) == path) return i;
        return -1;
    }

    private static string DiffSig(IReadOnlyList<DiffSection> sections)
    {
        var sb = new StringBuilder();
        foreach (var s in sections)
        {
            sb.Append(s.Label).Append('␞');
            foreach (var f in s.Diff.Files)
            {
                sb.Append(f.OldPath).Append('>').Append(f.NewPath).Append(f.IsBinary ? '#' : '.').Append('␞');
                foreach (var h in f.Hunks)
                {
                    sb.Append(h.Header).Append('␞');
                    foreach (var l in h.Lines)
                        sb.Append((char)('0' + (int)l.Kind)).Append(l.Text).Append('\n');
                }
            }
        }
        return sb.ToString();
    }

    // ---- diff mode / wrap ----

    private Button ModeButton(string label, bool split)
    {
        var b = new Button { Content = label, VerticalAlignment = VerticalAlignment.Center, Padding = new Thickness(10, 4) };
        b.Click += (_, _) => SetMode(split);
        return b;
    }

    private void SetMode(bool split)
    {
        if (_split == split) return;
        _split = split;
        _diff.SetSplit(split);
        _settings.GitReviewSplitView = split;
        _settings.Save();
        UpdateModeButtons();
    }

    private void UpdateModeButtons()
    {
        Style(_unifiedBtn, !_split);
        Style(_splitBtn, _split);

        void Style(Button b, bool active)
        {
            b.Background = active ? _pal.Accent : _pal.ButtonBg;
            b.Foreground = active ? _pal.OnAccent : _pal.Fg;
        }
    }

    // ---- per-window light / dark ----

    // Repaints every explicitly-coloured element in this window (and its diff) from the local palette, and
    // flips the Fluent theme variant so the templated chrome (buttons, checkbox, text box, scrollbars, list
    // selection) matches. Called at construction and on every toggle.
    private void ApplyPalette()
    {
        RequestedThemeVariant = _light ? ThemeVariant.Light : ThemeVariant.Dark;
        Background = _pal.WindowBg;
        _headerBorder.Background = _pal.HeaderBg;
        _findBar.Background = _pal.HeaderBg;
        _composer.Background = _pal.HeaderBg;

        _titleText.Foreground = _pal.Title;
        _subText.Foreground = _pal.Muted;
        _graphLabel.Foreground = _pal.Muted;
        _filesLabel.Foreground = _pal.Muted;
        _composerLabel.Foreground = _pal.Muted;
        _findLabel.Foreground = _pal.Muted;
        _diffPlaceholder.Foreground = _pal.Muted;
        _nodesEmpty.Foreground = _pal.Muted;
        _filesEmpty.Foreground = _pal.Muted;
        _wrapCheck.Foreground = _pal.Fg;
        _expandBtn.Foreground = _pal.Muted;

        _prChip.Background = _pal.Accent;
        _prChip.Foreground = _pal.OnAccent;
        _commitBtn.Background = _pal.Accent;
        _commitBtn.Foreground = _pal.OnAccent;

        foreach (var s in _splitters) s.Background = _pal.Separator;

        _themeBtn.Content = _light ? "Dark" : "Light";
        UpdateModeButtons();
        _diff.SetLight(_light);
    }

    private void ToggleLight()
    {
        _light = !_light;
        _pal = _light ? TreePalette.Light() : TreePalette.Dark();
        _settings.GitTreeLight = _light;
        _settings.Save();
        ApplyPalette();
        UpdateComposer();                       // re-tint the composer hint
        RefreshStatus(preserveSelection: true); // rebuild node/file rows through the new palette
    }

    private void SetWrap(bool wrap)
    {
        if (_wrap == wrap) return;
        _wrap = wrap;
        _diff.SetWrap(wrap);
        _diffScroll.HorizontalScrollBarVisibility = wrap ? ScrollBarVisibility.Disabled : ScrollBarVisibility.Auto;
        _settings.GitReviewWrap = wrap;
        _settings.Save();
    }

    // ---- small UI helpers ----

    private static ListBox MakeList(IDataTemplate template) => new()
    {
        Background = Brushes.Transparent,
        ItemTemplate = template,
        BorderThickness = new Thickness(0),
    };

    private static TextBlock EmptyHint(string text) => new()
    {
        Text = text, Foreground = Palette.MutedBrush, FontSize = 12, FontStyle = FontStyle.Italic,
        Margin = new Thickness(12, 6, 12, 0), IsVisible = false, IsHitTestVisible = false,
        VerticalAlignment = VerticalAlignment.Top, HorizontalAlignment = HorizontalAlignment.Left,
    };

    private void UpdateEmptyStates()
    {
        _nodesEmpty.IsVisible = _nodesList.ItemCount == 0;
        _filesEmpty.IsVisible = _filesList.ItemCount == 0;
        _filesEmpty.Text = _nodesList.SelectedItem is null ? "Select a node" : "No files";
    }

    // ---- templates ----

    // A graph node row: a lane rail (vertical line + a knot) beside the commit / working-tree summary.
    private FuncDataTemplate<TreeNode> NodeTemplate() =>
        new((node, _) => node is null ? new Control() : NodeRow(node, _pal), supportsRecycling: false);

    /// <summary>Builds one graph-node row (the lane rail + knot beside the commit / working-tree / base
    /// summary), painted from <paramref name="pal"/>. The rail is split into an upper and a lower segment so
    /// the line can be trimmed at the ends — no line above the first knot, none below the last. Static and
    /// self-contained so the render harness can eyeball it with sample data.</summary>
    internal static Control NodeRow(TreeNode node, TreePalette pal)
    {
        // Rail: [upper segment | knot | lower segment]. Upper is hidden on the first row, lower on the last.
        var rail = new Grid { Width = 26, RowDefinitions = new RowDefinitions("*,Auto,*") };
        var upper = LaneSegment(pal);
        upper.SetValue(Grid.RowProperty, 0);
        upper.IsVisible = !node.IsFirst;
        var lower = LaneSegment(pal);
        lower.SetValue(Grid.RowProperty, 2);
        lower.IsVisible = !node.IsLast;
        Shape knot = Knot(node, pal);
        knot.SetValue(Grid.RowProperty, 1);
        rail.Children.Add(upper);
        rail.Children.Add(lower);
        rail.Children.Add(knot);

        var content = new StackPanel { Orientation = Orientation.Vertical, Margin = new Thickness(6, 10, 10, 10) };
        if (node.IsWip)
        {
            content.Children.Add(new TextBlock
            {
                Text = "Working tree", Foreground = pal.Brand,
                FontSize = 13, FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap,
            });
            content.Children.Add(new TextBlock
            {
                Text = $"{node.ChangeCount} uncommitted change{(node.ChangeCount == 1 ? "" : "s")}",
                Foreground = pal.Muted, FontFamily = Mono, FontSize = 11, Margin = new Thickness(0, 3, 0, 0),
            });
        }
        else if (node.IsBase)
        {
            // The base marker: where this branch left its base ref. Not a destination.
            content.Children.Add(new TextBlock
            {
                Text = node.Label ?? "base", Foreground = pal.Fg, FontFamily = Mono, FontSize = 12,
                FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap,
            });
            content.Children.Add(new TextBlock
            {
                Text = "branch base", Foreground = pal.Muted, FontSize = 11, Margin = new Thickness(0, 3, 0, 0),
            });
        }
        else
        {
            var c = node.Commit;
            // Subject wraps; the HEAD tag docks top-right so a long subject flows beneath it.
            var subjRow = new DockPanel { LastChildFill = true };
            if (node.IsHead)
            {
                var tag = new Border
                {
                    Background = pal.Accent, CornerRadius = new CornerRadius(3),
                    Padding = new Thickness(5, 0, 5, 1), Margin = new Thickness(6, 1, 0, 0),
                    VerticalAlignment = VerticalAlignment.Top, [DockPanel.DockProperty] = Dock.Right,
                    Child = new TextBlock { Text = "HEAD", Foreground = pal.OnAccent, FontSize = 9, FontFamily = Mono },
                };
                subjRow.Children.Add(tag);
            }
            subjRow.Children.Add(new TextBlock
            {
                Text = c.Subject, Foreground = pal.Title, FontSize = 13, TextWrapping = TextWrapping.Wrap,
            });
            content.Children.Add(subjRow);
            content.Children.Add(new TextBlock
            {
                Text = $"{c.ShortHash} · {c.Author} · {RelTime(c.Date)}",
                Foreground = pal.Muted, FontFamily = Mono, FontSize = 11, Margin = new Thickness(0, 3, 0, 0),
                TextTrimming = TextTrimming.CharacterEllipsis,
            });
            ToolTip.SetTip(content, c.Body);
            ToolTip.SetShowDelay(content, 750);
        }

        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*") };
        grid.Children.Add(rail);
        content.SetValue(Grid.ColumnProperty, 1);
        grid.Children.Add(content);
        return grid;
    }

    // One half of the lane line, painted from the palette's accent at half strength.
    private static Rectangle LaneSegment(TreePalette pal) => new()
    {
        Width = 2, Fill = pal.Accent, Opacity = 0.5,
        HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Stretch,
    };

    // The knot on the rail: a dashed brand ring for WIP, a hollow muted ring for the base marker, else a
    // filled accent dot (with a ring on HEAD).
    private static Shape Knot(TreeNode node, TreePalette pal) => node.Kind switch
    {
        NodeKind.Wip => new Ellipse
        {
            Width = 13, Height = 13, StrokeThickness = 2,
            Stroke = pal.Brand, Fill = pal.HollowFill,
            StrokeDashArray = new AvaloniaList<double> { 2, 1.4 },
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
        },
        NodeKind.Base => new Ellipse
        {
            Width = 11, Height = 11, StrokeThickness = 2,
            Stroke = pal.Muted, Fill = pal.HollowFill,
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
        },
        _ => new Ellipse
        {
            Width = 12, Height = 12,
            Fill = pal.Accent,
            Stroke = node.IsHead ? pal.AccentHover : null,
            StrokeThickness = node.IsHead ? 3 : 0,
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
        },
    };

    /// <summary>The window-local colour set the Tree window and its diff paint from, so light/dark applies to
    /// just this window. <see cref="Dark"/> mirrors the app's active theme (via <see cref="Palette"/>); light
    /// uses fixed, print-legible values with darkened status hues.</summary>
    internal sealed class TreePalette
    {
        public required IBrush WindowBg, HeaderBg, Title, Fg, Muted, Accent, AccentHover, OnAccent, Brand,
            ButtonBg, Separator, HollowFill, Green, Red, Orange;

        public static TreePalette Dark() => new()
        {
            WindowBg = new SolidColorBrush(Palette.Sunken),
            HeaderBg = Palette.FormBgBrush,
            Title = Palette.TitleBrush,
            Fg = Palette.FgBrush,
            Muted = Palette.MutedBrush,
            Accent = Palette.AccentBrush,
            AccentHover = new SolidColorBrush(Palette.AccentHover),
            OnAccent = Palette.OnAccentBrush,
            Brand = Palette.BrandBrush,
            ButtonBg = Palette.ButtonBgBrush,
            Separator = Palette.SeparatorBrush,
            HollowFill = new SolidColorBrush(Palette.Sunken),
            Green = new SolidColorBrush(Palette.Green),
            Red = new SolidColorBrush(Palette.Red),
            Orange = new SolidColorBrush(Palette.Orange),
        };

        public static TreePalette Light() => new()
        {
            WindowBg = B(0xEC, 0xEE, 0xF3),
            HeaderBg = B(0xFF, 0xFF, 0xFF),
            Title = B(0x0D, 0x0E, 0x16),
            Fg = B(0x1A, 0x1B, 0x26),
            Muted = B(0x5C, 0x60, 0x72),
            Accent = B(0x2F, 0x68, 0xE0),
            AccentHover = B(0x1F, 0x58, 0xD0),
            OnAccent = B(0xFF, 0xFF, 0xFF),
            Brand = B(0xD2, 0x43, 0x27),
            ButtonBg = B(0xF1, 0xF2, 0xF5),
            Separator = B(0xDD, 0xE0, 0xE8),
            HollowFill = B(0xFF, 0xFF, 0xFF),
            Green = B(0x1F, 0x88, 0x3D),
            Red = B(0xCF, 0x22, 0x2E),
            Orange = B(0xB3, 0x6B, 0x00),
        };

        private static IBrush B(byte r, byte g, byte b) => new SolidColorBrush(Color.FromRgb(r, g, b));
    }

    // A WIP file row: a stage checkbox (ticked when the file has staged content) beside the coloured
    // status + path. Ticking stages the whole file (git add); unticking unstages it (git restore --staged).
    private FuncDataTemplate<GitFileChange> WipFileTemplate() => new((fc, _) =>
    {
        if (fc.Path is null) return new Control();

        var check = new CheckBox
        {
            IsChecked = fc.Staged != GitChangeKind.None,
            VerticalAlignment = VerticalAlignment.Center, MinWidth = 0, Margin = new Thickness(0, 0, 4, 0),
        };
        ToolTip.SetTip(check, "Stage / unstage this file");
        string path = fc.Path;
        check.Click += (_, _) => ToggleStage(path, check.IsChecked == true);

        var text = new TextBlock
        {
            Text = WipFileLabel(fc), FontFamily = Mono, FontSize = 12,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = WipFileColor(fc),
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        return new StackPanel
        {
            Orientation = Orientation.Horizontal, Margin = new Thickness(10, 5, 8, 5),
            Children = { check, text },
        };
    }, supportsRecycling: false);

    private FuncDataTemplate<GitDiffFile> CommitFileTemplate() => new((gf, _) =>
    {
        string? path = gf.NewPath ?? gf.OldPath;
        return new TextBlock
        {
            Text = path is null ? "" : $"{CommitFileCode(gf)}  {(gf.OldPath is { } o && gf.NewPath is { } n && o != n ? $"{o} → {n}" : path)}",
            FontFamily = Mono, FontSize = 12, Margin = new Thickness(12, 6, 12, 6),
            Foreground = path is null ? _pal.Muted : CommitFileColor(gf),
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
    }, supportsRecycling: true);

    private string WipFileLabel(GitFileChange fc)
    {
        char code = fc.Untracked ? '?'
                  : _invisiblePaths.Contains(fc.Path) ? '≈'
                  : CodeOf(fc.Unstaged != GitChangeKind.None ? fc.Unstaged : fc.Staged);
        return fc.OrigPath is { } o ? $"{code}  {o} → {fc.Path}" : $"{code}  {fc.Path}";
    }

    private IBrush WipFileColor(GitFileChange fc)
    {
        if (fc.Untracked) return _pal.Green;
        if (_invisiblePaths.Contains(fc.Path)) return _pal.Muted;
        var k = fc.Unstaged != GitChangeKind.None ? fc.Unstaged : fc.Staged;
        return k switch
        {
            GitChangeKind.Added => _pal.Green,
            GitChangeKind.Deleted => _pal.Red,
            _ => _pal.Orange,
        };
    }

    private static char CommitFileCode(GitDiffFile gf) =>
        gf.OldPath is null ? 'A'
        : gf.NewPath is null ? 'D'
        : gf.OldPath != gf.NewPath ? 'R'
        : 'M';

    private IBrush CommitFileColor(GitDiffFile gf) =>
        gf.OldPath is null ? _pal.Green
        : gf.NewPath is null ? _pal.Red
        : _pal.Orange;

    private static char CodeOf(GitChangeKind k) => k switch
    {
        GitChangeKind.Added => 'A',
        GitChangeKind.Modified => 'M',
        GitChangeKind.Deleted => 'D',
        GitChangeKind.Renamed => 'R',
        GitChangeKind.Copied => 'C',
        GitChangeKind.TypeChanged => 'T',
        GitChangeKind.Unmerged => 'U',
        _ => ' ',
    };

    private static string RelTime(DateTimeOffset d)
    {
        var s = DateTimeOffset.Now - d;
        if (s.TotalDays >= 1) return $"{(int)s.TotalDays}d";
        if (s.TotalHours >= 1) return $"{(int)s.TotalHours}h";
        if (s.TotalMinutes >= 1) return $"{(int)s.TotalMinutes}m";
        return "now";
    }
}
