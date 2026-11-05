using System.Buffers.Binary;
using Xunit;

namespace Kazo.Os.Apps.KazoPaint.Tests;

/// <summary>Header sniffing and size limits of the formats Kazo Paint opens (ADR 0103 §1).</summary>
public sealed class ImageFileFormatTests
{
    internal static byte[] Png(int width, int height)
    {
        byte[] data = new byte[33];
        new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }.CopyTo(data, 0);
        BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(8), 13);
        "IHDR"u8.CopyTo(data.AsSpan(12));
        BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(16), (uint)width);
        BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(20), (uint)height);
        return data;
    }

    private static byte[] Gif(int width, int height)
    {
        byte[] data = new byte[13];
        "GIF89a"u8.CopyTo(data);
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(6), (ushort)width);
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(8), (ushort)height);
        return data;
    }

    private static byte[] Bmp(int width, int height)
    {
        byte[] data = new byte[54];
        data[0] = (byte)'B';
        data[1] = (byte)'M';
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(14), 40);
        BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(18), width);
        BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(22), height);
        return data;
    }

    private static byte[] WebPLossless(int width, int height)
    {
        byte[] data = new byte[30];
        "RIFF"u8.CopyTo(data);
        "WEBP"u8.CopyTo(data.AsSpan(8));
        "VP8L"u8.CopyTo(data.AsSpan(12));
        data[20] = 0x2F;
        uint bits = (uint)(width - 1) | ((uint)(height - 1) << 14);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(21), bits);
        return data;
    }

    private static byte[] Jpeg(int width, int height)
    {
        // SOI, an APP0 segment to skip, then SOF0 (length 17): precision, height, width.
        List<byte> data = [0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x04, 0x00, 0x00, 0xFF, 0xC0, 0x00, 0x11, 0x08];
        data.AddRange([(byte)(height >> 8), (byte)height, (byte)(width >> 8), (byte)width]);
        data.AddRange(new byte[12]);
        return [.. data];
    }

    public static TheoryData<string, byte[], ImageFileKind> Samples => new()
    {
        { "png", Png(640, 480), ImageFileKind.Png },
        { "gif", Gif(640, 480), ImageFileKind.Gif },
        { "bmp", Bmp(640, -480), ImageFileKind.Bmp },
        { "webp", WebPLossless(640, 480), ImageFileKind.WebP },
        { "jpeg", Jpeg(640, 480), ImageFileKind.Jpeg },
    };

    [Theory]
    [MemberData(nameof(Samples))]
    public void TryReadHeader_reads_kind_and_size(string name, byte[] data, ImageFileKind kind)
    {
        Assert.True(ImageFileFormat.TryReadHeader(data, out ImageFileHeader header), name);
        Assert.Equal(new ImageFileHeader(kind, 640, 480), header);
    }

    [Fact]
    public void TryReadHeader_refuses_text_and_truncated_files()
    {
        Assert.False(ImageFileFormat.TryReadHeader("hello world, not an image"u8, out _));
        Assert.False(ImageFileFormat.TryReadHeader(Png(10, 10).AsSpan(0, 12), out _));
        Assert.False(ImageFileFormat.TryReadHeader(Png(0, 10), out _));
    }

    [Theory]
    [InlineData("photo.JPG", ImageFileKind.Jpeg)]
    [InlineData("/home/alice/a.jpeg", ImageFileKind.Jpeg)]
    [InlineData("drawing.png", ImageFileKind.Png)]
    [InlineData("old.bmp", ImageFileKind.Png)]
    public void SaveKindFor_writes_jpeg_only_for_jpeg_names(string name, ImageFileKind expected) =>
        Assert.Equal(expected, ImageFileFormat.SaveKindFor(name));

    [Fact]
    public void Inspect_refuses_oversized_images_with_a_message()
    {
        PaintFileReadResult tooWide = PaintFileStore.Inspect(Png(ImageFileFormat.MaxDimension + 1, 10));
        PaintFileReadResult garbage = PaintFileStore.Inspect([1, 2, 3]);
        PaintFileReadResult ok = PaintFileStore.Inspect(Png(100, 50));

        Assert.Null(tooWide.Bytes);
        Assert.Contains("4096", tooWide.Error);
        Assert.NotNull(garbage.Error);
        Assert.Null(ok.Error);
        Assert.Equal("image/png", ok.Header!.MediaType);
    }
}
