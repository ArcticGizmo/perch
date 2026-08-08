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
using Avalonia.Layout;
using Avalonia.Media;
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
    private static Color BodyBg => Palette.Sunken;
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
    private readonly CheckBox _wrapCheck;
    private readonly ListBox _nodesList;
    private readonly ListBox _filesList;
    private readonly DiffView _diff;
    private readonly ScrollViewer _diffScroll;
    private readonly Border _findBar;
    private readonly TextBox _findBox;
    private readonly TextBlock _findLabel;
    private readonly TextBlock _nodesEmpty;
    private readonly TextBlock _filesEmpty;
    private readonly TextBlock _diffPlaceholder;

    private readonly FuncDataTemplate<TreeNode> _nodeTemplate;
    private readonly FuncDataTemplate<GitFileChange> _wipFileTemplate;
    private readonly FuncDataTemplate<GitDiffFile> _commitFileTemplate;

    private string? _cwd;
    private string? _prUrl;
    private string? _branch;
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

    /// <summary>A row in the left graph: either the working-tree (WIP) node or one commit. Internal so the
    /// headless render harness can build sample rows via <see cref="NodeRow"/>.</summary>
    internal sealed record TreeNode(bool IsWip, bool IsHead, GitCommit Commit, int ChangeCount)
    {
        public string Key => IsWip ? "\0wip" : Commit.Hash;
    }

    public GitTreeWindow(AppSettings settings)
    {
        _settings = settings;
        _split = settings.GitReviewSplitView;
        _wrap = settings.GitReviewWrap;
        Title = "Tree";
        Width = 1560;
        Height = 1020;
        MinWidth = 820;
        MinHeight = 460;
        Background = new SolidColorBrush(BodyBg);
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

        // Ghosted Phase-3 affordance: the full multi-branch graph. Present so the destination is visible; a
        // tooltip says why it's disabled.
        var expandBtn = new Button
        {
            Content = "⤢ Expand to full tree", IsEnabled = false, VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(8, 0, 0, 0), Foreground = Palette.MutedBrush,
            [DockPanel.DockProperty] = Dock.Right,
        };
        ToolTip.SetTip(expandBtn, "The all-branches graph is coming in a later pass.");

        var headerText = new StackPanel { Orientation = Orientation.Vertical, Children = { _titleText, _subText } };
        var header = new Border
        {
            Background = Palette.FormBgBrush, Padding = new Thickness(14, 10),
            [DockPanel.DockProperty] = Dock.Top,
            Child = new DockPanel
            {
                LastChildFill = true,
                Children = { _prChip, refreshBtn, modeGroup, _wrapCheck, expandBtn, _baseBtn, headerText },
            },
        };

        // ---- pane 1: graph nodes ----
        _nodesList = MakeList(_nodeTemplate);
        _nodesList.SelectionChanged += OnNodeSelected;
        _nodesEmpty = EmptyHint("No commits");
        var graphPane = LabeledPane("Commits · this branch", _nodesList, _nodesEmpty);

        // ---- pane 2: files in the selected node ----
        _filesList = MakeList(_wipFileTemplate);
        _filesList.SelectionChanged += OnFileSelected;
        _filesEmpty = EmptyHint("Select a node");
        var filesPane = LabeledPane("Files", _filesList, _filesEmpty);

        // ---- pane 3: find bar + diff ----
        _diff = new DiffView();
        _diff.SetSplit(_split);
        _diff.SetWrap(_wrap);
        _diff.SearchResultsChanged += OnSearchResults;
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

        Content = new DockPanel { Children = { header, body } };
    }

    // A vertical drag handle between two panes.
    private static GridSplitter Splitter() => new()
    {
        Width = 6,
        Background = Palette.SeparatorBrush,
        ResizeDirection = GridResizeDirection.Columns,
        HorizontalAlignment = HorizontalAlignment.Stretch,
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
    public void Retarget(string cwd, string title, PullRequestInfo? pr)
    {
        if (!string.Equals(cwd, _cwd, StringComparison.Ordinal))
            _baseOverride = null; // a new repo: re-decide the base automatically
        _cwd = cwd;
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

                    _nodes = BuildNodes(status, commits);
                    _suppressSelect = true;
                    _nodesList.ItemsSource = _nodes;
                    _nodesList.SelectedIndex = -1;
                    _suppressSelect = false;

                    int idx = keepNode is not null ? IndexOfNodeKey(_nodes, keepNode) : -1;
                    if (idx < 0 && _nodes.Count > 0) idx = 0; // default: select the tip
                    _pendingFilePath = keepFile;              // OnNodeSelected reselects this file after load
                    _nodesList.SelectedIndex = idx;

                    UpdateEmptyStates();
                });
            });
    }

    private static IReadOnlyList<TreeNode> BuildNodes(GitRepoStatus? status, IReadOnlyList<GitCommit> commits)
    {
        var nodes = new List<TreeNode>();
        if (status is { IsClean: false } s)
            nodes.Add(new TreeNode(IsWip: true, IsHead: false, default, s.Changes.Count));
        for (int i = 0; i < commits.Count; i++)
            nodes.Add(new TreeNode(false, IsHead: i == 0, commits[i], 0));
        return nodes;
    }

    // ---- node selection -> files pane ----

    private string? _pendingFilePath; // file to reselect once a node's files have loaded (refresh path)

    private void OnNodeSelected(object? sender, SelectionChangedEventArgs e)
    {
        if (_suppressSelect || _nodesList.SelectedItem is not TreeNode node || _cwd is not { } cwd) return;

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
            PopulateFiles(changes, wantFile is null ? null : IndexOfPath(changes, wantFile));
        }
        else
        {
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

        var diffs = new List<(string? Label, GitDiff Diff)>();
        if (hasStaged && hasUnstaged)
        {
            if (_git.GetWorkingDiff(cwd, fc.Path, staged: true) is { } staged) diffs.Add(("Staged", staged));
            if (_git.GetWorkingDiff(cwd, fc.Path, staged: false) is { } unstaged) diffs.Add(("Unstaged", unstaged));
        }
        else if (_git.GetWorkingDiff(cwd, fc.Path, staged: hasStaged) is { } d)
        {
            diffs.Add((null, d));
        }

        if (IsPlainModified(fc) && diffs.Count > 0 && diffs.All(x => GitRepoService.HasNoTextChange(x.Diff)))
            return ([], "Content unchanged — only a line ending or byte-order mark (BOM) differs.");

        var sections = diffs.Where(x => x.Diff.Files.Count > 0)
                            .Select(x => new DiffSection(x.Label, x.Diff)).ToList();
        return sections.Count > 0 ? (sections, null) : ([], $"No diff for {fc.Path}.");

        static (IReadOnlyList<DiffSection>, string?) Single(GitDiff? diff, string path) =>
            diff is { Files.Count: > 0 } d
                ? (new[] { new DiffSection(null, d) }, null)
                : ([], $"No diff for {path}.");
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
                    _nodes = BuildNodes(status, commits);
                    _suppressSelect = true;
                    _nodesList.ItemsSource = _nodes;
                    _nodesList.SelectedIndex = -1;
                    _suppressSelect = false;
                    int idx = keepNode is not null ? IndexOfNodeKey(_nodes, keepNode) : -1;
                    if (idx < 0 && _nodes.Count > 0) idx = 0;
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

        static void Style(Button b, bool active)
        {
            b.Background = active ? new SolidColorBrush(Palette.Accent) : Palette.ButtonBgBrush;
            b.Foreground = active ? Palette.OnAccentBrush : Palette.FgBrush;
        }
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

    // A titled pane: a section label over a scrolling list, with a dim empty-state hint overlaid on the list.
    private static Control LabeledPane(string title, ListBox list, TextBlock empty)
    {
        var label = new TextBlock
        {
            Text = title, Foreground = Palette.MutedBrush, FontSize = 11, FontWeight = FontWeight.SemiBold,
            Margin = new Thickness(12, 10, 12, 6), [DockPanel.DockProperty] = Dock.Top,
        };
        var listArea = new Grid { Children = { list, empty } };
        return new DockPanel { LastChildFill = true, Children = { label, listArea } };
    }

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
        new((node, _) => node is null ? new Control() : NodeRow(node), supportsRecycling: false);

    /// <summary>Builds one graph-node row (the lane rail + knot beside the commit / working-tree summary).
    /// Static and self-contained so the render harness can eyeball it with sample data.</summary>
    internal static Control NodeRow(TreeNode node)
    {
        var rail = new Grid { Width = 26 };
        rail.Children.Add(new Rectangle
        {
            Width = 2, Fill = Palette.AccentBrush, Opacity = 0.5,
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Stretch,
        });
        Shape knot = node.IsWip
            ? new Ellipse
            {
                Width = 13, Height = 13, StrokeThickness = 2,
                Stroke = new SolidColorBrush(Palette.Brand), Fill = new SolidColorBrush(Palette.Sunken),
                StrokeDashArray = new AvaloniaList<double> { 2, 1.4 },
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
            }
            : new Ellipse
            {
                Width = 12, Height = 12,
                Fill = new SolidColorBrush(Palette.Accent),
                Stroke = node.IsHead ? new SolidColorBrush(Palette.AccentHover) : null,
                StrokeThickness = node.IsHead ? 3 : 0,
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
            };
        rail.Children.Add(knot);

        var content = new StackPanel { Orientation = Orientation.Vertical, Margin = new Thickness(6, 10, 10, 10) };
        if (node.IsWip)
        {
            content.Children.Add(new TextBlock
            {
                Text = "Working tree", Foreground = new SolidColorBrush(Palette.Brand),
                FontSize = 13, FontWeight = FontWeight.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis,
            });
            content.Children.Add(new TextBlock
            {
                Text = $"{node.ChangeCount} uncommitted change{(node.ChangeCount == 1 ? "" : "s")}",
                Foreground = Palette.MutedBrush, FontFamily = Mono, FontSize = 11, Margin = new Thickness(0, 3, 0, 0),
            });
        }
        else
        {
            var c = node.Commit;
            var subj = new StackPanel { Orientation = Orientation.Horizontal };
            subj.Children.Add(new TextBlock
            {
                Text = c.Subject, Foreground = Palette.TitleBrush, FontSize = 13,
                TextTrimming = TextTrimming.CharacterEllipsis,
            });
            if (node.IsHead)
                subj.Children.Add(new Border
                {
                    Background = new SolidColorBrush(Palette.Accent), CornerRadius = new CornerRadius(3),
                    Padding = new Thickness(5, 0, 5, 1), Margin = new Thickness(6, 1, 0, 0),
                    VerticalAlignment = VerticalAlignment.Center,
                    Child = new TextBlock { Text = "HEAD", Foreground = Palette.OnAccentBrush, FontSize = 9, FontFamily = Mono },
                });
            content.Children.Add(subj);
            content.Children.Add(new TextBlock
            {
                Text = $"{c.ShortHash} · {c.Author} · {RelTime(c.Date)}",
                Foreground = Palette.MutedBrush, FontFamily = Mono, FontSize = 11, Margin = new Thickness(0, 3, 0, 0),
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

    private FuncDataTemplate<GitFileChange> WipFileTemplate() => new((fc, _) => new TextBlock
    {
        Text = fc.Path is null ? "" : WipFileLabel(fc),
        FontFamily = Mono, FontSize = 12, Margin = new Thickness(12, 6, 12, 6),
        Foreground = new SolidColorBrush(fc.Path is null ? Palette.Muted : WipFileColor(fc)),
        TextTrimming = TextTrimming.CharacterEllipsis,
    }, supportsRecycling: true);

    private FuncDataTemplate<GitDiffFile> CommitFileTemplate() => new((gf, _) =>
    {
        string? path = gf.NewPath ?? gf.OldPath;
        return new TextBlock
        {
            Text = path is null ? "" : $"{CommitFileCode(gf)}  {(gf.OldPath is { } o && gf.NewPath is { } n && o != n ? $"{o} → {n}" : path)}",
            FontFamily = Mono, FontSize = 12, Margin = new Thickness(12, 6, 12, 6),
            Foreground = new SolidColorBrush(path is null ? Palette.Muted : CommitFileColor(gf)),
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

    private Color WipFileColor(GitFileChange fc)
    {
        if (fc.Untracked) return Palette.Green;
        if (_invisiblePaths.Contains(fc.Path)) return Palette.Muted;
        var k = fc.Unstaged != GitChangeKind.None ? fc.Unstaged : fc.Staged;
        return k switch
        {
            GitChangeKind.Added => Palette.Green,
            GitChangeKind.Deleted => Palette.Red,
            _ => Palette.Orange,
        };
    }

    private static char CommitFileCode(GitDiffFile gf) =>
        gf.OldPath is null ? 'A'
        : gf.NewPath is null ? 'D'
        : gf.OldPath != gf.NewPath ? 'R'
        : 'M';

    private static Color CommitFileColor(GitDiffFile gf) =>
        gf.OldPath is null ? Palette.Green
        : gf.NewPath is null ? Palette.Red
        : Palette.Orange;

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
