using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Perch.Avalonia.Rendering;
using Perch.Avalonia.Services;
using Perch.Avalonia.Theming;
using Perch.Avalonia.Views;
using Perch.Data;
using Perch.Feeds;

namespace Perch.Avalonia.Windows;

/// <summary>
/// The feeds story player (docs/feeds-plan.md §1, "Story player"). One portrait card per entry, played from a
/// <see cref="StoryPlan"/>: a click on an unread head plays its unread entries oldest first, then every other feed
/// with news, then an "All caught up" card; a click on a read head replays its newest few. Across the top, a tray of
/// every feed (click one to switch) and one progress segment per card of the current feed (click one to jump).
///
/// <para>Only the side gutters, the keys and the segments advance — never a click on the body, which holds
/// selectable text and links. There's no timed auto-advance. Showing a card marks it read, so the overlay ring
/// follows along; a poll that lands mid-story is merged in ahead of the cursor only.</para>
///
/// <para>The body is the entry's content converted by <see cref="FeedCard"/> (the allowlist sanitizer) off the UI
/// thread and rendered by <see cref="MarkdownView"/> <b>without</b> a file-reference context, so nothing in a feed can
/// arm a local path (pinned by <c>UiConventionTests</c>). Reused via <c>WindowHost.ShowOrFocus</c>;
/// <see cref="Play"/> points it at a head.</para>
/// </summary>
internal sealed class FeedStoryWindow : Window
{
    private const double SlideMs = 150, FeedSlideMs = 230;
    private const double MarkdownInset = 11;   // MarkdownView's per-block anchor bar + padding; the title lines up with it

    private readonly FeedsMonitorHost _host;
    private readonly Func<string?, Bitmap?> _icon;
    private readonly SessionPalette _p;
    private readonly MarkdownStyle _prose;

    private readonly StackPanel _tray = new() { Orientation = Orientation.Horizontal, Spacing = 10 };
    private readonly ScrollViewer _trayScroll;
    private readonly Grid _segments = new() { Margin = new Thickness(16, 10, 16, 0) };
    private readonly Border _header;
    private readonly HeadGlyph _headGlyph;
    private readonly TextBlock _feedTitle, _age;
    private readonly Button _menu, _open;
    private readonly Border _prev, _next;
    private readonly Border _cardHost;
    private readonly ScaleTransform _scale = new();
    private readonly TranslateTransform _shift = new();
    private ScrollViewer? _cardScroll;
    private Border? _bodySlot;
    private string? _shownKey;

    private StoryPlan? _plan;
    private List<string> _trayOrder = [];
    private readonly Dictionary<string, string> _markdown = new(StringComparer.Ordinal);
    private readonly HashSet<string> _converting = new(StringComparer.Ordinal);

    private DispatcherTimer? _anim;
    private long _animStart;
    private double _animFrom, _animMs, _animScale;
    private bool _renderMode;   // headless render: convert synchronously, no slide

    /// <summary>The ⋯ menu's (or an error card's) "Edit feed…": the app opens the Feeds settings page.</summary>
    public event Action<string>? EditRequested;

    public FeedStoryWindow(FeedsMonitorHost host, Func<string?, Bitmap?> icon, SessionPalette? palette = null)
    {
        _host = host;
        _icon = icon;
        _p = palette ?? SessionPalette.Current;
        _prose = _p.Prose with { BodySize = 14, RootMargin = new Thickness(0) };

        Title = "Feeds";
        WindowDecorations = WindowDecorations.None;
        Background = Brushes.Transparent;
        TransparencyLevelHint = [WindowTransparencyLevel.Transparent];
        CanResize = false;
        Width = 440;
        Height = 640;
        ShowInTaskbar = true;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        FontFamily = _p.Body;

        // ── Tray: every feed, the current one enlarged; close on the right ──
        _trayScroll = new ScrollViewer
        {
            Content = _tray, HorizontalScrollBarVisibility = ScrollBarVisibility.Hidden,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled, VerticalAlignment = VerticalAlignment.Center,
        };
        var close = GlyphButton("✕", 14);
        close.Click += (_, _) => Close();
        ToolTip.SetTip(close, "Close (Esc)");
        var trayRow = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Margin = new Thickness(16, 12, 10, 0), Height = 40 };
        Grid.SetColumn(close, 1);
        trayRow.Children.Add(_trayScroll);
        trayRow.Children.Add(close);

        // ── Header: the current feed's head, title and the entry's age; ⋯ menu ──
        _headGlyph = new HeadGlyph(_p) { Width = 26, Height = 26, Margin = new Thickness(0, 0, 10, 0), VerticalAlignment = VerticalAlignment.Center };
        _feedTitle = new TextBlock
        {
            FontSize = 13, FontWeight = FontWeight.SemiBold, Foreground = _p.Title,
            TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center,
        };
        _age = new TextBlock { FontSize = 12, Foreground = _p.Muted, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 0, 0) };
        _menu = GlyphButton("⋯", 18);
        _menu.Click += (_, _) => ShowMenu();
        ToolTip.SetTip(_menu, "More");
        var headerRow = new DockPanel();
        DockPanel.SetDock(_headGlyph, Dock.Left);
        DockPanel.SetDock(_menu, Dock.Right);
        DockPanel.SetDock(_age, Dock.Right);
        headerRow.Children.Add(_headGlyph);
        headerRow.Children.Add(_menu);
        headerRow.Children.Add(_age);
        headerRow.Children.Add(_feedTitle);
        _header = new Border { Child = headerRow, Padding = new Thickness(16, 10, 10, 10), Margin = new Thickness(0, 8, 0, 0) };

        var top = new StackPanel { Children = { trayRow, _segments, _header } };
        top.PointerPressed += DragFromChrome;

        // ── Card between the gutters ──
        _cardHost = new Border
        {
            Background = _p.Raised, BorderBrush = _p.BorderSoft, BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(12), ClipToBounds = true,
            RenderTransform = new TransformGroup { Children = { _scale, _shift } },
        };
        _prev = Gutter("‹", "Previous (←)", Prev);
        _next = Gutter("›", "Next (→ or Space)", Next);
        var middle = new Grid { ColumnDefinitions = new ColumnDefinitions("34,*,34") };
        Grid.SetColumn(_prev, 0);
        Grid.SetColumn(_cardHost, 1);
        Grid.SetColumn(_next, 2);
        middle.Children.Add(_prev);
        middle.Children.Add(_cardHost);
        middle.Children.Add(_next);

        // ── Pinned: Open in browser ──
        _open = new Button
        {
            Content = "Open in browser  ↗", Foreground = _p.Text, Background = _p.Raised2, BorderBrush = _p.Border,
            BorderThickness = new Thickness(1), CornerRadius = SessionPalette.ButtonRadius, Padding = new Thickness(16, 7),
            FontSize = 12.5, Cursor = new Cursor(StandardCursorType.Hand), Focusable = false,
            HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 12, 0, 14),
        };
        _open.Click += (_, _) => OpenEntry();

        var root = new DockPanel();
        DockPanel.SetDock(top, Dock.Top);
        DockPanel.SetDock(_open, Dock.Bottom);
        root.Children.Add(top);
        root.Children.Add(_open);
        root.Children.Add(middle);

        Content = new Border
        {
            Background = _p.Surface, CornerRadius = new CornerRadius(14), BorderBrush = _p.Border,
            BorderThickness = new Thickness(1.5), Child = root, ClipToBounds = true,
        };

        // Tunnel, so the keys work wherever focus sits (a selected run of body text, the scroller) and a focused
        // button can't also act on Space/Enter.
        AddHandler(KeyDownEvent, OnKey, RoutingStrategies.Tunnel);
        _host.Changed += OnHostChanged;
        Closed += (_, _) =>
        {
            _host.Changed -= OnHostChanged;
            _anim?.Stop();
        };
    }

    // ── Playing ─────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Plays <paramref name="subId"/>'s story (its unread entries, else a replay), or for null (the
    /// overlay's "+N" chip) the first feed with news. Freezes the tray's order for this playback, so heads don't
    /// jump about as their feeds are read.</summary>
    public void Play(string? subId)
    {
        var snap = _host.Current;
        _trayOrder = snap.Heads.Select(h => h.SubId).ToList();
        _markdown.Clear();
        _plan = subId is null ? StoryPlan.ForOverflow(snap.Stories) : StoryPlan.ForHead(snap.Stories, subId);
        ShowCard(0, feedChanged: false);
    }

    /// <summary>Re-reads the icons (one finished decoding) without touching the card.</summary>
    public void RefreshIcons()
    {
        if (_plan is null) return;
        RenderTray();
        UpdateHeader();
    }

    // A tray head: that feed's story, sliding the way the tray reads.
    private void SwitchFeed(string subId)
    {
        if (_plan is null || subId == CurrentSubId) return;
        int from = _trayOrder.IndexOf(CurrentSubId ?? ""), to = _trayOrder.IndexOf(subId);
        _plan = StoryPlan.ForHead(_host.Current.Stories, subId);
        ShowCard(to >= from ? 1 : -1, feedChanged: true);
    }

    private void Next()
    {
        if (_plan is null) return;
        var move = _plan.Next();
        if (move == StoryMove.Ended)
        {
            // Past "All caught up" the story's over; a replay simply stops at its newest entry.
            if (!_plan.IsReplay) Close();
            return;
        }
        ShowCard(1, move == StoryMove.FeedChanged);
    }

    private void Prev()
    {
        if (_plan is null) return;
        var move = _plan.Prev();
        if (move != StoryMove.None) ShowCard(-1, move == StoryMove.FeedChanged);
    }

    private void JumpTo(int card)
    {
        if (_plan is null) return;
        int from = _plan.CardIndex;
        if (_plan.JumpTo(card) == StoryMove.Moved) ShowCard(card > from ? 1 : -1, feedChanged: false);
    }

    private string? CurrentSubId => _plan is null ? null
        : _plan.Current.SubId is { Length: > 0 } s ? s
        : _plan.CurrentRun.SubId is { Length: > 0 } r ? r : null;

    private static string CardKey(StoryCard c) => c.SubId + "\n" + c.Entry?.Id;

    // Shows the plan's current card: the card itself, then the chrome; marks it read last (which raises the host's
    // Changed → OnHostChanged, re-rendering the tray's rings).
    private void ShowCard(int dir, bool feedChanged)
    {
        if (_plan is null) return;
        var card = _plan.Current;
        _bodySlot = null;
        _cardScroll = null;
        _shownKey = card.Kind == StoryCardKind.Entry ? CardKey(card) : null;

        _cardHost.Child = card.Kind switch
        {
            StoryCardKind.Entry => EntryCard(card),
            StoryCardKind.Error => ErrorCard(card),
            StoryCardKind.CaughtUp => CaughtUpCard(),
            _ => EmptyCard(card),
        };
        // Swapped while visible: re-arrange explicitly (CLAUDE.md, "Filling a window's content after Show()").
        _cardHost.InvalidateArrange();

        UpdateHeader();
        RenderSegments();
        RenderTray();
        UpdateNav();
        Animate(dir, feedChanged);

        if (card is { Kind: StoryCardKind.Entry, Entry: { } e }) _host.MarkRead(card.SubId, e.Id);
        Prefetch();
    }

    // A poll landed (or a read was recorded): fold new entries in ahead of the cursor and refresh the chrome. The
    // card itself is left alone, so its scroll position survives.
    private void OnHostChanged()
    {
        if (!IsVisible || _plan is null) return;
        _plan.Merge(_host.Current.Stories);
        RenderTray();
        RenderSegments();
        UpdateNav();
    }

    // ── Cards ───────────────────────────────────────────────────────────────────────────────────────────────

    private Control EntryCard(StoryCard card)
    {
        var e = card.Entry!;
        var title = new SelectableTextBlock
        {
            Text = e.Title.Length > 0 ? e.Title : "(untitled)", FontFamily = _p.Display, FontSize = 19,
            FontWeight = FontWeight.SemiBold, Foreground = _p.Title, TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(MarkdownInset, 0, 0, 0),
        };
        var meta = new TextBlock
        {
            Text = Meta(e), FontSize = 12, Foreground = _p.Muted, TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(MarkdownInset, 5, 0, 14),
        };

        _bodySlot = new Border();
        var key = CardKey(card);
        if (_markdown.TryGetValue(key, out var md)) _bodySlot.Child = Body(md);
        else
        {
            _bodySlot.Child = Note("Loading…");
            Convert(card);
        }

        _cardScroll = new ScrollViewer
        {
            Content = new StackPanel { Margin = new Thickness(20, 18, 20, 20), Children = { title, meta, _bodySlot } },
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        };
        return _cardScroll;
    }

    // The converted body, or a stub when the entry carries neither content nor summary. No FileRefContext: feed
    // content must never arm a local path.
    private Control Body(string md) => md.Length == 0
        ? Note("No preview. Open it in the browser to read it.")
        : MarkdownView.Build(md, _prose);

    private string Meta(FeedEntry e)
    {
        var parts = new List<string>();
        if (e.Author is { Length: > 0 } a) parts.Add(a);
        parts.Add((e.Published ?? e.Updated).ToLocalTime().ToString("d MMM yyyy, HH:mm"));
        if (e.Url is { } url && Uri.TryCreate(url, UriKind.Absolute, out var u)) parts.Add(FeedUrl.DisplayHost(u));
        return string.Join("  ·  ", parts);
    }

    private Control ErrorCard(StoryCard card)
    {
        string name = _host.Head(card.SubId)?.Title ?? "this feed";
        var retry = PillButton("Retry");
        retry.Click += (_, _) =>
        {
            _host.RefreshNow(card.SubId);
            retry.Content = "Checking…";
            retry.IsEnabled = false;
        };
        var edit = PillButton("Edit feed…");
        edit.Click += (_, _) => EditRequested?.Invoke(card.SubId);
        // What › does next: the entries cached before the failure, another feed, or nothing (the hint goes).
        string? hint = _plan is not { } pl ? null
            : pl.CardIndex + 1 < pl.CurrentRun.Cards.Count ? "› shows what was fetched before."
            : pl.RunIndex + 1 < pl.Runs.Count ? "› moves on."
            : null;
        return Centered("⚠", _p.Await, $"Couldn't load {name}", card.Error ?? "The last check failed.", hint, retry, edit);
    }

    private Control CaughtUpCard()
    {
        var close = PillButton("Close");
        close.Click += (_, _) => Close();
        return Centered("✓", _p.Ok, "All caught up", "You've seen everything new in your feeds.",
            "› or Space closes.", close);
    }

    private Control EmptyCard(StoryCard card)
    {
        var head = card.SubId.Length > 0 ? _host.Head(card.SubId) : null;
        var check = PillButton("Check now");
        check.Click += (_, _) =>
        {
            _host.RefreshNow(card.SubId.Length > 0 ? card.SubId : null);
            check.Content = "Checking…";
            check.IsEnabled = false;
        };
        string detail = head is null ? "There are no feeds to play."
            : head.HasFetched ? "This feed has no entries yet." : "Perch hasn't fetched this feed yet.";
        return head is null
            ? Centered("◎", _p.Muted, "Nothing to play", detail, null)
            : Centered("◎", _p.Muted, "Nothing to play", detail, null, check);
    }

    private Control Centered(string glyph, IBrush glyphBrush, string title, string detail, string? hint, params Button[] buttons)
    {
        var stack = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(26, 0), Spacing = 6 };
        stack.Children.Add(new TextBlock { Text = glyph, FontSize = 30, Foreground = glyphBrush, HorizontalAlignment = HorizontalAlignment.Center });
        stack.Children.Add(new TextBlock
        {
            Text = title, FontFamily = _p.Display, FontSize = 18, FontWeight = FontWeight.SemiBold, Foreground = _p.Title,
            TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center,
        });
        stack.Children.Add(new SelectableTextBlock
        {
            Text = detail, FontSize = 13, Foreground = _p.Text, TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center,
        });
        if (buttons.Length > 0)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 10, 0, 0) };
            foreach (var b in buttons) row.Children.Add(b);
            stack.Children.Add(row);
        }
        if (hint is not null)
            stack.Children.Add(new TextBlock
            {
                Text = hint, FontSize = 11.5, Foreground = _p.Muted, TextWrapping = TextWrapping.Wrap,
                TextAlignment = TextAlignment.Center, Margin = new Thickness(0, 8, 0, 0),
            });
        return stack;
    }

    private TextBlock Note(string text) => new()
    {
        Text = text, FontSize = 13, Foreground = _p.Muted, FontStyle = FontStyle.Italic, TextWrapping = TextWrapping.Wrap,
        Margin = new Thickness(MarkdownInset, 0, 0, 0),
    };

    // ── Conversion (off the UI thread) ──────────────────────────────────────────────────────────────────────

    private void Convert(StoryCard card)
    {
        if (card.Entry is not { } entry) return;
        var key = CardKey(card);
        if (_renderMode)
        {
            Converted(key, FeedCard.BodyMarkdown(entry));
            return;
        }
        if (!_converting.Add(key)) return;   // already on its way; it lands in the slot if still shown
        Task.Run(() => FeedCard.BodyMarkdown(entry)).ContinueWith(t => Dispatcher.UIThread.Post(() =>
        {
            _converting.Remove(key);
            if (!IsVisible) return;
            Converted(key, t.IsCompletedSuccessfully ? t.Result : "");
        }));
    }

    private void Converted(string key, string md)
    {
        _markdown[key] = md;
        if (key == _shownKey && _bodySlot is { } slot)
        {
            slot.Child = Body(md);
            _cardHost.InvalidateArrange();
        }
    }

    // Converts the next card ahead of time, so › lands on a finished body.
    private void Prefetch()
    {
        if (_plan is null) return;
        var run = _plan.CurrentRun;
        int i = _plan.CardIndex + 1;
        if (i < run.Cards.Count && run.Cards[i] is { Kind: StoryCardKind.Entry } c && !_markdown.ContainsKey(CardKey(c)))
            Convert(c);
    }

    // ── Chrome ──────────────────────────────────────────────────────────────────────────────────────────────

    // The heads in the order frozen at Play; feeds added since go on the end.
    private List<FeedHead> TrayHeads()
    {
        var heads = _host.Current.Heads;
        return heads.Select((h, i) => (h, at: _trayOrder.IndexOf(h.SubId) is var k && k >= 0 ? k : _trayOrder.Count + i))
                    .OrderBy(x => x.at).Select(x => x.h).ToList();
    }

    private void RenderTray()
    {
        _tray.Children.Clear();
        string? current = CurrentSubId;
        Control? currentHit = null;
        foreach (var h in TrayHeads())
        {
            bool isCurrent = h.SubId == current;
            double d = isCurrent ? 36 : 28;
            var glyph = new HeadGlyph(_p) { Width = d, Height = d };
            glyph.Set(h.Title, _icon(h.IconPath), h.UnreadCount > 0, h.Error is not null, isCurrent);
            var hit = new Border
            {
                Child = glyph, Background = Brushes.Transparent, VerticalAlignment = VerticalAlignment.Center,
                Cursor = new Cursor(StandardCursorType.Hand), Padding = new Thickness(1),
            };
            ToolTip.SetTip(hit, h.UnreadCount > 0 ? $"{h.Title}  ·  {h.UnreadCount} new" : h.Title);
            string id = h.SubId;
            hit.PointerPressed += (_, e) =>
            {
                if (!e.GetCurrentPoint(hit).Properties.IsLeftButtonPressed) return;
                e.Handled = true;
                Dispatcher.UIThread.Post(() => SwitchFeed(id));   // the tray is rebuilt by the switch
            };
            if (isCurrent) currentHit = hit;
            _tray.Children.Add(hit);
        }
        if (currentHit is not null) Dispatcher.UIThread.Post(() => currentHit.BringIntoView());
    }

    // One segment per card of the current feed's run: watched ones filled, the current one in the accent.
    private void RenderSegments()
    {
        _segments.Children.Clear();
        _segments.ColumnDefinitions.Clear();
        if (_plan is null) return;
        int n = _plan.CurrentRun.Cards.Count;
        bool show = n > 1 || _plan.CurrentRun.Cards[0].Kind == StoryCardKind.Entry;
        _segments.IsVisible = show;
        if (!show) return;
        for (int i = 0; i < n; i++)
        {
            _segments.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
            var bar = new Border
            {
                Height = 3, CornerRadius = new CornerRadius(1.5),
                Background = i == _plan.CardIndex ? _p.Brand : i < _plan.CardIndex ? _p.Muted : _p.Border,
            };
            var hit = new Border
            {
                Child = bar, Background = Brushes.Transparent, Cursor = new Cursor(StandardCursorType.Hand),
                Padding = new Thickness(i == 0 ? 0 : 1.5, 5, i == n - 1 ? 0 : 1.5, 5),
            };
            int k = i;
            hit.PointerPressed += (_, e) =>
            {
                e.Handled = true;
                Dispatcher.UIThread.Post(() => JumpTo(k));
            };
            Grid.SetColumn(hit, i);
            _segments.Children.Add(hit);
        }
    }

    private void UpdateHeader()
    {
        if (_plan is null) return;
        var head = CurrentSubId is { } id ? _host.Head(id) : null;
        if (head is null)
        {
            _headGlyph.IsVisible = false;
            _feedTitle.Text = "Feeds";
            _age.Text = "";
            _menu.IsVisible = false;
            _header.Background = Brushes.Transparent;
            return;
        }
        _headGlyph.IsVisible = true;
        _headGlyph.Set(head.Title, _icon(head.IconPath), head.UnreadCount > 0, head.Error is not null, current: false);
        _feedTitle.Text = head.Title;
        _age.Text = _plan.Current.Entry is { } e ? RelativeTime.Ago(DateTime.UtcNow, e.Published ?? e.Updated) : "";
        _menu.IsVisible = true;
        // A faint wash of the feed's own colour behind the header, so feeds read apart as you move between them.
        var tint = OverlayCanvas.FallbackColor(head.Title);
        _header.Background = new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0, 0.5, RelativeUnit.Relative),
            EndPoint = new RelativePoint(1, 0.5, RelativeUnit.Relative),
            GradientStops =
            {
                new GradientStop(Color.FromArgb(_p.IsDark ? (byte)0x30 : (byte)0x24, tint.R, tint.G, tint.B), 0),
                new GradientStop(Color.FromArgb(0, tint.R, tint.G, tint.B), 1),
            },
        };
    }

    private void UpdateNav()
    {
        if (_plan is null) return;
        bool atStart = _plan.RunIndex == 0 && _plan.CardIndex == 0;
        bool atEnd = _plan.RunIndex == _plan.Runs.Count - 1 && _plan.CardIndex == _plan.CurrentRun.Cards.Count - 1;
        // Hidden rather than disabled: the gutter keeps its width, so the card doesn't shift.
        _prev.IsVisible = !atStart;
        _next.IsVisible = !(atEnd && _plan.IsReplay);
        var url = _plan.Current.Entry?.Url;
        // Dimmed by hand: Fluent's disabled look all but erases the button on a light theme.
        _open.IsHitTestVisible = url is not null;
        _open.Opacity = url is not null ? 1 : 0.4;
        ToolTip.SetTip(_open, url is not null && Uri.TryCreate(url, UriKind.Absolute, out var u)
            ? FeedUrl.DisplayHost(u) + "  (Enter)" : null);
    }

    private void ShowMenu()
    {
        if (CurrentSubId is not { } id || _host.Head(id) is not { } head) return;
        var items = new List<Control>();
        if (head.UnreadCount > 0) items.Add(Item("Mark all read", () => _host.MarkAllRead(id)));
        items.Add(Item("Check now", () => _host.RefreshNow(id)));
        if (head.SiteUrl is { } site) items.Add(Item("Open website", () => PlatformServices.UrlOpener.Open(site)));
        items.Add(new Separator());
        items.Add(Item("Edit feed…", () => EditRequested?.Invoke(id)));
        new MenuFlyout { ItemsSource = items, Placement = PlacementMode.BottomEdgeAlignedRight }.ShowAt(_menu);
    }

    private static MenuItem Item(string header, Action onClick)
    {
        var item = new MenuItem { Header = header };
        item.Click += (_, _) => onClick();
        return item;
    }

    private void OpenEntry()
    {
        if (_plan?.Current.Entry?.Url is { } url) PlatformServices.UrlOpener.Open(url);
    }

    // ── Motion ──────────────────────────────────────────────────────────────────────────────────────────────

    // A short horizontal slide between cards; a longer one with a slight zoom between feeds. Reduce-motion (and the
    // headless render) skip it.
    private void Animate(int dir, bool feedChanged)
    {
        if (dir == 0 || _renderMode || Pulse.ReduceMotion)
        {
            _anim?.Stop();
            ApplyAnim(1);
            return;
        }
        _animFrom = dir * (feedChanged ? 90 : 36);
        _animMs = feedChanged ? FeedSlideMs : SlideMs;
        _animScale = feedChanged ? 0.94 : 1;
        _animStart = Environment.TickCount64;
        ApplyAnim(0);
        _anim ??= new DispatcherTimer(TimeSpan.FromMilliseconds(15), DispatcherPriority.Render, (_, _) => StepAnim());
        _anim.Start();
    }

    private void StepAnim()
    {
        double t = Math.Min(1, (Environment.TickCount64 - _animStart) / _animMs);
        ApplyAnim(t);
        if (t >= 1) _anim?.Stop();
    }

    private void ApplyAnim(double t)
    {
        double e = 1 - Math.Pow(1 - t, 3);   // ease-out cubic
        _shift.X = _animFrom * (1 - e);
        _scale.ScaleX = _scale.ScaleY = _animScale + (1 - _animScale) * e;
        _cardHost.Opacity = 0.3 + 0.7 * e;
    }

    // ── Input ───────────────────────────────────────────────────────────────────────────────────────────────

    private void OnKey(object? sender, KeyEventArgs e)
    {
        if (e.KeyModifiers != KeyModifiers.None) return;   // Ctrl+C on selected text etc. pass through
        switch (e.Key)
        {
            case Key.Right or Key.Space: Next(); break;
            case Key.Left: Prev(); break;
            case Key.Down: ScrollBody(48); break;
            case Key.Up: ScrollBody(-48); break;
            case Key.PageDown: ScrollBody(Page()); break;
            case Key.PageUp: ScrollBody(-Page()); break;
            case Key.Enter: OpenEntry(); break;
            case Key.Escape: Close(); break;
            default: return;
        }
        e.Handled = true;
    }

    private double Page() => Math.Max(48, (_cardScroll?.Viewport.Height ?? 0) - 40);

    private void ScrollBody(double dy)
    {
        if (_cardScroll is not { } sv) return;
        double max = Math.Max(0, sv.Extent.Height - sv.Viewport.Height);
        sv.Offset = new Vector(0, Math.Clamp(sv.Offset.Y + dy, 0, max));
    }

    // The tray / segments / header band drags the window (buttons and heads keep their clicks).
    private void DragFromChrome(object? sender, PointerPressedEventArgs e)
    {
        if (e.Handled || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        if (e.Source is Visual v && (v is Button || v.FindAncestorOfType<Button>() is not null)) return;
        BeginMoveDrag(e);
    }

    // ── Bits ────────────────────────────────────────────────────────────────────────────────────────────────

    private Button GlyphButton(string glyph, double size) => new()
    {
        Content = glyph, Foreground = _p.Muted, Background = Brushes.Transparent, BorderThickness = new Thickness(0),
        Padding = new Thickness(6, 0), FontSize = size, Cursor = new Cursor(StandardCursorType.Hand), Focusable = false,
        VerticalAlignment = VerticalAlignment.Center,
    };

    // A side gutter: the full card height is the hit target, the chevron brightens on hover. Plain borders rather
    // than Buttons, so there's no theme hover/disabled fill across the whole strip.
    private Border Gutter(string glyph, string tip, Action onClick)
    {
        var text = new TextBlock
        {
            Text = glyph, FontSize = 26, Foreground = _p.Muted,
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
        };
        var b = new Border { Child = text, Background = Brushes.Transparent, Cursor = new Cursor(StandardCursorType.Hand) };
        b.PointerEntered += (_, _) => text.Foreground = _p.Title;
        b.PointerExited += (_, _) => text.Foreground = _p.Muted;
        b.PointerPressed += (_, e) =>
        {
            if (!e.GetCurrentPoint(b).Properties.IsLeftButtonPressed) return;
            e.Handled = true;
            onClick();
        };
        ToolTip.SetTip(b, tip);
        return b;
    }

    private Button PillButton(string text) => new()
    {
        Content = text, Foreground = _p.Text, Background = _p.Raised2, BorderBrush = _p.Border, BorderThickness = new Thickness(1),
        CornerRadius = SessionPalette.ButtonRadius, Padding = new Thickness(14, 6), FontSize = 12.5, Focusable = false,
        Cursor = new Cursor(StandardCursorType.Hand),
    };

    /// <summary>Headless-render seam: no slide, and bodies convert synchronously.</summary>
    internal void PrepareForRender() => _renderMode = true;

    /// <summary>Headless-render seam: step through the plan as the keys would.</summary>
    internal void StepForRender(int steps)
    {
        for (int i = 0; i < steps; i++) Next();
    }

    internal bool ShowingFeedForRender(string subId) => CurrentSubId == subId;

    /// <summary>Headless-render seam: scroll the card body.</summary>
    internal void ScrollForRender(double y) => ScrollBody(y);

    /// <summary>
    /// A feed's head: its icon inscribed in a disc (or its initials on its derived colour), inside a ring — the
    /// accent → brand story ring while it has unread entries, a quiet outline otherwise; a small warning dot when the
    /// last check failed. The overlay row's look, as a control.
    /// </summary>
    private sealed class HeadGlyph(SessionPalette p) : Control
    {
        private string _title = "";
        private Bitmap? _icon;
        private bool _unread, _error, _current;

        public void Set(string title, Bitmap? icon, bool unread, bool error, bool current)
        {
            _title = title;
            _icon = icon;
            _unread = unread;
            _error = error;
            _current = current;
            InvalidateVisual();
        }

        public override void Render(DrawingContext ctx)
        {
            double d = Math.Min(Bounds.Width, Bounds.Height);
            if (d <= 0) return;
            var c = new Point(Bounds.Width / 2, Bounds.Height / 2);
            double r = d / 2, ringW = _current ? 2.5 : 2, ri = r - ringW - 1.5;

            if (_icon is { } icon)
            {
                ctx.DrawEllipse(p.Raised2, null, c, ri, ri);
                double side = ri * Math.Sqrt(2);
                ctx.DrawImage(icon, new Rect(c.X - side / 2, c.Y - side / 2, side, side));
            }
            else
            {
                var tint = OverlayCanvas.FallbackColor(_title);
                ctx.DrawEllipse(OverlayDraw.Brush(tint, 0.22), null, c, ri, ri);
                var ft = OverlayDraw.Text(OverlayCanvas.Initials(_title), Math.Round(ri * 0.72), OverlayDraw.Brush(tint), FontWeight.Bold);
                ctx.DrawText(ft, new Point(c.X - ft.Width / 2, c.Y - ft.Height / 2));
            }

            if (_unread)
            {
                var ring = new LinearGradientBrush
                {
                    StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
                    EndPoint = new RelativePoint(1, 1, RelativeUnit.Relative),
                    GradientStops = { new GradientStop(p.Brand.Color, 0), new GradientStop(Palette.Brand, 1) },
                };
                ctx.DrawEllipse(null, OverlayDraw.Pen(ring, ringW), c, r - ringW / 2, r - ringW / 2);
            }
            else
            {
                ctx.DrawEllipse(null, OverlayDraw.Pen(_current ? p.Title : OverlayDraw.Brush(p.Muted.Color, 0.5), _current ? 1.5 : 1),
                    c, r - 0.75, r - 0.75);
            }

            if (_error)
            {
                var at = new Point(c.X + r * 0.72, c.Y - r * 0.72);
                ctx.DrawEllipse(p.Surface, null, at, 4.5, 4.5);
                ctx.DrawEllipse(p.Await, null, at, 3.2, 3.2);
            }
        }
    }
}
