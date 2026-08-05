using System.IO;
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
        left.Children.Add(SectionLabel("Changes", 0));
        left.Children.Add(Place(_filesList, 1));
        left.Children.Add(SectionLabel("Recent commits", 2));
        left.Children.Add(Place(_commitsList, 3));

        // ---- right: diff ----
        _diff = new DiffView();
        _diff.SetSplit(_split);
        _diff.SetWrap(_wrap);
        UpdateModeButtons();
        _diffScroll = new ScrollViewer
        {
            Content = _diff,
            HorizontalScrollBarVisibility = _wrap ? ScrollBarVisibility.Disabled : ScrollBarVisibility.Auto,
            [Grid.ColumnProperty] = 1,
        };

        var body = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*") };
        body.Children.Add(left);
        body.Children.Add(_diffScroll);

        Content = new DockPanel { Children = { header, body } };
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Key == Key.Escape) Close();
    }

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
            _diff.SetDiff(null, "Select a file or commit to see its diff.");
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

                    // Restore the prior selection (fires the selection handler, which reloads the diff).
                    if (keepPath is not null)
                    {
                        for (int i = 0; i < changes.Count; i++)
                            if (changes[i].Path == keepPath) { _filesList.SelectedIndex = i; break; }
                    }
                    else if (keepHash is not null)
                    {
                        for (int i = 0; i < commits.Count; i++)
                            if (commits[i].Hash == keepHash) { _commitsList.SelectedIndex = i; break; }
                    }
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
            if (IsVisible) RefreshStatus(preserveSelection: true);
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

        int gen = ++_gen;
        _diff.SetLoading();
        System.Threading.Tasks.Task.Run(() => LoadFileDiff(cwd, fc)).ContinueWith(t =>
        {
            if (!t.IsCompletedSuccessfully) return;
            Dispatcher.UIThread.Post(() =>
            {
                if (!IsVisible || gen != _gen) return;
                _diff.SetSections(t.Result.sections, t.Result.note);
            });
        });
    }

    private void OnCommitSelected(object? sender, SelectionChangedEventArgs e)
    {
        if (_suppressSelect || _commitsList.SelectedItem is not GitCommit c || _cwd is not { } cwd) return;
        _suppressSelect = true; _filesList.SelectedItem = null; _suppressSelect = false;

        int gen = ++_gen;
        _diff.SetLoading();
        System.Threading.Tasks.Task.Run(() => _git.GetCommitDiff(cwd, c.Hash)).ContinueWith(t =>
        {
            if (!t.IsCompletedSuccessfully) return;
            Dispatcher.UIThread.Post(() =>
            {
                if (!IsVisible || gen != _gen) return;
                _diff.SetDiff(t.Result, DiffNote(t.Result, $"No diff for {c.ShortHash}."));
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
