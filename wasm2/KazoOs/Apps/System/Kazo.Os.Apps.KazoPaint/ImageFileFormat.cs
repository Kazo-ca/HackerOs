using System.Buffers.Binary;

namespace Kazo.Os.Apps.KazoPaint;

/// <summary>Image container formats Hack Paint reads (ADR 0103 §1).</summary>
public enum ImageFileKind
{
    /// <summary>Not a supported image.</summary>
    Unknown,

    /// <summary>Portable Network Graphics.</summary>
    Png,

    /// <summary>JPEG (JFIF/EXIF).</summary>
    Jpeg,

    /// <summary>Windows bitmap.</summary>
    Bmp,

    /// <summary>GIF; only the first frame is read.</summary>
    Gif,

    /// <summary>WebP (lossy, lossless or extended).</summary>
    WebP
}

/// <summary>What the header of an image file says: its format and pixel size.</summary>
/// <param name="Kind">Container format.</param>
/// <param name="Width">Width in pixels.</param>
/// <param name="Height">Height in pixels.</param>
public sealed record ImageFileHeader(ImageFileKind Kind, int Width, int Height)
{
    /// <summary>Gets the media type of the format.</summary>
    public string MediaType => ImageFileFormat.MediaTypeOf(Kind);
}

/// <summary>
/// Reads the format and size of an image from its first bytes, the way <c>file(1)</c> and image
/// libraries do, so Hack Paint can refuse an oversized image <em>before</em> handing it to the
/// browser's decoder (ADR 0103 §1: at most 4 096 × 4 096 pixels and 16 MiB per file).
/// </summary>
public static class ImageFileFormat
{
    /// <summary>Largest width or height Hack Paint edits, in pixels.</summary>
    public const int MaxDimension = 4096;

    /// <summary>Largest image file Hack Paint opens, in bytes (16 MiB, the OS file size limit of ADR 0094).</summary>
    public const int MaxFileBytes = 16 * 1024 * 1024;

    /// <summary>Extensions Hack Paint opens, for dialogs and file associations.</summary>
    public static IReadOnlyList<string> OpenExtensions { get; } = [".png", ".jpg", ".jpeg", ".bmp", ".gif", ".webp"];

    /// <summary>Gets the media type of a format.</summary>
    /// <param name="kind">Format.</param>
    public static string MediaTypeOf(ImageFileKind kind) => kind switch
    {
        ImageFileKind.Png => "image/png",
        ImageFileKind.Jpeg => "image/jpeg",
        ImageFileKind.Bmp => "image/bmp",
        ImageFileKind.Gif => "image/gif",
        ImageFileKind.WebP => "image/webp",
        _ => "application/octet-stream"
    };

    /// <summary>Gets the format Hack Paint writes for a file name: JPEG for <c>.jpg</c>/<c>.jpeg</c>, PNG otherwise.</summary>
    /// <param name="fileName">Target file name or path.</param>
    public static ImageFileKind SaveKindFor(string fileName) =>
        fileName.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase) || fileName.EndsWith(".jpeg", StringComparison.OrdinalIgnoreCase)
            ? ImageFileKind.Jpeg
            : ImageFileKind.Png;

    /// <summary>Reads the header of an image file.</summary>
    /// <param name="data">File content (only the first bytes are needed, except for some JPEGs).</param>
    /// <param name="header">Format and size when recognized.</param>
    /// <returns><see langword="true"/> when the format is supported and the size could be read.</returns>
    public static bool TryReadHeader(ReadOnlySpan<byte> data, out ImageFileHeader header)
    {
        header = new ImageFileHeader(ImageFileKind.Unknown, 0, 0);
        (ImageFileKind kind, int width, int height)? read =
            TryPng(data) ?? TryGif(data) ?? TryBmp(data) ?? TryWebP(data) ?? TryJpeg(data);
        if (read is not { } value || value.width <= 0 || value.height <= 0)
        {
            return false;
        }

        header = new ImageFileHeader(value.kind, value.width, value.height);
        return true;
    }

    // PNG: 8-byte signature, then the IHDR chunk whose data starts with big-endian width and height.
    private static (ImageFileKind, int, int)? TryPng(ReadOnlySpan<byte> data)
    {
        ReadOnlySpan<byte> signature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];
        if (data.Length < 24 || !data[..8].SequenceEqual(signature) || !data.Slice(12, 4).SequenceEqual("IHDR"u8))
        {
            return null;
        }

        return (ImageFileKind.Png, (int)BinaryPrimitives.ReadUInt32BigEndian(data[16..]), (int)BinaryPrimitives.ReadUInt32BigEndian(data[20..]));
    }

    // GIF: "GIF87a"/"GIF89a", then the logical screen size as little-endian 16-bit values.
    private static (ImageFileKind, int, int)? TryGif(ReadOnlySpan<byte> data)
    {
        if (data.Length < 10 || !(data[..6].SequenceEqual("GIF87a"u8) || data[..6].SequenceEqual("GIF89a"u8)))
        {
            return null;
        }

        return (ImageFileKind.Gif, BinaryPrimitives.ReadUInt16LittleEndian(data[6..]), BinaryPrimitives.ReadUInt16LittleEndian(data[8..]));
    }

    // BMP: "BM", then a DIB header at offset 14. A 12-byte BITMAPCOREHEADER stores 16-bit sizes,
    // later headers 32-bit signed ones (a negative height means a top-down bitmap).
    private static (ImageFileKind, int, int)? TryBmp(ReadOnlySpan<byte> data)
    {
        if (data.Length < 26 || data[0] != (byte)'B' || data[1] != (byte)'M')
        {
            return null;
        }

        uint dibSize = BinaryPrimitives.ReadUInt32LittleEndian(data[14..]);
        if (dibSize == 12)
        {
            return (ImageFileKind.Bmp, BinaryPrimitives.ReadUInt16LittleEndian(data[18..]), BinaryPrimitives.ReadUInt16LittleEndian(data[20..]));
        }

        return data.Length < 26 || dibSize < 40
            ? null
            : (ImageFileKind.Bmp, BinaryPrimitives.ReadInt32LittleEndian(data[18..]), Math.Abs(BinaryPrimitives.ReadInt32LittleEndian(data[22..])));
    }

    // WebP: RIFF container "RIFF....WEBP", then a VP8 (lossy), VP8L (lossless) or VP8X (extended) chunk.
    private static (ImageFileKind, int, int)? TryWebP(ReadOnlySpan<byte> data)
    {
        if (data.Length < 30 || !data[..4].SequenceEqual("RIFF"u8) || !data.Slice(8, 4).SequenceEqual("WEBP"u8))
        {
            return null;
        }

        ReadOnlySpan<byte> chunk = data.Slice(12, 4);
        if (chunk.SequenceEqual("VP8 "u8))
        {
            // Frame header: 3-byte tag, start code 9D 01 2A, then 14-bit width and height.
            return (ImageFileKind.WebP, BinaryPrimitives.ReadUInt16LittleEndian(data[26..]) & 0x3FFF, BinaryPrimitives.ReadUInt16LittleEndian(data[28..]) & 0x3FFF);
        }

        if (chunk.SequenceEqual("VP8L"u8))
        {
            // Signature byte 0x2F, then 14-bit (width - 1) and (height - 1) packed in 4 bytes.
            uint bits = BinaryPrimitives.ReadUInt32LittleEndian(data[21..]);
            return (ImageFileKind.WebP, (int)(bits & 0x3FFF) + 1, (int)((bits >> 14) & 0x3FFF) + 1);
        }

        if (chunk.SequenceEqual("VP8X"u8))
        {
            // Canvas size as 24-bit (width - 1) and (height - 1).
            int width = (data[24] | (data[25] << 8) | (data[26] << 16)) + 1;
            int height = (data[27] | (data[28] << 8) | (data[29] << 16)) + 1;
            return (ImageFileKind.WebP, width, height);
        }

        return null;
    }

    // JPEG: SOI marker, then segments; the first start-of-frame marker (SOF0..SOF15 except DHT,
    // JPG and DAC) carries big-endian height then width.
    private static (ImageFileKind, int, int)? TryJpeg(ReadOnlySpan<byte> data)
    {
        if (data.Length < 4 || data[0] != 0xFF || data[1] != 0xD8)
        {
            return null;
        }

        int offset = 2;
        while (offset + 9 < data.Length)
        {
            if (data[offset] != 0xFF)
            {
                return null;
            }

            byte marker = data[offset + 1];
            if (marker == 0xFF)
            {
                // Fill byte.
                offset++;
                continue;
            }

            if (marker is 0xD8 or 0x01 || marker is >= 0xD0 and <= 0xD7)
            {
                // Markers without a length.
                offset += 2;
                continue;
            }

            int length = BinaryPrimitives.ReadUInt16BigEndian(data[(offset + 2)..]);
            if (marker is >= 0xC0 and <= 0xCF and not 0xC4 and not 0xC8 and not 0xCC)
            {
                int height = BinaryPrimitives.ReadUInt16BigEndian(data[(offset + 5)..]);
                int width = BinaryPrimitives.ReadUInt16BigEndian(data[(offset + 7)..]);
                return (ImageFileKind.Jpeg, width, height);
            }

            if (marker == 0xDA || length < 2)
            {
                // Start of scan before any frame header: malformed.
                return null;
            }

            offset += 2 + length;
        }

        return null;
    }
}
