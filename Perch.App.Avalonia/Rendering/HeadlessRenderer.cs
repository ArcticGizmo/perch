using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Media.Imaging;
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

        RenderControl(canvas, Path.Combine(outDir, "overlay_1x.png"), 96);
        RenderControl(canvas, Path.Combine(outDir, "overlay_1.5x.png"), 144);
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
