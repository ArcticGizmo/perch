using System.IO;
using System.Linq;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
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
/// The read-only git <b>Change Review</b> window (Milestone 1 of Session Change Review — see
/// <c>docs/git-review-plan.md</c>). Opened from a session's "Review changes…" menu item, it shows what
/// changed in that session's working directory: a header (branch, ahead/behind, PR), a list of
/// working-tree changes and a linear list of recent commits on the left, and a plain owner-drawn diff
/// (<see cref="DiffView"/>) on the right. Selecting a file shows its diff; selecting a commit shows that
/// commit's diff. No staging or committing — that's M2.
///
/// A single reused instance (via <c>WindowHost.ShowOrFocus</c>); <see cref="Retarget"/> re-points it at a
/// different session without reopening. All git work runs off the UI thread and marshals back guarded by
/// <see cref="Visual.IsVisible"/> and a generation token, so a result arriving after the window closed or
/// was re-pointed is dropped (the <c>HistoryWindow</c>/<c>StatsWindow</c> idiom).
/// </summary>
internal sealed class GitReviewWindow : Window
{
    private static readonly Color BodyBg = Color.FromRgb(18, 18, 24);
    private static readonly FontFamily Mono = new("Cascadia Code, Consolas, Menlo, monospace");

    private readonly GitRepoService _git = new();
    private readonly AppSettings _settings;

    private readonly TextBlock _titleText;
    private readonly TextBlock _subText;
    private readonly Button _prChip;
    private readonly Button _unifiedBtn;
    private readonly Button _splitBtn;
    private readonly CheckBox _wrapCheck;
    private readonly ListBox _filesList;
    private readonly ListBox _commitsList;
    private readonly DiffView _diff;
    private readonly ScrollViewer _diffScroll;
    private readonly Border _findBar;
    private readonly TextBox _findBox;
    private readonly TextBlock _findLabel;
    private readonly TextBlock _filesEmpty;
    private readonly TextBlock _commitsEmpty;
    private readonly TextBlock _diffPlaceholder;

    private string? _cwd;
    private string? _prUrl;
    private bool _split;
    private bool _wrap;
    private int _gen;            // bumped on every retarget/refresh; async results check they're still current
    private bool _suppressSelect; // guards the "selecting in one list clears the other" cross-update

    // Auto-refresh: a filesystem watcher on the working tree, debounced so a burst of edits collapses into
    // one reload. Both are torn down when the window closes.
    private FileSystemWatcher? _watcher;
    private DispatcherTimer? _debounce;

    // What the lists and diff currently show, so an auto-refresh can update ONLY what actually changed —
    // and in particular leave the diff (and its scroll position) untouched when nothing changed.
    private GitRepoStatus? _lastStatus;
    private IReadOnlyList<GitCommit> _lastCommits = [];
    private string _shownDiffSig = "";

    public GitReviewWindow(AppSettings settings)
    {
        _settings = settings;
        _split = settings.GitReviewSplitView;
        _wrap = settings.GitReviewWrap;
        Title = "Review changes";
        Width = 1560;
        Height = 1020;
        MinWidth = 720;
        MinHeight = 420;
        Background = new SolidColorBrush(BodyBg);
        WindowStartupLocation = WindowStartupLocation.CenterScreen;

        // ---- header ----
        _titleText = new TextBlock { Foreground = Palette.TitleBrush, FontSize = 15, FontWeight = FontWeight.SemiBold };
        _subText = new TextBlock { Foreground = Palette.MutedBrush, FontSize = 12, Margin = new Thickness(0, 2, 0, 0) };
        _prChip = new Button
        {
            IsVisible = false, VerticalAlignment = VerticalAlignment.Center, Padding = new Thickness(10, 4),
            Background = new SolidColorBrush(Palette.Accent), Foreground = Brushes.White,
            [DockPanel.DockProperty] = Dock.Right,
        };
        _prChip.Click += (_, _) =>
        {
            if (_prUrl is { } url)
                try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true }); } catch { }
        };
        var refreshBtn = new Button
        {
            Content = "Refresh", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0),
            [DockPanel.DockProperty] = Dock.Right,
        };
        refreshBtn.Click += (_, _) => RefreshStatus(preserveSelection: true);

        // Unified / Split toggle (GitHub/GitKraken-style side-by-side), persisted to AppSettings.
        _unifiedBtn = ModeButton("Unified", split: false);
        _splitBtn = ModeButton("Split", split: true);
        var modeGroup = new StackPanel
        {
            Orientation = Orientation.Horizontal, Spacing = 4, VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(8, 0, 0, 0), [DockPanel.DockProperty] = Dock.Right,
            Children = { _unifiedBtn, _splitBtn },
        };

        // Wrap toggle — checked by default; also persisted. Flips the diff body's wrapping and the diff
        // scroller's horizontal scrollbar (wrapped text needs no horizontal scroll).
        _wrapCheck = new CheckBox
        {
            Content = "Wrap", IsChecked = _wrap, VerticalAlignment = VerticalAlignment.Center,
            Foreground = Palette.FgBrush, Margin = new Thickness(8, 0, 0, 0), [DockPanel.DockProperty] = Dock.Right,
        };
        _wrapCheck.IsCheckedChanged += (_, _) => SetWrap(_wrapCheck.IsChecked == true);

        var headerText = new StackPanel { Orientation = Orientation.Vertical, Children = { _titleText, _subText } };
        var header = new Border
        {
            Background = Palette.FormBgBrush, Padding = new Thickness(14, 10),
            [DockPanel.DockProperty] = Dock.Top,
            Child = new DockPanel { LastChildFill = true, Children = { _prChip, refreshBtn, modeGroup, _wrapCheck, headerText } },
        };

        // ---- left column: two lists ----
        _filesList = MakeList(FileTemplate());
        _filesList.SelectionChanged += OnFileSelected;
        _commitsList = MakeList(CommitTemplate());
        _commitsList.SelectionChanged += OnCommitSelected;

        var left = new Grid
        {
            RowDefinitions = new RowDefinitions("Auto,*,Auto,*"),
            Width = 320,
            [Grid.ColumnProperty] = 0,
        };
        _filesEmpty = EmptyHint("No changes", 1);
        _commitsEmpty = EmptyHint("No recent commits", 3);
        left.Children.Add(SectionLabel("Changes", 0));
        left.Children.Add(Place(_filesList, 1));
        left.Children.Add(_filesEmpty);       // overlays the (empty) list cell
        left.Children.Add(SectionLabel("Recent commits", 2));
        left.Children.Add(Place(_commitsList, 3));
        left.Children.Add(_commitsEmpty);

        // ---- right: find bar (hidden until Ctrl+F) over the diff ----
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

        // The diff scroller plus a centered placeholder shown when nothing is selected.
        _diffPlaceholder = new TextBlock
        {
            Text = "Select a change or recent commit",
            Foreground = Palette.MutedBrush, FontSize = 14,
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
            IsHitTestVisible = false,
        };
        var diffArea = new Grid { Children = { _diffScroll, _diffPlaceholder } };
        var diffColumn = new DockPanel { LastChildFill = true, [Grid.ColumnProperty] = 1, Children = { _findBar, diffArea } };

        var body = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*") };
        body.Children.Add(left);
        body.Children.Add(diffColumn);

        Content = new DockPanel { Children = { header, body } };
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
            // Copy a line-range selection if one is active; otherwise let the focused control copy its own
            // (within-line) text selection.
            if (_diff.TryCopyLineSelection()) e.Handled = true;
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
        _filesList.Focus();
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
    /// window (the reused-window refresh path) — it bumps the generation so any in-flight load is ignored.</summary>
    public void Retarget(string cwd, string title, PullRequestInfo? pr)
    {
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

    // Loads status + log off the UI thread, then repopulates the header and both lists. When
    // <paramref name="preserveSelection"/> (a manual Refresh or an auto-refresh tick), the currently selected
    // file/commit is reselected after reload — which re-runs its diff — so the view doesn't jump on the user.
    private void RefreshStatus(bool preserveSelection = false)
    {
        if (_cwd is not { } cwd) return;
        int gen = ++_gen;

        string? keepPath = preserveSelection && _filesList.SelectedItem is GitFileChange sf ? sf.Path : null;
        string? keepHash = preserveSelection && _commitsList.SelectedItem is GitCommit sc ? sc.Hash : null;

        if (!preserveSelection)
        {
            _subText.Text = "Loading…";
            _diff.SetDiff(null, ""); // blank — the centered placeholder shows the "nothing selected" hint
        }

        System.Threading.Tasks.Task.Run(() => (status: _git.GetStatus(cwd), commits: _git.GetLog(cwd, 50)))
            .ContinueWith(t =>
            {
                if (!t.IsCompletedSuccessfully) return;
                Dispatcher.UIThread.Post(() =>
                {
                    if (!IsVisible || gen != _gen) return; // window closed or re-pointed since we started
                    var (status, commits) = t.Result;
                    ApplyStatus(status, cwd);
                    var changes = status?.Changes ?? [];

                    _suppressSelect = true;
                    _filesList.ItemsSource = changes;
                    _commitsList.ItemsSource = commits;
                    _filesList.SelectedIndex = -1;
                    _commitsList.SelectedIndex = -1;
                    _suppressSelect = false;
                    _lastStatus = status;
                    _lastCommits = commits;

                    // Restore the prior selection (fires the selection handler, which reloads the diff).
                    if (keepPath is not null)
                        _filesList.SelectedIndex = IndexOfPath(changes, keepPath);
                    else if (keepHash is not null)
                        _commitsList.SelectedIndex = IndexOfHash(commits, keepHash);

                    UpdateEmptyStates();
                    UpdateDiffPlaceholder();
                });
            });
    }

    // (Re)creates the filesystem watcher for the current working tree. A burst of edits (a session saving
    // several files, a commit touching .git) is debounced into a single preserve-selection refresh.
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
        catch { /* best-effort: no auto-refresh if the watcher can't attach */ }
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

    private void ApplyStatus(GitRepoStatus? status, string cwd)
    {
        if (status is not { } s)
        {
            _subText.Text = cwd; // not a repo / git unavailable — still show where we looked
            return;
        }
        string branch = s.Branch ?? "(detached)";
        string ab = (s.Ahead, s.Behind) switch
        {
            (0, 0) => "",
            var (a, b) => $"  ·  ↑{a} ↓{b}",
        };
        string dirty = s.IsClean ? "clean" : $"{s.Changes.Count} change{(s.Changes.Count == 1 ? "" : "s")}";
        _subText.Text = $"{branch}{ab}  ·  {dirty}  ·  {cwd}";
    }

    private void OnFileSelected(object? sender, SelectionChangedEventArgs e)
    {
        if (_suppressSelect || _filesList.SelectedItem is not GitFileChange fc || _cwd is not { } cwd) return;
        _suppressSelect = true; _commitsList.SelectedItem = null; _suppressSelect = false;
        _diffPlaceholder.IsVisible = false;

        int gen = ++_gen;
        _diff.SetLoading();
        _shownDiffSig = ""; // a real diff always differs from this, so the pending load will render
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

    private void OnCommitSelected(object? sender, SelectionChangedEventArgs e)
    {
        if (_suppressSelect || _commitsList.SelectedItem is not GitCommit c || _cwd is not { } cwd) return;
        _suppressSelect = true; _filesList.SelectedItem = null; _suppressSelect = false;
        _diffPlaceholder.IsVisible = false;

        int gen = ++_gen;
        _diff.SetLoading();
        _shownDiffSig = ""; // a real diff always differs from this, so the pending load will render
        System.Threading.Tasks.Task.Run(() => _git.GetCommitDiff(cwd, c.Hash)).ContinueWith(t =>
        {
            if (!t.IsCompletedSuccessfully) return;
            Dispatcher.UIThread.Post(() =>
            {
                if (!IsVisible || gen != _gen) return;
                ShowSections(CommitSections(t.Result), DiffNote(t.Result, $"No diff for {c.ShortHash}."));
            });
        });
    }

    // Shows diff sections and records a signature of what's now displayed, so an auto-refresh can tell
    // whether the diff actually changed before re-rendering it (which would otherwise reset the scroll).
    private void ShowSections(IReadOnlyList<DiffSection> sections, string? note)
    {
        _diff.SetSections(sections, note);
        _shownDiffSig = DiffSig(sections);
    }

    private static IReadOnlyList<DiffSection> CommitSections(GitDiff? diff) =>
        diff is { Files.Count: > 0 } d ? [new DiffSection(null, d)] : [];

    // A debounced filesystem event fired. Re-read status/log (and the selected file's diff) off the UI
    // thread, then update ONLY the parts that actually changed: an unchanged diff is left exactly as it is,
    // so the view doesn't jump when a watcher event didn't correspond to a real change. Commit diffs are
    // immutable, so a selected commit's diff is never re-fetched.
    private void AutoRefresh()
    {
        if (_cwd is not { } cwd) return;
        int gen = ++_gen;
        string? selPath = _filesList.SelectedItem is GitFileChange f ? f.Path : null;

        System.Threading.Tasks.Task.Run(() =>
        {
            var status = _git.GetStatus(cwd);
            var commits = _git.GetLog(cwd, 50);
            (IReadOnlyList<DiffSection> sections, string? note)? sel = null;
            if (selPath is not null)
                sel = FindByPath(status, selPath) is { } fc
                    ? LoadFileDiff(cwd, fc)
                    : ((IReadOnlyList<DiffSection>)[], $"No diff for {selPath}.");
            return (status, commits, sel);
        }).ContinueWith(t =>
        {
            if (!t.IsCompletedSuccessfully) return;
            Dispatcher.UIThread.Post(() =>
            {
                if (!IsVisible || gen != _gen) return;
                var (status, commits, sel) = t.Result;

                if (!CommitsEqual(commits, _lastCommits))
                {
                    string? keepHash = _commitsList.SelectedItem is GitCommit c ? c.Hash : null;
                    _suppressSelect = true;
                    _commitsList.ItemsSource = commits;
                    _commitsList.SelectedIndex = IndexOfHash(commits, keepHash);
                    _suppressSelect = false;
                    _lastCommits = commits;
                }

                if (!StatusEqual(status, _lastStatus))
                {
                    ApplyStatus(status, cwd);
                    var changes = status?.Changes ?? [];
                    string? keepPath = _filesList.SelectedItem is GitFileChange f ? f.Path : null;
                    _suppressSelect = true;
                    _filesList.ItemsSource = changes;
                    _filesList.SelectedIndex = IndexOfPath(changes, keepPath);
                    _suppressSelect = false;
                    _lastStatus = status;
                }

                // Only touch the diff when its content actually changed (a selected working file only —
                // commit diffs can't change). This is what keeps the scroll position stable.
                if (sel is { } s && DiffSig(s.sections) != _shownDiffSig)
                    ShowSections(s.sections, s.note);

                UpdateEmptyStates();
                UpdateDiffPlaceholder();
            });
        });
    }

    // Picks the diff(s) for a working-tree change: untracked files show their full contents; a file with
    // BOTH staged and unstaged edits shows two labelled sections; otherwise the single relevant diff.
    private (IReadOnlyList<DiffSection> sections, string? note) LoadFileDiff(string cwd, GitFileChange fc)
    {
        if (fc.Untracked)
            return Single(_git.GetUntrackedDiff(cwd, fc.Path), fc.Path);

        bool hasStaged = fc.Staged != GitChangeKind.None;
        bool hasUnstaged = fc.Unstaged != GitChangeKind.None;

        if (hasStaged && hasUnstaged)
        {
            var list = new List<DiffSection>();
            if (_git.GetWorkingDiff(cwd, fc.Path, staged: true) is { Files.Count: > 0 } staged)
                list.Add(new DiffSection("Staged", staged));
            if (_git.GetWorkingDiff(cwd, fc.Path, staged: false) is { Files.Count: > 0 } unstaged)
                list.Add(new DiffSection("Unstaged", unstaged));
            return (list, list.Count == 0 ? $"No diff for {fc.Path}." : null);
        }

        return Single(hasUnstaged ? _git.GetWorkingDiff(cwd, fc.Path, staged: false)
                                  : _git.GetWorkingDiff(cwd, fc.Path, staged: true), fc.Path);

        static (IReadOnlyList<DiffSection>, string?) Single(GitDiff? diff, string path) =>
            diff is { Files.Count: > 0 } d
                ? (new[] { new DiffSection(null, d) }, null)
                : ([], $"No diff for {path}.");
    }

    private static string? DiffNote(GitDiff? diff, string emptyNote) =>
        diff is { Files.Count: > 0 } ? null : emptyNote;

    // ---- change detection for auto-refresh ----

    // Value-compares two statuses (record-struct fields + a sequence-compare of the changed-file list,
    // whose elements are record structs with value equality).
    private static bool StatusEqual(GitRepoStatus? a, GitRepoStatus? b)
    {
        if (a is null) return b is null;
        if (b is null) return false;
        var x = a.Value;
        var y = b.Value;
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

    private static int IndexOfPath(IReadOnlyList<GitFileChange> list, string? path)
    {
        if (path is not null)
            for (int i = 0; i < list.Count; i++)
                if (list[i].Path == path) return i;
        return -1;
    }

    private static int IndexOfHash(IReadOnlyList<GitCommit> list, string? hash)
    {
        if (hash is not null)
            for (int i = 0; i < list.Count; i++)
                if (list[i].Hash == hash) return i;
        return -1;
    }

    // A compact, order-preserving signature of the rendered diff — labels, per-file paths/binary flag, and
    // every hunk header + typed line. Cheap to compute and compare; two diffs with the same signature render
    // identically, so an auto-refresh can skip re-rendering (and leave the scroll position alone).
    private static string DiffSig(IReadOnlyList<DiffSection> sections)
    {
        var sb = new StringBuilder();
        foreach (var s in sections)
        {
            sb.Append(s.Label).Append('\u241e');
            foreach (var f in s.Diff.Files)
            {
                sb.Append(f.OldPath).Append('>').Append(f.NewPath).Append(f.IsBinary ? '#' : '.').Append('\u241e');
                foreach (var h in f.Hunks)
                {
                    sb.Append(h.Header).Append('\u241e');
                    foreach (var l in h.Lines)
                        sb.Append((char)('0' + (int)l.Kind)).Append(l.Text).Append('\n');
                }
            }
        }
        return sb.ToString();
    }

    // ---- diff layout mode (unified / split) ----

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
            b.Foreground = active ? Brushes.White : Palette.FgBrush;
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

    private static Control Place(Control c, int row)
    {
        c.SetValue(Grid.RowProperty, row);
        return c;
    }

    private static Control SectionLabel(string text, int row)
    {
        var tb = new TextBlock
        {
            Text = text, Foreground = Palette.MutedBrush, FontSize = 11, FontWeight = FontWeight.SemiBold,
            Margin = new Thickness(12, 10, 12, 4),
        };
        tb.SetValue(Grid.RowProperty, row);
        return tb;
    }

    // A dim "empty list" hint overlaid in a list's grid row; shown only while that list has no items.
    private static TextBlock EmptyHint(string text, int row)
    {
        var tb = new TextBlock
        {
            Text = text, Foreground = Palette.MutedBrush, FontSize = 12, FontStyle = FontStyle.Italic,
            Margin = new Thickness(12, 6, 12, 0), IsVisible = false, IsHitTestVisible = false,
        };
        tb.SetValue(Grid.RowProperty, row);
        return tb;
    }

    // Shows each list's empty hint only when that list has no items.
    private void UpdateEmptyStates()
    {
        _filesEmpty.IsVisible = _filesList.ItemCount == 0;
        _commitsEmpty.IsVisible = _commitsList.ItemCount == 0;
    }

    // Shows the centered "nothing selected" hint in the diff area when neither list has a selection.
    private void UpdateDiffPlaceholder() =>
        _diffPlaceholder.IsVisible = _filesList.SelectedItem is null && _commitsList.SelectedItem is null;

    // ListBox item templates. Both guard a null item — Avalonia invokes the template with null on a
    // measure pass and an unguarded dereference inside a layout pass crashes the process (see HistoryWindow).
    private static FuncDataTemplate<GitFileChange> FileTemplate() => new((fc, _) => new TextBlock
    {
        Text = FileLabel(fc),
        FontFamily = Mono, FontSize = 12,
        Foreground = new SolidColorBrush(FileColor(fc)),
        TextTrimming = TextTrimming.CharacterEllipsis,
    }, supportsRecycling: true);

    private static FuncDataTemplate<GitCommit> CommitTemplate() => new((c, _) =>
    {
        var tb = new TextBlock
        {
            Text = c.Hash is null ? "" : $"{c.ShortHash} - {c.Subject}",
            FontFamily = Mono, FontSize = 12, Foreground = Palette.FgBrush,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        if (c.Hash is not null)
        {
            // Hover shows the full commit message after a short dwell.
            ToolTip.SetTip(tb, c.Body);
            ToolTip.SetShowDelay(tb, 750);
        }
        return tb;
    }, supportsRecycling: true);

    private static string FileLabel(GitFileChange fc)
    {
        if (fc.Path is null) return "";
        char code = fc.Untracked ? '?' : CodeOf(fc.Unstaged != GitChangeKind.None ? fc.Unstaged : fc.Staged);
        return fc.OrigPath is { } o ? $"{code}  {o} → {fc.Path}" : $"{code}  {fc.Path}";
    }

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

    // Green for additions (incl. untracked new files), red for deletions, orange for everything else that
    // is a modification (modify / rename / copy / type-change / conflict).
    private static Color FileColor(GitFileChange fc)
    {
        if (fc.Untracked) return Palette.Green;
        var k = fc.Unstaged != GitChangeKind.None ? fc.Unstaged : fc.Staged;
        return k switch
        {
            GitChangeKind.Added => Palette.Green,
            GitChangeKind.Deleted => Palette.Red,
            _ => Palette.Orange,
        };
    }
}
