using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using Svg;

// Generates every raster icon asset from the single source-of-truth SVG (perch.svg).
//
//   src/Perch.App.Avalonia/Assets/icon.png   256x256 PNG — window icons and the in-app logo
//   src/Perch.App.Avalonia/Assets/icon.ico   multi-resolution ICO (16..256) — tray icon + the .exe ApplicationIcon
//   landing-icon.png                         512x512 PNG — the README header logo
//
// Re-run after editing perch.svg:  dotnet run --project tools/IconGen
// (or tools/gen-icons.ps1, which also restores packages.)

// Resolve the repo root from this tool's location so the script works regardless of CWD.
// tools/IconGen/bin/<cfg>/<tfm>/  ->  up four levels lands on tools/IconGen, then up two more = repo root.
string repoRoot = FindRepoRoot(AppContext.BaseDirectory);
string svgPath = Path.Combine(repoRoot, "perch.svg");
if (!File.Exists(svgPath))
{
    Console.Error.WriteLine($"Source SVG not found: {svgPath}");
    return 1;
}

Console.WriteLine($"Source: {svgPath}");
var doc = SvgDocument.Open(svgPath);

// The artwork doesn't fill its 96x96 viewBox — there's transparent margin (more on top than
// bottom). To make the icon use as much of its pixel canvas as possible (it matters most in the
// tiny tray), we crop to the actual drawn content, re-center it in a square, and scale that to
// fill the frame. PAD keeps a sliver of breathing room so anti-aliased edges don't clip.
const float PAD = 0.01f;
var fit = ComputeFit(doc, PAD);
float vbSide = doc.ViewBox.Width > 0 ? doc.ViewBox.Width : fit.Side;
Console.WriteLine($"Fit: content {fit.Box.Width:0.0}x{fit.Box.Height:0.0} cropped from {vbSide:0}x{vbSide:0} viewBox ({vbSide / fit.Side:0.00}x larger)");

// The .ico ships a true frame at each of these sizes so Windows never has to downscale at runtime
// (the tray asks for a 16px frame; large surfaces ask for 256).
int[] icoSizes = { 16, 24, 32, 48, 64, 128, 256 };

string assetsDir = Path.Combine(repoRoot, "src", "Perch.App.Avalonia", "Assets");
WritePng(doc, fit, Path.Combine(assetsDir, "icon.png"), 256);
WritePng(doc, fit, Path.Combine(repoRoot, "landing-icon.png"), 512);
WriteIco(doc, fit, Path.Combine(assetsDir, "icon.ico"), icoSizes);

Console.WriteLine("Done.");
return 0;

// Renders the cropped, re-centered content square at the given pixel size with high-quality
// anti-aliasing and a transparent background.
static Bitmap Render(SvgDocument doc, Fit fit, int size)
{
    var bmp = new Bitmap(size, size, PixelFormat.Format32bppArgb);
    bmp.SetResolution(96, 96);
    using var g = Graphics.FromImage(bmp);
    g.SmoothingMode = SmoothingMode.AntiAlias;
    g.InterpolationMode = InterpolationMode.HighQualityBicubic;
    g.PixelOffsetMode = PixelOffsetMode.HighQuality;
    g.Clear(Color.Transparent);
    // Map the padded content square (origin .. origin+side) onto the pixel canvas: shift its
    // top-left to 0,0 then scale it up to fill. Prepend order means the translate is applied first.
    float scale = size / fit.Side;
    g.ScaleTransform(scale, scale);
    g.TranslateTransform(-fit.OriginX, -fit.OriginY);
    doc.Draw(g);
    return bmp;
}

static void WritePng(SvgDocument doc, Fit fit, string path, int size)
{
    using var bmp = Render(doc, fit, size);
    bmp.Save(path, ImageFormat.Png);
    Console.WriteLine($"  {Path.GetFileName(path)}  {size}x{size}");
}

// Writes a Vista+ ICO whose frames are PNG-compressed (keeps the file small and supports 256px).
static void WriteIco(SvgDocument doc, Fit fit, string path, int[] sizes)
{
    var frames = new List<byte[]>();
    foreach (var size in sizes)
    {
        using var bmp = Render(doc, fit, size);
        using var ms = new MemoryStream();
        bmp.Save(ms, ImageFormat.Png);
        frames.Add(ms.ToArray());
    }

    using var fs = File.Create(path);
    using var w = new BinaryWriter(fs);

    // ICONDIR header
    w.Write((ushort)0);             // reserved
    w.Write((ushort)1);             // type = icon
    w.Write((ushort)sizes.Length);  // image count

    // Each ICONDIRENTRY is 16 bytes; image data follows the full directory.
    int offset = 6 + sizes.Length * 16;
    for (int i = 0; i < sizes.Length; i++)
    {
        int size = sizes[i];
        w.Write((byte)(size >= 256 ? 0 : size)); // width  (0 = 256)
        w.Write((byte)(size >= 256 ? 0 : size)); // height (0 = 256)
        w.Write((byte)0);                        // palette count
        w.Write((byte)0);                        // reserved
        w.Write((ushort)1);                      // colour planes
        w.Write((ushort)32);                     // bits per pixel
        w.Write(frames[i].Length);               // bytes of image data
        w.Write(offset);                         // offset of image data
        offset += frames[i].Length;
    }

    foreach (var frame in frames)
        w.Write(frame);

    Console.WriteLine($"  {Path.GetFileName(path)}  [{string.Join(", ", sizes)}]");
}

// Walks up from the tool's binary location to the directory that holds perch.svg.
static string FindRepoRoot(string start)
{
    var dir = new DirectoryInfo(start);
    while (dir != null)
    {
        if (File.Exists(Path.Combine(dir.FullName, "perch.svg")))
            return dir.FullName;
        dir = dir.Parent;
    }
    // Fall back to four-up from bin/<cfg>/<tfm> if the marker walk fails.
    return Path.GetFullPath(Path.Combine(start, "..", "..", "..", "..", ".."));
}

// Computes the square crop that tightly frames the SVG's drawn content (so the raster uses the
// whole canvas instead of inheriting the viewBox's transparent margin). The square is the larger
// content dimension, centered on the content, grown by `pad` on every side.
static Fit ComputeFit(SvgDocument doc, float pad)
{
    var box = doc.Bounds;
    if (box.Width <= 0 || box.Height <= 0)
    {
        // No measurable content — fall back to the viewBox (or a unit square) so we never divide by zero.
        float w = doc.ViewBox.Width > 0 ? doc.ViewBox.Width : 1f;
        float h = doc.ViewBox.Height > 0 ? doc.ViewBox.Height : 1f;
        box = new RectangleF(0, 0, w, h);
    }
    float side = Math.Max(box.Width, box.Height);
    side += side * pad * 2f;
    float cx = box.X + box.Width / 2f;
    float cy = box.Y + box.Height / 2f;
    return new Fit(box, side, cx - side / 2f, cy - side / 2f);
}

// The crop geometry: the content's own bounds, the side of the padded square that frames it, and
// that square's top-left corner in SVG user units.
readonly record struct Fit(RectangleF Box, float Side, float OriginX, float OriginY);
