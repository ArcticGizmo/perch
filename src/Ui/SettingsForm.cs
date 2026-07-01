namespace Perch.Ui;
using Perch.Data;

using System.Diagnostics;
using System.Drawing.Drawing2D;
using System.Text.RegularExpressions;
using Perch.Ui;

/// <summary>
/// First-class settings window opened by left-clicking the tray icon. A dark-themed window split
/// into a fixed-width left navigation rail and a resizable content area. The nav switches between
/// pages: Getting started (banner, feature overview, permission-mode badge legend), Plugin Control,
/// Usage, Notifications (Windows desktop + external ntfy), and About (info + updates). Reads/writes
/// the shared <see cref="AppSettings"/> instance and drives <see cref="UsageMonitor"/> directly;
/// toggling usage and checking for updates are raised as events so the owning context keeps timers
/// and the overlay in sync.
/// </summary>
internal sealed class SettingsForm : Form
{
    private const int NavWidth = 178;
    private const int PagePad  = 16;

    // Slightly darker than the form body so the nav rail reads as a distinct sidebar.
    private static readonly Color NavBg = Color.FromArgb(18, 18, 24);

    private readonly AppSettings  _settings;
    private readonly UsageMonitor _usageMonitor;

    private readonly Bitmap? _icon = EmbeddedResources.LoadBitmap("Perch.icon.png");

    // Shell.
    private FlowLayoutPanel _navPanel    = null!;
    private Panel           _contentHost = null!;
    private readonly Dictionary<string, FlowLayoutPanel> _pages = new();
    private readonly List<(string key, Panel item, Label label, Panel accent)> _navItems = new();
    private string _currentKey = "";

    // Fluid-width bookkeeping. The window is resizable, so every full-width control is registered
    // here and re-sized whenever the content area changes. Width controls get their Width set to the
    // available width minus an optional inset; wrap labels get their MaximumSize updated so AutoSize
    // re-wraps to the new width.
    private readonly List<(Control c, int inset)> _fluidWidth = new();
    // Same as _fluidWidth but the inset is computed live, for controls whose reserved space depends
    // on sibling widths that themselves scale with DPI (e.g. the topic box sharing a row with the
    // auto-sized Generate/QR buttons).
    private readonly List<(Control c, Func<int> inset)> _fluidWidthDynamic = new();
    private readonly List<Label> _fluidWrap = new();

    // Usage section.
    private ToggleSwitch     _usageToggle        = null!;
    private ToggleSwitch     _expectedRateToggle = null!;
    private UsageBarsControl _usageBars          = null!;
    private Button           _usageRefreshBtn    = null!;

    // Notifications section. The master toggle gates the per-type sub-rows (a pop-up toggle, a chime
    // toggle, their captions, and the Test button), all of which dim while it's off.
    private ToggleSwitch _notifyMasterToggle = null!;
    private readonly List<NotifyRow> _notifySubRows = new();
    private sealed record NotifyRow(
        Label Label, ToggleSwitch Popup, ToggleSwitch Chime, Button Test, Label PopupCap, Label ChimeCap);

    // External notifications (ntfy) section. The host/topic boxes stay editable regardless of the
    // toggle, so they can be set up (and tested) before the feature is switched on.
    private ToggleSwitch _externalToggle = null!;
    private TextBox      _ntfyHostBox    = null!;
    private TextBox      _ntfyTopicBox   = null!;
    private QrCodeForm?  _topicQrForm;
    // Sub-row toggle: include the claude.ai deep link as an action for remote-controlled sessions.
    // Dimmed while the external master toggle is off.
    private ToggleSwitch _remoteLinkToggle = null!;
    private Label        _remoteLinkLabel  = null!;
    // Sub-row toggle: push any session's alert while the screen is locked, without a per-session
    // opt-in. Dimmed while the external master toggle is off.
    private ToggleSwitch _lockNotifyToggle = null!;
    private Label        _lockNotifyLabel  = null!;

    // Indicators section. The permission-mode-badge display toggle; its legend dims while off.
    private ToggleSwitch _modeBadgesToggle = null!;

    // Automation section. Two independent toggles persisted straight to settings: the SessionStart
    // hook reads auto-start from settings.json, and the owning context reads auto-close live, so
    // neither needs an event back to the owner.
    private ToggleSwitch _autoStartToggle = null!;
    private ToggleSwitch _autoCloseToggle = null!;

    // Detection section. A master toggle gating two heuristic sub-rows (repeated failures / failing
    // loops); all three states are read off the toggles and raised together via StuckDetectionChanged,
    // and the sub-rows dim while the master is off.
    private ToggleSwitch _stuckMasterToggle = null!;
    private ToggleSwitch _stuckErrorToggle  = null!;
    private ToggleSwitch _stuckLoopToggle   = null!;
    private Label        _stuckErrorLabel   = null!;
    private Label        _stuckLoopLabel    = null!;

    // Quick links section. The working list is an editable copy of the saved links; every mutation
    // (add/edit/remove/toggle) rewrites the visible rows and raises QuickLinksChanged. _quickLinksList
    // is the (manually height-managed) container the rows are rebuilt into.
    private readonly List<QuickLink> _quickLinks = new();
    private FlowLayoutPanel _quickLinksList = null!;
    private FlowLayoutPanel _quickLinkPresets = null!;

    private UsageInfo _usage;

    /// <summary>Raised when the user toggles "Show usage limits" (true = enabled).</summary>
    public event Action<bool>? UsageEnabledChanged;

    /// <summary>Raised when the user toggles "Show expected rate marker" (true = enabled).</summary>
    public event Action<bool>? ExpectedRateChanged;

    /// <summary>Raised when the user toggles "Show context pressure" (true = enabled).</summary>
    public event Action<bool>? ContextPressureChanged;

    /// <summary>Raised when the user toggles the green "first segment" indicator (true = the
    /// below-yellow band is drawn green instead of left blank).</summary>
    public event Action<bool>? ContextGreenSegmentChanged;

    /// <summary>Raised when the user toggles "Permission mode badges" (true = shown in the overlay).</summary>
    public event Action<bool>? PermissionModeBadgesChanged;

    /// <summary>Raised when the user toggles "Task progress" (true = the n/m count is shown in the overlay).</summary>
    public event Action<bool>? TaskProgressChanged;

    /// <summary>Raised when the user toggles "Artifacts" (true = the clickable artifact glyph is shown).</summary>
    public event Action<bool>? ArtifactsChanged;

    /// <summary>Raised when the user toggles "Hide inactive members" (true = idle teammates are dropped
    /// from the overlay roster).</summary>
    public event Action<bool>? HideInactiveTeamMembersChanged;

    /// <summary>Raised when the user adjusts the context-pressure thresholds (whole percentages,
    /// ordered yellow &lt; orange &lt; red).</summary>
    public event Action<int, int, int>? ContextThresholdsChanged;

    /// <summary>Raised when any stuck-detection switch changes. Carries the current state of all
    /// three: (master enabled, detect error streaks, detect failing loops).</summary>
    public event Action<bool, bool, bool>? StuckDetectionChanged;

    /// <summary>Raised when the user clicks "Check for Updates".</summary>
    public event EventHandler? CheckForUpdatesRequested;

    /// <summary>Raised when the user clicks a per-type "Test" button, to preview that notification.</summary>
    public event Action<NotificationKind>? TestNotificationRequested;

    /// <summary>Raised when the user toggles external (ntfy) notifications (true = enabled).</summary>
    public event Action<bool>? ExternalNotificationsEnabledChanged;

    /// <summary>Raised when the user clicks "Send test notification" for the external (ntfy) channel.</summary>
    public event Action? TestExternalNotificationRequested;

    /// <summary>Raised whenever the quick-links list changes (add/edit/remove/enable). Carries the
    /// full current list so the owning context can persist it and refresh the overlay.</summary>
    public event Action<IReadOnlyList<QuickLink>>? QuickLinksChanged;

    /// <summary>Raised when the upside-down quick-links toggle changes. Carries the new on/off state.</summary>
    public event Action<bool>? UpsideDownQuickLinksChanged;

    /// <summary>Raised when the user clicks "Open session stats", to open the stats window.</summary>
    public event Action? OpenStatsRequested;

    public SettingsForm(AppSettings settings, UsageMonitor usageMonitor, UsageInfo currentUsage)
    {
        _settings     = settings;
        _usageMonitor = usageMonitor;
        _usage        = currentUsage;

        Text            = "Perch Settings";
        FormBorderStyle = FormBorderStyle.Sizable;
        MaximizeBox     = true;
        MinimizeBox     = true;
        StartPosition   = FormStartPosition.CenterScreen;
        BackColor       = Theme.FormBg;
        ForeColor       = Theme.Fg;
        Font            = new Font("Segoe UI", 9f, FontStyle.Regular, GraphicsUnit.Point);
        MinimumSize     = new Size(748, 560);
        ClientSize      = new Size(880, 660);
        if (_icon != null)
            Icon = Icon.FromHandle(_icon.GetHicon());

        BuildLayout();
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        NativeMethods.UseDarkTitleBar(Handle);
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        foreach (var page in _pages.Values)
            NativeMethods.UseDarkScrollBars(page.Handle);
        ApplyFluidWidth();
    }

    // ── Shell ─────────────────────────────────────────────────────────────────────
    private void BuildLayout()
    {
        _navPanel = new FlowLayoutPanel
        {
            Dock          = DockStyle.Left,
            Width         = NavWidth,
            FlowDirection = FlowDirection.TopDown,
            WrapContents  = false,
            BackColor     = NavBg,
            Padding       = new Padding(0, 8, 0, 0),
        };

        _contentHost = new Panel { Dock = DockStyle.Fill, BackColor = Theme.FormBg };
        _contentHost.Resize += (_, _) => ApplyFluidWidth();

        AddPage("start",        "Getting started", BuildGettingStartedPage);
        AddPage("plugin",       "Plugin Control",  BuildPluginPage);
        AddPage("usage",        "Usage Limits",    BuildUsagePage);
        AddPage("indicators",   "Indicators",      BuildIndicatorsPage);
        AddPage("stats",        "Session Stats",   BuildStatsPage);
        AddPage("notify",       "Notifications",   BuildNotificationsPage);
        AddPage("quicklinks",   "Quick Links",      BuildQuickLinksPage);
        AddPage("experimental", "Experimental",    BuildExperimentalPage);
        AddPage("about",        "About",           BuildAboutPage);
        AddPage("changelog",    "Changelog",       BuildChangelogPage);

        // Add the Fill host first (so it sits behind) and the Left rail second, so the rail claims
        // its edge and the host fills the remainder.
        Controls.Add(_contentHost);
        Controls.Add(_navPanel);

        _usageBars.SetOn(_settings.ShowUsage);
        _usageBars.SetUsage(_usage);

        SelectPage("start");
    }

    // Builds a page panel, runs its content builder, and registers the matching nav item.
    private void AddPage(string key, string title, Action<FlowLayoutPanel> build)
    {
        var page = new FlowLayoutPanel
        {
            Dock          = DockStyle.Fill,
            FlowDirection = FlowDirection.TopDown,
            WrapContents  = false,
            AutoScroll    = true,
            Padding       = new Padding(PagePad),
            BackColor     = Theme.FormBg,
            Visible       = false,
        };
        build(page);
        // WinForms' AutoScroll sizes the scroll range to the bottom edge of the last child and omits
        // the panel's bottom Padding, so the final control sits flush against the clip edge and the
        // intended bottom gap (and the control's own bottom margin) can't be scrolled into view. A
        // trailing spacer the height of the page padding keeps that gap inside the scrollable region
        // on every page, so a shrunk window can still reach the last element.
        page.Controls.Add(new Panel { Width = 1, Height = PagePad, Margin = new Padding(0) });
        _pages[key] = page;
        _contentHost.Controls.Add(page);
        AddNavItem(key, title);
    }

    // A single nav rail entry: a full-width row with a left accent bar (shown when selected) and a
    // left-aligned label. Hover lightens the background unless the row is the active page.
    private void AddNavItem(string key, string title)
    {
        var item = new Panel
        {
            Width     = NavWidth,
            Height    = 44,
            Margin    = new Padding(0),
            Cursor    = Cursors.Hand,
            BackColor = NavBg,
        };
        var accent = new Panel { Dock = DockStyle.Left, Width = 3, BackColor = Theme.Accent, Visible = false };
        var label  = new Label
        {
            Text      = title,
            Dock      = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft,
            Padding   = new Padding(16, 0, 8, 0),
            ForeColor = Theme.Muted,
            BackColor = NavBg,
            Font      = new Font("Segoe UI", 10f, FontStyle.Regular, GraphicsUnit.Point),
        };
        item.Controls.Add(label);
        item.Controls.Add(accent);

        void Select() => SelectPage(key);
        item.Click  += (_, _) => Select();
        label.Click += (_, _) => Select();

        void Enter()
        {
            if (_currentKey == key) return;
            item.BackColor = Theme.ButtonBg;
            label.BackColor = Theme.ButtonBg;
        }
        void Leave()
        {
            if (_currentKey == key) return;
            item.BackColor = NavBg;
            label.BackColor = NavBg;
        }
        item.MouseEnter  += (_, _) => Enter();
        item.MouseLeave  += (_, _) => Leave();
        label.MouseEnter += (_, _) => Enter();
        label.MouseLeave += (_, _) => Leave();

        _navPanel.Controls.Add(item);
        _navItems.Add((key, item, label, accent));
    }

    // Shows the chosen page (hiding the rest) and restyles the nav rail to mark it active.
    private void SelectPage(string key)
    {
        if (!_pages.TryGetValue(key, out var page)) return;
        _currentKey = key;

        foreach (var kv in _pages)
            kv.Value.Visible = kv.Key == key;
        page.BringToFront();

        foreach (var (k, item, label, accent) in _navItems)
        {
            bool sel = k == key;
            accent.Visible  = sel;
            item.BackColor  = sel ? Theme.ButtonBg : NavBg;
            label.BackColor = sel ? Theme.ButtonBg : NavBg;
            label.ForeColor = sel ? Theme.Title    : Theme.Muted;
        }

        ApplyFluidWidth();
    }

    // The width available to full-width page controls: the content area minus page padding and a
    // reserved vertical-scrollbar gutter (so a scrolling page never also shows a horizontal bar).
    private int FluidWidth()
    {
        int w = _contentHost.ClientSize.Width - PagePad * 2 - (SystemInformation.VerticalScrollBarWidth + 4);
        return Math.Max(200, w);
    }

    // Re-flows every registered full-width control to the current available width.
    private void ApplyFluidWidth()
    {
        if (_contentHost is null) return;
        int w = FluidWidth();
        foreach (var (c, inset) in _fluidWidth)
            c.Width = Math.Max(40, w - inset);
        foreach (var (c, inset) in _fluidWidthDynamic)
            c.Width = Math.Max(40, w - inset());
        foreach (var l in _fluidWrap)
            l.MaximumSize = new Size(w, 0);
    }

    // Pins a single control to the right edge of a fluid-width row, vertically centred, and keeps it
    // there as the row resizes. The exact shape the section-title and notification sub-rows each
    // repeated (a label on the left, a toggle right-aligned).
    private void RegisterRightAlignedRow(Panel row, Control control)
    {
        void Position() => control.Location = new Point(row.Width - control.Width, (row.Height - control.Height) / 2);
        row.Resize += (_, _) => Position();
        _fluidWidth.Add((row, 0));
        Position();
    }

    // ── Getting started ─────────────────────────────────────────────────────────────
    private void BuildGettingStartedPage(FlowLayoutPanel page)
    {
        BuildBanner(page);

        page.Controls.Add(SectionTitle("What it does"));
        page.Controls.Add(BodyText(
            "•  See every active Claude Code session in one floating overlay — Idle, Running, or " +
            "Needs Attention at a glance. Click a session to jump to its terminal; drag the overlay " +
            "to dock it on the left or right."));
        page.Controls.Add(BodyText(
            "•  Get a desktop notification the moment a session finishes or is waiting on you."));
        page.Controls.Add(BodyText(
            "•  Push those same alerts to your phone or other devices via ntfy, so you're covered " +
            "when you're away from your desk."));
        page.Controls.Add(BodyText(
            "•  Keep an eye on your 5-hour and weekly usage limits without leaving your desktop."));
        page.Controls.Add(BodyText(
            "•  Install the companion Claude Code plugin for live permission-mode badges, /afk, and " +
            "/history."));

        page.Controls.Add(Separator());

        // Automation lives here rather than on its own tab: two independent toggles persisted straight
        // to settings (the SessionStart hook reads auto-start; the owning context reads auto-close).
        _autoStartToggle = MakeToggle();
        _autoStartToggle.Checked = _settings.AutoStartOnFirstSession;
        _autoStartToggle.CheckedChanged += (_, _) =>
        {
            _settings.AutoStartOnFirstSession = _autoStartToggle.Checked;
            _settings.Save();
        };
        page.Controls.Add(TitleRow("Start automatically", _autoStartToggle));

        page.Controls.Add(BodyText(
            "Launch Perch in the background when a Claude Code session opens and it isn't " +
            "already running. Requires the installed app — the plugin starts it via the " +
            "\"perch\" command on your PATH, so sessions run from a dev build (dotnet run) " +
            "won't trigger it."));

        page.Controls.Add(Separator());

        _autoCloseToggle = MakeToggle();
        _autoCloseToggle.Checked = _settings.AutoCloseAfterLastSession;
        _autoCloseToggle.CheckedChanged += (_, _) =>
        {
            _settings.AutoCloseAfterLastSession = _autoCloseToggle.Checked;
            _settings.Save();
        };
        page.Controls.Add(TitleRow("Close automatically", _autoCloseToggle));

        page.Controls.Add(BodyText(
            "Exit Perch a short while after the last Claude Code session ends — but only when " +
            "it was started automatically by the option above. A window you opened yourself stays open."));
    }

    // Centred app banner: the logo, the app name, and the tagline, all horizontally centred and
    // re-laid-out whenever the banner's width changes.
    private void BuildBanner(FlowLayoutPanel page)
    {
        var banner = new Panel { Height = 156, Margin = new Padding(0, 8, 0, 8), BackColor = Theme.FormBg };

        // Owner-drawn rather than a Zoom PictureBox: the source is a 256px PNG and PictureBox's
        // built-in scaler is low quality, so a 64px target comes out jagged. We paint it ourselves
        // with HighQualityBicubic, which also keeps it sharp at DPI-scaled sizes.
        PictureBox? pic = null;
        if (_icon is { } bannerIcon)
        {
            pic = new PictureBox { Size = new Size(64, 64), BackColor = Color.Transparent };
            pic.Paint += (_, e) =>
            {
                e.Graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
                e.Graphics.PixelOffsetMode   = PixelOffsetMode.HighQuality;
                e.Graphics.DrawImage(bannerIcon, pic.ClientRectangle);
            };
        }
        var name = new Label
        {
            Text      = "Perch",
            AutoSize  = true,
            ForeColor = Theme.Title,
            Font      = new Font("Segoe UI", 16f, FontStyle.Bold, GraphicsUnit.Point),
        };
        var tag = new Label
        {
            Text      = "Never miss what Claude's working on",
            AutoSize  = true,
            ForeColor = Theme.Muted,
            Font      = new Font("Segoe UI", 10f, FontStyle.Regular, GraphicsUnit.Point),
        };
        if (pic != null) banner.Controls.Add(pic);
        banner.Controls.Add(name);
        banner.Controls.Add(tag);

        void Layout()
        {
            int cx = banner.Width / 2;
            int y  = 14;
            if (pic != null) { pic.Location = new Point(cx - pic.Width / 2, y); y += pic.Height + 10; }
            name.Location = new Point(cx - name.Width / 2, y); y += name.Height + 4;
            tag.Location  = new Point(cx - tag.Width  / 2, y);
        }
        banner.Resize += (_, _) => Layout();
        _fluidWidth.Add((banner, 0));
        Layout();

        page.Controls.Add(banner);
    }

    // ── Plugin Control ────────────────────────────────────────────────────────────
    // The install commands for the perch Claude Code plugin (marketplace ref name@marketplace).
    private const string PluginInstallCommands =
        "/plugin marketplace add ArcticGizmo/perch\n/plugin install perch@perch --scope user";

    // Status of the perch plugin and the single action button (Enable / Update / Up to date).
    private Label   _pluginStatusLabel = null!;
    private Button  _pluginActionBtn   = null!;
    private Spinner _pluginSpinner     = null!;
    private PluginStatus _pluginStatus = PluginStatus.UpToDate;

    // One-click install/update of the perch plugin via the claude CLI. The button's label and
    // enabled-state follow an async status check (spinner shown while it runs); the manual commands
    // remain below as a fallback when the CLI isn't on PATH.
    private void BuildPluginPage(FlowLayoutPanel page)
    {
        page.Controls.Add(SectionTitle("Plugin Control"));

        page.Controls.Add(BodyText(
            "Perch pairs with a small Claude Code plugin. With it installed you get:"));
        page.Controls.Add(BodyText(
            "•  Live permission-mode badges next to each session in the overlay — Plan, Accept edits, " +
            "Auto, and Bypass."));
        page.Controls.Add(BodyText(
            "•  /afk — toggle external (phone) notifications for the current session without leaving " +
            "Claude Code."));
        page.Controls.Add(BodyText(
            "•  /history — open the current session's history in Perch."));
        page.Controls.Add(BodyText(
            "Perch can add the marketplace and install the plugin for you. If a newer version " +
            "is published later, use Update to pull it in."));

        // Action row: the Enable/Update button with a spinner beside it while a check or install runs.
        var row = ButtonRow();
        _pluginActionBtn = MakeButton("Enable");
        _pluginActionBtn.Enabled = false;
        _pluginActionBtn.Click += async (_, _) => await RunPluginActionAsync();
        row.Controls.Add(_pluginActionBtn);

        _pluginSpinner = new Spinner { Margin = new Padding(2, 4, 0, 0) };
        row.Controls.Add(_pluginSpinner);
        page.Controls.Add(row);

        _pluginStatusLabel = BodyText("Checking plugin status…");
        page.Controls.Add(_pluginStatusLabel);

        // Manual fallback for when the CLI isn't reachable from the app.
        page.Controls.Add(FieldCaption("Or run these in any Claude Code session:"));
        page.Controls.Add(CodeBlock(PluginInstallCommands));
        var copyRow = ButtonRow();
        var copyBtn = MakeButton("Copy install commands");
        copyBtn.Click += (_, _) => { try { Clipboard.SetText(PluginInstallCommands); } catch { } };
        copyRow.Controls.Add(copyBtn);
        page.Controls.Add(copyRow);

        // Kick off the initial status check (don't block the UI thread building the form).
        _ = RefreshPluginStatusAsync();
    }

    // Runs the async status check, driving the spinner and then the button/label.
    private async Task RefreshPluginStatusAsync()
    {
        SetPluginBusy("Checking plugin status…");
        var status = await new PluginManager().GetStatusAsync();
        if (IsDisposed) return;
        ApplyPluginStatus(status);
    }

    // Runs Enable or Update depending on the current status, then re-checks to refresh the button.
    private async Task RunPluginActionAsync()
    {
        var mgr = new PluginManager();
        bool updating = _pluginStatus == PluginStatus.UpdateAvailable;
        SetPluginBusy(updating ? "Updating the plugin…" : "Installing the plugin…");

        var (ok, message) = updating ? await mgr.UpdateAsync() : await mgr.EnableAsync();
        if (IsDisposed) return;

        if (!ok)
        {
            // Surface the failure but re-enable the button so the user can retry.
            _pluginSpinner.Spinning = false;
            _pluginStatusLabel.Text = message;
            _pluginActionBtn.Enabled = true;
            return;
        }

        // Re-check so the button settles to "Up to date" (or surfaces any remaining work).
        await RefreshPluginStatusAsync();
        if (!IsDisposed)
            _pluginStatusLabel.Text = message;
    }

    // Shows the spinner and disables the button while an async plugin operation is in flight.
    private void SetPluginBusy(string message)
    {
        _pluginSpinner.Spinning  = true;
        _pluginActionBtn.Enabled = false;
        _pluginStatusLabel.Text  = message;
    }

    // Maps a resolved status onto the button label/enabled-state and the status caption.
    private void ApplyPluginStatus(PluginStatus status)
    {
        _pluginStatus = status;
        _pluginSpinner.Spinning = false;

        switch (status)
        {
            case PluginStatus.NeedsEnable:
                _pluginActionBtn.Text    = "Enable";
                _pluginActionBtn.Enabled = true;
                _pluginStatusLabel.Text  = "Not installed yet.";
                break;
            case PluginStatus.UpdateAvailable:
                _pluginActionBtn.Text    = "Update";
                _pluginActionBtn.Enabled = true;
                _pluginStatusLabel.Text  = "A newer version is available.";
                break;
            case PluginStatus.UpToDate:
                _pluginActionBtn.Text    = "Up to date";
                _pluginActionBtn.Enabled = false;
                _pluginStatusLabel.Text  = "Installed and up to date.";
                break;
            case PluginStatus.CliMissing:
                _pluginActionBtn.Text    = "Enable";
                _pluginActionBtn.Enabled = false;
                _pluginStatusLabel.Text  = "claude CLI not found on PATH — run the commands below manually.";
                break;
        }
    }

    // ── Usage ───────────────────────────────────────────────────────────────────────
    private void BuildUsagePage(FlowLayoutPanel page)
    {
        _usageToggle = MakeToggle();
        _usageToggle.Checked = _settings.ShowUsage;
        _usageToggle.CheckedChanged += (_, _) =>
        {
            UsageEnabledChanged?.Invoke(_usageToggle.Checked);
            _usageRefreshBtn.Enabled    = _usageToggle.Checked;
            _expectedRateToggle.Enabled = _usageToggle.Checked;
            _usageBars.SetOn(_usageToggle.Checked);
        };
        page.Controls.Add(TitleRow("Usage limits", _usageToggle));

        page.Controls.Add(BodyText("Your account-wide 5-hour and weekly rate-limit usage."));

        _expectedRateToggle = MakeToggle();
        _expectedRateToggle.Checked = _settings.ShowExpectedUsageRate;
        _expectedRateToggle.Enabled = _settings.ShowUsage;
        _expectedRateToggle.CheckedChanged += (_, _) =>
        {
            ExpectedRateChanged?.Invoke(_expectedRateToggle.Checked);
            _usageBars.SetShowExpectedRate(_expectedRateToggle.Checked);
        };
        page.Controls.Add(TitleRow("Show expected rate", _expectedRateToggle));

        _usageBars = new UsageBarsControl { Margin = new Padding(0, 2, 0, 6) };
        _fluidWidth.Add((_usageBars, 0));
        page.Controls.Add(_usageBars);

        var row = ButtonRow();
        _usageRefreshBtn = MakeButton("Refresh");
        _usageRefreshBtn.Enabled = _settings.ShowUsage;
        _usageRefreshBtn.Click += async (_, _) =>
        {
            if (!_settings.ShowUsage) return;
            _usageRefreshBtn.Enabled = false;
            _usage = await _usageMonitor.FetchAsync();
            if (IsDisposed) return;
            _usageBars.SetUsage(_usage);
            _usageRefreshBtn.Enabled = _settings.ShowUsage;
        };
        row.Controls.Add(_usageRefreshBtn);
        page.Controls.Add(row);
    }

    // ── Indicators ──────────────────────────────────────────────────────────────────
    // Everything controlling the glyphs/badges shown next to a session in the overlay, in five
    // sections: permission-mode badges, task-list progress count, artifact glyph, context-pressure
    // thermometer (+ its threshold slider), and stuck-detection. Reuses the existing
    // ContextPressure*/StuckDetection* events; the badge, task-progress and artifact toggles are new.
    private void BuildIndicatorsPage(FlowLayoutPanel page)
    {
        BuildModeBadgeSection(page);
        page.Controls.Add(Separator());
        BuildTaskProgressSection(page);
        page.Controls.Add(Separator());
        BuildArtifactsSection(page);
        page.Controls.Add(Separator());
        BuildContextPressureSection(page);
        page.Controls.Add(Separator());
        BuildDetectionSection(page);
    }

    // Artifacts: a display toggle (default on). Raises ArtifactsChanged so the overlay redraws and
    // reclaims the freed width on the session name.
    private void BuildArtifactsSection(FlowLayoutPanel page)
    {
        var toggle = MakeToggle();
        toggle.Checked = _settings.ShowArtifacts;
        toggle.CheckedChanged += (_, _) => ArtifactsChanged?.Invoke(toggle.Checked);
        page.Controls.Add(TitleRow("Artifacts", toggle));

        page.Controls.Add(BodyText(
            "Shows a clickable amber glyph next to a session that has published one or more web " +
            "artifacts. Click it in the overlay to open the artifact (or pick from a list when there " +
            "are several)."));
    }

    // Task-list progress: a display toggle (default on). Raises TaskProgressChanged so the overlay
    // redraws and reclaims the freed width on the session name.
    private void BuildTaskProgressSection(FlowLayoutPanel page)
    {
        var toggle = MakeToggle();
        toggle.Checked = _settings.ShowTaskProgress;
        toggle.CheckedChanged += (_, _) => TaskProgressChanged?.Invoke(toggle.Checked);
        page.Controls.Add(TitleRow("Task progress", toggle));

        page.Controls.Add(BodyText(
            "Shows a small \"done/total\" count next to a session that's working through a task list " +
            "(the native checklist Claude Code builds as it plans). It turns green when every task is " +
            "complete; hover it in the overlay for the full list."));
    }

    // Permission-mode badges: a display toggle (default on) plus the colour legend, which dims when
    // the toggle is off. The toggle raises PermissionModeBadgesChanged so the overlay redraws.
    private void BuildModeBadgeSection(FlowLayoutPanel page)
    {
        var legend = new ModeLegend { Margin = new Padding(0, 2, 0, 8) };

        _modeBadgesToggle = MakeToggle();
        _modeBadgesToggle.Checked = _settings.ShowPermissionModeBadges;
        _modeBadgesToggle.CheckedChanged += (_, _) =>
        {
            PermissionModeBadgesChanged?.Invoke(_modeBadgesToggle.Checked);
            legend.Enabled = _modeBadgesToggle.Checked;
        };
        page.Controls.Add(TitleRow("Permission mode badges", _modeBadgesToggle));

        page.Controls.Add(BodyText(
            "When the Claude Code plugin is installed, each session's live permission mode is shown " +
            "as a coloured badge next to that session in the overlay:"));

        legend.Enabled = _settings.ShowPermissionModeBadges;
        _fluidWidth.Add((legend, 0));
        page.Controls.Add(legend);
    }

    // Context-pressure thermometer + threshold slider. The slider's painted bands double as the
    // explainer, so it sits directly under the description with no separate "Thresholds" copy.
    private void BuildContextPressureSection(FlowLayoutPanel page)
    {
        var slider = new ContextThresholdSlider
        {
            Margin           = new Padding(0, 4, 0, 8),
            ShowGreenSegment = _settings.ShowContextGreenSegment,
        };
        var greenRow = BuildGreenSegmentSubRow(out var greenToggle, out var greenLabel);

        void ApplyEnabled(bool on)
        {
            slider.Enabled       = on;
            greenToggle.Enabled  = on;
            greenLabel.ForeColor = on ? Theme.Fg : Theme.Muted;
        }

        var toggle = MakeToggle();
        toggle.Checked = _settings.ShowContextPressure;
        toggle.CheckedChanged += (_, _) =>
        {
            ContextPressureChanged?.Invoke(toggle.Checked);
            ApplyEnabled(toggle.Checked);
        };
        page.Controls.Add(TitleRow("Context pressure", toggle));

        page.Controls.Add(BodyText(
            "Warns when a session is filling up its context window. A small thermometer appears next " +
            "to the session in the overlay once it crosses the first threshold, filling up and warming " +
            "from yellow to orange to red as the window approaches full. Drag the handles to set where " +
            "it first appears and where it turns orange and red."));
        page.Controls.Add(BodyText(
            "The window size is read from the model the session is running — the 1M-token beta is " +
            "recognised as such — so the gauge reflects the real headroom, not a fixed limit."));

        slider.SetValues(
            _settings.ContextPressureYellowPercent,
            _settings.ContextPressureOrangePercent,
            _settings.ContextPressureRedPercent);
        slider.RangeChanged += (y, o, r) => ContextThresholdsChanged?.Invoke(y, o, r);
        _fluidWidth.Add((slider, 0));
        page.Controls.Add(slider);

        greenToggle.CheckedChanged += (_, _) =>
        {
            ContextGreenSegmentChanged?.Invoke(greenToggle.Checked);
            slider.ShowGreenSegment = greenToggle.Checked;
        };
        page.Controls.Add(greenRow);

        ApplyEnabled(_settings.ShowContextPressure);
    }

    // Indented sub-row for the green "first segment" indicator: label + right-aligned toggle. Dimmed
    // with the slider whenever the context-pressure master toggle is off.
    private Panel BuildGreenSegmentSubRow(out ToggleSwitch toggle, out Label label)
    {
        var row = new Panel { Height = 30, Margin = new Padding(0, 2, 0, 4) };

        label = new Label
        {
            Text      = "Show a green indicator below the first threshold instead of leaving it blank",
            AutoSize  = true,
            ForeColor = Theme.Fg,
            Location  = new Point(16, 7),
        };

        toggle = MakeToggle();
        toggle.Checked = _settings.ShowContextGreenSegment;

        row.Controls.Add(label);
        row.Controls.Add(toggle);
        RegisterRightAlignedRow(row, toggle);
        return row;
    }

    // Stuck-detection: a master toggle and the two heuristic sub-rows, with the prose trimmed to a
    // single line now that it shares the tab.
    private void BuildDetectionSection(FlowLayoutPanel page)
    {
        _stuckMasterToggle = MakeToggle();
        _stuckMasterToggle.Checked = _settings.StuckDetectionEnabled;
        _stuckMasterToggle.CheckedChanged += (_, _) =>
        {
            ApplyStuckEnabled();
            RaiseStuckChanged();
        };
        page.Controls.Add(TitleRow("Stuck detection", _stuckMasterToggle));

        page.Controls.Add(BodyText(
            "Flags a running session that looks stuck with an amber warning glyph in the overlay. " +
            "It's a heuristic — switch off whichever check is too eager, or the whole feature."));

        page.Controls.Add(BuildStuckSubRow(
            "Repeated failures — several tool calls fail in a row",
            _settings.DetectErrorStreaks, out _stuckErrorToggle, out _stuckErrorLabel));

        page.Controls.Add(BuildStuckSubRow(
            "Failing loops — the same action repeats and keeps failing",
            _settings.DetectFailingLoops, out _stuckLoopToggle, out _stuckLoopLabel));

        ApplyStuckEnabled();
    }

    // An indented detection sub-row: a label on the left, a right-aligned toggle. Any change raises
    // the combined StuckDetectionChanged so the owning context persists it and updates the monitor.
    private Panel BuildStuckSubRow(string text, bool initial, out ToggleSwitch toggle, out Label label)
    {
        var row = new Panel { Height = 30, Margin = new Padding(0, 2, 0, 4) };

        label = new Label
        {
            Text      = text,
            AutoSize  = true,
            ForeColor = Theme.Fg,
            Location  = new Point(16, 7),
        };

        toggle = MakeToggle();
        toggle.Checked = initial;
        toggle.CheckedChanged += (_, _) => RaiseStuckChanged();

        row.Controls.Add(label);
        row.Controls.Add(toggle);
        RegisterRightAlignedRow(row, toggle);
        return row;
    }

    // Dims both detection sub-rows whenever the master switch is off.
    private void ApplyStuckEnabled()
    {
        bool on = _stuckMasterToggle.Checked;
        _stuckErrorToggle.Enabled  = on;
        _stuckLoopToggle.Enabled   = on;
        _stuckErrorLabel.ForeColor = on ? Theme.Fg : Theme.Muted;
        _stuckLoopLabel.ForeColor  = on ? Theme.Fg : Theme.Muted;
    }

    private void RaiseStuckChanged() =>
        StuckDetectionChanged?.Invoke(
            _stuckMasterToggle.Checked, _stuckErrorToggle.Checked, _stuckLoopToggle.Checked);

    // ── Session Stats ────────────────────────────────────────────────────────────────
    private void BuildStatsPage(FlowLayoutPanel page)
    {
        page.Controls.Add(SectionTitle("Session stats"));
        page.Controls.Add(BodyText(
            "Daily activity derived from your Claude Code transcripts — a summary line in the tray menu " +
            "and a full breakdown in the Session stats window (right-click the tray icon → Session stats)."));

        var openRow = ButtonRow();
        var openBtn = MakeButton("Open session stats…");
        openBtn.Click += (_, _) => OpenStatsRequested?.Invoke();
        openRow.Controls.Add(openBtn);
        page.Controls.Add(openRow);

        var trayToggle = MakeToggle();
        trayToggle.Checked = _settings.ShowTodayStatsInTray;
        trayToggle.CheckedChanged += (_, _) =>
        {
            _settings.ShowTodayStatsInTray = trayToggle.Checked;
            _settings.Save();
        };
        page.Controls.Add(TitleRow("Show today's summary in the tray menu", trayToggle));

        var costToggle = MakeToggle();
        costToggle.Checked = _settings.ShowEstimatedCost;
        costToggle.CheckedChanged += (_, _) =>
        {
            _settings.ShowEstimatedCost = costToggle.Checked;
            _settings.Save();
        };
        page.Controls.Add(TitleRow("Show estimated cost", costToggle));
        page.Controls.Add(BodyText(
            "Shows an \"equivalent API cost\" in the stats window — what the tokens would have cost on " +
            "pay-as-you-go API pricing, using built-in per-model rates. It's a usage-intensity signal, " +
            "not a bill (subscription usage isn't billed per token)."));

        page.Controls.Add(Separator());

        page.Controls.Add(SectionTitle("Active-time idle threshold"));
        page.Controls.Add(BodyText(
            "\"Active\" time is estimated from the gaps between transcript records. A gap longer than this " +
            "counts as you having stepped away, and is capped at the threshold. Default 5 minutes."));
        page.Controls.Add(BuildIdleStepper());
    }

    // A "−  N min  +" stepper for the active-time idle threshold (clamped 1–30 minutes). Persists the
    // value and pushes it straight to the stats engine so it takes effect immediately.
    private Panel BuildIdleStepper()
    {
        const int min = 1, max = 30;
        var row = ButtonRow();
        var dec = MakeButton("−");
        var inc = MakeButton("+");
        var value = new Label
        {
            AutoSize  = false,
            Width     = 72,
            Height    = 30,
            TextAlign = ContentAlignment.MiddleCenter,
            ForeColor = Theme.Fg,
            Font      = new Font("Segoe UI", 10f, FontStyle.Regular, GraphicsUnit.Point),
            Margin    = new Padding(0, 0, 8, 0),
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

        row.Controls.Add(dec);
        row.Controls.Add(value);
        row.Controls.Add(inc);
        return row;
    }

    // ── Notifications (Windows desktop + external ntfy) ──────────────────────────────
    private void BuildNotificationsPage(FlowLayoutPanel page)
    {
        _notifyMasterToggle = MakeToggle();
        _notifyMasterToggle.Checked = _settings.NotificationsEnabled;
        _notifyMasterToggle.CheckedChanged += (_, _) =>
        {
            _settings.NotificationsEnabled = _notifyMasterToggle.Checked;
            _settings.Save();
            ApplyNotifyEnabled();
        };
        page.Controls.Add(TitleRow("Notifications", _notifyMasterToggle));

        page.Controls.Add(BodyText(
            "Windows desktop notifications when a session needs you. Each type has a pop-up and an " +
            "optional chime (the built-in Windows sound, off by default). Turn the whole feature off, " +
            "or just the parts you don't want. Use Test to preview one."));

        page.Controls.Add(BuildNotifyRow(
            "Done — a session finished working",
            _settings.NotifyOnDone,
            v => { _settings.NotifyOnDone = v; _settings.Save(); },
            _settings.ChimeOnDone,
            v => { _settings.ChimeOnDone = v; _settings.Save(); },
            NotificationKind.Done));

        page.Controls.Add(BuildNotifyRow(
            "Waiting for input — a session is blocked on a prompt",
            _settings.NotifyOnWaitingInput,
            v => { _settings.NotifyOnWaitingInput = v; _settings.Save(); },
            _settings.ChimeOnWaitingInput,
            v => { _settings.ChimeOnWaitingInput = v; _settings.Save(); },
            NotificationKind.WaitingForInput));

        ApplyNotifyEnabled();

        page.Controls.Add(Separator());

        BuildExternalSection(page);
    }

    // An indented sub-row for one notification type: a label, a "Test" button, and two captioned
    // toggles on the right — "Pop-up" (the desktop balloon) and "Chime" (the Windows sound). The
    // controls are tracked so ApplyNotifyEnabled can dim the whole row when the master switch is off.
    private Panel BuildNotifyRow(
        string text,
        bool popupInitial, Action<bool> onPopupChanged,
        bool chimeInitial, Action<bool> onChimeChanged,
        NotificationKind kind)
    {
        var row = new Panel
        {
            Height = 30,
            Margin = new Padding(0, 2, 0, 4),
        };

        var label = new Label
        {
            Text      = text,
            AutoSize  = true,
            ForeColor = Theme.Fg,
            Location  = new Point(16, 7),
        };

        var popup = MakeToggle();
        popup.Checked  = popupInitial;
        popup.CheckedChanged += (_, _) => onPopupChanged(popup.Checked);
        var popupCap = ToggleCaption("Pop-up");

        var chime = MakeToggle();
        chime.Checked  = chimeInitial;
        chime.CheckedChanged += (_, _) => onChimeChanged(chime.Checked);
        var chimeCap = ToggleCaption("Chime");

        // Let the button auto-size to its text + padding so its height tracks the font at any DPI.
        // A fixed height clips the label's descenders on >100%-scaled monitors (PerMonitorV2). Both
        // rows carry the same "Test" text, so they stay the same width regardless.
        var test = MakeButton("Test");
        test.Margin    = new Padding(0);
        test.TextAlign = ContentAlignment.MiddleCenter;
        test.Click   += (_, _) => TestNotificationRequested?.Invoke(kind);

        row.Controls.Add(label);
        row.Controls.Add(test);
        row.Controls.Add(chimeCap);
        row.Controls.Add(chime);
        row.Controls.Add(popupCap);
        row.Controls.Add(popup);

        // Right-align the captioned toggles and Test button to the row's current width. Laid out
        // from the right edge inward: [Test] … [Chime cap][chime] [Pop-up cap][popup].
        void Position()
        {
            int Mid(Control c) => (row.Height - c.Height) / 2;
            int x = row.Width;
            x -= popup.Width;    popup.Location    = new Point(x, Mid(popup));
            x -= 4 + popupCap.Width; popupCap.Location = new Point(x, Mid(popupCap));
            x -= 14 + chime.Width;   chime.Location    = new Point(x, Mid(chime));
            x -= 4 + chimeCap.Width; chimeCap.Location = new Point(x, Mid(chimeCap));
            x -= 14 + test.Width;    test.Location     = new Point(x, Mid(test));
        }
        row.Resize += (_, _) => Position();
        _fluidWidth.Add((row, 0));
        Position();

        _notifySubRows.Add(new NotifyRow(label, popup, chime, test, popupCap, chimeCap));
        return row;
    }

    // A muted, vertically-centred caption sitting to the left of an inline toggle.
    private static Label ToggleCaption(string text) => new()
    {
        Text      = text,
        AutoSize  = true,
        ForeColor = Theme.Muted,
        Font      = new Font("Segoe UI", 8.5f, FontStyle.Regular, GraphicsUnit.Point),
    };

    // Dims every per-type sub-row control (labels, both toggles, Test) when the master switch is off.
    private void ApplyNotifyEnabled()
    {
        bool on = _notifyMasterToggle.Checked;
        var capColor = on ? Theme.Muted : Theme.Border;
        foreach (var r in _notifySubRows)
        {
            r.Popup.Enabled   = on;
            r.Chime.Enabled   = on;
            r.Test.Enabled    = on;
            r.Label.ForeColor    = on ? Theme.Fg : Theme.Muted;
            r.PopupCap.ForeColor = capColor;
            r.ChimeCap.ForeColor = capColor;
        }
    }

    // External notifications via ntfy. The toggle only gates whether pushes are sent (and whether
    // the per-session opt-in is offered in the overlay); the host/topic boxes stay enabled either
    // way so they can be filled in and tested before turning the feature on.
    private void BuildExternalSection(FlowLayoutPanel page)
    {
        _externalToggle = MakeToggle();
        _externalToggle.Checked = _settings.ExternalNotificationsEnabled;
        _externalToggle.CheckedChanged += (_, _) =>
        {
            _settings.ExternalNotificationsEnabled = _externalToggle.Checked;
            _settings.Save();
            ApplyExternalEnabled();
            ExternalNotificationsEnabledChanged?.Invoke(_externalToggle.Checked);
        };
        page.Controls.Add(TitleRow("External notifications", _externalToggle));

        page.Controls.Add(BodyText(
            "Also push \"Done\" and \"Waiting for input\" alerts to your phone or other devices via " +
            "ntfy. Enter your server and topic below, then enable it per session by right-clicking " +
            "that session in the overlay."));

        // Default the host to the public server, but only in-memory until the box is edited — opening
        // settings shouldn't silently rewrite settings.json.
        string host = string.IsNullOrWhiteSpace(_settings.NtfyHost) ? "https://ntfy.sh" : _settings.NtfyHost!;
        _settings.NtfyHost = host;

        page.Controls.Add(FieldCaption("Server URL"));
        _ntfyHostBox = MakeTextBox(host);
        _fluidWidth.Add((_ntfyHostBox, 0));
        _ntfyHostBox.TextChanged += (_, _) => _settings.NtfyHost = _ntfyHostBox.Text;
        _ntfyHostBox.Leave       += (_, _) => _settings.Save();
        page.Controls.Add(_ntfyHostBox);

        page.Controls.Add(FieldCaption("Topic"));

        // Topic box with two helpers beside it: "Generate" mints a hard-to-guess 64-char topic, and
        // "QR code" shows an ntfy:// subscribe link for scanning on a phone.
        var topicRow = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents  = false,
            AutoSize      = true,
            AutoSizeMode  = AutoSizeMode.GrowAndShrink,
            Margin        = new Padding(0, 0, 0, 8),
        };

        _ntfyTopicBox = MakeTextBox(_settings.NtfyTopic ?? "");
        _ntfyTopicBox.Margin = new Padding(0, 0, 8, 0);
        _ntfyTopicBox.TextChanged += (_, _) => _settings.NtfyTopic = _ntfyTopicBox.Text;
        _ntfyTopicBox.Leave       += (_, _) => _settings.Save();

        // Auto-size the helper buttons so their height tracks the font at any DPI — a fixed height
        // clips the text on >100%-scaled monitors (PerMonitorV2), same as the per-type Test buttons.
        var genBtn = MakeButton("Generate");
        genBtn.Margin    = new Padding(0, 0, 8, 0);
        genBtn.TextAlign = ContentAlignment.MiddleCenter;
        genBtn.Click += (_, _) =>
        {
            _ntfyTopicBox.Text = GenerateTopic();   // raises TextChanged -> updates _settings.NtfyTopic
            _settings.Save();
        };

        var qrBtn = MakeButton("QR code");
        qrBtn.Margin    = new Padding(0);
        qrBtn.TextAlign = ContentAlignment.MiddleCenter;
        qrBtn.Click += (_, _) => ShowTopicQr();

        // The topic box fills the row, less whatever the two auto-sized buttons currently occupy
        // (their widths scale with DPI, so the reserved space is computed live, not hard-coded).
        _fluidWidthDynamic.Add((_ntfyTopicBox, () =>
            _ntfyTopicBox.Margin.Right
            + genBtn.Width + genBtn.Margin.Horizontal
            + qrBtn.Width  + qrBtn.Margin.Horizontal));

        topicRow.Controls.Add(_ntfyTopicBox);
        topicRow.Controls.Add(genBtn);
        topicRow.Controls.Add(qrBtn);
        page.Controls.Add(topicRow);

        page.Controls.Add(BuildLockNotifyRow());
        page.Controls.Add(BuildRemoteLinkRow());

        var row = ButtonRow();
        row.Margin = new Padding(0, 4, 0, 4);
        var testBtn = MakeButton("Send test notification");
        testBtn.Click += (_, _) => { _settings.Save(); TestExternalNotificationRequested?.Invoke(); };
        row.Controls.Add(testBtn);
        page.Controls.Add(row);

        ApplyExternalEnabled();
    }

    // An indented sub-row for the AFK override: while the screen is locked, push every session's
    // alert without needing the per-session right-click opt-in. Dimmed while the external master
    // toggle is off, since no push is sent then anyway.
    private Panel BuildLockNotifyRow()
    {
        var row = new Panel
        {
            Height = 30,
            Margin = new Padding(0, 2, 0, 4),
        };

        _lockNotifyLabel = new Label
        {
            Text      = "Notify any session while my screen is locked",
            AutoSize  = true,
            ForeColor = Theme.Fg,
            Location  = new Point(16, 7),
        };

        _lockNotifyToggle = MakeToggle();
        _lockNotifyToggle.Checked = _settings.NotifyWhenLocked;
        _lockNotifyToggle.CheckedChanged += (_, _) =>
        {
            _settings.NotifyWhenLocked = _lockNotifyToggle.Checked;
            _settings.Save();
        };

        row.Controls.Add(_lockNotifyLabel);
        row.Controls.Add(_lockNotifyToggle);

        RegisterRightAlignedRow(row, _lockNotifyToggle);
        return row;
    }

    // An indented sub-row that opts remote-controlled sessions into carrying a claude.ai "Open
    // session" deep link in their push. Dimmed (like the per-type notify rows) while the external
    // master toggle is off, since no push is sent then anyway.
    private Panel BuildRemoteLinkRow()
    {
        var row = new Panel
        {
            Height = 30,
            Margin = new Padding(0, 2, 0, 4),
        };

        _remoteLinkLabel = new Label
        {
            Text      = "Include a claude.ai link for remote-controlled sessions",
            AutoSize  = true,
            ForeColor = Theme.Fg,
            Location  = new Point(16, 7),
        };

        _remoteLinkToggle = MakeToggle();
        _remoteLinkToggle.Checked = _settings.ExternalNotificationsIncludeRemoteLink;
        _remoteLinkToggle.CheckedChanged += (_, _) =>
        {
            _settings.ExternalNotificationsIncludeRemoteLink = _remoteLinkToggle.Checked;
            _settings.Save();
        };

        row.Controls.Add(_remoteLinkLabel);
        row.Controls.Add(_remoteLinkToggle);

        RegisterRightAlignedRow(row, _remoteLinkToggle);
        return row;
    }

    // Dims the remote-link sub-row whenever the external master switch is off.
    private void ApplyExternalEnabled()
    {
        bool on = _externalToggle.Checked;
        _remoteLinkToggle.Enabled  = on;
        _remoteLinkLabel.ForeColor = on ? Theme.Fg : Theme.Muted;
        _lockNotifyToggle.Enabled  = on;
        _lockNotifyLabel.ForeColor = on ? Theme.Fg : Theme.Muted;
    }

    // Mints a hard-to-guess topic of the form "perch-{random}", padded with random
    // alphanumerics to a total length of 64 — long enough that the topic doubles as the secret.
    private static string GenerateTopic()
    {
        const string prefix = "perch-";
        const string chars  = "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789";
        var buf = new char[64];
        prefix.CopyTo(0, buf, 0, prefix.Length);
        for (int i = prefix.Length; i < buf.Length; i++)
            buf[i] = chars[Random.Shared.Next(chars.Length)];
        return new string(buf);
    }

    // Shows a QR card encoding ntfy://<host>/<topic> (host with any scheme stripped), so the topic
    // can be subscribed to by scanning it in the ntfy phone app. Only one card is shown at a time.
    private void ShowTopicQr()
    {
        var topic = _ntfyTopicBox.Text.Trim();
        if (topic.Length == 0) return;

        var host = _ntfyHostBox.Text.Trim();
        int scheme = host.IndexOf("://", StringComparison.Ordinal);
        if (scheme >= 0) host = host[(scheme + 3)..];
        host = host.Trim('/');

        var url = $"ntfy://{host}/{topic}";

        _topicQrForm?.Close();
        _topicQrForm = new QrCodeForm("ntfy subscription", url);
        _topicQrForm.FormClosed += (_, _) => _topicQrForm = null;
        _topicQrForm.CenterOn(Screen.FromControl(this));
        _topicQrForm.Show();
        _topicQrForm.Activate();
    }

    // ── Quick links ───────────────────────────────────────────────────────────────
    private void BuildQuickLinksPage(FlowLayoutPanel page)
    {
        page.Controls.Add(BodyText(
            "Quick links are a row of icons below the usage bars in the overlay. Click an icon to " +
            "open that app, or bring it to the front if it's already running. Add a shortcut to any " +
            "program on your PC; use the toggle to show or hide one without removing it."));

        page.Controls.Add(Separator());

        // Take an editable copy of the saved links so edits aren't committed until raised back out.
        _quickLinks.Clear();
        foreach (var l in _settings.QuickLinks ?? Enumerable.Empty<QuickLink>())
            _quickLinks.Add(l.Clone());

        // The rows live in a manually height-managed container: it gets its width from the fluid pass
        // and we set its height to fit the rows we draw into it.
        _quickLinksList = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.TopDown,
            WrapContents  = false,
            AutoSize      = false,
            Margin        = new Padding(0, 0, 0, 8),
            Padding       = new Padding(0),
        };
        _fluidWidth.Add((_quickLinksList, 0));
        _quickLinksList.Resize += (_, _) => SizeQuickLinkRows();
        page.Controls.Add(_quickLinksList);

        var addRow = ButtonRow();
        var addBtn = MakeButton("Add quick link…");
        addBtn.Click += (_, _) => AddOrEditQuickLink(null);
        addRow.Controls.Add(addBtn);
        page.Controls.Add(addRow);

        // One-click adds for the well-known apps Perch ships icons for, shown only while they
        // aren't already in the list.
        _quickLinkPresets = ButtonRow();
        page.Controls.Add(_quickLinkPresets);

        page.Controls.Add(Separator());

        var upsideDownToggle = MakeToggle();
        upsideDownToggle.Checked = _settings.UpsideDownQuickLinks;
        upsideDownToggle.CheckedChanged += (_, _) =>
            UpsideDownQuickLinksChanged?.Invoke(upsideDownToggle.Checked);
        page.Controls.Add(TitleRow("Upside-down icons", upsideDownToggle));
        page.Controls.Add(BodyText("For when the world feels right way up and you'd rather it didn't."));

        RebuildQuickLinksList();
    }

    // Rebuilds every quick-link row from the working list (and refreshes the preset buttons). Called
    // after any structural change — add, remove, or edit.
    private void RebuildQuickLinksList()
    {
        foreach (Control c in _quickLinksList.Controls) c.Dispose();
        _quickLinksList.Controls.Clear();

        if (_quickLinks.Count == 0)
        {
            _quickLinksList.Controls.Add(new Label
            {
                Text      = "No quick links yet — add one below.",
                AutoSize  = true,
                ForeColor = Theme.Muted,
                Margin    = new Padding(0, 4, 0, 4),
            });
        }
        else
        {
            foreach (var link in _quickLinks)
                _quickLinksList.Controls.Add(BuildQuickLinkRow(link));
        }

        SizeQuickLinkRows();
        RebuildPresetButtons();
    }

    // A single editable quick-link row: an enable toggle, the name and its target path, and Edit /
    // Remove buttons. The link object is captured directly so the row keeps working across reorders.
    private Panel BuildQuickLinkRow(QuickLink link)
    {
        var row = new Panel { Height = 50, Margin = new Padding(0, 0, 0, 6), BackColor = Theme.FormBg };

        var toggle = MakeToggle();
        toggle.Checked = link.Enabled;
        toggle.CheckedChanged += (_, _) => { link.Enabled = toggle.Checked; RaiseQuickLinksChanged(); };

        var name = new Label
        {
            Text      = string.IsNullOrWhiteSpace(link.Name) ? "(unnamed)" : link.Name,
            AutoSize  = true,
            ForeColor = Theme.Title,
            Font      = new Font("Segoe UI", 10f, FontStyle.Bold, GraphicsUnit.Point),
        };
        var path = new Label
        {
            Text         = QuickLinkSubtitle(link),
            AutoSize     = false,
            AutoEllipsis = true,
            Height       = 18,
            ForeColor    = Theme.Muted,
            Font         = new Font("Segoe UI", 8.5f, FontStyle.Regular, GraphicsUnit.Point),
        };

        var edit = MakeButton("Edit");
        edit.Click += (_, _) => AddOrEditQuickLink(link);
        var remove = MakeButton("Remove");
        remove.ForeColor = Theme.Danger;
        remove.Click += (_, _) => { _quickLinks.Remove(link); RebuildQuickLinksList(); RaiseQuickLinksChanged(); };

        row.Controls.Add(toggle);
        row.Controls.Add(name);
        row.Controls.Add(path);
        row.Controls.Add(edit);
        row.Controls.Add(remove);

        void Position()
        {
            int midY = row.Height / 2;
            toggle.Location = new Point(0, midY - toggle.Height / 2);

            // Buttons right-aligned: Remove flush to the edge, Edit just to its left.
            remove.Location = new Point(Math.Max(0, row.Width - remove.Width), midY - remove.Height / 2);
            edit.Location   = new Point(Math.Max(0, remove.Left - edit.Width - 6), midY - edit.Height / 2);

            int textLeft  = toggle.Right + 12;
            int textRight = edit.Left - 8;
            name.Location = new Point(textLeft, 6);
            path.Location = new Point(textLeft, 27);
            path.Width    = Math.Max(20, textRight - textLeft);
        }
        row.Resize += (_, _) => Position();
        Position();
        return row;
    }

    // The dim subtitle under a link's name: its explicit path, an auto-detected path for a preset
    // with no path set, or a hint when nothing resolves.
    private static string QuickLinkSubtitle(QuickLink link)
    {
        // An explicit path is always honoured and shown — used as-is even if it yields a placeholder
        // icon. A genuinely missing path is still flagged, since that's usually a typo.
        if (!string.IsNullOrWhiteSpace(link.ExePath))
            return File.Exists(link.ExePath) ? link.ExePath : link.ExePath + "   ⚠ file not found";

        // No path: a case-insensitive Start Menu match means the link resolves by name, so leave the
        // subtitle blank rather than showing a misleading "not found".
        if (ShellIcon.StartMenuAppExists(link.Name)) return "";

        // A well-known app discovered on disk (installed but not surfaced in the Start Menu).
        var resolved = link.ResolveExe();
        if (resolved != null) return resolved + "  (auto-detected)";

        return "Not found — install the app, or Edit to set its path";
    }

    // Sets each row to the container's width and the container's height to fit the rows.
    private void SizeQuickLinkRows()
    {
        if (_quickLinksList is null) return;
        int w = _quickLinksList.ClientSize.Width;
        int totalH = 0;
        foreach (Control row in _quickLinksList.Controls)
        {
            if (row is Panel) row.Width = Math.Max(40, w);
            totalH += row.Height + row.Margin.Vertical;
        }
        _quickLinksList.Height = Math.Max(1, totalH);
    }

    // Shows a "+ App" button for each shipped preset not already in the list.
    private void RebuildPresetButtons()
    {
        foreach (Control c in _quickLinkPresets.Controls) c.Dispose();
        _quickLinkPresets.Controls.Clear();

        foreach (var preset in KnownApps.PresetNames)
        {
            if (_quickLinks.Any(l => string.Equals(l.Name, preset, StringComparison.OrdinalIgnoreCase)))
                continue;

            var btn = MakeButton("+ " + preset);
            btn.Click += (_, _) =>
            {
                // Name-only: the icon and launch resolve through the Start Menu, so it shows the real
                // logo and stays correct across updates without pinning a path.
                _quickLinks.Add(new QuickLink { Name = preset, Enabled = true });
                RebuildQuickLinksList();
                RaiseQuickLinksChanged();
            };
            _quickLinkPresets.Controls.Add(btn);
        }
        _quickLinkPresets.Visible = _quickLinkPresets.Controls.Count > 0;
    }

    // Opens the add/edit dialog; on OK, applies the result to a new or existing link and republishes.
    private void AddOrEditQuickLink(QuickLink? existing)
    {
        using var dlg = new QuickLinkDialog(existing);
        if (dlg.ShowDialog(this) != DialogResult.OK) return;

        if (existing == null)
            _quickLinks.Add(new QuickLink { Name = dlg.LinkName, ExePath = dlg.LinkPath, Enabled = true });
        else
        {
            existing.Name    = dlg.LinkName;
            existing.ExePath = dlg.LinkPath;
        }
        RebuildQuickLinksList();
        RaiseQuickLinksChanged();
    }

    private void RaiseQuickLinksChanged() =>
        QuickLinksChanged?.Invoke(_quickLinks.Select(l => l.Clone()).ToList());

    // ── Experimental ────────────────────────────────────────────────────────────────
    // Opt-in switches for in-development features. A single toggle enables Claude Code's experimental
    // Agent Teams (an env var written into ~/.claude/settings.json that CC reads on launch); Perch then
    // surfaces any teammates it finds automatically.
    private void BuildExperimentalPage(FlowLayoutPanel page)
    {
        page.Controls.Add(SectionTitle("Experimental"));
        page.Controls.Add(BodyText(
            "Opt-in switches for features still in development. They may change or break between updates."));

        page.Controls.Add(Separator());

        // 1. Claude Code's experimental Agent Teams feature. The toggle reads/writes the env var
        //    straight in ~/.claude/settings.json; it isn't part of Perch's own settings.
        var teamsEnvToggle = MakeToggle();
        teamsEnvToggle.Checked = ClaudeUserSettings.IsAgentTeamsEnabled();
        teamsEnvToggle.CheckedChanged += (_, _) =>
            ClaudeUserSettings.SetAgentTeamsEnabled(teamsEnvToggle.Checked);
        page.Controls.Add(TitleRow("Enable Agent Teams in Claude Code", teamsEnvToggle));
        page.Controls.Add(BodyText(
            "Sets the CLAUDE_CODE_EXPERIMENTAL_AGENT_TEAMS environment variable in your user settings " +
            "(~/.claude/settings.json). Claude Code reads it on launch, so restart any open sessions " +
            "for it to take effect."));
        page.Controls.Add(BodyText(
            "Once enabled, Perch surfaces teammates automatically as distinct, named rows in the overlay — " +
            "kept on the roster while they're alive, even when idle between messages from the lead."));

        page.Controls.Add(Separator());

        // 2. Trim the roster down to teammates that are actually working. Display-only; raises the
        //    event so the overlay re-filters live. Off by default (show the full roster).
        var hideInactiveToggle = MakeToggle();
        hideInactiveToggle.Checked = _settings.HideInactiveTeamMembers;
        hideInactiveToggle.CheckedChanged += (_, _) =>
            HideInactiveTeamMembersChanged?.Invoke(hideInactiveToggle.Checked);
        page.Controls.Add(TitleRow("Hide inactive members", hideInactiveToggle));
        page.Controls.Add(BodyText(
            "Drops idle teammates — those waiting for the lead — from the overlay, so only teammates " +
            "actively working are shown. A hidden teammate reappears the moment it starts working again."));
    }

    // ── About ─────────────────────────────────────────────────────────────────────
    private void BuildAboutPage(FlowLayoutPanel page)
    {
        page.Controls.Add(SectionTitle("About"));

        var header = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents  = false,
            AutoSize      = true,
            AutoSizeMode  = AutoSizeMode.GrowAndShrink,
            Margin        = new Padding(0, 0, 0, 6),
        };
        if (_icon != null)
            header.Controls.Add(new PictureBox
            {
                Image    = _icon,
                SizeMode = PictureBoxSizeMode.Zoom,
                Size     = new Size(32, 32),
                Margin   = new Padding(0, 0, 10, 0),
            });
        header.Controls.Add(new Label
        {
            Text      = $"Perch\nv{AppInfo.Version}",
            AutoSize  = true,
            ForeColor = Theme.Fg,
            Margin    = new Padding(0, 2, 0, 0),
        });
        page.Controls.Add(header);

        page.Controls.Add(LinkRow("GitHub repository", AppInfo.RepoUrl));
        page.Controls.Add(LinkRow("Report an issue on GitHub", AppInfo.IssuesUrl));

        page.Controls.Add(Separator());

        page.Controls.Add(SectionTitle("Updates"));
        page.Controls.Add(BodyText($"Currently running v{AppInfo.Version}."));

        var row = ButtonRow();  // the page's trailing spacer (see AddPage) handles the bottom gap
        var checkBtn = MakeButton("Check for Updates");
        checkBtn.Click += (_, _) => CheckForUpdatesRequested?.Invoke(this, EventArgs.Empty);
        row.Controls.Add(checkBtn);
        page.Controls.Add(row);
    }

    // ── Changelog ────────────────────────────────────────────────────────────────
    // Renders the embedded CHANGELOG.md into the page using the same factory controls as every other
    // settings page. Handles the subset of markdown that actually appears in that file: H1/H2/H3
    // headings, bullet lists, blockquotes, thematic breaks, and inline emphasis/links.
    private void BuildChangelogPage(FlowLayoutPanel page)
    {
        string? markdown = EmbeddedResources.LoadText("Perch.CHANGELOG.md");
        if (markdown is null)
        {
            page.Controls.Add(BodyText("Changelog not available."));
            return;
        }

        foreach (var rawLine in markdown.Split('\n'))
        {
            var line = rawLine.TrimEnd('\r');

            if (line.StartsWith("## "))
                page.Controls.Add(SectionTitle(StripInlineMarkdown(line[3..])));
            else if (line.StartsWith("### "))
                page.Controls.Add(ChangelogSubHeading(StripInlineMarkdown(line[4..])));
            else if (line.StartsWith("# "))
                { /* top-level title — the nav label already says "Changelog" */ }
            else if (line.StartsWith("- ") || line.StartsWith("* "))
                page.Controls.Add(BulletText(StripInlineMarkdown(line[2..])));
            else if (line == "---")
                page.Controls.Add(Separator());
            else if (line.StartsWith("> "))
                page.Controls.Add(BlockQuote(StripInlineMarkdown(line[2..])));
            else if (line.Trim().Length > 0)
                page.Controls.Add(BodyText(StripInlineMarkdown(line)));
        }
    }

    // Strips the inline markdown patterns that appear in CHANGELOG.md: bold, italic, inline code,
    // and [text](url) links. Bare [brackets] (version tags) are intentionally left alone.
    private static string StripInlineMarkdown(string text)
    {
        text = Regex.Replace(text, @"\*\*(.*?)\*\*", "$1");            // **bold**
        text = Regex.Replace(text, @"__(.*?)__",     "$1");            // __bold__
        text = Regex.Replace(text, @"\*(.*?)\*",     "$1");            // *italic*
        text = Regex.Replace(text, @"_(.*?)_",       "$1");            // _italic_
        text = Regex.Replace(text, @"`([^`]+)`",     "$1");            // `inline code`
        text = Regex.Replace(text, @"\[([^\]]+)\]\([^)]+\)", "$1");   // [text](url)
        return text;
    }

    // ── Public updates from the owner ────────────────────────────────────────────
    /// <summary>Pushes a fresh usage reading in (e.g. after the context's periodic poll).</summary>
    public void UpdateUsage(UsageInfo usage)
    {
        _usage = usage;
        if (!IsDisposed)
            _usageBars.SetUsage(usage);
    }

    // ── Control factories ───────────────────────────────────────────────────────────
    private static Label SectionTitle(string text) => new()
    {
        Text      = text,
        AutoSize  = true,
        ForeColor = Theme.Title,
        Font      = new Font("Segoe UI", 11f, FontStyle.Bold, GraphicsUnit.Point),
        Margin    = new Padding(0, 4, 0, 8),
    };

    // A section header with a right-justified toggle on the same row. The toggle is re-positioned
    // to the row's right edge whenever the row width changes.
    private Panel TitleRow(string title, ToggleSwitch toggle)
    {
        var row = new Panel
        {
            Height = 30,
            Margin = new Padding(0, 4, 0, 8),
        };
        var label = new Label
        {
            Text      = title,
            AutoSize  = true,
            ForeColor = Theme.Title,
            Font      = new Font("Segoe UI", 11f, FontStyle.Bold, GraphicsUnit.Point),
            Location  = new Point(0, 2),
        };
        row.Controls.Add(label);
        row.Controls.Add(toggle);

        RegisterRightAlignedRow(row, toggle);
        return row;
    }

    private static ToggleSwitch MakeToggle() => new() { Margin = new Padding(0) };

    // A small muted caption sitting just above a text field.
    private static Label FieldCaption(string text) => new()
    {
        Text      = text,
        AutoSize  = true,
        ForeColor = Theme.Muted,
        Margin    = new Padding(0, 2, 0, 2),
    };

    // A dark-themed single-line text box matching the rest of the settings surface. Width is managed
    // by the fluid-layout pass; callers register it in _fluidWidth.
    private TextBox MakeTextBox(string value) => new()
    {
        Text        = value,
        Width       = 480,
        BackColor   = Theme.ButtonBg,
        ForeColor   = Theme.Fg,
        BorderStyle = BorderStyle.FixedSingle,
        Font        = new Font("Segoe UI", 9.5f, FontStyle.Regular, GraphicsUnit.Point),
        Margin      = new Padding(0, 0, 0, 8),
    };

    // A wrapping body paragraph; registered so its wrap width tracks the content area.
    private Label BodyText(string text)
    {
        var l = new Label
        {
            Text        = text,
            AutoSize    = true,
            MaximumSize = new Size(480, 0),  // updated by ApplyFluidWidth
            ForeColor   = Theme.Muted,
            Margin      = new Padding(0, 0, 0, 6),
        };
        _fluidWrap.Add(l);
        return l;
    }

    // A monospace, boxed block for copy-pasteable commands.
    private Label CodeBlock(string text)
    {
        var l = new Label
        {
            Text        = text,
            AutoSize    = true,
            MaximumSize = new Size(480, 0),
            Font        = new Font("Consolas", 9.5f, FontStyle.Regular, GraphicsUnit.Point),
            ForeColor   = Theme.Fg,
            BackColor   = Color.FromArgb(34, 34, 44),
            Padding     = new Padding(10, 8, 10, 8),
            Margin      = new Padding(0, 0, 0, 8),
        };
        _fluidWrap.Add(l);
        return l;
    }

    private LinkLabel LinkRow(string text, string url)
    {
        var link = new LinkLabel
        {
            Text             = text,
            AutoSize         = true,
            LinkColor        = Theme.Accent,
            ActiveLinkColor  = Theme.AccentHover,
            VisitedLinkColor = Theme.Accent,
            LinkBehavior     = LinkBehavior.HoverUnderline,
            BackColor        = Theme.FormBg,
            Margin           = new Padding(0, 0, 0, 4),
        };
        link.LinkClicked += (_, _) => OpenUrl(url);
        return link;
    }

    private Panel Separator()
    {
        var p = new Panel
        {
            Height    = 1,
            Width     = 480,
            BackColor = Theme.Border,
            Margin    = new Padding(0, 12, 0, 12),
        };
        _fluidWidth.Add((p, 0));
        return p;
    }

    private static FlowLayoutPanel ButtonRow() => new()
    {
        FlowDirection = FlowDirection.LeftToRight,
        WrapContents  = false,
        AutoSize      = true,
        AutoSizeMode  = AutoSizeMode.GrowAndShrink,
        Margin        = new Padding(0, 0, 0, 4),
    };

    // The settings window's buttons: a shared flat dark button that auto-sizes to its text with a
    // little padding and a right margin between buttons in a row.
    private static Button MakeButton(string text)
    {
        var b = ThemedControls.FlatButton(text);
        b.AutoSize = true;
        b.Padding  = new Padding(8, 4, 8, 4);
        b.Margin   = new Padding(0, 0, 8, 0);
        return b;
    }

    // A bullet-prefixed body paragraph for changelog list items.
    private Label BulletText(string text)
    {
        var l = new Label
        {
            Text        = "•  " + text,
            AutoSize    = true,
            MaximumSize = new Size(480, 0),
            ForeColor   = Theme.Muted,
            Margin      = new Padding(0, 0, 0, 4),
        };
        _fluidWrap.Add(l);
        return l;
    }

    // A smaller bold heading for H3 changelog sub-sections.
    private static Label ChangelogSubHeading(string text) => new()
    {
        Text      = text,
        AutoSize  = true,
        ForeColor = Theme.Fg,
        Font      = new Font("Segoe UI", 9.5f, FontStyle.Bold, GraphicsUnit.Point),
        Margin    = new Padding(0, 6, 0, 4),
    };

    // An indented italic label for blockquote text (used for the editorial asides in the changelog).
    private Label BlockQuote(string text)
    {
        var l = new Label
        {
            Text        = text,
            AutoSize    = true,
            MaximumSize = new Size(480, 0),
            ForeColor   = Theme.Muted,
            Font        = new Font("Segoe UI", 9f, FontStyle.Italic, GraphicsUnit.Point),
            Margin      = new Padding(12, 0, 0, 6),
        };
        _fluidWrap.Add(l);
        return l;
    }

    private static void OpenUrl(string url)
    {
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
        catch { }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _icon?.Dispose();
            _topicQrForm?.Close();
        }
        base.Dispose(disposing);
    }
}
