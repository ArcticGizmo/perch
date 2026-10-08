using System.Buffers.Binary;
using Perch.Data;

namespace Perch.Feeds;

/// <summary>
/// Vets a downloaded feed icon before it's stored or decoded (docs/feeds-plan.md §3.5): it must be a raster format
/// Perch knows — PNG, JPEG, GIF, WebP, BMP via <see cref="ImageHeader"/>, plus ICO (the favicon format, which
/// ImageHeader doesn't cover) — and no larger than <see cref="MaxDimension"/> on either side, including every
/// image inside an ICO. SVG and anything unrecognised are refused (SVG is a document, not an image). Pure; never
/// throws.
/// </summary>
internal static class FeedIcon
{
    public const int MaxDimension = 1024;
    private const int MaxIcoEntries = 64;

    /// <summary>The file extension to store it under, or null when the bytes are refused.</summary>
    public static string? Validate(byte[] bytes)
    {
        if (bytes.Length < 6) return null;
        if (IsIco(bytes)) return IcoOk(bytes) ? ".ico" : null;

        var size = ImageHeader.TryReadSize(new MemoryStream(bytes, writable: false));
        if (size is not { } s || s.Width > MaxDimension || s.Height > MaxDimension) return null;
        return bytes[0] switch
        {
            0x89 => ".png",
            0xFF => ".jpg",
            (byte)'G' => ".gif",
            (byte)'B' => ".bmp",
            (byte)'R' => ".webp",
            _ => null,
        };
    }

    // ICONDIR: reserved 0, type 1 (icon), count.
    private static bool IsIco(byte[] b) => b[0] == 0 && b[1] == 0 && b[2] == 1 && b[3] == 0;

    private static bool IcoOk(byte[] b)
    {
        int count = BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(4));
        if (count is 0 or > MaxIcoEntries || b.Length < 6 + 16 * count) return false;
        for (int i = 0; i < count; i++)
        {
            var e = b.AsSpan(6 + 16 * i, 16);
            uint size = BinaryPrimitives.ReadUInt32LittleEndian(e[8..]);
            uint offset = BinaryPrimitives.ReadUInt32LittleEndian(e[12..]);
            if (size == 0 || offset >= b.Length || size > b.Length - offset) return false;

            // An entry is either PNG data (which carries its own, possibly much larger, size) or a BMP DIB whose
            // header holds width and doubled height. Check what the decoder will actually see.
            var data = b.AsSpan((int)offset, (int)size);
            if (data.Length >= 8 && data[0] == 0x89 && data[1] == (byte)'P')
            {
                var png = ImageHeader.TryReadSize(new MemoryStream(data.ToArray(), writable: false));
                if (png is not { } p || p.Width > MaxDimension || p.Height > MaxDimension) return false;
            }
            else
            {
                if (data.Length < 12) return false;
                int w = BinaryPrimitives.ReadInt32LittleEndian(data[4..]);
                int h = Math.Abs(BinaryPrimitives.ReadInt32LittleEndian(data[8..])) / 2;
                if (w <= 0 || h <= 0 || w > MaxDimension || h > MaxDimension) return false;
            }
        }
        return true;
    }
}
