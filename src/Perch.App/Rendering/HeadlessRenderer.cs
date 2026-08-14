using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using Perch.Avalonia.Services;
using Perch.Avalonia.Theming;
using Perch.Avalonia.Views;
using Perch.Avalonia.Windows;
using Perch.Data;
using Perch.Data.Hypertree;
using Perch.Data.Replay;
using Perch.Platform;

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
    public static int RenderAll(string outDir, string? themeId = null)
    {
        Directory.CreateDirectory(outDir);

        AppBuilder.Configure<App>()
            .UseSkia()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
            .WithInterFont()
            .SetupWithoutStarting();

        // Render under the requested theme (default Midnight). Palette drives every owner-drawn surface.
        Theming.Palette.Apply(ThemeService.Resolve(themeId));

        var canvas = new OverlayCanvas();
        canvas.SetShowPullRequests(true);
        canvas.SetShowJiraTickets(true);
        canvas.Update(SampleData.Sessions());
        canvas.UpdateUsage(SampleData.Usage());
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

        // Dense status-change bubble (AppSettings.DenseStatusChangeStyle = Bubble): the fading speech bubble
        // that floats off the strip's logo. It's a separate top-level window at runtime, so render it
        // standalone — one per status colour/label, plus a left-docked variant to check the tail flips.
        RenderControl(DenseBubbleWindow.CreateForRender(DenseSide.Right, Palette.Fixed.StatusAttention.ToColor(), "done"),
            Path.Combine(outDir, "dense_bubble_done_1x.png"), 96);
        RenderControl(DenseBubbleWindow.CreateForRender(DenseSide.Right, Palette.Fixed.StatusAttention.ToColor(), "done"),
            Path.Combine(outDir, "dense_bubble_done_1.5x.png"), 144);
        RenderControl(DenseBubbleWindow.CreateForRender(DenseSide.Right, Palette.Fixed.StatusAwaiting.ToColor(), "input"),
            Path.Combine(outDir, "dense_bubble_input_1x.png"), 96);
        RenderControl(DenseBubbleWindow.CreateForRender(DenseSide.Right, Palette.Fixed.StatusError.ToColor(), "api error"),
            Path.Combine(outDir, "dense_bubble_apierror_1x.png"), 96);
        RenderControl(DenseBubbleWindow.CreateForRender(DenseSide.Left, Palette.Fixed.StatusAwaiting.ToColor(), "input"),
            Path.Combine(outDir, "dense_bubble_left_1x.png"), 96);

        // Update badge: the perch-orange download disc in the header cluster, shown while an update is
        // pending. Rendered at both DPIs so the owner-drawn disc + arrow stay crisp.
        canvas.SetUpdateAvailable(true);
        RenderControl(canvas, Path.Combine(outDir, "overlay_update_1x.png"), 96);
        RenderControl(canvas, Path.Combine(outDir, "overlay_update_1.5x.png"), 144);

        canvas.SetUpdateAvailable(false);

        var probe = new OverlayCanvas();
        probe.Update(SampleData.Sessions());
        probe.StartAutoCloseCountdown(20_000);
        RenderControl(probe, Path.Combine(outDir, "overlay_autoclose_1x.png"), 96);

        // Now-playing media strip: below the session rows, a track label + previous / play-pause / next.
        // Rendered "playing" (pause glyph shown) with previous disabled, to exercise the enabled/disabled
        // button styling and the label truncation.
        var mediaProbe = new OverlayCanvas();
        mediaProbe.Update(SampleData.Sessions());
        mediaProbe.SetShowMediaController(true);
        mediaProbe.UpdateMedia(new MediaSnapshot(
            Title: "Weightless (Ambient Transmission, Pt. 3)", Artist: "Marconi Union",
            IsPlaying: true, CanPlayPause: true, CanNext: true, CanPrevious: false,
            Position: TimeSpan.FromMinutes(2) + TimeSpan.FromSeconds(14), Duration: TimeSpan.FromMinutes(8)));
        RenderControl(mediaProbe, Path.Combine(outDir, "overlay_media_1x.png"), 96);
        RenderControl(mediaProbe, Path.Combine(outDir, "overlay_media_1.5x.png"), 144);

        // Microphone strip: who currently holds the mic, whose name is a link to that app's window. One state,
        // because that is all it has — the second app is there to exercise the tooltip's "Also:" line and the
        // long device name to exercise nothing on the strip itself, which stays a glyph and a name.
        var micProbe = new OverlayCanvas();
        micProbe.Update(SampleData.Sessions());
        micProbe.SetShowMicPresence(true);
        micProbe.UpdateMic(new MicSnapshot(
            [new MicUser("91750D7E.Slack_8she8kybcnzg4", "Slack", 4242, true, DateTimeOffset.Now.AddMinutes(-7))],
            DeviceName: "Microphone (Logitech Webcam C930e)"));
        RenderControl(micProbe, Path.Combine(outDir, "overlay_mic_1x.png"), 96);
        RenderControl(micProbe, Path.Combine(outDir, "overlay_mic_1.5x.png"), 144);

        micProbe.UpdateMic(new MicSnapshot(
            [new MicUser("MSTeams_8wekyb3d8bbwe", "Microsoft Teams", 5028, true, DateTimeOffset.Now.AddMinutes(-23))],
            DeviceName: "Microphone (Logitech Webcam C930e)"));
        RenderControl(micProbe, Path.Combine(outDir, "overlay_mic_call_1x.png"), 96);
        RenderControl(micProbe, Path.Combine(outDir, "overlay_mic_call_1.5x.png"), 144);

        // Hypertree strip: the branch list under the quick links, with the row the cursor is on marked.
        // The sample puts main mid-stack (Hypertree publishes the stack already flattened, main at its
        // slot) and gives one branch a long desktop label so the trailing-label truncation is exercised.
        var hyperProbe = new OverlayCanvas();
        hyperProbe.Update(SampleData.Sessions());
        hyperProbe.SetQuickLinks(links, icons);
        hyperProbe.SetHypertree(SampleHypertree());
        RenderControl(hyperProbe, Path.Combine(outDir, "overlay_hypertree_1x.png"), 96);
        RenderControl(hyperProbe, Path.Combine(outDir, "overlay_hypertree_1.5x.png"), 144);

        // Daemon strip: the background daemon's headless workers as their own "daemon" section below the
        // rows, capped at five with the "show +N more" overflow line (the spare is hidden as noise). One
        // worker also has a live session file (its hooks ran): it must render only in the daemon strip —
        // with the running-green dot taken from that session — while staying out of the normal rows and
        // the header's status counts (still 2 running, not 3).
        var daemonProbe = new OverlayCanvas();
        daemonProbe.Update(SampleData.Sessions()
            .Append(new ClaudeSession("61112", "f7d0b5fc-e679-492f-9fee-18a29f41602a",
                SessionStatus.Running, @"C:\src\hypertree", "hypertree", DateTime.Now))
            .ToList());
        daemonProbe.SetDaemonWorkers(SampleDaemonWorkers());
        RenderControl(daemonProbe, Path.Combine(outDir, "overlay_daemon_1x.png"), 96);
        RenderControl(daemonProbe, Path.Combine(outDir, "overlay_daemon_1.5x.png"), 144);

        // Empty roster: no sessions at all, so the header reads "no sessions" and the rows are simply
        // absent — but the strips the session list has nothing to do with (machine metrics, plan limits,
        // quick links, Hypertree branches) all stay, which is the whole point of this surface.
        var emptyProbe = new OverlayCanvas();
        emptyProbe.Update([]);
        emptyProbe.UpdateUsage(SampleData.Usage());
        emptyProbe.UpdateSystemMetrics(new SystemMetrics(CpuPercent: 37.5, UsedRamBytes: 12_000_000_000, TotalRamBytes: 32_000_000_000));
        emptyProbe.SetQuickLinks(links, icons);
        emptyProbe.SetHypertree(SampleHypertree());
        RenderControl(emptyProbe, Path.Combine(outDir, "overlay_empty_1x.png"), 96);
        RenderControl(emptyProbe, Path.Combine(outDir, "overlay_empty_1.5x.png"), 144);

        // "Jump to next session" landing highlight: the blue selection wash + left bar on the cycled row.
        // Rendered immediately after triggering it, so the fade timer hasn't run and it's at full strength.
        var cycleProbe = new OverlayCanvas();
        cycleProbe.Update(SampleData.Sessions());
        cycleProbe.HighlightCycledSession(SampleData.Sessions()[1].SessionId);
        RenderControl(cycleProbe, Path.Combine(outDir, "overlay_cycle_1x.png"), 96);

        // PR state-change banner: the transient full-row banner flashed over a session's row on a PR event.
        // Rendered right after triggering (the fade timer hasn't run, so full strength) — one per colourway
        // (merged purple, closed red, approved green, changes-requested amber, reviewed blue) so the full-row
        // cover + label read can be eyeballed.
        (string id, string text, OverlayCanvas.PrBannerKind kind, string file)[] prBanners =
        [
            (SampleData.Sessions()[0].SessionId, "Merged",                 OverlayCanvas.PrBannerKind.Merged,           "overlay_pr_merged"),
            (SampleData.Sessions()[1].SessionId, "Closed",                 OverlayCanvas.PrBannerKind.Closed,           "overlay_pr_closed"),
            (SampleData.Sessions()[0].SessionId, "Approved by octocat",    OverlayCanvas.PrBannerKind.Approved,         "overlay_pr_approved"),
            (SampleData.Sessions()[1].SessionId, "octocat requested changes", OverlayCanvas.PrBannerKind.ChangesRequested, "overlay_pr_changes"),
            (SampleData.Sessions()[0].SessionId, "Reviewed by octocat",    OverlayCanvas.PrBannerKind.Reviewed,         "overlay_pr_reviewed"),
        ];
        foreach (var (id, text, kind, file) in prBanners)
        {
            var prProbe = new OverlayCanvas();
            prProbe.SetShowPullRequests(true);
            prProbe.Update(SampleData.Sessions());
            prProbe.ShowPrBanner(id, text, kind);
            RenderControl(prProbe, Path.Combine(outDir, $"{file}_1x.png"), 96);
        }

        // Replay branding: the light-blue "Perch - Replay" header label + 2px border shown under
        // `perch replay`, so a recording can't be mistaken for live sessions.
        var replayProbe = new OverlayCanvas { ReplayMode = true };
        replayProbe.Update(SampleData.Sessions());
        RenderControl(replayProbe, Path.Combine(outDir, "overlay_replay_1x.png"), 96);
        RenderControl(replayProbe, Path.Combine(outDir, "overlay_replay_1.5x.png"), 144);

        // Replay timeline scrubber: the played track + a marker tick per notable frame (prompt / tool /
        // sub-agent / interrupt), coloured by kind, with the playhead partway along.
        var timeline = new ReplayTimelineBar { Width = 448 };
        timeline.SetDuration(300_000);
        timeline.SetMarkers(SampleMarkers());
        timeline.SetPosition(120_000);
        RenderControl(timeline, Path.Combine(outDir, "replay_timeline_1x.png"), 96);
        RenderControl(timeline, Path.Combine(outDir, "replay_timeline_1.5x.png"), 144);

        // Service-status outage footer: a major-impact reading with one unresolved incident, so the
        // severity-tinted banner + dot + description render at the panel bottom.
        canvas.UpdateStatus(SampleStatus());
        RenderControl(canvas, Path.Combine(outDir, "overlay_status_1x.png"), 96);
        RenderControl(canvas, Path.Combine(outDir, "overlay_status_1.5x.png"), 144);
        canvas.UpdateStatus(StatusInfo.Healthy);

        // QR card (5.2): render the window's content card so the code + chrome can be eyeballed.
        var qr = new Windows.QrWindow("perch", "https://claude.ai/code/bridge-xyz-1234");
        if (qr.Content is Control qrCard)
            RenderControl(qrCard, Path.Combine(outDir, "qr_1x.png"), 96);

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

        // Change Review diff (git-review M1): a synthetic multi-file diff so the added/removed/context
        // colours, the file bars, and the monospace alignment can be eyeballed without a repo — in both
        // the unified and side-by-side split layouts.
        var diffUnified = new Views.DiffView { Width = 760 };
        diffUnified.SetDiff(SampleDiff(), null);
        RenderControl(diffUnified, Path.Combine(outDir, "change_review_unified_1x.png"), 96);
        RenderControl(diffUnified, Path.Combine(outDir, "change_review_unified_1.5x.png"), 144);

        var diffSplit = new Views.DiffView { Width = 760 };
        diffSplit.SetDiff(SampleDiff(), null);
        diffSplit.SetSplit(true);
        RenderControl(diffSplit, Path.Combine(outDir, "change_review_split_1x.png"), 96);
        RenderControl(diffSplit, Path.Combine(outDir, "change_review_split_1.5x.png"), 144);

        // Find state: every "the" match highlighted (yellow) with the current one prominent (orange).
        var diffFind = new Views.DiffView { Width = 760 };
        diffFind.SetDiff(SampleDiff(), null);
        diffFind.SetSearch("the");
        RenderControl(diffFind, Path.Combine(outDir, "change_review_find_1x.png"), 96);
        RenderControl(diffFind, Path.Combine(outDir, "change_review_find_1.5x.png"), 144);

        // Staging surfaces. Unified/Split modes (SetPerHunk false) put a whole-file "Stage file"/"Discard
        // file" button set on each file header; Hunk mode (SetPerHunk true) puts stage/discard buttons on
        // each hunk header instead. A stageable ("Unstaged") section drives which buttons show.
        var diffFileStage = new Views.DiffView { Width = 760 };
        diffFileStage.SetSections([new Views.DiffSection("Unstaged", SampleDiff(), Views.HunkStageAction.Stage)], null);
        RenderControl(diffFileStage, Path.Combine(outDir, "change_review_stage_file_1x.png"), 96);
        RenderControl(diffFileStage, Path.Combine(outDir, "change_review_stage_file_1.5x.png"), 144);

        var diffHunkStage = new Views.DiffView { Width = 760 };
        diffHunkStage.SetPerHunk(true);
        diffHunkStage.SetSplit(true);
        diffHunkStage.SetSections([new Views.DiffSection("Unstaged", SampleDiff(), Views.HunkStageAction.Stage)], null);
        RenderControl(diffHunkStage, Path.Combine(outDir, "change_review_stage_hunk_1x.png"), 96);
        RenderControl(diffHunkStage, Path.Combine(outDir, "change_review_stage_hunk_1.5x.png"), 144);

        // Plain content view (the floating bar's "Previous"/"Current"): a whole file as an all-context,
        // single-column numbered listing — no deltas, no bands, no staging buttons.
        string[] plainLines = ["using System;", "", "namespace Perch;", "", "class Sample", "{", "    public int N = 42;", "}"];
        var plainHunk = new GitDiffHunk($"@@ -1,{plainLines.Length} +1,{plainLines.Length} @@",
            [.. plainLines.Select(l => new GitDiffLine(GitDiffLineKind.Context, l))]);
        var plainFile = new GitDiffFile("src/Sample.cs", "src/Sample.cs", false, [plainHunk]);
        var diffPlain = new Views.DiffView { Width = 760 };
        diffPlain.SetSections([new Views.DiffSection(null, new GitDiff([plainFile]))], null, plain: true);
        RenderControl(diffPlain, Path.Combine(outDir, "change_review_plain_1x.png"), 96);
        RenderControl(diffPlain, Path.Combine(outDir, "change_review_plain_1.5x.png"), 144);

        // Git tree window: the three panes — the commit-graph nodes (WIP knot + lane rail + HEAD tag + the
        // terminal base node), the files the selected node touched, and the diff — composed as a static
        // surface (the live window's async loads + ListBox virtualisation don't realise in a one-shot bitmap).
        // Rendered in both the window's own dark and light modes (the per-window light toggle).
        RenderControl(BuildTreeSurface(light: false), Path.Combine(outDir, "git_tree_1x.png"), 96);
        RenderControl(BuildTreeSurface(light: false), Path.Combine(outDir, "git_tree_1.5x.png"), 144);
        RenderControl(BuildTreeSurface(light: true), Path.Combine(outDir, "git_tree_light_1x.png"), 96);

        // Markdown viewer window: the file tree (session produced/referenced groups + the project folder
        // tree) beside the rendered preview. Unlike the owner-drawn surfaces this uses templated controls
        // (TreeView/ScrollViewer/SelectableTextBlock) that only realise inside a shown window, so it's
        // captured via CaptureRenderedFrame rather than a detached one-shot bitmap.
        RenderMarkdownWindow(outDir);

        // Dedicated Achievements window (the "trophy cabinet"): the roomy grid variant with per-badge
        // criteria lines, fed the same all-time sample so earned + locked tiles both show.
        var cabinet = new Views.AchievementsDashboard { Width = 840 };
        cabinet.SetBadges(AchievementCatalog.Evaluate(allReport, allRange, includeCost: true),
            "your lifetime trophies · since Jan 2026");
        RenderControl(cabinet, Path.Combine(outDir, "achievements_1x.png"), 96);
        RenderControl(cabinet, Path.Combine(outDir, "achievements_1.5x.png"), 144);

        // Achievement unlock reveal, single card: the vignette + coin-flip card (frozen at its settled,
        // face-up frame) under the "Achievement Unlocked!" heading with the OK / Don't-show-again buttons.
        var reveal = Windows.AchievementCardWindow.BuildStaticSurface(
            [new AchievementUnlock("Token Titan", "🏆", "Tokens · Lvl 5", "1B input tokens", AchievementTier.Gold)], 900, 680);
        RenderControl(reveal, Path.Combine(outDir, "achievement_reveal_1x.png"), 96);
        RenderControl(reveal, Path.Combine(outDir, "achievement_reveal_1.5x.png"), 144);

        // Achievement unlock reveal, batch: four unlocks → three cards side by side plus a "+1 more" card.
        var revealBatch = Windows.AchievementCardWindow.BuildStaticSurface(
        [
            new AchievementUnlock("Token Titan", "🏆", "Tokens · Lvl 5", "1B input tokens", AchievementTier.Gold),
            new AchievementUnlock("Tool Master", "🛠", "Tools · Lvl 4", "100,000 tool calls", AchievementTier.Gold),
            new AchievementUnlock("Night Owl", "🦉", "Sessions · Lvl 3", "100 sessions", AchievementTier.Silver),
            new AchievementUnlock("Streak Keeper", "🔥", "Streak · Lvl 2", "7-day streak", AchievementTier.Bronze),
        ], 1280, 680);
        RenderControl(revealBatch, Path.Combine(outDir, "achievement_reveal_batch_1x.png"), 96);
        RenderControl(revealBatch, Path.Combine(outDir, "achievement_reveal_batch_1.5x.png"), 144);

        // The secret Space Invaders clone: the title frame (full swarm + prompt) and a posed in-play frame
        // (a shot rising, bombs falling, a few invaders cleared). Timers don't tick headless, so both frames
        // are static poses.
        RenderControl(new Windows.InvadersField(), Path.Combine(outDir, "invaders_title_1x.png"), 96);
        var invadersPlay = new Windows.InvadersField();
        invadersPlay.SnapshotPlaying();
        RenderControl(invadersPlay, Path.Combine(outDir, "invaders_play_1x.png"), 96);

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

        // Overlay tooltips: the context-pressure hint (figure, then the model and how its window was
        // worked out) and the multi-line usage panel, over the dark backdrop they float on — so the text
        // centering can be eyeballed.
        var tipSingle = new Views.OverlayTooltip.Body
        {
            Lines =
            [
                new Views.OverlayTooltip.Line("128k/200k (64%)", Views.OverlayTooltip.FgColor, false),
                new Views.OverlayTooltip.Line("Sonnet 4.6 · from /model line", Views.OverlayTooltip.FgColor, false),
            ],
        };
        RenderOnBackdrop(tipSingle, Path.Combine(outDir, "tooltip_single_1x.png"), Color.FromRgb(40, 40, 52));
        var tipUsage = new Views.OverlayTooltip.Body
        {
            Lines =
            [
                new Views.OverlayTooltip.Line("Plan usage", Views.OverlayTooltip.FgColor, true),
                new Views.OverlayTooltip.Line("Session  62%  ·  resets 3:40pm", Views.OverlayTooltip.FgColor, false),
                new Views.OverlayTooltip.Line("Weekly   28%  ·  resets Thu", Views.OverlayTooltip.FgColor, false),
                new Views.OverlayTooltip.Line("Fable    41%  ·  resets Thu", Views.OverlayTooltip.FgColor, false),
            ],
        };
        RenderOnBackdrop(tipUsage, Path.Combine(outDir, "tooltip_usage_1x.png"), Color.FromRgb(40, 40, 52));
        // PR hover tooltip: a bold header with each CI check listed beneath it as a status-coloured child
        // (green pass / red fail / blue running) — mirrors OverlayCanvas.ShowPrTooltip.
        var tipPr = new Views.OverlayTooltip.Body
        {
            Lines =
            [
                new Views.OverlayTooltip.Line("#1135 · Open · Surface PRs on the overlay", Views.OverlayTooltip.FgColor, true),
                new Views.OverlayTooltip.Line("    ✓  build",          Color.FromRgb(74, 222, 128), false),
                new Views.OverlayTooltip.Line("    ✗  unit-tests",     Color.FromRgb(248, 113, 113), false),
                new Views.OverlayTooltip.Line("    •  deploy-preview", Color.FromRgb(96, 165, 250), false),
                new Views.OverlayTooltip.Line("    ✓  lint",           Color.FromRgb(74, 222, 128), false),
            ],
        };
        RenderOnBackdrop(tipPr, Path.Combine(outDir, "tooltip_pr_1x.png"), Color.FromRgb(40, 40, 52));

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

        // Row note glyph with the notes indicator on: s1 has a session note (full amber) and s2 ("api")
        // has only a project note (dimmed amber, so it recedes) — both surface the glyph, but the
        // project-only note is deliberately quieter.
        var noteProbe = new OverlayCanvas();
        noteProbe.SetShowNoteLine(true);
        noteProbe.Update(SampleData.Sessions());
        RenderControl(noteProbe, Path.Combine(outDir, "overlay_notes_1x.png"), 96);

        // Sticky notes: the global scratch pad (single section) and a session row note (project + session
        // sections at double height), over a dark backdrop so the paper, tape strip and shadow read.
        RenderOnBackdrop(Windows.StickyNoteWindow.BuildPreviewSurface(sessionRow: false),
            Path.Combine(outDir, "note_scratch_1x.png"), Color.FromRgb(30, 30, 38));
        RenderOnBackdrop(Windows.StickyNoteWindow.BuildPreviewSurface(sessionRow: true),
            Path.Combine(outDir, "note_row_1x.png"), Color.FromRgb(30, 30, 38));

        // Settings surface (Phase 3 remainder): a factory-built sample page exercising the new custom
        // controls — the pill toggles, the owner-drawn usage bars, the permission-mode legend, and the
        // context-pressure slider — over synthetic state (no subprocess, no real settings).
        RenderControl(SampleSettingsPage(), Path.Combine(outDir, "settings_1x.png"), 96);
        RenderControl(SampleSettingsPage(), Path.Combine(outDir, "settings_1.5x.png"), 144);

        // Settings live-preview chain (M0 spike): drive a fresh canvas purely through
        // OverlaySettingsGates.Apply with a *cloned* AppSettings — exactly the mechanism the Settings
        // preview pane will use. One frame with the opt-in glyphs turned on (git churn, burn rate, metrics,
        // notes), one with the row glyphs gated off, to prove a mutated clone re-gates what renders.
        var previewOn = new OverlayCanvas();
        previewOn.Update(SampleData.Sessions());
        previewOn.UpdateUsage(SampleData.Usage());
        previewOn.UpdateSystemMetrics(SampleData.SystemMetrics());
        previewOn.UpdateSessionMetrics(SampleData.SessionMetrics());
        OverlaySettingsGates.Apply(previewOn, PreviewSettings(allGlyphsOn: true));
        RenderControl(previewOn, Path.Combine(outDir, "overlay_preview_on_1x.png"), 96);

        var previewOff = new OverlayCanvas();
        previewOff.Update(SampleData.Sessions());
        previewOff.UpdateUsage(SampleData.Usage());
        OverlaySettingsGates.Apply(previewOff, PreviewSettings(allGlyphsOn: false));
        RenderControl(previewOff, Path.Combine(outDir, "overlay_preview_off_1x.png"), 96);

        // The actual Settings live-preview control (M1): a Viewbox-scaled OverlayCanvas in its framed pane,
        // applied a busy snapshot, at both DPIs — to confirm the miniature renders crisply and reflects the
        // settings clone it's handed.
        var pane = new PreviewPane();
        pane.Apply(PreviewSettings(allGlyphsOn: true));
        RenderControl(pane, Path.Combine(outDir, "preview_pane_1x.png"), 96);
        RenderControl(pane, Path.Combine(outDir, "preview_pane_1.5x.png"), 144);

        // Settings search page (M2): the registry-driven filter over every setting, captured with a query
        // applied so the result rows, breadcrumbs, live toggles and the "matched keyword" hint all render.
        RenderControl(SampleSearchPage("chime"), Path.Combine(outDir, "settings_search_1x.png"), 96);
        RenderControl(SampleSearchPage(""),      Path.Combine(outDir, "settings_search_index_1x.png"), 96);

        // Settings feature catalogue (M3): surface-grouped cards with previews, live toggles/steppers, and
        // the surface chip row. Rendered wide enough to show the cards flowing into columns.
        RenderControl(SampleCatalogPage(), Path.Combine(outDir, "settings_catalog_1x.png"), 96);

        // Unified Settings shell (M4): the two-pane Features view — the catalogue on the left with the live
        // overlay preview docked on the right, at a representative window size.
        RenderControl(SampleShellPage(), Path.Combine(outDir, "settings_shell_1x.png"), 96);

        Console.WriteLine($"Rendered PNGs to {Path.GetFullPath(outDir)}");
        return 0;
    }

    private static Control SampleSettingsPage()
    {
        static Windows.PerchToggle Toggle(bool on) { var t = new Windows.PerchToggle(); t.SetCheckedSilent(on); return t; }

        var stack = new StackPanel { Width = 560, Margin = new Thickness(16) };
        stack.Children.Add(Windows.SettingsUi.TitleRow("Usage limits", Toggle(true)));
        stack.Children.Add(Windows.SettingsUi.BodyText("Your account-wide 5-hour and weekly rate-limit usage, plus any per-model weekly limits."));
        var bars = new Windows.UsageBarsView();
        bars.SetOn(true);
        bars.SetUsage(SampleData.Usage());
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

    // The Settings search page with a query applied, framed the way the content area presents it, so the
    // registry-driven results can be eyeballed. Uses default settings + empty hooks (no live wiring needed
    // to render).
    private static Control SampleSearchPage(string query)
    {
        var view = new Windows.SettingsSearchView(new AppSettings(), new Windows.SettingsHooks())
        {
            Width = 620, Margin = new Thickness(16),
        };
        view.SetQuery(query);
        return new Panel { Width = 652, Background = new SolidColorBrush(Color.FromRgb(24, 24, 32)), Children = { view } };
    }

    // The feature catalogue, framed the way the content area presents it, wide enough for two card columns.
    private static Control SampleCatalogPage()
    {
        var view = new Windows.SettingsCatalogView(new AppSettings(), new Windows.SettingsHooks())
        {
            Width = 700, Margin = new Thickness(16),
        };
        return new Panel { Width = 732, Background = new SolidColorBrush(Color.FromRgb(24, 24, 32)), Children = { view } };
    }

    // The M4 two-pane Features shell: catalogue on the left, the live overlay preview docked on the right.
    // Composed without the window's ScrollViewers (a one-shot RenderTargetBitmap doesn't realise scrolled
    // content), so the split and the docked preview read in a single frame.
    private static Control SampleShellPage()
    {
        var catalog = new Windows.SettingsCatalogView(new AppSettings(), new Windows.SettingsHooks())
        {
            Margin = new Thickness(16), VerticalAlignment = VerticalAlignment.Top,
        };
        Grid.SetColumn(catalog, 0);

        var preview = new PreviewPane();
        // Turn on the opt-in strips so the daemon/mic/now-playing sections all render in the probe.
        var seeded = new AppSettings { ShowMediaController = true, ShowMicPresence = true };
        preview.Apply(seeded);
        var dockStack = new StackPanel { Margin = new Thickness(14, 16, 16, 16) };
        dockStack.Children.Add(new TextBlock
        {
            Text = "LIVE PREVIEW", FontSize = 11, FontWeight = FontWeight.SemiBold,
            Foreground = Theming.Palette.MutedBrush, Margin = new Thickness(2, 0, 0, 8),
        });
        dockStack.Children.Add(preview);
        var dock = new Border
        {
            Width = 300, BorderThickness = new Thickness(1, 0, 0, 0), BorderBrush = Theming.Palette.BorderBrush,
            VerticalAlignment = VerticalAlignment.Stretch, Child = dockStack,
        };
        Grid.SetColumn(dock, 1);

        // The content area at the real default window width (1220) minus the 178-wide nav rail, so the
        // two-column fit and leftover gap read as they will in the app. (The probe has no scrollbar, so the
        // real cards column is ~18px narrower — still comfortably two columns.)
        return new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,Auto"), Width = 1220 - 178,
            Background = Theming.Palette.FormBgBrush, Children = { catalog, dock },
        };
    }

    // A cloned settings snapshot for the live-preview render probe. `allGlyphsOn` flips the opt-in
    // indicators on (git churn, burn rate, notes, metrics) for a busy overlay; otherwise it gates the
    // common row glyphs off for a stripped-back one. Goes through Clone() to exercise the snapshot path
    // the preview pane relies on.
    private static AppSettings PreviewSettings(bool allGlyphsOn)
    {
        var s = new AppSettings().Clone();
        if (allGlyphsOn)
        {
            s.ShowGitStats = true;
            s.ShowBurnRate = true;
            s.ShowNotes = true;
            s.ShowPullRequests = true;
            s.ShowSystemMetrics = true;
            s.ShowSessionMetrics = true;
            s.ShowMediaController = true;
            s.ShowMicPresence = true;
            s.ShowMonthlySpend = true;
        }
        else
        {
            s.ShowUsage = false;
            s.ShowPermissionModeBadges = false;
            s.ShowTaskProgress = false;
            s.ShowContextPressure = false;
            s.ShowWaitingTimer = false;
            s.ShowArtifacts = false;
            s.ShowServiceStatus = false;
        }
        return s;
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

    // A spread of marker kinds across a 5-minute scene, so the timeline shows each tick colour.
    private static IReadOnlyList<ReplayMarker> SampleMarkers() =>
    [
        new(0,       ReplayMarkerKind.Prompt,        "prompt"),
        new(28_000,  ReplayMarkerKind.ToolUse,       "Bash"),
        new(45_000,  ReplayMarkerKind.ToolUse,       "Read"),
        new(90_000,  ReplayMarkerKind.SubagentSpawn, "sub-agent"),
        new(150_000, ReplayMarkerKind.Interrupt,     "interrupt"),
        new(210_000, ReplayMarkerKind.Prompt,        "prompt"),
        new(240_000, ReplayMarkerKind.ToolUse,       "Edit"),
        new(300_000, ReplayMarkerKind.Prompt,        "prompt"),
    ];

    // A visible outage: major impact with one unresolved incident, so the footer's severity colour,
    // description, and click-menu content are all exercised.
    private static StatusInfo SampleStatus() =>
        new(StatusLevel.Major, "Partial System Outage",
            [new StatusIncident("Elevated errors on the Messages API", "major", "investigating",
                "We are investigating elevated error rates.", "https://status.claude.com/incidents/abc123")],
            StatusInfo.DefaultPageUrl, DateTime.Now, true, null);

    // A Hypertree stack as its tray would publish one: two branches around the main timeline, the cursor
    // sitting on main. Cursors point at a non-first desktop so the trailing "where a jump lands" label is
    // visibly the resume point rather than just the first entry.
    private static HypertreeStatus SampleHypertree() => new()
    {
        Schema = 1,
        Version = "0.2.0",
        Pid = Environment.ProcessId,
        Rows =
        [
            new HypertreeRow
            {
                Kind = "branch", Id = Guid.NewGuid(), Name = "perch", Cursor = 1,
                Desktops = [new() { Label = "code" }, new() { Label = "docs" }],
            },
            new HypertreeRow
            {
                Kind = "main", Name = "main", Cursor = 4,
                Desktops =
                [
                    new() { Label = "1 - Admin" }, new() { Label = "2 - Git+Jira" },
                    new() { Label = "3 - Spa" },   new() { Label = "4 - Api" },
                    new() { Label = "5 - Mobile" },
                ],
            },
            new HypertreeRow
            {
                Kind = "branch", Id = Guid.NewGuid(), Name = "hypertree-cli-spike", Cursor = 0,
                Desktops = [new() { Label = "a rather long desktop label" }],
            },
        ],
        Current = new HypertreePosition { Row = 1, Desktop = 4 },
    };

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

    // A synthetic unified diff for the Change Review surface: a modified file (context + add + remove),
    // an added file (all-green), and a binary file — enough to exercise every DiffView branch.
    private static GitDiff SampleDiff()
    {
        GitDiffLine C(string t) => new(GitDiffLineKind.Context, t);
        GitDiffLine A(string t) => new(GitDiffLineKind.Added, t);
        GitDiffLine R(string t) => new(GitDiffLineKind.Removed, t);

        var modified = new GitDiffFile("src/Overlay/SessionRow.cs", "src/Overlay/SessionRow.cs", false,
        [
            new GitDiffHunk("@@ -18,7 +18,8 @@ internal sealed class SessionRow",
            [
                C("    public string Title { get; init; }"),
                C(""),
                R("    private int _height = 24;"),
                A("    private int _height = 28;"),
                A("    private bool _dense;"),
                C(""),
                C("    public void Measure()"),
            ]),
        ]);

        var added = new GitDiffFile(null, "docs/change-review.md", false,
        [
            new GitDiffHunk("@@ -0,0 +1,4 @@",
            [
                A("# Change review"),
                A(""),
                A("Read-only git review, launched from a session's right-click menu. This line is intentionally long so the wrap-text option and the aligned line-number gutter can both be eyeballed when it soft-wraps across several visual rows."),
                A("Experimental."),
            ]),
        ]);

        var binary = new GitDiffFile("assets/icon.png", "assets/icon.png", true, []);

        return new GitDiff([modified, added, binary]);
    }

    // Shows a real MarkdownWindow seeded with sample data (session file groups + a project folder tree on
    // the left, a rendered preview on the right) and captures its rendered frame.
    private static void RenderMarkdownWindow(string outDir)
    {
        const string cwd = @"C:\src\perch";
        var sets = new MarkdownFileSets(
            [@"C:\src\perch\docs\markdown-viewer-plan.md"],
            [@"C:\src\perch\README.md", @"C:\src\perch\CLAUDE.md"]);
        var project = new MarkdownProjectFiles(
            ["CHANGELOG.md", "README.md", "docs/distribution-plan.md", "docs/macos-port-plan.md",
             "docs/markdown-viewer-plan.md", "docs/theming-plan.md"],
            Truncated: false);
        const string sampleMd =
            "# Markdown viewer\n\nA **rich** view of `*.md`, with:\n\n- the files this session produced or referenced\n" +
            "- a `.gitignore`-aware project tree\n- a live rendered preview\n\n> Rendered through the in-house MarkdownRender.\n\n" +
            "## Editing\n\nAn editable split (source + preview) with save lands in Phase 4.";

        // Default: dark window chrome with a light "paper" preview. Plus the inverse (light window, dark
        // preview) to eyeball both per-window palettes and the independent preview override.
        Capture("markdown_window_1x.png", windowLight: false, previewLight: true);
        Capture("markdown_window_light_1x.png", windowLight: true, previewLight: false);

        void Capture(string file, bool windowLight, bool previewLight)
        {
            var w = new MarkdownWindow(new AppSettings()) { Width = 1000, Height = 620 };
            w.SeedForRender(cwd, sets, project, @"C:\src\perch\docs\markdown-viewer-plan.md", sampleMd,
                windowLight, previewLight);
            w.Show();
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            var frame = w.CaptureRenderedFrame();
            if (frame != null)
            {
                using var fs = File.Create(Path.Combine(outDir, file));
                frame.Save(fs);
            }
            w.Close();
        }
    }

    // Composes the git Tree window's three panes into one static surface for eyeballing: the commit-graph
    // node rows (built by the real GitTreeWindow.NodeRow), a sample files list, and the diff view. Painted
    // from the window's own light/dark palette so both modes can be eyeballed.
    private static Control BuildTreeSurface(bool light)
    {
        var pal = light ? GitTreeWindow.TreePalette.Light() : GitTreeWindow.TreePalette.Dark();
        var now = DateTimeOffset.Now;
        GitTreeWindow.TreeNode Commit(string hash, string subj, int hoursAgo, bool head) =>
            new(GitTreeWindow.NodeKind.Commit, head, false, false,
                new GitCommit(hash + "0000000", hash, "Jon Howell", now.AddHours(-hoursAgo), subj, subj, "p"), 0, null);

        var nodes = new StackPanel();
        nodes.Children.Add(GitTreeWindow.NodeRow(
            new GitTreeWindow.TreeNode(GitTreeWindow.NodeKind.Wip, false, IsFirst: true, IsLast: false, default, 3, null), pal));
        nodes.Children.Add(GitTreeWindow.NodeRow(Commit("1c448d9", "middle click to open in new browser for links", 2, head: true), pal));
        nodes.Children.Add(GitTreeWindow.NodeRow(Commit("754fd60", "add more subtle bubble alert while in dense mode", 5, head: false), pal));
        nodes.Children.Add(GitTreeWindow.NodeRow(Commit("ebdb3ce", "Stronger handling of lower screen relative positioning", 27, head: false), pal));
        nodes.Children.Add(GitTreeWindow.NodeRow(
            new GitTreeWindow.TreeNode(GitTreeWindow.NodeKind.Base, false, IsFirst: false, IsLast: true, default, 0, "origin/main"), pal));

        var mono = new FontFamily("Cascadia Code, Consolas, Menlo, monospace");
        TextBlock FileRow(string t, IBrush c) => new()
        {
            Text = t, FontFamily = mono, FontSize = 12, Margin = new Thickness(12, 6, 12, 6),
            Foreground = c, TextTrimming = TextTrimming.CharacterEllipsis,
        };
        var files = new StackPanel();
        files.Children.Add(FileRow("M  src/Perch.App/Views/OverlayCanvas.cs", pal.Orange));
        files.Children.Add(FileRow("A  src/Perch.App/Windows/GitTreeWindow.cs", pal.Green));
        files.Children.Add(FileRow("A  docs/git-tree-window-design.html", pal.Green));

        var diff = new Views.DiffView { Width = 600 };
        diff.SetLight(light);
        // A working-tree ("Unstaged") section so the whole-file "Stage file"/"Discard file" buttons show on
        // the file header in the eyeball (the default Unified mode; Hunk mode moves them onto each hunk).
        diff.SetSections([new Views.DiffSection("Unstaged", SampleDiff(), Views.HunkStageAction.Stage)], null);

        Control Pane(string title, Control body)
        {
            var label = new TextBlock
            {
                Text = title, Foreground = pal.Muted, FontSize = 11, FontWeight = FontWeight.SemiBold,
                Margin = new Thickness(12, 10, 12, 6),
            };
            var dp = new DockPanel { LastChildFill = true };
            DockPanel.SetDock(label, Dock.Top);
            dp.Children.Add(label);
            dp.Children.Add(body);
            return dp;
        }

        Control Sep() => new Border { Background = pal.Separator };

        var grid = new Grid
        {
            Width = 1180, Height = 340,
            Background = pal.WindowBg,
            ColumnDefinitions = new ColumnDefinitions("300,1,254,1,*"),
        };
        void Add(Control c, int col) { c.SetValue(Grid.ColumnProperty, col); grid.Children.Add(c); }
        Add(Pane("Commits · this branch", nodes), 0);
        Add(Sep(), 1);
        Add(Pane("Files", files), 2);
        Add(Sep(), 3);
        Add(Pane("Diff", diff), 4);
        return grid;
    }

    private static StatsReport SampleStatsReport()
    {
        var today = DateOnly.FromDateTime(DateTime.Now);
        var tk = new TokenTotals(Input: 120_000, Output: 45_000, CacheWrite: 30_000, CacheRead: 900_000);
        var hourly = new int[24];
        hourly[9] = 900; hourly[10] = 2400; hourly[11] = 1800; hourly[14] = 3000; hourly[15] = 2100; hourly[20] = 1200;
        return new StatsReport(
            Day: today, SessionCount: 7, ActiveTime: TimeSpan.FromHours(3) + TimeSpan.FromMinutes(42),
            Prompts: 58, Swears: 12, Whoops: 6, ToolCalls: 214, SubAgents: 4, Teammates: 2,
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
            Prompts: 3400, Swears: 140, Whoops: 120, ToolCalls: 12_000, SubAgents: 140, Teammates: 3,
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
            FirstActiveDay: today.AddDays(-300), MaxSessionWhoops: 5);
        return (report, range);
    }

    private static FlightPathReport SampleFlightReport()
    {
        var day = DateOnly.FromDateTime(DateTime.Now);
        DateTime At(int h, int m) => day.ToDateTime(new TimeOnly(h, m));
        var lanes = new List<FlightLane>
        {
            new("s1", "perch", "avalonia-port", At(9, 10), At(12, 30), TimeSpan.FromHours(2), TimeSpan.FromMinutes(30), TimeSpan.Zero,
            [
                new FlightSegment(At(9, 10), At(10, 20), FlightState.Active),
                new FlightSegment(At(10, 20), At(10, 50), FlightState.AwaitingInput),
                new FlightSegment(At(10, 50), At(12, 30), FlightState.Active),
            ],
            []),
            new("s2", "api", "main", At(11, 0), At(15, 0), TimeSpan.FromMinutes(90), TimeSpan.Zero, TimeSpan.FromMinutes(30),
            [
                new FlightSegment(At(11, 0), At(11, 40), FlightState.Active),
                new FlightSegment(At(13, 0), At(13, 30), FlightState.Stuck),
                new FlightSegment(At(14, 0), At(14, 30), FlightState.Active),
                new FlightSegment(At(14, 30), At(15, 0), FlightState.Idle),
            ],
            [new ApiErrorMark(At(13, 0), 529), new ApiErrorMark(At(13, 15), 429)]),
            new("s3", "docs-site", "", At(16, 0), At(17, 30), TimeSpan.FromMinutes(45), TimeSpan.Zero, TimeSpan.FromMinutes(45),
            [
                new FlightSegment(At(16, 0), At(16, 45), FlightState.Active),
                new FlightSegment(At(16, 45), At(17, 30), FlightState.Idle),
            ],
            []),
        };
        return new FlightPathReport(day, At(9, 0), At(18, 0), lanes);
    }

    // Seven daemon workers mirroring the real roster shapes — a dispatched task (named, with a project),
    // a pre-warmed spare, and enough more to push past the strip's five-row cap so the "show +N more"
    // overflow line renders.
    private static IReadOnlyList<DaemonWorker> SampleDaemonWorkers()
    {
        var now = DateTime.Now;
        var workers = new List<DaemonWorker>
        {
            new("f7d0b5fc", "f7d0b5fc-e679-492f-9fee-18a29f41602a", 61112, @"C:\src\hypertree", "hypertree",
                "slash", "Implement streamlined PowerShell install pathway", now.AddMinutes(-12)),
            new("b98bfb1f", "b98bfb1f-c540-4dcf-9997-d12deb9e341d", 62548, @"C:\src\hypertree", "hypertree",
                "spare", null, now.AddMinutes(-11)),
        };
        for (int i = 0; i < 5; i++)
            workers.Add(new($"c0ffee0{i}", $"c0ffee0{i}-0000-4000-8000-00000000000{i}", 70000 + i,
                @"C:\src\api", "api", "slash", $"Sweep flaky test batch {i + 1}", now.AddMinutes(-10 + i)));
        return workers;
    }

}
