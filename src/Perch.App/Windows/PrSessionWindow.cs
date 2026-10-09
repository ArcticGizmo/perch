using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
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
/// "Start session" on a GitHub dashboard card: layout A of docs/pr-session-dialog-layouts.md (part 5 of
/// docs/github-dashboard-plan.md). The usual case is one decision, confirm and launch, so the workspace reads as a
/// short summary card and the details open only on request:
/// <list type="bullet">
/// <item><b>Workspace</b>: a <c>Checkout | No checkout</c> toggle. No checkout is just prompting, in an empty Perch
/// scratch folder.</item>
/// <item><b>Clone</b>: the folder name pinned, its directory elided from the front. <i>Change</i> opens the session
/// launcher's folder search (<see cref="FolderSearchBox"/>) and Browse in place. With several clones found a
/// "1 of 2" chip says so; with none, Perch will make a fresh clone.</item>
/// <item><b>Worktree</b>: what the session gets ("New · .claude\worktrees\pr-12 · on perch/pr-12"). <i>Change</i>
/// opens <c>New | Existing | None</c> and one row for that choice: the worktree <b>Location</b> (the strategy,
/// inferred from the user's own worktrees, remembered per repo when changed) or the existing worktree to use. A
/// choice that edits a working copy off the PR's branch carries a warning line even while collapsed.</item>
/// <item><b>Account</b>, when there's a choice, beside the folder that decides it.</item>
/// </list>
/// Then the prompt (a quick-prompt picker over an editable task, with the full composed prompt behind a disclosure),
/// and bottom right <b>Copy command</b> (copy a terminal command that does the setup itself, a worktree fetch and add,
/// a clone or a scratch folder, then starts Claude: Perch changes nothing, <see cref="PrSetup"/> +
/// <see cref="PrSessionCommand"/>), a quieter <b>Set up &amp; copy</b> when there's setup to do (Perch makes the
/// folder now; the command just starts Claude there) or <b>Launch in Perch</b> (open a session window named after
/// the PR).
///
/// <para>Folder trust is asked the way a new session asks it, for the final folder: a worktree's code is the PR's,
/// which may come from someone else. The account follows the same rules and guardrails as the session launcher,
/// resolved again for the final folder. All folder IO runs off the UI thread.</para>
/// </summary>
internal sealed class PrSessionWindow : Window
{
    private static readonly IBrush Bg     = Palette.OverlaySurfaceBrush;
    private static readonly IBrush CardBg = Palette.FormBgBrush;
    private static readonly IBrush Stroke = Palette.BorderBrush;
    private static readonly IBrush Fg     = Palette.FgBrush;
    private static readonly IBrush Muted  = Palette.MutedBrush;
    private static readonly IBrush Accent = Palette.AccentBrush;
    private static readonly FontFamily Mono = new("Cascadia Mono, Consolas, Menlo, monospace");

    private const double LabelColumn = 88;
    private const int WtNew = 0, WtExisting = 1, WtNone = 2;

    /// <summary>A worktree strategy as its picker shows it: where this PR's worktree would land, and where the
    /// layout came from.</summary>
    private sealed record LayoutChoice(string Path, string Source);

    private readonly GhPrItem _item;
    private readonly GitRepoRef _repo;
    private readonly Func<IReadOnlyList<string>> _knownFolders;
    private readonly string? _remembered;
    private readonly string? _rememberedLayout;
    private readonly Action<string, string> _remember;
    private readonly Action<string, string?> _rememberLayout;
    private readonly Func<IReadOnlyList<AccountRule>?> _rules;
    private readonly Func<PrSessionLaunch, string?> _launch;

    // Workspace: Checkout | No checkout, and the summary card's rows
    private readonly SegmentedPicker _how;
    private readonly Border _cloneRow, _worktreeRow, _scratchRow, _accountRow;

    // Clone: summary (name pinned, directory elided from the front) or the folder search
    private readonly Grid _cloneSummary, _cloneEditor;
    private readonly TextBlock _cloneName, _cloneDir, _cloneNote;
    private readonly Button _cloneChange, _cloneChip;
    private readonly AutoCompleteBox _cloneBox;
    private bool _cloneEditing;
    private CheckoutMatch? _match;

    // Worktree: summary or New | Existing | None plus one row for the choice
    private readonly Grid _worktreeSummary;
    private readonly StackPanel _worktreeEditor;
    private readonly TextBlock _wtKind, _wtDetail, _consequence;
    private readonly SegmentedPicker _worktree;
    private readonly ComboBox _layoutBox, _existingBox;
    private readonly Grid _layoutRow, _existingRow;
    private IReadOnlyList<WorktreeLayout> _layouts = [];
    private IReadOnlyList<GitWorktree> _existing = [];
    private PrWorktreeContext? _context;          // the chosen checkout's worktrees + inferred layout, read off the UI thread
    private bool _worktreeEditing;
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

    private readonly ComboBox _accountBox;

    private readonly TextBlock _status;
    private readonly Button _launchButton, _copyButton, _setupCopyButton;

    /// <summary>What the footer's buttons do: start the session in Perch; prepare the folder here, then copy the
    /// command that starts Claude in it; or copy everything, setup included, and change nothing.</summary>
    private enum RunKind { Launch, SetUpAndCopy, CopyAll }

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
        Width = 820;   // room for a real clone path and a worktree location on one line
        SizeToContent = SizeToContent.Height;
        ShowInTaskbar = true;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        // ── Header: the PR is the subject, so its title is the strongest text ──
        var close = new Button
        {
            Content = "✕", Foreground = Muted, Background = Brushes.Transparent, BorderThickness = new Thickness(0),
            Padding = new Thickness(4, 0), FontSize = 14, Cursor = new Cursor(StandardCursorType.Hand), VerticalAlignment = VerticalAlignment.Center,
        };
        close.Click += (_, _) => Close();
        var eyebrowRow = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        Grid.SetColumn(close, 1);
        eyebrowRow.Children.Add(Eyebrow("START A SESSION"));
        eyebrowRow.Children.Add(close);

        var prTitle = new TextBlock
        {
            Text = item.Pr.Title.Length > 0 ? item.Pr.Title : "(untitled)", Foreground = Fg, FontSize = 16,
            FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap, MaxLines = 2,
            TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 6, 0, 0),
        };
        var prRepo = new TextBlock
        {
            Text = $"{item.Pr.Repo} #{item.Pr.Number}", Foreground = Muted, FontSize = 12,
            TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 3, 0, 0),
        };
        var tags = new WrapPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 6, 0, 0) };
        if (GitHubAlertsWindow.Branches(item.Pr) is { Length: > 0 } branches)
            tags.Children.Add(new TextBlock
            {
                Text = branches, FontFamily = Mono, FontSize = 11.5, Foreground = Muted, VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 12, 4),
            });
        foreach (var r in item.Reasons) tags.Children.Add(ReasonPill(r));
        tags.IsVisible = tags.Children.Count > 0;
        var header = new StackPanel { Margin = new Thickness(24, 18, 18, 16), Children = { eyebrowRow, prTitle, prRepo, tags } };

        // ── Workspace: Checkout | No checkout on the heading, then the summary card ──
        _how = new SegmentedPicker(["Checkout", "No checkout"], 0);
        _how.Changed += _ => HowChanged();
        var workspaceHead = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Margin = new Thickness(0, 18, 0, 10) };
        var wsLabel = Eyebrow("WORKSPACE");
        wsLabel.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(_how.View, 1);
        workspaceHead.Children.Add(wsLabel);
        workspaceHead.Children.Add(_how.View);

        // Clone, collapsed: the folder name pinned, the directory taking the leading ellipsis, then Change.
        _cloneName = new TextBlock { Foreground = Fg, FontSize = 13, FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center };
        _cloneDir = new TextBlock
        {
            Foreground = Muted, FontFamily = Mono, FontSize = 11.5, VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(10, 0, 0, 0), TextTrimming = TextTrimming.PrefixCharacterEllipsis,
        };
        var clonePath = new DockPanel { LastChildFill = true, VerticalAlignment = VerticalAlignment.Center };
        DockPanel.SetDock(_cloneName, Dock.Left);
        clonePath.Children.Add(_cloneName);
        clonePath.Children.Add(_cloneDir);
        _cloneChip = new Button
        {
            Foreground = Muted, Background = Palette.ButtonBgBrush, BorderThickness = new Thickness(0), CornerRadius = new CornerRadius(999),
            Padding = new Thickness(8, 1), FontSize = 11, Margin = new Thickness(10, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center,
            Cursor = new Cursor(StandardCursorType.Hand), IsVisible = false,
        };
        ToolTip.SetTip(_cloneChip, "Several clones of this repo are among your projects. Pick another");
        _cloneChip.Click += (_, _) => OpenCloneEditor();
        _cloneChange = LinkButton("Change");
        _cloneChange.Margin = new Thickness(12, 0, 0, 0);
        _cloneChange.Click += (_, _) => OpenCloneEditor();
        _cloneSummary = Columns(clonePath, _cloneChip, _cloneChange);

        // Clone, editing: the session launcher's folder search, Browse, Done.
        _cloneBox = new AutoCompleteBox { FontSize = 12.5, HorizontalAlignment = HorizontalAlignment.Stretch, VerticalAlignment = VerticalAlignment.Center };
        FolderSearchBox.Configure(_cloneBox, $"Search your projects for {_repo.Repo}…", Fg, Muted, Palette.FormBgBrush, Stroke,
            FontFamily.Default, Mono, borderless: false);
        // Committed when the suggestions close on a pick or on Enter — not per arrow-key move.
        _cloneBox.DropDownClosed += (_, _) => { if (!_syncing && _cloneBox.SelectedItem is string) CommitCloneText(); };
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
        var cloneDone = LinkButton("Done");
        cloneDone.Margin = new Thickness(12, 0, 0, 0);
        cloneDone.Click += (_, _) => CommitCloneText();
        _cloneEditor = Columns(_cloneBox, browse, cloneDone);
        _cloneEditor.IsVisible = false;

        _cloneNote = new TextBlock { FontSize = 11.5, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(LabelColumn, 6, 0, 0), IsVisible = false };
        _cloneRow = CardRow("Clone", new Panel { Children = { _cloneSummary, _cloneEditor } }, _cloneNote, first: true);

        // Worktree, collapsed: what the session gets, then Change.
        _wtKind = new TextBlock { Foreground = Fg, FontSize = 13, FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center };
        _wtDetail = new TextBlock
        {
            Foreground = Muted, FontFamily = Mono, FontSize = 11.5, VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(10, 0, 0, 0), TextTrimming = TextTrimming.PrefixCharacterEllipsis,
        };
        var wtText = new DockPanel { LastChildFill = true, VerticalAlignment = VerticalAlignment.Center };
        DockPanel.SetDock(_wtKind, Dock.Left);
        wtText.Children.Add(_wtKind);
        wtText.Children.Add(_wtDetail);
        var wtChange = LinkButton("Change");
        wtChange.Margin = new Thickness(12, 0, 0, 0);
        wtChange.Click += (_, _) => { _worktreeEditing = true; WorktreeChanged(); };
        _worktreeSummary = Columns(wtText, wtChange);

        // Worktree, editing: New | Existing | None, then Location (the strategy) or the existing worktree.
        _worktree = new SegmentedPicker(["New", "Existing", "None"], WtNew);
        _worktree.Changed += _ => { _worktreeTouched = true; WorktreeChanged(); };
        ToolTip.SetTip(_worktree.View, "New: a worktree for this PR. Existing: one of the repo's worktrees as it is. None: your checkout as it is.");
        var wtDone = LinkButton("Done");
        wtDone.Click += (_, _) => { _worktreeEditing = false; WorktreeChanged(); };
        var segRow = Columns(_worktree.View, wtDone);
        _layoutBox = new ComboBox { FontSize = 12, HorizontalAlignment = HorizontalAlignment.Stretch, ItemTemplate = LayoutTemplate() };
        _layoutBox.SelectionChanged += (_, _) => { if (!_syncing) { UpdateWorktreeSummary(); UpdatePreview(); } };
        ToolTip.SetTip(_layoutBox, "Where new worktrees go for this repo. Perch follows your existing worktrees; a change is remembered for the repo.");
        _layoutRow = SubRow("Location", _layoutBox);
        _existingBox = new ComboBox { FontSize = 12, HorizontalAlignment = HorizontalAlignment.Stretch };
        _existingBox.SelectionChanged += (_, _) => { if (!_syncing) { UpdateWorktreeSummary(); UpdatePreview(); } };
        _existingRow = SubRow("Worktree", _existingBox);
        _worktreeEditor = new StackPanel { Spacing = 8, IsVisible = false, Children = { segRow, _layoutRow, _existingRow } };

        // The one warning: a choice that edits a working copy off the PR's branch, shown collapsed too.
        _consequence = new TextBlock
        {
            Foreground = Palette.WarnBrush, FontSize = 11.5, TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(LabelColumn, 6, 0, 0), IsVisible = false,
        };
        _worktreeRow = CardRow("Worktree", new Panel { Children = { _worktreeSummary, _worktreeEditor } }, _consequence, first: false);

        // No checkout: the card shrinks to one line (plus Account).
        _scratchRow = new Border
        {
            Padding = new Thickness(16, 12), IsVisible = false,
            Child = new TextBlock
            {
                Text = "Nothing is checked out. Claude reads the PR with gh, in an empty scratch folder.",
                Foreground = Muted, FontSize = 12, TextWrapping = TextWrapping.Wrap,
            },
        };

        _accountBox = new ComboBox { FontSize = 12, MinWidth = 280, HorizontalAlignment = HorizontalAlignment.Left };
        _accountRow = CardRow("Account", _accountBox, null, first: false);
        _accountRow.IsVisible = false;

        var card = new Border
        {
            Background = CardBg, BorderBrush = Stroke, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(10),
            ClipToBounds = true,
            Child = new StackPanel { Children = { _cloneRow, _worktreeRow, _scratchRow, _accountRow } },
        };

        // ── Prompt: the picker beside its heading, the editable task, the full prompt behind a disclosure ──
        _templateBox = new ComboBox { FontSize = 12, MinWidth = 240, VerticalAlignment = VerticalAlignment.Center };
        foreach (var t in _templates) _templateBox.Items.Add(t.Label);
        _templateBox.SelectionChanged += (_, _) =>
        {
            if (!_syncing && _templateBox.SelectedIndex >= 0) PickTemplate(_templates[_templateBox.SelectedIndex]);
        };
        _readOnlyBadge = new Border
        {
            CornerRadius = new CornerRadius(999), Padding = new Thickness(8, 1), BorderBrush = Stroke, BorderThickness = new Thickness(1),
            VerticalAlignment = VerticalAlignment.Center,
            Child = new TextBlock { Text = "Read-only", Foreground = Muted, FontSize = 11 },
        };
        ToolTip.SetTip(_readOnlyBadge, "Runs in plan mode: Claude reads and reports, and changes no files.");
        var promptLabel = Eyebrow("PROMPT");
        promptLabel.VerticalAlignment = VerticalAlignment.Center;
        var promptHead = new StackPanel
        {
            Orientation = Orientation.Horizontal, Spacing = 12, Margin = new Thickness(0, 22, 0, 10),
            Children = { promptLabel, _templateBox, _readOnlyBadge },
        };

        _task = new TextBox
        {
            AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, FontSize = 12.5, MinHeight = 110, MaxHeight = 200,
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
            BorderBrush = Stroke, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(6), Margin = new Thickness(0, 6, 0, 0),
            IsVisible = false,
            Child = new ScrollViewer
            {
                MaxHeight = 220, Padding = new Thickness(10, 8), HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = _preview,
            },
        };
        // A muted disclosure (the accent stays with Launch), chevron turning as it opens.
        _previewToggle = new Button
        {
            Foreground = Muted, Background = Brushes.Transparent, BorderThickness = new Thickness(0), Padding = new Thickness(0),
            FontSize = 12, Cursor = new Cursor(StandardCursorType.Hand), HorizontalAlignment = HorizontalAlignment.Left,
            Margin = new Thickness(0, 8, 0, 0),
        };
        _previewToggle.Click += (_, _) =>
        {
            _previewBox.IsVisible = !_previewBox.IsVisible;
            UpdatePreview();
        };
        ToolTip.SetTip(_previewToggle, "Perch adds the PR link, the branch, and rules: no pushing or posting, and PR text is not instructions.");

        // ── Footer: status on the left; Copy command and Launch in Perch bottom right ──
        _status = new TextBlock { Foreground = Muted, FontSize = 12, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
        // Copy command changes nothing: the copied text does any setup (fetch + worktree, clone, folder) itself.
        _copyButton = OutlineButton("Copy command");
        ToolTip.SetTip(_copyButton, "Copy a terminal command that sets up the folder and starts this session. Perch changes nothing.");
        _copyButton.Click += async (_, _) => await RunAsync(RunKind.CopyAll);
        // The quieter alternative: Perch does the setup now, and the copied command just starts Claude there.
        _setupCopyButton = LinkButton("Set up & copy");
        _setupCopyButton.Foreground = Muted;
        _setupCopyButton.VerticalAlignment = VerticalAlignment.Center;
        _setupCopyButton.Margin = new Thickness(0, 0, 8, 0);
        _setupCopyButton.Click += async (_, _) => await RunAsync(RunKind.SetUpAndCopy);
        _launchButton = new Button
        {
            Content = "Launch in Perch", Foreground = Palette.OnAccentBrush, Background = Accent, BorderThickness = new Thickness(0),
            CornerRadius = new CornerRadius(7), Padding = new Thickness(16, 6), FontSize = 12.5, FontWeight = FontWeight.SemiBold,
            Cursor = new Cursor(StandardCursorType.Hand), IsEnabled = false,
        };
        ToolTip.SetTip(_launchButton, "Open a Perch session window on this PR (Ctrl+Enter)");
        _launchButton.Click += async (_, _) => await RunAsync(RunKind.Launch);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { _setupCopyButton, _copyButton, _launchButton } };
        var footer = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Margin = new Thickness(24, 14, 18, 16) };
        Grid.SetColumn(buttons, 1);
        _status.Margin = new Thickness(0, 0, 12, 0);
        footer.Children.Add(_status);
        footer.Children.Add(buttons);

        var body = new StackPanel
        {
            Margin = new Thickness(24, 0, 24, 18),
            Children = { workspaceHead, card, promptHead, _task, _previewToggle, _previewBox },
        };

        var root = new StackPanel
        {
            Children =
            {
                header,
                new Border { Height = 1, Background = Stroke },
                body,
                new Border { Height = 1, Background = Stroke },
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
            if (e.Source is Visual v && (v is Button or TextBox or ComboBox or SelectableTextBlock or AutoCompleteBox
                || v.FindAncestorOfType<Button>() is not null || v.FindAncestorOfType<TextBox>() is not null
                || v.FindAncestorOfType<ComboBox>() is not null)) return;
            if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) BeginMoveDrag(e);
        };
        Content = frame;

        _syncing = true;
        _templateBox.SelectedIndex = 0;
        _syncing = false;
        PickTemplate(_template);
        UpdateCloneSummary();
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
    /// <paramref name="worktree"/> picks "new" / "existing" / "none"; <paramref name="editWorktree"/> and
    /// <paramref name="editClone"/> open those rows' editors. Call right after construction.</summary>
    internal void SeedForRender(CheckoutMatch match, IReadOnlyList<string>? accounts = null, bool showPreview = false,
        string? template = null, PrWorktreeContext? context = null, bool noCheckout = false, string? worktree = null,
        bool editWorktree = false, bool editClone = false)
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
            _worktree.Selected = worktree switch { "existing" => WtExisting, "none" => WtNone, _ => WtNew };
        }
        _worktreeEditing = editWorktree;
        WorktreeChanged();
        if (editClone) OpenCloneEditor();
        if (noCheckout) { _how.Selected = 1; HowChanged(); }
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

    private bool NoCheckout => _how.Selected == 1;

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
        if (NoCheckout) return (PrWorkspace.DiffOnly, null, null);
        if (_folder is null) return (PrWorkspace.Clone, PrBranch, null);
        if (_worktree.Selected == WtNew) return (PrWorkspace.Worktree, PrBranch, null);
        var (path, branch) = _worktree.Selected == WtExisting
            ? (SelectedExisting?.Path, SelectedExisting?.Branch)
            : (_folder, _context?.Set.MainBranch);
        if (branch == PrBranch) return (PrWorkspace.Worktree, PrBranch, path);
        if (IsPrHead(branch)) return (PrWorkspace.ExistingWorktree, null, path);
        return (PrWorkspace.Checkout, null, path);
    }

    private void HowChanged()
    {
        _cloneRow.IsVisible = !NoCheckout;
        _worktreeRow.IsVisible = !NoCheckout && _folder is not null;
        _scratchRow.IsVisible = NoCheckout;
        UpdatePreview();
        UpdateEnabled();
    }

    private void WorktreeChanged()
    {
        _worktreeSummary.IsVisible = !_worktreeEditing;
        _worktreeEditor.IsVisible = _worktreeEditing;
        _layoutRow.IsVisible = _worktree.Selected == WtNew && _layouts.Count > 0;
        _existingRow.IsVisible = _worktree.Selected == WtExisting && _existing.Count > 0;
        UpdateWorktreeSummary();
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
        _previewToggle.Content = _previewBox.IsVisible ? "▾  Full prompt" : "▸  Full prompt";
        if (!_previewBox.IsVisible) return;
        var (where, local, _) = Workspace();
        _preview.Text = PrSessionPrompts.Compose(_task.Text ?? "", _template.Mode, _item.Pr, where, local);
    }

    private void UpdateEnabled()
    {
        bool ready = !_busy && !string.IsNullOrWhiteSpace(_task.Text) && (NoCheckout || CheckoutReady);
        _launchButton.IsEnabled = ready;
        _copyButton.IsEnabled = ready;
        _setupCopyButton.IsEnabled = ready;
        // Only worth offering when there's a folder to make; otherwise it's the same as Copy command.
        _setupCopyButton.IsVisible = NeedsSetup;
        ToolTip.SetTip(_setupCopyButton, NoCheckout ? "Make the scratch folder now, then copy just the command that starts Claude in it"
            : _folder is null ? "Clone the repo now, then copy just the command that starts Claude in the clone"
            : "Create the worktree now, then copy just the command that starts Claude in it");
    }

    // Whether the chosen folder has to be made first: a scratch folder, a clone, or a new worktree (unless Perch's
    // earlier one for this PR is there to reuse).
    private bool NeedsSetup =>
        NoCheckout || _folder is null
        || _worktree.Selected == WtNew && _context?.Set.PathOfBranch(PrBranch) is null;

    // A checkout is ready once the clone search is done (no clone → a fresh one), no folder edit is half-typed, and an
    // existing worktree, if that's the choice, is picked.
    private bool CheckoutReady =>
        !_cloneEditing && (_folder is null ? _resolved : _worktree.Selected != WtExisting || SelectedExisting is not null);

    // ── The clone row ──

    private void UpdateCloneSummary()
    {
        _cloneSummary.IsVisible = !_cloneEditing;
        _cloneEditor.IsVisible = _cloneEditing;
        _cloneChip.IsVisible = false;
        ToolTip.SetTip(_cloneSummary, null);
        if (!_resolved)
        {
            _cloneName.Text = "Looking…";
            _cloneName.Foreground = Muted;
            _cloneDir.Text = $"for your clone of {_repo.Slug}";
            _cloneChange.IsVisible = false;
            return;
        }
        _cloneChange.IsVisible = true;
        if (_folder is not { } folder)
        {
            _cloneName.Text = "None found.";
            _cloneName.Foreground = Fg;
            _cloneDir.Text = $"Perch will make a fresh clone on {PrBranch}";
            _cloneChange.Content = "Find…";
            return;
        }
        var trimmed = Path.TrimEndingDirectorySeparator(folder);
        _cloneName.Text = Path.GetFileName(trimmed) is { Length: > 0 } n ? n : trimmed;
        _cloneName.Foreground = Fg;
        _cloneDir.Text = Path.GetDirectoryName(trimmed) ?? "";
        _cloneChange.Content = "Change";
        ToolTip.SetTip(_cloneSummary, folder);
        // Several clones found: say which of them this is, one click from picking another.
        if (_match is { Candidates.Count: > 1 } m && m.Candidates.ToList().FindIndex(c => PathEquals(c, folder)) is >= 0 and var i)
        {
            _cloneChip.Content = $"{i + 1} of {m.Candidates.Count}";
            _cloneChip.IsVisible = true;
        }
    }

    private void OpenCloneEditor()
    {
        _cloneEditing = true;
        _syncing = true;
        _cloneBox.Text = _folder ?? "";
        _syncing = false;
        UpdateCloneSummary();
        UpdateEnabled();
        if (_renderOnly) return;
        Dispatcher.UIThread.Post(() =>
        {
            _cloneBox.Focus();
            _cloneBox.IsDropDownOpen = true;
        }, DispatcherPriority.Input);
    }

    private void CloseCloneEditor()
    {
        _cloneEditing = false;
        UpdateCloneSummary();
        UpdateEnabled();
    }

    private void SetCloneNote(string? text, IBrush? brush = null)
    {
        _cloneNote.Text = text ?? "";
        _cloneNote.Foreground = brush ?? Muted;
        _cloneNote.IsVisible = !string.IsNullOrEmpty(text);
    }

    // ── The worktree row ──

    // Fills the Location and existing-worktree pickers from the checkout's worktree picture (null while it's being
    // read, or with no clone), and picks the default unless the user already chose: the PR's own branch or Perch's
    // earlier worktree for it when one exists, else a new worktree.
    private void UpdateWorktreeOptions(PrWorktreeContext? ctx)
    {
        _context = ctx;
        _syncing = true;
        try
        {
            _layouts = ctx is null ? [] : WorktreeLayout.ChoicesFor(ctx.Layout, ctx.Set.Bare, _rememberedLayout);
            _layoutBox.ItemsSource = _layouts.Select(l => Describe(l, ctx!)).ToList();
            int saved = _layouts.ToList().FindIndex(l => l.Template == _rememberedLayout);
            _layoutBox.SelectedIndex = _layouts.Count == 0 ? -1 : Math.Max(saved, 0);

            _existing = ctx?.Set.Linked ?? [];
            _existingBox.ItemsSource = _existing.Select(w => $"{Leaf(w.Path)}  ·  {w.Branch ?? "detached"}").ToList();
            int onPr = _existing.ToList().FindIndex(w => w.Branch == PrBranch || IsPrHead(w.Branch));
            _existingBox.SelectedIndex = _existing.Count == 0 ? -1 : Math.Max(onPr, 0);
        }
        finally { _syncing = false; }

        _worktreeRow.IsVisible = !NoCheckout && _folder is not null;
        _worktree.SetEnabled(WtExisting, _existing.Count > 0, "This repo has no other worktrees");
        bool bare = ctx is { Set.Bare: true };
        _worktree.SetVisible(WtNone, !bare);                     // a bare repo's root isn't a working tree

        if (!_worktreeTouched || _existing.Count == 0 && _worktree.Selected == WtExisting || bare && _worktree.Selected == WtNone)
        {
            bool existingOnPr = _existing.Any(w => w.Branch == PrBranch || IsPrHead(w.Branch));
            bool rootOnPr = ctx is { Set.Bare: false } && IsPrHead(ctx.Set.MainBranch);
            _worktree.Selected = existingOnPr ? WtExisting : rootOnPr ? WtNone : WtNew;
        }
        WorktreeChanged();
    }

    // What a layout means for this PR: the folder its worktree would get, and where the layout came from.
    private LayoutChoice Describe(WorktreeLayout l, PrWorktreeContext ctx) => new(
        RelPath(l.Render(ctx.Set.Root, $"pr-{_item.Pr.Number}"), ctx.Set.Root),
        l.Source switch
        {
            WorktreeLayoutSource.Detected => "matches yours",
            WorktreeLayoutSource.IgnoreHint => "from your ignore file",
            WorktreeLayoutSource.Default => "Claude Code default",
            _ when l.Template == WorktreeLayout.DefaultTemplate => "Claude Code default",
            _ when l.Template == _rememberedLayout => "your pick for this repo",
            _ => "",
        });

    // The Location picker's rows: the path (a path, so its tail stays legible) with the source muted on the right.
    private static FuncDataTemplate<LayoutChoice> LayoutTemplate() => new((c, _) =>
    {
        var source = new TextBlock { Text = c?.Source ?? "", Foreground = Muted, FontSize = 11.5, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 0, 0) };
        DockPanel.SetDock(source, Dock.Right);
        var path = new TextBlock
        {
            Text = c?.Path ?? "", Foreground = Fg, FontFamily = Mono, FontSize = 12, VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.PrefixCharacterEllipsis,
        };
        return new DockPanel { LastChildFill = true, Children = { source, path } };
    }, supportsRecycling: true);

    // The collapsed worktree line ("New · .claude\worktrees\pr-12 · on perch/pr-12") and the warning under it.
    private void UpdateWorktreeSummary()
    {
        var ctx = _context;
        switch (_worktree.Selected)
        {
            case WtExisting when SelectedExisting is { } w:
                _wtKind.Text = "Existing";
                _wtDetail.Text = $"{(ctx is null ? w.Path : RelPath(w.Path, ctx.Set.Root))} · on {w.Branch ?? "a detached HEAD"}";
                break;
            case WtExisting:
                _wtKind.Text = "Existing";
                _wtDetail.Text = "pick a worktree";
                break;
            case WtNone:
                _wtKind.Text = "Your checkout";
                _wtDetail.Text = ctx?.Set.MainBranch is { Length: > 0 } main ? $"on {main}" : "as it is";
                break;
            default:
                _wtKind.Text = "New";
                _wtDetail.Text = ctx is null || SelectedLayout is not { } layout ? $"on {PrBranch}"
                    : ctx.Set.PathOfBranch(PrBranch) is { } earlier ? $"{RelPath(earlier, ctx.Set.Root)} · your earlier one, on {PrBranch}"
                    : $"{RelPath(layout.Render(ctx.Set.Root, $"pr-{_item.Pr.Number}"), ctx.Set.Root)} · on {PrBranch}";
                break;
        }

        // Only a choice that edits a working copy that isn't on the PR's branch earns the warning.
        var (where, _, path) = Workspace();
        bool warn = !NoCheckout && _folder is not null && where == PrWorkspace.Checkout && path is not null;
        if (warn)
        {
            var branch = _worktree.Selected == WtExisting ? SelectedExisting?.Branch : ctx?.Set.MainBranch;
            var on = branch is { Length: > 0 } ? $"On {branch}, not the PR's branch" : "Not on the PR's branch";
            _consequence.Text = _worktree.Selected == WtExisting
                ? $"⚠  {on}: edits land in that worktree."
                : $"⚠  {on}: edits land in your working copy.";
        }
        _consequence.IsVisible = warn;
    }

    // A worktree path said relative to the checkout so it fits a line: ".claude\worktrees\pr-12" inside it,
    // "..\acme-api-pr-12" beside it, else the full path.
    private static string RelPath(string path, string root)
    {
        var r = Path.TrimEndingDirectorySeparator(root);
        var cmp = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (path.StartsWith(r + Path.DirectorySeparatorChar, cmp))
            return path[(r.Length + 1)..];
        if (Path.GetDirectoryName(r) is { } parent && path.StartsWith(parent + Path.DirectorySeparatorChar, cmp))
            return ".." + Path.DirectorySeparatorChar + path[(parent.Length + 1)..];
        return path;
    }

    private static string Leaf(string path) =>
        Path.GetFileName(Path.TrimEndingDirectorySeparator(path)) is { Length: > 0 } n ? n : path;

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

    // Several clones: the first is used, and the "1 of 2" chip offers the rest.
    private void ApplyMatch(CheckoutMatch match)
    {
        _match = match;
        _resolved = true;
        SetFolder(match.Path ?? (match.NeedsChoice ? match.Candidates[0] : null));
    }

    private void SetFolder(string? folder, string? note = null, IBrush? noteBrush = null)
    {
        _folder = folder;
        _cloneEditing = false;
        SetCloneNote(note, noteBrush);
        UpdateCloneSummary();
        UpdateWorktreeOptions(null);
        HowChanged();
        if (_renderOnly) return;
        ResolveAccountsAsync(folder);
        if (folder is null) return;
        Task.Run(() => PrWorktree.Inspect(folder)).ContinueWith(t => Dispatcher.UIThread.Post(() =>
        {
            if (IsVisible && _folder == folder) UpdateWorktreeOptions(t.IsCompletedSuccessfully ? t.Result : null);
        }));
    }

    // The search box's text, committed (a pick, Enter or Done): use it when it names another folder, else just close.
    private void CommitCloneText()
    {
        if (_syncing || _renderOnly) return;
        var text = _cloneBox.Text?.Trim() ?? "";
        if (text.Length == 0 || PathEquals(text, _folder))
        {
            SetCloneNote(null);
            CloseCloneEditor();
            return;
        }
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
        if (PathEquals(picked, _folder)) { CloseCloneEditor(); return; }
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
            SetCloneNote($"{picked} isn't a git checkout.", Palette.ErrorBrush);   // the editor stays open to try again
            return;
        }
        if (found.Remote is null)
        {
            // Without a remote naming the repo a new worktree can't fetch the PR; the checkout as it is still works.
            _worktreeTouched = true;
            _worktree.Selected = WtNone;
        }
        SetFolder(found.Root, found.Remote is null
            ? $"No remote there points at {_repo.Slug}, so a new worktree can't fetch the PR."
            : null, Palette.WarnBrush);
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

    // Launch and "Set up & copy" prepare the folder here (a worktree fetch or a clone can take a while); "Copy command"
    // only works out the setup steps, reading the repo but changing nothing, and copies them ahead of the command.
    // Then the account and, for a launch, folder trust; then the session starts in Perch or the command is copied.
    private async Task RunAsync(RunKind run)
    {
        if (_busy || !_launchButton.IsEnabled) return;
        var (where, local, existing) = Workspace();
        var root = _folder;
        bool noCheckout = NoCheckout;
        bool newWorktree = !noCheckout && root is not null && _worktree.Selected == WtNew;
        var layout = SelectedLayout;
        var prompt = PrSessionPrompts.Compose(_task.Text ?? "", _template.Mode, _item.Pr, where, local);
        SetBusy(true, run == RunKind.CopyAll ? "Working out the setup…" : where switch
        {
            PrWorkspace.Worktree when newWorktree => "Preparing the worktree…",
            PrWorkspace.Clone => "Cloning… (a big repo can take a few minutes)",
            _ => run == RunKind.Launch ? "Starting…" : "Preparing…",
        });

        var repo = _repo;
        int number = _item.Pr.Number;
        string cwd;
        IReadOnlyList<PrSetupStep> steps = [];
        if (run == RunKind.CopyAll)
        {
            var (plan, planError) = await Task.Run(() => PlanSetup(noCheckout, root, newWorktree, existing, layout, repo, number));
            if (!IsVisible) return;
            if (plan is null)
            {
                SetBusy(false, planError ?? "Couldn't work out the setup.");
                return;
            }
            (cwd, steps) = (plan.Path, plan.Steps);
        }
        else
        {
            var wt = noCheckout ? await Task.Run(() => PrScratch.Ensure(repo, number))
                : root is null ? await Task.Run(() => PrClone.Ensure(repo, number))
                : newWorktree ? await Task.Run(() => PrWorktree.Ensure(root, repo, number, layout))
                : existing is not null ? new PrWorktreeResult(existing, true, null)
                : new PrWorktreeResult(null, false, "Pick the worktree to use.");
            if (!IsVisible) return;
            if (wt.Path is null)
            {
                SetBusy(false, wt.Error ?? "Couldn't prepare the folder.");
                return;
            }
            cwd = wt.Path;
        }
        if (root is not null && !noCheckout)
        {
            _remember(repo.Slug, root);
            // A Location the user changed sticks for the repo; going back to the inferred one forgets it.
            if (newWorktree && _context is { } ctx && layout is not null)
                _rememberLayout(repo.Slug, layout.Template == ctx.Layout.Template ? null : layout.Template);
        }

        // The account: the guardrails for where the session runs — except a clone or scratch folder, which live in
        // Perch's data folder, take the rules of the user's checkout for that repo (or none).
        string? pickKey = _accountBox.SelectedIndex >= 0 && _accountBox.SelectedIndex < _accountOptions.Count
            ? _accountOptions[_accountBox.SelectedIndex].Key : null;
        var guardFolder = where is PrWorkspace.Clone or PrWorkspace.DiffOnly ? root : cwd;
        // Granting trust writes Claude's settings, so "Copy command" (change nothing) leaves it to the terminal's claude.
        bool grantScratch = where == PrWorkspace.DiffOnly && run != RunKind.CopyAll;
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
        if (run != RunKind.Launch)
        {
            // The terminal's claude asks for folder trust itself, so there's no gate here.
            var command = PrSessionCommand.Build(cwd, prompt, mode, configDir, title, PrSessionCommand.DefaultShell, steps);
            try
            {
                if (Clipboard is { } clip) await clip.SetTextAsync(command);
                SetBusy(false, run == RunKind.SetUpAndCopy
                    ? $"Copied. The folder is ready at {cwd}; paste the command into a terminal."
                    : steps.Count > 0
                        ? $"Copied. Nothing was changed: the command sets up {Leaf(cwd)} first, then starts Claude there."
                        : "Copied. Paste it into a terminal.");
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

    // "Copy command"'s setup, worked out off the UI thread from what's on disk, reading only: the scratch folder or
    // clone (and whether it's there yet), or the new worktree (the repo's worktrees and layout, the remote naming the
    // PR's repo, the exclude file). An existing worktree or the checkout as it is needs no setup.
    private static (PrSetupPlan? Plan, string? Error) PlanSetup(bool noCheckout, string? root, bool newWorktree,
        string? existing, WorktreeLayout? layout, GitRepoRef repo, int number)
    {
        try
        {
            var baseDir = PrWorkspaceFolders.Base;
            if (noCheckout)
                return (PrSetup.Scratch(repo, number, baseDir,
                    Directory.Exists(PrWorkspaceFolders.For(baseDir, "pr-scratch", repo, number))), null);
            if (root is null)
            {
                var dir = PrWorkspaceFolders.For(baseDir, "pr-clones", repo, number);
                bool cloned = Directory.Exists(Path.Combine(dir, ".git"));
                if (!cloned && Directory.Exists(dir) && Directory.EnumerateFileSystemEntries(dir).Any())
                    return (null, $"{dir} already exists and isn't a clone");
                return PrSetup.Clone(repo, number, baseDir, cloned) is { } c ? (c, null) : (null, $"Can't clone {repo.Slug}");
            }
            if (!newWorktree)
                return existing is not null ? (new PrSetupPlan(existing, []), null) : (null, "Pick the worktree to use.");

            if (PrWorktree.Inspect(root) is not { } inspected) return (null, "That folder isn't a git checkout");
            var ctx = layout is null ? inspected : inspected with { Layout = layout };
            if (PrWorktree.RemoteFor(GitCheckoutScanner.ReadRemotes(ctx.Set.Root), repo) is not { } remote)
                return (null, $"No remote in {ctx.Set.Root} points at {repo.Slug}");
            var common = GitWorktreeScanner.CommonDir(ctx.Set.Root);
            var excludePath = common is null ? null : Path.Combine(common, "info", "exclude");
            var exclude = excludePath is not null && File.Exists(excludePath) ? File.ReadAllText(excludePath) : null;
            // The exclude step only when git's info folder is there to append to (it is in any normal repo).
            if (common is not null && !Directory.Exists(Path.Combine(common, "info"))) common = null;
            return PrSetup.Worktree(ctx, remote, number, common, exclude) is { } w ? (w, null) : (null, "Couldn't plan the worktree.");
        }
        catch (Exception ex)
        {
            return (null, ex.Message);
        }
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
            // Esc backs out of an open editor first, then closes.
            if (_cloneEditing) { SetCloneNote(null); CloseCloneEditor(); }
            else if (_worktreeEditing) { _worktreeEditing = false; WorktreeChanged(); }
            else Close();
            e.Handled = true;
        }
        else if (e.Key == Key.Enter && e.KeyModifiers.HasFlag(KeyModifiers.Control) && _launchButton.IsEnabled)
        {
            _ = RunAsync(RunKind.Launch);
            e.Handled = true;
        }
        base.OnKeyDown(e);
    }

    // ── Building blocks ──

    // A small-caps section heading: START A SESSION, WORKSPACE, PROMPT.
    private static TextBlock Eyebrow(string text) => new()
    {
        Text = text, Foreground = Muted, FontSize = 11, FontWeight = FontWeight.SemiBold, LetterSpacing = 1.5,
    };

    // A row of the workspace card: "Label  content", on the shared label column, an optional line under it, and a
    // rule above every row but the first.
    private static Border CardRow(string label, Control content, Control? under, bool first)
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions($"{LabelColumn},*") };
        var l = new TextBlock { Text = label, Foreground = Muted, FontSize = 12.5, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 6, 0, 0) };
        grid.Children.Add(l);
        Grid.SetColumn(content, 1);
        content.VerticalAlignment = VerticalAlignment.Center;
        content.MinHeight = 28;
        grid.Children.Add(content);
        var stack = new StackPanel { Children = { grid } };
        if (under is not null) stack.Children.Add(under);
        return new Border
        {
            Child = stack, Padding = new Thickness(16, 10), BorderBrush = Stroke,
            BorderThickness = new Thickness(0, first ? 0 : 1, 0, 0),
        };
    }

    // An inline editor row under the worktree toggle: "Location  [ … ▾ ]".
    private static Grid SubRow(string label, Control content)
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*") };
        grid.Children.Add(new TextBlock
        {
            Text = label, Foreground = Muted, FontSize = 12, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0),
        });
        Grid.SetColumn(content, 1);
        content.VerticalAlignment = VerticalAlignment.Center;
        grid.Children.Add(content);
        return grid;
    }

    // The fill control first, then fixed controls on the right.
    private static Grid Columns(Control fill, params Control[] right)
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*" + string.Concat(right.Select(_ => ",Auto"))) };
        grid.Children.Add(fill);
        for (int i = 0; i < right.Length; i++)
        {
            Grid.SetColumn(right[i], i + 1);
            right[i].VerticalAlignment = VerticalAlignment.Center;
            grid.Children.Add(right[i]);
        }
        return grid;
    }

    private static Control ReasonPill(GhAlertReason r)
    {
        var c = Views.OverlayCanvas.GitHubKindColor(r.Kind);
        return new Border
        {
            CornerRadius = new CornerRadius(999), Padding = new Thickness(8, 2), Margin = new Thickness(0, 0, 6, 4),
            Background = new SolidColorBrush(c) { Opacity = 0.16 }, VerticalAlignment = VerticalAlignment.Center,
            Child = new TextBlock { Text = r.Text, FontSize = 11.5, FontWeight = FontWeight.SemiBold, Foreground = new SolidColorBrush(c) },
        };
    }

    private static Button LinkButton(string text) => new()
    {
        Content = text, Foreground = Accent, Background = Brushes.Transparent, BorderThickness = new Thickness(0),
        Padding = new Thickness(0), FontSize = 12, Cursor = new Cursor(StandardCursorType.Hand),
        HorizontalAlignment = HorizontalAlignment.Left,
    };

    private static Button OutlineButton(string text) => new()
    {
        Content = text, Foreground = Fg, Background = Brushes.Transparent, BorderBrush = Stroke, BorderThickness = new Thickness(1),
        CornerRadius = new CornerRadius(7), Padding = new Thickness(12, 5), FontSize = 12, Cursor = new Cursor(StandardCursorType.Hand),
    };
}
