using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.VisualTree;
using Perch.Avalonia.Services;
using Perch.Avalonia.Theming;
using Perch.Data;

namespace Perch.Avalonia.Windows;

/// <summary>
/// The GitHub dashboard, opened by clicking the overlay's GitHub strip: a launch centre for your open pull requests.
/// Each PR is a card on a sunken page: its repo, number, author and age, the title, the reasons it needs you
/// ("Review requested", "Changes requested by alice", "Ready to merge" …) and its branches, with the actions bottom
/// right: "Dismiss" (hides it until its state changes), "Open in GitHub" and "Start session" (opens
/// <see cref="PrSessionWindow"/>). Tabs switch between the PRs needing you (the default), every open PR that involves
/// you, and the ones you dismissed (each with "Restore"). A toolbar picks the grouping (repo / reason / role / none),
/// the card order and an "Updated within" age window.
///
/// <para>Reused via <c>WindowHost.ShowOrFocus</c> (<see cref="Retarget"/> re-renders). It owns no data: it renders
/// <see cref="GitHubAlertsMonitorHost.Current"/> and re-renders on the host's <c>Changed</c>. The view options live
/// on the host (the age window trims the strip too), which persists them through the App. Opening a PR marks it
/// seen through the host, which reclassifies at once — so a PR that only had new comments drops out of "Needs
/// you" as you go to read them.</para>
/// </summary>
internal sealed class GitHubAlertsWindow : Window
{
    private static readonly IBrush Page   = Palette.SurfaceSunkenBrush;   // the window behind the cards
    private static readonly IBrush CardBg = Palette.FormBgBrush;
    private static readonly IBrush Stroke = Palette.BorderBrush;
    private static readonly IBrush Fg     = Palette.FgBrush;
    private static readonly IBrush Muted  = Palette.MutedBrush;
    private static readonly IBrush Accent = Palette.AccentBrush;
    private static readonly FontFamily Mono = new("Cascadia Mono, Consolas, Menlo, monospace");

    // The toolbar pickers' choices, index-aligned with their ComboBox items.
    private static readonly (GhGroupBy Value, string Label)[] GroupChoices =
        [(GhGroupBy.Repo, "Repo"), (GhGroupBy.Reason, "Reason"), (GhGroupBy.Role, "Role"), (GhGroupBy.None, "None")];
    private static readonly (GhSortBy Value, string Label)[] SortChoices =
        [(GhSortBy.Urgency, "Most urgent"), (GhSortBy.Updated, "Recently updated"), (GhSortBy.Oldest, "Oldest first")];

    private readonly GitHubAlertsMonitorHost _host;
    private readonly TextBlock _subhead;
    private readonly Button _refresh;
    private readonly Button _needsYouTab, _allTab, _dismissedTab;
    private readonly TextBox _search;
    private readonly ComboBox _groupBox, _sortBox, _ageBox;
    private readonly Button _includeDismissedToggle;
    private readonly StackPanel _list = new();
    private GhView _view = GhView.NeedsYou;
    private bool _includeDismissed;
    private bool _syncingPickers;

    public GitHubAlertsWindow(GitHubAlertsMonitorHost host)
    {
        _host = host;

        Title = "GitHub dashboard";
        WindowDecorations = WindowDecorations.None;
        Background = Brushes.Transparent;
        TransparencyLevelHint = [WindowTransparencyLevel.Transparent];
        CanResize = false;
        Width = 980;
        Height = 760;
        ShowInTaskbar = true;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;

        // ── Header (draggable): title + status line on the left; search, Refresh and close on the right ──
        var heading = new TextBlock { Text = "GitHub dashboard", Foreground = Fg, FontWeight = FontWeight.Bold, FontSize = 18 };
        _subhead = new TextBlock { Foreground = Muted, FontSize = 12, Margin = new Thickness(0, 2, 0, 0), TextTrimming = TextTrimming.CharacterEllipsis };
        _search = new TextBox
        {
            PlaceholderText = "Search title, repo, author, #number…", FontSize = 12.5, Width = 300,
            VerticalContentAlignment = VerticalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
        };
        _search.TextChanged += (_, _) => Refresh();
        ToolTip.SetTip(_search, "Ctrl+F. Every word must match; Esc clears.");
        _refresh = OutlineButton("Refresh");
        _refresh.VerticalAlignment = VerticalAlignment.Center;
        _refresh.Click += (_, _) => _host.RefreshNow();
        var closeGlyph = new Button
        {
            Content = "✕", Foreground = Muted, Background = Brushes.Transparent, BorderThickness = new Thickness(0),
            Padding = new Thickness(4, 0), FontSize = 14, Cursor = new Cursor(StandardCursorType.Hand),
            VerticalAlignment = VerticalAlignment.Center,
        };
        closeGlyph.Click += (_, _) => Close();
        var actions = new StackPanel
        {
            Orientation = Orientation.Horizontal, Spacing = 10, VerticalAlignment = VerticalAlignment.Center,
            Children = { _search, _refresh, closeGlyph },
        };
        var header = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Margin = new Thickness(24, 18, 18, 14) };
        var titles = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Children = { heading, _subhead } };
        Grid.SetColumn(actions, 1);
        header.Children.Add(titles);
        header.Children.Add(actions);

        // ── Tabs on the left: Needs you | All open | Dismissed ──
        _needsYouTab = TabButton();
        _allTab = TabButton();
        _dismissedTab = TabButton();
        _needsYouTab.Click += (_, _) => { _view = GhView.NeedsYou; Refresh(); };
        _allTab.Click += (_, _) => { _view = GhView.All; Refresh(); };
        _dismissedTab.Click += (_, _) => { _view = GhView.Dismissed; Refresh(); };
        ToolTip.SetTip(_dismissedTab, "PRs you dismissed. Each comes back on its own when its state changes.");
        var tabs = new StackPanel
        {
            Orientation = Orientation.Horizontal, Spacing = 6, VerticalAlignment = VerticalAlignment.Center,
            Children = { _needsYouTab, _allTab, _dismissedTab },
        };

        // ── View on the right: Group by · Sort · Updated within (persisted through the host) ──
        _groupBox = Picker(GroupChoices.Select(c => c.Label));
        _sortBox = Picker(SortChoices.Select(c => c.Label));
        _ageBox = Picker(GhListOptions.AgeChoices.Select(AgeLabel));
        _groupBox.SelectionChanged += (_, _) => PickerChanged();
        _sortBox.SelectionChanged += (_, _) => PickerChanged();
        _ageBox.SelectionChanged += (_, _) => PickerChanged();
        ToolTip.SetTip(_ageBox, "Hide PRs not updated in this long, here and on the overlay.");
        // Ignore dismissals for a while, to hunt a PR down. Not persisted: a dismissal should keep hiding by default.
        _includeDismissedToggle = TabButton();
        _includeDismissedToggle.Margin = new Thickness(10, 0, 0, 0);
        _includeDismissedToggle.VerticalAlignment = VerticalAlignment.Center;
        _includeDismissedToggle.Click += (_, _) => SetIncludeDismissed(!_includeDismissed);
        ToolTip.SetTip(_includeDismissedToggle, "Show dismissed PRs in Needs you and All open too, to find one you hid.");
        var view = new StackPanel
        {
            Orientation = Orientation.Horizontal, Spacing = 6, VerticalAlignment = VerticalAlignment.Center,
            Children =
            {
                PickerLabel("Group by"), _groupBox,
                PickerLabel("Sort", 10), _sortBox,
                PickerLabel("Updated", 10), _ageBox,
                _includeDismissedToggle,
            },
        };
        var toolbar = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*"), Margin = new Thickness(22, 0, 22, 14) };
        Grid.SetColumn(view, 1);
        view.HorizontalAlignment = HorizontalAlignment.Right;
        toolbar.Children.Add(tabs);
        toolbar.Children.Add(view);

        var headerBorder = new Border
        {
            Background = Page, BorderBrush = Stroke, BorderThickness = new Thickness(0, 0, 0, 1),
            Child = new StackPanel { Children = { header, toolbar } },
        };
        // The Border has a Background, so its empty space is hit-testable and the whole band drags the window.
        headerBorder.PointerPressed += (_, e) =>
        {
            if (e.Source is Button || (e.Source is Visual v && v.FindAncestorOfType<Button>() is not null)) return;
            if (e.Source is Visual tv && (tv is TextBox || tv.FindAncestorOfType<TextBox>() is not null)) return;
            if (e.Source is Visual cv && (cv is ComboBox || cv.FindAncestorOfType<ComboBox>() is not null)) return;
            if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) BeginMoveDrag(e);
        };

        _list.Margin = new Thickness(22, 4, 22, 18);
        var scroller = new ScrollViewer
        {
            Content = _list, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        };

        var root = new DockPanel();
        DockPanel.SetDock(headerBorder, Dock.Top);
        root.Children.Add(headerBorder);
        root.Children.Add(scroller);

        Content = new Border
        {
            Background = Page, CornerRadius = new CornerRadius(12), BorderBrush = Stroke, BorderThickness = new Thickness(1.5),
            Child = root, ClipToBounds = true,
        };

        _host.Changed += OnHostChanged;
        Closed += (_, _) => _host.Changed -= OnHostChanged;
        Refresh();
    }

    /// <summary>Set by the App to offer "Start session…" on each row (opens <see cref="PrSessionWindow"/>). Null
    /// hides the button.</summary>
    public Action<GhPrItem>? StartSessionRequested { get; set; }

    /// <summary>Re-renders from the host's current snapshot. Satisfies <c>WindowHost.ShowOrFocus</c>'s
    /// refresh-on-both-paths contract.</summary>
    public void Retarget() => Refresh();

    /// <summary>Headless-render seam: pick the tab and, optionally, a search and "Include dismissed".</summary>
    internal void SetViewForRender(GhView view, string? search = null, bool includeDismissed = false)
    {
        _view = view;
        _includeDismissed = includeDismissed;
        _search.Text = search ?? "";
        Refresh();
    }

    private void OnHostChanged()
    {
        if (IsVisible) Refresh();
    }

    // A picker moved: hand the new options to the host, which reclassifies, persists, and raises Changed (→ Refresh).
    private void PickerChanged()
    {
        if (_syncingPickers) return;
        var o = _host.Options;
        _host.Options = o with
        {
            GroupBy = _groupBox.SelectedIndex >= 0 ? GroupChoices[_groupBox.SelectedIndex].Value : o.GroupBy,
            SortBy = _sortBox.SelectedIndex >= 0 ? SortChoices[_sortBox.SelectedIndex].Value : o.SortBy,
            MaxAgeDays = _ageBox.SelectedIndex >= 0 ? GhListOptions.AgeChoices[_ageBox.SelectedIndex] : o.MaxAgeDays,
        };
    }

    // Shows the host's options in the pickers without feeding the change back. An age no picker offers (a hand-edited
    // settings file) shows no selection but still applies.
    private void SyncPickers(GhListOptions o)
    {
        _syncingPickers = true;
        try
        {
            _groupBox.SelectedIndex = Array.FindIndex(GroupChoices, c => c.Value == o.GroupBy);
            _sortBox.SelectedIndex = Array.FindIndex(SortChoices, c => c.Value == o.SortBy);
            _ageBox.SelectedIndex = GhListOptions.AgeChoices.ToList().IndexOf(o.MaxAgeDays);
        }
        finally { _syncingPickers = false; }
    }

    private void Refresh()
    {
        var snap = _host.Current;
        var options = _host.Options;
        var now = DateTime.UtcNow;

        _subhead.Text = SubheadText(snap, now);
        _refresh.IsEnabled = !_host.Busy;
        _refresh.Content = _host.Busy ? "Checking…" : "Refresh";
        SyncPickers(options);

        // The tab counts follow the search (and "Include dismissed"), so each says how many rows it holds.
        string query = _search.Text?.Trim() ?? "";
        int needs = snap?.Filter(GhView.NeedsYou, query, _includeDismissed).Count() ?? 0;
        int all = snap?.Filter(GhView.All, query, _includeDismissed).Count() ?? 0;
        int dismissed = snap?.Filter(GhView.Dismissed, query).Count() ?? 0;
        StyleTab(_needsYouTab, $"Needs you  {needs}", _view == GhView.NeedsYou);
        StyleTab(_allTab, $"All open  {all}", _view == GhView.All);
        StyleTab(_dismissedTab, $"Dismissed  {dismissed}", _view == GhView.Dismissed);
        StyleTab(_includeDismissedToggle, _includeDismissed ? "✓ Include dismissed" : "Include dismissed", _includeDismissed);
        _includeDismissedToggle.IsEnabled = _view != GhView.Dismissed;   // that tab is nothing but dismissed PRs

        _list.Children.Clear();
        if (snap is null)
        {
            _list.Children.Add(EmptyText(_host.Busy ? "Checking GitHub…" : "No answer from GitHub yet."));
            return;
        }

        // What the age window is hiding, said once at the foot of the list (or in the empty text).
        string? ageNote = snap.TooOldCount > 0
            ? $"{Plural(snap.TooOldCount, "older PR")} hidden: not updated in the {AgeLabel(options.MaxAgeDays).ToLowerInvariant()}."
            : null;

        // A search that turns up dismissed PRs this view hides offers to show them (one click on the hint).
        int hiddenDismissed = snap.HiddenDismissedMatches(_view, query, _includeDismissed);

        var groups = snap.Grouped(_view, options, query, _includeDismissed);
        if (groups.Count == 0)
        {
            if (snap.Error is { } err && snap.Items.Count == 0)
                _list.Children.Add(EmptyText(err));
            else if (query.Length > 0)
                _list.Children.Add(EmptyText(_view == GhView.NeedsYou && all > 0
                    ? $"Nothing needing you matches \"{query}\". All open has {all}."
                    : $"No PRs match \"{query}\"."));
            else
                _list.Children.Add(EmptyText(_view switch
                {
                    GhView.NeedsYou  => "Nothing needs you right now.",
                    GhView.Dismissed => "Nothing dismissed. Dismiss a PR to hide it until something changes on it.",
                    _                => "No open pull requests involve you.",
                }));
            if (hiddenDismissed > 0) _list.Children.Add(IncludeDismissedHint(hiddenDismissed));
            if (ageNote is not null) _list.Children.Add(FootText(ageNote));
            return;
        }

        foreach (var (title, items) in groups)
        {
            // GroupBy.None is one untitled group: no header, the cards start at the top.
            if (title.Length > 0)
                _list.Children.Add(new TextBlock
                {
                    FontSize = 11, FontWeight = FontWeight.SemiBold, Foreground = Muted, LetterSpacing = 1.2,
                    Margin = new Thickness(2, 16, 2, 8), TextTrimming = TextTrimming.CharacterEllipsis,
                    Text = $"{title.ToUpperInvariant()}   {items.Count}",
                });
            else
                _list.Children.Add(new Border { Height = 14 });
            foreach (var item in items) _list.Children.Add(BuildCard(item, now, options.GroupBy));
        }
        if (hiddenDismissed > 0) _list.Children.Add(IncludeDismissedHint(hiddenDismissed));
        if (ageNote is not null) _list.Children.Add(FootText(ageNote));
    }

    private void SetIncludeDismissed(bool on)
    {
        _includeDismissed = on;
        Refresh();
    }

    // "2 dismissed PRs also match · Include them": a link-style button that flips "Include dismissed" on.
    private Control IncludeDismissedHint(int n)
    {
        var link = new Button
        {
            Content = $"{(n == 1 ? "1 dismissed PR also matches" : $"{n} dismissed PRs also match")} · Include them",
            Foreground = Accent, Background = Brushes.Transparent, BorderThickness = new Thickness(0),
            Padding = new Thickness(0), FontSize = 11.5, Cursor = new Cursor(StandardCursorType.Hand),
            Margin = new Thickness(2, 12, 2, 4), HorizontalAlignment = HorizontalAlignment.Left,
        };
        link.Click += (_, _) => SetIncludeDismissed(true);
        return link;
    }

    private static string Plural(int n, string noun) => n == 1 ? $"1 {noun}" : $"{n} {noun}s";

    private static string AgeLabel(int days) => days switch
    {
        <= 0 => "Any time",
        1    => "Last day",
        7    => "Last week",
        _    => $"Last {days} days",
    };

    private string SubheadText(GitHubAlertsSnapshot? snap, DateTime now)
    {
        if (snap is null) return _host.Busy ? "Checking GitHub…" : "Waiting for the first check.";
        var parts = new List<string>();
        if (snap.Login is { Length: > 0 } login) parts.Add($"@{login}");
        parts.Add($"checked {RelativeTime.Ago(now, snap.FetchedUtc)}");
        if (snap.Error is { } err) parts.Add($"last check failed: {err}");
        return string.Join(" · ", parts);
    }

    // One PR as a card: "#77 · frank · review" over the title (age on the right), the reason pills, then the branches
    // on the left of the footer and the actions on its right. A strip down the left edge carries the headline reason's
    // colour, so the page scans by urgency.
    private Control BuildCard(GhPrItem item, DateTime now, GhGroupBy groupBy)
    {
        var pr = item.Pr;
        bool live = item.NeedsYou && !item.Dismissed;

        // "#77 · yours" for your own PR; "#80 · frank · assigned" for someone else's. When the groups aren't repos,
        // the repo leads the line instead ("acme/api #77 · yours").
        var meta = new List<string> { groupBy == GhGroupBy.Repo ? $"#{pr.Number}" : $"{pr.Repo} #{pr.Number}" };
        bool yours = pr.Relation.HasFlag(GhPrRelation.Author);
        if (!yours && pr.Author.Length > 0) meta.Add(pr.Author);
        meta.Add(Role(pr.Relation));
        if (pr.IsDraft) meta.Add("draft");
        if (item.Dismissed && _view != GhView.Dismissed) meta.Add("dismissed");   // shown via "Include dismissed"
        var metaText = new TextBlock
        {
            Text = string.Join(" · ", meta.Where(m => m.Length > 0)), FontSize = 12, Foreground = Muted,
            TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center,
        };
        var age = new TextBlock
        {
            Text = pr.UpdatedUtc > DateTime.MinValue ? $"updated {RelativeTime.Ago(now, pr.UpdatedUtc)}" : "",
            FontSize = 12, Foreground = Muted, Margin = new Thickness(12, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center,
        };
        var top = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        Grid.SetColumn(age, 1);
        top.Children.Add(metaText);
        top.Children.Add(age);

        var title = new TextBlock
        {
            Text = pr.Title.Length > 0 ? pr.Title : "(untitled)", FontSize = 14.5, FontWeight = FontWeight.SemiBold,
            Foreground = live ? Fg : Muted, TextWrapping = TextWrapping.Wrap, MaxLines = 2,
            TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 4, 0, 0),
        };

        var body = new StackPanel { Children = { top, title } };
        if (item.Reasons.Count > 0)
        {
            var pills = new WrapPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 0) };
            foreach (var r in item.Reasons) pills.Children.Add(ReasonPill(r));
            body.Children.Add(pills);
        }

        var open = OutlineButton("Open in GitHub");
        ToolTip.SetTip(open, pr.Url);
        open.Click += (_, _) =>
        {
            PlatformServices.UrlOpener.Open(pr.Url);
            _host.MarkSeen(pr.Url);
        };

        // Dismiss hides the PR until its state changes; in the Dismissed tab the same slot restores it.
        var dismiss = GhostButton(item.Dismissed ? "Restore" : "Dismiss");
        ToolTip.SetTip(dismiss, item.Dismissed
            ? "Show this PR again now"
            : "Hide until something changes: new comments or reviews, a review decision, checks starting or stopping " +
              "failing, conflicts, a draft marked ready, or (on someone else's PR) a new push");
        dismiss.Click += (_, _) =>
        {
            if (item.Dismissed) _host.Restore(pr.Url);
            else _host.Dismiss(pr.Url);
        };

        var actions = new StackPanel
        {
            Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(12, 0, 0, 0), Children = { dismiss, open },
        };
        if (StartSessionRequested is { } startSession)
        {
            var start = PrimaryButton("Start session");
            ToolTip.SetTip(start, "Start a Claude session on this PR: pick how it's checked out and what to ask");
            start.Click += (_, _) => startSession(item);
            actions.Children.Add(start);
        }

        // The branches, "feature/checkout-form → main", when the poll knows them. Branch names are the author's, but
        // this is display only.
        var branches = new TextBlock
        {
            Text = Branches(pr),
            FontFamily = Mono, FontSize = 11.5, Foreground = Muted, VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        if (pr.HeadBranch.Length > 0) ToolTip.SetTip(branches, pr.IsCrossRepository && pr.HeadRepo.Length > 0
            ? $"From {pr.HeadRepo}:{pr.HeadBranch} into {pr.BaseBranch}" : $"{pr.HeadBranch} into {pr.BaseBranch}");
        var footer = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Margin = new Thickness(0, 12, 0, 0) };
        Grid.SetColumn(actions, 1);
        footer.Children.Add(branches);
        footer.Children.Add(actions);
        body.Children.Add(footer);

        var strip = new Border
        {
            Width = 4,
            Background = live ? new SolidColorBrush(KindColor(item.Reasons[0].Kind)) : Brushes.Transparent,
        };
        body.Margin = new Thickness(16, 14, 16, 14);
        var layout = new DockPanel { LastChildFill = true };
        DockPanel.SetDock(strip, Dock.Left);
        layout.Children.Add(strip);
        layout.Children.Add(body);

        var hover = new SolidColorBrush(Palette.Blend(Palette.Border, Palette.Muted, 0.45f));
        var card = new Border
        {
            Child = layout, Background = CardBg, BorderBrush = Stroke, BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10), ClipToBounds = true, Margin = new Thickness(0, 0, 0, 10),
        };
        card.PointerEntered += (_, _) => card.BorderBrush = hover;
        card.PointerExited += (_, _) => card.BorderBrush = Stroke;
        return card;
    }

    /// <summary>"feature/x → main", "feature/x" without a base, or "" when the poll didn't name the branches.</summary>
    internal static string Branches(GhPullRequest pr) =>
        pr.HeadBranch.Length == 0 ? "" : pr.BaseBranch.Length > 0 ? $"{pr.HeadBranch} → {pr.BaseBranch}" : pr.HeadBranch;

    // How you relate to the PR, for the meta line — "yours" wins over the others.
    private static string Role(GhPrRelation r) =>
        r.HasFlag(GhPrRelation.Author) ? "yours"
        : r.HasFlag(GhPrRelation.ReviewRequested) ? "review"
        : r.HasFlag(GhPrRelation.Assignee) ? "assigned"
        : "";

    private static Control ReasonPill(GhAlertReason r)
    {
        var c = KindColor(r.Kind);
        return new Border
        {
            CornerRadius = new CornerRadius(999), Padding = new Thickness(8, 2), Margin = new Thickness(0, 0, 6, 4),
            Background = new SolidColorBrush(c) { Opacity = 0.16 },
            Child = new TextBlock { Text = r.Text, FontSize = 11.5, FontWeight = FontWeight.SemiBold, Foreground = new SolidColorBrush(c) },
        };
    }

    // The same colour as the overlay strip's symbol for that kind, so the two read as one key.
    private static Color KindColor(GhAlertKind k) => Views.OverlayCanvas.GitHubKindColor(k);

    private static TextBlock EmptyText(string text) => new()
    {
        Text = text, Foreground = Muted, FontSize = 12.5, Margin = new Thickness(2, 22, 2, 4), TextWrapping = TextWrapping.Wrap,
    };

    private static TextBlock FootText(string text) => new()
    {
        Text = text, Foreground = Muted, FontSize = 11.5, FontStyle = FontStyle.Italic,
        Margin = new Thickness(2, 8, 2, 4), TextWrapping = TextWrapping.Wrap,
    };

    private static ComboBox Picker(IEnumerable<string> labels)
    {
        var box = new ComboBox { FontSize = 12, MinWidth = 96, VerticalAlignment = VerticalAlignment.Center };
        foreach (var l in labels) box.Items.Add(l);
        return box;
    }

    private static TextBlock PickerLabel(string text, double leftGap = 0) => new()
    {
        Text = text, Foreground = Muted, FontSize = 12, VerticalAlignment = VerticalAlignment.Center,
        Margin = new Thickness(leftGap, 0, 0, 0),
    };

    // A quieter, borderless button for Dismiss / Restore, so the card's other actions stay the obvious ones.
    private static Button GhostButton(string text) => new()
    {
        Content = text, Foreground = Muted, Background = Brushes.Transparent, BorderThickness = new Thickness(0),
        CornerRadius = new CornerRadius(7), Padding = new Thickness(8, 5), FontSize = 12, Cursor = new Cursor(StandardCursorType.Hand),
    };

    private static Button TabButton() => new()
    {
        FontSize = 12, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(7),
        Padding = new Thickness(11, 4), Cursor = new Cursor(StandardCursorType.Hand),
    };

    private static void StyleTab(Button b, string text, bool on)
    {
        var accent = Palette.Active.Accent.ToColor();
        b.Content = text;
        b.Background = on ? new SolidColorBrush(accent) { Opacity = 0.16 } : Brushes.Transparent;
        b.Foreground = on ? Accent : Muted;
        b.BorderBrush = on ? Accent : Stroke;
    }

    private static Button OutlineButton(string text) => new()
    {
        Content = text, Foreground = Fg, Background = Brushes.Transparent, BorderBrush = Stroke, BorderThickness = new Thickness(1),
        CornerRadius = new CornerRadius(7), Padding = new Thickness(12, 5), FontSize = 12, Cursor = new Cursor(StandardCursorType.Hand),
    };

    // The card's call to action, filled with the accent.
    private static Button PrimaryButton(string text) => new()
    {
        Content = text, Foreground = Palette.OnAccentBrush, Background = Accent, BorderThickness = new Thickness(0),
        CornerRadius = new CornerRadius(7), Padding = new Thickness(14, 6), FontSize = 12, FontWeight = FontWeight.SemiBold,
        Cursor = new Cursor(StandardCursorType.Hand),
    };

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.F && e.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            _search.Focus();
            _search.SelectAll();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            // Esc clears an active search first, then closes.
            if (!string.IsNullOrEmpty(_search.Text)) _search.Text = "";
            else Close();
            e.Handled = true;
        }
        base.OnKeyDown(e);
    }
}
