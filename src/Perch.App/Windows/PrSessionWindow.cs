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

/// <summary>What the dialog hands the App to start: folder, the prompt, the permission mode and the account.</summary>
internal sealed record PrSessionLaunch(string Cwd, string Prompt, string PermissionMode, string? ConfigDir);

/// <summary>
/// "Start a session" on a GitHub dashboard PR (S4 in docs/github-dashboard-plan.md). Pick a quick prompt (the ones
/// that fit the PR's reasons come first), edit it, confirm the folder, and the session starts in the background as
/// an ordinary Perch-controlled session: it shows on the overlay and in the Roost, and opens like any other.
///
/// <para>The folder is found by <see cref="RepoCheckoutResolver"/> over the folders Claude sessions have run in
/// (read off the UI thread), or the one remembered for the repo; the user can change it. By default the work happens
/// in a per-PR <see cref="PrWorktree"/> so the user's checkout is never touched. Folder trust is asked the same way a
/// new session asks it, for the worktree itself: its code is the PR's, which may come from someone else. The account
/// follows the same rules and guardrails as the session launcher.</para>
/// </summary>
internal sealed class PrSessionWindow : Window
{
    private static readonly IBrush Bg     = Palette.OverlaySurfaceBrush;
    private static readonly IBrush Stroke = Palette.BorderBrush;
    private static readonly IBrush Fg     = Palette.FgBrush;
    private static readonly IBrush Muted  = Palette.MutedBrush;
    private static readonly IBrush Accent = Palette.AccentBrush;

    private readonly GhPrItem _item;
    private readonly GitRepoRef _repo;
    private readonly Func<IReadOnlyList<string>> _knownFolders;
    private readonly string? _remembered;
    private readonly Action<string, string> _remember;
    private readonly Func<IReadOnlyList<AccountRule>?> _rules;
    private readonly Func<PrSessionLaunch, string?> _launch;

    private readonly IReadOnlyList<PrPromptTemplate> _templates;
    private readonly List<Button> _templateChips = new();
    private PrPromptTemplate _template;

    private readonly TextBox _prompt;
    private readonly TextBlock _modeText;
    private readonly TextBlock _folderText;
    private readonly ComboBox _folderChoices;
    private readonly Button _changeFolder;
    private readonly CheckBox _useWorktree;
    private readonly TextBlock _worktreeText;
    private readonly StackPanel _accountRow;
    private readonly ComboBox _accountBox;
    private readonly TextBlock _status;
    private readonly Button _start;

    private string? _folder;
    private IReadOnlyList<AccountChoice> _accountOptions = [];
    private bool _busy;

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

        Title = "Start a session";
        WindowDecorations = WindowDecorations.None;
        Background = Brushes.Transparent;
        TransparencyLevelHint = [WindowTransparencyLevel.Transparent];
        CanResize = false;
        Width = 620;
        SizeToContent = SizeToContent.Height;
        ShowInTaskbar = true;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        // ── Header: what we're starting on ──
        var heading = new TextBlock { Text = "Start a session", Foreground = Fg, FontWeight = FontWeight.Bold, FontSize = 16 };
        var sub = new TextBlock
        {
            Text = $"{item.Pr.Repo}#{item.Pr.Number} · {item.Pr.Title}", Foreground = Muted, FontSize = 12,
            Margin = new Thickness(0, 2, 0, 0), TextTrimming = TextTrimming.CharacterEllipsis,
        };
        var close = new Button
        {
            Content = "✕", Foreground = Muted, Background = Brushes.Transparent, BorderThickness = new Thickness(0),
            Padding = new Thickness(4, 0), FontSize = 14, Cursor = new Cursor(StandardCursorType.Hand), VerticalAlignment = VerticalAlignment.Top,
        };
        close.Click += (_, _) => Close();
        var header = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        Grid.SetColumn(close, 1);
        header.Children.Add(new StackPanel { Children = { heading, sub } });
        header.Children.Add(close);

        // ── Prompt: quick-prompt chips over an editable box ──
        var chips = new WrapPanel { Orientation = Orientation.Horizontal };
        foreach (var t in _templates)
        {
            var chip = Chip(t.Label);
            chip.Click += (_, _) => PickTemplate(t);
            _templateChips.Add(chip);
            chips.Children.Add(chip);
        }
        _prompt = new TextBox
        {
            AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, FontSize = 12.5, Height = 170,
            Margin = new Thickness(0, 6, 0, 0),
        };
        ScrollViewer.SetVerticalScrollBarVisibility(_prompt, ScrollBarVisibility.Auto);
        _modeText = new TextBlock { Foreground = Muted, FontSize = 11.5, Margin = new Thickness(0, 6, 0, 0), TextWrapping = TextWrapping.Wrap };

        // ── Folder: the resolved checkout (a path keeps its name and sheds its head when it can't fit) ──
        _folderText = new TextBlock
        {
            Text = $"Looking for {_repo.Slug} on this machine…", Foreground = Muted, FontSize = 12.5,
            VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.PrefixCharacterEllipsis,
        };
        _folderChoices = new ComboBox { FontSize = 12, IsVisible = false, HorizontalAlignment = HorizontalAlignment.Stretch };
        _folderChoices.SelectionChanged += (_, _) =>
        {
            if (_folderChoices.SelectedItem is string s) SetFolder(s, null);
        };
        _changeFolder = OutlineButton("Choose…");
        _changeFolder.Click += async (_, _) => await ChooseFolderAsync();
        var folderGrid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Margin = new Thickness(0, 4, 0, 0) };
        var folderCell = new Panel { Children = { _folderText, _folderChoices } };
        Grid.SetColumn(_changeFolder, 1);
        _changeFolder.Margin = new Thickness(10, 0, 0, 0);
        folderGrid.Children.Add(folderCell);
        folderGrid.Children.Add(_changeFolder);

        _useWorktree = new CheckBox
        {
            IsChecked = true, FontSize = 12.5, Foreground = Fg, Margin = new Thickness(0, 8, 0, 0),
            Content = $"Work in a separate worktree on branch perch/pr-{item.Pr.Number}, leaving your checkout untouched",
        };
        _useWorktree.IsCheckedChanged += (_, _) => UpdateWorktreeText();
        _worktreeText = new TextBlock { Foreground = Muted, FontSize = 11.5, Margin = new Thickness(28, 0, 0, 0), TextTrimming = TextTrimming.PrefixCharacterEllipsis };

        // ── Account (only on machines with a choice) ──
        _accountBox = new ComboBox { FontSize = 12, MinWidth = 260 };
        _accountRow = new StackPanel
        {
            Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Thickness(0, 10, 0, 0), IsVisible = false,
            Children = { Label("Account", top: 0), _accountBox },
        };
        ((TextBlock)_accountRow.Children[0]).VerticalAlignment = VerticalAlignment.Center;

        // ── Footer: status, Cancel, Start ──
        _status = new TextBlock { Foreground = Muted, FontSize = 12, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
        var cancel = OutlineButton("Cancel");
        cancel.Click += (_, _) => Close();
        _start = new Button
        {
            Content = "Start in background", Foreground = Palette.OnAccentBrush, Background = Accent, BorderThickness = new Thickness(0),
            CornerRadius = new CornerRadius(7), Padding = new Thickness(14, 6), FontSize = 12.5, FontWeight = FontWeight.SemiBold,
            Cursor = new Cursor(StandardCursorType.Hand), IsEnabled = false,
        };
        _start.Click += async (_, _) => await StartAsync();
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { cancel, _start } };
        var footer = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Margin = new Thickness(0, 16, 0, 0) };
        Grid.SetColumn(buttons, 1);
        footer.Children.Add(_status);
        footer.Children.Add(buttons);

        var body = new StackPanel
        {
            Margin = new Thickness(20, 16, 20, 18),
            Children =
            {
                header,
                Label("Prompt"), chips, _prompt, _modeText,
                Label("Folder"), folderGrid, _useWorktree, _worktreeText,
                _accountRow,
                footer,
            },
        };
        var frame = new Border
        {
            Background = Bg, CornerRadius = new CornerRadius(12), BorderBrush = Stroke, BorderThickness = new Thickness(1.5),
            Child = body, ClipToBounds = true,
        };
        frame.PointerPressed += (_, e) =>
        {
            if (e.Source is Visual v && (v is Button or TextBox or ComboBox or CheckBox
                || v.FindAncestorOfType<Button>() is not null || v.FindAncestorOfType<TextBox>() is not null
                || v.FindAncestorOfType<ComboBox>() is not null || v.FindAncestorOfType<CheckBox>() is not null)) return;
            if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) BeginMoveDrag(e);
        };
        Content = frame;

        PickTemplate(_template);
        // Deferred to Opened, so the render seam can claim the dialog first.
        Opened += (_, _) => { if (!_renderOnly) ResolveFolderAsync(); };
    }

    private bool _renderOnly;

    /// <summary>Headless-render seam: show the dialog with a resolved folder (or a choice) and sample accounts,
    /// touching no files (no folder scan, no sign-in reads — so a render never shows the machine's real accounts).
    /// Call right after construction, before the background folder scan can land.</summary>
    internal void SeedForRender(CheckoutMatch match, IReadOnlyList<string>? accounts = null)
    {
        _renderOnly = true;
        ApplyMatch(match);
        if (accounts is { Count: > 1 })
        {
            _accountBox.ItemsSource = accounts;
            _accountBox.SelectedIndex = 0;
            _accountRow.IsVisible = true;
        }
    }

    // owner/repo from the PR URL, else from the Repo field ("owner/repo").
    private static GitRepoRef RepoOf(GhPullRequest pr)
    {
        if (GitRemote.FromPullRequestUrl(pr.Url) is { } r) return r;
        var parts = pr.Repo.Split('/', 2);
        return new GitRepoRef("github.com", parts[0], parts.Length > 1 ? parts[1] : parts[0]);
    }

    private void PickTemplate(PrPromptTemplate t)
    {
        _template = t;
        _prompt.Text = PrSessionPrompts.Fill(t.Text, _item.Pr);
        for (int i = 0; i < _templates.Count; i++) StyleChip(_templateChips[i], _templates[i] == t);
        _modeText.Text = t.Mode == PrSessionMode.Plan
            ? "Runs read-only (plan mode): Claude can read and run commands to look around, but won't edit files."
            : "Claude can edit files in the folder without asking (accept edits). Commands still ask first.";
    }

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
            SetFolder(path, null);
            return;
        }
        if (match.NeedsChoice)
        {
            _folderChoices.ItemsSource = match.Candidates;
            _folderChoices.IsVisible = true;
            _folderText.IsVisible = false;
            _folderChoices.SelectedIndex = 0;    // → SetFolder
            _status.Text = $"{match.Candidates.Count} checkouts of {_repo.Slug} found. Pick one.";
            return;
        }
        _folderText.Text = $"No checkout of {_repo.Slug} found among your Claude project folders.";
        _status.Text = "Choose the folder it's checked out in.";
        UpdateWorktreeText();
    }

    private void SetFolder(string folder, string? status)
    {
        _folder = folder;
        if (!_folderChoices.IsVisible)
        {
            _folderText.Text = folder;
            _folderText.Foreground = Fg;
        }
        _status.Text = status ?? "";
        _status.Foreground = Muted;
        _start.IsEnabled = !_busy;
        UpdateWorktreeText();
        if (!_renderOnly) ResolveAccountsAsync(folder);
    }

    private void UpdateWorktreeText()
    {
        var plan = _folder is { } f ? PrWorktree.PlanFor(f, _repo, _item.Pr.Number) : null;
        _worktreeText.IsVisible = _useWorktree.IsChecked == true && plan is not null;
        _worktreeText.Text = plan?.Path ?? "";
    }

    private async Task ChooseFolderAsync()
    {
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = $"Where is {_repo.Slug} checked out?", AllowMultiple = false,
        });
        if (folders.Count == 0 || folders[0].TryGetLocalPath() is not { } picked) return;

        // Check it's really that repo before taking it (a wrong folder would only fail later, at the fetch).
        var remotes = await Task.Run(() => GitCheckoutScanner.FindRoot(picked) is { } root
            ? (Root: root, Remote: PrWorktree.RemoteFor(GitCheckoutScanner.ReadRemotes(root), _repo))
            : (Root: (string?)null, Remote: (string?)null));
        if (!IsVisible) return;
        if (remotes.Root is null)
        {
            _status.Text = "That folder isn't inside a git checkout.";
            return;
        }
        _folderChoices.IsVisible = false;
        _folderText.IsVisible = true;
        SetFolder(remotes.Root, remotes.Remote is null
            ? $"No remote there points at {_repo.Slug}. A worktree needs one; untick it to work in the folder as it is."
            : null);
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
        if (_busy || _folder is not { } root) return;
        var prompt = _prompt.Text?.Trim() ?? "";
        if (prompt.Length == 0)
        {
            _status.Text = "The prompt is empty.";
            return;
        }
        SetBusy(true, _useWorktree.IsChecked == true ? "Preparing the worktree…" : "Starting…");

        var repo = _repo;
        int number = _item.Pr.Number;
        var wt = _useWorktree.IsChecked == true
            ? await Task.Run(() => PrWorktree.Ensure(root, repo, number))
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
        if (_launch(new PrSessionLaunch(cwd, prompt, mode, configDir)) is { } launchError)
        {
            SetBusy(false, launchError);
            return;
        }
        Close();
    }

    private void SetBusy(bool busy, string status)
    {
        _busy = busy;
        _status.Text = status;
        _status.Foreground = busy ? Muted : Palette.ErrorBrush;
        _start.IsEnabled = !busy && _folder is not null;
        _start.Content = busy ? "Starting…" : "Start in background";
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Escape && !_busy)
        {
            Close();
            e.Handled = true;
        }
        base.OnKeyDown(e);
    }

    private static TextBlock Label(string text, double top = 14) => new()
    {
        Text = text.ToUpperInvariant(), Foreground = Muted, FontSize = 10.5, FontWeight = FontWeight.SemiBold,
        LetterSpacing = 0.6, Margin = new Thickness(0, top, 0, 6),
    };

    private static Button Chip(string text) => new()
    {
        Content = text, FontSize = 12, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(7),
        Padding = new Thickness(10, 4), Margin = new Thickness(0, 0, 6, 6), Cursor = new Cursor(StandardCursorType.Hand),
    };

    private static void StyleChip(Button b, bool on)
    {
        b.Background = on ? new SolidColorBrush(Palette.Active.Accent.ToColor()) { Opacity = 0.16 } : Brushes.Transparent;
        b.Foreground = on ? Accent : Muted;
        b.BorderBrush = on ? Accent : Stroke;
    }

    private static Button OutlineButton(string text) => new()
    {
        Content = text, Foreground = Fg, Background = Brushes.Transparent, BorderBrush = Stroke, BorderThickness = new Thickness(1),
        CornerRadius = new CornerRadius(7), Padding = new Thickness(12, 5), FontSize = 12, Cursor = new Cursor(StandardCursorType.Hand),
    };
}
