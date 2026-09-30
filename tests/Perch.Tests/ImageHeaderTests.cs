using System.Buffers.Binary;
using Perch.Data;
using Xunit;

namespace Perch.Tests;

/// <summary>
/// Review fixes CP24: attachment images are size-checked from their header before any decode, so a small but
/// huge-dimensioned file can't make the UI decode gigabytes. These are hand-built minimal headers for each format.
/// </summary>
public sealed class ImageHeaderTests
{
    private static (int, int)? Size(byte[] bytes) => ImageHeader.TryReadSize(new MemoryStream(bytes));

    private static byte[] Png(int w, int h)
    {
        var b = new byte[33];
        new byte[] { 0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A }.CopyTo(b, 0);
        BinaryPrimitives.WriteInt32BigEndian(b.AsSpan(8), 13);
        "IHDR"u8.CopyTo(b.AsSpan(12));
        BinaryPrimitives.WriteInt32BigEndian(b.AsSpan(16), w);
        BinaryPrimitives.WriteInt32BigEndian(b.AsSpan(20), h);
        return b;
    }

    [Fact]
    public void Png_ihdr() => Assert.Equal((1920, 1080), Size(Png(1920, 1080)));

    [Fact]
    public void Gif()
    {
        var b = new byte[13];
        "GIF89a"u8.CopyTo(b);
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(6), 320);
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(8), 200);
        Assert.Equal((320, 200), Size(b));
    }

    [Fact]
    public void Bmp_including_a_top_down_negative_height()
    {
        var b = new byte[54];
        b[0] = (byte)'B'; b[1] = (byte)'M';
        BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(18), 640);
        BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(22), -480);
        Assert.Equal((640, 480), Size(b));
    }

    [Fact]
    public void Jpeg_finds_the_frame_header_behind_a_large_metadata_segment()
    {
        var ms = new MemoryStream();
        ms.Write([0xFF, 0xD8]);                                   // SOI
        var app1 = new byte[40_000];                              // an EXIF-sized APP1, well past the 64-byte prefix
        ms.Write([0xFF, 0xE1, (byte)((app1.Length + 2) >> 8), (byte)(app1.Length + 2)]);
        ms.Write(app1);
        ms.Write([0xFF, 0xC4, 0x00, 0x04, 0x00, 0x00]);           // a DHT (C4) is not a frame header
        ms.Write([0xFF, 0xC2, 0x00, 0x11, 0x08]);                 // SOF2 (progressive), precision 8
        ms.Write([0x0F, 0xA0, 0x0B, 0xB8]);                       // height 4000, width 3000
        ms.Write(new byte[12]);
        Assert.Equal((3000, 4000), Size(ms.ToArray()));
    }

    [Fact]
    public void Jpeg_with_no_frame_header_before_the_scan_is_unknown()
    {
        byte[] b = [0xFF, 0xD8, 0xFF, 0xDA, 0x00, 0x02, 0x00, 0x00];
        Assert.Null(Size(b));
    }

    private static byte[] Riff(string chunk, byte[] data)
    {
        var b = new byte[20 + data.Length];
        "RIFF"u8.CopyTo(b);
        "WEBP"u8.CopyTo(b.AsSpan(8));
        System.Text.Encoding.ASCII.GetBytes(chunk).CopyTo(b, 12);
        data.CopyTo(b, 20);
        return b;
    }

    [Fact]
    public void WebP_lossy_lossless_and_extended()
    {
        var vp8 = new byte[10];
        vp8[3] = 0x9D; vp8[4] = 0x01; vp8[5] = 0x2A;
        BinaryPrimitives.WriteUInt16LittleEndian(vp8.AsSpan(6), 800);
        BinaryPrimitives.WriteUInt16LittleEndian(vp8.AsSpan(8), 600);
        Assert.Equal((800, 600), Size(Riff("VP8 ", vp8)));

        var vp8l = new byte[10];
        vp8l[0] = 0x2F;
        BinaryPrimitives.WriteUInt32LittleEndian(vp8l.AsSpan(1), (uint)(1023 | (767 << 14)));   // width-1, height-1
        Assert.Equal((1024, 768), Size(Riff("VP8L", vp8l)));

        var vp8x = new byte[10];
        int w = 5000 - 1, h = 7000 - 1;
        vp8x[4] = (byte)w; vp8x[5] = (byte)(w >> 8); vp8x[6] = (byte)(w >> 16);
        vp8x[7] = (byte)h; vp8x[8] = (byte)(h >> 8); vp8x[9] = (byte)(h >> 16);
        Assert.Equal((5000, 7000), Size(Riff("VP8X", vp8x)));
    }

    [Fact]
    public void Unknown_truncated_or_zero_sized_is_null()
    {
        Assert.Null(Size("hello world, not an image at all"u8.ToArray()));
        Assert.Null(Size([]));
        Assert.Null(Size(Png(1920, 1080)[..20]));   // cut before the height
        Assert.Null(Size(Png(0, 1080)));
    }

    [Fact]
    public void Reads_from_a_path_and_a_missing_path_is_null()
    {
        var path = Path.Combine(Path.GetTempPath(), "perch-hdr-" + Guid.NewGuid().ToString("N") + ".png");
        try
        {
            File.WriteAllBytes(path, Png(12, 34));
            Assert.Equal((12, 34), ImageHeader.TryReadSize(path));
        }
        finally { File.Delete(path); }
        Assert.Null(ImageHeader.TryReadSize(path));
    }
}
