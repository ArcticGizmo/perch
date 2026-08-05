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

    private readonly TextBlock _titleText;
    private readonly TextBlock _subText;
    private readonly Button _prChip;
    private readonly ListBox _filesList;
    private readonly ListBox _commitsList;
    private readonly DiffView _diff;

    private string? _cwd;
    private string? _prUrl;
    private int _gen;            // bumped on every retarget/refresh; async results check they're still current
    private bool _suppressSelect; // guards the "selecting in one list clears the other" cross-update

    public GitReviewWindow()
    {
        Title = "Review changes";
        Width = 1040;
        Height = 680;
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
        refreshBtn.Click += (_, _) => RefreshStatus();

        var headerText = new StackPanel { Orientation = Orientation.Vertical, Children = { _titleText, _subText } };
        var header = new Border
        {
            Background = Palette.FormBgBrush, Padding = new Thickness(14, 10),
            [DockPanel.DockProperty] = Dock.Top,
            Child = new DockPanel { LastChildFill = true, Children = { _prChip, refreshBtn, headerText } },
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
        var diffScroll = new ScrollViewer
        {
            Content = _diff,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            [Grid.ColumnProperty] = 1,
        };

        var body = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*") };
        body.Children.Add(left);
        body.Children.Add(diffScroll);

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
        RefreshStatus();
    }

    // Loads status + log off the UI thread, then repopulates the header and both lists.
    private void RefreshStatus()
    {
        if (_cwd is not { } cwd) return;
        int gen = ++_gen;
        _subText.Text = "Loading…";
        _diff.SetDiff(null, "Select a file or commit to see its diff.");

        System.Threading.Tasks.Task.Run(() => (status: _git.GetStatus(cwd), commits: _git.GetLog(cwd, 50)))
            .ContinueWith(t =>
            {
                if (!t.IsCompletedSuccessfully) return;
                Dispatcher.UIThread.Post(() =>
                {
                    if (!IsVisible || gen != _gen) return; // window closed or re-pointed since we started
                    var (status, commits) = t.Result;
                    ApplyStatus(status, cwd);
                    _suppressSelect = true;
                    _filesList.ItemsSource = status?.Changes ?? [];
                    _commitsList.ItemsSource = commits;
                    _filesList.SelectedItem = null;
                    _commitsList.SelectedItem = null;
                    _suppressSelect = false;
                });
            });
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
                _diff.SetDiff(t.Result.diff, t.Result.note);
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

    // Picks the right diff for a working-tree change: untracked files show their full contents; a file with
    // unstaged edits shows the worktree diff; otherwise the staged diff.
    private (GitDiff? diff, string? note) LoadFileDiff(string cwd, GitFileChange fc)
    {
        GitDiff? diff = fc.Untracked
            ? _git.GetUntrackedDiff(cwd, fc.Path)
            : fc.Unstaged != GitChangeKind.None
                ? _git.GetWorkingDiff(cwd, fc.Path, staged: false)
                : _git.GetWorkingDiff(cwd, fc.Path, staged: true);
        return (diff, DiffNote(diff, $"No diff for {fc.Path}."));
    }

    private static string? DiffNote(GitDiff? diff, string emptyNote) =>
        diff is { Files.Count: > 0 } ? null : emptyNote;

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

    private static FuncDataTemplate<GitCommit> CommitTemplate() => new((c, _) => new TextBlock
    {
        Text = c.Hash is null ? "" : $"{c.ShortHash}  {c.Subject}",
        FontSize = 12, Foreground = Palette.FgBrush,
        TextTrimming = TextTrimming.CharacterEllipsis,
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

    private static Color FileColor(GitFileChange fc)
    {
        if (fc.Untracked) return Palette.Muted;
        var k = fc.Unstaged != GitChangeKind.None ? fc.Unstaged : fc.Staged;
        return k switch
        {
            GitChangeKind.Added => Palette.Green,
            GitChangeKind.Deleted => Palette.Red,
            GitChangeKind.Renamed or GitChangeKind.Copied => Palette.Accent,
            GitChangeKind.Unmerged => Palette.Orange,
            _ => Palette.Fg,
        };
    }
}
