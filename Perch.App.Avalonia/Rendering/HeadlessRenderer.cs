using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
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

        var probe = new OverlayCanvas();
        probe.Update(SampleSessions());
        probe.StartAutoCloseCountdown(20_000);
        RenderControl(probe, Path.Combine(outDir, "overlay_autoclose_1x.png"), 96);

        Console.WriteLine($"Rendered overlay PNGs to {Path.GetFullPath(outDir)}");
        return 0;
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

    private static (IReadOnlyList<QuickLink> links, IReadOnlyList<string?> icons) SampleQuickLinks(string outDir)
    {
        // Write the bundled brand icon to a PNG file so the icon-drawing path (decode + 180° flip) is
        // exercised; the other two links carry no icon, so they draw initials.
        string brandPng = Path.Combine(outDir, "sample_quicklink.png");
        try
        {
            using var s = AssetLoader.Open(new Uri("avares://perch-avalonia/Assets/icon.png"));
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
            new ClaudeSession("3456", "s4", SessionStatus.Idle, @"C:\src\scratch", "scratch", now),
            // A background/SDK session (Entrypoint != "cli") -> grouped under the Autonomous section.
            new ClaudeSession("7788", "s5", SessionStatus.Running, @"C:\src\bot", "nightly-bot", now,
                Entrypoint: "sdk-py"),
        ];
    }
}
