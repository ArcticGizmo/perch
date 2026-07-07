using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;

namespace Perch.Data;

/// <summary>
/// Minimal 8-bit RGBA PNG encoder for a top-down 32bpp BGRA buffer (Avalonia's <c>Bgra8888</c> layout, as
/// produced by <c>RenderTargetBitmap.CopyPixels</c>). Hand-rolled — no <c>System.Drawing</c> — so it stays
/// in the UI-free core and works on every head; it exists because the macOS clipboard seam
/// (<c>Perch.Platform.Mac.ImageClipboard</c>) needs PNG bytes to hand to <c>NSPasteboard</c>, and encoding
/// pixels is platform-neutral logic worth unit-testing on its own.
///
/// Emits a single uncompressed-filter (filter type 0 "None") image whose scanlines are zlib-compressed via
/// <see cref="ZLibStream"/> — exactly the RFC-1950 stream PNG's IDAT expects. Alpha is preserved (the source
/// poster is opaque, but carrying A costs nothing and keeps the encoder general).
/// </summary>
public static class PngEncoder
{
    private static readonly byte[] Signature = [137, 80, 78, 71, 13, 10, 26, 10];

    /// <summary>
    /// Encodes a <paramref name="width"/>×<paramref name="height"/> image from a top-down 32bpp BGRA
    /// buffer with row <paramref name="stride"/> (bytes per row) to PNG bytes. Throws
    /// <see cref="ArgumentException"/> on inconsistent dimensions.
    /// </summary>
    public static byte[] FromBgra(byte[] bgra, int width, int height, int stride)
    {
        ArgumentNullException.ThrowIfNull(bgra);
        if (width <= 0 || height <= 0 || stride < width * 4 || (long)stride * height > bgra.Length)
            throw new ArgumentException("BGRA buffer smaller than width/height/stride imply.");

        // Raw PNG image data: each scanline prefixed by its filter-type byte (0 = None), pixels as RGBA.
        int rowBytes = width * 4;
        var raw = new byte[(rowBytes + 1) * height];
        int p = 0;
        for (int y = 0; y < height; y++)
        {
            raw[p++] = 0; // filter: None
            int srcOff = y * stride;
            for (int x = 0; x < width; x++)
            {
                int s = srcOff + x * 4;
                raw[p++] = bgra[s + 2]; // R
                raw[p++] = bgra[s + 1]; // G
                raw[p++] = bgra[s + 0]; // B
                raw[p++] = bgra[s + 3]; // A
            }
        }

        byte[] idat;
        using (var ms = new MemoryStream())
        {
            using (var z = new ZLibStream(ms, CompressionLevel.Optimal, leaveOpen: true))
                z.Write(raw, 0, raw.Length);
            idat = ms.ToArray();
        }

        using var outMs = new MemoryStream();
        outMs.Write(Signature);
        WriteChunk(outMs, "IHDR", Ihdr(width, height));
        WriteChunk(outMs, "IDAT", idat);
        WriteChunk(outMs, "IEND", []);
        return outMs.ToArray();
    }

    private static byte[] Ihdr(int width, int height)
    {
        var d = new byte[13];
        BinaryPrimitives.WriteInt32BigEndian(d.AsSpan(0), width);
        BinaryPrimitives.WriteInt32BigEndian(d.AsSpan(4), height);
        d[8] = 8;  // bit depth
        d[9] = 6;  // colour type: truecolour with alpha (RGBA)
        d[10] = 0; // compression: deflate
        d[11] = 0; // filter method: adaptive
        d[12] = 0; // interlace: none
        return d;
    }

    private static void WriteChunk(Stream s, string type, byte[] data)
    {
        var typeBytes = Encoding.ASCII.GetBytes(type);
        Span<byte> u32 = stackalloc byte[4];

        BinaryPrimitives.WriteInt32BigEndian(u32, data.Length);
        s.Write(u32);
        s.Write(typeBytes);
        s.Write(data);

        uint crc = Crc32(0xFFFFFFFF, typeBytes);
        crc = Crc32(crc, data) ^ 0xFFFFFFFF;
        BinaryPrimitives.WriteUInt32BigEndian(u32, crc);
        s.Write(u32);
    }

    // Standard PNG CRC-32 (polynomial 0xEDB88320), computed incrementally over type then data. Table-less:
    // an image is copied once on an explicit user action, so the per-bit loop's cost is irrelevant.
    private static uint Crc32(uint crc, byte[] buf)
    {
        foreach (byte b in buf)
        {
            crc ^= b;
            for (int k = 0; k < 8; k++)
                crc = (crc & 1) != 0 ? 0xEDB88320 ^ (crc >> 1) : crc >> 1;
        }
        return crc;
    }
}
