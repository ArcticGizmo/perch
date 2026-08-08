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
    private readonly Button _hunkBtn;
    private readonly Button _diffBtn;
    private readonly Button _prevBtn;
    private readonly Button _currBtn;
    private readonly Border _modeSegment;   // Unified/Split/Hunk segment — hidden unless content mode is Diff
    private readonly Border _floatingBar;    // floating control bar over the bottom of the diff pane
    private readonly Button _themeBtn;       // sun/moon light-dark glyph, lives in the floating bar
    private readonly Button _expandBtn;
    private readonly Button _stageLinesBtn;
    private readonly Border _headerBorder;
    private readonly TextBlock _graphLabel;
    private readonly TextBlock _filesLabel;
    private readonly TextBlock _unstagedLabel;
    private readonly TextBlock _stagedLabel;
    private readonly Button _stageAllBtn;
    private readonly Button _unstageAllBtn;
    private readonly TextBlock _composerLabel;
    private readonly ListBox _nodesList;
    private readonly ListBox _filesList;      // commit-node files (read-only)
    private readonly ListBox _unstagedList;   // WIP: unstaged changes ("Changes")
    private readonly ListBox _stagedList;     // WIP: staged changes ("Staged · pending commit")
    private readonly Grid _commitAreaHost;    // toggled visible for a commit node
    private readonly DockPanel _wipPanel;     // toggled visible for the working-tree (WIP) node
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
    private readonly FuncDataTemplate<GitFileChange> _unstagedRowTemplate;
    private readonly FuncDataTemplate<GitFileChange> _stagedRowTemplate;
    private readonly FuncDataTemplate<GitDiffFile> _commitFileTemplate;

    private string? _cwd;
    private string? _prUrl;
    private string? _branch;
    private bool _isActive; // the session was working (Running/AwaitingInput) at last Retarget — commit guard
    private DiffViewMode _mode;
    private ContentMode _contentMode = ContentMode.Diff; // Diff / Previous / Current — not persisted
    private int _gen;
    private bool _suppressSelect;

    // The working-tree file currently shown in the diff — its path and which side (staged/unstaged), so a
    // refresh can reselect it in the right group. Null when no WIP file is shown.
    private (string Path, bool Staged)? _shownWip;
    private (string Hash, GitDiffFile File)? _shownCommit; // the shown commit file, for Previous/Current reloads
    private string _selectedCommitHash = "";
    private bool _pendingWipStaged; // side to reselect once the WIP lists reload (mirrors _pendingFilePath)

    // Segmented-control chrome (container borders + inner dividers), recoloured on the light/dark toggle.
    private readonly List<Border> _segmentContainers = new();
    private readonly List<Border> _segmentDividers = new();

    // Base-ref scoping. _baseOverride: null = auto-pick, "" = explicitly unscoped ("recent"), else a ref.
    private string? _baseOverride;
    private IReadOnlyList<string> _baseCandidates = [];
    private string? _effectiveBase; // what the current node list is actually scoped to (null = unscoped)

    // Auto-refresh (filesystem watcher, debounced).
    private FileSystemWatcher? _watcher;
    private DispatcherTimer? _debounce;
    // When we perform our own staging write, the .git/index change trips the watcher into a redundant full
    // refresh. This timestamp (Environment.TickCount64) lets the debounce swallow that self-induced fire.
    private long _lastSelfRefresh;

    // What is currently shown, so an auto-refresh updates only what changed and leaves the diff (and its
    // scroll) untouched otherwise.
    private GitRepoStatus? _lastStatus;
    private IReadOnlyList<GitCommit> _lastCommits = [];
    private IReadOnlyList<TreeNode> _nodes = [];
    private GitDiff? _selectedCommitDiff; // the selected commit node's whole diff, so file clicks are instant
    private string _shownDiffSig = "";
    private HashSet<string> _invisiblePaths = new(StringComparer.Ordinal);

    internal enum NodeKind { Wip, Commit, Base }

    /// <summary>The three mutually-exclusive diff view modes. <see cref="Unified"/> and <see cref="Split"/>
    /// stage whole files (buttons on the file header); <see cref="Hunk"/> builds on the split layout and adds
    /// per-hunk and per-line staging. Persisted across the existing <c>GitReviewSplitView</c> +
    /// <c>GitTreeHunkStaging</c> settings (Hunk ⇒ both; Split ⇒ split only; Unified ⇒ neither).</summary>
    internal enum DiffViewMode { Unified, Split, Hunk }

    /// <summary>What the diff pane shows for the selected file. <see cref="Diff"/> is the delta (the default,
    /// not persisted, and the only mode that offers the Unified/Split/Hunk layouts + staging).
    /// <see cref="Previous"/> and <see cref="Current"/> show the whole file — as it was before the change, or
    /// with the change applied — as a plain listing with no deltas.</summary>
    internal enum ContentMode { Diff, Previous, Current }

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
        _mode = settings.GitTreeHunkStaging ? DiffViewMode.Hunk
              : settings.GitReviewSplitView ? DiffViewMode.Split
              : DiffViewMode.Unified;
        _light = settings.GitTreeLight;
        _pal = _light ? TreePalette.Light() : TreePalette.Dark();
        Title = "Tree";
        Width = 1560;
        Height = 1020;
        MinWidth = 820;
        MinHeight = 460;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;

        _nodeTemplate = NodeTemplate();
        _unstagedRowTemplate = WipRowTemplate(staged: false);
        _stagedRowTemplate = WipRowTemplate(staged: true);
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

        // View controls live in a floating bar over the diff (built below): the layout segment
        // (Unified/Split/Hunk) and the content segment (Diff/Previous/Current).
        _unifiedBtn = ModeButton("Unified", DiffViewMode.Unified);
        _splitBtn = ModeButton("Split", DiffViewMode.Split);
        _hunkBtn = ModeButton("Hunk", DiffViewMode.Hunk);
        _diffBtn = ContentButton("Diff", ContentMode.Diff);
        _prevBtn = ContentButton("Previous", ContentMode.Previous);
        _currBtn = ContentButton("Current", ContentMode.Current);

        // Stage/unstage the selected diff lines. Only shown in Hunk + Diff mode; disabled until a line
        // selection sits within one stageable hunk (select line numbers in a working-tree section, then click).
        _stageLinesBtn = new Button
        {
            Content = "Stage lines", IsEnabled = false, IsVisible = false,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(8, 0, 0, 0), [DockPanel.DockProperty] = Dock.Right,
        };
        ToolTip.SetTip(_stageLinesBtn, "Select line numbers in the working-tree diff, then stage/unstage just those lines");
        // Click wired after _diff is constructed (below), so it can't dereference a not-yet-set field.

        // Per-window light/dark toggle — a sun/moon glyph in the floating bar. Reading a lot of diff/commit
        // text is easier when this window can go light without flipping the always-on-top overlay.
        _themeBtn = new Button
        {
            FontSize = 16, Padding = new Thickness(8, 2), VerticalAlignment = VerticalAlignment.Center,
            Background = Brushes.Transparent, BorderThickness = new Thickness(0),
            Cursor = new Cursor(StandardCursorType.Hand),
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
                Children = { _prChip, refreshBtn, _stageLinesBtn, _expandBtn, _baseBtn, headerText },
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

        // ---- pane 2: files in the selected node ----
        // A commit node shows one read-only list; the working-tree (WIP) node shows two groups — unstaged
        // "Changes" over "Staged · pending commit" — with the commit composer docked under the staged group.
        _filesList = MakeList(_commitFileTemplate);
        _filesList.SelectionChanged += OnCommitFileSelected;
        _filesEmpty = EmptyHint("Select a node");

        _unstagedList = MakeList(_unstagedRowTemplate);
        _unstagedList.SelectionChanged += (_, _) => OnWipFileSelected(_unstagedList, staged: false);
        _stagedList = MakeList(_stagedRowTemplate);
        _stagedList.SelectionChanged += (_, _) => OnWipFileSelected(_stagedList, staged: true);

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
            Padding = new Thickness(12),
            [DockPanel.DockProperty] = Dock.Bottom,
            Child = new StackPanel { Children = { _composerLabel, _msgBox, commitRow } },
        };

        // WIP two-group panel: "Changes" (unstaged) over "Staged · pending commit", composer docked bottom.
        // Each group header carries a bulk action on the right: Stage all / Unstage all.
        _unstagedLabel = GroupLabel("Changes");
        _stagedLabel = GroupLabel("Staged · pending commit");
        _stageAllBtn = HeaderActionButton("Stage all");
        _stageAllBtn.Click += (_, _) => StageAll();
        _unstageAllBtn = HeaderActionButton("Unstage all");
        _unstageAllBtn.Click += (_, _) => UnstageAll();
        var unstagedGroup = new DockPanel
        {
            LastChildFill = true, Children = { GroupHeader(_unstagedLabel, _stageAllBtn), _unstagedList },
        };
        var stagedGroup = new DockPanel
        {
            LastChildFill = true, Children = { GroupHeader(_stagedLabel, _unstageAllBtn), _stagedList },
        };
        var groups = new Grid { RowDefinitions = new RowDefinitions("*,Auto,*") };
        groups.Children.Add(WithRow(unstagedGroup, 0));
        groups.Children.Add(WithRow(GroupSeparator(), 1));
        groups.Children.Add(WithRow(stagedGroup, 2));
        _wipPanel = new DockPanel { LastChildFill = true, IsVisible = false, Children = { _composer, groups } };

        _commitAreaHost = new Grid { Children = { _filesList, _filesEmpty } };

        _filesLabel = PaneLabel("Files");
        var filesBody = new Grid { Children = { _commitAreaHost, _wipPanel } };
        var filesPane = new DockPanel { LastChildFill = true, Children = { _filesLabel, filesBody } };

        // ---- pane 3: find bar + diff ----
        _diff = new DiffView { Padding = new Thickness(0, 0, 0, 64) }; // clear space for the floating bar
        _diff.SetSplit(_mode != DiffViewMode.Unified);
        _diff.SetPerHunk(_mode == DiffViewMode.Hunk);
        _diff.SetWrap(true); // always wrap (the wrap toggle was retired)
        _diff.SearchResultsChanged += OnSearchResults;
        _diff.HunkStageRequested += OnHunkStage;
        _diff.FileStageRequested += OnFileStage;
        _diff.LineStageStateChanged += OnLineStageState;
        _diff.LineStageRequested += OnLineStageRequest;
        _stageLinesBtn.Click += (_, _) => _diff.RequestStageSelection();
        UpdateModeButtons();
        UpdateContentButtons();
        UpdateStageLinesVisibility();
        _diffScroll = new ScrollViewer
        {
            Content = _diff,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
        };

        // Floating control bar: [☀/☾]  [Previous|Diff|Current]  [Unified|Split|Hunk]. Overlays the bottom of
        // the diff pane, centred. Diff sits between the two whole-file views; the content segment comes before
        // the layout segment because it gates whether the layout segment shows.
        _modeSegment = Segment(_unifiedBtn, _splitBtn, _hunkBtn);
        _modeSegment.IsVisible = _contentMode == ContentMode.Diff;
        var contentSegment = Segment(_prevBtn, _diffBtn, _currBtn);
        var barContent = new StackPanel
        {
            Orientation = Orientation.Horizontal, Spacing = 10, VerticalAlignment = VerticalAlignment.Center,
            Children = { _themeBtn, contentSegment, _modeSegment },
        };
        _floatingBar = new Border
        {
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Bottom,
            Margin = new Thickness(0, 0, 0, 18), Padding = new Thickness(10, 6),
            CornerRadius = new CornerRadius(12), BorderThickness = new Thickness(1),
            BoxShadow = new BoxShadows(new BoxShadow { Blur = 16, OffsetY = 3, Color = Color.FromArgb(90, 0, 0, 0) }),
            Child = barContent,
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
        var diffArea = new Grid { Children = { _diffScroll, _diffPlaceholder, _floatingBar } };
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

    private static Control WithRow(Control c, int row)
    {
        c.SetValue(Grid.RowProperty, row);
        return c;
    }

    // A WIP group's header ("Changes" / "Staged · pending commit"); its count is appended live and its
    // colour is applied by ApplyPalette (like PaneLabel, but kept as a field so the count can be updated).
    private static TextBlock GroupLabel(string text) => new()
    {
        Text = text, Foreground = Palette.MutedBrush, FontSize = 11, FontWeight = FontWeight.SemiBold,
        Margin = new Thickness(12, 10, 12, 6), VerticalAlignment = VerticalAlignment.Center,
    };

    // A group's header row: the label filling, a bulk-action button ("Stage all"/"Unstage all") docked right.
    private static Control GroupHeader(TextBlock label, Button action)
    {
        action.SetValue(DockPanel.DockProperty, Dock.Right);
        return new DockPanel { LastChildFill = true, [DockPanel.DockProperty] = Dock.Top, Children = { action, label } };
    }

    private static Button HeaderActionButton(string text) => new()
    {
        Content = text, FontSize = 11, Padding = new Thickness(8, 2),
        VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0),
        Cursor = new Cursor(StandardCursorType.Hand),
    };

    // The thin horizontal rule between the two WIP groups (recoloured with the window's palette).
    private Border GroupSeparator()
    {
        var b = new Border { Height = 1, Background = _pal.Separator, Margin = new Thickness(12, 2) };
        _groupSeparators.Add(b);
        return b;
    }
    private readonly List<Border> _groupSeparators = new();

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
        string? keepFile = preserveSelection ? CurrentFilePath() : null;
        bool keepStaged = _shownWip?.Staged ?? false;

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
                    _pendingWipStaged = keepStaged;             // …on the same side (staged/unstaged)
                    _nodesList.SelectedIndex = idx;

                    UpdateEmptyStates();
                });
            });
    }

    // A fast refresh after a working-tree staging action (stage/unstage/discard a hunk, line, or file, or
    // Stage/Unstage all). Staging never touches the commit graph, so this skips the base-ref + branch-log
    // work of RefreshStatus and reuses the cached commits — it just re-reads status (one git call) and lets
    // the reselected file reload its own diff. Falls back to a full refresh if the tree went clean (the WIP
    // node must then disappear).
    // <paramref name="advanceUnstaged"/> (after "Stage file"): land on the topmost remaining "Changes" file
    // instead of following the just-staged file over to the staged group (blank if nothing is left unstaged).
    private void RefreshWorkingTree(bool advanceUnstaged = false)
    {
        if (_cwd is not { } cwd) return;
        if (_nodesList.SelectedItem is not TreeNode { IsWip: true }) { RefreshStatus(preserveSelection: true); return; }

        int gen = ++_gen;
        string? keepPath = _shownWip?.Path;
        bool keepStaged = _shownWip?.Staged ?? false;

        System.Threading.Tasks.Task.Run(() => _git.GetStatus(cwd)).ContinueWith(t =>
        {
            if (!t.IsCompletedSuccessfully) return;
            Dispatcher.UIThread.Post(() =>
            {
                if (!IsVisible || gen != _gen) return;
                _lastSelfRefresh = Environment.TickCount64;
                var status = t.Result;

                // Tree went clean → the WIP node is gone; a full refresh rebuilds the graph and lands on HEAD.
                if (status is not { IsClean: false } s) { RefreshStatus(preserveSelection: false); return; }

                bool countChanged = _lastStatus is not { } ls || ls.Changes.Count != s.Changes.Count;
                _lastStatus = status;
                ApplyStatus(status, cwd);

                if (countChanged)
                {
                    // A file entered/left the change set → rebuild the node list (WIP count) from the cached
                    // commits — no git — and reselect the WIP node, which repopulates its groups + diff.
                    string? keepNode = _nodesList.SelectedItem is TreeNode n ? n.Key : null;
                    _nodes = BuildNodes(status, _lastCommits, _effectiveBase);
                    _suppressSelect = true;
                    _nodesList.ItemsSource = _nodes;
                    _nodesList.SelectedIndex = -1;
                    _suppressSelect = false;
                    int idx = keepNode is not null ? IndexOfNodeKey(_nodes, keepNode) : -1;
                    if (idx < 0) idx = FirstSelectable(_nodes);
                    _pendingFilePath = keepPath;
                    _pendingWipStaged = keepStaged;
                    _nodesList.SelectedIndex = idx;
                    UpdateEmptyStates();
                    return;
                }

                // Same file set: repopulate the two groups (follows the file to its new side if it moved) and
                // repaint the diff only if its content actually changed. After "Stage file", advance to the
                // topmost remaining Changes file instead of following the staged one.
                PopulateWip(s.Changes, keepPath, keepStaged, preferUnstagedTop: advanceUnstaged);
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
        bool wantStaged = _pendingWipStaged;
        _pendingFilePath = null;

        int gen = ++_gen;
        _selectedCommitDiff = null;
        _diff.SetLoading();
        _diffPlaceholder.IsVisible = false;
        _shownDiffSig = "";

        if (node.IsWip)
        {
            _wipPanel.IsVisible = true;
            _commitAreaHost.IsVisible = false;
            _shownCommit = null;
            _filesList.ItemsSource = null;
            PopulateWip(_lastStatus?.Changes ?? [], wantFile, wantStaged);
        }
        else
        {
            _wipPanel.IsVisible = false;
            _commitAreaHost.IsVisible = true;
            _shownWip = null;
            _shownCommit = null;
            _selectedCommitHash = node.Commit.Hash;
            _suppressSelect = true;
            _unstagedList.ItemsSource = null;
            _stagedList.ItemsSource = null;
            _suppressSelect = false;

            string hash = node.Commit.Hash;
            System.Threading.Tasks.Task.Run(() => _git.GetCommitDiff(cwd, hash)).ContinueWith(t =>
            {
                if (!t.IsCompletedSuccessfully) return;
                Dispatcher.UIThread.Post(() =>
                {
                    if (!IsVisible || gen != _gen) return;
                    _selectedCommitDiff = t.Result;
                    var files = t.Result?.Files ?? [];
                    int sel = wantFile is null ? (files.Count > 0 ? 0 : -1) : IndexOfDiffFile(files, wantFile);
                    PopulateFiles(files, sel);
                });
            });
        }
    }

    // ---- WIP two-group population & selection ----

    // Splits the working-tree changes into the unstaged ("Changes") and staged ("pending commit") groups —
    // a file with both appears in both — updates the group counts and composer, and selects the requested
    // file+side (else the first unstaged, else the first staged).
    // <paramref name="quiet"/> (the auto-refresh path): when the same file+side is still present, restore its
    // selection highlight without re-selecting it — so the diff (and its scroll position) is left untouched
    // for the caller's content-signature guard to repaint only if it actually changed.
    // <paramref name="preferUnstagedTop"/> (after "Stage file"): ignore <paramref name="wantPath"/> and land
    // on the topmost remaining "Changes" row so the user keeps working down the list — or, if nothing is left
    // unstaged, clear to the placeholder rather than jumping over to the staged group.
    private void PopulateWip(IReadOnlyList<GitFileChange> changes, string? wantPath, bool wantStaged,
        bool quiet = false, bool preferUnstagedTop = false)
    {
        var unstaged = changes.Where(c => c.Untracked || c.Unstaged != GitChangeKind.None).ToList();
        var staged = changes.Where(c => c.Staged != GitChangeKind.None).ToList();

        _suppressSelect = true;
        _unstagedList.ItemsSource = unstaged;
        _stagedList.ItemsSource = staged;
        _unstagedList.SelectedIndex = -1;
        _stagedList.SelectedIndex = -1;

        _unstagedLabel.Text = $"Changes ({unstaged.Count})";
        _stagedLabel.Text = $"Staged · pending commit ({staged.Count})";
        _stageAllBtn.IsEnabled = unstaged.Count > 0;
        _unstageAllBtn.IsEnabled = staged.Count > 0;
        UpdateComposer();

        if (preferUnstagedTop)
        {
            _suppressSelect = false;
            if (unstaged.Count > 0)
            {
                _unstagedList.SelectedIndex = 0; // fires OnWipFileSelected → loads the topmost Changes diff
            }
            else
            {
                _shownWip = null;
                _diff.SetDiff(null, "");
                _diffPlaceholder.IsVisible = true;
            }
            UpdateEmptyStates();
            return;
        }

        int uidx = wantPath is not null ? IndexOfPath(unstaged, wantPath) : -1;
        int sidx = wantPath is not null ? IndexOfPath(staged, wantPath) : -1;

        // Choose the row to select: the requested side first, then the other side (the file may have moved
        // sides — e.g. its last unstaged hunk was staged), then the first row of either group.
        (ListBox list, int idx, bool side) =
            !wantStaged && uidx >= 0 ? (_unstagedList, uidx, false)
          : wantStaged && sidx >= 0 ? (_stagedList, sidx, true)
          : uidx >= 0 ? (_unstagedList, uidx, false)
          : sidx >= 0 ? (_stagedList, sidx, true)
          : unstaged.Count > 0 ? (_unstagedList, 0, false)
          : staged.Count > 0 ? (_stagedList, 0, true)
          : (_unstagedList, -1, false);

        if (idx < 0)
        {
            _suppressSelect = false;
            _shownWip = null;
            _diff.SetDiff(null, "");
            _diffPlaceholder.IsVisible = true;
            UpdateEmptyStates();
            return;
        }

        // Quiet + the shown file stayed on the same side: reselect under suppression so the diff (and its
        // scroll) survive — the caller's content-signature guard repaints only if it actually changed.
        if (quiet && side == wantStaged && (side ? sidx : uidx) >= 0)
        {
            list.SelectedIndex = idx;
            _suppressSelect = false;
            UpdateEmptyStates();
            return;
        }

        _suppressSelect = false;
        list.SelectedIndex = idx; // fires OnWipFileSelected → loads the diff and sets _shownWip
        UpdateEmptyStates();
    }

    // A file was selected in one of the two WIP lists: clear the other list's selection and show it according
    // to the current content mode (Diff / Previous / Current).
    private void OnWipFileSelected(ListBox list, bool staged)
    {
        if (_suppressSelect || _cwd is not { } cwd) return;
        if (list.SelectedItem is not GitFileChange fc || fc.Path is null) return;

        _suppressSelect = true;
        (staged ? _unstagedList : _stagedList).SelectedIndex = -1;
        _suppressSelect = false;

        _diffPlaceholder.IsVisible = false;
        _shownWip = (fc.Path, staged);
        _shownCommit = null;
        LoadWipContent(cwd, fc.Path, staged);
    }

    // Loads the shown working-tree file per the current content mode (off-thread), then paints it.
    private void LoadWipContent(string cwd, string path, bool staged)
    {
        var status = _lastStatus;
        var mode = _contentMode;
        int gen = ++_gen;
        _diff.SetLoading();
        _shownDiffSig = "";
        System.Threading.Tasks.Task.Run(() => WipContentSections(cwd, status, path, staged, mode)).ContinueWith(t =>
        {
            if (!t.IsCompletedSuccessfully) return;
            Dispatcher.UIThread.Post(() =>
            {
                if (!IsVisible || gen != _gen) return;
                ShowContent(mode, t.Result.sections, t.Result.note);
            });
        });
    }

    // The sections for a working-tree file in a given content mode. Diff → the delta for that side; Previous →
    // the file before the change (unstaged: the index; staged: HEAD); Current → the file with the change
    // (unstaged: the worktree; staged: the index). Pure of UI — used by both selection and auto-refresh.
    private (IReadOnlyList<DiffSection> sections, string? note) WipContentSections(
        string cwd, GitRepoStatus? status, string path, bool staged, ContentMode mode)
    {
        var fc = FindByPath(status, path);
        if (mode == ContentMode.Diff)
            return fc is { } f ? LoadWipSideDiff(cwd, f, staged) : ([], $"No diff for {path}.");

        bool untracked = fc?.Untracked ?? false;
        if (mode == ContentMode.Previous)
        {
            string? content = untracked && !staged ? "" : _git.GetFileAtRef(cwd, path, staged ? "HEAD" : "");
            return PlainSections(path, content, "No previous version — this file is new.");
        }
        string? cur = staged ? _git.GetFileAtRef(cwd, path, "") : ReadWorktree(cwd, path);
        return PlainSections(path, cur, "This file is empty or missing.");
    }

    // Sets the commit-node files list and selects a row (default first), which fires OnCommitFileSelected to
    // load the diff.
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

    private void OnCommitFileSelected(object? sender, SelectionChangedEventArgs e)
    {
        if (_suppressSelect || _cwd is not { } cwd) return;
        if (_filesList.SelectedItem is not GitDiffFile gf) return;
        _diffPlaceholder.IsVisible = false;
        _shownWip = null;
        _shownCommit = (_selectedCommitHash, gf);
        LoadCommitContent(cwd, _selectedCommitHash, gf);
    }

    // Loads the shown commit file per the current content mode. Diff is already in hand (no git); Previous/
    // Current read the file at the commit's parent / the commit itself.
    private void LoadCommitContent(string cwd, string hash, GitDiffFile gf)
    {
        var mode = _contentMode;
        if (mode == ContentMode.Diff)
        {
            var one = new GitDiff([gf]);
            IReadOnlyList<DiffSection> sections = gf.Hunks.Count > 0 || gf.IsBinary ? [new DiffSection(null, one)] : [];
            string? note = sections.Count > 0 ? null : $"No textual diff for {gf.NewPath ?? gf.OldPath}.";
            ShowContent(mode, sections, note);
            return;
        }
        int gen = ++_gen;
        _diff.SetLoading();
        _shownDiffSig = "";
        System.Threading.Tasks.Task.Run(() =>
        {
            if (mode == ContentMode.Previous)
            {
                string? content = gf.OldPath is { } o ? _git.GetFileAtRef(cwd, o, hash + "^") : "";
                return PlainSections(gf.NewPath ?? gf.OldPath ?? "", content, "No previous version — added in this commit.");
            }
            string? cur = gf.NewPath is { } n ? _git.GetFileAtRef(cwd, n, hash) : "";
            return PlainSections(gf.NewPath ?? gf.OldPath ?? "", cur, "No current version — deleted in this commit.");
        }).ContinueWith(t =>
        {
            if (!t.IsCompletedSuccessfully) return;
            Dispatcher.UIThread.Post(() =>
            {
                if (!IsVisible || gen != _gen) return;
                ShowContent(mode, t.Result.sections, t.Result.note);
            });
        });
    }

    // Reloads whatever file is currently shown (WIP side or commit file) in the current content mode — used
    // when the content mode changes.
    private void ReloadShownContent()
    {
        if (_cwd is not { } cwd) return;
        if (_shownWip is { } w) LoadWipContent(cwd, w.Path, w.Staged);
        else if (_shownCommit is { } c) LoadCommitContent(cwd, c.Hash, c.File);
    }

    private void ShowContent(ContentMode mode, IReadOnlyList<DiffSection> sections, string? note)
    {
        _diff.SetSections(sections, note, plain: mode != ContentMode.Diff);
        _shownDiffSig = DiffSig(sections);
        _diffPlaceholder.IsVisible = false;
    }

    // Reads a worktree file's text (for the "Current" view of an unstaged change). Null on any IO error.
    private static string? ReadWorktree(string cwd, string path)
    {
        try { return File.ReadAllText(System.IO.Path.Combine(cwd, path)); }
        catch { return null; }
    }

    // Builds a plain, all-context "whole file" section (the Previous/Current views): one file, one hunk whose
    // every line is context, so DiffView renders a numbered listing with no deltas. Null content (missing /
    // errored) or an empty file yields the note instead.
    private static (IReadOnlyList<DiffSection> sections, string? note) PlainSections(string path, string? content, string emptyNote)
    {
        if (content is null) return ([], emptyNote);
        var norm = content.Replace("\r\n", "\n").Replace("\r", "\n");
        var arr = norm.Split('\n');
        int n = arr.Length;
        if (n > 0 && arr[^1].Length == 0) n--; // drop the trailing empty from a final newline
        if (n <= 0) return ([], emptyNote);
        var lines = new List<GitDiffLine>(n);
        for (int i = 0; i < n; i++) lines.Add(new GitDiffLine(GitDiffLineKind.Context, arr[i]));
        var hunk = new GitDiffHunk($"@@ -1,{n} +1,{n} @@", lines);
        var file = new GitDiffFile(path, path, false, new[] { hunk });
        return (new[] { new DiffSection(null, new GitDiff(new[] { file })) }, null);
    }

    // The diff for one side of a working-tree change: the staged side (index vs HEAD, unstageable) or the
    // unstaged side (worktree vs index — untracked files show their full contents — stageable/discardable).
    // The section carries the direction so the diff pane shows the right stage/discard/unstage buttons.
    private (IReadOnlyList<DiffSection> sections, string? note) LoadWipSideDiff(string cwd, GitFileChange fc, bool staged)
    {
        if (staged)
            return _git.GetWorkingDiff(cwd, fc.Path, staged: true) is { Files.Count: > 0 } d
                ? (new[] { new DiffSection(null, d, HunkStageAction.Unstage) }, null)
                : ([], $"No staged diff for {fc.Path}.");

        if (fc.Untracked)
            return _git.GetUntrackedDiff(cwd, fc.Path) is { Files.Count: > 0 } u
                ? (new[] { new DiffSection(null, u, HunkStageAction.Stage) }, null)
                : ([], $"No diff for {fc.Path}.");

        if (_git.GetWorkingDiff(cwd, fc.Path, staged: false) is { Files.Count: > 0 } w)
        {
            if (IsPlainModified(fc) && GitRepoService.HasNoTextChange(w))
                return ([], "Content unchanged — only a line ending or byte-order mark (BOM) differs.");
            return (new[] { new DiffSection(null, w, HunkStageAction.Stage) }, null);
        }
        return ([], $"No diff for {fc.Path}.");
    }

    // ---- commit authoring (Phase 2) ----

    private int StagedCount() => _lastStatus?.Changes.Count(c => c.Staged != GitChangeKind.None) ?? 0;

    // Reflects the staged count in the composer hint and enables the Commit button accordingly.
    private void UpdateComposer()
    {
        int staged = StagedCount();
        _commitBtn.IsEnabled = staged > 0;
        SetHint(staged == 0
            ? "Stage changes (＋ / Stage all), then commit."
            : $"{staged} file{(staged == 1 ? "" : "s")} staged.", warn: false);
    }

    private void SetHint(string text, bool warn)
    {
        _composerHint.Text = text;
        _composerHint.Foreground = warn ? _pal.Orange : _pal.Muted;
    }

    // Stage or unstage a whole file (the row +/− button), then refresh so it moves between the two groups.
    private void ToggleStage(string path, bool stage) =>
        RunWorkingTreeOp(cwd => stage ? _git.StageFile(cwd, path) : _git.UnstageFile(cwd, path));

    // Runs a working-tree write off the UI thread, then does a fast working-tree refresh (or reports git's
    // error inline). The shared path for the row +/− buttons and the Stage/Unstage-all header buttons.
    private void RunWorkingTreeOp(Func<string, (bool Ok, string Error)> op)
    {
        if (_cwd is not { } cwd) return;
        System.Threading.Tasks.Task.Run(() => op(cwd)).ContinueWith(t =>
        {
            if (!t.IsCompletedSuccessfully) return;
            Dispatcher.UIThread.Post(() =>
            {
                if (!IsVisible) return;
                if (!t.Result.Ok) { SetHint(FirstLine(t.Result.Error), warn: true); return; }
                RefreshWorkingTree();
            });
        });
    }

    private void StageAll() => RunWorkingTreeOp(cwd => _git.StageAll(cwd));
    private void UnstageAll() => RunWorkingTreeOp(cwd => _git.UnstageAll(cwd));

    // Stage, unstage, or discard a single hunk (from its button in the diff pane), then refresh so the file
    // list, staged count, and the diff's staged/unstaged split reflect the change. Discard is destructive, so
    // it's confirmed first.
    private async void OnHunkStage(HunkStageRequest req)
    {
        if (_cwd is not { } cwd || req.Path is not { } path) return;

        if (req.Action == HunkStageAction.Discard)
        {
            bool go = await ConfirmDialog.ShowAsync(this, "Discard hunk",
                $"Discard this hunk's changes to {System.IO.Path.GetFileName(path)}? This can't be undone.",
                "Discard", "Cancel");
            if (!go) return;
        }

        var (ok, err) = await System.Threading.Tasks.Task.Run(() => req.Action switch
        {
            HunkStageAction.Stage => _git.StageHunk(cwd, path, req.HunkHeader),
            HunkStageAction.Unstage => _git.UnstageHunk(cwd, path, req.HunkHeader),
            HunkStageAction.Discard => _git.DiscardHunk(cwd, path, req.HunkHeader),
            _ => (false, ""),
        });
        if (!IsVisible) return;
        if (!ok) { SetHint(FirstLine(err), warn: true); return; }
        RefreshWorkingTree();
    }

    // Stage, unstage, or discard a whole file from the diff pane's file-scope buttons (Unified/Split modes),
    // then refresh. Discard is destructive (drops unstaged edits / removes an untracked file), so it's
    // confirmed first.
    private async void OnFileStage(FileStageRequest req)
    {
        if (_cwd is not { } cwd || req.Path is not { } path) return;

        bool untracked = FindByPath(_lastStatus, path)?.Untracked ?? false;
        if (req.Action == HunkStageAction.Discard)
        {
            string what = untracked ? "Delete" : "Discard changes to";
            bool go = await ConfirmDialog.ShowAsync(this, "Discard file",
                $"{what} {System.IO.Path.GetFileName(path)}? This can't be undone.",
                untracked ? "Delete" : "Discard", "Cancel");
            if (!go) return;
        }

        var (ok, err) = await System.Threading.Tasks.Task.Run(() => req.Action switch
        {
            HunkStageAction.Stage => _git.StageFile(cwd, path),
            HunkStageAction.Unstage => _git.UnstageFile(cwd, path),
            HunkStageAction.Discard => _git.DiscardFile(cwd, path, untracked),
            _ => (false, ""),
        });
        if (!IsVisible) return;
        if (!ok) { SetHint(FirstLine(err), warn: true); return; }
        // Staging the whole file moves it out of "Changes" → advance to the topmost remaining Changes file
        // (or a blank preview if none is left), rather than following it into the staged group.
        RefreshWorkingTree(advanceUnstaged: req.Action == HunkStageAction.Stage);
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
                    RefreshWorkingTree();
                });
            });
    }

    private async void OnCommitClick(object? sender, RoutedEventArgs e)
    {
        if (_cwd is not { } cwd) return;
        string msg = _msgBox.Text?.Trim() ?? "";
        if (msg.Length == 0) { SetHint("Write a commit message.", warn: true); _msgBox.Focus(); return; }
        if (StagedCount() == 0) { SetHint("Stage some changes first.", warn: true); return; }

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
            // Swallow the watcher fire our own staging write just triggered — RefreshWorkingTree already
            // re-read the tree, so a full AutoRefresh here would only re-spawn git for nothing.
            if (Environment.TickCount64 - _lastSelfRefresh < 900) return;
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
        var shown = wipSelected ? _shownWip : null;
        var contentMode = _contentMode;

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
            // The shown working-tree file's content in the active content mode, for the repaint-if-changed
            // guard. (Commit content is immutable, so only a WIP file is ever re-fetched.)
            (IReadOnlyList<DiffSection> sections, string? note)? sel =
                shown is { } sw ? WipContentSections(cwd, status, sw.Path, sw.Staged, contentMode) : null;
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

                bool reselected = false;
                if (changed)
                {
                    string? keepNode = _nodesList.SelectedItem is TreeNode n ? n.Key : null;
                    string? keepFile = CurrentFilePath();
                    bool keepStaged = _shownWip?.Staged ?? false;
                    _nodes = BuildNodes(status, commits, baseRef);
                    _suppressSelect = true;
                    _nodesList.ItemsSource = _nodes;
                    _nodesList.SelectedIndex = -1;
                    _suppressSelect = false;
                    int idx = keepNode is not null ? IndexOfNodeKey(_nodes, keepNode) : -1;
                    if (idx < 0) idx = FirstSelectable(_nodes);
                    // If the same WIP node stays selected, keep it and refresh its groups quietly below;
                    // reselecting would reload the diff. Only drive a reselect when the node changed identity.
                    if (_nodesList.SelectedIndex != idx || keepNode is null)
                    {
                        _pendingFilePath = keepFile;
                        _pendingWipStaged = keepStaged;
                        _nodesList.SelectedIndex = idx;
                        reselected = true;
                    }
                    UpdateEmptyStates();

                    // Same WIP node stayed selected → repopulate its two groups (a file may have been staged/
                    // added/removed externally), quietly so the shown diff & scroll survive.
                    if (!reselected && _nodesList.SelectedItem is TreeNode { IsWip: true })
                        PopulateWip(status?.Changes ?? [], _shownWip?.Path, _shownWip?.Staged ?? false, quiet: true);
                }

                // Repaint a still-shown WIP file only when its content actually changed (skipped when a
                // reselect above already reloaded it).
                if (!reselected && sel is { } s && wipSelected && DiffSig(s.sections) != _shownDiffSig)
                    ShowContent(contentMode, s.sections, s.note);
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

    // The file whose diff is currently shown: the shown WIP side for the working-tree node, else the selected
    // commit file. Used to reselect the same file across a refresh.
    private string? CurrentFilePath()
    {
        if (_nodesList.SelectedItem is TreeNode { IsWip: true }) return _shownWip?.Path;
        return _filesList.SelectedItem is GitDiffFile gf ? gf.NewPath ?? gf.OldPath : null;
    }

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

    // ---- floating bar: segmented view controls ----

    private Button ModeButton(string label, DiffViewMode mode)
    {
        var b = SegButton(label);
        b.Click += (_, _) => SetMode(mode);
        return b;
    }

    private Button ContentButton(string label, ContentMode mode)
    {
        var b = SegButton(label);
        b.Click += (_, _) => SetContentMode(mode);
        return b;
    }

    // A segmented-control button: square corners so adjacent buttons share an edge; its fill is set by the
    // Update*Buttons routines (accent when active).
    private static Button SegButton(string label) => new()
    {
        Content = label, Padding = new Thickness(13, 6), CornerRadius = new CornerRadius(0),
        BorderThickness = new Thickness(0), VerticalAlignment = VerticalAlignment.Center,
        Cursor = new Cursor(StandardCursorType.Hand),
    };

    // Joins buttons into one rounded, clipped control with 1px dividers between them (the "shared edge" look).
    // The container border + dividers are tracked so the light/dark toggle can recolour them.
    private Border Segment(params Button[] buttons)
    {
        var sp = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 0 };
        for (int i = 0; i < buttons.Length; i++)
        {
            if (i > 0)
            {
                var div = new Border { Width = 1, Background = _pal.Separator };
                _segmentDividers.Add(div);
                sp.Children.Add(div);
            }
            sp.Children.Add(buttons[i]);
        }
        var container = new Border
        {
            CornerRadius = new CornerRadius(8), ClipToBounds = true,
            BorderThickness = new Thickness(1), BorderBrush = _pal.Separator,
            VerticalAlignment = VerticalAlignment.Center, Child = sp,
        };
        _segmentContainers.Add(container);
        return container;
    }

    // Switch view mode. Unified/Split stage whole files; Hunk builds on the split layout and adds per-hunk +
    // per-line staging. The DiffView rebuilds itself, so no diff reload is needed. Persisted across the two
    // legacy bools (Hunk ⇒ both; Split ⇒ split only; Unified ⇒ neither).
    private void SetMode(DiffViewMode mode)
    {
        if (_mode == mode) return;
        _mode = mode;
        _diff.SetSplit(mode != DiffViewMode.Unified);
        _diff.SetPerHunk(mode == DiffViewMode.Hunk);
        _settings.GitReviewSplitView = mode is DiffViewMode.Split or DiffViewMode.Hunk;
        _settings.GitTreeHunkStaging = mode == DiffViewMode.Hunk;
        _settings.Save();
        UpdateModeButtons();
        UpdateStageLinesVisibility();
    }

    // Switch content mode. Previous/Current show the whole file (no deltas) and hide the layout segment;
    // reloads the shown file in the new mode. Not persisted (always starts on Diff).
    private void SetContentMode(ContentMode mode)
    {
        if (_contentMode == mode) return;
        _contentMode = mode;
        _modeSegment.IsVisible = mode == ContentMode.Diff;
        UpdateContentButtons();
        UpdateStageLinesVisibility();
        ReloadShownContent();
    }

    private void UpdateStageLinesVisibility() =>
        _stageLinesBtn.IsVisible = _mode == DiffViewMode.Hunk && _contentMode == ContentMode.Diff;

    private void UpdateModeButtons()
    {
        StyleSeg(_unifiedBtn, _mode == DiffViewMode.Unified);
        StyleSeg(_splitBtn, _mode == DiffViewMode.Split);
        StyleSeg(_hunkBtn, _mode == DiffViewMode.Hunk);
    }

    private void UpdateContentButtons()
    {
        StyleSeg(_diffBtn, _contentMode == ContentMode.Diff);
        StyleSeg(_prevBtn, _contentMode == ContentMode.Previous);
        StyleSeg(_currBtn, _contentMode == ContentMode.Current);
    }

    private void StyleSeg(Button b, bool active)
    {
        b.Background = active ? _pal.Accent : _pal.ButtonBg;
        b.Foreground = active ? _pal.OnAccent : _pal.Fg;
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
        _unstagedLabel.Foreground = _pal.Muted;
        _stagedLabel.Foreground = _pal.Muted;
        _composerLabel.Foreground = _pal.Muted;
        _findLabel.Foreground = _pal.Muted;
        _diffPlaceholder.Foreground = _pal.Muted;
        _nodesEmpty.Foreground = _pal.Muted;
        _filesEmpty.Foreground = _pal.Muted;
        _expandBtn.Foreground = _pal.Muted;

        _prChip.Background = _pal.Accent;
        _prChip.Foreground = _pal.OnAccent;
        _commitBtn.Background = _pal.Accent;
        _commitBtn.Foreground = _pal.OnAccent;

        foreach (var s in _splitters) s.Background = _pal.Separator;
        foreach (var s in _groupSeparators) s.Background = _pal.Separator;

        // Floating bar + segmented controls.
        _floatingBar.Background = _pal.HeaderBg;
        _floatingBar.BorderBrush = _pal.Separator;
        foreach (var c in _segmentContainers) c.BorderBrush = _pal.Separator;
        foreach (var d in _segmentDividers) d.Background = _pal.Separator;
        // Sun (go light) when dark, moon (go dark) when light.
        _themeBtn.Content = _light ? "☾" : "☀";
        _themeBtn.Foreground = _pal.Fg;

        UpdateModeButtons();
        UpdateContentButtons();
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
        // _filesEmpty covers the commit-node list only; the WIP node uses its per-group counts instead.
        _filesEmpty.IsVisible = _commitAreaHost.IsVisible && _filesList.ItemCount == 0;
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
        }

        // A transparent background makes the whole row (rail column + any gaps around the text) hit-testable,
        // so the commit-body tooltip opens from anywhere on the card — not just over the text.
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*"), Background = Brushes.Transparent };
        grid.Children.Add(rail);
        content.SetValue(Grid.ColumnProperty, 1);
        grid.Children.Add(content);
        if (node.Kind == NodeKind.Commit)
        {
            ToolTip.SetTip(grid, node.Commit.Body);
            ToolTip.SetShowDelay(grid, 750);
        }
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

    // A WIP file row for one group: the coloured status + path, with a stage ("+") button on unstaged rows
    // and an unstage ("−") button on staged rows docked to the right. The button moves the whole file between
    // the two groups (git add / git restore --staged).
    private FuncDataTemplate<GitFileChange> WipRowTemplate(bool staged) =>
        new((fc, _) => fc.Path is null ? new Control() : WipRow(fc, staged), supportsRecycling: false);

    private Control WipRow(GitFileChange fc, bool staged)
    {
        string path = fc.Path!;
        var btn = new Button
        {
            Content = staged ? "−" : "+",
            FontFamily = Mono, FontSize = 14, FontWeight = FontWeight.SemiBold,
            Padding = new Thickness(9, 0), MinWidth = 0,
            VerticalAlignment = VerticalAlignment.Center, [DockPanel.DockProperty] = Dock.Right,
            Cursor = new Cursor(StandardCursorType.Hand),
        };
        ToolTip.SetTip(btn, staged ? "Unstage this file" : "Stage this file");
        btn.Click += (_, e) => { ToggleStage(path, !staged); e.Handled = true; };

        var text = new TextBlock
        {
            Text = WipFileLabel(fc, staged), FontFamily = Mono, FontSize = 12,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = WipFileColor(fc, staged),
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        return new DockPanel
        {
            LastChildFill = true, Margin = new Thickness(10, 5, 8, 5),
            Children = { btn, text },
        };
    }

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

    // The status code + path for a WIP row, reflecting the relevant side: the staged kind in the staged
    // group, else the unstaged kind (with '?' for untracked and '≈' for a BOM/EOL-only change).
    private string WipFileLabel(GitFileChange fc, bool staged)
    {
        char code = !staged && fc.Untracked ? '?'
                  : !staged && _invisiblePaths.Contains(fc.Path) ? '≈'
                  : CodeOf(staged ? fc.Staged : fc.Unstaged);
        return fc.OrigPath is { } o ? $"{code}  {o} → {fc.Path}" : $"{code}  {fc.Path}";
    }

    private IBrush WipFileColor(GitFileChange fc, bool staged)
    {
        if (!staged && fc.Untracked) return _pal.Green;
        if (!staged && _invisiblePaths.Contains(fc.Path)) return _pal.Muted;
        var k = staged ? fc.Staged : fc.Unstaged;
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
