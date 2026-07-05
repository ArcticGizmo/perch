using Avalonia;
using Avalonia.Headless;
using Avalonia.Media.Imaging;

namespace AvaloniaSpike;

class Program
{
    // `AvaloniaSpike render <outDir>` renders the custom-drawn StatCard to PNG at 1x and 1.5x DPI so
    // the visuals can be inspected without a display. Any other invocation launches the tray app.
    [STAThread]
    public static int Main(string[] args)
    {
        if (args.Length > 0 && args[0] == "render")
        {
            var outDir = args.Length > 1 ? args[1] : ".";
            return RenderCards(outDir);
        }

        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        return 0;
    }

    // Avalonia configuration, don't remove; also used by visual designer.
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
#if DEBUG
            .WithDeveloperTools()
#endif
            .WithInterFont()
            .LogToTrace();

    private static int RenderCards(string outDir)
    {
        Directory.CreateDirectory(outDir);

        // Real Skia rendering (UseHeadlessDrawing = false) on a headless platform, so RenderTargetBitmap
        // produces true pixels rather than the headless stub.
        AppBuilder.Configure<App>()
            .UseSkia()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
            .WithInterFont()
            .SetupWithoutStarting();

        RenderOne(Path.Combine(outDir, "statcard_1x.png"), 96);
        RenderOne(Path.Combine(outDir, "statcard_1.5x.png"), 144);
        Console.WriteLine($"Rendered StatCard PNGs to {Path.GetFullPath(outDir)}");
        return 0;
    }

    private static void RenderOne(string path, double dpi)
    {
        var logical = new Size(232, 150);
        var card = new StatCard { Width = logical.Width, Height = logical.Height };
        card.Measure(logical);
        card.Arrange(new Rect(logical));

        double scale = dpi / 96.0;
        var pixelSize = new PixelSize((int)Math.Round(logical.Width * scale), (int)Math.Round(logical.Height * scale));
        using var rtb = new RenderTargetBitmap(pixelSize, new Vector(dpi, dpi));
        rtb.Render(card);
        using var fs = File.Create(path);
        rtb.Save(fs);
    }
}
