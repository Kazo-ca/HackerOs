using System.Globalization;

namespace Kazo.Os.Simulation.Abstractions.FileSystem;

/// <summary>
/// Identifies one filesystem entry independently from its current path.
/// </summary>
public readonly record struct FileSystemEntryId
{
    private FileSystemEntryId(Guid value)
    {
        Value = value;
    }

    /// <summary>Gets the opaque 128-bit identifier value.</summary>
    public Guid Value { get; }

    /// <summary>Gets whether this value is the invalid empty identifier.</summary>
    public bool IsEmpty => Value == Guid.Empty;

    /// <summary>Creates an entry identifier from a non-empty 128-bit value.</summary>
    /// <param name="value">Identifier value supplied by a trusted ID generator.</param>
    /// <returns>The immutable entry identifier.</returns>
    /// <exception cref="ArgumentException">The value is empty.</exception>
    public static FileSystemEntryId FromGuid(Guid value)
    {
        if (value == Guid.Empty)
        {
            throw new ArgumentException("Filesystem entry IDs cannot be empty.", nameof(value));
        }

        return new FileSystemEntryId(value);
    }

    /// <summary>Parses a canonical 32-digit entry identifier.</summary>
    /// <param name="value">Canonical identifier text.</param>
    /// <returns>The parsed entry identifier.</returns>
    /// <exception cref="FormatException">The value is not canonical or is empty.</exception>
    public static FileSystemEntryId Parse(string value)
    {
        if (!TryParse(value, out FileSystemEntryId entryId))
        {
            throw new FormatException($"'{value}' is not a canonical filesystem entry ID.");
        }

        return entryId;
    }

    /// <summary>Attempts to parse a canonical 32-digit entry identifier.</summary>
    /// <param name="value">Canonical identifier text.</param>
    /// <param name="entryId">Parsed identifier when successful.</param>
    /// <returns><see langword="true"/> when the value is canonical and non-empty.</returns>
    public static bool TryParse(string? value, out FileSystemEntryId entryId)
    {
        entryId = default;
        if (!Guid.TryParseExact(value, "N", out Guid parsed) || parsed == Guid.Empty)
        {
            return false;
        }

        entryId = new FileSystemEntryId(parsed);
        return true;
    }

    /// <summary>Returns the canonical lowercase 32-digit identifier.</summary>
    /// <returns>The canonical identifier text.</returns>
    public override string ToString() => Value.ToString("N", CultureInfo.InvariantCulture);
}