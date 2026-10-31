using System.Buffers.Binary;
using System.Globalization;
using System.Text;

namespace HackerOs.Theming.Abstractions;

/// <summary>One line of the <c>[cursors]</c> section: a cursor name, its PNG file and its hot spot.</summary>
/// <param name="Name">Cursor name of <see cref="CursorNames.All"/>.</param>
/// <param name="FileName">PNG file name in the theme's folder.</param>
/// <param name="HotspotX">Hot spot column.</param>
/// <param name="HotspotY">Hot spot row.</param>
/// <param name="Line">1-based line, for diagnostics.</param>
public sealed record CursorThemeEntry(string Name, string FileName, int HotspotX, int HotspotY, int Line);

/// <summary>A parsed <c>cursor.theme</c> file, before its images are read.</summary>
/// <param name="Id">Theme id.</param>
/// <param name="Name">Display name.</param>
/// <param name="Entries">Accepted cursor lines.</param>
public sealed record CursorThemeIndex(string Id, string Name, IReadOnlyList<CursorThemeEntry> Entries);

/// <summary>Result of <see cref="CursorThemeFile.Parse"/>.</summary>
/// <param name="Index">The index, or <see langword="null"/> when an error refused the theme.</param>
/// <param name="Diagnostics">Every warning and error, in line order.</param>
public sealed record CursorThemeParseResult(CursorThemeIndex? Index, IReadOnlyList<ThemeFileDiagnostic> Diagnostics);

/// <summary>
/// Reads the <c>cursor.theme</c> index of a cursor theme folder (ADR 0205 §1), in the line-based
/// format of the settings and <c>.theme</c> files:
/// <code>
/// [cursor-theme]
/// id = my-cursors
/// name = My cursors
///
/// [cursors]
/// default = arrow.png 2 1
/// pointer = hand.png 11 1
/// </code>
/// and checks its PNG images (signature, size, hot spot) without decoding them.
/// </summary>
/// <remarks>
/// Reading is tolerant like the other settings files (ADR 0044 §5): an unknown cursor name, a bad
/// line or a refused image is ignored with a warning; only a missing or invalid identity refuses
/// the theme. Images are data only: PNG, no remote address, no SVG (ADR 0190 §3).
/// </remarks>
public static class CursorThemeFile
{
    /// <summary>Name of the index file in a theme folder (like <c>index.theme</c> of freedesktop).</summary>
    public const string IndexFileName = "cursor.theme";

    /// <summary>Largest index file, in bytes.</summary>
    public const int MaxIndexBytes = 16 * 1024;

    /// <summary>Largest cursor image file, in bytes (64 KiB).</summary>
    public const int MaxImageBytes = 64 * 1024;

    /// <summary>Largest cursor width and height, in pixels: browsers ignore larger cursor images.</summary>
    public const int MaxImageSize = 128;

    private const string ThemeSection = "cursor-theme";
    private const string CursorsSection = "cursors";

    private static ReadOnlySpan<byte> PngSignature => [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    /// <summary>Parses an index file.</summary>
    /// <param name="content">Whole content.</param>
    /// <param name="path">File path, for diagnostics.</param>
    public static CursorThemeParseResult Parse(string content, string? path = null)
    {
        ArgumentNullException.ThrowIfNull(content);
        List<ThemeFileDiagnostic> diagnostics = [];
        void Warn(int? line, string key, params object?[] arguments) =>
            diagnostics.Add(ThemeFileDiagnostic.Create(line, new ThemeText(key, arguments), isError: false, path));
        CursorThemeParseResult Fail(string key, params object?[] arguments)
        {
            diagnostics.Add(ThemeFileDiagnostic.Create(null, new ThemeText(key, arguments), isError: true, path));
            return new CursorThemeParseResult(null, diagnostics);
        }

        if (Encoding.UTF8.GetByteCount(content) > MaxIndexBytes)
        {
            return Fail("File.TooLarge", MaxIndexBytes / 1024);
        }

        string? id = null;
        string? name = null;
        List<CursorThemeEntry> entries = [];
        string? section = null;
        string[] lines = content.ReplaceLineEndings("\n").Split('\n');
        for (int index = 0; index < lines.Length; index++)
        {
            int lineNumber = index + 1;
            string line = lines[index].Trim();
            if (line.Length == 0 || line[0] is '#' or ';')
            {
                continue;
            }

            if (line[0] == '[')
            {
                if (line[^1] != ']')
                {
                    Warn(lineNumber, "File.MalformedSection");
                    section = null;
                    continue;
                }

                section = line[1..^1].Trim().ToLowerInvariant();
                if (section is not (ThemeSection or CursorsSection))
                {
                    Warn(lineNumber, "File.UnknownSection", section);
                }

                continue;
            }

            int equals = line.IndexOf('=', StringComparison.Ordinal);
            if (equals <= 0)
            {
                Warn(lineNumber, "File.ExpectedKeyValue");
                continue;
            }

            string key = line[..equals].Trim();
            string value = line[(equals + 1)..].Trim();
            if (section == ThemeSection)
            {
                switch (key)
                {
                    case "id":
                        id = value;
                        break;
                    case "name":
                        name = value;
                        break;
                    default:
                        Warn(lineNumber, "Cursor.UnknownKey", key);
                        break;
                }
            }
            else if (section == CursorsSection)
            {
                if (ParseEntry(key, value, lineNumber, Warn) is { } entry)
                {
                    entries.RemoveAll(existing => existing.Name == entry.Name);
                    entries.Add(entry);
                }
            }
            else if (section is null)
            {
                Warn(lineNumber, "File.KeyOutsideSection");
            }
        }

        if (!ThemeFile.IsValidId(id))
        {
            return id is null ? Fail("Cursor.MissingId") : Fail("File.InvalidId", id);
        }

        if (string.IsNullOrWhiteSpace(name) || name.Length > ThemeFile.MaxNameLength)
        {
            return Fail("Cursor.InvalidName", ThemeFile.MaxNameLength);
        }

        return new CursorThemeParseResult(new CursorThemeIndex(id!, name, entries), diagnostics);
    }

    /// <summary>
    /// Checks a cursor image without decoding it: a PNG (signature and <c>IHDR</c> header), at most
    /// <see cref="MaxImageBytes"/> bytes and <see cref="MaxImageSize"/> pixels each way, with the hot
    /// spot inside the image.
    /// </summary>
    /// <param name="bytes">Whole file.</param>
    /// <param name="hotspotX">Hot spot column.</param>
    /// <param name="hotspotY">Hot spot row.</param>
    /// <param name="reasonKey">Resource key of the reason when refused.</param>
    /// <returns><see langword="true"/> when the image is accepted.</returns>
    public static bool TryValidateImage(ReadOnlySpan<byte> bytes, int hotspotX, int hotspotY, out string? reasonKey)
    {
        reasonKey = null;
        if (bytes.Length > MaxImageBytes)
        {
            reasonKey = "Cursor.ImageTooLarge";
            return false;
        }

        // Signature, then the first chunk: 4-byte length, "IHDR", 4-byte width, 4-byte height.
        if (bytes.Length < 24 || !bytes[..8].SequenceEqual(PngSignature) || !bytes[12..16].SequenceEqual("IHDR"u8))
        {
            reasonKey = "Cursor.NotPng";
            return false;
        }

        uint width = BinaryPrimitives.ReadUInt32BigEndian(bytes[16..20]);
        uint height = BinaryPrimitives.ReadUInt32BigEndian(bytes[20..24]);
        if (width is 0 or > MaxImageSize || height is 0 or > MaxImageSize)
        {
            reasonKey = "Cursor.ImageTooBig";
            return false;
        }

        if (hotspotX >= width || hotspotY >= height)
        {
            reasonKey = "Cursor.HotspotOutside";
            return false;
        }

        return true;
    }

    /// <summary>Builds the <c>data:</c> URL of an accepted PNG image.</summary>
    /// <param name="bytes">Whole PNG file.</param>
    public static string ToDataUrl(ReadOnlySpan<byte> bytes) => "data:image/png;base64," + Convert.ToBase64String(bytes);

    private static CursorThemeEntry? ParseEntry(string name, string value, int line, Action<int?, string, object?[]> warn)
    {
        if (!CursorNames.IsKnown(name))
        {
            warn(line, "Cursor.UnknownName", [name]);
            return null;
        }

        string[] parts = value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 3
            || !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out int x)
            || !int.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out int y))
        {
            warn(line, "Cursor.MalformedEntry", [name]);
            return null;
        }

        string fileName = parts[0];
        if (!IsPlainPngName(fileName))
        {
            warn(line, "Cursor.BadFileName", [fileName]);
            return null;
        }

        return new CursorThemeEntry(name, fileName, x, y, line);
    }

    // A file of the theme's own folder only: no path, no "..", no remote address.
    private static bool IsPlainPngName(string fileName) =>
        fileName.EndsWith(".png", StringComparison.OrdinalIgnoreCase)
        && fileName.Length <= 128
        && fileName[0] != '.'
        && fileName.All(static character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or '.');
}
