using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Perch.Avalonia.Services;
using Perch.Avalonia.Theming;
using Perch.Data;

namespace Perch.Avalonia.Windows;

/// <summary>What the dialog hands the App to start: folder, the composed prompt, permission mode, account and the
/// session's name.</summary>
internal sealed record PrSessionLaunch(string Cwd, string Prompt, string PermissionMode, string? ConfigDir, string Title);

/// <summary>
/// "Start a session" on a GitHub dashboard PR (S4 in docs/github-dashboard-plan.md). Three questions, top to
/// bottom: <b>what</b> should Claude do (a quick prompt, the ones fitting the PR's reasons first, whose short task
/// text is editable — Perch wraps it with the PR link and the rules, previewable), <b>where</b> (the checkout found
/// for the repo, in a new per-PR worktree by default or the checkout as it is) and, on machines with a choice, which
/// <b>account</b>. Start opens an ordinary Perch-controlled session window, named after the PR, with the prompt sent.
///
/// <para>The checkout is found by <see cref="RepoCheckoutResolver"/> over the folders Claude sessions have run in (read
/// off the UI thread), or the one remembered for the repo. Folder trust is asked the way a new session asks it, for
/// the worktree itself: its code is the PR's, which may come from someone else. The account follows the same rules
/// and guardrails as the session launcher, resolved again for the final folder.</para>
/// </summary>
internal sealed class PrSessionWindow : Window
{
    private static readonly IBrush Bg     = Palette.OverlaySurfaceBrush;
    private static readonly IBrush Stroke = Palette.BorderBrush;
    private static readonly IBrush Fg     = Palette.FgBrush;
    private static readonly IBrush Muted  = Palette.MutedBrush;
    private static readonly IBrush Accent = Palette.AccentBrush;

    private const double LabelColumn = 76;

    private readonly GhPrItem _item;
    private readonly GitRepoRef _repo;
    private readonly Func<IReadOnlyList<string>> _knownFolders;
    private readonly string? _remembered;
    private readonly Action<string, string> _remember;
    private readonly Func<IReadOnlyList<AccountRule>?> _rules;
    private readonly Func<PrSessionLaunch, string?> _launch;

    // What
    private readonly IReadOnlyList<PrPromptTemplate> _templates;
    private readonly Dictionary<string, string> _taskEdits = new();     // per template, so switching keeps edits
    private readonly List<RadioButton> _templateRows = new();           // index-aligned with _templates
    private PrPromptTemplate _template;
    private readonly TextBox _task;
    private readonly Button _previewToggle;
    private readonly Border _previewBox;
    private readonly SelectableTextBlock _preview;

    // Where
    private readonly TextBlock _folderName, _folderDir;
    private readonly DockPanel _folderPath;
    private readonly ComboBox _folderChoices;
    private readonly Button _changeFolder;
    private readonly RadioButton _inExisting, _inWorktree, _inCheckout;
    private readonly TextBlock _existingTitle, _existingDesc, _worktreeDesc, _checkoutDesc;
    private PrWorktreeContext? _context;          // the chosen checkout's worktrees + layout, read off the UI thread
    private string? _existingPath;                // a worktree already on the PR's head branch, when there is one
    private readonly Grid _accountRow;
    private readonly ComboBox _accountBox;

    private readonly TextBlock _status;
    private readonly Button _start;

    private string? _folder;
    private IReadOnlyList<AccountChoice> _accountOptions = [];
    private bool _busy, _renderOnly;

    public PrSessionWindow(GhPrItem item, Func<IReadOnlyList<string>> knownFolders, string? remembered,
        Action<string, string> remember, Func<IReadOnlyList<AccountRule>?> rules, Func<PrSessionLaunch, string?> launch)
    {
        _item = item;
        _repo = RepoOf(item.Pr);
        _knownFolders = knownFolders;
        _remembered = remembered;
        _remember = remember;
        _rules = rules;
        _launch = launch;
        _templates = PrSessionPrompts.ForReasons(item.Reasons.Select(r => r.Kind));
        _template = _templates[0];

        Title = $"Start a session on PR #{item.Pr.Number}";
        WindowDecorations = WindowDecorations.None;
        Background = Brushes.Transparent;
        TransparencyLevelHint = [WindowTransparencyLevel.Transparent];
        CanResize = false;
        Width = 600;
        SizeToContent = SizeToContent.Height;
        ShowInTaskbar = true;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        // ── Header: the PR, as the dashboard row shows it ──
        var heading = new TextBlock { Text = "Start a session", Foreground = Fg, FontWeight = FontWeight.Bold, FontSize = 16 };
        var close = new Button
        {
            Content = "✕", Foreground = Muted, Background = Brushes.Transparent, BorderThickness = new Thickness(0),
            Padding = new Thickness(4, 0), FontSize = 14, Cursor = new Cursor(StandardCursorType.Hand), VerticalAlignment = VerticalAlignment.Top,
        };
        close.Click += (_, _) => Close();
        var headRow = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        Grid.SetColumn(close, 1);
        headRow.Children.Add(heading);
        headRow.Children.Add(close);

        var prTitle = new TextBlock
        {
            Text = item.Pr.Title.Length > 0 ? item.Pr.Title : "(untitled)", Foreground = Fg, FontSize = 13.5,
            FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap, MaxLines = 2,
            TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 10, 0, 0),
        };
        var prMeta = new TextBlock
        {
            Text = $"{item.Pr.Repo}#{item.Pr.Number}", Foreground = Muted, FontSize = 12,
            TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 2, 0, 0),
        };
        var pills = new WrapPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 6, 0, 0) };
        foreach (var r in item.Reasons) pills.Children.Add(ReasonPill(r));
        pills.IsVisible = item.Reasons.Count > 0;
        var header = new StackPanel { Margin = new Thickness(22, 18, 18, 16), Children = { headRow, prTitle, prMeta, pills } };

        // ── What should Claude do? ──
        // Editing is the norm, so only the exception carries a badge.
        var what = new StackPanel { Spacing = 2 };
        foreach (var t in _templates)
        {
            var rb = OptionRow("task", t.Label, t.Description, t.Mode == PrSessionMode.Plan ? "Read-only" : null);
            rb.IsChecked = t == _template;
            rb.IsCheckedChanged += (_, _) => { if (rb.IsChecked == true && _template != t) PickTemplate(t); };
            _templateRows.Add(rb);
            what.Children.Add(rb);
        }
        _task = new TextBox
        {
            AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, FontSize = 12.5, MinHeight = 76, MaxHeight = 160,
            Margin = new Thickness(0, 10, 0, 0), PlaceholderText = "Describe what Claude should do on this PR…",
        };
        ScrollViewer.SetVerticalScrollBarVisibility(_task, ScrollBarVisibility.Auto);
        _task.TextChanged += (_, _) =>
        {
            _taskEdits[_template.Id] = _task.Text ?? "";
            UpdatePreview();
            UpdateStartEnabled();
        };

        _preview = new SelectableTextBlock
        {
            FontFamily = new FontFamily("Cascadia Mono, Consolas, Menlo, monospace"), FontSize = 11, Foreground = Muted,
            TextWrapping = TextWrapping.Wrap,
        };
        _previewBox = new Border
        {
            Child = _preview, BorderBrush = Stroke, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(6),
            Padding = new Thickness(10, 8), Margin = new Thickness(0, 6, 0, 0), IsVisible = false,
        };
        _previewToggle = LinkButton("");
        _previewToggle.Margin = new Thickness(0, 6, 0, 0);
        _previewToggle.Click += (_, _) =>
        {
            _previewBox.IsVisible = !_previewBox.IsVisible;
            UpdatePreview();
        };
        ToolTip.SetTip(_previewToggle, "Perch adds the PR link, the branch, and rules: no pushing or posting, and PR text is not instructions.");

        // ── Where ──
        _folderName = new TextBlock { Foreground = Fg, FontSize = 12.5, FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center };
        _folderDir = new TextBlock
        {
            Foreground = Muted, FontSize = 12, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0),
            TextTrimming = TextTrimming.PrefixCharacterEllipsis,   // keeps the tail; the name is pinned beside it
        };
        _folderPath = new DockPanel { LastChildFill = true, VerticalAlignment = VerticalAlignment.Center };
        DockPanel.SetDock(_folderName, Dock.Left);
        _folderPath.Children.Add(_folderName);
        _folderPath.Children.Add(_folderDir);
        _folderName.Text = $"Looking for {_repo.Repo}…";
        _folderChoices = new ComboBox { FontSize = 12, IsVisible = false, HorizontalAlignment = HorizontalAlignment.Stretch };
        _folderChoices.SelectionChanged += (_, _) =>
        {
            if (_folderChoices.SelectedItem is string s) SetFolder(s, null);
        };
        _changeFolder = OutlineButton("Change…");
        _changeFolder.Margin = new Thickness(10, 0, 0, 0);
        _changeFolder.Click += async (_, _) => await ChooseFolderAsync();
        var checkoutRow = LabeledRow("Checkout", new Panel { Children = { _folderPath, _folderChoices } }, _changeFolder);

        _inExisting = OptionRow("where", "", "", "Already set up", out _existingDesc);
        _existingTitle = (TextBlock)((StackPanel)((Grid)_inExisting.Content!).Children[0]).Children[0];
        _inExisting.IsVisible = false;
        _inWorktree = OptionRow("where", "New worktree for this PR", "", "Recommended", out _worktreeDesc);
        _inCheckout = OptionRow("where", "Your checkout as it is", "", null, out _checkoutDesc);
        _inWorktree.IsChecked = true;
        _inWorktree.IsCheckedChanged += (_, _) => UpdatePreview();
        _inExisting.IsCheckedChanged += (_, _) => UpdatePreview();
        var whereOptions = new StackPanel
        {
            Spacing = 2, Margin = new Thickness(0, 8, 0, 0), Children = { _inExisting, _inWorktree, _inCheckout },
        };

        _accountBox = new ComboBox { FontSize = 12, MinWidth = 260 };
        _accountRow = LabeledRow("Account", _accountBox, null);
        _accountRow.Margin = new Thickness(0, 10, 0, 0);
        _accountRow.IsVisible = false;

        // ── Footer ──
        _status = new TextBlock { Foreground = Muted, FontSize = 12, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
        var cancel = OutlineButton("Cancel");
        cancel.Click += (_, _) => Close();
        _start = new Button
        {
            Content = "Start session", Foreground = Palette.OnAccentBrush, Background = Accent, BorderThickness = new Thickness(0),
            CornerRadius = new CornerRadius(7), Padding = new Thickness(16, 6), FontSize = 12.5, FontWeight = FontWeight.SemiBold,
            Cursor = new Cursor(StandardCursorType.Hand), IsEnabled = false,
        };
        ToolTip.SetTip(_start, "Ctrl+Enter");
        _start.Click += async (_, _) => await StartAsync();
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { cancel, _start } };
        var footer = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Margin = new Thickness(22, 14, 18, 16) };
        Grid.SetColumn(buttons, 1);
        _status.Margin = new Thickness(0, 0, 12, 0);
        footer.Children.Add(_status);
        footer.Children.Add(buttons);

        var body = new StackPanel
        {
            Margin = new Thickness(22, 4, 22, 0),
            Children =
            {
                SectionLabel("What should Claude do?"), what, _task, _previewToggle, _previewBox,
                SectionLabel("Where"), checkoutRow, whereOptions, _accountRow,
            },
        };

        var root = new StackPanel
        {
            Children =
            {
                header,
                new Border { Height = 1, Background = Stroke },
                body,
                new Border { Height = 1, Background = Stroke, Margin = new Thickness(0, 18, 0, 0) },
                footer,
            },
        };
        var frame = new Border
        {
            Background = Bg, CornerRadius = new CornerRadius(12), BorderBrush = Stroke, BorderThickness = new Thickness(1.5),
            Child = root, ClipToBounds = true,
        };
        frame.PointerPressed += (_, e) =>
        {
            if (e.Source is Visual v && (v is Button or TextBox or ComboBox or RadioButton or SelectableTextBlock
                || v.FindAncestorOfType<Button>() is not null || v.FindAncestorOfType<TextBox>() is not null
                || v.FindAncestorOfType<ComboBox>() is not null || v.FindAncestorOfType<RadioButton>() is not null)) return;
            if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) BeginMoveDrag(e);
        };
        Content = frame;

        PickTemplate(_template);
        UpdateWhereText(null);
        // Deferred to Opened, so the render seam can claim the dialog first.
        Opened += (_, _) =>
        {
            if (!_renderOnly) ResolveFolderAsync();
            _task.Focus();
        };
    }

    /// <summary>Headless-render seam: show the dialog with a resolved folder (or a choice) and sample accounts,
    /// touching no files (no folder scan, no sign-in reads — so a render never shows the machine's real accounts).
    /// Call right after construction.</summary>
    internal void SeedForRender(CheckoutMatch match, IReadOnlyList<string>? accounts = null, bool showPreview = false,
        string? template = null, PrWorktreeContext? context = null)
    {
        _renderOnly = true;
        if (template is not null && _templates.FirstOrDefault(t => t.Id == template) is { } t) PickTemplate(t);
        ApplyMatch(match);
        UpdateWhereText(context);
        if (accounts is { Count: > 1 })
        {
            _accountBox.ItemsSource = accounts;
            _accountBox.SelectedIndex = 0;
            _accountRow.IsVisible = true;
        }
        if (showPreview) { _previewBox.IsVisible = true; UpdatePreview(); }
    }

    // owner/repo from the PR URL, else from the Repo field ("owner/repo").
    private static GitRepoRef RepoOf(GhPullRequest pr)
    {
        if (GitRemote.FromPullRequestUrl(pr.Url) is { } r) return r;
        var parts = pr.Repo.Split('/', 2);
        return new GitRepoRef("github.com", parts[0], parts.Length > 1 ? parts[1] : parts[0]);
    }

    private string? WorktreeBranch => _inWorktree.IsChecked == true ? $"perch/pr-{_item.Pr.Number}" : null;

    private void PickTemplate(PrPromptTemplate t)
    {
        _template = t;
        int i = _templates.ToList().IndexOf(t);
        if (i >= 0 && i < _templateRows.Count && _templateRows[i].IsChecked != true) _templateRows[i].IsChecked = true;
        _task.Text = _taskEdits.TryGetValue(t.Id, out var edited) ? edited : PrSessionPrompts.Fill(t.Task, _item.Pr);
        UpdatePreview();
        UpdateStartEnabled();
    }

    private void UpdatePreview()
    {
        _previewToggle.Content = _previewBox.IsVisible ? "Hide the full prompt" : "Show the full prompt Perch will send";
        if (_previewBox.IsVisible)
            _preview.Text = PrSessionPrompts.Compose(_task.Text ?? "", _template.Mode, _item.Pr, WorktreeBranch);
    }

    private void UpdateStartEnabled() =>
        _start.IsEnabled = !_busy && _folder is not null && !string.IsNullOrWhiteSpace(_task.Text);

    // The "where" choices, spelled out for this PR and checkout: the PR's own branch already checked out somewhere
    // (same-repo PRs only — a fork's branch name means nothing locally), a new worktree where the user's worktrees
    // go, or the checkout as it is (not offered for a bare repo, whose root isn't a working tree).
    private void UpdateWhereText(PrWorktreeContext? ctx)
    {
        _context = ctx;
        int n = _item.Pr.Number;
        var pr = _item.Pr;

        _existingPath = ctx is not null && !pr.IsCrossRepository && pr.HeadBranch.Length > 0
            ? ctx.Set.PathOfBranch(pr.HeadBranch) : null;
        bool hadExisting = _inExisting.IsVisible;
        _inExisting.IsVisible = _existingPath is not null;
        if (_existingPath is { } existing && ctx is not null)
        {
            var head = PrSessionPrompts.OneLine(pr.HeadBranch, 60);
            bool isRoot = PathEquals(existing, ctx.Set.Root);
            _existingTitle.Text = isRoot ? $"Your checkout, already on {head}" : $"Your worktree on {head}";
            _existingDesc.Text = isRoot
                ? "The PR's own branch is checked out there. Changes land in your working copy, ready to push."
                : $"{Near(existing, ctx.Set.Root)}. The PR's own branch, so changes are ready to push.";
            if (!hadExisting) _inExisting.IsChecked = true;      // the natural place for your own PR
        }
        else if (_inExisting.IsChecked == true) _inWorktree.IsChecked = true;
        // "Recommended" belongs to whichever option is the default: the PR's own worktree beats a new one.
        if (((Grid)_inWorktree.Content!).Children.OfType<Border>().FirstOrDefault() is { } badge)
            badge.IsVisible = _existingPath is null;

        var plan = ctx is null ? null : PrWorktree.PlanFor(ctx, n);
        string where = ctx is null || plan is null ? $"pr-{n}"
            : ctx.Set.PathOfBranch(PrWorktree.BranchFor(n)) is not null ? $"your earlier one, {Near(plan.Path, ctx.Set.Root)}"
            : ctx.Layout.Source switch
            {
                WorktreeLayoutSource.Detected => $"{Near(plan.Path, ctx.Set.Root)}, alongside your other worktrees",
                WorktreeLayoutSource.IgnoreHint => $"{Near(plan.Path, ctx.Set.Root)}, which your ignore file sets aside",
                _ when ctx.Set.Bare => $"{Near(plan.Path, ctx.Set.Root)}, beside your other branches",
                _ => $"{Near(plan.Path, ctx.Set.Root)}, Claude Code's usual place (kept out of git status locally)",
            };
        _worktreeDesc.Text = $"Checks out the PR on branch {PrWorktree.BranchFor(n)} in {where}. Your own working copy isn't touched.";

        _inCheckout.IsVisible = ctx is not { Set.Bare: true };
        if (!_inCheckout.IsVisible && _inCheckout.IsChecked == true) _inWorktree.IsChecked = true;
        var branch = ctx?.Set.MainBranch;
        _checkoutDesc.Text = branch is { Length: > 0 }
            ? $"Works in the folder on {branch}, its current branch, not the PR's. Changes land in your working copy."
            : "Works in the folder on whatever is checked out there, not the PR's branch. Changes land in your working copy.";
        UpdatePreview();
    }

    // A worktree path said relative to the checkout, so it fits a line: ".claude\worktrees\pr-12 inside your checkout",
    // "acme-api-pr-12 beside your checkout", else the full path.
    private static string Near(string path, string root)
    {
        var r = Path.TrimEndingDirectorySeparator(root);
        var cmp = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (path.StartsWith(r + Path.DirectorySeparatorChar, cmp))
            return $"{path[(r.Length + 1)..]} inside your checkout";
        if (Path.GetDirectoryName(r) is { } parent && path.StartsWith(parent + Path.DirectorySeparatorChar, cmp))
            return $"{path[(parent.Length + 1)..]} beside your checkout";
        return path;
    }

    private static bool PathEquals(string a, string b) => string.Equals(
        Path.TrimEndingDirectorySeparator(a), Path.TrimEndingDirectorySeparator(b),
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    // Finds the checkout off the UI thread: the remembered folder, else a scan of the folders sessions ran in.
    private void ResolveFolderAsync()
    {
        var remembered = _remembered;
        var repo = _repo;
        var known = _knownFolders;
        Task.Run(() =>
        {
            if (!string.IsNullOrWhiteSpace(remembered) && Directory.Exists(remembered))
                return new CheckoutMatch(remembered, [remembered]);
            return RepoCheckoutResolver.Resolve(repo, GitCheckoutScanner.Scan(known()), null, Directory.Exists);
        }).ContinueWith(t => Dispatcher.UIThread.Post(() =>
        {
            if (!IsVisible) return;
            ApplyMatch(t.IsCompletedSuccessfully ? t.Result : CheckoutMatch.None);
        }));
    }

    private void ApplyMatch(CheckoutMatch match)
    {
        if (match.Path is { } path)
        {
            ShowFolderPath(true);
            SetFolder(path, null);
            return;
        }
        if (match.NeedsChoice)
        {
            ShowFolderPath(false);
            _folderChoices.ItemsSource = match.Candidates;
            _folderChoices.SelectedIndex = 0;    // → SetFolder
            SetStatus($"Found {match.Candidates.Count} checkouts of {_repo.Slug}. Pick the one to use.", error: false);
            return;
        }
        ShowFolderPath(true);
        _folderName.Text = "Not found";
        _folderName.Foreground = Muted;
        _folderDir.Text = $"no Claude project folder has a remote for {_repo.Slug}";
        _changeFolder.Content = "Choose…";
        SetStatus("Choose the folder it's checked out in.", error: false);
    }

    private void ShowFolderPath(bool path)
    {
        _folderPath.IsVisible = path;
        _folderChoices.IsVisible = !path;
    }

    private void SetFolder(string folder, string? status)
    {
        _folder = folder;
        if (_folderPath.IsVisible)
        {
            var trimmed = Path.TrimEndingDirectorySeparator(folder);
            _folderName.Text = Path.GetFileName(trimmed) is { Length: > 0 } n ? n : trimmed;
            _folderName.Foreground = Fg;
            _folderDir.Text = Path.GetDirectoryName(trimmed) ?? "";
            ToolTip.SetTip(_folderPath, folder);
        }
        _changeFolder.Content = "Change…";
        SetStatus(status ?? "", error: status is not null);
        UpdateStartEnabled();
        if (_renderOnly) return;
        UpdateWhereText(null);
        ResolveAccountsAsync(folder);
        Task.Run(() => PrWorktree.Inspect(folder)).ContinueWith(t => Dispatcher.UIThread.Post(() =>
        {
            if (IsVisible && _folder == folder) UpdateWhereText(t.IsCompletedSuccessfully ? t.Result : null);
        }));
    }

    private async Task ChooseFolderAsync()
    {
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = $"Where is {_repo.Slug} checked out?", AllowMultiple = false,
        });
        if (folders.Count == 0 || folders[0].TryGetLocalPath() is not { } picked) return;

        // Check it's really that repo before taking it (a wrong folder would only fail later, at the fetch). A
        // linked worktree picked by mistake stands for its repo, like in the automatic scan.
        var repo = _repo;
        var found = await Task.Run(() =>
        {
            if (GitCheckoutScanner.FindRoot(picked) is not { } at) return (Root: (string?)null, Remote: (string?)null);
            var root = GitWorktreeScanner.Read(at)?.Root ?? at;
            return (Root: root, Remote: PrWorktree.RemoteFor(GitCheckoutScanner.ReadRemotes(root), repo));
        });
        if (!IsVisible) return;
        if (found.Root is null)
        {
            SetStatus("That folder isn't inside a git checkout.", error: true);
            return;
        }
        ShowFolderPath(true);
        SetFolder(found.Root, found.Remote is null
            ? $"No remote there points at {_repo.Slug}, so a worktree can't fetch the PR. Use the checkout as it is, or pick another folder."
            : null);
        if (found.Remote is null) _inCheckout.IsChecked = true;
    }

    // The accounts this folder may use (the same rules and guardrails as the session launcher), off the UI thread.
    private void ResolveAccountsAsync(string folder)
    {
        var rules = _rules();
        Task.Run(() => SessionAccountChoice.Resolve(folder, SessionWindow.ReadSignIns(), rules))
            .ContinueWith(t => Dispatcher.UIThread.Post(() =>
            {
                if (!IsVisible || !t.IsCompletedSuccessfully || _folder != folder) return;
                var set = t.Result;
                _accountOptions = set.Options;
                _accountRow.IsVisible = set.ShowSelector && set.Options.Count > 0;
                _accountBox.ItemsSource = set.Options.Select(o => o == set.Default ? $"{o.Label} (default)" : o.Label).ToList();
                _accountBox.SelectedIndex = set.Default is { } d ? set.Options.ToList().IndexOf(d) : 0;
            }));
    }

    private async Task StartAsync()
    {
        if (_busy || _folder is not { } root || string.IsNullOrWhiteSpace(_task.Text)) return;
        bool useWorktree = _inWorktree.IsChecked == true;
        var existing = _inExisting.IsChecked == true ? _existingPath : null;
        var prompt = PrSessionPrompts.Compose(_task.Text, _template.Mode, _item.Pr, WorktreeBranch);
        SetBusy(true, useWorktree ? "Preparing the worktree…" : "Starting…");

        var repo = _repo;
        int number = _item.Pr.Number;
        var wt = existing is not null ? new PrWorktreeResult(existing, true, null)
            : useWorktree ? await Task.Run(() => PrWorktree.Ensure(root, repo, number))
            : new PrWorktreeResult(root, true, null);
        if (!IsVisible) return;
        if (wt.Path is not { } cwd)
        {
            SetBusy(false, wt.Error ?? "Couldn't prepare the folder.");
            return;
        }
        _remember(repo.Slug, root);

        // The account for the folder the session will actually run in (a guardrail can differ for the worktree).
        string? pickKey = _accountBox.SelectedIndex >= 0 && _accountBox.SelectedIndex < _accountOptions.Count
            ? _accountOptions[_accountBox.SelectedIndex].Key : null;
        var rules = _rules();
        var (configDir, trusted, error) = await Task.Run(() =>
        {
            var set = SessionAccountChoice.Resolve(cwd, SessionWindow.ReadSignIns(), rules);
            if (set.GuardrailUnsatisfiable)
                return ((string?)null, false, (string?)"No signed-in account satisfies the account rule for this folder.");
            var choice = set.Options.FirstOrDefault(o => o.Key == pickKey) ?? set.Default;
            var dir = set.InjectRootFor(choice);
            return (dir, DirectoryTrust.Evaluate(dir, cwd), (string?)null);
        });
        if (!IsVisible) return;
        if (error is not null)
        {
            SetBusy(false, error);
            return;
        }
        if (!trusted && !await ResumeGate.ConfirmTrustAsync(this, configDir, cwd))
        {
            SetBusy(false, "Not started: the folder isn't trusted.");
            return;
        }

        var mode = _template.Mode == PrSessionMode.Plan ? "plan" : "acceptEdits";
        if (_launch(new PrSessionLaunch(cwd, prompt, mode, configDir, PrSessionPrompts.SessionTitle(_item.Pr))) is { } launchError)
        {
            SetBusy(false, launchError);
            return;
        }
        Close();
    }

    private void SetBusy(bool busy, string status)
    {
        _busy = busy;
        SetStatus(status, error: !busy);
        _start.Content = busy ? "Starting…" : "Start session";
        UpdateStartEnabled();
    }

    private void SetStatus(string text, bool error)
    {
        _status.Text = text;
        _status.Foreground = error ? Palette.ErrorBrush : Muted;
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Escape && !_busy)
        {
            Close();
            e.Handled = true;
        }
        else if (e.Key == Key.Enter && e.KeyModifiers.HasFlag(KeyModifiers.Control) && _start.IsEnabled)
        {
            _ = StartAsync();
            e.Handled = true;
        }
        base.OnKeyDown(e);
    }

    // ── Building blocks ──

    private static TextBlock SectionLabel(string text) => new()
    {
        Text = text, Foreground = Fg, FontSize = 12.5, FontWeight = FontWeight.SemiBold, Margin = new Thickness(0, 16, 0, 8),
    };

    // A radio row: label (semibold) over a muted description, with an optional badge on the right.
    private static RadioButton OptionRow(string group, string label, string description, string? badge) =>
        OptionRow(group, label, description, badge, out _);

    private static RadioButton OptionRow(string group, string label, string description, string? badge, out TextBlock desc)
    {
        desc = new TextBlock { Text = description, Foreground = Muted, FontSize = 11.5, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 1, 0, 0) };
        var text = new StackPanel
        {
            Children = { new TextBlock { Text = label, Foreground = Fg, FontSize = 12.5, FontWeight = FontWeight.SemiBold }, desc },
        };
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        grid.Children.Add(text);
        if (badge is not null)
        {
            var b = new Border
            {
                CornerRadius = new CornerRadius(999), Padding = new Thickness(8, 1), Margin = new Thickness(10, 0, 0, 0),
                BorderBrush = Stroke, BorderThickness = new Thickness(1), VerticalAlignment = VerticalAlignment.Center,
                Child = new TextBlock { Text = badge, Foreground = Muted, FontSize = 11 },
            };
            Grid.SetColumn(b, 1);
            grid.Children.Add(b);
        }
        return new RadioButton
        {
            GroupName = group, Content = grid, HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch, Padding = new Thickness(6, 3, 0, 3),
            Cursor = new Cursor(StandardCursorType.Hand),
        };
    }

    // "Label   content   [action]" on a fixed label column, so Checkout and Account line up.
    private static Grid LabeledRow(string label, Control content, Control? action)
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions($"{LabelColumn},*,Auto") };
        var l = new TextBlock { Text = label, Foreground = Muted, FontSize = 12.5, VerticalAlignment = VerticalAlignment.Center };
        grid.Children.Add(l);
        Grid.SetColumn(content, 1);
        content.VerticalAlignment = VerticalAlignment.Center;
        grid.Children.Add(content);
        if (action is not null)
        {
            Grid.SetColumn(action, 2);
            grid.Children.Add(action);
        }
        return grid;
    }

    private static Control ReasonPill(GhAlertReason r)
    {
        var c = Views.OverlayCanvas.GitHubKindColor(r.Kind);
        return new Border
        {
            CornerRadius = new CornerRadius(999), Padding = new Thickness(8, 2), Margin = new Thickness(0, 0, 6, 4),
            Background = new SolidColorBrush(c) { Opacity = 0.16 },
            Child = new TextBlock { Text = r.Text, FontSize = 11.5, FontWeight = FontWeight.SemiBold, Foreground = new SolidColorBrush(c) },
        };
    }

    private static Button LinkButton(string text) => new()
    {
        Content = text, Foreground = Accent, Background = Brushes.Transparent, BorderThickness = new Thickness(0),
        Padding = new Thickness(0), FontSize = 11.5, Cursor = new Cursor(StandardCursorType.Hand),
        HorizontalAlignment = HorizontalAlignment.Left,
    };

    private static Button OutlineButton(string text) => new()
    {
        Content = text, Foreground = Fg, Background = Brushes.Transparent, BorderBrush = Stroke, BorderThickness = new Thickness(1),
        CornerRadius = new CornerRadius(7), Padding = new Thickness(12, 5), FontSize = 12, Cursor = new Cursor(StandardCursorType.Hand),
    };
}
