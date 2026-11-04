using System.Text;

namespace Kazo.Os.Simulation.Abstractions.FileSystem;

/// <summary>
/// Represents one normalized name within a virtual directory.
/// </summary>
public readonly record struct FileSystemEntryName
{
    /// <summary>Maximum encoded length of one entry name.</summary>
    public const int MaximumUtf8ByteLength = 255;

    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    private FileSystemEntryName(string value)
    {
        Value = value;
    }

    /// <summary>Gets the Unicode Form C entry name.</summary>
    public string Value { get; }

    /// <summary>Parses and normalizes one directory entry name.</summary>
    /// <param name="value">Untrusted entry name.</param>
    /// <returns>The normalized entry name.</returns>
    /// <exception cref="FormatException">The name is invalid or exceeds the encoded limit.</exception>
    public static FileSystemEntryName Parse(string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            throw new FormatException("Filesystem entry names cannot be empty.");
        }

        string normalized;
        int byteLength;
        try
        {
            normalized = value.Normalize(NormalizationForm.FormC);
            byteLength = StrictUtf8.GetByteCount(normalized);
        }
        catch (ArgumentException exception)
        {
            throw new FormatException("Filesystem entry names must contain valid Unicode.", exception);
        }

        if (normalized is "." or "..")
        {
            throw new FormatException("'.' and '..' are reserved path segments.");
        }

        if (normalized.Contains('/') || normalized.Contains('\\') || normalized.Contains('\0'))
        {
            throw new FormatException("Filesystem entry names cannot contain path separators or null characters.");
        }

        if (normalized.Any(char.IsControl))
        {
            throw new FormatException("Filesystem entry names cannot contain control characters.");
        }

        if (byteLength > MaximumUtf8ByteLength)
        {
            throw new FormatException(
                $"Filesystem entry names cannot exceed {MaximumUtf8ByteLength} UTF-8 bytes.");
        }

        return new FileSystemEntryName(normalized);
    }

    /// <summary>Returns the normalized entry name.</summary>
    /// <returns>The normalized entry name.</returns>
    public override string ToString() => Value;
}