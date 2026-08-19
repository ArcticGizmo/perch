using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Perch.Avalonia.Services;
using Perch.Avalonia.Theming;
using Perch.Avalonia.Views;
using Perch.Data;
using Perch.Data.Hypertree;
using Perch.Data.Replay;
using Perch.Platform;
using Perch.Social;
using Perch.Theming;

namespace Perch.Avalonia.Windows;

/// <summary>
/// Live-apply callbacks the settings window raises so the owning <c>App</c> keeps the overlay, the
/// data-layer monitors, and the transient windows in sync as the user edits — the Avalonia counterpart
/// of the ~30 events the WinForms <c>SettingsForm</c> exposed, consolidated to the handful of distinct
/// actions the Avalonia head actually needs. Every one is optional; the window persists to
/// <see cref="AppSettings"/> regardless, so unhooked features still save.
/// </summary>
internal sealed class SettingsHooks
{
    /// <summary>Re-apply every overlay display gate + the monitor's data-layer flags (the App reads the
    /// mutated <see cref="AppSettings"/> back). Cheap and idempotent, so raised after any display change.</summary>
    public Action? DisplayChanged;

    /// <summary>Re-resolve and apply the active colour theme app-wide (the App reads the mutated
    /// <see cref="AppSettings.ActiveThemeId"/> back). Raised by the Appearance page after a theme is picked.</summary>
    public Action? ThemeChanged;

    /// <summary>Start (true) or stop (false) the account-usage poll.</summary>
    public Action<bool>? UsageEnabledChanged;

    /// <summary>Start (true) or stop (false) the Claude service-status poll.</summary>
    public Action<bool>? ServiceStatusEnabledChanged;

    /// <summary>Re-apply the service-status poll interval (the App reads the mutated setting back).</summary>
    public Action? ServiceStatusIntervalChanged;

    /// <summary>Start (true) or stop (false) listening to the system media session for the overlay strip.</summary>
    public Action<bool>? MediaEnabledChanged;

    /// <summary>Start (true) or stop (false) watching which app holds the microphone for the overlay strip.</summary>
    public Action<bool>? MicEnabledChanged;

    /// <summary>Start (true) or stop (false) polling Hypertree's status file for the overlay's branch strip.</summary>
    public Action<bool>? HypertreeEnabledChanged;

    /// <summary>Start (true) or stop (false) watching the Claude Code daemon's worker roster for the
    /// overlay's "daemon" section.</summary>
    public Action<bool>? DaemonProcessesEnabledChanged;

#if DEBUG
    /// <summary>Push a sample outage onto the overlay so the status footer can be illustrated (debug only).</summary>
    public Action? TestServiceStatus;

    /// <summary>Fire a batch of 4 fake achievement unlocks through the real announce path, to test the
    /// post-update / first-run "several at once" behaviour (debug only).</summary>
    public Action? TestAchievementBatch;

    /// <summary>Pop the post-update "what's new" window for an arbitrary (from, to) version range, to
    /// preview exactly what an update from one version to another would surface (debug only).</summary>
    public Action<string, string>? PreviewChangelog;
#endif

    /// <summary>Reconfigure the system/per-session/subprocess metrics sampler.</summary>
    public Action? MetricsChanged;

    /// <summary>Rebuild the overlay's quick-links strip (re-resolving icons off-thread).</summary>
    public Action? QuickLinksChanged;

    /// <summary>Re-register the global keyboard shortcuts after a binding was edited or toggled.</summary>
    public Action? HotkeysChanged;

    /// <summary>Preview a local desktop notification of the given kind.</summary>
    public Action<NotificationKind>? TestNotification;

    /// <summary>Send a test push through the configured external (ntfy) channel.</summary>
    public Action? TestExternalNotification;

    /// <summary>Run a user-initiated update check (explicit feedback via a toast).</summary>
    public Action? CheckForUpdates;

    /// <summary>Download and apply the pending update, then restart.</summary>
    public Action? PerformUpdate;

    public Action? OpenStats;
    public Action? OpenFlightPath;
    public Action? OpenAchievements;

    /// <summary>Open the drag-to-place initial-placement editor (also on the overlay header menu).</summary>
    public Action? OpenPlacements;

    /// <summary>Open the Social compose / friends windows (also on the overlay's right-click menu).</summary>
    public Action? OpenSocialCompose;
    public Action? OpenSocialFriends;

    /// <summary>Open the developer puppet-account testing tool (shown only when the debug flag is set).</summary>
    public Action? OpenSocialDebug;
}

/// <summary>
/// The first-class settings window (the Avalonia port of the WinForms <c>SettingsForm</c>). A dark
/// window split into a fixed-width left navigation rail and a scrollable content area; the nav switches
/// between pages (Getting started, Usage, Indicators, Monitoring, Shortcuts, Session Stats,
/// Notifications, Quick Links, Experimental, Export, About, Changelog). Reads/writes the shared
/// <see cref="AppSettings"/> and applies changes live through <see cref="SettingsHooks"/> so the overlay
/// and monitors stay in sync.
/// </summary>
internal sealed class SettingsWindow : Window
{
    private const double NavWidth = 178;

    private static readonly IBrush NavBg = Palette.SurfaceSunkenBrush;

    private readonly AppSettings _settings;
    private readonly UsageMonitorHost _usageHost;
    private readonly SettingsHooks _hooks;
    private readonly IAppIconProvider _icons;
    private readonly ISocialClient? _social;

    // The Social page rebuilds its body on every auth change (sign-in/out that may originate from the
    // overlay strip or menu). The status line sits outside the rebuilt body so a message survives a rebuild.
    private StackPanel? _socialBody;
    private TextBlock? _socialStatus;
    private Action<AuthState>? _socialAuthHandler;

    private Panel _contentHost = null!;
    private readonly Dictionary<string, Control> _pages = new();
    private readonly List<(string key, Button item)> _navItems = new();
    private string _currentKey = "";
    private SettingsSearchView? _search;
    private SettingsCatalogView? _catalog;

    // About-page update controls + the current pending-update state (kept so a state change that arrives
    // while the window is open — or the state read at open time — reflects on the About page live).
    private TextBlock? _updateStatus;
    private Button? _updateNowBtn;
    private bool _updateAvailable;
    private string? _updateVersion;

    public SettingsWindow(AppSettings settings, UsageMonitorHost usageHost, SettingsHooks hooks,
        IAppIconProvider icons, ISocialClient? social = null)
    {
        _settings = settings;
        _usageHost = usageHost;
        _hooks = hooks;
        _icons = icons;
        _social = social;

        Title = "Perch Settings";
        // Sized for the unified shell: just wide enough to show two catalogue card columns beside the docked
        // live preview, without a lot of empty gap — nav (178) + preview dock (~301) + a cards column that
        // fits two 320-wide cards plus the catalogue margins and scrollbar (~713).
        Width = 1220;
        Height = 858;
        MinWidth = 1040;
        MinHeight = 620;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Background = Palette.FormBgBrush;
        try { Icon = new WindowIcon(AssetLoader.Open(new Uri("avares://perch/Assets/icon.ico"))); } catch { }

        BuildLayout();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Escape) { Close(); e.Handled = true; }
        else if (e.Key == Key.F && e.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            SelectPage("search");
            e.Handled = true;
        }
        base.OnKeyDown(e);
    }

    // ── Shell ─────────────────────────────────────────────────────────────────────
    private void BuildLayout()
    {
        var nav = new StackPanel { Orientation = Orientation.Vertical, Margin = new Thickness(0, 8, 0, 0) };
        var navHost = new Border
        {
            Background = NavBg, Width = NavWidth,
            Child = new ScrollViewer { Content = nav, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled },
        };

        // Each page owns its own scroller (so the Features page can pin its live-preview dock beside a
        // scrolling card list); the content host just stacks them and toggles which is visible.
        _contentHost = new Panel();

        // Unified shell: Search + the Features catalogue (with its docked live preview) are the primary way
        // to change settings. The old pure-toggle pages — Usage, Indicators, Monitoring, Shortcuts,
        // Integrations, Music, Microphone — are retired: every one of their settings is now an inline card in
        // the catalogue. The pages kept below carry things the catalogue doesn't (yet): actions (open Stats /
        // Achievements, test notifications), a bespoke editor (Quick Links), the Agent Teams env toggle, or
        // non-settings content (Getting started, Export, About, Changelog). Their builders are now
        // unreachable dead code, excised in a follow-up once verified in the running app.
        AddPage(nav, "search",       "Search",          BuildSearchPage);
        AddAppearancePage(nav);
        AddFeaturesPage(nav);
        AddPage(nav, "start",        "Getting started", BuildGettingStartedPage);
        AddPage(nav, "stats",        "Session Stats",   BuildStatsPage);
        AddPage(nav, "achievements", "Achievements",    BuildAchievementsPage);
        AddPage(nav, "notify",       "Notifications",   BuildNotificationsPage);
        AddPage(nav, "social",       "Social",          BuildSocialPage);
        AddPage(nav, "shortcuts",    "Shortcuts",       BuildHotkeysPage);
        AddPage(nav, "quicklinks",   "Quick Links",     BuildQuickLinksPage);
        AddPage(nav, "export",       "Export",          BuildExportPage);
        AddPage(nav, "about",        "About",           BuildAboutPage);
        AddPage(nav, "changelog",    "Changelog",       BuildChangelogPage);

        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*") };
        Grid.SetColumn(navHost, 0);
        Grid.SetColumn(_contentHost, 1);
        grid.Children.Add(navHost);
        grid.Children.Add(_contentHost);
        Content = grid;

        SelectPage("features");
    }

    // The search page: a registry-driven filter over every setting (built once; owns its own field/results).
    private void BuildSearchPage(StackPanel page)
    {
        _search = new SettingsSearchView(_settings, _hooks) { Navigate = SelectPage };
        page.Children.Add(_search);
    }


    private void AddPage(StackPanel nav, string key, string title, Action<StackPanel> build)
    {
        var content = new StackPanel { Margin = new Thickness(16) };
        build(content);
        var page = new ScrollViewer
        {
            Content = content, IsVisible = false,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        };
        _pages[key] = page;
        _contentHost.Children.Add(page);
        AddNavItem(nav, key, title);
    }

    // The Features page is the unified shell's centrepiece: the surface catalogue on the left, a docked
    // live overlay preview on the right that re-applies the settings on every inline edit — so a change is
    // seen on the miniature overlay the moment it's made. Built directly (not via AddPage) because it owns
    // a two-column layout with its own card scroller rather than a single scrolling StackPanel.
    private void AddFeaturesPage(StackPanel nav)
    {
        var catalog = _catalog = new SettingsCatalogView(_settings, _hooks) { Navigate = SelectPage };
        var preview = new PreviewPane();
        preview.Apply(_settings);
        catalog.Changed += () => preview.Apply(_settings);

        var cards = new ScrollViewer
        {
            Content = new StackPanel { Margin = new Thickness(16), Children = { catalog } },
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        };
        Grid.SetColumn(cards, 0);

        var dock = BuildPreviewDock(preview);
        Grid.SetColumn(dock, 1);

        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), IsVisible = false };
        grid.Children.Add(cards);
        grid.Children.Add(dock);

        _pages["features"] = grid;
        _contentHost.Children.Add(grid);
        AddNavItem(nav, "features", "Features");
    }

    private StackPanel _themeList = null!;
    private readonly List<(string id, Button card)> _themeCards = new();

    // The Appearance page: pick a colour theme from a list of swatch cards (built-ins + your custom themes),
    // with the same docked live overlay preview the Features page uses, and a "Design a new theme…" button
    // that opens the designer. Selecting a theme applies it app-wide immediately (via the ThemeChanged hook),
    // repainting this window and the preview too — so the choice is seen at once.
    private void AddAppearancePage(StackPanel nav)
    {
        var preview = new PreviewPane();
        preview.Apply(_settings);

        _themeList = new StackPanel { Margin = new Thickness(16), Spacing = 10 };
        RebuildThemeList();

        var cardsScroll = new ScrollViewer
        {
            Content = _themeList, IsVisible = true,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        };
        Grid.SetColumn(cardsScroll, 0);

        var dock = BuildPreviewDock(preview);
        Grid.SetColumn(dock, 1);

        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), IsVisible = false };
        grid.Children.Add(cardsScroll);
        grid.Children.Add(dock);

        _pages["appearance"] = grid;
        _contentHost.Children.Add(grid);
        AddNavItem(nav, "appearance", "Appearance");
    }

    // (Re)populate the theme list — called on open and after a save/delete so custom themes stay in sync.
    private void RebuildThemeList()
    {
        _themeList.Children.Clear();
        _themeCards.Clear();

        _themeList.Children.Add(SettingsUi.SectionTitle("Theme"));
        _themeList.Children.Add(SettingsUi.BodyText(
            "Pick a colour theme — dark or light. The status colours (running, waiting, error) are tuned to " +
            "stay glanceable in every theme, and you can recolour them in the designer. Contrast is checked " +
            "against WCAG AA for every built-in theme."));

        foreach (var theme in ThemeCatalog.All(_settings.CustomThemes))
            _themeList.Children.Add(BuildThemeRow(theme));
        RestyleThemeCards();

        var design = SettingsUi.FlatButton("+  Design a new theme…");
        design.HorizontalAlignment = HorizontalAlignment.Left;
        design.Margin = new Thickness(0, 6, 0, 0);
        design.Click += (_, _) =>
        {
            var seed = ThemeCatalog.Resolve(_settings.ActiveThemeId, _settings.CustomThemes);
            _ = new ThemeDesignerWindow(_settings, seed, onSaved: RebuildThemeList).ShowDialog(this);
        };
        _themeList.Children.Add(design);

        // Import / export / share the active theme as a compact code (also QR-able).
        var shareRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Thickness(0, 2, 0, 0) };
        var import = SettingsUi.FlatButton("Import from clipboard");
        import.Click += async (_, _) =>
        {
            try
            {
                var text = Clipboard is null ? null : await Clipboard.TryGetTextAsync();
                if (ThemeCodec.Decode(text) is not { } imported) return;   // not a Perch theme code — ignore
                var id = UniqueThemeId(imported.Name);
                _settings.CustomThemes ??= new();
                _settings.CustomThemes.Add(imported with { Id = id });
                _settings.ActiveThemeId = id;
                _settings.Save();
                _hooks.ThemeChanged?.Invoke();
                RebuildThemeList();
            }
            catch { /* clipboard unavailable / bad data — no-op */ }
        };
        var export = SettingsUi.FlatButton("Copy active as code");
        export.Click += async (_, _) =>
        {
            try
            {
                if (Clipboard is not null)
                    await Clipboard.SetTextAsync(ThemeCodec.Encode(ActiveTheme()));
            }
            catch { }
        };
        shareRow.Children.Add(import);
        shareRow.Children.Add(export);
        _themeList.Children.Add(shareRow);
    }

    private Theme ActiveTheme() => ThemeCatalog.Resolve(_settings.ActiveThemeId, _settings.CustomThemes);

    // A unique custom-theme id from a display name (mirrors the designer's), so an imported theme doesn't
    // collide with an existing custom one or a built-in.
    private string UniqueThemeId(string name)
    {
        var slug = new string((name ?? "theme").ToLowerInvariant()
            .Select(ch => char.IsLetterOrDigit(ch) ? ch : '-').ToArray()).Trim('-');
        if (string.IsNullOrEmpty(slug)) slug = "theme";
        var baseId = "custom-" + slug;
        var taken = _settings.CustomThemes?.Select(t => t.Id).ToHashSet() ?? new();
        if (!taken.Contains(baseId) && !ThemeCatalog.IsBuiltIn(baseId)) return baseId;
        int n = 2;
        while (taken.Contains($"{baseId}-{n}")) n++;
        return $"{baseId}-{n}";
    }

    private void RestyleThemeCards()
    {
        foreach (var (id, card) in _themeCards)
        {
            bool active = id == _settings.ActiveThemeId;
            card.BorderBrush = active ? Palette.AccentBrush : Palette.BorderBrush;
            card.BorderThickness = new Thickness(active ? 2 : 1);
        }
    }

    // A theme row: the swatch card, plus a Delete button for custom (non-built-in) themes.
    private Control BuildThemeRow(Theme theme)
    {
        var id = theme.Id;
        var card = BuildThemeCard(theme);
        card.Click += (_, _) =>
        {
            if (_settings.ActiveThemeId == id) return;
            _settings.ActiveThemeId = id;
            _settings.Save();
            _hooks.ThemeChanged?.Invoke();   // repaints the whole app, including this window + preview
            RestyleThemeCards();
        };
        _themeCards.Add((id, card));

        if (ThemeCatalog.IsBuiltIn(id)) return card;

        var row = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto") };
        Grid.SetColumn(card, 0);

        // Edit re-opens the designer seeded from this custom theme; saving replaces it in place.
        var edit = SettingsUi.FlatButton("Edit");
        edit.VerticalAlignment = VerticalAlignment.Center;
        edit.Margin = new Thickness(8, 0, 0, 0);
        edit.Click += (_, _) =>
            _ = new ThemeDesignerWindow(_settings, theme, onSaved: RebuildThemeList, editingId: id).ShowDialog(this);
        Grid.SetColumn(edit, 1);

        var del = SettingsUi.FlatButton("Delete");
        del.VerticalAlignment = VerticalAlignment.Center;
        del.Margin = new Thickness(8, 0, 0, 0);
        del.Click += (_, _) =>
        {
            _settings.CustomThemes?.RemoveAll(t => t.Id == id);
            if (_settings.ActiveThemeId == id) _settings.ActiveThemeId = "midnight";
            _settings.Save();
            _hooks.ThemeChanged?.Invoke();
            RebuildThemeList();
        };
        Grid.SetColumn(del, 2);
        row.Children.Add(card);
        row.Children.Add(edit);
        row.Children.Add(del);
        return row;
    }

    // One theme option: a header (name + a Light/Dark tag) over a strip of swatches. Imported twins carry the
    // variant in their name too (e.g. "Nord (Dark)") so they're distinct in the tagless dropdowns as well.
    private Button BuildThemeCard(Theme t)
    {
        var swatches = new StackPanel
        {
            Orientation = Orientation.Horizontal, Spacing = 4, Margin = new Thickness(0, 8, 0, 0),
        };
        // Chrome/text/accent and the semantic status hues all come from the theme (per-glyph colouring), so
        // a card shows that theme's own glyph colours; the trailing brand swatch stays theme-independent.
        foreach (var rgb in new[]
                 {
                     t.Surface, t.SurfaceRaised, t.TextPrimary, t.Accent,
                     t.StatusRunning, t.StatusAwaiting,
                     t.StatusError, FixedColors.Default.Brand,
                 })
        {
            swatches.Children.Add(new Border
            {
                Width = 22, Height = 22, CornerRadius = new CornerRadius(4),
                Background = rgb.ToBrush(),
                BorderBrush = Palette.BorderBrush, BorderThickness = new Thickness(1),
            });
        }

        var name = new TextBlock
        {
            Text = t.Name, FontSize = 14, FontWeight = FontWeight.SemiBold, Foreground = Palette.TitleBrush,
            VerticalAlignment = VerticalAlignment.Center,
        };

        var tag = ThemeTag(t.IsDark ? "Dark" : "Light", Palette.MutedBrush, Palette.TrackBrush);
        DockPanel.SetDock(tag, Dock.Right);

        var header = new DockPanel();
        header.Children.Add(tag);
        header.Children.Add(name);

        return new Button
        {
            Content = new StackPanel { Children = { header, swatches } },
            Background = Palette.ButtonBgBrush,
            BorderBrush = Palette.BorderBrush, BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6), Padding = new Thickness(12, 10),
            HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Cursor = new Cursor(StandardCursorType.Hand),
        };
    }

    // A small pill tag on a theme card (variant label / pair badge).
    private static Border ThemeTag(string text, IBrush fg, IBrush bg, string? tooltip = null)
    {
        var pill = new Border
        {
            Background = bg, CornerRadius = new CornerRadius(4), Padding = new Thickness(6, 1),
            VerticalAlignment = VerticalAlignment.Center,
            Child = new TextBlock { Text = text, FontSize = 10, FontWeight = FontWeight.SemiBold, Foreground = fg },
        };
        if (tooltip is not null) ToolTip.SetTip(pill, tooltip);
        return pill;
    }

    private static Control BuildPreviewDock(PreviewPane preview)
    {
        var stack = new StackPanel { Margin = new Thickness(14, 16, 16, 16) };
        stack.Children.Add(new TextBlock
        {
            Text = "LIVE PREVIEW", FontSize = 11, FontWeight = FontWeight.SemiBold,
            Foreground = Palette.MutedBrush, Margin = new Thickness(2, 0, 0, 8),
        });
        stack.Children.Add(preview);

        return new Border
        {
            Width = 300, BorderThickness = new Thickness(1, 0, 0, 0), BorderBrush = Palette.BorderBrush,
            Child = new ScrollViewer
            {
                Content = stack,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            },
        };
    }

    private void AddNavItem(StackPanel nav, string key, string title)
    {
        var item = new Button
        {
            Content = title,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Left,
            VerticalContentAlignment = VerticalAlignment.Center,
            Background = NavBg, Foreground = Palette.MutedBrush,
            BorderThickness = new Thickness(3, 0, 0, 0), BorderBrush = Brushes.Transparent,
            CornerRadius = new CornerRadius(0), Padding = new Thickness(13, 0, 8, 0),
            Height = 44, FontSize = 14,
        };
        item.Click += (_, _) => SelectPage(key);
        nav.Children.Add(item);
        _navItems.Add((key, item));
    }

    /// <summary>Navigate to a page by key from outside (e.g. the overlay's "finish Social setup" click).</summary>
    public void NavigateTo(string key) => SelectPage(key);

    private void SelectPage(string key)
    {
        if (!_pages.TryGetValue(key, out var page)) return;
        _currentKey = key;

        foreach (var kv in _pages)
            kv.Value.IsVisible = kv.Key == key;

        foreach (var (k, item) in _navItems)
        {
            bool sel = k == key;
            item.Background = sel ? Palette.ButtonBgBrush : NavBg;
            item.Foreground = sel ? Palette.TitleBrush : Palette.MutedBrush;
            item.BorderBrush = sel ? Palette.AccentBrush : Brushes.Transparent;
        }

        // Scan for recordable sessions only when the Export page is first opened — it walks ~/.claude, so
        // there's no reason to pay for it on every Settings open.
        if (key == "export" && !_exportLoaded)
        {
            _exportLoaded = true;
            LoadExportSessions();
        }

        if (key == "search") _search?.FocusSearch();
    }

    /// <summary>Reflects an out-of-band settings change (e.g. a display flag flipped from the overlay's
    /// right-click menu) back into the open window: the catalogue rebuilds its cards from the current
    /// settings. The App-side handler has already persisted the flag and driven the hosts.</summary>
    public void SyncDisplayToggles() => _catalog?.Sync();

    // ── Toggle helpers ──────────────────────────────────────────────────────────────
    private static PerchToggle Toggle(bool initial)
    {
        var t = new PerchToggle();
        t.SetCheckedSilent(initial);
        return t;
    }

    // A toggle that persists a setting and re-applies the overlay display gates on change.
    private PerchToggle DisplayToggle(bool initial, Action<bool> set)
    {
        var t = Toggle(initial);
        t.CheckedChanged += (_, _) => { set(t.IsChecked); _settings.Save(); _hooks.DisplayChanged?.Invoke(); };
        return t;
    }

    // A toggle that only persists a setting (no live overlay effect needed).
    private PerchToggle SaveToggle(bool initial, Action<bool> set)
    {
        var t = Toggle(initial);
        t.CheckedChanged += (_, _) => { set(t.IsChecked); _settings.Save(); };
        return t;
    }

    // ── Getting started ─────────────────────────────────────────────────────────────
    // The Social page: sign in / out, and claim a handle — the same actions the overlay strip and its
    // right-click menu offer, gathered here with account state. Rebuilds its body on every auth change so it
    // reflects a sign-in/out that happened from the overlay while the window is open.
    private void BuildSocialPage(StackPanel page)
    {
        page.Children.Add(SettingsUi.SectionTitle("Social"));
        page.Children.Add(SettingsUi.BodyText(
            "Add friends by handle and see their statuses under the overlay. Enable \"Social feed\" on the Features page first."));

        if (_social is null)
        {
            page.Children.Add(SettingsUi.BodyText("Social isn't available in this build."));
            return;
        }

        _socialBody = new StackPanel { Spacing = 10, Margin = new Thickness(0, 6, 0, 6) };
        page.Children.Add(_socialBody);

        _socialStatus = SettingsUi.BodyText("");   // survives body rebuilds
        page.Children.Add(_socialStatus);

        // Reflect sign-in/out that originates elsewhere (the overlay strip / menu).
        _socialAuthHandler = _ => Dispatcher.UIThread.Post(RefreshSocialPage);
        _social.AuthChanged += _socialAuthHandler;
        Closed += (_, _) => { if (_socialAuthHandler is not null) _social.AuthChanged -= _socialAuthHandler; };

        RefreshSocialPage();
    }

    private void RefreshSocialPage()
    {
        if (_social is null || _socialBody is null) return;
        _socialBody.Children.Clear();
        var state = _social.Current;

        if (!state.SignedIn)
        {
            _socialBody.Children.Add(SettingsUi.BodyText("You're signed out."));
            var signIn = SettingsUi.FlatButton("Sign in with GitHub");
            signIn.Click += async (_, _) => await RunSocial(signIn, () => _social.SignInAsync(default));
            _socialBody.Children.Add(Left(signIn));
            return;
        }

        if (state.Me is null)
        {
            // Signed in, no handle yet — claim one inline.
            _socialBody.Children.Add(SettingsUi.BodyText(
                "Signed in. Pick a handle your friends will recognise — 3–20 of a–z, 0–9 or _."));
            var box = SettingsUi.ThemedTextBox("");
            box.PlaceholderText = "handle";
            box.Width = 200;
            var claim = SettingsUi.FlatButton("Claim");
            claim.Click += async (_, _) => await RunSocial(claim, () => _social.ClaimHandleAsync(box.Text ?? "", null, null, default));
            var row = SettingsUi.ButtonRow();
            row.Children.Add(new TextBlock { Text = "@", Foreground = Palette.MutedBrush, VerticalAlignment = VerticalAlignment.Center });
            row.Children.Add(box);
            row.Children.Add(claim);
            _socialBody.Children.Add(row);
            AddSignOut();
            return;
        }

        // Signed in with a handle.
        var me = state.Me;
        string who = me.DisplayName is { Length: > 0 } dn ? $"Signed in as @{me.Handle}  ({dn})" : $"Signed in as @{me.Handle}";
        _socialBody.Children.Add(new TextBlock { Text = who, FontSize = 14, Foreground = Palette.FgBrush });

        // These open the same overlay-first windows (compose / friends) as the overlay's right-click menu.
        var actions = SettingsUi.ButtonRow();
        var post = SettingsUi.FlatButton("Post a status…");
        post.Click += (_, _) => _hooks.OpenSocialCompose?.Invoke();
        var friends = SettingsUi.FlatButton("Friends…");
        friends.Click += (_, _) => _hooks.OpenSocialFriends?.Invoke();
        actions.Children.Add(post);
        actions.Children.Add(friends);
        _socialBody.Children.Add(actions);

        // Developer testing tool — only when the debug flag is set (env or .env.local PERCH_SOCIAL_DEBUG).
        // Drives a second "puppet" account so the whole loop can be tested from one machine.
        if (SocialDebug.Enabled)
        {
            _socialBody.Children.Add(SettingsUi.Separator());
            _socialBody.Children.Add(SettingsUi.FieldCaption("Developer"));
            var debug = SettingsUi.FlatButton("Testing tool (puppet account)…");
            debug.Click += (_, _) => _hooks.OpenSocialDebug?.Invoke();
            _socialBody.Children.Add(Left(debug));
        }

        AddSignOut();
    }

    private void AddSignOut()
    {
        var signOut = SettingsUi.FlatButton("Sign out");
        signOut.Click += async (_, _) => await RunSocial(signOut, () => _social!.SignOutAsync(default));
        _socialBody!.Children.Add(Left(signOut));
    }

    // Runs a Social action with a busy state + inline error surfacing. A successful action raises AuthChanged,
    // which rebuilds the body via RefreshSocialPage; the status line (outside the body) shows any error.
    private async Task RunSocial(Button btn, Func<Task> action)
    {
        btn.IsEnabled = false;
        if (_socialStatus is not null) _socialStatus.Text = "Working…";
        try
        {
            await action();
            if (_socialStatus is not null) _socialStatus.Text = "";
        }
        catch (SocialException ex) { if (_socialStatus is not null) _socialStatus.Text = ex.Message; }
        catch { if (_socialStatus is not null) _socialStatus.Text = "Something went wrong. Please try again."; }
        finally { btn.IsEnabled = true; }
    }

    private static Control Left(Control c) { c.HorizontalAlignment = HorizontalAlignment.Left; return c; }

    private void BuildGettingStartedPage(StackPanel page)
    {
        BuildBanner(page);

        page.Children.Add(SettingsUi.SectionTitle("What it does"));
        page.Children.Add(SettingsUi.BodyText(
            "•  See every active Claude Code session in one floating overlay — Idle, Running, or " +
            "Needs Attention at a glance. Click a session to jump to its terminal; drag the overlay " +
            "to dock it on the left or right."));
        page.Children.Add(SettingsUi.BodyText(
            "•  Get a desktop notification the moment a session finishes or is waiting on you."));
        page.Children.Add(SettingsUi.BodyText(
            "•  Push those same alerts to your phone or other devices via ntfy, so you're covered " +
            "when you're away from your desk."));
        page.Children.Add(SettingsUi.BodyText(
            "•  Keep an eye on your 5-hour and weekly usage limits without leaving your desktop."));
        page.Children.Add(SettingsUi.BodyText(
            "•  See each session's live permission mode (Plan, Accept edits, Auto, Bypass) badged in the " +
            "overlay — Perch wires this into Claude Code automatically, no plugin to install."));

        page.Children.Add(SettingsUi.Separator());

        BuildStartModeSection(page);

        page.Children.Add(SettingsUi.Separator());

        page.Children.Add(SettingsUi.TitleRow("Close automatically",
            SaveToggle(_settings.AutoCloseAfterLastSession, v => _settings.AutoCloseAfterLastSession = v)));
        page.Children.Add(SettingsUi.BodyText(
            "Exit Perch a short while after the last Claude Code session ends — but only when a session " +
            "start is what launched it. A window you opened yourself, or one started at login, stays open."));
    }

    // When Perch launches itself, as a dropdown sitting on the header row (the options say enough on their
    // own). "On session start" is read by perch-hook straight from settings.json, so saving is enough;
    // "at login" also has to be registered with the OS, which SyncLoginItem does both here and at startup
    // (so a stale path from an update is refreshed).
    private void BuildStartModeSection(StackPanel page)
    {
        // Order must match the StartMode enum ordinals (Off, OnSessionStart, OnLogin).
        var combo = SettingsUi.Dropdown(
            new[] { "Never", "When a Claude Code session starts", "When I log in" },
            (int)_settings.StartMode);
        combo.MinWidth = 280;
        combo.HorizontalAlignment = HorizontalAlignment.Right;
        combo.SelectionChanged += (_, _) =>
        {
            if (combo.SelectedIndex < 0) return;
            _settings.StartMode = (StartMode)combo.SelectedIndex;
            _settings.Save();
            App.SyncLoginItem(_settings.StartMode);
        };

        page.Children.Add(SettingsUi.TitleRow("Auto Start Perch", combo));
    }

    private void BuildBanner(StackPanel page)
    {
        var stack = new StackPanel
        {
            Orientation = Orientation.Vertical, HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 8, 0, 8), Spacing = 4,
        };
        try
        {
            var bmp = new Bitmap(AssetLoader.Open(new Uri("avares://perch/Assets/icon.png")));
            stack.Children.Add(new Image
            {
                Source = bmp, Width = 64, Height = 64,
                HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 6, 0, 6),
            });
        }
        catch { }
        stack.Children.Add(new TextBlock
        {
            Text = "Perch", FontSize = 22, FontWeight = FontWeight.Bold, Foreground = Palette.TitleBrush,
            HorizontalAlignment = HorizontalAlignment.Center,
        });
        stack.Children.Add(new TextBlock
        {
            Text = "Never miss what Claude's working on", FontSize = 14, Foreground = Palette.MutedBrush,
            HorizontalAlignment = HorizontalAlignment.Center,
        });
        page.Children.Add(stack);
    }


    // ── Shortcuts (global hotkeys) ───────────────────────────────────────────────────
    private void BuildHotkeysPage(StackPanel page)
    {
        page.Children.Add(SettingsUi.SectionTitle("Keyboard shortcuts"));
        page.Children.Add(SettingsUi.BodyText(
            "System-wide shortcuts that work even when Perch isn't focused. Click a shortcut to change it — " +
            "hold your modifiers (Ctrl / Alt / Shift) and press a letter, digit or Space. Switch one off " +
            "with its toggle if it clashes with another app."));

        page.Children.Add(SettingsUi.Separator());

        AddHotkeyRow(page, "Expand / collapse the overlay", _settings.HotkeyToggleDense,
            "Collapses the overlay to its slim dock strip, or expands it back to the full panel.");

        page.Children.Add(SettingsUi.Separator());

        AddHotkeyRow(page, "Jump to next session", _settings.HotkeyCycleSessions,
            "Focuses the terminal of the next active session, walking through them one press at a time.");

        page.Children.Add(SettingsUi.Separator());

        AddHotkeyRow(page, "Open session switcher", _settings.HotkeyOpenSwitcher,
            "Pops a search box in the middle of the screen — type or arrow to a session and press Enter. " +
            "Active sessions jump to their terminal; recently-closed ones reopen in a fresh one (Ctrl+Enter " +
            "copies the claude --resume command instead). Esc or clicking away dismisses it.");

#if WINDOWS
        page.Children.Add(SettingsUi.Separator());
        BuildReopenTerminalSection(page);
#endif
    }

#if WINDOWS
    // The terminal the switcher launches when reopening a closed session. Read live by App.ReopenSession, so
    // saving the setting is all that's needed — no re-registration hook.
    private void BuildReopenTerminalSection(StackPanel page)
    {
        page.Children.Add(SettingsUi.SectionTitle("Reopen sessions in"));
        page.Children.Add(SettingsUi.BodyText(
            "Which terminal the switcher launches to reopen a closed session (running claude --resume in its " +
            "working directory). If it can't be launched, the command is copied to your clipboard instead."));

        // Order must match the TerminalApp enum ordinals (Auto, WindowsTerminal, PowerShell, CommandPrompt).
        var combo = SettingsUi.Dropdown(
            new[] { "Automatic (Windows Terminal, else Command Prompt)", "Windows Terminal", "PowerShell", "Command Prompt" },
            (int)_settings.ReopenTerminal);
        combo.SelectionChanged += (_, _) =>
        {
            if (combo.SelectedIndex < 0) return;
            _settings.ReopenTerminal = (TerminalApp)combo.SelectedIndex;
            _settings.Save();
        };

        var row = SettingsUi.ButtonRow();
        row.Children.Add(combo);
        page.Children.Add(row);
    }
#endif

    private void AddHotkeyRow(StackPanel page, string title, HotkeyBinding binding, string desc)
    {
        var enableToggle = Toggle(binding.Enabled);
        var capture = new HotkeyCaptureButton(binding);

        enableToggle.CheckedChanged += (_, _) =>
        {
            binding.Enabled = enableToggle.IsChecked;
            capture.IsEnabled = binding.Enabled;
            SaveHotkeys();
        };
        capture.Changed += SaveHotkeys;
        capture.IsEnabled = binding.Enabled;

        page.Children.Add(SettingsUi.TitleRow(title, enableToggle));
        page.Children.Add(SettingsUi.BodyText(desc));
        var row = SettingsUi.ButtonRow();
        row.Children.Add(capture);
        page.Children.Add(row);
    }

    // Persist the (mutated-in-place) bindings and ask the App to re-register the OS hotkeys so an edit or
    // a toggle takes effect immediately.
    private void SaveHotkeys()
    {
        _settings.Save();
        _hooks.HotkeysChanged?.Invoke();
    }

    // ── Session Stats ────────────────────────────────────────────────────────────────
    private void BuildStatsPage(StackPanel page)
    {
        page.Children.Add(SettingsUi.SectionTitle("Session stats"));
        page.Children.Add(SettingsUi.BodyText(
            "Daily activity derived from your Claude Code transcripts — a summary line in the tray menu and " +
            "a full breakdown in the Session stats window (right-click the tray icon → Session stats)."));

        var openRow = SettingsUi.ButtonRow();
        var openBtn = SettingsUi.FlatButton("Open session stats…");
        openBtn.Click += (_, _) => _hooks.OpenStats?.Invoke();
        var flightBtn = SettingsUi.FlatButton("Open flight path…");
        flightBtn.Click += (_, _) => _hooks.OpenFlightPath?.Invoke();
        openRow.Children.Add(openBtn);
        openRow.Children.Add(flightBtn);
        page.Children.Add(openRow);
        page.Children.Add(SettingsUi.BodyText(
            "The flight path is a timeline of a day — one lane per session, coloured by state (active, " +
            "waiting on you, or stuck) across the hours."));

        page.Children.Add(SettingsUi.TitleRow("Show today's summary in the tray menu",
            SaveToggle(_settings.ShowTodayStatsInTray, v => _settings.ShowTodayStatsInTray = v)));

        page.Children.Add(SettingsUi.TitleRow("Show estimated cost",
            SaveToggle(_settings.ShowEstimatedCost, v => _settings.ShowEstimatedCost = v)));
        page.Children.Add(SettingsUi.BodyText(
            "Shows an \"equivalent API cost\" in the stats window — what the tokens would have cost on " +
            "pay-as-you-go API pricing, using built-in per-model rates. It's a usage-intensity signal, not " +
            "a bill (subscription usage isn't billed per token)."));

        page.Children.Add(SettingsUi.Separator());

        page.Children.Add(SettingsUi.SectionTitle("Active-time idle threshold"));
        page.Children.Add(SettingsUi.BodyText(
            "\"Active\" time is estimated from the gaps between transcript records. A gap longer than this " +
            "counts as you having stepped away, and is capped at the threshold. Default 5 minutes."));
        page.Children.Add(BuildIdleStepper());
    }

    // ── Achievements ─────────────────────────────────────────────────────────────────
    private PerchToggle? _celebrateToggle;

    /// <summary>Re-reads the "Celebrate new unlocks" setting into its toggle without firing the change
    /// handler — so the switch reflects it when it's turned off elsewhere (the reveal's "Don't show again"
    /// button) while this window is open.</summary>
    public void SyncAchievementCelebration() => _celebrateToggle?.SetCheckedSilent(_settings.NotifyOnAchievement);

    private void BuildAchievementsPage(StackPanel page)
    {
        page.Children.Add(SettingsUi.SectionTitle("Achievements"));
        page.Children.Add(SettingsUi.BodyText(
            "Collectible trophies earned from your lifetime Claude Code activity — sessions, streaks, tokens, " +
            "tool use and more. They unlock retroactively, so your history already counts. Locked trophies " +
            "show how close you are."));

        var openRow = SettingsUi.ButtonRow();
        var openBtn = SettingsUi.FlatButton("Open achievements…");
        openBtn.Click += (_, _) => _hooks.OpenAchievements?.Invoke();
        openRow.Children.Add(openBtn);
        page.Children.Add(openRow);

        page.Children.Add(SettingsUi.Separator());

        _celebrateToggle = SaveToggle(_settings.NotifyOnAchievement, v => _settings.NotifyOnAchievement = v);
        page.Children.Add(SettingsUi.TitleRow("Celebrate new unlocks", _celebrateToggle));
        page.Children.Add(SettingsUi.BodyText(
            "Play a full-screen card reveal when you cross a rare gold-tier achievement — up to three cards " +
            "side by side, plus a \"+N more\" card if a batch unlocked several at once. Turn this off to " +
            "unlock silently; your trophies still appear in the Achievements window."));

        page.Children.Add(SettingsUi.Separator());

        page.Children.Add(SettingsUi.TitleRow("Unlock toast messages",
            SaveToggle(_settings.AchievementToasts, v => _settings.AchievementToasts = v)));
        page.Children.Add(SettingsUi.BodyText(
            "Also pop a desktop toast for each unlock (a single summary for a big batch). This is on top of " +
            "the card reveal and can get noisy, so it's off by default."));

#if DEBUG
        page.Children.Add(SettingsUi.Separator());
        var batchRow = SettingsUi.ButtonRow();
        var batchBtn = SettingsUi.FlatButton("Simulate 4 unlocks at once");
        batchBtn.Click += (_, _) => _hooks.TestAchievementBatch?.Invoke();
        batchRow.Children.Add(batchBtn);
        page.Children.Add(batchRow);
        page.Children.Add(SettingsUi.BodyText(
            "(debug) Play the reveal for a batch of 4 fake unlocks — three cards plus a \"+1 more\" card."));
#endif
    }

    private Control BuildIdleStepper()
    {
        const int min = 1, max = 30;
        var row = SettingsUi.ButtonRow();
        var dec = SettingsUi.FlatButton("−");
        var inc = SettingsUi.FlatButton("+");
        dec.Width = 36; inc.Width = 36;
        var value = new TextBlock
        {
            Width = 72, TextAlignment = TextAlignment.Center, Foreground = Palette.FgBrush,
            VerticalAlignment = VerticalAlignment.Center, FontSize = 14,
        };
        void Render() => value.Text = $"{_settings.StatsActiveIdleMinutes} min";
        void Apply(int v)
        {
            v = Math.Clamp(v, min, max);
            if (v == _settings.StatsActiveIdleMinutes) return;
            _settings.StatsActiveIdleMinutes = v;
            _settings.Save();
            SessionStatsService.IdleThreshold = TimeSpan.FromMinutes(v);
            Render();
        }
        dec.Click += (_, _) => Apply(_settings.StatsActiveIdleMinutes - 1);
        inc.Click += (_, _) => Apply(_settings.StatsActiveIdleMinutes + 1);
        Render();
        row.Children.Add(dec);
        row.Children.Add(value);
        row.Children.Add(inc);
        return row;
    }

    // ── Export (replay recordings) ───────────────────────────────────────────────────
    private ComboBox? _exportCombo;
    private PerchToggle? _exportRedactToggle;
    private Button? _exportBtn;
    private Button? _exportRefreshBtn;
    private TextBlock? _exportStatus;
    private bool _exportLoaded;
    private IReadOnlyList<ReplaySessionInfo> _exportSessions = [];

    private void BuildExportPage(StackPanel page)
    {
        page.Children.Add(SettingsUi.SectionTitle("Export a session for replay"));
        page.Children.Add(SettingsUi.BodyText(
            "Capture a Claude Code session to a portable .perchreplay recording, then play it back through " +
            "the real Perch later — a repeatable demo, or a step-by-step repro of a bug, on any machine."));

        page.Children.Add(SettingsUi.Separator());

        page.Children.Add(SettingsUi.FieldCaption("Session"));
        _exportCombo = SettingsUi.Dropdown(["Scanning…"], 0);
        _exportCombo.IsEnabled = false;
        page.Children.Add(_exportCombo);

        var refreshRow = SettingsUi.ButtonRow();
        _exportRefreshBtn = SettingsUi.FlatButton("Rescan");
        _exportRefreshBtn.Click += (_, _) => LoadExportSessions();
        refreshRow.Children.Add(_exportRefreshBtn);
        page.Children.Add(refreshRow);

        _exportRedactToggle = Toggle(true);
        page.Children.Add(SettingsUi.TitleRow("Redact content (recommended)", _exportRedactToggle));
        page.Children.Add(SettingsUi.BodyText(
            "Scrub message text, tool input/output, file paths, titles and git branch — keeping only the " +
            "structure, timings, token counts and models replay needs, so the recording is safe to share. " +
            "Turn this off only for a private local repro you won't share: a raw recording contains your " +
            "session verbatim."));

        var exportRow = SettingsUi.ButtonRow();
        exportRow.Margin = new Thickness(0, 8, 0, 4);
        _exportBtn = SettingsUi.FlatButton("Export…");
        _exportBtn.IsEnabled = false;
        _exportBtn.Click += (_, _) => ExportSelected();
        exportRow.Children.Add(_exportBtn);
        page.Children.Add(exportRow);

        _exportStatus = SettingsUi.BodyText("");
        _exportStatus.IsVisible = false;
        page.Children.Add(_exportStatus);

        page.Children.Add(SettingsUi.Separator());

        page.Children.Add(SettingsUi.SectionTitle("Playing a recording back"));
        page.Children.Add(SettingsUi.BodyText(
            "Replay drives the real, unmodified Perch through the recording under a virtual clock. Run it " +
            "from a terminal:"));
        page.Children.Add(SettingsUi.CodeBlock("perch replay <path-to-recording>.perchreplay"));
        page.Children.Add(SettingsUi.BodyText(
            "A “Perch Replay” controller window opens with play/pause, a speed selector, a scrub bar, and " +
            "prev/next-marker buttons that jump between prompts, tool calls and sub-agent activity. While " +
            "replaying, the overlay is branded light-blue “Perch - Replay” so it can't be mistaken for live " +
            "sessions, and it runs alongside your real Perch without touching it."));
        page.Children.Add(SettingsUi.BodyText(
            "The recording is self-contained — copy it to another machine and replay it there; no original " +
            "session, process, or Claude account is needed."));

        // Session discovery is kicked off lazily the first time this page is shown (see SelectPage).
    }

    // Discovers recordable sessions off the UI thread (it walks ~/.claude), then repopulates the picker.
    private void LoadExportSessions()
    {
        if (_exportCombo is null) return;
        _exportRefreshBtn!.IsEnabled = false;
        _exportBtn!.IsEnabled = false;
        _exportCombo.ItemsSource = new[] { "Scanning…" };
        _exportCombo.SelectedIndex = 0;
        _exportCombo.IsEnabled = false;

        Task.Run(() => RecordingExporter.DiscoverSessions()).ContinueWith(t =>
        {
            IReadOnlyList<ReplaySessionInfo> sessions =
                t.IsCompletedSuccessfully ? t.Result : Array.Empty<ReplaySessionInfo>();
            Dispatcher.UIThread.Post(() =>
            {
                if (_exportCombo is null) return; // window closed mid-scan
                _exportSessions = sessions.Take(50).ToList();
                _exportRefreshBtn!.IsEnabled = true;
                if (_exportSessions.Count == 0)
                {
                    _exportCombo.ItemsSource = new[] { "No sessions found" };
                    _exportCombo.SelectedIndex = 0;
                    _exportCombo.IsEnabled = false;
                    _exportBtn!.IsEnabled = false;
                    return;
                }
                _exportCombo.ItemsSource = _exportSessions.Select(FormatSession).ToList();
                _exportCombo.SelectedIndex = 0;
                _exportCombo.IsEnabled = true;
                _exportBtn!.IsEnabled = true;
            });
        });
    }

    private static string FormatSession(ReplaySessionInfo s)
    {
        var where = string.IsNullOrEmpty(s.Cwd) ? s.SessionId[..Math.Min(8, s.SessionId.Length)] : PathLeaf.Of(s.Cwd);
        return $"{where}  ·  {s.LastActivityUtc.ToLocalTime():MMM d, HH:mm}  ·  {s.SizeBytes / 1024} KB";
    }

    // Prompts for a destination, then exports the picked session off the UI thread.
    private async void ExportSelected()
    {
        if (_exportCombo is null || _exportBtn is null) return;
        int idx = _exportCombo.SelectedIndex;
        if (idx < 0 || idx >= _exportSessions.Count) return;
        var session = _exportSessions[idx];
        bool redact = _exportRedactToggle!.IsChecked;

        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Export replay recording",
            SuggestedFileName = session.SessionId + ".perchreplay",
            DefaultExtension = "perchreplay",
            FileTypeChoices = [new FilePickerFileType("Perch replay") { Patterns = ["*.perchreplay"] }],
        });
        if (file is null) return;
        var path = file.Path.LocalPath;

        _exportBtn.IsEnabled = false;
        SetExportStatus("Exporting…");
        try
        {
            await Task.Run(() => RecordingExporter.Export([session], path, redact));
            SetExportStatus(redact
                ? $"Exported (redacted) to {path}"
                : $"Exported RAW to {path} — contains your session verbatim, keep it local.");
        }
        catch (Exception ex)
        {
            SetExportStatus($"Export failed: {ex.Message}");
        }
        finally
        {
            _exportBtn.IsEnabled = true;
        }
    }

    private void SetExportStatus(string text)
    {
        if (_exportStatus is null) return;
        _exportStatus.Text = text;
        _exportStatus.IsVisible = true;
    }

    // ── Notifications ────────────────────────────────────────────────────────────────
    private PerchToggle _notifyMasterToggle = null!;
    private readonly List<NotifyRow> _notifySubRows = new();
    private sealed record NotifyRow(TextBlock Label, PerchToggle Popup, PerchToggle Chime, Button Test, TextBlock PopupCap, TextBlock ChimeCap);

    private PerchToggle _externalToggle = null!;
    private TextBox _ntfyHostBox = null!;
    private TextBox _ntfyTopicBox = null!;
    private PerchToggle _remoteLinkToggle = null!;
    private TextBlock _remoteLinkLabel = null!;
    private PerchToggle _lockNotifyToggle = null!;
    private TextBlock _lockNotifyLabel = null!;
    private QrWindow? _topicQrWindow;

    private void BuildNotificationsPage(StackPanel page)
    {
        _notifyMasterToggle = Toggle(_settings.NotificationsEnabled);
        _notifyMasterToggle.CheckedChanged += (_, _) =>
        {
            _settings.NotificationsEnabled = _notifyMasterToggle.IsChecked;
            _settings.Save();
            ApplyNotifyEnabled();
        };
        page.Children.Add(SettingsUi.TitleRow("Notifications", _notifyMasterToggle));
        page.Children.Add(SettingsUi.BodyText(
            "Windows desktop notifications when a session needs you. Each type has a pop-up and an optional " +
            "chime (the built-in Windows sound, off by default). Turn the whole feature off, or just the " +
            "parts you don't want. Use Test to preview one."));

        page.Children.Add(BuildNotifyRow(
            "Done — a session finished working",
            _settings.NotifyOnDone, v => { _settings.NotifyOnDone = v; _settings.Save(); },
            _settings.ChimeOnDone, v => { _settings.ChimeOnDone = v; _settings.Save(); },
            NotificationKind.Done));

        page.Children.Add(BuildNotifyRow(
            "Waiting for input — a session is blocked on a prompt",
            _settings.NotifyOnWaitingInput, v => { _settings.NotifyOnWaitingInput = v; _settings.Save(); },
            _settings.ChimeOnWaitingInput, v => { _settings.ChimeOnWaitingInput = v; _settings.Save(); },
            NotificationKind.WaitingForInput));

        page.Children.Add(BuildNotifyRow(
            "API error — a session's last request failed (e.g. 529 Overloaded)",
            _settings.NotifyOnApiError, v => { _settings.NotifyOnApiError = v; _settings.Save(); },
            _settings.ChimeOnApiError, v => { _settings.ChimeOnApiError = v; _settings.Save(); },
            NotificationKind.ApiFailed));

        page.Children.Add(BuildNotifyRow(
            "PR finished — a tracked pull request was merged or closed",
            _settings.NotifyOnPrFinished, v => { _settings.NotifyOnPrFinished = v; _settings.Save(); },
            _settings.ChimeOnPrFinished, v => { _settings.ChimeOnPrFinished = v; _settings.Save(); },
            NotificationKind.PrFinished));

        page.Children.Add(BuildNotifyRow(
            "PR reviewed — a new review was added to a tracked PR",
            _settings.NotifyOnPrReviewed, v => { _settings.NotifyOnPrReviewed = v; _settings.Save(); },
            _settings.ChimeOnPrReviewed, v => { _settings.ChimeOnPrReviewed = v; _settings.Save(); },
            NotificationKind.PrReviewed));

        page.Children.Add(BuildNotifyRow(
            "PR approved — a tracked PR was approved",
            _settings.NotifyOnPrApproved, v => { _settings.NotifyOnPrApproved = v; _settings.Save(); },
            _settings.ChimeOnPrApproved, v => { _settings.ChimeOnPrApproved = v; _settings.Save(); },
            NotificationKind.PrApproved));

        // The PR banner is a second, independent surface for the PR state-change events — a full-row overlay
        // flash rather than a desktop toast — so it sits as a sub-row and stays live even when the master
        // toggle above is off (like the attention flash, it isn't a "desktop notification").
        var prBannerToggle = Toggle(_settings.PrFinishedOverlayBanner);
        prBannerToggle.CheckedChanged += (_, _) => { _settings.PrFinishedOverlayBanner = prBannerToggle.IsChecked; _settings.Save(); };
        page.Children.Add(SettingsUi.SubRow(
            "Also flash a full-row banner on the overlay for these PR events", prBannerToggle, out _));

        ApplyNotifyEnabled();

        page.Children.Add(SettingsUi.Separator());
        BuildExternalSection(page);
    }

    private Control BuildNotifyRow(
        string text, bool popupInitial, Action<bool> onPopup, bool chimeInitial, Action<bool> onChime, NotificationKind kind)
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Margin = new Thickness(16, 2, 0, 4) };
        var label = new TextBlock
        {
            Text = text, FontSize = 13, Foreground = Palette.FgBrush,
            TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 8, 0),
        };
        Grid.SetColumn(label, 0);

        var popup = Toggle(popupInitial);
        popup.CheckedChanged += (_, _) => onPopup(popup.IsChecked);
        var popupCap = SettingsUi.ToggleCaption("Pop-up");

        var chime = Toggle(chimeInitial);
        chime.CheckedChanged += (_, _) => onChime(chime.IsChecked);
        var chimeCap = SettingsUi.ToggleCaption("Chime");

        var test = SettingsUi.FlatButton("Test");
        test.Click += (_, _) => _hooks.TestNotification?.Invoke(kind);

        var right = new StackPanel
        {
            Orientation = Orientation.Horizontal, Spacing = 4, VerticalAlignment = VerticalAlignment.Center,
        };
        right.Children.Add(test);
        right.Children.Add(new Control { Width = 10 });
        right.Children.Add(popupCap);
        right.Children.Add(popup);
        right.Children.Add(new Control { Width = 10 });
        right.Children.Add(chimeCap);
        right.Children.Add(chime);
        Grid.SetColumn(right, 1);

        grid.Children.Add(label);
        grid.Children.Add(right);

        _notifySubRows.Add(new NotifyRow(label, popup, chime, test, popupCap, chimeCap));
        return grid;
    }

    private void ApplyNotifyEnabled()
    {
        bool on = _notifyMasterToggle.IsChecked;
        var capColor = on ? Palette.MutedBrush : Palette.BorderBrush;
        foreach (var r in _notifySubRows)
        {
            r.Popup.IsEnabled = on;
            r.Chime.IsEnabled = on;
            r.Test.IsEnabled = on;
            r.Label.Foreground = on ? Palette.FgBrush : Palette.MutedBrush;
            r.PopupCap.Foreground = capColor;
            r.ChimeCap.Foreground = capColor;
        }
    }

    private void BuildExternalSection(StackPanel page)
    {
        _externalToggle = Toggle(_settings.ExternalNotificationsEnabled);
        _externalToggle.CheckedChanged += (_, _) =>
        {
            _settings.ExternalNotificationsEnabled = _externalToggle.IsChecked;
            _settings.Save();
            ApplyExternalEnabled();
            _hooks.DisplayChanged?.Invoke();
        };
        page.Children.Add(SettingsUi.TitleRow("External notifications", _externalToggle));
        page.Children.Add(SettingsUi.BodyText(
            "Also push \"Done\" and \"Waiting for input\" alerts to your phone or other devices via ntfy. " +
            "Enter your server and topic below, then enable it per session by right-clicking that session " +
            "in the overlay."));

        string host = string.IsNullOrWhiteSpace(_settings.NtfyHost) ? "https://ntfy.sh" : _settings.NtfyHost!;
        _settings.NtfyHost = host;

        page.Children.Add(SettingsUi.FieldCaption("Server URL"));
        _ntfyHostBox = SettingsUi.ThemedTextBox(host);
        _ntfyHostBox.TextChanged += (_, _) => _settings.NtfyHost = _ntfyHostBox.Text;
        _ntfyHostBox.LostFocus += (_, _) => _settings.Save();
        page.Children.Add(_ntfyHostBox);

        page.Children.Add(SettingsUi.FieldCaption("Topic"));
        _ntfyTopicBox = SettingsUi.ThemedTextBox(_settings.NtfyTopic ?? "");
        _ntfyTopicBox.TextChanged += (_, _) => _settings.NtfyTopic = _ntfyTopicBox.Text;
        _ntfyTopicBox.LostFocus += (_, _) => _settings.Save();

        var genBtn = SettingsUi.FlatButton("Generate");
        genBtn.Click += (_, _) => { _ntfyTopicBox.Text = GenerateTopic(); _settings.Save(); };
        var qrBtn = SettingsUi.FlatButton("QR code");
        qrBtn.Click += (_, _) => ShowTopicQr();

        var topicRow = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto"), Margin = new Thickness(0, 0, 0, 8) };
        Grid.SetColumn(_ntfyTopicBox, 0);
        genBtn.Margin = new Thickness(8, 0, 0, 0); Grid.SetColumn(genBtn, 1);
        qrBtn.Margin = new Thickness(8, 0, 0, 0); Grid.SetColumn(qrBtn, 2);
        topicRow.Children.Add(_ntfyTopicBox);
        topicRow.Children.Add(genBtn);
        topicRow.Children.Add(qrBtn);
        page.Children.Add(topicRow);

        _lockNotifyToggle = Toggle(_settings.NotifyWhenLocked);
        _lockNotifyToggle.CheckedChanged += (_, _) => { _settings.NotifyWhenLocked = _lockNotifyToggle.IsChecked; _settings.Save(); };
        page.Children.Add(SettingsUi.SubRow("Notify any session while my screen is locked", _lockNotifyToggle, out _lockNotifyLabel));

        _remoteLinkToggle = Toggle(_settings.ExternalNotificationsIncludeRemoteLink);
        _remoteLinkToggle.CheckedChanged += (_, _) => { _settings.ExternalNotificationsIncludeRemoteLink = _remoteLinkToggle.IsChecked; _settings.Save(); };
        page.Children.Add(SettingsUi.SubRow("Include a claude.ai link for remote-controlled sessions", _remoteLinkToggle, out _remoteLinkLabel));

        var testRow = SettingsUi.ButtonRow();
        testRow.Margin = new Thickness(0, 4, 0, 4);
        var testBtn = SettingsUi.FlatButton("Send test notification");
        testBtn.Click += (_, _) => { _settings.Save(); _hooks.TestExternalNotification?.Invoke(); };
        testRow.Children.Add(testBtn);
        page.Children.Add(testRow);

        ApplyExternalEnabled();
    }

    private void ApplyExternalEnabled()
    {
        bool on = _externalToggle.IsChecked;
        _remoteLinkToggle.IsEnabled = on;
        _remoteLinkLabel.Foreground = on ? Palette.FgBrush : Palette.MutedBrush;
        _lockNotifyToggle.IsEnabled = on;
        _lockNotifyLabel.Foreground = on ? Palette.FgBrush : Palette.MutedBrush;
    }

    private static string GenerateTopic()
    {
        const string prefix = "perch-";
        const string chars = "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789";
        var buf = new char[64];
        prefix.CopyTo(0, buf, 0, prefix.Length);
        for (int i = prefix.Length; i < buf.Length; i++)
            buf[i] = chars[Random.Shared.Next(chars.Length)];
        return new string(buf);
    }

    private void ShowTopicQr()
    {
        var topic = _ntfyTopicBox.Text?.Trim() ?? "";
        if (topic.Length == 0) return;

        var host = _ntfyHostBox.Text?.Trim() ?? "";
        int scheme = host.IndexOf("://", StringComparison.Ordinal);
        if (scheme >= 0) host = host[(scheme + 3)..];
        host = host.Trim('/');

        var url = $"ntfy://{host}/{topic}";
        _topicQrWindow?.Close();
        _topicQrWindow = new QrWindow("ntfy subscription", url);
        _topicQrWindow.Closed += (_, _) => _topicQrWindow = null;
        _topicQrWindow.Show();
        _topicQrWindow.Activate();
    }

    // ── Quick links ───────────────────────────────────────────────────────────────
    private readonly List<QuickLink> _quickLinks = new();
    private StackPanel _quickLinksList = null!;
    private StackPanel _quickLinkPresets = null!;

    private void BuildQuickLinksPage(StackPanel page)
    {
        page.Children.Add(SettingsUi.BodyText(
            "Quick links are a row of icons below the usage bars in the overlay. Click an icon to open that " +
            "app, or bring it to the front if it's already running. Add a shortcut to any program on your PC; " +
            "use the toggle to show or hide one without removing it."));

        page.Children.Add(SettingsUi.Separator());

        _quickLinks.Clear();
        foreach (var l in _settings.QuickLinks ?? Enumerable.Empty<QuickLink>())
            _quickLinks.Add(l.Clone());

        _quickLinksList = new StackPanel { Margin = new Thickness(0, 0, 0, 8) };
        page.Children.Add(_quickLinksList);

        var addRow = SettingsUi.ButtonRow();
        var addBtn = SettingsUi.FlatButton("Add quick link…");
        addBtn.Click += async (_, _) => await AddOrEditQuickLink(null);
        addRow.Children.Add(addBtn);
        page.Children.Add(addRow);

        _quickLinkPresets = SettingsUi.ButtonRow();
        page.Children.Add(_quickLinkPresets);

        page.Children.Add(SettingsUi.Separator());

        page.Children.Add(SettingsUi.TitleRow("Upside-down icons",
            SaveToggle(_settings.UpsideDownQuickLinks, v => { _settings.UpsideDownQuickLinks = v; _hooks.QuickLinksChanged?.Invoke(); })));
        page.Children.Add(SettingsUi.BodyText("For when the world feels right way up and you'd rather it didn't."));

        RebuildQuickLinksList();
    }

    private void RebuildQuickLinksList()
    {
        _quickLinksList.Children.Clear();
        if (_quickLinks.Count == 0)
        {
            _quickLinksList.Children.Add(new TextBlock
            {
                Text = "No quick links yet — add one below.", Foreground = Palette.MutedBrush,
                Margin = new Thickness(0, 4, 0, 4),
            });
        }
        else
        {
            foreach (var link in _quickLinks)
                _quickLinksList.Children.Add(BuildQuickLinkRow(link));
        }
        RebuildPresetButtons();
    }

    private Control BuildQuickLinkRow(QuickLink link)
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto,Auto"), Margin = new Thickness(0, 0, 0, 6) };

        var toggle = Toggle(link.Enabled);
        toggle.VerticalAlignment = VerticalAlignment.Center;
        toggle.CheckedChanged += (_, _) => { link.Enabled = toggle.IsChecked; RaiseQuickLinksChanged(); };
        Grid.SetColumn(toggle, 0);

        var textStack = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 8, 0) };
        textStack.Children.Add(new TextBlock
        {
            Text = string.IsNullOrWhiteSpace(link.Name) ? "(unnamed)" : link.Name,
            FontSize = 14, FontWeight = FontWeight.Bold, Foreground = Palette.TitleBrush,
        });
        textStack.Children.Add(new TextBlock
        {
            Text = QuickLinkSubtitle(link), FontSize = 12, Foreground = Palette.MutedBrush,
            TextTrimming = TextTrimming.CharacterEllipsis,
        });
        Grid.SetColumn(textStack, 1);

        var edit = SettingsUi.FlatButton("Edit");
        edit.VerticalAlignment = VerticalAlignment.Center;
        edit.Click += async (_, _) => await AddOrEditQuickLink(link);
        Grid.SetColumn(edit, 2);

        var remove = SettingsUi.FlatButton("Remove");
        remove.Foreground = new SolidColorBrush(Palette.Danger);
        remove.Margin = new Thickness(6, 0, 0, 0);
        remove.VerticalAlignment = VerticalAlignment.Center;
        remove.Click += (_, _) => { _quickLinks.Remove(link); RebuildQuickLinksList(); RaiseQuickLinksChanged(); };
        Grid.SetColumn(remove, 3);

        grid.Children.Add(toggle);
        grid.Children.Add(textStack);
        grid.Children.Add(edit);
        grid.Children.Add(remove);
        return grid;
    }

    private static string QuickLinkSubtitle(QuickLink link)
    {
        if (!string.IsNullOrWhiteSpace(link.ExePath))
            return File.Exists(link.ExePath) ? link.ExePath : link.ExePath + "   ⚠ file not found";
        var resolved = link.ResolveExe();
        if (resolved != null) return resolved + "  (auto-detected)";
        return "Not found — install the app, or Edit to set its path";
    }

    private void RebuildPresetButtons()
    {
        _quickLinkPresets.Children.Clear();
        foreach (var preset in KnownApps.PresetNames)
        {
            if (_quickLinks.Any(l => string.Equals(l.Name, preset, StringComparison.OrdinalIgnoreCase)))
                continue;
            var btn = SettingsUi.FlatButton("+ " + preset);
            btn.Click += (_, _) =>
            {
                _quickLinks.Add(new QuickLink { Name = preset, Enabled = true });
                RebuildQuickLinksList();
                RaiseQuickLinksChanged();
            };
            _quickLinkPresets.Children.Add(btn);
        }
        _quickLinkPresets.IsVisible = _quickLinkPresets.Children.Count > 0;
    }

    private async System.Threading.Tasks.Task AddOrEditQuickLink(QuickLink? existing)
    {
        var dlg = new QuickLinkDialog(existing, _icons);
        bool ok = await dlg.ShowDialog<bool>(this);
        if (!ok) return;

        if (existing == null)
            _quickLinks.Add(new QuickLink { Name = dlg.LinkName, ExePath = dlg.LinkPath, Enabled = true });
        else
        {
            existing.Name = dlg.LinkName;
            existing.ExePath = dlg.LinkPath;
        }
        RebuildQuickLinksList();
        RaiseQuickLinksChanged();
    }

    private void RaiseQuickLinksChanged()
    {
        _settings.QuickLinks = _quickLinks.Select(l => l.Clone()).ToList();
        _settings.Save();
        _hooks.QuickLinksChanged?.Invoke();
    }


    // ── About ─────────────────────────────────────────────────────────────────────
    private void BuildAboutPage(StackPanel page)
    {
        page.Children.Add(SettingsUi.SectionTitle("About"));

        var header = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, Margin = new Thickness(0, 0, 0, 6) };
        try
        {
            var bmp = new Bitmap(AssetLoader.Open(new Uri("avares://perch/Assets/icon.png")));
            header.Children.Add(new Image { Source = bmp, Width = 32, Height = 32, VerticalAlignment = VerticalAlignment.Center });
        }
        catch { }
        header.Children.Add(new TextBlock
        {
            Text = $"Perch\nv{AppInfo.Version}", Foreground = Palette.FgBrush, VerticalAlignment = VerticalAlignment.Center,
        });
        page.Children.Add(header);

        page.Children.Add(LinkRow("GitHub repository", AppInfo.RepoUrl));
        page.Children.Add(LinkRow("Report an issue on GitHub", AppInfo.IssuesUrl));

        page.Children.Add(SettingsUi.Separator());

        page.Children.Add(SettingsUi.SectionTitle("Updates"));
        _updateStatus = SettingsUi.BodyText("");
        page.Children.Add(_updateStatus);

        var buttons = SettingsUi.ButtonRow();
        var checkBtn = SettingsUi.FlatButton("Check for updates");
        checkBtn.Click += (_, _) => _hooks.CheckForUpdates?.Invoke();
        buttons.Children.Add(checkBtn);

        // Only a self-updating (Velopack) install gets "Update now" — a portable copy is replaced by hand,
        // and RefreshUpdateUi says so instead. See InstallChannel.
        if (InstallChannel.SelfUpdates)
        {
            _updateNowBtn = SettingsUi.FlatButton("Update now");
            _updateNowBtn.Click += (_, _) => _hooks.PerformUpdate?.Invoke();
            buttons.Children.Add(_updateNowBtn);
        }
        page.Children.Add(buttons);

        RefreshUpdateUi();
    }

    /// <summary>Reflects the pending-update state on the About page (called by the App when the updater's
    /// availability changes, and once at open time). Safe to call before/after the page is built.</summary>
    public void SetUpdateAvailable(bool available, string? version)
    {
        _updateAvailable = available;
        _updateVersion = version;
        RefreshUpdateUi();
    }

    // The wording follows the install channel: a Velopack install talks about "Update now", a portable copy
    // says it's replaced by hand. "Update now" only exists on the former, so the button may legitimately be
    // absent.
    private void RefreshUpdateUi()
    {
        if (_updateStatus is null) return;
        _updateStatus.Text = _updateAvailable
            ? $"Version {_updateVersion} is available. {InstallChannel.Instruction}"
            : $"Currently running v{AppInfo.Version}. {InstallChannel.OwnershipNote}";
        if (_updateNowBtn is not null) _updateNowBtn.IsVisible = _updateAvailable;
    }

    private Control LinkRow(string text, string url)
    {
        var link = new TextBlock
        {
            Text = text, Foreground = Palette.AccentBrush, FontSize = 13,
            Cursor = new Cursor(StandardCursorType.Hand), Margin = new Thickness(0, 0, 0, 4),
            TextDecorations = TextDecorations.Underline,
        };
        // Middle-click forces a fresh browser window (on the current virtual desktop); left-click reuses one.
        link.PointerPressed += (_, e) =>
        {
            if (e.GetCurrentPoint(link).Properties.IsMiddleButtonPressed)
                PlatformServices.UrlOpener.OpenInNewWindow(url);
            else
                OpenUrl(url);
        };
        return link;
    }

    // ── Changelog ────────────────────────────────────────────────────────────────
    private void BuildChangelogPage(StackPanel page)
    {
        page.Children.Add(SettingsUi.TitleRow("Show changelog after updates",
            SaveToggle(_settings.ShowChangelogOnUpdate, v => _settings.ShowChangelogOnUpdate = v)));
        page.Children.Add(SettingsUi.BodyText(
            "After Perch updates itself, pop a \"what's new\" window listing only the releases since the " +
            "version you were last on. On by default; you can also dismiss it for good from the window."));
        page.Children.Add(SettingsUi.Separator());

#if DEBUG
        page.Children.Add(SettingsUi.SectionTitle("Preview (debug)"));
        var fromBox = SettingsUi.ThemedTextBox("v0.2.0");
        var toBox = SettingsUi.ThemedTextBox("v" + AppInfo.Version);
        fromBox.Width = 96;
        toBox.Width = 96;
        var previewBtn = SettingsUi.FlatButton("Preview window");
        previewBtn.Click += (_, _) => _hooks.PreviewChangelog?.Invoke(fromBox.Text ?? "", toBox.Text ?? "");
        var previewRow = SettingsUi.ButtonRow();
        previewRow.VerticalAlignment = VerticalAlignment.Center;
        previewRow.Children.Add(SettingsUi.ToggleCaption("From"));
        previewRow.Children.Add(fromBox);
        previewRow.Children.Add(SettingsUi.ToggleCaption("to"));
        previewRow.Children.Add(toBox);
        previewRow.Children.Add(previewBtn);
        page.Children.Add(previewRow);
        page.Children.Add(SettingsUi.BodyText(
            "Pops the \"what's new\" window for the entries an update from one version to the other would " +
            "show (exclusive of \"From\", inclusive of \"to\"). (Debug builds only.)"));
        page.Children.Add(SettingsUi.Separator());
#endif

        string? markdown = ChangelogMarkdown.LoadEmbedded();
        if (markdown is null)
        {
            page.Children.Add(SettingsUi.BodyText("Changelog not available."));
            return;
        }

        ChangelogMarkdown.Render(page, markdown.Split('\n'));
    }

    private static void OpenUrl(string url) => PlatformServices.UrlOpener.Open(url);
}
