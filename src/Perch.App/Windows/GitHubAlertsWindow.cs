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
/// The GitHub alerts list, opened by clicking the overlay's GitHub strip. Your open pull
/// requests grouped by repository, each with the reasons it needs you ("Review requested", "Changes requested by
/// alice", "Ready to merge" …) and an "Open in GitHub" button — the only action for now. A filter at the top
/// switches between just the PRs needing you (the default) and every open PR that involves you.
///
/// <para>Reused via <c>WindowHost.ShowOrFocus</c> (<see cref="Retarget"/> re-renders). It owns no data: it renders
/// <see cref="GitHubAlertsMonitorHost.Current"/> and re-renders on the host's <c>Changed</c>. Opening a PR marks it
/// seen through the host, which reclassifies at once — so a PR that only had new comments drops out of "Needs
/// you" as you go to read them. Styled off <see cref="TodoWindow"/> so the popups read as one app.</para>
/// </summary>
internal sealed class GitHubAlertsWindow : Window
{
    private static readonly IBrush Bg       = Palette.OverlaySurfaceBrush;
    private static readonly IBrush Stroke   = Palette.BorderBrush;
    private static readonly IBrush Fg       = Palette.FgBrush;
    private static readonly IBrush Muted    = Palette.MutedBrush;
    private static readonly IBrush Accent   = Palette.AccentBrush;
    private static readonly IBrush RowHover = new SolidColorBrush(Color.FromArgb(28, 255, 255, 255));

    private readonly GitHubAlertsMonitorHost _host;
    private readonly TextBlock _subhead;
    private readonly Button _refresh;
    private readonly Button _needsYouTab, _allTab;
    private readonly TextBox _search;
    private readonly StackPanel _list = new();
    private bool _needsYouOnly = true;

    public GitHubAlertsWindow(GitHubAlertsMonitorHost host)
    {
        _host = host;

        Title = "GitHub alerts";
        WindowDecorations = WindowDecorations.None;
        Background = Brushes.Transparent;
        TransparencyLevelHint = [WindowTransparencyLevel.Transparent];
        CanResize = false;
        Width = 640;
        Height = 620;
        ShowInTaskbar = true;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;

        // ── Header (draggable): title + status line, Refresh and close on the right ──
        var heading = new TextBlock { Text = "GitHub alerts", Foreground = Fg, FontWeight = FontWeight.Bold, FontSize = 16 };
        _subhead = new TextBlock { Foreground = Muted, FontSize = 12, Margin = new Thickness(0, 2, 0, 0), TextTrimming = TextTrimming.CharacterEllipsis };
        _refresh = OutlineButton("Refresh", Fg);
        _refresh.Click += (_, _) => _host.RefreshNow();
        var closeGlyph = new Button
        {
            Content = "✕", Foreground = Muted, Background = Brushes.Transparent, BorderThickness = new Thickness(0),
            Padding = new Thickness(4, 0), FontSize = 14, Cursor = new Cursor(StandardCursorType.Hand),
            VerticalAlignment = VerticalAlignment.Top,
        };
        closeGlyph.Click += (_, _) => Close();
        var actions = new StackPanel
        {
            Orientation = Orientation.Horizontal, Spacing = 10, VerticalAlignment = VerticalAlignment.Top,
            Children = { _refresh, closeGlyph },
        };
        var header = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Margin = new Thickness(20, 16, 18, 12) };
        var titles = new StackPanel { Children = { heading, _subhead } };
        Grid.SetColumn(actions, 1);
        header.Children.Add(titles);
        header.Children.Add(actions);

        // ── Filter: Needs you | All open, and a search box filling the rest of the row ──
        _needsYouTab = TabButton();
        _allTab = TabButton();
        _needsYouTab.Click += (_, _) => { _needsYouOnly = true; Refresh(); };
        _allTab.Click += (_, _) => { _needsYouOnly = false; Refresh(); };
        var tabs = new StackPanel
        {
            Orientation = Orientation.Horizontal, Spacing = 6, VerticalAlignment = VerticalAlignment.Center,
            Children = { _needsYouTab, _allTab },
        };
        _search = new TextBox
        {
            PlaceholderText = "Search title, repo, author, #number…", FontSize = 12.5,
            Margin = new Thickness(12, 0, 0, 0), VerticalContentAlignment = VerticalAlignment.Center,
        };
        _search.TextChanged += (_, _) => Refresh();
        ToolTip.SetTip(_search, "Ctrl+F. Every word must match; Esc clears.");
        var filterRow = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*"), Margin = new Thickness(18, 0, 18, 12) };
        Grid.SetColumn(_search, 1);
        filterRow.Children.Add(tabs);
        filterRow.Children.Add(_search);

        var headerBorder = new Border
        {
            Background = Bg, BorderBrush = Stroke, BorderThickness = new Thickness(0, 0, 0, 1),
            Child = new StackPanel { Children = { header, filterRow } },
        };
        // The Border has a Background, so its empty space is hit-testable and the whole band drags the window.
        headerBorder.PointerPressed += (_, e) =>
        {
            if (e.Source is Button || (e.Source is Visual v && v.FindAncestorOfType<Button>() is not null)) return;
            if (e.Source is Visual tv && (tv is TextBox || tv.FindAncestorOfType<TextBox>() is not null)) return;
            if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) BeginMoveDrag(e);
        };

        var scroller = new ScrollViewer
        {
            Content = _list, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Padding = new Thickness(0, 0, 0, 10),
        };

        var root = new DockPanel();
        DockPanel.SetDock(headerBorder, Dock.Top);
        root.Children.Add(headerBorder);
        root.Children.Add(scroller);

        Content = new Border
        {
            Background = Bg, CornerRadius = new CornerRadius(12), BorderBrush = Stroke, BorderThickness = new Thickness(1.5),
            Child = root, ClipToBounds = true,
        };

        _host.Changed += OnHostChanged;
        Closed += (_, _) => _host.Changed -= OnHostChanged;
        Refresh();
    }

    /// <summary>Re-renders from the host's current snapshot. Satisfies <c>WindowHost.ShowOrFocus</c>'s
    /// refresh-on-both-paths contract.</summary>
    public void Retarget() => Refresh();

    /// <summary>Headless-render seam: pick the filter tab and, optionally, a search.</summary>
    internal void SetNeedsYouOnlyForRender(bool needsYouOnly, string? search = null)
    {
        _needsYouOnly = needsYouOnly;
        _search.Text = search ?? "";
        Refresh();
    }

    private void OnHostChanged()
    {
        if (IsVisible) Refresh();
    }

    private void Refresh()
    {
        var snap = _host.Current;
        var now = DateTime.UtcNow;

        _subhead.Text = SubheadText(snap, now);
        _refresh.IsEnabled = !_host.Busy;
        _refresh.Content = _host.Busy ? "Checking…" : "Refresh";

        // The tab counts follow the search, so each says how many matches it holds.
        string query = _search.Text?.Trim() ?? "";
        int needs = snap?.Filter(needsYouOnly: true, query).Count() ?? 0;
        int all = snap?.Filter(needsYouOnly: false, query).Count() ?? 0;
        StyleTab(_needsYouTab, $"Needs you  {needs}", _needsYouOnly);
        StyleTab(_allTab, $"All open  {all}", !_needsYouOnly);

        _list.Children.Clear();
        if (snap is null)
        {
            _list.Children.Add(EmptyText(_host.Busy ? "Checking GitHub…" : "No answer from GitHub yet."));
            return;
        }

        var groups = snap.ByRepo(_needsYouOnly, query);
        if (groups.Count == 0)
        {
            if (snap.Error is { } err && snap.Items.Count == 0)
                _list.Children.Add(EmptyText(err));
            else if (query.Length > 0)
                _list.Children.Add(EmptyText(_needsYouOnly && all > 0
                    ? $"Nothing needing you matches \"{query}\". All open has {all}."
                    : $"No PRs match \"{query}\"."));
            else
                _list.Children.Add(EmptyText(_needsYouOnly
                    ? "Nothing needs you right now."
                    : "No open pull requests involve you."));
            return;
        }

        foreach (var (repo, items) in groups)
        {
            _list.Children.Add(new TextBlock
            {
                FontSize = 10.5, FontWeight = FontWeight.SemiBold, Foreground = Muted, LetterSpacing = 0.6,
                Margin = new Thickness(20, 14, 20, 6), TextTrimming = TextTrimming.CharacterEllipsis,
                Text = $"{repo.ToUpperInvariant()}   {items.Count}",
            });
            foreach (var item in items) _list.Children.Add(BuildRow(item, now));
        }
    }

    private string SubheadText(GitHubAlertsSnapshot? snap, DateTime now)
    {
        if (snap is null) return _host.Busy ? "Checking GitHub…" : "Waiting for the first check.";
        var parts = new List<string>();
        if (snap.Login is { Length: > 0 } login) parts.Add($"@{login}");
        parts.Add($"checked {RelativeTime.Ago(now, snap.FetchedUtc)}");
        if (snap.Error is { } err) parts.Add($"last check failed: {err}");
        return string.Join(" · ", parts);
    }

    private Control BuildRow(GhPrItem item, DateTime now)
    {
        var pr = item.Pr;

        var title = new TextBlock
        {
            Text = pr.Title.Length > 0 ? pr.Title : "(untitled)", FontSize = 13, FontWeight = FontWeight.SemiBold,
            Foreground = item.NeedsYou ? Fg : Muted, TextTrimming = TextTrimming.CharacterEllipsis,
        };

        // "#77 · yours" for your own PR; "#80 · frank · assigned" for someone else's.
        var meta = new List<string> { $"#{pr.Number}" };
        bool yours = pr.Relation.HasFlag(GhPrRelation.Author);
        if (!yours && pr.Author.Length > 0) meta.Add(pr.Author);
        meta.Add(Role(pr.Relation));
        if (pr.IsDraft) meta.Add("draft");
        if (pr.UpdatedUtc > DateTime.MinValue) meta.Add($"updated {RelativeTime.Ago(now, pr.UpdatedUtc)}");
        var metaText = new TextBlock
        {
            Text = string.Join(" · ", meta.Where(m => m.Length > 0)), FontSize = 11.5, Foreground = Muted,
            TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 2, 0, 0),
        };

        var body = new StackPanel { Children = { title, metaText } };
        if (item.Reasons.Count > 0)
        {
            var pills = new WrapPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 6, 0, 0) };
            foreach (var r in item.Reasons) pills.Children.Add(ReasonPill(r));
            body.Children.Add(pills);
        }

        var open = OutlineButton("Open in GitHub", Fg);
        open.VerticalAlignment = VerticalAlignment.Center;
        open.Margin = new Thickness(12, 0, 0, 0);
        ToolTip.SetTip(open, pr.Url);
        open.Click += (_, _) =>
        {
            PlatformServices.UrlOpener.Open(pr.Url);
            _host.MarkSeen(pr.Url);
        };

        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        Grid.SetColumn(open, 1);
        grid.Children.Add(body);
        grid.Children.Add(open);

        // The left rule carries the headline reason's colour, so the list scans by urgency.
        var rule = item.NeedsYou ? new SolidColorBrush(KindColor(item.Reasons[0].Kind)) : (IBrush)Brushes.Transparent;
        var row = new Border
        {
            Child = grid, Padding = new Thickness(18, 9, 18, 9), Margin = new Thickness(0, 0, 0, 1),
            BorderThickness = new Thickness(2, 0, 0, 0), BorderBrush = rule, Background = Brushes.Transparent,
        };
        row.PointerEntered += (_, _) => row.Background = RowHover;
        row.PointerExited += (_, _) => row.Background = Brushes.Transparent;
        return row;
    }

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
        Text = text, Foreground = Muted, FontSize = 12.5, Margin = new Thickness(20, 18), TextWrapping = TextWrapping.Wrap,
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

    private static Button OutlineButton(string text, IBrush fg) => new()
    {
        Content = text, Foreground = fg, Background = Brushes.Transparent, BorderBrush = Stroke, BorderThickness = new Thickness(1),
        CornerRadius = new CornerRadius(7), Padding = new Thickness(12, 5), FontSize = 12, Cursor = new Cursor(StandardCursorType.Hand),
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
