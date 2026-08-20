using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Shapes;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Perch.Avalonia.Rendering;
using Perch.Avalonia.Theming;
using Perch.Data;
using Path = System.IO.Path;   // disambiguate from Avalonia.Controls.Shapes.Path (we use Ellipse from there)

namespace Perch.Avalonia.Windows;

/// <summary>
/// The Markdown viewer/editor. Opened from a session's right-click "Markdown files…" item (or a click on
/// its overlay glyph), it lists the <c>.md</c> files that session produced (rose) or referenced (muted) —
/// the recently-touched files that are the useful default, filtered by a search box. A "Search all project
/// files…" button opens a separate VS Code-style quick-open palette
/// (<see cref="MarkdownProjectSearchWindow"/>) over the <em>whole</em> project's Markdown (a bigger, lazier,
/// <c>.gitignore</c>-aware scan) so files on disk stay findable without cluttering the default view. It edits
/// the selected file in a split view — a source editor on the left, a live rendered preview on the right —
/// with save.
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
    private const double SourceFontSize = 12.5;   // shared by the source editor and its syntax highlighter
    // The rose that marks "produced" files, matching the overlay's Markdown glyph. Fixed across themes.
    private static readonly IBrush ProducedDotBrush = new SolidColorBrush(Color.FromRgb(244, 114, 182));

    // Whether a `code` launcher is on PATH — gates the "Open in VS Code" context item. Resolved once.
    private static readonly Lazy<bool> CodeAvailable = new(() =>
    {
        try
        {
            var pathEnv = Environment.GetEnvironmentVariable("PATH");
            if (string.IsNullOrEmpty(pathEnv))
                return false;
            var exts = OperatingSystem.IsWindows() ? new[] { ".cmd", ".exe", ".bat" } : new[] { "" };
            foreach (var dir in pathEnv.Split(Path.PathSeparator))
            {
                if (string.IsNullOrWhiteSpace(dir))
                    continue;
                foreach (var ext in exts)
                {
                    try { if (File.Exists(Path.Combine(dir, "code" + ext))) return true; } catch { }
                }
            }
            return false;
        }
        catch { return false; }
    });

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

    private readonly Border _filePaneHost;         // left: the search/button/tree stack
    private readonly Control _paneContent;         // search box + project toggle over the tree
    private readonly TextBox _searchBox;
    private readonly Button _projectBtn;           // toggles the project-wide scan on/off
    private readonly Border _treeHost;             // holds either the tree or the empty/loading placeholder
    private readonly TreeView _tree;
    private readonly TextBlock _filesPlaceholder;
    private readonly GridSplitter _bodySplitter;

    private readonly Border _editorHost;           // right: split editor / placeholder
    private readonly Control _editorRoot;
    private readonly Border _editorToolbar;
    private readonly GridSplitter _innerSplitter;
    private readonly Border _sourceCard;           // rounded card framing the source editor
    private readonly Border _previewPane;          // card wrapping the preview scroll so its "paper" bg can retint
    private readonly ScrollViewer _previewScroll;  // holds the rendered MarkdownView document
    // Find-in-page (Ctrl+F). One bar, re-parented over whichever pane is being searched: the rendered preview
    // (_search) or the source editor (_editorFind). Both share the FindHighlighter engine; the bar targets the
    // focused pane on open and a scope button switches between them.
    private readonly MarkdownSearch _search;        // find over the rendered preview
    private readonly EditorFind _editorFind;        // find over the source editor
    private readonly Border _findBar;
    private readonly TextBox _findBox;
    private readonly TextBlock _findCount;
    private readonly Button _findScope, _findPrev, _findNext, _findClose;
    // Replace row (Ctrl+H): a second row under the find row with the replacement field, a Preserve-Case toggle
    // and Replace / Replace-all buttons. Collapsible — hidden in find-only mode. Replace is editor-only.
    private readonly TextBox _replaceBox;
    private readonly Button _replaceBtn, _replaceAllBtn;
    // Option toggles (plain Buttons styled by a bool, matching the HistoryWindow toggle idiom): Match Case and
    // Regex drive matching on both panes; Preserve Case is replace-only. State lives in the bools.
    private readonly Button _matchCaseBtn, _regexBtn, _preserveCaseBtn;
    private bool _matchCase, _regex, _preserveCase;
    private StackPanel _replaceRow = null!;   // the collapsible second row
    private bool _replaceOpen;
    private Grid _previewOverlay = null!;           // preview scroll + (when targeted) the find bar
    private Grid _editorOverlay = null!;            // source box + editor find layer + gutter + (when targeted) the bar
    private bool _findOpen;
    private FindSide _findSide = FindSide.Editor;
    // The pane the user last interacted with (pointer press / typing), so Ctrl+F targets the right one. Focus
    // can't tell them apart — a preview click deliberately focuses the editor (the two-way cursor sync) — so
    // this is tracked by pointer/keyboard activity instead.
    private FindSide _activeSide = FindSide.Editor;
    private readonly TextBlock _editorPlaceholder;
    private readonly TextBlock _editorFileLabel;
    private readonly TextBlock _editorStatus;
    private readonly Button _saveBtn;
    private readonly Button _revertBtn;
    private readonly HighlightTextBox _sourceBox;
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

    // Attention markers: files added (or edited — promoted reference→produced) in the pane *while the window
    // stayed open*, keyed by canonical forward-slashed path (case-insensitive, matching the reader's key). Each
    // shows a small marker in its tree row until the user opens it. Populated only by the live rescan (never the
    // initial scan), so nothing is flagged on first load. _attentionMarkers maps each path to its marker in the
    // *current* tree so opening a file can hide its marker without rebuilding the whole pane.
    private readonly HashSet<string> _attention = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Ellipse> _attentionMarkers = new(StringComparer.OrdinalIgnoreCase);
    // Last-seen on-disk mtime per listed file (canonical key), captured each scan. Lets a rescan flag a file
    // the session *edited again* — an already-listed produced file whose set membership didn't change, so the
    // set diff alone wouldn't catch it. The currently-open file is exempt (the user's already looking at it).
    private Dictionary<string, DateTime> _fileMtimes = new(StringComparer.OrdinalIgnoreCase);

    // The pane shows the session's own Markdown (recently touched, the useful case). The whole project is a
    // bigger, .gitignore-aware scan reached through a separate quick-open palette (the "Search all project
    // files…" button → MarkdownProjectSearchWindow); its result is cached here, per target, once scanned.

    // Open-file / edit state.
    private string? _openFilePath;
    private DateTime? _openFileMtimeUtc;   // the file's mtime when loaded/last saved — for conflict detection
    private string _loadedText = "";       // the on-load buffer, to detect dirtiness
    private bool _dirty;
    private bool _loading;                 // set while programmatically filling the source box (don't mark dirty)
    private bool _externalChange;          // the file changed on disk while the buffer had unsaved edits
    private FileSystemWatcher? _watcher;
    // Live pane refresh: while the window is open we *poll* rather than rely on file-system events. A poll
    // re-scans the session's produced/referenced sets (cheap — mtime-cached on the transcript) and re-stats
    // the listed files, so it catches new files, the session's own edits, AND external/IDE edits — including
    // editors (IntelliJ, VS Code) that save via atomic temp-file rename, which a FileSystemWatcher bound to
    // one filename routinely misses. That miss is why an event-driven approach was flaky here.
    private readonly DispatcherTimer _pollTimer;
    private bool _refreshInFlight;         // one off-thread scan at a time — a slow poll can't pile up
    private FileNode? _currentFileNode;    // the file leaf currently open, for the discard-on-switch prompt
    private bool _suppressSelect;          // reverting a tree selection shouldn't re-trigger the handler
    private bool _closeConfirmed;          // discard already confirmed / programmatic close — skip the prompt

    // ── Cursor sync (source editor ↔ preview) ──
    private IReadOnlyList<MarkdownView.PreviewAnchor> _previewAnchors = [];
    private Border? _activeAnchorBorder;    // the preview block whose left bar marks "where the caret is"
    private int _lastCaretLine = -1;        // gates source→preview sync so it only fires when the line changes
    private bool _suppressCaretSync;        // set while we move the caret programmatically (a preview click)

    // Editor gutter bar: a left-margin accent bar spanning the active block's lines (the editor twin of the
    // preview's left bar), so you can see where the caret jumped without an in-text highlight.
    private Canvas? _gutter;
    private Border? _gutterBar;
    private int _activeStartLine = -1, _activeEndLine = -1;   // active block's source line range
    private TextPresenter? _srcPresenter;   // the editor's text layout host (resolved from the template)
    private ScrollViewer? _srcScroll;       // the editor's inner scroll (resolved from the template)
    private bool _gutterPending;            // coalesces gutter recomputes posted after a layout pass

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
            Padding = new Thickness(18, 10),
            Child = headerPanel,
        };

        // ── Left: search over the file tree ────────────────────────────────────────────────────────
        _searchBox = new TextBox
        {
            [DockPanel.DockProperty] = Dock.Top,
            PlaceholderText = "Search session files…", FontSize = 12,
            BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(5),
            Padding = new Thickness(8, 5), Margin = new Thickness(8, 8, 8, 6),
        };
        _searchBox.TextChanged += (_, _) => RebuildPane();

        // The pane lists the session's own files; the whole project is a click away in a quick-open palette
        // (VS Code-style fuzzy search) so files on disk stay findable without cluttering the default view.
        _projectBtn = SettingsUi.FlatButton("Search all project files…");
        _projectBtn[DockPanel.DockProperty] = Dock.Top;
        _projectBtn.HorizontalAlignment = HorizontalAlignment.Stretch;
        _projectBtn.HorizontalContentAlignment = HorizontalAlignment.Left;
        _projectBtn.Margin = new Thickness(8, 0, 8, 6);
        _projectBtn.Click += (_, _) => OpenProjectSearch();

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
        // Right-click handled at the tree level (not on each node visual) so the whole row — the full-width
        // TreeViewItem, not just its text — is the target.
        _tree.AddHandler(ContextRequestedEvent, OnTreeContextRequested, RoutingStrategies.Bubble);

        _filesPlaceholder = new TextBlock
        {
            Text = "Loading Markdown files…", FontSize = 12,
            Margin = new Thickness(14), TextWrapping = TextWrapping.Wrap,
        };
        // The tree region swaps between the tree and the placeholder; the search box and project toggle above
        // it stay put, so the toggle is reachable even when the session touched no files.
        _treeHost = new Border { Child = _filesPlaceholder };
        _paneContent = new DockPanel { LastChildFill = true, Children = { _searchBox, _projectBtn, _treeHost } };

        // The file pane is a rounded, bordered "card" (like the reference's nav/panels) rather than a
        // hard-divided pane — the gutter to the editor and the card borders do the separating.
        _filePaneHost = new Border
        {
            BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(10), ClipToBounds = true,
            Child = _paneContent,
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
        // Revert discards every unsaved edit back to the last-saved buffer — the escape hatch when the editor's
        // own undo history can't reach far enough back ("whoops"). Enabled only while there are unsaved changes.
        _revertBtn = SettingsUi.FlatButton("Revert");
        _revertBtn.IsEnabled = false;
        _revertBtn.Margin = new Thickness(0, 0, 8, 0);
        _revertBtn.Click += (_, _) => Revert();
        ToolTip.SetTip(_revertBtn, "Discard unsaved changes and restore the last saved version");

        _sourceBox = new HighlightTextBox
        {
            AcceptsReturn = true, AcceptsTab = true, TextWrapping = TextWrapping.Wrap,
            FontFamily = Mono, FontSize = SourceFontSize,
            BorderThickness = new Thickness(0), CornerRadius = new CornerRadius(0),
            Padding = new Thickness(12, 10), VerticalContentAlignment = VerticalAlignment.Top,
            [ScrollViewer.VerticalScrollBarVisibilityProperty] = ScrollBarVisibility.Auto,
        };
        // Editor-side find. The presenter/scroll come from the TextBox template and are resolved lazily by the
        // window; hand the finder accessors that ensure they're resolved before use. Built before the source
        // box's LayoutUpdated wiring below, which repaints its highlight layer.
        _editorFind = new EditorFind(_sourceBox,
            () => { ResolveEditorParts(); return _srcPresenter; },
            () => { ResolveEditorParts(); return _srcScroll; });
        _sourceBox.TextChanged += OnSourceTextChanged;
        // Moving the caret (click, arrows, typing across a line) highlights and scrolls the matching preview
        // block — the source→preview half of the cursor sync.
        _sourceBox.PropertyChanged += (_, e) =>
        {
            if (e.Property == TextBox.CaretIndexProperty && !_loading && !_suppressCaretSync)
                SyncPreviewToCaret(scroll: true);
        };
        // The gutter bar and the editor find highlights track the editor's own scrolling and re-wrapping.
        _sourceBox.LayoutUpdated += (_, _) => { UpdateGutter(); _editorFind.OnEditorMoved(); };
        // A press in the source box marks it the active pane for Ctrl+F (tunnelled + handledEventsToo so the
        // TextBox's own pointer handling doesn't hide it from us).
        _sourceBox.AddHandler(PointerPressedEvent, (_, _) => _activeSide = FindSide.Editor,
            RoutingStrategies.Tunnel, handledEventsToo: true);

        _previewScroll = new ScrollViewer
        {
            // Horizontal scrolling off so the rendered document is constrained to the pane width and wraps
            // (code blocks carry their own inner horizontal scroll).
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        };
        // Clicking a preview block jumps the caret to its source line and focuses the editor — the
        // preview→source half (and the "edit from the preview" gesture). handledEventsToo so a click the
        // selectable text already handled still reaches us.
        _previewScroll.AddHandler(PointerPressedEvent, OnPreviewPointerPressed,
            RoutingStrategies.Bubble, handledEventsToo: true);
        _previewTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(180) };
        _previewTimer.Tick += (_, _) => { _previewTimer.Stop(); RenderPreview(_sourceBox.Text ?? ""); };

        // Live-refresh poll: re-scan sets + re-stat listed files on a cadence while the window is open. Polling
        // (not FileSystemWatcher) is what makes external/IDE edits show up reliably — see the field comment.
        _pollTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1200) };
        _pollTimer.Tick += (_, _) => RefreshPane();

        // Find-in-page (Ctrl+F): one bar over two engines — the rendered preview and the source editor. The
        // bar targets whichever pane holds focus on open; a scope button switches between them. Hidden until
        // opened; it re-runs live as the query changes.
        _search = new MarkdownSearch(_previewScroll);
        _findScope = SettingsUi.FlatButton("Source");
        _findScope.Padding = new Thickness(8, 3);
        _findScope.Click += (_, _) => SwitchFindSide(_findSide == FindSide.Editor ? FindSide.Preview : FindSide.Editor);
        ToolTip.SetTip(_findScope, "Switch between searching the source and the preview");
        _findBox = new TextBox
        {
            PlaceholderText = "Find…", FontSize = 12, Width = 172,
            BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(5),
            Padding = new Thickness(8, 4), VerticalContentAlignment = VerticalAlignment.Center,
        };
        _findBox.TextChanged += (_, _) => { if (_findOpen) ActiveFind.SetSearch(_findBox.Text ?? ""); };
        _findBox.AddHandler(KeyDownEvent, OnFindBoxKeyDown, RoutingStrategies.Tunnel);
        _findCount = new TextBlock
        {
            Text = "", FontSize = 11.5, MinWidth = 42, VerticalAlignment = VerticalAlignment.Center,
            TextAlignment = TextAlignment.Center,
        };
        _findPrev = SettingsUi.FlatButton("‹");
        _findPrev.Padding = new Thickness(8, 3);
        _findPrev.Click += (_, _) => ActiveFind.Prev();
        ToolTip.SetTip(_findPrev, "Previous match (Shift+Enter)");
        _findNext = SettingsUi.FlatButton("›");
        _findNext.Padding = new Thickness(8, 3);
        _findNext.Click += (_, _) => ActiveFind.Next();
        ToolTip.SetTip(_findNext, "Next match (Enter)");
        _findClose = SettingsUi.FlatButton("✕");
        _findClose.Padding = new Thickness(8, 3);
        _findClose.Click += (_, _) => CloseFind();
        ToolTip.SetTip(_findClose, "Close (Esc)");

        // VS Code-style option toggles. Aa = Match Case, .* = Regex (both affect matching on either pane);
        // AB = Preserve Case (replace-only). Monospace glyphs so they read as the familiar icons.
        _matchCaseBtn = MakeOptionToggle("Aa", "Match case (Alt+C)",
            () => { _matchCase = !_matchCase; StyleToggle(_matchCaseBtn, _matchCase); OnFindOptionChanged(); });
        _regexBtn = MakeOptionToggle(".*", "Use regular expression (Alt+R)",
            () => { _regex = !_regex; StyleToggle(_regexBtn, _regex); OnFindOptionChanged(); });
        _preserveCaseBtn = MakeOptionToggle("AB", "Preserve case on replace (Alt+P)",
            () => { _preserveCase = !_preserveCase; StyleToggle(_preserveCaseBtn, _preserveCase); });

        _replaceBox = new TextBox
        {
            PlaceholderText = "Replace…", FontSize = 12, Width = 172,
            BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(5),
            Padding = new Thickness(8, 4), VerticalContentAlignment = VerticalAlignment.Center,
        };
        _replaceBox.AddHandler(KeyDownEvent, OnReplaceBoxKeyDown, RoutingStrategies.Tunnel);
        _replaceBtn = SettingsUi.FlatButton("Replace");
        _replaceBtn.Padding = new Thickness(10, 3);
        _replaceBtn.Click += (_, _) => DoReplaceCurrent();
        ToolTip.SetTip(_replaceBtn, "Replace this match (Enter)");
        _replaceAllBtn = SettingsUi.FlatButton("Replace all");
        _replaceAllBtn.Padding = new Thickness(10, 3);
        _replaceAllBtn.Click += (_, _) => DoReplaceAll();
        ToolTip.SetTip(_replaceAllBtn, "Replace all matches (Ctrl+Alt+Enter)");

        var findRow = new StackPanel
        {
            Orientation = Orientation.Horizontal, Spacing = 6, VerticalAlignment = VerticalAlignment.Center,
            Children = { _findScope, _findBox, _matchCaseBtn, _regexBtn, _findCount, _findPrev, _findNext, _findClose },
        };
        _replaceRow = new StackPanel
        {
            Orientation = Orientation.Horizontal, Spacing = 6, VerticalAlignment = VerticalAlignment.Center,
            IsVisible = false,
            Children = { _replaceBox, _preserveCaseBtn, _replaceBtn, _replaceAllBtn },
        };
        _findBar = new Border
        {
            // Floats above both pane cards (it spans the editor width). A fixed width is deliberate: the two-row
            // widget's Border under-measures when shown after the initial layout, collapsing its background to the
            // fixed-width find box; pinning the width keeps the whole card painted. Sized for the widest row
            // (find-only, which also shows the scope button).
            IsVisible = false, ZIndex = 100, Width = 470,
            HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 10, 16, 0), Padding = new Thickness(8, 6),
            BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(8),
            BoxShadow = new BoxShadows(new BoxShadow
            {
                OffsetX = 0, OffsetY = 2, Blur = 10, Spread = 0, Color = Color.FromArgb(70, 0, 0, 0),
            }),
            Child = new StackPanel
            {
                Orientation = Orientation.Vertical, Spacing = 6, HorizontalAlignment = HorizontalAlignment.Left,
                Children = { findRow, _replaceRow },
            },
        };
        // Only the active engine drives the count label; the inactive one is cleared when the side switches.
        _search.ResultsChanged += (cur, total) => OnFindResults(FindSide.Preview, cur, total);
        _editorFind.ResultsChanged += (cur, total) => OnFindResults(FindSide.Editor, cur, total);

        // The toolbar is a light header row above the two cards (no hard bottom rule); the inner splitter is a
        // wide, transparent gutter so the page shows between the source and preview cards.
        _editorToolbar = new Border { [DockPanel.DockProperty] = Dock.Top, Padding = new Thickness(4, 2, 4, 12) };
        _innerSplitter = new GridSplitter
        {
            Width = 14, ResizeDirection = GridResizeDirection.Columns,
            HorizontalAlignment = HorizontalAlignment.Center, Background = Brushes.Transparent,
        };
        _sourceCard = new Border { BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(10), ClipToBounds = true };
        _previewPane = new Border { BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(10), ClipToBounds = true };
        _editorRoot = BuildEditorRoot();
        _editorHost = new Border { Child = _editorPlaceholder };

        // ── Body: file card | gutter | editor ────────────────────────────────────────────────────────
        // A padded "page" holds the cards, with a wide transparent splitter as the breathing gutter between
        // the nav card and the editor (the page shows through it).
        var body = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("300,Auto,*"),
            Margin = new Thickness(14, 12, 14, 14),
        };
        Grid.SetColumn(_filePaneHost, 0);
        _bodySplitter = new GridSplitter
        {
            Width = 18, ResizeDirection = GridResizeDirection.Columns,
            HorizontalAlignment = HorizontalAlignment.Center, Background = Brushes.Transparent,
        };
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
        DockPanel.SetDock(_revertBtn, Dock.Right);
        DockPanel.SetDock(_editorStatus, Dock.Right);
        toolbarPanel.Children.Add(_saveBtn);           // rightmost
        toolbarPanel.Children.Add(_revertBtn);         // left of Save
        toolbarPanel.Children.Add(_editorStatus);
        toolbarPanel.Children.Add(_editorFileLabel);   // fills the remaining width
        _editorToolbar.Child = toolbarPanel;

        // The preview scroll fills the pane; the find bar (when this pane is the search target) floats over its
        // top-right corner, outside the scroll, so it stays put while the document scrolls.
        _previewOverlay = new Grid { Children = { _previewScroll } };
        _previewPane.Child = _previewOverlay;

        // The source editor with a left gutter overlay: a thin accent bar (in the source box's left padding,
        // clear of the text) marks the active block's lines. Non-interactive, clipped to the editor height.
        _gutterBar = new Border { Width = 3, CornerRadius = new CornerRadius(1.5), IsVisible = false };
        Canvas.SetLeft(_gutterBar, 4);
        _gutter = new Canvas
        {
            Width = 12, HorizontalAlignment = HorizontalAlignment.Left, ClipToBounds = true,
            IsHitTestVisible = false, Children = { _gutterBar },
        };
        // Editor find highlights paint on a layer over the text (below the gutter bar); the find bar floats
        // over this card when the editor is the search target.
        _editorOverlay = new Grid { Children = { _sourceBox, _editorFind.Layer, _gutter } };
        _sourceCard.Child = _editorOverlay;

        var split = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto,*") };
        Grid.SetColumn(_sourceCard, 0);
        Grid.SetColumn(_innerSplitter, 1);
        Grid.SetColumn(_previewPane, 2);
        split.Children.Add(_sourceCard);
        split.Children.Add(_innerSplitter);
        split.Children.Add(_previewPane);

        // The find/replace bar floats over the whole editor area (top-right), not inside either pane card — the
        // two-row widget is wider than a single ~half-width pane, so confining it there clipped its controls. Its
        // *scope* still selects which engine (source/preview) is searched; the bar just doesn't move.
        var editorArea = new Grid { Children = { split, _findBar } };
        return new DockPanel { LastChildFill = true, Children = { _editorToolbar, editorArea } };
    }

    // ── Theming ──────────────────────────────────────────────────────────────────────────────────

    // Recolour every chrome control from the current window theme and rebuild the tree (its node visuals
    // capture brushes at build time, so recolouring means re-running the pane).
    private void ApplyWindowTheme()
    {
        _theme = _windowLight ? MdTheme.Light() : MdTheme.Dark();
        var t = _theme;

        // Drive the Fluent theme variant off our window toggle so every Fluent-templated control (search box,
        // buttons, tree, scrollbars) resolves its state resources — focus/hover/placeholder — from the right
        // polarity.
        RequestedThemeVariant = _windowLight ? ThemeVariant.Light : ThemeVariant.Dark;

        Background = t.WindowBg;                 // the "page" behind the cards
        _header.Background = t.WindowBg;
        _header.BorderBrush = t.Separator;
        _titleText.Foreground = t.Title;
        _subText.Foreground = t.Muted;

        _filePaneHost.Background = t.PaneBg;      // card
        _filePaneHost.BorderBrush = t.Border;
        _filesPlaceholder.Foreground = t.Muted;
        _searchBox.Background = t.EditorBg;
        _searchBox.Foreground = t.Fg;
        _searchBox.BorderBrush = t.Border;
        _searchBox.SelectionBrush = t.Selection;
        _searchBox.SelectionForegroundBrush = t.Fg;

        // The splitters are the transparent gutters — the page shows through them.
        _bodySplitter.Background = Brushes.Transparent;
        _innerSplitter.Background = Brushes.Transparent;

        _editorHost.Background = t.WindowBg;      // page (behind the source/preview cards)
        _editorToolbar.Background = Brushes.Transparent;
        _sourceCard.Background = t.EditorBg;      // card
        _sourceCard.BorderBrush = t.Border;
        _editorFileLabel.Foreground = t.Muted;
        _editorPlaceholder.Foreground = t.Muted;
        // Syntax-highlight the raw Markdown source, coloured from the Aurora editor/syntax tokens for this
        // polarity (the base foreground matches the highlighter's so unstyled text and styled runs agree).
        var syntax = EditorSyntax.For(_windowLight);
        _sourceBox.Background = t.EditorBg;
        _sourceBox.Foreground = syntax.Fg;
        // Our minimal template dropped the Fluent theme's caret brush, so the caret defaulted to black —
        // invisible on the dark editor. Drive it from the editor foreground so it reads on either polarity.
        _sourceBox.CaretBrush = syntax.Fg;
        _sourceBox.SetHighlighter(text => MarkdownSourceHighlighter.Highlight(text, syntax, new Typeface(Mono), SourceFontSize));
        // The default selection highlight reads as a harsh near-black block; use a soft translucent tint
        // and keep the selected text its normal colour.
        _sourceBox.SelectionBrush = t.Selection;
        _sourceBox.SelectionForegroundBrush = t.Fg;
        // (The source box uses its own minimal template, so it needs no Fluent focus/hover background pinning.)
        ApplyFieldBackgrounds(_searchBox, t);

        StyleButton(_windowThemeBtn, t);
        StyleButton(_previewThemeBtn, t);
        StyleButton(_saveBtn, t);
        StyleButton(_revertBtn, t);
        _windowThemeBtn.Content = _windowLight ? "Window: Light" : "Window: Dark";
        _previewThemeBtn.Content = _previewLight ? "Preview: Light" : "Preview: Dark";

        _treeHost.Background = t.PaneBg;
        StyleButton(_projectBtn, t);

        // The find bar reads as a small floating card in the window chrome (not the preview's own theme), so
        // its controls stay legible whatever the preview polarity.
        _findBar.Background = t.PaneBg;
        _findBar.BorderBrush = t.Border;
        _findBox.Background = t.EditorBg;
        _findBox.Foreground = t.Fg;
        _findBox.BorderBrush = t.Border;
        _findBox.SelectionBrush = t.Selection;
        _findBox.SelectionForegroundBrush = t.Fg;
        ApplyFieldBackgrounds(_findBox, t);
        _findCount.Foreground = t.Muted;
        StyleButton(_findScope, t);
        StyleButton(_findPrev, t);
        StyleButton(_findNext, t);
        StyleButton(_findClose, t);
        // Replace row: the field, the Replace / Replace-all buttons, and the three option toggles.
        _replaceBox.Background = t.EditorBg;
        _replaceBox.Foreground = t.Fg;
        _replaceBox.BorderBrush = t.Border;
        _replaceBox.SelectionBrush = t.Selection;
        _replaceBox.SelectionForegroundBrush = t.Fg;
        ApplyFieldBackgrounds(_replaceBox, t);
        StyleButton(_replaceBtn, t);
        StyleButton(_replaceAllBtn, t);
        StyleToggle(_matchCaseBtn, _matchCase);
        StyleToggle(_regexBtn, _regex);
        StyleToggle(_preserveCaseBtn, _preserveCase);
        // The editor's find highlights follow the window (editor) polarity, unlike the preview's own theme.
        _editorFind.SetDark(!_windowLight);

        if (_gutterBar != null) _gutterBar.Background = t.Accent;
        UpdateEditorChrome();
        RebuildPane();
    }

    // The preview pane has its own theme (default light). Retint its "paper" background and re-render.
    private void ApplyPreviewTheme()
    {
        _previewTheme = _previewLight ? MdTheme.Light() : MdTheme.Dark();
        _previewPane.Background = _previewTheme.Paper;
        // The card frame relates to the paper (light paper → light border), since the preview carries its own
        // theme independent of the window chrome.
        _previewPane.BorderBrush = _previewTheme.Border;
        _previewThemeBtn.Content = _previewLight ? "Preview: Light" : "Preview: Dark";
        RenderPreview(_sourceBox.Text ?? "");
    }

    private static void StyleButton(Button b, MdTheme t)
    {
        b.Background = t.ButtonBg;
        b.Foreground = t.Fg;
        b.FontSize = 12;
    }

    // Fluent swaps a TextBox to its own (near-black) background resources on focus/hover. Pin all states to
    // the resting EditorBg so the field never changes colour — only the focus ring signals focus.
    private static void ApplyFieldBackgrounds(TextBox box, MdTheme t)
    {
        box.Resources["TextControlBackgroundFocused"] = t.EditorBg;
        box.Resources["TextControlBackgroundPointerOver"] = t.EditorBg;
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

        // Drop any open file / watchers — a different project is being loaded.
        StopWatcher();
        _pollTimer.Stop();
        _openFilePath = null;
        _currentFileNode = null;
        _dirty = false;
        _externalChange = false;
        _paneSets = null;
        _paneProject = null;   // re-scanned lazily the next time the project-search palette is opened
        _attention.Clear();    // a different session — start with a clean slate of "new since open" badges
        _attentionMarkers.Clear();
        _fileMtimes = new(StringComparer.OrdinalIgnoreCase);
        _editorPlaceholder.Text = "Select a file to view it.";
        _editorHost.Child = _editorPlaceholder;

        int gen = _gen;
        _tree.ItemsSource = null;
        _filesPlaceholder.Text = "Loading Markdown files…";
        _treeHost.Child = _filesPlaceholder;

        // Only the session's own file sets are scanned up front — the whole-project walk waits for the toggle.
        Task.Run(() =>
            {
                var sets = new MarkdownFilesReader().GetFileSets(sid, cwd);
                return (Sets: sets, Mtimes: StatMtimes(sets));
            })
            .ContinueWith(t =>
            {
                if (!t.IsCompletedSuccessfully)
                    return;
                Dispatcher.UIThread.Post(() =>
                {
                    if (!IsVisible || gen != _gen)
                        return;
                    _paneCwd = cwd;
                    _paneSets = t.Result.Sets;
                    _fileMtimes = t.Result.Mtimes;   // baseline — nothing flagged on first load
                    RebuildPane();
                    // Poll from here on so files the session/IDE touch while this window stays open appear live.
                    _pollTimer.Start();
                });
            });
    }

    // Re-scan the session's produced/referenced .md sets off the UI thread and rebuild the pane if they
    // changed, and reload the open file if it changed on disk. Called on the poll cadence while the window is
    // open; guarded by IsVisible + the generation token so a result arriving after a close/retarget is dropped,
    // and by _refreshInFlight so a slow scan can't pile up behind the timer. Cheap in the steady state: the set
    // scan is mtime-cached on the transcript, the stats are a handful of files, and nothing rebuilds unless the
    // set changed or a file was newly flagged.
    private void RefreshPane()
    {
        var cwd = _cwd;
        var sid = _sessionId ?? "";
        if (_refreshInFlight || string.IsNullOrEmpty(cwd) || !IsVisible)
            return;

        _refreshInFlight = true;
        int gen = _gen;
        Task.Run(() =>
            {
                var sets = new MarkdownFilesReader().GetFileSets(sid, cwd);
                return (Sets: sets, Mtimes: StatMtimes(sets));
            })
            .ContinueWith(t =>
            {
                Dispatcher.UIThread.Post(() =>
                {
                    _refreshInFlight = false;
                    if (!t.IsCompletedSuccessfully || !IsVisible || gen != _gen
                        || _paneCwd is not { } paneCwd || paneCwd != cwd)
                        return;

                    var sets = t.Result.Sets;
                    var mtimes = t.Result.Mtimes;

                    ReloadOpenFileIfStale(mtimes);   // the open file changed on disk (IDE/session) — refresh it

                    bool setChanged = !SetsEqual(_paneSets, sets);
                    int flaggedBefore = _attention.Count;

                    if (setChanged)
                        FlagNewOrChanged(_paneSets, sets);   // new files / read→write promotions
                    FlagEdited(mtimes);                      // already-listed files edited since the last poll

                    _fileMtimes = mtimes;                    // advance the baseline either way
                    if (!setChanged && _attention.Count == flaggedBefore)
                        return;   // no rows added and nothing newly flagged — leave the tree untouched

                    _paneSets = sets;
                    if (setChanged)
                        _paneProject = null;   // the file list grew; let the project-search palette rescan next open
                    RebuildPane();
                });
            });
    }

    // Keep the open editor honest about on-disk changes the (flaky, atomic-rename-blind) FileSystemWatcher can
    // miss: if the open file's mtime advanced, reload it when the buffer is clean, or flag the conflict when it
    // isn't — the same policy as OnFileChangedOnDisk, driven by the reliable poll instead of an event.
    private void ReloadOpenFileIfStale(Dictionary<string, DateTime> newMtimes)
    {
        if (_openFilePath is not { } path)
            return;
        if (!newMtimes.TryGetValue(CanonKey(path), out var cur))
            return;
        if (_openFileMtimeUtc is { } loaded && cur <= loaded)
            return;   // no newer than what we have (or our own just-saved write)

        if (_dirty)
        {
            _externalChange = true;
            UpdateEditorChrome();
        }
        else
        {
            LoadFile(path, isReload: true);   // clean buffer — safe to show the newest content
        }
    }

    // Stat each listed file's on-disk mtime (canonical key → UTC last-write), off the UI thread. Unreadable /
    // nonexistent paths (a transcript can carry paths from another host) are simply omitted.
    private static Dictionary<string, DateTime> StatMtimes(MarkdownFileSets sets)
    {
        var map = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in sets.Produced.Concat(sets.Referenced))
        {
            var k = CanonKey(p);
            if (map.ContainsKey(k))
                continue;
            try { map[k] = File.GetLastWriteTimeUtc(p); } catch { /* gone / foreign path — skip */ }
        }
        return map;
    }

    // Flag any already-listed file whose on-disk mtime advanced since the last scan — i.e. the session edited a
    // file the pane already showed (a re-edit doesn't change the set, so FlagNewOrChanged wouldn't catch it).
    // The currently-open file is exempt: the user's already looking at it (and our own Save bumps its mtime).
    private void FlagEdited(Dictionary<string, DateTime> newMtimes)
    {
        var openKey = _openFilePath is { } o ? CanonKey(o) : null;
        foreach (var (k, mtime) in newMtimes)
        {
            if (openKey != null && string.Equals(k, openKey, StringComparison.OrdinalIgnoreCase))
                continue;
            if (_fileMtimes.TryGetValue(k, out var prev) && mtime > prev)
                _attention.Add(k);
        }
    }

    // Sequence-equal over both file lists (ordinal). The reader preserves first-seen order and de-dupes, so a
    // list-level compare is enough to tell "a new file appeared" from "same files as last scan".
    private static bool SetsEqual(MarkdownFileSets? a, MarkdownFileSets? b)
    {
        if (ReferenceEquals(a, b))
            return true;
        if (a is null || b is null)
            return false;
        return a.Produced.SequenceEqual(b.Produced, StringComparer.Ordinal)
            && a.Referenced.SequenceEqual(b.Referenced, StringComparer.Ordinal);
    }

    // Canonical path key, matching MarkdownFilesReader's (forward-slashed; case handled by the set's comparer).
    private static string CanonKey(string path) => path.Replace('\\', '/');

    // Compare the previous scan to the new one and flag every file that just appeared or was edited: a brand-new
    // produced/referenced file, or one promoted from referenced to produced (a produce over a file we'd only
    // seen read). A file already produced that's produced again doesn't change the sets, so it isn't re-flagged
    // — the pane already lists it. Only ever called from the live rescan, so the initial load flags nothing.
    private void FlagNewOrChanged(MarkdownFileSets? oldSets, MarkdownFileSets newSets)
    {
        var oldProduced = new HashSet<string>((oldSets?.Produced ?? []).Select(CanonKey), StringComparer.OrdinalIgnoreCase);
        var oldReferenced = new HashSet<string>((oldSets?.Referenced ?? []).Select(CanonKey), StringComparer.OrdinalIgnoreCase);

        foreach (var p in newSets.Produced)
        {
            var k = CanonKey(p);
            if (!oldProduced.Contains(k))   // new file, or referenced-before now produced (an edit)
                _attention.Add(k);
        }
        foreach (var r in newSets.Referenced)
        {
            var k = CanonKey(r);
            if (!oldReferenced.Contains(k) && !oldProduced.Contains(k))
                _attention.Add(k);
        }
    }

    // Drop a file's attention flag and hide its marker (if the row is realised) — called when the file is opened,
    // so the badge clears the moment the user looks at it. Surgical: no pane rebuild.
    private void ClearAttention(string path)
    {
        var k = CanonKey(path);
        if (!_attention.Remove(k))
            return;
        if (_attentionMarkers.TryGetValue(k, out var marker))
            marker.Fill = Brushes.Transparent;
    }

    // (Re)build the file tree from the cached session scan, applying the current search filter. Cheap — no
    // rescan. Only the session's own files live here; the whole project is reached via the quick-open palette.
    private void RebuildPane()
    {
        if (_paneSets is not { } sets || _paneCwd is not { } cwd)
            return;

        var query = (_searchBox.Text ?? "").Trim();
        var roots = new List<FileNode>();
        _attentionMarkers.Clear();   // markers are rebuilt as the fresh rows realise (via BuildNodeVisual)

        var produced = FilterSession(cwd, sets.Produced, query);
        var referenced = FilterSession(cwd, sets.Referenced, query);
        if (produced.Count > 0)
            roots.Add(SessionGroup($"Produced ({produced.Count})", cwd, produced, NodeKind.ProducedFile));
        if (referenced.Count > 0)
            roots.Add(SessionGroup($"Referenced ({referenced.Count})", cwd, referenced, NodeKind.ReferencedFile));

        if (roots.Count == 0)
        {
            _filesPlaceholder.Text = EmptyPaneMessage(query);
            _treeHost.Child = _filesPlaceholder;
            return;
        }

        _tree.ItemsSource = roots;
        _treeHost.Child = _tree;
        ReselectOpenFile(roots);
    }

    // After a rebuild the tree holds fresh FileNode instances, so any selection is lost. If a file is open
    // and still present, re-select its new leaf (suppressed, so it doesn't re-trigger a load) and re-point
    // _currentFileNode at it — otherwise a live refresh would drop the highlight and confuse the discard guard.
    private void ReselectOpenFile(IReadOnlyList<FileNode> roots)
    {
        if (_openFilePath is not { } open)
            return;
        var match = roots
            .SelectMany(r => r.Children)
            .FirstOrDefault(c => string.Equals(c.FullPath, open, StringComparison.OrdinalIgnoreCase));
        if (match is null)
            return;
        _suppressSelect = true;
        _tree.SelectedItem = match;
        _suppressSelect = false;
        _currentFileNode = match;
    }

    // What to show when no session file matches — always pointing at the project-search button as the way out.
    private static string EmptyPaneMessage(string query) =>
        query.Length > 0
            ? $"No session files match “{query}”. Use “Search all project files” to look across the project."
            : "This session didn’t touch any Markdown files. Use “Search all project files” to find files on disk.";

    // Open the project-wide quick-open palette (VS Code-style fuzzy search over the project's .md). Feeds it
    // the cached file list if we have one, else kicks off a lazy scan and streams the result in while it shows
    // a "Scanning…" line. When the user picks a file, open it (honouring the unsaved-changes guard).
    private async void OpenProjectSearch()
    {
        var cwd = _paneCwd ?? _cwd;
        if (string.IsNullOrEmpty(cwd))
            return;

        var palette = new MarkdownProjectSearchWindow(_theme, cwd);
        if (_paneProject is { } cached)
        {
            palette.SetFiles(cached);
        }
        else
        {
            palette.SetLoading();
            int gen = _gen;
            _ = Task.Run(() => MarkdownProjectScan.Scan(cwd))
                .ContinueWith(t =>
                {
                    if (!t.IsCompletedSuccessfully)
                        return;
                    Dispatcher.UIThread.Post(() =>
                    {
                        if (gen == _gen)
                            _paneProject = t.Result;   // cache for next time (only if still the same target)
                        palette.SetFiles(t.Result);    // feed the palette regardless — it was opened for this cwd
                    });
                });
        }

        var picked = await palette.ShowDialog<string?>(this);
        if (picked is { } path)
            await TryOpenPath(path);
    }

    // Open an arbitrary file path (from the project palette) in the editor, prompting first if the current
    // buffer has unsaved edits. The tree selection is cleared since the file may live outside the session lists.
    private async Task TryOpenPath(string path)
    {
        if (string.Equals(_openFilePath, path, StringComparison.OrdinalIgnoreCase))
            return;   // already open — don't reload over any unsaved edits

        if (_dirty)
        {
            bool discard = await ConfirmDialog.ShowAsync(this, "Discard changes?",
                $"'{Path.GetFileName(_openFilePath ?? "this file")}' has unsaved changes. Discard them?",
                "Discard changes", "Keep editing");
            if (!discard)
                return;
        }

        _suppressSelect = true;
        _tree.SelectedItem = null;
        _suppressSelect = false;
        _currentFileNode = null;
        LoadFile(path);
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
                _                       => _theme.Fg,
            },
            FontWeight = n.Kind == NodeKind.Group ? FontWeight.SemiBold : FontWeight.Normal,
        };

        // File leaves carry a small bullet: rose for produced, muted for referenced.
        if (n.Kind is NodeKind.ProducedFile or NodeKind.ReferencedFile)
        {
            // Leftmost: an attention badge for files added/edited while the window's been open. The slot is
            // reserved (transparent when idle) so flagging/clearing never shifts the row. Amber reads as "new"
            // and stays distinct from the rose/muted type bullet beside it.
            bool attention = n.FullPath is { } fp && _attention.Contains(CanonKey(fp));
            var marker = new Ellipse
            {
                Width = 6, Height = 6, Margin = new Thickness(0, 0, 6, 0),
                VerticalAlignment = VerticalAlignment.Center,
                Fill = attention ? _theme.Warn : Brushes.Transparent,
            };
            if (n.FullPath is { } path)
                _attentionMarkers[CanonKey(path)] = marker;

            var dot = new Ellipse
            {
                Width = 6, Height = 6, Margin = new Thickness(0, 0, 7, 0),
                VerticalAlignment = VerticalAlignment.Center,
                Fill = n.Kind == NodeKind.ProducedFile ? ProducedDotBrush : _theme.Muted,
            };
            var sp = new StackPanel { Orientation = Orientation.Horizontal, Children = { marker, dot, text } };
            ToolTip.SetTip(sp, attention ? $"{n.FullPath}\n(new since you opened this window)" : n.FullPath);
            return sp;   // right-click is handled at the tree level (OnTreeContextRequested)
        }

        return text;
    }

    // Right-click on the tree: resolve the file leaf under the pointer (the whole row is the target, since
    // the event bubbles up from the full-width TreeViewItem) and pop its menu at the pointer.
    private void OnTreeContextRequested(object? sender, ContextRequestedEventArgs e)
    {
        var node = (e.Source as Visual)?.FindAncestorOfType<TreeViewItem>(includeSelf: true)?.DataContext as FileNode;
        if (node?.FullPath is not { } path)
            return;   // a group/folder row (or empty space) — no file menu
        BuildFileMenu(path).ShowAt(_tree, showAtPointer: true);
        e.Handled = true;
    }

    // The right-click menu for a file leaf: copy its relative/absolute path, and (when a `code` launcher
    // is on PATH) open it in VS Code.
    private MenuFlyout BuildFileMenu(string absolutePath)
    {
        var items = new List<Control>
        {
            MenuItem("Copy relative path", () => Clipboard?.SetTextAsync(RelativeLabel(_cwd ?? "", absolutePath))),
            MenuItem("Copy absolute path", () => Clipboard?.SetTextAsync(absolutePath)),
        };
        if (CodeAvailable.Value)
            items.Add(MenuItem("Open in VS Code", () => OpenInVsCode(absolutePath)));
        return new MenuFlyout { ItemsSource = items };
    }

    private static MenuItem MenuItem(string header, Action onClick)
    {
        var item = new MenuItem { Header = header };
        item.Click += (_, _) => onClick();
        return item;
    }

    private static void OpenInVsCode(string path)
    {
        try
        {
            System.Diagnostics.Process.Start(
                new System.Diagnostics.ProcessStartInfo("code", $"\"{path}\"") { UseShellExecute = true });
        }
        catch { /* best-effort — VS Code may have been removed from PATH since we probed */ }
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
    // so a slow read into a since-retargeted/closed window is dropped. <paramref name="isReload"/> is set when
    // refreshing a file that's already open (poll/watcher), so a transient read failure keeps the editor
    // rather than clobbering it — see OpenInEditor.
    private void LoadFile(string path, bool isReload = false)
    {
        int gen = _gen;
        Task.Run(() => TryReadFile(path)).ContinueWith(t =>
        {
            if (!t.IsCompletedSuccessfully)
                return;
            Dispatcher.UIThread.Post(() =>
            {
                if (!IsVisible || gen != _gen)
                    return;
                OpenInEditor(path, t.Result.Text, t.Result.Mtime, isReload);
            });
        });
    }

    // Read a file resiliently through the atomic-save window. Editors like VS Code / IntelliJ save by writing
    // a temp file and renaming it over the target, so a read can briefly hit a sharing violation or a missing
    // file mid-swap. Open with the widest share (ReadWrite | Delete — tolerate a concurrent writer and a
    // rename-in-progress) and retry a few times with a short pause; only a persistent failure returns null.
    private static (string? Text, DateTime? Mtime) TryReadFile(string path)
    {
        for (int attempt = 0; ; attempt++)
        {
            try
            {
                using var fs = new FileStream(path, FileMode.Open, FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete);
                using var reader = new StreamReader(fs);
                var text = reader.ReadToEnd();
                DateTime? mtime = null;
                try { mtime = File.GetLastWriteTimeUtc(path); } catch { }
                return (text, mtime);
            }
            catch when (attempt < 4)
            {
                System.Threading.Thread.Sleep(40);   // ride out the temp-file rename, then retry
            }
            catch
            {
                return (null, null);   // genuinely unreadable (gone, permissions) — give up
            }
        }
    }

    private void OpenInEditor(string path, string? text, DateTime? mtimeUtc, bool isReload = false)
    {
        if (text is null)
        {
            // A transient read failure while *reloading* an open file (e.g. caught mid atomic-save) must not
            // destroy the editor: keep the current buffer and let the next poll retry — we don't advance
            // _openFileMtimeUtc, so ReloadOpenFileIfStale will try again until the file settles. Only an
            // *initial* open failure surfaces the "Couldn't read" placeholder.
            if (isReload)
                return;
            StopWatcher();
            _openFilePath = null;
            _dirty = false;
            _editorPlaceholder.Text = $"Couldn't read {Path.GetFileName(path)}.";
            _editorHost.Child = _editorPlaceholder;
            return;
        }

        StopWatcher();
        _openFilePath = path;
        _openFileMtimeUtc = mtimeUtc;
        _loadedText = text;
        _dirty = false;
        _externalChange = false;
        ClearAttention(path);   // the user's looking at it now — drop its "new" badge

        _loading = true;
        _sourceBox.Text = text;
        _loading = false;
        _lastCaretLine = -1;   // a fresh file: let the next sync re-establish the active block
        _activeStartLine = _activeEndLine = -1;
        if (_gutterBar != null) _gutterBar.IsVisible = false;

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
        _activeSide = FindSide.Editor;   // typing means the editor is the active pane for Ctrl+F
        UpdateEditorChrome();
        _previewTimer.Stop();
        _previewTimer.Start();   // debounce: re-render the preview once typing settles
        if (_findOpen && _findSide == FindSide.Editor)
            _editorFind.Refresh();   // re-find over the edited source (preview refreshes on its own re-render)
    }

    private void RenderPreview(string md)
    {
        var p = _previewTheme;
        var style = new MarkdownStyle(
            Fg: p.Fg, Muted: p.Muted, Title: p.Title, Link: p.Accent,
            CodeFg: p.Code, CodeBg: p.CodeBg, QuoteBar: p.Border, Rule: p.Separator,
            TableBorder: p.Border, TableHeaderBg: p.CodeBg,
            Syntax: _previewLight ? CodeSyntax.Light() : CodeSyntax.Dark());

        var root = MarkdownView.Build(md, style, out var anchors);
        _previewAnchors = anchors;
        _activeAnchorBorder = null;   // the old wrappers are gone; re-attach the highlight below
        // Route the fresh tree through the search engine — it wraps it with the highlight layer, re-collects
        // the searchable blocks, and re-runs any active find (repaint only, no scroll jump).
        _previewScroll.Content = _search.SetContent(root, previewDark: !_previewLight);

        // Re-mark the block under the caret on the fresh tree (no scroll — don't yank the view while typing).
        SyncPreviewToCaret(scroll: false, force: true);
    }

    // ── Cursor sync (source editor ↔ preview) ───────────────────────────────────────────────────────

    // Source→preview: highlight (and optionally scroll to) the block containing the caret. Gated to fire
    // only when the caret's line changes, so typing within a line doesn't churn.
    private void SyncPreviewToCaret(bool scroll, bool force = false)
    {
        if (_previewAnchors.Count == 0)
            return;
        int line = LineOfIndex(_sourceBox.Text ?? "", _sourceBox.CaretIndex);
        if (!force && line == _lastCaretLine)
            return;
        _lastCaretLine = line;
        var anchor = FindAnchorForLine(line);
        SetActiveAnchor(anchor, scroll: false);   // tint + gutter only; scrolling is the aligned kind below
        // Same-point alignment: put the matching preview block at the same viewport height as the caret.
        if (scroll && anchor is { } a)
            AlignPreviewToCaret(a);
    }

    // Preview→source: clicking maps to a precise source position and focuses the editor (ready to edit).
    // It resolves the nearest stamped element (a list item, table row, heading, paragraph or code block) —
    // finer than the enclosing block — and inside a verbatim code block hit-tests to the exact line/column.
    private void OnPreviewPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        _activeSide = FindSide.Preview;   // interacting with the preview makes it the active pane for Ctrl+F
        if (!e.GetCurrentPoint(_previewScroll).Properties.IsLeftButtonPressed)
            return;
        for (var v = e.Source as Visual; v is not null; v = v.GetVisualParent())
        {
            if (v is not Control c)
                continue;
            int line = MarkdownView.GetSourceLine(c);
            if (line < 0)
                continue;

            var text = _sourceBox.Text ?? "";
            int caret = MarkdownView.GetVerbatim(c) && c is TextBlock tb
                ? VerbatimClickOffset(tb, e.GetPosition(tb), text, line)
                : LineStartOffset(text, line);
            // Align the editor so the clicked line lands at the same height the click did in the preview.
            JumpEditorToCaret(caret, e.GetPosition(_previewScroll).Y);
            return;
        }
    }

    // Map a click inside a verbatim (code) block to an exact source offset via text hit-testing: the visual
    // line the point falls on (code doesn't wrap) plus the column within it. Best-effort — any hiccup falls
    // back to the block's first content line.
    private static int VerbatimClickOffset(TextBlock tb, Point pt, string text, int firstContentLine)
    {
        try
        {
            var layout = tb.TextLayout;
            var hit = layout.HitTestPoint(pt);
            int pos = hit.TextPosition + (hit.IsTrailing ? 1 : 0);
            var lines = layout.TextLines;
            int lineIdx = lines.Count - 1;
            for (int i = 0; i < lines.Count; i++)
                if (pos < lines[i].FirstTextSourceIndex + lines[i].Length) { lineIdx = i; break; }
            int col = Math.Max(0, pos - lines[lineIdx].FirstTextSourceIndex);
            return Math.Clamp(LineStartOffset(text, firstContentLine + lineIdx) + col, 0, text.Length);
        }
        catch
        {
            return LineStartOffset(text, firstContentLine);
        }
    }

    private void SetActiveAnchor(MarkdownView.PreviewAnchor? anchor, bool scroll)
    {
        // Preview side: a left accent bar on the active block (no in-text wash).
        if (_activeAnchorBorder is { } prev)
            prev.BorderBrush = Brushes.Transparent;
        _activeAnchorBorder = anchor?.Control as Border;
        if (_activeAnchorBorder is { } cur)
        {
            cur.BorderBrush = _previewTheme.Accent;
            if (scroll)
                cur.BringIntoView();
        }

        // Editor side: mark the same block's line range in the gutter.
        _activeStartLine = anchor?.StartLine ?? -1;
        _activeEndLine = anchor?.EndLine ?? -1;
        ScheduleGutter();
    }

    // The anchor whose source-line range contains the line, else the last block starting at or before it.
    private MarkdownView.PreviewAnchor? FindAnchorForLine(int line)
    {
        MarkdownView.PreviewAnchor? before = null;
        foreach (var a in _previewAnchors)
        {
            if (line >= a.StartLine && line <= a.EndLine)
                return a;
            if (a.StartLine <= line)
                before = a;
        }
        return before ?? (_previewAnchors.Count > 0 ? _previewAnchors[0] : null);
    }

    // Move the caret to an exact source offset and focus the editor (scrolls it into view, ready to type).
    // No in-text highlight — the gutter bar marks the block; the caret marks the exact spot. When
    // <paramref name="alignViewportY"/> is given (a preview click), the editor is scrolled so the caret's line
    // lands at that same viewport height — the preview→editor half of the same-point alignment.
    private void JumpEditorToCaret(int caret, double? alignViewportY = null)
    {
        var text = _sourceBox.Text ?? "";
        caret = Math.Clamp(caret, 0, text.Length);
        int line = LineOfIndex(text, caret);

        _suppressCaretSync = true;                    // we drive the preview highlight explicitly below
        _sourceBox.SelectionStart = caret;
        _sourceBox.SelectionEnd = caret;
        _sourceBox.CaretIndex = caret;
        _sourceBox.Focus();
        _suppressCaretSync = false;

        _lastCaretLine = line;
        SetActiveAnchor(FindAnchorForLine(line), scroll: false);
        // Post so the focus-induced scroll-into-view has run first; we then override it with the aligned offset.
        if (alignViewportY is { } ay)
            Dispatcher.UIThread.Post(() => ScrollEditorToLineAt(caret, ay), DispatcherPriority.Background);
    }

    // ── Same-point alignment: keep the clicked/edited block at the same viewport height in both panes ──

    // The text-layout Y (top, in the editor's content space above its top padding) of the visual line that
    // holds <paramref name="caret"/>. -1 when the editor layout isn't ready.
    private double LineTopY(int caret)
    {
        ResolveEditorParts();
        if (_srcPresenter?.TextLayout is not { } layout)
            return -1;
        var lines = layout.TextLines;
        double y = 0;
        for (int i = 0; i < lines.Count; i++)
        {
            if (caret < lines[i].FirstTextSourceIndex + lines[i].Length || i == lines.Count - 1)
                return y;
            y += lines[i].Height;
        }
        return 0;
    }

    // Scroll the editor so the visual line holding <paramref name="caret"/> sits at viewport height
    // <paramref name="alignY"/> (where the user clicked in the preview). Clamped to the scrollable range.
    private void ScrollEditorToLineAt(int caret, double alignY)
    {
        ResolveEditorParts();
        if (_srcScroll is null)
            return;
        double lineY = LineTopY(caret);
        if (lineY < 0)
            return;
        double target = _sourceBox.Padding.Top + lineY - alignY;
        target = Math.Clamp(target, 0, Math.Max(0, _srcScroll.Extent.Height - _srcScroll.Viewport.Height));
        _srcScroll.Offset = new Vector(_srcScroll.Offset.X, target);
    }

    // Scroll the preview so <paramref name="anchor"/>'s block sits at the same viewport height as the caret in
    // the editor — the source→preview half of the same-point alignment.
    private void AlignPreviewToCaret(MarkdownView.PreviewAnchor anchor)
    {
        ResolveEditorParts();
        double alignY = 0;
        double lineY = LineTopY(_sourceBox.CaretIndex);
        if (_srcScroll is not null && lineY >= 0)
            alignY = _sourceBox.Padding.Top + lineY - _srcScroll.Offset.Y;
        // Post so a just-rebuilt preview tree has laid out before we measure the block's position.
        Dispatcher.UIThread.Post(() => ScrollPreviewToAnchorAt(anchor, alignY), DispatcherPriority.Background);
    }

    // Scroll the preview so <paramref name="anchor"/>'s block top lands at viewport height
    // <paramref name="alignY"/>. Falls back to BringIntoView if the block can't yet be located.
    private void ScrollPreviewToAnchorAt(MarkdownView.PreviewAnchor anchor, double alignY)
    {
        if (anchor.Control.TranslatePoint(new Point(0, 0), _previewScroll) is not { } p)
        {
            anchor.Control.BringIntoView();
            return;
        }
        double target = _previewScroll.Offset.Y + (p.Y - alignY);
        target = Math.Clamp(target, 0, Math.Max(0, _previewScroll.Extent.Height - _previewScroll.Viewport.Height));
        _previewScroll.Offset = new Vector(_previewScroll.Offset.X, target);
    }

    // Recompute the editor gutter bar after the next layout pass (so the text layout is current). Coalesced.
    private void ScheduleGutter()
    {
        if (_gutterPending)
            return;
        _gutterPending = true;
        Dispatcher.UIThread.Post(() => { _gutterPending = false; UpdateGutter(); }, DispatcherPriority.Background);
    }

    // Draw the gutter bar spanning the active block's lines: find the block's start/end source offsets, map
    // them to Y through the editor's own text layout, and place the bar (allowing for the inner scroll and
    // the source box's top padding). Hidden when there's no active block or the layout isn't ready.
    private void UpdateGutter()
    {
        if (_gutter is null || _gutterBar is null)
            return;
        ResolveEditorParts();
        if (_srcPresenter?.TextLayout is not { } layout || _activeStartLine < 0)
        {
            _gutterBar.IsVisible = false;
            return;
        }

        var text = _sourceBox.Text ?? "";
        int startOff = LineStartOffset(text, _activeStartLine);
        int endOff = _activeEndLine == int.MaxValue
            ? text.Length
            : LineStartOffset(text, _activeEndLine + 1);

        double y = 0, top = -1, bottom = -1;
        foreach (var tl in layout.TextLines)
        {
            int s = tl.FirstTextSourceIndex, e = s + tl.Length;
            if (e > startOff && s < endOff)   // this visual line is part of the block
            {
                if (top < 0) top = y;
                bottom = y + tl.Height;
            }
            y += tl.Height;
        }
        if (top < 0)
        {
            _gutterBar.IsVisible = false;
            return;
        }

        double scrollY = _srcScroll?.Offset.Y ?? 0;
        Canvas.SetTop(_gutterBar, _sourceBox.Padding.Top + top - scrollY);
        _gutterBar.Height = Math.Max(2, bottom - top);
        _gutterBar.IsVisible = true;
    }

    // Resolve the editor's inner text presenter + scroll from its template (once), subscribing to scroll so
    // the gutter tracks it.
    private void ResolveEditorParts()
    {
        _srcPresenter ??= _sourceBox.GetVisualDescendants().OfType<TextPresenter>().FirstOrDefault();
        if (_srcScroll is null &&
            _sourceBox.GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault() is { } sv)
        {
            _srcScroll = sv;
            sv.ScrollChanged += (_, _) => { UpdateGutter(); _editorFind.OnEditorMoved(); };
        }
    }

    // 0-based source line containing the character offset.
    private static int LineOfIndex(string text, int index)
    {
        int line = 0, max = Math.Min(index, text.Length);
        for (int i = 0; i < max; i++)
            if (text[i] == '\n') line++;
        return line;
    }

    // Character offset of the start of the 0-based line (clamped to the text length).
    private static int LineStartOffset(string text, int line)
    {
        if (line <= 0)
            return 0;
        int seen = 0;
        for (int i = 0; i < text.Length; i++)
            if (text[i] == '\n' && ++seen == line)
                return i + 1;
        return text.Length;
    }

    private void UpdateEditorChrome()
    {
        _saveBtn.IsEnabled = _dirty;
        _revertBtn.IsEnabled = _dirty;
        (_editorStatus.Text, _editorStatus.Foreground) = _externalChange
            ? ("changed on disk", _theme.Warn)
            : (_dirty ? "● unsaved changes" : "", _theme.Muted);
    }

    // Discard every unsaved edit back to the last-saved buffer (_loadedText) — the "whoops" escape hatch when
    // undo can't reach far enough. Destructive, so it confirms first (matching the switch/close discard prompts).
    private async void Revert()
    {
        if (_openFilePath is not { } path || !_dirty)
            return;

        bool discard = await ConfirmDialog.ShowAsync(this, "Revert changes?",
            $"Discard all unsaved changes to '{Path.GetFileName(path)}' and restore the last saved version?",
            "Revert", "Keep editing");
        if (!discard || _openFilePath != path)
            return;

        _loading = true;                   // programmatic fill — don't mark dirty or kick the preview debounce
        _sourceBox.Text = _loadedText;
        _loading = false;
        _dirty = false;
        _externalChange = false;
        _lastCaretLine = -1;               // re-establish the active block against the restored text
        UpdateEditorChrome();
        RenderPreview(_loadedText);
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
                LoadFile(path, isReload: true);   // clean buffer — safe to show the newest content
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
        _paneProject = project;   // primes the project-search palette's cache (its own render is separate)
        RebuildPane();
        OpenInEditor(samplePath, sampleMarkdown, null);
    }

    /// <summary>Render/verification seam: open the find bar over the given pane with a query and run it. Must
    /// be called after the window is shown and laid out, so the text (preview blocks / editor presenter) is
    /// visible and its layout exists.</summary>
    internal void OpenFindForRender(string query, bool preview)
    {
        _findBox.Text = query;
        _findOpen = true;
        SetFindSide(preview ? FindSide.Preview : FindSide.Editor);
        _findBar.IsVisible = true;
        ActiveFind.SetSearch(query);
    }

    /// <summary>Render/verification seam: open the find+replace bar (editor scope) with a query and replacement
    /// and run it. Options (Match Case / Regex) can be pre-set to capture those states.</summary>
    internal void OpenReplaceForRender(string query, string replacement,
        bool matchCase = false, bool useRegex = false, bool preserveCase = false)
    {
        _matchCase = matchCase; StyleToggle(_matchCaseBtn, matchCase);
        _regex = useRegex; StyleToggle(_regexBtn, useRegex);
        _preserveCase = preserveCase; StyleToggle(_preserveCaseBtn, preserveCase);
        OnFindOptionChanged();
        _findBox.Text = query;
        _replaceBox.Text = replacement;
        _replaceOpen = true;
        ShowReplaceRow(true);
        _findOpen = true;
        SetFindSide(FindSide.Editor);
        _findBar.IsVisible = true;
        ActiveFind.SetSearch(query);
    }

    /// <summary>Render/verification seam: run Replace All over the source with the current bar state and return
    /// the count, so a headless capture can prove the edit path (regex + preserve-case) actually mutates.</summary>
    internal int ReplaceAllForRender() => _editorFind.ReplaceAll(_replaceBox.Text ?? "", _preserveCase);

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
        _pollTimer.Stop();
        _previewTimer.Stop();
        base.OnClosed(e);
    }

    // ── Find in page (Ctrl+F) ───────────────────────────────────────────────────────────────────────

    // The engine for the pane currently being searched.
    private FindHighlighter ActiveFind => _findSide == FindSide.Editor ? _editorFind : _search;

    // Open the bar (find only), targeting whichever pane the user last interacted with, and run any existing query.
    private void OpenFind()
    {
        _replaceOpen = false;
        ShowReplaceRow(false);
        _findOpen = true;
        SetFindSide(_activeSide);
        _findBar.IsVisible = true;
        _findBox.Focus();
        _findBox.SelectAll();
        ActiveFind.SetSearch(_findBox.Text ?? "");
    }

    // Open the bar with the replace row shown (Ctrl+H). Replace is editor-only, so the scope is forced to Source.
    private void OpenReplace()
    {
        _replaceOpen = true;
        ShowReplaceRow(true);
        _findOpen = true;
        SetFindSide(FindSide.Editor);
        _findBar.IsVisible = true;
        _findBox.Focus();
        _findBox.SelectAll();
        ActiveFind.SetSearch(_findBox.Text ?? "");
    }

    private void CloseFind()
    {
        _findOpen = false;
        _replaceOpen = false;
        ShowReplaceRow(false);
        _findBar.IsVisible = false;
        _search.Clear();
        _editorFind.Clear();
        _sourceBox.Focus();
    }

    // Show/hide the replace row. Replacing is editor-only, so the pane-scope button is meaningless while it's
    // open — hide it (and restore it for find-only mode).
    private void ShowReplaceRow(bool show)
    {
        _replaceRow.IsVisible = show;
        _findScope.IsVisible = !show;
    }

    // Switch which pane the bar searches (the scope button), clearing the pane we're leaving and re-running the
    // query on the one we're moving to. Replacing is editor-only, so a switch to the preview is refused then.
    private void SwitchFindSide(FindSide side)
    {
        if (!_findOpen || side == _findSide)
            return;
        if (_replaceOpen && side == FindSide.Preview)
            return;
        SetFindSide(side);
        ActiveFind.SetSearch(_findBox.Text ?? "");
        _findBox.Focus();
    }

    // A small option toggle (Aa / .* / AB) as a plain Button styled by a bool — the HistoryWindow toggle idiom.
    private Button MakeOptionToggle(string glyph, string tip, Action onClick)
    {
        var b = new Button
        {
            Content = glyph, FontFamily = Mono, FontSize = 12,
            Padding = new Thickness(7, 3), MinWidth = 0,
            CornerRadius = new CornerRadius(4), VerticalAlignment = VerticalAlignment.Center,
            HorizontalContentAlignment = HorizontalAlignment.Center,
        };
        ToolTip.SetTip(b, tip);
        b.Click += (_, _) => onClick();
        return b;
    }

    // Colour an option toggle for its on/off state (accent when active, resting chrome otherwise).
    private void StyleToggle(Button b, bool active)
    {
        b.Background = active ? _theme.Accent : _theme.ButtonBg;
        b.Foreground = active ? Brushes.White : _theme.Fg;
        b.BorderBrush = active ? _theme.Accent : _theme.Border;
        b.BorderThickness = new Thickness(1);
    }

    // A Match Case / Regex toggle changed: push the options to both engines (so a scope switch preserves them)
    // and re-run the active query.
    private void OnFindOptionChanged()
    {
        _search.MatchCase = _matchCase; _search.UseRegex = _regex;
        _editorFind.MatchCase = _matchCase; _editorFind.UseRegex = _regex;
        if (_findOpen)
            ActiveFind.SetSearch(_findBox.Text ?? "");
    }

    // Replace the current match / all matches (editor scope only — the buttons are only reachable in replace mode).
    private void DoReplaceCurrent()
    {
        if (!_findOpen || _findSide != FindSide.Editor)
            return;
        _editorFind.ReplaceCurrent(_replaceBox.Text ?? "", _preserveCase);
    }

    private void DoReplaceAll()
    {
        if (!_findOpen || _findSide != FindSide.Editor)
            return;
        int n = _editorFind.ReplaceAll(_replaceBox.Text ?? "", _preserveCase);
        _findCount.Text = n > 0 ? $"Replaced {n}" : "No results";
    }

    // Enter replaces the current match; Ctrl+Alt+Enter replaces all; Escape closes. Tunnelled so the TextBox's
    // own Enter handling (a newline) doesn't swallow it first.
    private void OnReplaceBoxKeyDown(object? sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Enter:
                if (e.KeyModifiers.HasFlag(KeyModifiers.Control) && e.KeyModifiers.HasFlag(KeyModifiers.Alt))
                    DoReplaceAll();
                else
                    DoReplaceCurrent();
                e.Handled = true;
                break;
            case Key.Escape:
                CloseFind();
                e.Handled = true;
                break;
        }
    }

    // Point the bar at a pane: clear the other engine and relabel. The bar itself floats over the whole editor
    // area (see BuildEditorRoot), so switching scope doesn't move it — only which engine runs the query.
    private void SetFindSide(FindSide side)
    {
        (side == FindSide.Editor ? (FindHighlighter)_search : _editorFind).Clear();
        _findSide = side;
        _findScope.Content = side == FindSide.Editor ? "Source" : "Preview";
        _findBox.PlaceholderText = side == FindSide.Editor ? "Find in source…" : "Find in preview…";
    }

    // Only the active engine drives the count label (the inactive one gets cleared when the side switches).
    private void OnFindResults(FindSide side, int cur, int total)
    {
        if (side != _findSide)
            return;
        if (ActiveFind.RegexError)
        {
            _findCount.Text = "Invalid regex";
            _findBox.BorderBrush = _theme.Error;   // tint the field so the bad pattern reads at a glance
            return;
        }
        _findBox.BorderBrush = _theme.Border;
        _findCount.Text = total > 0 ? $"{cur}/{total}"
            : string.IsNullOrEmpty(_findBox.Text) ? "" : "No results";
    }

    // Enter/Shift+Enter step through matches; Escape closes. Tunnelled so the TextBox's own handling doesn't
    // swallow Enter first.
    private void OnFindBoxKeyDown(object? sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Enter:
                if (e.KeyModifiers.HasFlag(KeyModifiers.Shift)) ActiveFind.Prev(); else ActiveFind.Next();
                e.Handled = true;
                break;
            case Key.Escape:
                CloseFind();
                e.Handled = true;
                break;
        }
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.S && e.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            Save();
            e.Handled = true;
            return;
        }
        // Ctrl+F opens find-in-page targeting the last-interacted pane. If it's already open, pressing it after
        // interacting with the *other* pane moves the search there; from replace mode it collapses to find only;
        // otherwise it closes.
        if (e.Key == Key.F && e.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            if (!_findOpen)
                OpenFind();
            else if (_replaceOpen)
            {
                _replaceOpen = false;
                ShowReplaceRow(false);
                _findBox.Focus();
            }
            else if (_activeSide != _findSide)
                SwitchFindSide(_activeSide);
            else
                CloseFind();
            e.Handled = true;
            return;
        }
        // Ctrl+H opens find + replace over the source editor.
        if (e.Key == Key.H && e.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            OpenReplace();
            e.Handled = true;
            return;
        }
        if (e.Key == Key.Escape)
        {
            // Find bar wins Escape (from anywhere) before the window does.
            if (_findOpen)
            {
                CloseFind();
                e.Handled = true;
                return;
            }
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

    private enum NodeKind { Group, ProducedFile, ReferencedFile }

    // Which pane the Ctrl+F find bar is searching.
    private enum FindSide { Editor, Preview }

    private sealed class FileNode
    {
        public required string Label { get; init; }
        public required NodeKind Kind { get; init; }
        public string? FullPath { get; init; }   // absolute path for a file leaf; null for group/folder/info
        public List<FileNode> Children { get; } = new();
    }

    // A minimal per-window palette. Two instances (Dark/Light) drive the window chrome and, independently,
    // the preview pane. Kept local so the toggles retint just this window rather than the app theme. Shared
    // with the project-search palette (MarkdownProjectSearchWindow) so it matches the parent's current theme.
    internal readonly record struct MdTheme(
        IBrush WindowBg, IBrush PaneBg, IBrush EditorBg, IBrush Paper, IBrush Separator, IBrush Border,
        IBrush Fg, IBrush Muted, IBrush Title, IBrush Accent, IBrush Code, IBrush CodeBg, IBrush Warn,
        IBrush Error, IBrush ButtonBg, IBrush Selection)
    {
        private static SolidColorBrush B(byte r, byte g, byte b) => new(Color.FromRgb(r, g, b));
        private static SolidColorBrush A(byte a, byte r, byte g, byte b) => new(Color.FromArgb(a, r, g, b));
        private static SolidColorBrush S(Color c) => new(c);

        // A card-on-page hierarchy (mirrors the theming-template reference): WindowBg is the medium "page"
        // surface, and the reading areas — file pane, editor, preview paper — are the lighter "card" surface
        // (Aurora.Raised) framed by Border. This reads softer than the old near-black chrome. Code panels sit
        // a step *below* the card (darker than paper), like the reference's code blocks. All from AuroraPalette.
        public static MdTheme Dark()
        {
            var a = AuroraPalette.Dark;
            return new(
                WindowBg: S(a.Surface), PaneBg: S(a.Raised), EditorBg: S(a.Raised),
                Paper: S(a.Raised), Separator: S(a.Separator), Border: S(a.Border),
                Fg: S(a.Text), Muted: S(a.Muted), Title: S(a.Title),
                Accent: S(a.Accent), Code: B(0x5E, 0xD6, 0xC5), CodeBg: B(0x14, 0x14, 0x1B),
                Warn: B(0xF5, 0x9E, 0x0B), Error: B(0xF8, 0x51, 0x49), ButtonBg: B(0x2A, 0x2A, 0x36),
                // A soft translucent blue selection instead of the stark default, so selected text stays legible.
                Selection: A(90, 0x60, 0xA5, 0xFA));
        }

        public static MdTheme Light()
        {
            var a = AuroraPalette.Light;
            return new(
                WindowBg: S(a.Sunken), PaneBg: S(a.Raised), EditorBg: S(a.Raised),
                Paper: S(a.Raised), Separator: S(a.Separator), Border: S(a.Border),
                Fg: S(a.Text), Muted: S(a.Muted), Title: S(a.Title),
                Accent: S(a.Accent), Code: B(0x0E, 0x7C, 0x66), CodeBg: B(0xEE, 0xF0, 0xF4),
                Warn: B(0xB4, 0x6A, 0x00), Error: B(0xC0, 0x2B, 0x2B), ButtonBg: B(0xEC, 0xEE, 0xF3),
                Selection: A(60, 0x25, 0x63, 0xEB));
        }
    }
}
