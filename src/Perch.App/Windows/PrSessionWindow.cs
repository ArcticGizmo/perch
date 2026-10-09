using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
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
/// "Start session" on a GitHub dashboard card (part 5 of docs/github-dashboard-plan.md). One question first: <b>how
/// should the PR be checked out?</b>
/// <list type="bullet">
/// <item><b>No checkout</b>: just prompting. The session runs in an empty Perch scratch folder and reads the PR
/// through <c>gh</c>.</item>
/// <item><b>Checkout</b> (the default): the user's clone of the repo, found among the folders Claude sessions have run
/// in (or the one remembered for the repo) and changeable with the same folder search the session launcher uses.
/// Then the worktree: a <b>new worktree</b> placed by the repo's worktree strategy (inferred from the user's own
/// worktrees, changeable, remembered per repo), an <b>existing worktree</b> of the repo, or <b>no worktree</b> (the
/// checkout as it is). With no clone found, Perch makes a fresh one in its data folder.</item>
/// </list>
/// Then the prompt: a quick-prompt picker (the ones fitting the PR's reasons first) filling an editable task, which
/// Perch wraps with the PR link and the rules (previewable). Bottom right, <b>Launch in Perch</b> opens an ordinary
/// Perch-controlled session window named after the PR; <b>Copy command</b> prepares the same folder and copies the
/// one-line terminal command instead (<see cref="PrSessionCommand"/>).
///
/// <para>Folder trust is asked the way a new session asks it, for the final folder: a worktree's code is the PR's,
/// which may come from someone else. The account follows the same rules and guardrails as the session launcher,
/// resolved again for the final folder. All folder IO runs off the UI thread.</para>
/// </summary>
internal sealed class PrSessionWindow : Window
{
    private static readonly IBrush Bg     = Palette.OverlaySurfaceBrush;
    private static readonly IBrush Sunken = Palette.SurfaceSunkenBrush;
    private static readonly IBrush Stroke = Palette.BorderBrush;
    private static readonly IBrush Fg     = Palette.FgBrush;
    private static readonly IBrush Muted  = Palette.MutedBrush;
    private static readonly IBrush Accent = Palette.AccentBrush;
    private static readonly FontFamily Mono = new("Cascadia Mono, Consolas, Menlo, monospace");

    private const double LabelColumn = 76;

    private enum WorktreeChoice { New, Existing, None }

    private readonly GhPrItem _item;
    private readonly GitRepoRef _repo;
    private readonly Func<IReadOnlyList<string>> _knownFolders;
    private readonly string? _remembered;
    private readonly string? _rememberedLayout;
    private readonly Action<string, string> _remember;
    private readonly Action<string, string?> _rememberLayout;
    private readonly Func<IReadOnlyList<AccountRule>?> _rules;
    private readonly Func<PrSessionLaunch, string?> _launch;

    // How: no checkout | checkout
    private readonly RadioButton _noCheckout, _checkout;
    private readonly Border _noCheckoutTile, _checkoutTile;
    private readonly Border _checkoutPanel;
    private readonly AutoCompleteBox _cloneBox;
    private readonly TextBlock _cloneNote;
    private readonly StackPanel _worktreeOptions;
    private readonly RadioButton _newWorktree, _existingWorktree, _noWorktree;
    private readonly TextBlock _newDesc, _existingDesc, _noWorktreeDesc;
    private readonly ComboBox _layoutBox, _existingBox;
    private readonly Grid _layoutRow, _existingRow;
    private IReadOnlyList<WorktreeLayout> _layouts = [];
    private IReadOnlyList<GitWorktree> _existing = [];
    private PrWorktreeContext? _context;          // the chosen checkout's worktrees + inferred layout, read off the UI thread
    private bool _worktreeTouched;                // the user picked a worktree option; stop moving the default under them
    private bool _syncing;                        // setting controls from code; ignore their change events
    private bool _resolved;                       // the clone search has finished (so "no clone found" is final)

    // What
    private readonly IReadOnlyList<PrPromptTemplate> _templates;
    private readonly Dictionary<string, string> _taskEdits = new();     // per template, so switching keeps edits
    private readonly ComboBox _templateBox;
    private readonly Border _readOnlyBadge;
    private PrPromptTemplate _template;
    private readonly TextBox _task;
    private readonly Button _previewToggle;
    private readonly Border _previewBox;
    private readonly SelectableTextBlock _preview;

    private readonly Grid _accountRow;
    private readonly ComboBox _accountBox;

    private readonly TextBlock _status;
    private readonly Button _launchButton, _copyButton;

    private string? _folder;
    private IReadOnlyList<AccountChoice> _accountOptions = [];
    private bool _busy, _renderOnly;

    public PrSessionWindow(GhPrItem item, Func<IReadOnlyList<string>> knownFolders, string? remembered,
        string? rememberedLayout, Action<string, string> remember, Action<string, string?> rememberLayout,
        Func<IReadOnlyList<AccountRule>?> rules, Func<PrSessionLaunch, string?> launch)
    {
        _item = item;
        _repo = RepoOf(item.Pr);
        _knownFolders = knownFolders;
        _remembered = remembered;
        _rememberedLayout = rememberedLayout;
        _remember = remember;
        _rememberLayout = rememberLayout;
        _rules = rules;
        _launch = launch;
        _templates = PrSessionPrompts.ForReasons(item.Reasons.Select(r => r.Kind));
        _template = _templates[0];

        Title = $"Start a session on PR #{item.Pr.Number}";
        WindowDecorations = WindowDecorations.None;
        Background = Brushes.Transparent;
        TransparencyLevelHint = [WindowTransparencyLevel.Transparent];
        CanResize = false;
        Width = 820;   // room for a real clone path and a worktree strategy on one line
        SizeToContent = SizeToContent.Height;
        ShowInTaskbar = true;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        // ── Header: the PR, as its card shows it ──
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
        var meta = $"{item.Pr.Repo} #{item.Pr.Number}";
        if (GitHubAlertsWindow.Branches(item.Pr) is { Length: > 0 } branches) meta += $" · {branches}";
        var prMeta = new TextBlock
        {
            Text = meta, Foreground = Muted, FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 2, 0, 0),
        };
        var pills = new WrapPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 6, 0, 0) };
        foreach (var r in item.Reasons) pills.Children.Add(ReasonPill(r));
        pills.IsVisible = item.Reasons.Count > 0;
        var header = new StackPanel { Margin = new Thickness(22, 18, 18, 16), Children = { headRow, prTitle, prMeta, pills } };

        // ── How should it be checked out? Two tiles side by side, the usual one (Checkout) first ──
        (_checkout, _checkoutTile) = ChoiceTile("Checkout", "Work on the PR's code in your clone of the repo.");
        (_noCheckout, _noCheckoutTile) = ChoiceTile("No checkout", "Just prompting. Claude reads the PR through gh, with no code on disk.");
        _checkout.IsChecked = true;
        _noCheckout.IsCheckedChanged += (_, _) => HowChanged();
        _checkout.IsCheckedChanged += (_, _) => HowChanged();
        var tiles = new Grid { ColumnDefinitions = new ColumnDefinitions("*,10,*") };
        Grid.SetColumn(_noCheckoutTile, 2);
        tiles.Children.Add(_checkoutTile);
        tiles.Children.Add(_noCheckoutTile);

        // Clone: the session launcher's folder search, over the folders Claude sessions have run in.
        _cloneBox = new AutoCompleteBox { FontSize = 12.5, HorizontalAlignment = HorizontalAlignment.Stretch, VerticalAlignment = VerticalAlignment.Center };
        FolderSearchBox.Configure(_cloneBox, $"Search your projects for {_repo.Repo}…", Fg, Muted, Palette.FormBgBrush, Stroke,
            FontFamily.Default, Mono, borderless: false);
        // Committed when the suggestions close (a pick), on Enter, or on leaving the box — not per arrow-key move.
        _cloneBox.DropDownClosed += (_, _) => CommitCloneText();
        _cloneBox.LostFocus += (_, _) => CommitCloneText();
        _cloneBox.AddHandler(KeyDownEvent, (_, e) =>
        {
            if (e.Key == Key.Enter && !e.KeyModifiers.HasFlag(KeyModifiers.Control) && !_cloneBox.IsDropDownOpen)
            {
                CommitCloneText();
                e.Handled = true;
            }
        }, RoutingStrategies.Tunnel);
        var browse = OutlineButton("Browse…");
        browse.Margin = new Thickness(8, 0, 0, 0);
        browse.Click += async (_, _) => await BrowseAsync();
        var cloneRow = LabeledRow("Clone", _cloneBox, browse);
        _cloneNote = new TextBlock
        {
            Foreground = Muted, FontSize = 11.5, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(LabelColumn, 4, 0, 0),
        };

        // Worktree: new (placed by the strategy) | existing | none.
        _newWorktree = OptionRow("worktree", "New worktree", out _newDesc);
        _existingWorktree = OptionRow("worktree", "Existing worktree", out _existingDesc);
        _noWorktree = OptionRow("worktree", "No worktree", out _noWorktreeDesc);
        _newWorktree.IsChecked = true;
        foreach (var rb in new[] { _newWorktree, _existingWorktree, _noWorktree })
        {
            rb.IsCheckedChanged += (_, _) => WorktreeChanged();
            rb.Click += (_, _) => _worktreeTouched = true;
        }
        _layoutBox = new ComboBox { FontSize = 12, HorizontalAlignment = HorizontalAlignment.Stretch };
        _layoutBox.SelectionChanged += (_, _) => { if (!_syncing) { UpdateWorktreeText(); UpdatePreview(); } };
        ToolTip.SetTip(_layoutBox, "Where new worktrees go for this repo. Perch follows your existing worktrees; a change is remembered for the repo.");
        _layoutRow = Indented("Strategy", _layoutBox);
        _existingBox = new ComboBox { FontSize = 12, HorizontalAlignment = HorizontalAlignment.Stretch };
        _existingBox.SelectionChanged += (_, _) => { if (!_syncing) { UpdateWorktreeText(); UpdatePreview(); } };
        _existingRow = Indented("Worktree", _existingBox);
        _worktreeOptions = new StackPanel
        {
            Spacing = 2, Margin = new Thickness(0, 12, 0, 0),
            Children = { SubLabel("Worktree"), _newWorktree, _layoutRow, _existingWorktree, _existingRow, _noWorktree },
        };

        _checkoutPanel = new Border
        {
            Background = Sunken, BorderBrush = Stroke, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(8),
            Padding = new Thickness(14, 12), Margin = new Thickness(0, 10, 0, 0),
            Child = new StackPanel { Children = { cloneRow, _cloneNote, _worktreeOptions } },
        };

        // ── Prompt: a quick-prompt picker and the editable task ──
        _templateBox = new ComboBox { FontSize = 12, MinWidth = 220, VerticalAlignment = VerticalAlignment.Center };
        foreach (var t in _templates) _templateBox.Items.Add(t.Label);
        _templateBox.SelectionChanged += (_, _) =>
        {
            if (!_syncing && _templateBox.SelectedIndex >= 0) PickTemplate(_templates[_templateBox.SelectedIndex]);
        };
        _readOnlyBadge = new Border
        {
            CornerRadius = new CornerRadius(999), Padding = new Thickness(8, 1), Margin = new Thickness(8, 0, 0, 0),
            BorderBrush = Stroke, BorderThickness = new Thickness(1), VerticalAlignment = VerticalAlignment.Center,
            Child = new TextBlock { Text = "Read-only", Foreground = Muted, FontSize = 11 },
        };
        ToolTip.SetTip(_readOnlyBadge, "Runs in plan mode: Claude reads and reports, and changes no files.");
        var promptHead = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto"), Margin = new Thickness(0, 18, 0, 8) };
        var promptLabel = SectionLabel("Prompt");
        promptLabel.Margin = new Thickness(0);
        promptLabel.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(_templateBox, 1);
        Grid.SetColumn(_readOnlyBadge, 2);
        promptHead.Children.Add(promptLabel);
        promptHead.Children.Add(_templateBox);
        promptHead.Children.Add(_readOnlyBadge);

        _task = new TextBox
        {
            AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, FontSize = 12.5, MinHeight = 84, MaxHeight = 180,
            PlaceholderText = "Describe what Claude should do on this PR…",
        };
        ScrollViewer.SetVerticalScrollBarVisibility(_task, ScrollBarVisibility.Auto);
        _task.TextChanged += (_, _) =>
        {
            _taskEdits[_template.Id] = _task.Text ?? "";
            UpdatePreview();
            UpdateEnabled();
        };
        _preview = new SelectableTextBlock { FontFamily = Mono, FontSize = 11, Foreground = Muted, TextWrapping = TextWrapping.Wrap };
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

        _accountBox = new ComboBox { FontSize = 12, MinWidth = 260 };
        _accountRow = LabeledRow("Account", _accountBox, null);
        _accountRow.Margin = new Thickness(0, 14, 0, 0);
        _accountRow.IsVisible = false;

        // ── Footer: status on the left; Copy command and Launch in Perch bottom right ──
        _status = new TextBlock { Foreground = Muted, FontSize = 12, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
        _copyButton = OutlineButton("Copy command");
        ToolTip.SetTip(_copyButton, "Prepare the folder, then copy a command that starts this session in a terminal");
        _copyButton.Click += async (_, _) => await RunAsync(copy: true);
        _launchButton = new Button
        {
            Content = "Launch in Perch", Foreground = Palette.OnAccentBrush, Background = Accent, BorderThickness = new Thickness(0),
            CornerRadius = new CornerRadius(7), Padding = new Thickness(16, 6), FontSize = 12.5, FontWeight = FontWeight.SemiBold,
            Cursor = new Cursor(StandardCursorType.Hand), IsEnabled = false,
        };
        ToolTip.SetTip(_launchButton, "Open a Perch session window on this PR (Ctrl+Enter)");
        _launchButton.Click += async (_, _) => await RunAsync(copy: false);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { _copyButton, _launchButton } };
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
                SectionLabel("How should it be checked out?"), tiles, _checkoutPanel,
                promptHead, _task, _previewToggle, _previewBox,
                _accountRow,
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
            if (e.Source is Visual v && (v is Button or TextBox or ComboBox or RadioButton or SelectableTextBlock or AutoCompleteBox
                || v.FindAncestorOfType<Button>() is not null || v.FindAncestorOfType<TextBox>() is not null
                || v.FindAncestorOfType<ComboBox>() is not null || v.FindAncestorOfType<RadioButton>() is not null)) return;
            if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) BeginMoveDrag(e);
        };
        Content = frame;

        _syncing = true;
        _templateBox.SelectedIndex = 0;
        _syncing = false;
        PickTemplate(_template);
        _cloneNote.Text = $"Looking for your clone of {_repo.Slug}…";
        UpdateWorktreeOptions(null);
        HowChanged();
        // Deferred to Opened, so the render seam can claim the dialog first.
        Opened += (_, _) =>
        {
            if (!_renderOnly) ResolveFolderAsync();
            _task.Focus();
        };
    }

    /// <summary>Headless-render seam: show the dialog with a resolved clone (or a choice, or none) and sample accounts,
    /// touching no files (no folder scan, no sign-in reads — so a render never shows the machine's real accounts).
    /// Call right after construction.</summary>
    internal void SeedForRender(CheckoutMatch match, IReadOnlyList<string>? accounts = null, bool showPreview = false,
        string? template = null, PrWorktreeContext? context = null, bool noCheckout = false, string? worktree = null)
    {
        _renderOnly = true;
        if (template is not null && _templates.FirstOrDefault(t => t.Id == template) is { } t)
        {
            _syncing = true;
            _templateBox.SelectedIndex = _templates.ToList().IndexOf(t);
            _syncing = false;
            PickTemplate(t);
        }
        ApplyMatch(match);
        UpdateWorktreeOptions(context);
        if (worktree is not null)
        {
            _worktreeTouched = true;
            (worktree switch { "existing" => _existingWorktree, "none" => _noWorktree, _ => _newWorktree }).IsChecked = true;
        }
        if (noCheckout) _noCheckout.IsChecked = true;
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

    // ── What the choices add up to ──

    private WorktreeChoice Choice =>
        _existingWorktree.IsChecked == true ? WorktreeChoice.Existing
        : _noWorktree.IsChecked == true ? WorktreeChoice.None
        : WorktreeChoice.New;

    private WorktreeLayout? SelectedLayout =>
        _layoutBox.SelectedIndex >= 0 && _layoutBox.SelectedIndex < _layouts.Count ? _layouts[_layoutBox.SelectedIndex] : _context?.Layout;

    private GitWorktree? SelectedExisting =>
        _existingBox.SelectedIndex >= 0 && _existingBox.SelectedIndex < _existing.Count ? _existing[_existingBox.SelectedIndex] : null;

    private string PrBranch => PrWorktree.BranchFor(_item.Pr.Number);

    // The PR's own head branch, which only means something locally for a same-repo PR.
    private bool IsPrHead(string? branch) =>
        branch is { Length: > 0 } && !_item.Pr.IsCrossRepository && branch == _item.Pr.HeadBranch;

    // The folder the session runs in, for the prompt and the prep step: a Perch scratch folder, a fresh clone, a new
    // worktree, or an existing folder (a worktree or the checkout itself), described by what it has checked out.
    private (PrWorkspace Where, string? LocalBranch, string? Existing) Workspace()
    {
        if (_noCheckout.IsChecked == true) return (PrWorkspace.DiffOnly, null, null);
        if (_folder is null) return (PrWorkspace.Clone, PrBranch, null);
        if (Choice == WorktreeChoice.New) return (PrWorkspace.Worktree, PrBranch, null);
        var (path, branch) = Choice == WorktreeChoice.Existing
            ? (SelectedExisting?.Path, SelectedExisting?.Branch)
            : (_folder, _context?.Set.MainBranch);
        if (branch == PrBranch) return (PrWorkspace.Worktree, PrBranch, path);
        if (IsPrHead(branch)) return (PrWorkspace.ExistingWorktree, null, path);
        return (PrWorkspace.Checkout, null, path);
    }

    private void HowChanged()
    {
        bool checkout = _checkout.IsChecked == true;
        StyleTile(_checkoutTile, checkout);
        StyleTile(_noCheckoutTile, !checkout);
        _checkoutPanel.IsVisible = checkout;
        UpdatePreview();
        UpdateEnabled();
    }

    private void WorktreeChanged()
    {
        _layoutRow.IsVisible = Choice == WorktreeChoice.New && _layouts.Count > 0;
        _existingRow.IsVisible = Choice == WorktreeChoice.Existing && _existing.Count > 0;
        UpdateWorktreeText();
        UpdatePreview();
        UpdateEnabled();
    }

    private void PickTemplate(PrPromptTemplate t)
    {
        _template = t;
        _task.Text = _taskEdits.TryGetValue(t.Id, out var edited) ? edited : PrSessionPrompts.Fill(t.Task, _item.Pr);
        _readOnlyBadge.IsVisible = t.Mode == PrSessionMode.Plan;
        UpdatePreview();
        UpdateEnabled();
    }

    private void UpdatePreview()
    {
        _previewToggle.Content = _previewBox.IsVisible ? "Hide the full prompt" : "Show the full prompt Perch will send";
        if (!_previewBox.IsVisible) return;
        var (where, local, _) = Workspace();
        _preview.Text = PrSessionPrompts.Compose(_task.Text ?? "", _template.Mode, _item.Pr, where, local);
    }

    private void UpdateEnabled()
    {
        bool ready = !_busy && !string.IsNullOrWhiteSpace(_task.Text) && (_noCheckout.IsChecked == true || CheckoutReady);
        _launchButton.IsEnabled = ready;
        _copyButton.IsEnabled = ready;
    }

    // A checkout is ready once the clone search is done (no clone → a fresh one) and an existing worktree, if that's
    // the choice, is picked.
    private bool CheckoutReady =>
        _folder is null ? _resolved : Choice != WorktreeChoice.Existing || SelectedExisting is not null;

    // ── The worktree options, for the chosen clone ──

    // Fills the strategy and existing-worktree pickers from the checkout's worktree picture (null while it's being
    // read, or with no clone), and picks the default unless the user already chose: the PR's own branch or Perch's
    // earlier worktree for it when one exists, else a new worktree.
    private void UpdateWorktreeOptions(PrWorktreeContext? ctx)
    {
        _context = ctx;
        _syncing = true;
        try
        {
            _layouts = ctx is null ? [] : WorktreeLayout.ChoicesFor(ctx.Layout, ctx.Set.Bare, _rememberedLayout);
            _layoutBox.ItemsSource = _layouts.Select(l => LayoutLabel(l, ctx!)).ToList();
            int saved = _layouts.ToList().FindIndex(l => l.Template == _rememberedLayout);
            _layoutBox.SelectedIndex = _layouts.Count == 0 ? -1 : Math.Max(saved, 0);

            _existing = ctx?.Set.Linked ?? [];
            _existingBox.ItemsSource = _existing.Select(w => $"{Path.GetFileName(Path.TrimEndingDirectorySeparator(w.Path))}  ·  {w.Branch ?? "detached"}").ToList();
            int onPr = _existing.ToList().FindIndex(w => w.Branch == PrBranch || IsPrHead(w.Branch));
            _existingBox.SelectedIndex = _existing.Count == 0 ? -1 : Math.Max(onPr, 0);
        }
        finally { _syncing = false; }

        _worktreeOptions.IsVisible = _folder is not null;
        _existingWorktree.IsEnabled = _existing.Count > 0;
        _existingWorktree.Opacity = _existing.Count > 0 ? 1 : 0.5;   // the row's text sets its own colours, so dim it
        _noWorktree.IsVisible = ctx is not { Set.Bare: true };       // a bare repo's root isn't a working tree

        if (!_worktreeTouched || !_existingWorktree.IsEnabled && _existingWorktree.IsChecked == true
            || !_noWorktree.IsVisible && _noWorktree.IsChecked == true)
        {
            bool existingOnPr = _existing.Any(w => w.Branch == PrBranch || IsPrHead(w.Branch));
            bool rootOnPr = ctx is { Set.Bare: false } && IsPrHead(ctx.Set.MainBranch);
            (existingOnPr ? _existingWorktree : rootOnPr ? _noWorktree : _newWorktree).IsChecked = true;
        }
        WorktreeChanged();
    }

    // "web-pr-12 beside your checkout (yours)" — the folder a new worktree for this PR would get under that layout.
    private string LayoutLabel(WorktreeLayout l, PrWorktreeContext ctx)
    {
        var where = Near(l.Render(ctx.Set.Root, $"pr-{_item.Pr.Number}"), ctx.Set.Root);
        return l.Source switch
        {
            WorktreeLayoutSource.Detected => $"{where} (like your other worktrees)",
            WorktreeLayoutSource.IgnoreHint => $"{where} (from your ignore file)",
            WorktreeLayoutSource.Default => $"{where} (Claude Code's default)",
            _ => where,
        };
    }

    private void UpdateWorktreeText()
    {
        var pr = _item.Pr;
        var ctx = _context;
        var layout = SelectedLayout;
        if (ctx is null || layout is null)
            _newDesc.Text = $"Checks out the PR on branch {PrBranch}. Your own working copy isn't touched.";
        else if (ctx.Set.PathOfBranch(PrBranch) is { } earlier)
            _newDesc.Text = $"Reuses your earlier worktree for this PR, {Near(earlier, ctx.Set.Root)}.";
        else
            _newDesc.Text = $"Checks out the PR on branch {PrBranch} in {Near(layout.Render(ctx.Set.Root, $"pr-{pr.Number}"), ctx.Set.Root)}. "
                + "Your own working copy isn't touched.";

        _existingDesc.Text = _existing.Count == 0 ? "This repo has no other worktrees."
            : SelectedExisting is not { } w || Choice != WorktreeChoice.Existing ? "One of the repo's worktrees, as it is."
            : w.Branch == PrBranch ? $"Perch's earlier worktree for this PR, {Near(w.Path, ctx!.Set.Root)}."
            : IsPrHead(w.Branch) ? "On the PR's own branch, so changes are ready to push."
            : $"On {w.Branch ?? "a detached HEAD"}, not the PR's branch. Changes land in that worktree.";

        var main = ctx?.Set.MainBranch;
        _noWorktreeDesc.Text = IsPrHead(main)
            ? $"Your checkout, already on the PR's branch {PrSessionPrompts.OneLine(main!, 60)}. Changes are ready to push."
            : main is { Length: > 0 }
                ? $"Your checkout as it is, on {main}, not the PR's branch. Changes land in your working copy."
                : "Your checkout as it is, not the PR's branch. Changes land in your working copy.";
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

    private static bool PathEquals(string? a, string? b) => a is not null && b is not null && string.Equals(
        Path.TrimEndingDirectorySeparator(a), Path.TrimEndingDirectorySeparator(b),
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    // ── Finding the clone ──

    // Off the UI thread: the remembered folder, else a scan of the folders sessions ran in. The same folders feed the
    // search box's suggestions, the matches first.
    private void ResolveFolderAsync()
    {
        var remembered = _remembered;
        var repo = _repo;
        var known = _knownFolders;
        Task.Run(() =>
        {
            var folders = known();
            var match = !string.IsNullOrWhiteSpace(remembered) && Directory.Exists(remembered)
                ? new CheckoutMatch(remembered, [remembered])
                : RepoCheckoutResolver.Resolve(repo, GitCheckoutScanner.Scan(folders), null, Directory.Exists);
            return (match, folders);
        }).ContinueWith(t => Dispatcher.UIThread.Post(() =>
        {
            if (!IsVisible) return;
            var (match, folders) = t.IsCompletedSuccessfully ? t.Result : (CheckoutMatch.None, (IReadOnlyList<string>)[]);
            _cloneBox.ItemsSource = match.Candidates.Concat(folders)
                .Distinct(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal).ToList();
            ApplyMatch(match);
        }));
    }

    private void ApplyMatch(CheckoutMatch match)
    {
        _resolved = true;
        if (match.Path is { } path)
        {
            SetFolder(path, "Found among your projects. Search to use another clone.");
            return;
        }
        if (match.NeedsChoice)
        {
            SetFolder(match.Candidates[0],
                $"Found {match.Candidates.Count} clones of {_repo.Slug}. Using the first; search to pick another.");
            return;
        }
        SetFolder(null, $"No clone of {_repo.Slug} among your projects. Search or browse for one, or Perch will make a "
            + $"fresh clone in its data folder (on branch {PrBranch}).");
    }

    private void SetFolder(string? folder, string note, bool error = false)
    {
        _folder = folder;
        _syncing = true;
        _cloneBox.Text = folder ?? "";
        _syncing = false;
        _cloneNote.Text = note;
        _cloneNote.Foreground = error ? Palette.ErrorBrush : Muted;
        _worktreeOptions.IsVisible = folder is not null;
        UpdateWorktreeOptions(null);
        UpdatePreview();
        UpdateEnabled();
        if (_renderOnly) return;
        ResolveAccountsAsync(folder);
        if (folder is null) return;
        Task.Run(() => PrWorktree.Inspect(folder)).ContinueWith(t => Dispatcher.UIThread.Post(() =>
        {
            if (IsVisible && _folder == folder) UpdateWorktreeOptions(t.IsCompletedSuccessfully ? t.Result : null);
        }));
    }

    // The search box's text, committed (Enter or leaving the box): use it when it names another folder.
    private void CommitCloneText()
    {
        if (_syncing || _renderOnly) return;
        var text = _cloneBox.Text?.Trim() ?? "";
        if (text.Length == 0 || PathEquals(text, _folder)) return;
        _ = UseFolderAsync(text);
    }

    private async Task BrowseAsync()
    {
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = $"Where is {_repo.Slug} checked out?", AllowMultiple = false,
        });
        if (folders.Count == 0 || folders[0].TryGetLocalPath() is not { } picked) return;
        await UseFolderAsync(picked);
    }

    // A folder the user searched or browsed to: check it's a checkout of this repo before taking it (a wrong folder
    // would only fail later, at the fetch). A linked worktree picked by mistake stands for its repo.
    private async Task UseFolderAsync(string picked)
    {
        if (PathEquals(picked, _folder)) return;
        var repo = _repo;
        var found = await Task.Run(() =>
        {
            if (!Directory.Exists(picked) || GitCheckoutScanner.FindRoot(picked) is not { } at)
                return (Root: (string?)null, Remote: (string?)null);
            var root = GitWorktreeScanner.Read(at)?.Root ?? at;
            return (Root: root, Remote: PrWorktree.RemoteFor(GitCheckoutScanner.ReadRemotes(root), repo));
        });
        if (!IsVisible) return;
        if (found.Root is null)
        {
            _cloneNote.Text = $"{picked} isn't a git checkout.";
            _cloneNote.Foreground = Palette.ErrorBrush;
            return;
        }
        SetFolder(found.Root, found.Remote is null
            ? $"No remote there points at {_repo.Slug}, so a new worktree can't fetch the PR. Use an existing worktree or no worktree, or pick another clone."
            : "", error: found.Remote is null);
        if (found.Remote is null)
        {
            _noWorktree.IsChecked = true;
            _worktreeTouched = true;
        }
    }

    // The accounts this folder may use (the same rules and guardrails as the session launcher), off the UI thread.
    private void ResolveAccountsAsync(string? folder)
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

    // ── Launch / copy ──

    // Prepares the folder (a worktree fetch or a clone can take a while), resolves the account and, for a launch,
    // folder trust; then either starts the session in Perch or copies the terminal command.
    private async Task RunAsync(bool copy)
    {
        if (_busy || !_launchButton.IsEnabled) return;
        var (where, local, existing) = Workspace();
        var root = _folder;
        var choice = Choice;
        var layout = SelectedLayout;
        var prompt = PrSessionPrompts.Compose(_task.Text ?? "", _template.Mode, _item.Pr, where, local);
        SetBusy(true, where switch
        {
            PrWorkspace.Worktree when existing is null => "Preparing the worktree…",
            PrWorkspace.Clone when root is null => "Cloning… (a big repo can take a few minutes)",
            _ => copy ? "Preparing…" : "Starting…",
        });

        var repo = _repo;
        int number = _item.Pr.Number;
        var wt = _noCheckout.IsChecked == true ? await Task.Run(() => PrScratch.Ensure(repo, number))
            : root is null ? await Task.Run(() => PrClone.Ensure(repo, number))
            : choice == WorktreeChoice.New ? await Task.Run(() => PrWorktree.Ensure(root, repo, number, layout))
            : existing is not null ? new PrWorktreeResult(existing, true, null)
            : new PrWorktreeResult(null, false, "Pick the worktree to use.");
        if (!IsVisible) return;
        if (wt.Path is not { } cwd)
        {
            SetBusy(false, wt.Error ?? "Couldn't prepare the folder.");
            return;
        }
        if (root is not null)
        {
            _remember(repo.Slug, root);
            // A strategy the user changed sticks for the repo; going back to the inferred one forgets it.
            if (choice == WorktreeChoice.New && _context is { } ctx && layout is not null)
                _rememberLayout(repo.Slug, layout.Template == ctx.Layout.Template ? null : layout.Template);
        }

        // The account: the guardrails for where the session runs — except a clone or scratch folder, which live in
        // Perch's data folder, take the rules of the user's checkout for that repo (or none).
        string? pickKey = _accountBox.SelectedIndex >= 0 && _accountBox.SelectedIndex < _accountOptions.Count
            ? _accountOptions[_accountBox.SelectedIndex].Key : null;
        var guardFolder = where is PrWorkspace.Clone or PrWorkspace.DiffOnly ? root : cwd;
        bool grantScratch = where == PrWorkspace.DiffOnly;
        var rules = _rules();
        var (configDir, trusted, error) = await Task.Run(() =>
        {
            var set = SessionAccountChoice.Resolve(guardFolder, SessionWindow.ReadSignIns(), rules);
            if (set.GuardrailUnsatisfiable)
                return ((string?)null, false, (string?)"No signed-in account satisfies the account rule for this folder.");
            var pick = set.Options.FirstOrDefault(o => o.Key == pickKey) ?? set.Default;
            var dir = set.InjectRootFor(pick);
            // The scratch folder is Perch's own and holds no code, so it's trusted without asking.
            if (grantScratch && !DirectoryTrust.Evaluate(dir, cwd)) DirectoryTrust.Grant(dir, cwd);
            return (dir, DirectoryTrust.Evaluate(dir, cwd), (string?)null);
        });
        if (!IsVisible) return;
        if (error is not null)
        {
            SetBusy(false, error);
            return;
        }

        var mode = _template.Mode == PrSessionMode.Plan ? "plan" : "acceptEdits";
        var title = PrSessionPrompts.SessionTitle(_item.Pr);
        if (copy)
        {
            // The terminal's claude asks for folder trust itself, so there's no gate here.
            var command = PrSessionCommand.Build(cwd, prompt, mode, configDir, title, PrSessionCommand.DefaultShell);
            try
            {
                if (Clipboard is { } clip) await clip.SetTextAsync(command);
                SetBusy(false, $"Copied. The folder is ready at {cwd}; paste the command into a terminal.");
                _status.Foreground = Muted;
            }
            catch (Exception ex)
            {
                SetBusy(false, $"Couldn't copy the command: {ex.Message}");
            }
            return;
        }

        if (!trusted && !await ResumeGate.ConfirmTrustAsync(this, configDir, cwd))
        {
            SetBusy(false, "Not started: the folder isn't trusted.");
            return;
        }
        if (_launch(new PrSessionLaunch(cwd, prompt, mode, configDir, title)) is { } launchError)
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
        UpdateEnabled();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Escape && !_busy && !_cloneBox.IsDropDownOpen)
        {
            Close();
            e.Handled = true;
        }
        else if (e.Key == Key.Enter && e.KeyModifiers.HasFlag(KeyModifiers.Control) && _launchButton.IsEnabled)
        {
            _ = RunAsync(copy: false);
            e.Handled = true;
        }
        base.OnKeyDown(e);
    }

    // ── Building blocks ──

    private static TextBlock SectionLabel(string text) => new()
    {
        Text = text, Foreground = Fg, FontSize = 12.5, FontWeight = FontWeight.SemiBold, Margin = new Thickness(0, 16, 0, 8),
    };

    private static TextBlock SubLabel(string text) => new()
    {
        Text = text, Foreground = Muted, FontSize = 12, Margin = new Thickness(0, 0, 0, 4),
    };

    // A tile for one of the two "how" choices: a radio filling a bordered box, accented when chosen (StyleTile).
    private static (RadioButton, Border) ChoiceTile(string title, string description)
    {
        var rb = new RadioButton
        {
            GroupName = "how", HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch, Cursor = new Cursor(StandardCursorType.Hand), Padding = new Thickness(6, 0, 0, 0),
            Content = new StackPanel
            {
                Children =
                {
                    new TextBlock { Text = title, Foreground = Fg, FontSize = 13, FontWeight = FontWeight.SemiBold },
                    new TextBlock { Text = description, Foreground = Muted, FontSize = 11.5, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 0) },
                },
            },
        };
        var tile = new Border
        {
            Child = rb, BorderThickness = new Thickness(1.5), CornerRadius = new CornerRadius(8), Padding = new Thickness(10, 8, 12, 10),
        };
        return (rb, tile);
    }

    private static void StyleTile(Border tile, bool on)
    {
        tile.BorderBrush = on ? Accent : Stroke;
        tile.Background = on ? new SolidColorBrush(Palette.Accent) { Opacity = 0.08 } : Brushes.Transparent;
    }

    // A radio row: label (semibold) over a muted description.
    private static RadioButton OptionRow(string group, string label, out TextBlock desc)
    {
        desc = new TextBlock { Foreground = Muted, FontSize = 11.5, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 1, 0, 0) };
        var text = new StackPanel
        {
            Children = { new TextBlock { Text = label, Foreground = Fg, FontSize = 12.5, FontWeight = FontWeight.SemiBold }, desc },
        };
        return new RadioButton
        {
            GroupName = group, Content = text, HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch, Padding = new Thickness(6, 3, 0, 3),
            Cursor = new Cursor(StandardCursorType.Hand),
        };
    }

    // A picker under a radio row, indented to the row's text: "Strategy  [ … ▾ ]".
    private static Grid Indented(string label, Control content)
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*"), Margin = new Thickness(28, 2, 0, 8) };
        var l = new TextBlock { Text = label, Foreground = Muted, FontSize = 12, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0) };
        grid.Children.Add(l);
        Grid.SetColumn(content, 1);
        content.VerticalAlignment = VerticalAlignment.Center;
        grid.Children.Add(content);
        return grid;
    }

    // "Label   content   [action]" on a fixed label column, so Clone and Account line up.
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
