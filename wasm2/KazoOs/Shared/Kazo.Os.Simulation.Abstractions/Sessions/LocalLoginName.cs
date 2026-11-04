using System.Text.RegularExpressions;

namespace Kazo.Os.Simulation.Abstractions.Sessions;

/// <summary>
/// Represents one normalized, unique, case-insensitive ASCII local login name, such as
/// <c>alice</c>. The login name also names the user's home directory segment.
/// </summary>
public readonly partial record struct LocalLoginName
{
    /// <summary>Maximum length of one login name.</summary>
    public const int MaximumLength = 32;

    private LocalLoginName(string value) => Value = value;

    /// <summary>Gets the normalized lowercase login name.</summary>
    public string Value { get; }

    /// <summary>Parses and normalizes one login name.</summary>
    /// <param name="value">Untrusted login name.</param>
    /// <returns>The normalized login name.</returns>
    /// <exception cref="FormatException">The name is empty, too long, or not valid ASCII.</exception>
    public static LocalLoginName Parse(string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            throw new FormatException("Login names cannot be empty.");
        }

        string normalized = value.ToLowerInvariant();

        if (normalized.Length > MaximumLength)
        {
            throw new FormatException($"Login names cannot exceed {MaximumLength} characters.");
        }

        if (!ValidPattern().IsMatch(normalized))
        {
            throw new FormatException(
                "Login names must start with a lowercase ASCII letter and contain only lowercase " +
                "ASCII letters, digits, hyphens, and underscores.");
        }

        return new LocalLoginName(normalized);
    }

    /// <summary>Returns the normalized login name.</summary>
    public override string ToString() => Value;

    [GeneratedRegex("^[a-z][a-z0-9_-]*$")]
    private static partial Regex ValidPattern();
}
