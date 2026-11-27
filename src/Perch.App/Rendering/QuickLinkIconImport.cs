using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Perch.Avalonia.Views;
using Perch.Data;

namespace Perch.Avalonia.Rendering;

/// <summary>
/// Turns an image the user picked into a quick-link icon: decoded through <see cref="BoundedBitmap"/> off the UI
/// thread (header first, pixel cap), then fitted into a transparent <see cref="QuickLinkIcons.PixelSize"/> square
/// with its aspect kept and encoded as PNG. Only the PNG bytes come back; the caller decides when to store them,
/// so cancelling the dialog leaves nothing on disk.
/// </summary>
internal static class QuickLinkIconImport
{
    /// <summary>The formats offered in the picker: what <see cref="ImageHeader"/> recognises. SVG (a document,
    /// not an image) and ICO aren't among them.</summary>
    public static readonly string[] Patterns = ["*.png", "*.jpg", "*.jpeg", "*.gif", "*.bmp", "*.webp"];

    // Decoding wider than this buys nothing for a 64px result and costs memory on a huge photo.
    private const int DecodeWidth = 256;

    /// <summary>The icon's PNG bytes, or null when the file isn't an image Perch can read.</summary>
    public static async Task<byte[]?> ImportAsync(string path)
    {
        var source = await BoundedBitmap.LoadAsync(path, DecodeWidth);
        if (source is null) return null;
        try
        {
            return await Dispatcher.UIThread.InvokeAsync(() => Fit(source));
        }
        catch
        {
            return null;
        }
        finally
        {
            source.Dispose();
        }
    }

    /// <summary>Fits a decoded image into the icon square and encodes it. UI thread.</summary>
    internal static byte[] Fit(Bitmap source)
    {
        const int size = QuickLinkIcons.PixelSize;
        var px = source.PixelSize;
        double scale = Math.Min((double)size / px.Width, (double)size / px.Height);
        double w = px.Width * scale, h = px.Height * scale;
        var dest = new Rect((size - w) / 2, (size - h) / 2, w, h);

        using var target = new RenderTargetBitmap(new PixelSize(size, size), new Vector(96, 96));
        using (var ctx = target.CreateDrawingContext())
        using (ctx.PushRenderOptions(new RenderOptions { BitmapInterpolationMode = BitmapInterpolationMode.HighQuality }))
            ctx.DrawImage(source, new Rect(0, 0, source.Size.Width, source.Size.Height), dest);

        using var ms = new MemoryStream();
        target.Save(ms);
        return ms.ToArray();
    }
}
