using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Media;
using Avalonia.Threading;
using Perch.Avalonia.ViewModels;
using Perch.Avalonia.Views;
using Perch.Data;

namespace Perch.Avalonia.Rendering;

/// <summary>
/// Renders Perch's Avalonia views to PNG on a headless Skia platform, so the UI can be eyeballed
/// without a display (and diffed across changes). Uses synthetic data — never touches the real
/// <c>~/.claude</c> — so it's deterministic and safe to run anywhere. The standing verification harness
/// for the UI-port phases. Views are hosted in a headless window so the FluentTheme templates apply
/// (a bare Measure/Arrange of a templated control renders nothing).
/// </summary>
internal static class HeadlessRenderer
{
    public static int RenderAll(string outDir)
    {
        Directory.CreateDirectory(outDir);

        // Real Skia rendering on a headless platform (UseHeadlessDrawing = false) so the captured frame
        // has true pixels. Configure<App>() loads App.axaml (FluentTheme + palette resources).
        AppBuilder.Configure<App>()
            .UseSkia()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
            .WithInterFont()
            .SetupWithoutStarting();

        var vm = new OverlayViewModel();
        vm.Update(SampleSessions());

        RenderView(new OverlayView { DataContext = vm }, Path.Combine(outDir, "overlay.png"));

        Console.WriteLine($"Rendered overlay PNG to {Path.GetFullPath(outDir)}");
        return 0;
    }

    private static void RenderView(Control view, string path)
    {
        var window = new Window
        {
            SizeToContent = SizeToContent.WidthAndHeight,
            Background = Brushes.Transparent,
            Content = view,
        };
        window.Show();
        Dispatcher.UIThread.RunJobs(); // flush layout + render

        var frame = window.CaptureRenderedFrame();
        using var fs = File.Create(path);
        frame!.Save(fs);
        window.Close();
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
