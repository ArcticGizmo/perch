using System.Buffers.Binary;

namespace Perch.Data;

/// <summary>
/// Reads an image's pixel dimensions from its header, without decoding it (review fixes CP24). A decoded image
/// costs width × height × 4 bytes whatever its file size, so a small, highly compressed file can still ask for
/// gigabytes; callers check the size here before handing the file to a decoder. Knows the formats Perch shows:
/// PNG, JPEG, GIF, WebP and BMP. Never throws.
/// </summary>
internal static class ImageHeader
{
    // Enough for every fixed-offset header. JPEG's frame header can sit behind large metadata segments, so JPEG
    // walks its segments from the stream instead.
    private const int PrefixBytes = 64;

    /// <summary>The (width, height) of the image at <paramref name="path"/>, or null when it can't be read or
    /// isn't a recognised format.</summary>
    public static (int Width, int Height)? TryReadSize(string path)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            return TryReadSize(fs);
        }
        catch
        {
            return null;
        }
    }

    /// <inheritdoc cref="TryReadSize(string)"/>
    public static (int Width, int Height)? TryReadSize(Stream stream)
    {
        try
        {
            var head = new byte[PrefixBytes];
            int n = ReadFully(stream, head, 0, head.Length);
            var h = head.AsSpan(0, n);

            // PNG: the signature, then IHDR's big-endian width and height.
            if (n >= 24 && h[..8].SequenceEqual((ReadOnlySpan<byte>)[0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A]))
                return Valid(BinaryPrimitives.ReadInt32BigEndian(h[16..]), BinaryPrimitives.ReadInt32BigEndian(h[20..]));

            // GIF: "GIF87a"/"GIF89a", then the logical screen's little-endian width and height.
            if (n >= 10 && h[0] == 'G' && h[1] == 'I' && h[2] == 'F')
                return Valid(BinaryPrimitives.ReadUInt16LittleEndian(h[6..]), BinaryPrimitives.ReadUInt16LittleEndian(h[8..]));

            // BMP: "BM", then the info header's width and height (a negative height means top-down rows).
            if (n >= 26 && h[0] == 'B' && h[1] == 'M')
                return Valid(BinaryPrimitives.ReadInt32LittleEndian(h[18..]), Math.Abs(BinaryPrimitives.ReadInt32LittleEndian(h[22..])));

            // WebP: RIFF....WEBP, then a VP8 (lossy), VP8L (lossless) or VP8X (extended) chunk.
            if (n >= 30 && h[..4].SequenceEqual("RIFF"u8) && h[8..12].SequenceEqual("WEBP"u8))
                return WebP(h);

            // JPEG: SOI, then segments until a start-of-frame marker.
            if (n >= 2 && h[0] == 0xFF && h[1] == 0xD8)
                return Jpeg(stream, head, n);
        }
        catch { }
        return null;
    }

    private static (int, int)? WebP(ReadOnlySpan<byte> h)
    {
        var chunk = h[12..16];
        if (chunk.SequenceEqual("VP8 "u8))
            // The key frame's start code (9D 01 2A) at 23, then 14-bit width and height.
            return h[23] == 0x9D && h[24] == 0x01 && h[25] == 0x2A
                ? Valid(BinaryPrimitives.ReadUInt16LittleEndian(h[26..]) & 0x3FFF, BinaryPrimitives.ReadUInt16LittleEndian(h[28..]) & 0x3FFF)
                : null;
        if (chunk.SequenceEqual("VP8L"u8))
        {
            // Signature 0x2F, then width-1 and height-1 packed as two 14-bit fields.
            if (h[20] != 0x2F) return null;
            uint bits = BinaryPrimitives.ReadUInt32LittleEndian(h[21..]);
            return Valid((int)(bits & 0x3FFF) + 1, (int)((bits >> 14) & 0x3FFF) + 1);
        }
        if (chunk.SequenceEqual("VP8X"u8))
            // Canvas width-1 and height-1 as 24-bit little-endian values.
            return Valid((h[24] | h[25] << 8 | h[26] << 16) + 1, (h[27] | h[28] << 8 | h[29] << 16) + 1);
        return null;
    }

    private static (int, int)? Jpeg(Stream stream, byte[] head, int n)
    {
        // Walk the segments: the prefix already read first, then the rest of the stream. Each segment is a marker
        // (FF xx) and, for all but the standalone markers, a big-endian length that includes itself.
        var reader = new PrefixedReader(stream, head, n) { Position = 2 };
        for (int guard = 0; guard < 1000; guard++)
        {
            int b = reader.ReadByte();
            if (b < 0) return null;
            if (b != 0xFF) continue;                 // tolerate junk between segments
            int marker;
            do marker = reader.ReadByte(); while (marker == 0xFF);   // fill bytes
            if (marker < 0) return null;
            if (marker is 0xD8 or 0x01 or (>= 0xD0 and <= 0xD7)) continue;   // standalone markers
            if (marker is 0xD9 or 0xDA) return null;  // end of image, or the scan began with no frame header
            int len = reader.ReadUInt16BE();
            if (len < 2) return null;
            // Start of frame: SOF0-SOF15, except DHT (C4), JPG (C8) and DAC (CC).
            if (marker is >= 0xC0 and <= 0xCF and not 0xC4 and not 0xC8 and not 0xCC)
            {
                reader.ReadByte();                   // sample precision
                int height = reader.ReadUInt16BE();
                int width = reader.ReadUInt16BE();
                return Valid(width, height);
            }
            if (!reader.Skip(len - 2)) return null;
        }
        return null;
    }

    private static (int, int)? Valid(int width, int height) => width > 0 && height > 0 ? (width, height) : null;

    private static int ReadFully(Stream s, byte[] buf, int offset, int count)
    {
        int total = 0, n;
        while (total < count && (n = s.Read(buf, offset + total, count - total)) > 0) total += n;
        return total;
    }

    // Reads the already-read prefix, then carries on from the stream.
    private sealed class PrefixedReader(Stream stream, byte[] prefix, int prefixLength)
    {
        public int Position;

        public int ReadByte()
        {
            if (Position < prefixLength) return prefix[Position++];
            Position++;
            return stream.ReadByte();
        }

        public int ReadUInt16BE()
        {
            int hi = ReadByte(), lo = ReadByte();
            return hi < 0 || lo < 0 ? -1 : hi << 8 | lo;
        }

        public bool Skip(int count)
        {
            while (count > 0 && Position < prefixLength) { Position++; count--; }
            if (count == 0) return true;
            if (stream.CanSeek)
            {
                if (stream.Position + count > stream.Length) return false;
                stream.Seek(count, SeekOrigin.Current);
                Position += count;
                return true;
            }
            for (; count > 0; count--)
                if (ReadByte() < 0) return false;
            return true;
        }
    }
}
