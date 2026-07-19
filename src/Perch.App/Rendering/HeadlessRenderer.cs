using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Headless;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Perch.Avalonia.Views;
using Perch.Data;

namespace Perch.Avalonia.Rendering;

/// <summary>
/// Renders Perch's Avalonia views to PNG on a headless Skia platform, so the UI can be eyeballed
/// without a display (and diffed across changes). Uses synthetic data — never touches the real
/// <c>~/.claude</c> — so it's deterministic and safe to run anywhere. The standing verification harness
/// for the UI-port phases. <see cref="OverlayCanvas"/> is an owner-drawn <see cref="Control"/>, so it
/// renders straight through <see cref="RenderTargetBitmap"/> at any DPI (no window/templating needed).
/// </summary>
internal static class HeadlessRenderer
{
    public static int RenderAll(string outDir)
    {
        Directory.CreateDirectory(outDir);

        AppBuilder.Configure<App>()
            .UseSkia()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
            .WithInterFont()
            .SetupWithoutStarting();

        var canvas = new OverlayCanvas();
        canvas.Update(SampleSessions());
        canvas.UpdateUsage(SampleUsage());
        canvas.UpdateSystemMetrics(new SystemMetrics(CpuPercent: 37.5, UsedRamBytes: 12_000_000_000, TotalRamBytes: 32_000_000_000));
        canvas.UpdateSessionMetrics(new Dictionary<string, SessionMetrics>
        {
            ["1234"] = new(CpuPercent: 24.0, RamBytes: 1_800_000_000, ProcessCount: 5),
            ["7788"] = new(CpuPercent: 8.0,  RamBytes: 600_000_000,   ProcessCount: 2),
        });

        // Quick links: one real icon (the bundled brand PNG, materialised to a temp file the way the
        // seam would) plus two icon-less links so both the image and initials-fallback paths render.
        var (links, icons) = SampleQuickLinks(outDir);
        canvas.SetQuickLinks(links, icons);

        RenderControl(canvas, Path.Combine(outDir, "overlay_1x.png"), 96);
        RenderControl(canvas, Path.Combine(outDir, "overlay_1.5x.png"), 144);

        // Attention flash: the sample already carries a NeedsAttention session, so trigger the chase
        // border and render one frame of it (the animation timer doesn't tick in headless, so this
        // captures the comet at phase 0 over its faint inward-glow base outline).
        canvas.TriggerAttention();
        RenderControl(canvas, Path.Combine(outDir, "overlay_attention_1x.png"), 96);

        // Update badge: the perch-orange download disc in the header cluster, shown while an update is
        // pending. Rendered at both DPIs so the owner-drawn disc + arrow stay crisp.
        canvas.SetUpdateAvailable(true);
        RenderControl(canvas, Path.Combine(outDir, "overlay_update_1x.png"), 96);
        RenderControl(canvas, Path.Combine(outDir, "overlay_update_1.5x.png"), 144);

        // Confetti finish armed: the party-popper glyph on a session row (left of its name). Arm the
        // first sample session the same way the right-click menu does, then repaint.
        canvas.SetUpdateAvailable(false);
        canvas.SetConfettiFinishAvailable(true);
        canvas.ToggleConfetti(SampleSessions()[1].SessionId);   // the "api" row: short name, shows the glyph cluster
        RenderControl(canvas, Path.Combine(outDir, "overlay_confetti_1x.png"), 96);
        RenderControl(canvas, Path.Combine(outDir, "overlay_confetti_1.5x.png"), 144);

        var probe = new OverlayCanvas();
        probe.Update(SampleSessions());
        probe.StartAutoCloseCountdown(20_000);
        RenderControl(probe, Path.Combine(outDir, "overlay_autoclose_1x.png"), 96);

        // "Jump to next session" landing highlight: the blue selection wash + left bar on the cycled row.
        // Rendered immediately after triggering it, so the fade timer hasn't run and it's at full strength.
        var cycleProbe = new OverlayCanvas();
        cycleProbe.Update(SampleSessions());
        cycleProbe.HighlightCycledSession(SampleSessions()[1].SessionId);
        RenderControl(cycleProbe, Path.Combine(outDir, "overlay_cycle_1x.png"), 96);

        // Service-status outage footer: a major-impact reading with one unresolved incident, so the
        // severity-tinted banner + dot + description render at the panel bottom.
        canvas.SetConfettiFinishAvailable(false);
        canvas.UpdateStatus(SampleStatus());
        RenderControl(canvas, Path.Combine(outDir, "overlay_status_1x.png"), 96);
        RenderControl(canvas, Path.Combine(outDir, "overlay_status_1.5x.png"), 144);
        canvas.UpdateStatus(StatusInfo.Healthy);

        // QR card (5.2): render the window's content card so the code + chrome can be eyeballed.
        var qr = new Windows.QrWindow("perch", "https://claude.ai/code/bridge-xyz-1234");
        if (qr.Content is Control qrCard)
            RenderControl(qrCard, Path.Combine(outDir, "qr_1x.png"), 96);

        // Glow edge bitmap (5.4): over a dark backdrop so the quadratic edge falloff is visible.
        var glowPanel = new Panel { Width = 480, Height = 320, Background = new SolidColorBrush(Color.FromRgb(20, 20, 26)) };
        glowPanel.Children.Add(new Image
        {
            Source = Windows.GlowWindow.BuildGlowBitmap(480, 320, 52, Theming.Palette.Orange),
            Stretch = Stretch.Fill,
        });
        RenderControl(glowPanel, Path.Combine(outDir, "glow_1x.png"), 96);

        // Stats dashboard (5.5): synthetic "Today" report so the cards, bars, and histograms render.
        var stats = new Views.StatsDashboard(showCost: true);
        stats.SetReport(SampleStatsReport(), null);
        RenderControl(stats, Path.Combine(outDir, "stats_1x.png"), 96);
        RenderControl(stats, Path.Combine(outDir, "stats_1.5x.png"), 144);

        // Stats dashboard, all-time scope: same control fed an "All time" range so the achievements grid
        // renders (it's gated to that scope) with a realistic mix of earned + locked trophies.
        var statsAll = new Views.StatsDashboard(showCost: true);
        var (allReport, allRange) = SampleAllTimeReport();
        statsAll.SetReport(allReport, allRange);
        RenderControl(statsAll, Path.Combine(outDir, "stats_alltime_1x.png"), 96);
        RenderControl(statsAll, Path.Combine(outDir, "stats_alltime_1.5x.png"), 144);

        // Dedicated Achievements window (the "trophy cabinet"): the roomy grid variant with per-badge
        // criteria lines, fed the same all-time sample so earned + locked tiles both show.
        var cabinet = new Views.AchievementsDashboard { Width = 640 };
        cabinet.SetBadges(AchievementCatalog.Evaluate(allReport, allRange, includeCost: true),
            "your lifetime trophies · since Jan 2026");
        RenderControl(cabinet, Path.Combine(outDir, "achievements_1x.png"), 96);
        RenderControl(cabinet, Path.Combine(outDir, "achievements_1.5x.png"), 144);

        // Perch Wrapped poster: a shareable Spotify-Wrapped-style card built from the sample report.
        // Rendered with the bundled bird icon so the header/footer icon paths are exercised too.
        IImage? brandIcon = null;
        try { brandIcon = new Bitmap(AssetLoader.Open(new Uri("avares://perch/Assets/icon.png"))); }
        catch { /* no icon — the poster just omits it */ }
        var wrapped = WrappedSummary.Build(SampleStatsReport(), null, "All Time", "since Jan 2026", showCost: true);
        RenderControl(new Views.WrappedPoster(wrapped, brandIcon), Path.Combine(outDir, "wrapped_1x.png"), 96);

        // The reveal card that hosts the poster (scaled) plus the Copy / Save / Close buttons.
        var wrappedCard = new Windows.WrappedWindow(wrapped, brandIcon, "perch-wrapped-all-time");
        if (wrappedCard.Content is Control card)
            RenderControl(card, Path.Combine(outDir, "wrapped_card_1x.png"), 96);

        // Overlay tooltips: a single-line glyph hint (the context-pressure figure) and the multi-line
        // usage panel, over the dark backdrop they float on — so the text centering can be eyeballed.
        var tipSingle = new Views.OverlayTooltip.Body
        {
            Lines = [new Views.OverlayTooltip.Line("128k/200k (64%)", Views.OverlayTooltip.FgColor, false)],
        };
        RenderOnBackdrop(tipSingle, Path.Combine(outDir, "tooltip_single_1x.png"), Color.FromRgb(40, 40, 52));
        var tipUsage = new Views.OverlayTooltip.Body
        {
            Lines =
            [
                new Views.OverlayTooltip.Line("Plan usage", Views.OverlayTooltip.FgColor, true),
                new Views.OverlayTooltip.Line("Session  62%  ·  resets 3:40pm", Views.OverlayTooltip.FgColor, false),
                new Views.OverlayTooltip.Line("Weekly   28%  ·  resets Thu", Views.OverlayTooltip.FgColor, false),
            ],
        };
        RenderOnBackdrop(tipUsage, Path.Combine(outDir, "tooltip_usage_1x.png"), Color.FromRgb(40, 40, 52));

        // Flight path (5.6): synthetic day with active / waiting / stuck segments across a few lanes.
        var flight = new Views.FlightPathTimeline();
        flight.SetReport(SampleFlightReport());
        RenderControl(flight, Path.Combine(outDir, "flightpath_1x.png"), 96);

        // Markdown transcript rendering (5.7b): headings, emphasis, inline code, code block, list, link.
        var md = new SelectableTextBlock { Width = 520, Margin = new Thickness(16), TextWrapping = TextWrapping.Wrap, FontSize = 13 };
        var mdInlines = new InlineCollection();
        MarkdownRender.Append(mdInlines,
            "## Plan\nHere's the **bold** and *italic* and `inline code`, plus a [link](https://x).\n\n"
            + "- first item\n- second item with `code`\n\n```\nvar x = 42;\nreturn x;\n```\n",
            new SolidColorBrush(Theming.Palette.Fg), new SolidColorBrush(Theming.Palette.Muted),
            new SolidColorBrush(Color.FromRgb(56, 189, 248)), new SolidColorBrush(Theming.Palette.Accent),
            new SolidColorBrush(Theming.Palette.Title));
        md.Inlines = mdInlines;
        var mdPanel = new Panel { Width = 520, Background = new SolidColorBrush(Color.FromRgb(18, 18, 24)) };
        mdPanel.Children.Add(md);
        RenderControl(mdPanel, Path.Combine(outDir, "markdown_1x.png"), 96);

        // Settings surface (Phase 3 remainder): a factory-built sample page exercising the new custom
        // controls — the pill toggles, the owner-drawn usage bars, the permission-mode legend, and the
        // context-pressure slider — over synthetic state (no subprocess, no real settings).
        RenderControl(SampleSettingsPage(), Path.Combine(outDir, "settings_1x.png"), 96);
        RenderControl(SampleSettingsPage(), Path.Combine(outDir, "settings_1.5x.png"), 144);

        Console.WriteLine($"Rendered PNGs to {Path.GetFullPath(outDir)}");
        return 0;
    }

    private static Control SampleSettingsPage()
    {
        static Windows.PerchToggle Toggle(bool on) { var t = new Windows.PerchToggle(); t.SetCheckedSilent(on); return t; }

        var stack = new StackPanel { Width = 560, Margin = new Thickness(16) };
        stack.Children.Add(Windows.SettingsUi.TitleRow("Usage limits", Toggle(true)));
        stack.Children.Add(Windows.SettingsUi.BodyText("Your account-wide 5-hour and weekly rate-limit usage."));
        var bars = new Windows.UsageBarsView();
        bars.SetOn(true);
        bars.SetUsage(SampleUsage());
        stack.Children.Add(bars);

        stack.Children.Add(Windows.SettingsUi.Separator());

        stack.Children.Add(Windows.SettingsUi.TitleRow("Permission mode badges", Toggle(true)));
        stack.Children.Add(new Windows.ModeLegendView());

        stack.Children.Add(Windows.SettingsUi.Separator());

        stack.Children.Add(Windows.SettingsUi.TitleRow("Context pressure", Toggle(true)));
        var slider = new Windows.ContextThresholdSliderView();
        slider.SetValues(50, 65, 80);
        stack.Children.Add(slider);
        stack.Children.Add(Windows.SettingsUi.SubRow("Show a green indicator below the first threshold", Toggle(false), out _));

        stack.Children.Add(Windows.SettingsUi.Separator());
        stack.Children.Add(Windows.SettingsUi.TitleRow("Stuck detection (off)", Toggle(false)));

        var panel = new Panel { Width = 592, Background = new SolidColorBrush(Color.FromRgb(24, 24, 32)) };
        panel.Children.Add(stack);
        return panel;
    }

    // Renders a control centred on a padded solid backdrop, so a self-contained panel (e.g. a tooltip
    // that draws its own dark card) can be eyeballed against a contrasting background.
    private static void RenderOnBackdrop(Control control, string path, Color backdrop)
    {
        var panel = new Panel { Background = new SolidColorBrush(backdrop) };
        control.Margin = new Thickness(20);
        panel.Children.Add(control);
        RenderControl(panel, path, 96);
    }

    private static void RenderControl(Control control, string path, double dpi)
    {
        control.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var size = control.DesiredSize;
        control.Arrange(new Rect(size));

        double scale = dpi / 96.0;
        var pixelSize = new PixelSize(
            (int)Math.Ceiling(size.Width * scale), (int)Math.Ceiling(size.Height * scale));
        using var rtb = new RenderTargetBitmap(pixelSize, new Vector(dpi, dpi));
        rtb.Render(control);
        using var fs = File.Create(path);
        rtb.Save(fs);
    }

    // A healthy-but-visible reading: session bar mid-yellow, weekly bar low-green, both with a reset
    // time an hour or two out so the expected-rate markers land partway along each track.
    private static UsageInfo SampleUsage()
    {
        var now = DateTime.Now;
        return new UsageInfo(
            FiveHourPercent: 62, SevenDayPercent: 28,
            FiveHourResetsAt: now.AddHours(2), SevenDayResetsAt: now.AddDays(4),
            LastUpdated: now, Ok: true, Error: null);
    }

    // A visible outage: major impact with one unresolved incident, so the footer's severity colour,
    // description, and click-menu content are all exercised.
    private static StatusInfo SampleStatus() =>
        new(StatusLevel.Major, "Partial System Outage",
            [new StatusIncident("Elevated errors on the Messages API", "major", "investigating",
                "We are investigating elevated error rates.", "https://status.claude.com/incidents/abc123")],
            StatusInfo.DefaultPageUrl, DateTime.Now, true, null);

    private static (IReadOnlyList<QuickLink> links, IReadOnlyList<string?> icons) SampleQuickLinks(string outDir)
    {
        // Write the bundled brand icon to a PNG file so the icon-drawing path (decode + 180° flip) is
        // exercised; the other two links carry no icon, so they draw initials.
        string brandPng = Path.Combine(outDir, "sample_quicklink.png");
        try
        {
            using var s = AssetLoader.Open(new Uri("avares://perch/Assets/icon.png"));
            using var fs = File.Create(brandPng);
            s.CopyTo(fs);
        }
        catch { brandPng = null!; }

        var links = new List<QuickLink>
        {
            new() { Name = "GitKraken" },
            new() { Name = "Slack" },
            new() { Name = "Microsoft Teams" },
        };
        var icons = new string?[] { brandPng, null, null };
        return (links, icons);
    }

    private static StatsReport SampleStatsReport()
    {
        var today = DateOnly.FromDateTime(DateTime.Now);
        var tk = new TokenTotals(Input: 120_000, Output: 45_000, CacheWrite: 30_000, CacheRead: 900_000);
        var hourly = new int[24];
        hourly[9] = 900; hourly[10] = 2400; hourly[11] = 1800; hourly[14] = 3000; hourly[15] = 2100; hourly[20] = 1200;
        return new StatsReport(
            Day: today, SessionCount: 7, ActiveTime: TimeSpan.FromHours(3) + TimeSpan.FromMinutes(42),
            Prompts: 58, ToolCalls: 214, SubAgents: 4, Teammates: 2,
            Tokens: tk, TeammateTokens: new TokenTotals(5_000, 2_000, 0, 10_000),
            EstimatedCost: 4.37m, CostComplete: true,
            Projects:
            [
                new ProjectStat("perch", 4, TimeSpan.FromHours(2), 800_000),
                new ProjectStat("api", 3, TimeSpan.FromMinutes(90), 400_000),
            ],
            Tools:
            [
                new ToolStat("Edit", 92), new ToolStat("Bash", 64), new ToolStat("Read", 58), new ToolStat("Grep", 30),
            ],
            Models: [new ModelStat("claude-opus-4-8", tk, 4.37m)],
            Branches:
            [
                new ProjectStat("main", 5, TimeSpan.FromHours(2), 700_000),
                new ProjectStat("feature-x", 2, TimeSpan.FromHours(1), 200_000),
            ],
            HourlyActiveSeconds: hourly);
    }

    // A beefy all-time report + range, tuned so a spread of achievements land on both sides of their
    // thresholds — enough earned to look rewarding, enough locked to show the aspirational greyed tiles.
    private static (StatsReport report, RangeReport range) SampleAllTimeReport()
    {
        var today = DateOnly.FromDateTime(DateTime.Now);
        var tk = new TokenTotals(Input: 5_000_000, Output: 6_000_000, CacheWrite: 4_000_000, CacheRead: 30_000_000);
        var hourly = new int[24];
        for (int h = 8; h <= 23; h++) hourly[h] = 1000 + h * 40;   // busy days, peak late — not all 24 hours
        hourly[23] = 40_000;                                       // clear late-night peak → Night Owl
        var report = new StatsReport(
            Day: today, SessionCount: 340, ActiveTime: TimeSpan.FromHours(126),
            Prompts: 3400, ToolCalls: 12_000, SubAgents: 140, Teammates: 3,
            Tokens: tk, TeammateTokens: TokenTotals.Zero, EstimatedCost: 260m, CostComplete: true,
            Projects: Enumerable.Range(1, 12).Select(i => new ProjectStat($"proj-{i}", 4, TimeSpan.FromHours(3), 1_000_000)).ToList(),
            Tools:
            [
                new ToolStat("Edit", 2000), new ToolStat("Read", 1500), new ToolStat("Bash", 700),
                new ToolStat("Grep", 600), new ToolStat("Glob", 260), new ToolStat("Task", 140),
                new ToolStat("WebSearch", 40), new ToolStat("WebFetch", 30),
            ],
            Models:
            [
                new ModelStat("claude-opus-4-8", tk, 260m),
                new ModelStat("claude-sonnet-4-5", TokenTotals.Zero, 0m),
                new ModelStat("claude-haiku-4-5", TokenTotals.Zero, 0m),
            ],
            Branches: Enumerable.Range(1, 14).Select(i => new ProjectStat($"branch-{i}", 2, TimeSpan.FromHours(1), 200_000)).ToList(),
            HourlyActiveSeconds: hourly);
        var range = new RangeReport("All time", "Active per day (last 30 days)", report,
            Trend: [], ActiveDays: 140, StreakDays: 9, BusiestDay: today.AddDays(-3),
            BusiestDayActive: TimeSpan.FromHours(7), LongestSession: TimeSpan.FromHours(5),
            FirstActiveDay: today.AddDays(-300));
        return (report, range);
    }

    private static FlightPathReport SampleFlightReport()
    {
        var day = DateOnly.FromDateTime(DateTime.Now);
        DateTime At(int h, int m) => day.ToDateTime(new TimeOnly(h, m));
        var lanes = new List<FlightLane>
        {
            new("s1", "perch", "avalonia-port", At(9, 10), At(12, 30), TimeSpan.FromHours(2), TimeSpan.FromMinutes(30),
            [
                new FlightSegment(At(9, 10), At(10, 20), FlightState.Active),
                new FlightSegment(At(10, 20), At(10, 50), FlightState.Waiting),
                new FlightSegment(At(10, 50), At(12, 30), FlightState.Active),
            ]),
            new("s2", "api", "main", At(11, 0), At(15, 0), TimeSpan.FromMinutes(90), TimeSpan.FromMinutes(20),
            [
                new FlightSegment(At(11, 0), At(11, 40), FlightState.Active),
                new FlightSegment(At(13, 0), At(13, 30), FlightState.Stuck),
                new FlightSegment(At(14, 0), At(15, 0), FlightState.Active),
            ]),
            new("s3", "docs-site", "", At(16, 0), At(17, 30), TimeSpan.FromMinutes(45), TimeSpan.Zero,
            [
                new FlightSegment(At(16, 0), At(16, 45), FlightState.Active),
            ]),
        };
        return new FlightPathReport(day, At(9, 0), At(18, 0), lanes);
    }

    private static IReadOnlyList<ClaudeSession> SampleSessions()
    {
        var now = DateTime.Now;
        var subs = new List<SubAgent>
        {
            new("t1", "teammate", "general-purpose", IsTeammate: true, Name: "arch-explorer",
                Color: "blue", Activity: "Reading Program.cs"),
            new("t2", "teammate", "general-purpose", IsTeammate: true, Name: "reviewer",
                Color: "green", IsIdle: true),
            new("a1", "Explore the auth flow", "general-purpose"),
        };
        return
        [
            new ClaudeSession("1234", "s1", SessionStatus.Running, @"C:\src\perch", "perch", now,
                Activity: "Editing OverlayForm.cs", SubAgents: subs, Mode: PermissionMode.AcceptEdits,
                Note: "risky refactor — waiting on review",
                ContextFill: 0.82f, BurnRate: 12300, GitStats: new GitLineStats(142, 37),
                Tasks: new List<TaskItem>
                {
                    new("Extract core", "extracting core", TaskState.Completed),
                    new("Port overlay", "porting overlay", TaskState.Pending),
                    new("Cutover", "cutting over", TaskState.Pending),
                }),
            new ClaudeSession("5678", "s2", SessionStatus.AwaitingInput, @"C:\src\api", "api", now,
                ExternalNotify: true,
                Artifacts: new List<Artifact> { new("https://claude.ai/code/artifact/1", "API report") }),
            new ClaudeSession("9012", "s3", SessionStatus.NeedsAttention, @"C:\src\docs", "docs-site", now,
                BridgeSessionId: "bridge-xyz", Stuck: new StuckSignal(StuckKind.FailingLoop, "repeating build")),
            new ClaudeSession("3456", "s4", SessionStatus.Idle, @"C:\src\scratch", "scratch", now,
                Note: "don't touch — bisecting a flaky test"),
            // A background/SDK session (Entrypoint != "cli") -> grouped under the Autonomous section.
            new ClaudeSession("7788", "s5", SessionStatus.Running, @"C:\src\bot", "nightly-bot", now,
                Entrypoint: "sdk-py"),
        ];
    }
}
