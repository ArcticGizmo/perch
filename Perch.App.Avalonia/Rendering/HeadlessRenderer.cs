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
        return
        [
            new ClaudeSession("1234", "s1", SessionStatus.Running, @"C:\src\perch", "perch", now,
                Activity: "Editing OverlayForm.cs"),
            new ClaudeSession("5678", "s2", SessionStatus.AwaitingInput, @"C:\src\api", "api", now),
            new ClaudeSession("9012", "s3", SessionStatus.NeedsAttention, @"C:\src\docs", "docs-site", now),
            new ClaudeSession("3456", "s4", SessionStatus.Idle, @"C:\src\scratch", "scratch", now),
        ];
    }
}
