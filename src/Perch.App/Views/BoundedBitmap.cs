using Avalonia.Media.Imaging;
using Perch.Data;

namespace Perch.Avalonia.Views;

/// <summary>
/// Decodes an attachment image at a bounded cost (review fixes CP24). The header is read first: an image over
/// <see cref="MaxPixels"/> is refused, since its full decode alone would take that many × 4 bytes (a small,
/// highly compressed file can ask for gigabytes). A preview asks for a width, and a larger image decodes scaled
/// down to it. The work is blocking IO and decode, so callers run it off the UI thread (<see cref="LoadAsync"/>);
/// an Avalonia <see cref="Bitmap"/> can be created on any thread.
/// </summary>
internal static class BoundedBitmap
{
    /// <summary>About 256 MB decoded. Room for any phone photo (48 MP) or a big screenshot.</summary>
    public const long MaxPixels = 64_000_000;

    /// <summary>The hover previews show at most 560 DIP, so decoding at twice that keeps them crisp at 200% scaling.</summary>
    public const int PreviewWidth = 1120;

    /// <summary>The image at <paramref name="path"/>, no wider than <paramref name="maxWidth"/> (null = native).
    /// Null when the file can't be read, isn't a recognised image, is over <see cref="MaxPixels"/>, or doesn't
    /// decode. Blocking: call off the UI thread.</summary>
    public static Bitmap? Load(string path, int? maxWidth = null)
    {
        try
        {
            if (ImageHeader.TryReadSize(path) is not var (w, h)) return null;
            if ((long)w * h > MaxPixels) return null;
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            return maxWidth is { } mw && mw < w ? Bitmap.DecodeToWidth(fs, mw) : new Bitmap(fs);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>As <see cref="Load(string, int?)"/>, for bytes already in memory (a downloaded feed image), under a
    /// caller-chosen pixel cap. Blocking: call off the UI thread.</summary>
    public static Bitmap? Load(byte[] bytes, int? maxWidth, long maxPixels = MaxPixels)
    {
        try
        {
            if (ImageHeader.TryReadSize(new MemoryStream(bytes, writable: false)) is not var (w, h)) return null;
            if ((long)w * h > maxPixels) return null;
            using var ms = new MemoryStream(bytes, writable: false);
            return maxWidth is { } mw && mw < w ? Bitmap.DecodeToWidth(ms, mw) : new Bitmap(ms);
        }
        catch
        {
            return null;
        }
    }

    /// <summary><see cref="Load"/> on a pool thread.</summary>
    public static Task<Bitmap?> LoadAsync(string path, int? maxWidth = null) => Task.Run(() => Load(path, maxWidth));
}
