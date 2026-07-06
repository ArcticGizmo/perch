using System.Runtime.InteropServices;
using Perch.Platform;

namespace Perch.Platform.Windows;

/// <summary>
/// Windows <see cref="IImageClipboard"/>: places a bitmap on the clipboard as <c>CF_DIB</c> — the
/// device-independent-bitmap format every Windows app (Paint, Word, Slack, browsers) accepts on paste.
/// We build the DIB by hand (BITMAPINFOHEADER + bottom-up 24bpp BGR rows) rather than pulling in
/// WinForms/WPF, matching this project's "Win32 without the desktop frameworks" approach.
///
/// The poster is fully opaque, so dropping the alpha channel to 24bpp is lossless here and sidesteps the
/// long-standing inconsistency in how consumers interpret alpha in a 32bpp CF_DIB.
/// </summary>
public sealed class ImageClipboard : IImageClipboard
{
    private const uint CF_DIB = 8;
    private const uint GMEM_MOVEABLE = 0x0002;
    private const int BitmapInfoHeaderSize = 40;

    public bool TryCopyBgra(byte[] bgra, int width, int height, int stride)
    {
        if (bgra is null || width <= 0 || height <= 0 || stride < width * 4) return false;

        // DIB rows are padded to a 4-byte boundary; the DIB is stored bottom-up (positive biHeight).
        int dstStride = ((width * 3) + 3) & ~3;
        int imageSize = dstStride * height;
        int total = BitmapInfoHeaderSize + imageSize;

        IntPtr hMem = GlobalAlloc(GMEM_MOVEABLE, (UIntPtr)total);
        if (hMem == IntPtr.Zero) return false;

        try
        {
            IntPtr ptr = GlobalLock(hMem);
            if (ptr == IntPtr.Zero) { GlobalFree(hMem); return false; }
            try
            {
                Marshal.WriteInt32(ptr, 0, BitmapInfoHeaderSize);  // biSize
                Marshal.WriteInt32(ptr, 4, width);                 // biWidth
                Marshal.WriteInt32(ptr, 8, height);                // biHeight (+ve => bottom-up)
                Marshal.WriteInt16(ptr, 12, 1);                    // biPlanes
                Marshal.WriteInt16(ptr, 14, 24);                   // biBitCount
                Marshal.WriteInt32(ptr, 16, 0);                    // biCompression = BI_RGB
                Marshal.WriteInt32(ptr, 20, imageSize);            // biSizeImage
                Marshal.WriteInt32(ptr, 24, 2835);                 // biXPelsPerMeter (~72 dpi)
                Marshal.WriteInt32(ptr, 28, 2835);                 // biYPelsPerMeter
                Marshal.WriteInt32(ptr, 32, 0);                    // biClrUsed
                Marshal.WriteInt32(ptr, 36, 0);                    // biClrImportant

                IntPtr bits = ptr + BitmapInfoHeaderSize;
                var row = new byte[dstStride];
                for (int y = 0; y < height; y++)
                {
                    int srcOff = y * stride;                 // source is top-down
                    int dstY = height - 1 - y;               // destination is bottom-up
                    for (int x = 0; x < width; x++)
                    {
                        int s = srcOff + x * 4;
                        int d = x * 3;
                        row[d + 0] = bgra[s + 0];            // B
                        row[d + 1] = bgra[s + 1];            // G
                        row[d + 2] = bgra[s + 2];            // R
                    }
                    Marshal.Copy(row, 0, bits + dstY * dstStride, dstStride);
                }
            }
            finally { GlobalUnlock(hMem); }

            if (!OpenClipboard(IntPtr.Zero)) { GlobalFree(hMem); return false; }
            try
            {
                EmptyClipboard();
                if (SetClipboardData(CF_DIB, hMem) == IntPtr.Zero) { GlobalFree(hMem); return false; }
                // Ownership of hMem transfers to the clipboard on success — must not free it.
                return true;
            }
            finally { CloseClipboard(); }
        }
        catch { GlobalFree(hMem); return false; }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GlobalAlloc(uint uFlags, UIntPtr dwBytes);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GlobalFree(IntPtr hMem);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GlobalLock(IntPtr hMem);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GlobalUnlock(IntPtr hMem);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool OpenClipboard(IntPtr hWndNewOwner);
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool CloseClipboard();
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool EmptyClipboard();
    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetClipboardData(uint uFormat, IntPtr hMem);
}
