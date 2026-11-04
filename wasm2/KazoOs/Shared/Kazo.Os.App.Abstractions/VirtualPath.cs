using System.Text;

namespace Kazo.Os.App.Abstractions;

/// <summary>
/// Represents a canonical absolute path in the Kazo OS virtual filesystem.
/// </summary>
public readonly record struct VirtualPath
{
    /// <summary>Maximum encoded length of one path segment.</summary>
    public const int MaximumSegmentUtf8ByteLength = 255;

    /// <summary>Maximum encoded length of a canonical absolute path.</summary>
    public const int MaximumPathUtf8ByteLength = 4096;

    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    private VirtualPath(string value)
    {
        Value = value;
    }

    /// <summary>Gets the canonical absolute path.</summary>
    public string Value { get; }

    /// <summary>Parses and normalizes an absolute Linux-style virtual path.</summary>
    /// <param name="value">Path to parse.</param>
    /// <returns>The canonical virtual path.</returns>
    /// <exception cref="FormatException">The path is relative, malformed, or traverses above root.</exception>
    public static VirtualPath Parse(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);

        if (!value.StartsWith("/", StringComparison.Ordinal))
        {
            throw new FormatException("Virtual paths must be absolute and start with '/'.");
        }

        if (value.Contains('\\') || value.Contains('\0'))
        {
            throw new FormatException("Virtual paths cannot contain backslashes or null characters.");
        }

        List<string> segments = [];
        foreach (string segment in value.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (segment == ".")
            {
                continue;
            }

            if (segment == "..")
            {
                if (segments.Count == 0)
                {
                    throw new FormatException("Virtual paths cannot traverse above root.");
                }

                segments.RemoveAt(segments.Count - 1);
                continue;
            }

            string normalized;
            int segmentByteLength;
            try
            {
                normalized = segment.Normalize(NormalizationForm.FormC);
                segmentByteLength = StrictUtf8.GetByteCount(normalized);
            }
            catch (ArgumentException exception)
            {
                throw new FormatException("Virtual path segments must contain valid Unicode.", exception);
            }

            if (normalized.Any(char.IsControl))
            {
                throw new FormatException("Virtual path segments cannot contain control characters.");
            }

            if (segmentByteLength > MaximumSegmentUtf8ByteLength)
            {
                throw new FormatException(
                    $"Virtual path segments cannot exceed {MaximumSegmentUtf8ByteLength} UTF-8 bytes.");
            }

            segments.Add(normalized);
        }

        string canonical = segments.Count == 0 ? "/" : $"/{string.Join('/', segments)}";
        if (StrictUtf8.GetByteCount(canonical) > MaximumPathUtf8ByteLength)
        {
            throw new FormatException(
                $"Virtual paths cannot exceed {MaximumPathUtf8ByteLength} UTF-8 bytes.");
        }

        return new VirtualPath(canonical);
    }

    /// <summary>Returns the canonical absolute path.</summary>
    /// <returns>The path string.</returns>
    public override string ToString() => Value;
}