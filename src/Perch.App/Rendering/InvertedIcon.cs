using System.Runtime.InteropServices;
using Avalonia.Media.Imaging;
using Avalonia.Platform;

namespace Perch.Avalonia.Rendering;

/// <summary>
/// A colour-inverted copy of a bitmap asset, transparency kept. The dev build's tray icon uses it so an isolated
/// dev instance can be told apart from the installed Perch at a glance in the notification area.
/// </summary>
internal static class InvertedIcon
{
    /// <summary>Decodes <paramref name="asset"/> at <paramref name="width"/> px and inverts each pixel's colour.
    /// Throws on a pixel layout other than 32-bit RGBA/BGRA, so callers fall back to the plain icon.</summary>
    public static WriteableBitmap Load(Uri asset, int width)
    {
        using var stream = AssetLoader.Open(asset);
        var bmp = WriteableBitmap.DecodeToWidth(stream, width);
        using var fb = bmp.Lock();
        if (fb.Format != PixelFormat.Bgra8888 && fb.Format != PixelFormat.Rgba8888)
            throw new NotSupportedException($"Unexpected pixel format {fb.Format}");

        // Premultiplied colour channels can't exceed alpha, so they invert against it rather than 255; otherwise a
        // translucent edge pixel would come out brighter than its own opacity allows.
        bool premul = fb.AlphaFormat == AlphaFormat.Premul;
        var row = new byte[fb.Size.Width * 4];
        for (int y = 0; y < fb.Size.Height; y++)
        {
            var at = fb.Address + y * fb.RowBytes;
            Marshal.Copy(at, row, 0, row.Length);
            for (int i = 0; i < row.Length; i += 4)
            {
                byte max = premul ? row[i + 3] : (byte)255;
                row[i] = (byte)(max - row[i]);
                row[i + 1] = (byte)(max - row[i + 1]);
                row[i + 2] = (byte)(max - row[i + 2]);
            }
            Marshal.Copy(row, 0, at, row.Length);
        }
        return bmp;
    }
}
