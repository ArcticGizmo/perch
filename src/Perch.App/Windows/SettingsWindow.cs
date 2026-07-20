using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using Perch.Avalonia.Services;
using Perch.Avalonia.Theming;
using Perch.Data;
using Perch.Platform;

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

    /// <summary>Start (true) or stop (false) the account-usage poll.</summary>
    public Action<bool>? UsageEnabledChanged;

    /// <summary>Start (true) or stop (false) the Claude service-status poll.</summary>
    public Action<bool>? ServiceStatusEnabledChanged;

    /// <summary>Re-apply the service-status poll interval (the App reads the mutated setting back).</summary>
    public Action? ServiceStatusIntervalChanged;

#if DEBUG
    /// <summary>Push a sample outage onto the overlay so the status footer can be illustrated (debug only).</summary>
    public Action? TestServiceStatus;

    /// <summary>Fire a batch of 4 fake achievement unlocks through the real announce path, to test the
    /// post-update / first-run "several at once" behaviour (debug only).</summary>
    public Action? TestAchievementBatch;
#endif

    /// <summary>Reconfigure the system/per-session/subprocess metrics sampler.</summary>
    public Action? MetricsChanged;

    /// <summary>Rebuild the overlay's quick-links strip (re-resolving icons off-thread).</summary>
    public Action? QuickLinksChanged;

    /// <summary>Re-evaluate the ambient screen-edge glow (its enable toggle flipped).</summary>
    public Action? GlowChanged;

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
}

/// <summary>
/// The first-class settings window (the Avalonia port of the WinForms <c>SettingsForm</c>). A dark
/// window split into a fixed-width left navigation rail and a scrollable content area; the nav switches
/// between pages (Getting started, Usage, Indicators, Monitoring, Shortcuts, Session Stats,
/// Notifications, Quick Links, Experimental, About, Changelog). Reads/writes the shared
/// <see cref="AppSettings"/> and applies changes live through <see cref="SettingsHooks"/> so the overlay
/// and monitors stay in sync.
/// </summary>
internal sealed class SettingsWindow : Window
{
    private const double NavWidth = 178;

    private static readonly IBrush NavBg = new SolidColorBrush(Color.FromRgb(18, 18, 24));

    private readonly AppSettings _settings;
    private readonly UsageMonitorHost _usageHost;
    private readonly SettingsHooks _hooks;
    private readonly IAppIconProvider _icons;

    private Panel _contentHost = null!;
    private readonly Dictionary<string, Control> _pages = new();
    private readonly List<(string key, Button item)> _navItems = new();
    private string _currentKey = "";

    // About-page update controls + the current pending-update state (kept so a state change that arrives
    // while the window is open — or the state read at open time — reflects on the About page live).
    private TextBlock? _updateStatus;
    private Button? _updateNowBtn;
    private bool _updateAvailable;
    private string? _updateVersion;

    public SettingsWindow(AppSettings settings, UsageMonitorHost usageHost, SettingsHooks hooks, IAppIconProvider icons)
    {
        _settings = settings;
        _usageHost = usageHost;
        _hooks = hooks;
        _icons = icons;

        Title = "Perch Settings";
        Width = 880;
        Height = 660;
        MinWidth = 748;
        MinHeight = 560;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Background = Palette.FormBgBrush;
        try { Icon = new WindowIcon(AssetLoader.Open(new Uri("avares://perch/Assets/icon.ico"))); } catch { }

        BuildLayout();

        _usageHost.Updated += OnUsageUpdated;
        Closed += (_, _) => _usageHost.Updated -= OnUsageUpdated;
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Escape) { Close(); e.Handled = true; }
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

        _contentHost = new Panel { Margin = new Thickness(16) };
        var scroller = new ScrollViewer
        {
            Content = _contentHost,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        };

        AddPage(nav, "start",        "Getting started", BuildGettingStartedPage);
        AddPage(nav, "usage",        "Usage Limits",    BuildUsagePage);
        AddPage(nav, "indicators",   "Indicators",      BuildIndicatorsPage);
        AddPage(nav, "monitoring",   "Monitoring",      BuildMonitoringPage);
        AddPage(nav, "shortcuts",    "Shortcuts",       BuildHotkeysPage);
        AddPage(nav, "stats",        "Session Stats",   BuildStatsPage);
        AddPage(nav, "achievements", "Achievements",    BuildAchievementsPage);
        AddPage(nav, "notify",       "Notifications",   BuildNotificationsPage);
        AddPage(nav, "quicklinks",   "Quick Links",     BuildQuickLinksPage);
        AddPage(nav, "experimental", "Experimental",    BuildExperimentalPage);
        AddPage(nav, "about",        "About",           BuildAboutPage);
        AddPage(nav, "changelog",    "Changelog",       BuildChangelogPage);

        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*") };
        Grid.SetColumn(navHost, 0);
        Grid.SetColumn(scroller, 1);
        grid.Children.Add(navHost);
        grid.Children.Add(scroller);
        Content = grid;

        SelectPage("start");
    }

    private void AddPage(StackPanel nav, string key, string title, Action<StackPanel> build)
    {
        var page = new StackPanel { IsVisible = false };
        build(page);
        _pages[key] = page;
        _contentHost.Children.Add(page);
        AddNavItem(nav, key, title);
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
    }

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

        page.Children.Add(SettingsUi.TitleRow("Start automatically",
            SaveToggle(_settings.AutoStartOnFirstSession, v => _settings.AutoStartOnFirstSession = v)));
        page.Children.Add(SettingsUi.BodyText(
            "Launch Perch in the background when a Claude Code session opens and it isn't already " +
            "running. Requires the installed app — the SessionStart hook starts it via the \"perch\" " +
            "command on your PATH, so sessions run from a dev build (dotnet run) won't trigger it."));

        page.Children.Add(SettingsUi.Separator());

        page.Children.Add(SettingsUi.TitleRow("Close automatically",
            SaveToggle(_settings.AutoCloseAfterLastSession, v => _settings.AutoCloseAfterLastSession = v)));
        page.Children.Add(SettingsUi.BodyText(
            "Exit Perch a short while after the last Claude Code session ends — but only when it was " +
            "started automatically by the option above. A window you opened yourself stays open."));
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

    // ── Usage ───────────────────────────────────────────────────────────────────────
    private UsageBarsView _usageBars = null!;
    private PerchToggle _expectedRateToggle = null!;
    private Button _usageRefreshBtn = null!;

    // The two display toggles the overlay's right-click menu can also flip, kept as fields so
    // SyncDisplayToggles can mirror an out-of-band change back into this window.
    private PerchToggle _usageToggle = null!;
    private PerchToggle _systemMetricsToggle = null!;

    private void BuildUsagePage(StackPanel page)
    {
        _usageToggle = Toggle(_settings.ShowUsage);
        _usageToggle.CheckedChanged += (_, _) =>
        {
            _settings.ShowUsage = _usageToggle.IsChecked;
            _settings.Save();
            _hooks.DisplayChanged?.Invoke();
            _hooks.UsageEnabledChanged?.Invoke(_usageToggle.IsChecked);
            _usageRefreshBtn.IsEnabled = _usageToggle.IsChecked;
            _expectedRateToggle.IsEnabled = _usageToggle.IsChecked;
            _usageBars.SetOn(_usageToggle.IsChecked);
        };
        page.Children.Add(SettingsUi.TitleRow("Usage limits", _usageToggle));
        page.Children.Add(SettingsUi.BodyText("Your account-wide 5-hour and weekly rate-limit usage."));

        _expectedRateToggle = Toggle(_settings.ShowExpectedUsageRate);
        _expectedRateToggle.IsEnabled = _settings.ShowUsage;
        _expectedRateToggle.CheckedChanged += (_, _) =>
        {
            _settings.ShowExpectedUsageRate = _expectedRateToggle.IsChecked;
            _settings.Save();
            _hooks.DisplayChanged?.Invoke();
            _usageBars.SetShowExpectedRate(_expectedRateToggle.IsChecked);
        };
        page.Children.Add(SettingsUi.TitleRow("Show expected rate", _expectedRateToggle));

        _usageBars = new UsageBarsView { Margin = new Thickness(0, 2, 0, 6) };
        _usageBars.SetOn(_settings.ShowUsage);
        _usageBars.SetShowExpectedRate(_settings.ShowExpectedUsageRate);
        _usageBars.SetUsage(_usageHost.Last);
        page.Children.Add(_usageBars);

        var row = SettingsUi.ButtonRow();
        _usageRefreshBtn = SettingsUi.FlatButton("Refresh");
        _usageRefreshBtn.IsEnabled = _settings.ShowUsage;
        _usageRefreshBtn.Click += async (_, _) =>
        {
            if (!_settings.ShowUsage) return;
            _usageRefreshBtn.IsEnabled = false;
            var usage = await _usageHost.RefreshAsync();
            _usageBars.SetUsage(usage);
            _usageRefreshBtn.IsEnabled = _settings.ShowUsage;
        };
        row.Children.Add(_usageRefreshBtn);
        page.Children.Add(row);
    }

    // Keeps the settings usage bars in step with the shared poll while the window is open.
    private void OnUsageUpdated(UsageInfo usage) => _usageBars?.SetUsage(usage);

    /// <summary>Mirrors the usage/system-metrics flags back into their toggles after the overlay's
    /// right-click menu flips one, so the menu and this window never disagree. Uses the silent setter —
    /// the App-side handler has already persisted the flag and driven the hosts, so re-firing the toggle
    /// handlers would be redundant. Safe to call any time the window is open (pages build eagerly).</summary>
    public void SyncDisplayToggles()
    {
        _usageToggle.SetCheckedSilent(_settings.ShowUsage);
        _usageRefreshBtn.IsEnabled = _settings.ShowUsage;
        _expectedRateToggle.IsEnabled = _settings.ShowUsage;
        _usageBars.SetOn(_settings.ShowUsage);
        _systemMetricsToggle.SetCheckedSilent(_settings.ShowSystemMetrics);
    }

    // ── Indicators ──────────────────────────────────────────────────────────────────
    private void BuildIndicatorsPage(StackPanel page)
    {
        BuildModeBadgeSection(page);
        page.Children.Add(SettingsUi.Separator());
        BuildTaskProgressSection(page);
        page.Children.Add(SettingsUi.Separator());
        BuildNotesSection(page);
        page.Children.Add(SettingsUi.Separator());
        BuildWaitingTimerSection(page);
        page.Children.Add(SettingsUi.Separator());
        BuildArtifactsSection(page);
        page.Children.Add(SettingsUi.Separator());
        BuildContextPressureSection(page);
        page.Children.Add(SettingsUi.Separator());
        BuildDetectionSection(page);
        page.Children.Add(SettingsUi.Separator());
        BuildServiceStatusSection(page);
    }

    private void BuildServiceStatusSection(StackPanel page)
    {
        Button? testBtn = null;
        var intervalStepper = BuildStatusIntervalStepper(out var applyIntervalEnabled);

        var toggle = Toggle(_settings.ShowServiceStatus);
        void ApplyEnabled(bool on)
        {
            applyIntervalEnabled(on);
            if (testBtn is not null) testBtn.IsEnabled = on;
        }
        toggle.CheckedChanged += (_, _) =>
        {
            _settings.ShowServiceStatus = toggle.IsChecked;
            _settings.Save();
            _hooks.DisplayChanged?.Invoke();
            _hooks.ServiceStatusEnabledChanged?.Invoke(toggle.IsChecked);
            ApplyEnabled(toggle.IsChecked);
        };
        page.Children.Add(SettingsUi.TitleRow("Service status", toggle));
        page.Children.Add(SettingsUi.BodyText(
            "Shows an outage banner at the bottom of the overlay when status.claude.com reports a problem — " +
            "click it for the list of incidents and a link to the status page. Nothing shows while all " +
            "systems are operational."));

        page.Children.Add(SettingsUi.FieldCaption("Check for outages every"));
        page.Children.Add(intervalStepper);
        page.Children.Add(SettingsUi.BodyText(
            "How often Perch polls the status page. Checks are conditional, so an unchanged status barely " +
            "costs anything — this mainly bounds how quickly a new incident shows up."));

#if DEBUG
        var row = SettingsUi.ButtonRow();
        testBtn = SettingsUi.FlatButton("Show example outage");
        testBtn.Click += (_, _) => _hooks.TestServiceStatus?.Invoke();
        row.Children.Add(testBtn);
        page.Children.Add(row);
        page.Children.Add(SettingsUi.BodyText(
            "Pushes a sample outage to the overlay so you can see how the banner looks; a real status check " +
            "replaces it within a couple of minutes. (Debug builds only.)"));
#endif

        ApplyEnabled(_settings.ShowServiceStatus);
    }

    // A −/+ stepper for the service-status poll interval (minutes). Returns the row plus an enable hook so
    // the section can grey it out with the feature toggle. Mirrors BuildIdleStepper.
    private Control BuildStatusIntervalStepper(out Action<bool> applyEnabled)
    {
        const int min = 1, max = 60;
        var row = SettingsUi.ButtonRow();
        var dec = SettingsUi.FlatButton("−");
        var inc = SettingsUi.FlatButton("+");
        dec.Width = 36; inc.Width = 36;
        var value = new TextBlock
        {
            Width = 90, TextAlignment = TextAlignment.Center, Foreground = Palette.FgBrush,
            VerticalAlignment = VerticalAlignment.Center, FontSize = 14,
        };
        void Render() => value.Text = _settings.ServiceStatusIntervalMinutes == 1
            ? "1 minute" : $"{_settings.ServiceStatusIntervalMinutes} minutes";
        void Apply(int v)
        {
            v = Math.Clamp(v, min, max);
            if (v == _settings.ServiceStatusIntervalMinutes) return;
            _settings.ServiceStatusIntervalMinutes = v;
            _settings.Save();
            _hooks.ServiceStatusIntervalChanged?.Invoke();
            Render();
        }
        dec.Click += (_, _) => Apply(_settings.ServiceStatusIntervalMinutes - 1);
        inc.Click += (_, _) => Apply(_settings.ServiceStatusIntervalMinutes + 1);
        Render();
        row.Children.Add(dec);
        row.Children.Add(value);
        row.Children.Add(inc);
        applyEnabled = on =>
        {
            dec.IsEnabled = on; inc.IsEnabled = on;
            value.Foreground = on ? Palette.FgBrush : Palette.MutedBrush;
        };
        return row;
    }

    private void BuildModeBadgeSection(StackPanel page)
    {
        var legend = new ModeLegendView { Margin = new Thickness(0, 2, 0, 8) };
        var toggle = Toggle(_settings.ShowPermissionModeBadges);
        toggle.CheckedChanged += (_, _) =>
        {
            _settings.ShowPermissionModeBadges = toggle.IsChecked;
            _settings.Save();
            _hooks.DisplayChanged?.Invoke();
            legend.IsEnabled = toggle.IsChecked;
        };
        page.Children.Add(SettingsUi.TitleRow("Permission mode badges", toggle));
        page.Children.Add(SettingsUi.BodyText(
            "Each session's live permission mode is shown as a coloured badge next to that session in the " +
            "overlay (Perch tracks this through the hooks it wires into Claude Code automatically):"));
        legend.IsEnabled = _settings.ShowPermissionModeBadges;
        page.Children.Add(legend);
    }

    private void BuildTaskProgressSection(StackPanel page)
    {
        page.Children.Add(SettingsUi.TitleRow("Task progress",
            DisplayToggle(_settings.ShowTaskProgress, v => _settings.ShowTaskProgress = v)));
        page.Children.Add(SettingsUi.BodyText(
            "Shows a small \"done/total\" count next to a session that's working through a task list " +
            "(the native checklist Claude Code builds as it plans). It turns green when every task is " +
            "complete; hover it in the overlay for the full list."));
    }

    private void BuildNotesSection(StackPanel page)
    {
        page.Children.Add(SettingsUi.TitleRow("Session notes",
            DisplayToggle(_settings.ShowNotes, v => _settings.ShowNotes = v)));
        page.Children.Add(SettingsUi.BodyText(
            "Shows a pinned note on its own line under a session (add or edit one from a session's " +
            "right-click menu). With this off the note stays as a small glyph you can hover to read, " +
            "keeping the row compact. On by default."));
    }

    private void BuildWaitingTimerSection(StackPanel page)
    {
        page.Children.Add(SettingsUi.TitleRow("Waiting timer",
            DisplayToggle(_settings.ShowWaitingTimer, v => _settings.ShowWaitingTimer = v)));
        page.Children.Add(SettingsUi.BodyText(
            "Shows how long a session has been blocked waiting on you once it hits a prompt or permission " +
            "request — a \"waiting on you\" line with the elapsed time. It warms from yellow toward red the " +
            "longer it sits unanswered, so a session you've left hanging is easy to spot."));

        page.Children.Add(SettingsUi.FieldCaption("Minutes until fully red"));
        var minutesBox = SettingsUi.ThemedTextBox(_settings.WaitingTimerRedMinutes.ToString());
        minutesBox.Width = 64;
        void Commit()
        {
            int minutes = int.TryParse(minutesBox.Text?.Trim(), out var m)
                ? Math.Clamp(m, 1, 240)
                : _settings.WaitingTimerRedMinutes;
            minutesBox.Text = minutes.ToString();
            _settings.WaitingTimerRedMinutes = minutes;
            _settings.Save();
            _hooks.DisplayChanged?.Invoke();
        }
        minutesBox.LostFocus += (_, _) => Commit();
        minutesBox.KeyDown += (_, e) => { if (e.Key == Key.Enter) { Commit(); e.Handled = true; } };

        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Thickness(0, 0, 0, 8) };
        row.Children.Add(minutesBox);
        row.Children.Add(new TextBlock { Text = "minutes", Foreground = Palette.MutedBrush, VerticalAlignment = VerticalAlignment.Center });
        page.Children.Add(row);
    }

    private void BuildArtifactsSection(StackPanel page)
    {
        page.Children.Add(SettingsUi.TitleRow("Artifacts",
            DisplayToggle(_settings.ShowArtifacts, v => _settings.ShowArtifacts = v)));
        page.Children.Add(SettingsUi.BodyText(
            "Shows a clickable amber glyph next to a session that has published one or more web artifacts. " +
            "Click it in the overlay to open the artifact (or pick from a list when there are several)."));
    }

    private void BuildContextPressureSection(StackPanel page)
    {
        var slider = new ContextThresholdSliderView
        {
            Margin = new Thickness(0, 4, 0, 8), ShowGreenSegment = _settings.ShowContextGreenSegment,
        };
        var greenToggle = Toggle(_settings.ShowContextGreenSegment);
        var greenRow = SettingsUi.SubRow(
            "Show a green indicator below the first threshold instead of leaving it blank", greenToggle, out var greenLabel);

        void ApplyEnabled(bool on)
        {
            slider.IsEnabled = on;
            greenToggle.IsEnabled = on;
            greenLabel.Foreground = on ? Palette.FgBrush : Palette.MutedBrush;
        }

        var toggle = Toggle(_settings.ShowContextPressure);
        toggle.CheckedChanged += (_, _) =>
        {
            _settings.ShowContextPressure = toggle.IsChecked;
            _settings.Save();
            _hooks.DisplayChanged?.Invoke();
            ApplyEnabled(toggle.IsChecked);
        };
        page.Children.Add(SettingsUi.TitleRow("Context pressure", toggle));

        page.Children.Add(SettingsUi.BodyText(
            "Warns when a session is filling up its context window. A small thermometer appears next to " +
            "the session in the overlay once it crosses the first threshold, filling up and warming from " +
            "yellow to orange to red as the window approaches full. Drag the handles to set where it first " +
            "appears and where it turns orange and red."));
        page.Children.Add(SettingsUi.BodyText(
            "The window size is read from the model the session is running — the 1M-token beta is " +
            "recognised as such — so the gauge reflects the real headroom, not a fixed limit."));

        slider.SetValues(_settings.ContextPressureYellowPercent, _settings.ContextPressureOrangePercent, _settings.ContextPressureRedPercent);
        slider.RangeChanged += (y, o, r) =>
        {
            _settings.ContextPressureYellowPercent = y;
            _settings.ContextPressureOrangePercent = o;
            _settings.ContextPressureRedPercent = r;
            _settings.Save();
            _hooks.DisplayChanged?.Invoke();
        };
        page.Children.Add(slider);

        greenToggle.CheckedChanged += (_, _) =>
        {
            _settings.ShowContextGreenSegment = greenToggle.IsChecked;
            _settings.Save();
            _hooks.DisplayChanged?.Invoke();
            slider.ShowGreenSegment = greenToggle.IsChecked;
        };
        page.Children.Add(greenRow);

        ApplyEnabled(_settings.ShowContextPressure);
    }

    private void BuildDetectionSection(StackPanel page)
    {
        var master = Toggle(_settings.StuckDetectionEnabled);
        var errorToggle = Toggle(_settings.DetectErrorStreaks);
        var loopToggle = Toggle(_settings.DetectFailingLoops);
        var errorRow = SettingsUi.SubRow("Repeated failures — several tool calls fail in a row", errorToggle, out var errorLabel);
        var loopRow = SettingsUi.SubRow("Failing loops — the same action repeats and keeps failing", loopToggle, out var loopLabel);

        void Persist()
        {
            _settings.StuckDetectionEnabled = master.IsChecked;
            _settings.DetectErrorStreaks = errorToggle.IsChecked;
            _settings.DetectFailingLoops = loopToggle.IsChecked;
            _settings.Save();
            _hooks.DisplayChanged?.Invoke();
        }
        void ApplyEnabled()
        {
            bool on = master.IsChecked;
            errorToggle.IsEnabled = on;
            loopToggle.IsEnabled = on;
            errorLabel.Foreground = on ? Palette.FgBrush : Palette.MutedBrush;
            loopLabel.Foreground = on ? Palette.FgBrush : Palette.MutedBrush;
        }

        master.CheckedChanged += (_, _) => { ApplyEnabled(); Persist(); };
        errorToggle.CheckedChanged += (_, _) => Persist();
        loopToggle.CheckedChanged += (_, _) => Persist();

        page.Children.Add(SettingsUi.TitleRow("Stuck detection", master));
        page.Children.Add(SettingsUi.BodyText(
            "Flags a running session that looks stuck with an amber warning glyph in the overlay. " +
            "It's a heuristic — switch off whichever check is too eager, or the whole feature."));
        page.Children.Add(errorRow);
        page.Children.Add(loopRow);
        ApplyEnabled();
    }

    // ── Monitoring ─────────────────────────────────────────────────────────────────
    private void BuildMonitoringPage(StackPanel page)
    {
        page.Children.Add(SettingsUi.SectionTitle("Monitoring"));
        page.Children.Add(SettingsUi.BodyText(
            "Surface live CPU and memory use right in the overlay. Sampling only runs while one of these " +
            "is on and reads standard Windows performance counters — nothing leaves your machine."));

        page.Children.Add(SettingsUi.Separator());

        _systemMetricsToggle = Toggle(_settings.ShowSystemMetrics);
        _systemMetricsToggle.CheckedChanged += (_, _) =>
        {
            _settings.ShowSystemMetrics = _systemMetricsToggle.IsChecked;
            _settings.Save();
            _hooks.DisplayChanged?.Invoke();
            _hooks.MetricsChanged?.Invoke();
        };
        page.Children.Add(SettingsUi.TitleRow("System metrics", _systemMetricsToggle));
        page.Children.Add(SettingsUi.BodyText(
            "A whole-machine CPU and physical-RAM strip at the top of the overlay, above the sessions."));

        page.Children.Add(SettingsUi.Separator());

        var subToggle = Toggle(_settings.IncludeSubprocessMetrics);
        var subRow = SettingsUi.SubRow(
            "Include subprocesses — roll a session up over its whole process tree", subToggle, out var subLabel);

        void ApplyEnabled(bool on)
        {
            subToggle.IsEnabled = on;
            subLabel.Foreground = on ? Palette.FgBrush : Palette.MutedBrush;
        }

        var sessionToggle = Toggle(_settings.ShowSessionMetrics);
        sessionToggle.CheckedChanged += (_, _) =>
        {
            _settings.ShowSessionMetrics = sessionToggle.IsChecked;
            _settings.Save();
            _hooks.DisplayChanged?.Invoke();
            _hooks.MetricsChanged?.Invoke();
            ApplyEnabled(sessionToggle.IsChecked);
        };
        page.Children.Add(SettingsUi.TitleRow("Per-session metrics", sessionToggle));
        page.Children.Add(SettingsUi.BodyText(
            "A small CPU (top) and RAM (bottom) bar on each session row, coloured by load. Hover it in the " +
            "overlay for the exact figures. Sub-agents share their session's process, so their use rolls up " +
            "into the session's own bar rather than showing on their row."));

        subToggle.CheckedChanged += (_, _) =>
        {
            _settings.IncludeSubprocessMetrics = subToggle.IsChecked;
            _settings.Save();
            _hooks.MetricsChanged?.Invoke();
        };
        page.Children.Add(subRow);
        page.Children.Add(SettingsUi.BodyText(
            "On, a session's figure includes the MCP servers, shells and tools its claude process spawns — " +
            "the true cost of the session. Off, only the claude process itself is measured."));

        ApplyEnabled(_settings.ShowSessionMetrics);
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

    // ── Experimental ────────────────────────────────────────────────────────────────
    private void BuildExperimentalPage(StackPanel page)
    {
        page.Children.Add(SettingsUi.SectionTitle("Experimental"));
        page.Children.Add(SettingsUi.BodyText(
            "Opt-in switches for features still in development. They may change or break between updates."));

        page.Children.Add(SettingsUi.Separator());

        var teamsEnvToggle = Toggle(ClaudeUserSettings.IsAgentTeamsEnabled());
        teamsEnvToggle.CheckedChanged += (_, _) => ClaudeUserSettings.SetAgentTeamsEnabled(teamsEnvToggle.IsChecked);
        page.Children.Add(SettingsUi.TitleRow("Enable Agent Teams in Claude Code", teamsEnvToggle));
        page.Children.Add(SettingsUi.BodyText(
            "Sets the CLAUDE_CODE_EXPERIMENTAL_AGENT_TEAMS environment variable in your user settings " +
            "(~/.claude/settings.json). Claude Code reads it on launch, so restart any open sessions for it " +
            "to take effect."));
        page.Children.Add(SettingsUi.BodyText(
            "Once enabled, Perch surfaces teammates automatically as distinct, named rows in the overlay — " +
            "kept on the roster while they're alive, even when idle between messages from the lead."));

        page.Children.Add(SettingsUi.Separator());

        page.Children.Add(SettingsUi.TitleRow("Hide inactive members",
            DisplayToggle(_settings.HideInactiveTeamMembers, v => _settings.HideInactiveTeamMembers = v)));
        page.Children.Add(SettingsUi.BodyText(
            "Drops idle teammates — those waiting for the lead — from the overlay, so only teammates actively " +
            "working are shown. A hidden teammate reappears the moment it starts working again."));

        page.Children.Add(SettingsUi.Separator());

        var glowToggle = Toggle(_settings.ScreenEdgeGlow);
        glowToggle.CheckedChanged += (_, _) =>
        {
            _settings.ScreenEdgeGlow = glowToggle.IsChecked;
            _settings.Save();
            _hooks.GlowChanged?.Invoke();
        };
        page.Children.Add(SettingsUi.TitleRow("Ambient screen-edge glow", glowToggle));
        page.Children.Add(SettingsUi.BodyText(
            "Softly pulses a glow around the edge of the screen while a session needs you or is waiting on " +
            "input — a peripheral nudge you can catch without watching the overlay. It's click-through and " +
            "never takes focus, so it stays out of your way, and it fades out the moment you've dealt with the " +
            "session. Handy on a second monitor."));

        page.Children.Add(SettingsUi.Separator());

        page.Children.Add(SettingsUi.TitleRow("Perch reacts",
            SaveToggle(_settings.PerchReacts, v => _settings.PerchReacts = v)));
        page.Children.Add(SettingsUi.BodyText(
            "Lets the tray and overlay bird wear the mood of your sessions: it dozes when nothing's running, " +
            "perks up while sessions work, flags a \"!\" when one needs you, and visibly panics when a session " +
            "looks stuck. Pure whimsy on top of the usual status cues. On by default."));

        page.Children.Add(SettingsUi.Separator());

        page.Children.Add(SettingsUi.TitleRow("Token burn rate",
            DisplayToggle(_settings.ShowBurnRate, v => _settings.ShowBurnRate = v)));
        page.Children.Add(SettingsUi.BodyText(
            "Shows a live tokens-per-minute figure (e.g. \"12.3k/m\") next to a running session, measured over " +
            "its most recent burst of turns. The rate can swing quite a bit between turns, so it's here in " +
            "Experimental while that settles. Off by default."));

        page.Children.Add(SettingsUi.Separator());

        page.Children.Add(SettingsUi.TitleRow("Git line changes",
            DisplayToggle(_settings.ShowGitStats, v => _settings.ShowGitStats = v)));
        page.Children.Add(SettingsUi.BodyText(
            "Shows a \"+142 -37\" chip next to a session — the lines added (green) and deleted (red) in its " +
            "working directory that haven't been staged yet, read from git. While this is off, Perch never runs " +
            "git at all, so it costs nothing; while on, it runs a lightweight \"git diff\" per session on a " +
            "background thread, cached for a few seconds. Off by default."));

        page.Children.Add(SettingsUi.Separator());

        page.Children.Add(SettingsUi.TitleRow("Confetti finish 🎉",
            DisplayToggle(_settings.ConfettiFinish, v => _settings.ConfettiFinish = v)));
        page.Children.Add(SettingsUi.BodyText(
            "Adds a \"Confetti finish 🎉\" item to a session's right-click menu. Arm a session and a " +
            "party-popper icon appears on its row; the instant it next finishes, confetti erupts across the " +
            "screen and the arming is spent (it fires exactly once). The arming is never saved. Off by default."));
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

        _updateNowBtn = SettingsUi.FlatButton("Update now");
        _updateNowBtn.Click += (_, _) => _hooks.PerformUpdate?.Invoke();
        buttons.Children.Add(_updateNowBtn);
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

    private void RefreshUpdateUi()
    {
        if (_updateStatus is null || _updateNowBtn is null) return;
        _updateStatus.Text = _updateAvailable
            ? $"Version {_updateVersion} is ready to install. Click Update now to apply it and restart."
            : $"Currently running v{AppInfo.Version}. Perch checks for updates in the background and applies " +
              "new versions on the next launch.";
        _updateNowBtn.IsVisible = _updateAvailable;
    }

    private Control LinkRow(string text, string url)
    {
        var link = new TextBlock
        {
            Text = text, Foreground = Palette.AccentBrush, FontSize = 13,
            Cursor = new Cursor(StandardCursorType.Hand), Margin = new Thickness(0, 0, 0, 4),
            TextDecorations = TextDecorations.Underline,
        };
        link.PointerPressed += (_, _) => OpenUrl(url);
        return link;
    }

    // ── Changelog ────────────────────────────────────────────────────────────────
    private void BuildChangelogPage(StackPanel page)
    {
        string? markdown = LoadEmbeddedChangelog();
        if (markdown is null)
        {
            page.Children.Add(SettingsUi.BodyText("Changelog not available."));
            return;
        }

        foreach (var rawLine in markdown.Split('\n'))
        {
            var line = rawLine.TrimEnd('\r');
            if (line.StartsWith("## "))
                page.Children.Add(SettingsUi.SectionTitle(StripInlineMarkdown(line[3..])));
            else if (line.StartsWith("### "))
                page.Children.Add(new TextBlock
                {
                    Text = StripInlineMarkdown(line[4..]), FontSize = 13, FontWeight = FontWeight.Bold,
                    Foreground = Palette.FgBrush, Margin = new Thickness(0, 6, 0, 4),
                });
            else if (line.StartsWith("# ")) { /* the nav label already says "Changelog" */ }
            else if (line.StartsWith("- ") || line.StartsWith("* "))
                page.Children.Add(SettingsUi.BodyText("•  " + StripInlineMarkdown(line[2..])));
            else if (line == "---")
                page.Children.Add(SettingsUi.Separator());
            else if (line.StartsWith("> "))
                page.Children.Add(new TextBlock
                {
                    Text = StripInlineMarkdown(line[2..]), TextWrapping = TextWrapping.Wrap, FontSize = 13,
                    FontStyle = FontStyle.Italic, Foreground = Palette.MutedBrush, Margin = new Thickness(12, 0, 0, 6),
                });
            else if (line.Trim().Length > 0)
                page.Children.Add(SettingsUi.BodyText(StripInlineMarkdown(line)));
        }
    }

    private static string? LoadEmbeddedChangelog()
    {
        try
        {
            using var s = typeof(SettingsWindow).Assembly.GetManifestResourceStream("Perch.CHANGELOG.md");
            if (s is null) return null;
            using var reader = new StreamReader(s);
            return reader.ReadToEnd();
        }
        catch { return null; }
    }

    private static string StripInlineMarkdown(string text)
    {
        text = System.Text.RegularExpressions.Regex.Replace(text, @"\*\*(.*?)\*\*", "$1");
        text = System.Text.RegularExpressions.Regex.Replace(text, @"__(.*?)__", "$1");
        text = System.Text.RegularExpressions.Regex.Replace(text, @"\*(.*?)\*", "$1");
        text = System.Text.RegularExpressions.Regex.Replace(text, @"_(.*?)_", "$1");
        text = System.Text.RegularExpressions.Regex.Replace(text, @"`([^`]+)`", "$1");
        text = System.Text.RegularExpressions.Regex.Replace(text, @"\[([^\]]+)\]\([^)]+\)", "$1");
        return text;
    }

    private static void OpenUrl(string url)
    {
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true }); }
        catch { }
    }
}
