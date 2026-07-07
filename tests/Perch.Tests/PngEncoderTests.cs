using System.Buffers.Binary;
using System.IO.Compression;
using Perch.Data;
using Xunit;

namespace Perch.Tests;

public class PngEncoderTests
{
    [Fact]
    public void FromBgra_ProducesWellFormedPng_WithRgbaPixels()
    {
        // 2×2 image, top-down BGRA. Distinct channel values so a B/R swap or row flip would show.
        const int w = 2, h = 2, stride = w * 4;
        var bgra = new byte[]
        {
            // row 0: px(10,20,30,255)      px(11,21,31,254)      (bytes are B,G,R,A)
            10, 20, 30, 255,   11, 21, 31, 254,
            // row 1: px(40,50,60,253)      px(41,51,61,252)
            40, 50, 60, 253,   41, 51, 61, 252,
        };

        byte[] png = PngEncoder.FromBgra(bgra, w, h, stride);

        // Signature.
        Assert.Equal(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }, png[..8]);

        // Walk chunks: verify order (IHDR, IDAT, IEND), CRCs, and IHDR fields; collect IDAT payload.
        var chunks = ReadChunks(png);
        Assert.Equal("IHDR", chunks[0].Type);
        Assert.Equal("IDAT", chunks[1].Type);
        Assert.Equal("IEND", chunks[^1].Type);

        var ihdr = chunks[0].Data;
        Assert.Equal(w, BinaryPrimitives.ReadInt32BigEndian(ihdr.AsSpan(0)));
        Assert.Equal(h, BinaryPrimitives.ReadInt32BigEndian(ihdr.AsSpan(4)));
        Assert.Equal(8, ihdr[8]);  // bit depth
        Assert.Equal(6, ihdr[9]);  // colour type: RGBA

        // Decompress IDAT and check the raw scanlines: filter byte 0 then RGBA (B/R swapped from source).
        byte[] raw = ZlibInflate(chunks[1].Data);
        byte[] expected =
        {
            0,  30, 20, 10, 255,   31, 21, 11, 254,   // row 0: filter=0, then R,G,B,A per pixel
            0,  60, 50, 40, 253,   61, 51, 41, 252,   // row 1
        };
        Assert.Equal(expected, raw);
    }

    [Fact]
    public void FromBgra_RejectsInconsistentDimensions()
    {
        Assert.Throws<ArgumentException>(() => PngEncoder.FromBgra(new byte[8], 2, 2, 8)); // needs 16 bytes
        Assert.Throws<ArgumentException>(() => PngEncoder.FromBgra(new byte[16], 2, 2, 4)); // stride < w*4
    }

    private static byte[] ZlibInflate(byte[] data)
    {
        using var input = new MemoryStream(data);
        using var z = new ZLibStream(input, CompressionMode.Decompress);
        using var outMs = new MemoryStream();
        z.CopyTo(outMs);
        return outMs.ToArray();
    }

    private static List<(string Type, byte[] Data)> ReadChunks(byte[] png)
    {
        var result = new List<(string, byte[])>();
        int i = 8; // skip signature
        while (i + 12 <= png.Length)
        {
            int len = BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(i));
            string type = System.Text.Encoding.ASCII.GetString(png, i + 4, 4);
            var data = png[(i + 8)..(i + 8 + len)];

            // Verify the stored CRC matches a recompute over type+data (proves a well-formed chunk).
            uint stored = BinaryPrimitives.ReadUInt32BigEndian(png.AsSpan(i + 8 + len));
            Assert.Equal(Crc32(png.AsSpan(i + 4, 4 + len)), stored);

            result.Add((type, data));
            i += 12 + len;
        }
        return result;
    }

    private static uint Crc32(ReadOnlySpan<byte> buf)
    {
        uint crc = 0xFFFFFFFF;
        foreach (byte b in buf)
        {
            crc ^= b;
            for (int k = 0; k < 8; k++)
                crc = (crc & 1) != 0 ? 0xEDB88320 ^ (crc >> 1) : crc >> 1;
        }
        return crc ^ 0xFFFFFFFF;
    }
}
