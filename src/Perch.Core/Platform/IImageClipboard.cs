namespace Perch.Platform;

/// <summary>
/// Puts a rendered raster image onto the system clipboard so it can be pasted into other apps (chat,
/// docs, image editors). This is the OS-specific seam behind "Copy image" on the Perch Wrapped card:
/// Avalonia's cross-platform clipboard only carries text/data objects, and pasting a bitmap into other
/// apps needs the platform's native image format (CF_DIB on Windows, NSPasteboard on macOS).
///
/// Pixels cross the boundary as top-down 32bpp BGRA (Avalonia's <c>Bgra8888</c> layout) — the caller
/// hands over the bytes it already has from a <c>RenderTargetBitmap</c>, keeping every platform bitmap
/// type behind the seam. Best-effort; never throws.
/// </summary>
public interface IImageClipboard
{
    /// <summary>
    /// Copies a <paramref name="width"/>×<paramref name="height"/> image to the clipboard from a
    /// top-down 32bpp BGRA buffer with the given row <paramref name="stride"/> (bytes per row). Returns
    /// true if the image was placed on the clipboard. Never throws.
    /// </summary>
    bool TryCopyBgra(byte[] bgra, int width, int height, int stride);
}
