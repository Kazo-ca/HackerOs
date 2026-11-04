using System.Globalization;
using System.Text.RegularExpressions;

namespace Kazo.Os.App.Abstractions;

/// <summary>
/// Represents a Semantic Version 2.0.0 value used by app and SDK compatibility checks.
/// </summary>
public readonly partial record struct SemanticVersion : IComparable<SemanticVersion>
{
    private SemanticVersion(
        int major,
        int minor,
        int patch,
        string? preRelease,
        string? buildMetadata)
    {
        Major = major;
        Minor = minor;
        Patch = patch;
        PreRelease = preRelease;
        BuildMetadata = buildMetadata;
    }

    /// <summary>Gets the major version.</summary>
    public int Major { get; }

    /// <summary>Gets the minor version.</summary>
    public int Minor { get; }

    /// <summary>Gets the patch version.</summary>
    public int Patch { get; }

    /// <summary>Gets prerelease identifiers, or <see langword="null"/> for a release.</summary>
    public string? PreRelease { get; }

    /// <summary>Gets build metadata, which does not affect precedence.</summary>
    public string? BuildMetadata { get; }

    /// <summary>Parses a Semantic Version 2.0.0 string.</summary>
    /// <param name="value">Version text.</param>
    /// <returns>The parsed semantic version.</returns>
    /// <exception cref="FormatException">The value is not a valid semantic version.</exception>
    public static SemanticVersion Parse(string value)
    {
        if (!TryParse(value, out SemanticVersion version))
        {
            throw new FormatException($"'{value}' is not a valid Semantic Version 2.0.0 value.");
        }

        return version;
    }

    /// <summary>Attempts to parse a Semantic Version 2.0.0 string.</summary>
    /// <param name="value">Version text.</param>
    /// <param name="version">Parsed version when successful.</param>
    /// <returns><see langword="true"/> when parsing succeeds.</returns>
    public static bool TryParse(string? value, out SemanticVersion version)
    {
        version = default;
        Match match = SemanticVersionPattern().Match(value ?? string.Empty);
        if (!match.Success)
        {
            return false;
        }

        string? preRelease = match.Groups[4].Success ? match.Groups[4].Value : null;
        if (preRelease is not null
            && preRelease.Split('.').Any(IsInvalidNumericPreReleaseIdentifier))
        {
            return false;
        }

        if (!int.TryParse(match.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out int major)
            || !int.TryParse(match.Groups[2].Value, NumberStyles.None, CultureInfo.InvariantCulture, out int minor)
            || !int.TryParse(match.Groups[3].Value, NumberStyles.None, CultureInfo.InvariantCulture, out int patch))
        {
            return false;
        }

        version = new SemanticVersion(
            major,
            minor,
            patch,
            preRelease,
            match.Groups[5].Success ? match.Groups[5].Value : null);
        return true;
    }

    /// <inheritdoc />
    public int CompareTo(SemanticVersion other)
    {
        int coreComparison = Major.CompareTo(other.Major);
        if (coreComparison == 0)
        {
            coreComparison = Minor.CompareTo(other.Minor);
        }

        if (coreComparison == 0)
        {
            coreComparison = Patch.CompareTo(other.Patch);
        }

        if (coreComparison != 0)
        {
            return coreComparison;
        }

        return ComparePreRelease(PreRelease, other.PreRelease);
    }

    /// <summary>Returns canonical semantic version text.</summary>
    /// <returns>The canonical semantic version.</returns>
    public override string ToString()
    {
        string value = $"{Major}.{Minor}.{Patch}";
        if (PreRelease is not null)
        {
            value += $"-{PreRelease}";
        }

        if (BuildMetadata is not null)
        {
            value += $"+{BuildMetadata}";
        }

        return value;
    }

    private static int ComparePreRelease(string? left, string? right)
    {
        if (left is null)
        {
            return right is null ? 0 : 1;
        }

        if (right is null)
        {
            return -1;
        }

        string[] leftIdentifiers = left.Split('.');
        string[] rightIdentifiers = right.Split('.');
        int sharedLength = Math.Min(leftIdentifiers.Length, rightIdentifiers.Length);

        for (int index = 0; index < sharedLength; index++)
        {
            int comparison = ComparePreReleaseIdentifier(leftIdentifiers[index], rightIdentifiers[index]);
            if (comparison != 0)
            {
                return comparison;
            }
        }

        return leftIdentifiers.Length.CompareTo(rightIdentifiers.Length);
    }

    private static int ComparePreReleaseIdentifier(string left, string right)
    {
        bool leftIsNumeric = left.All(char.IsDigit);
        bool rightIsNumeric = right.All(char.IsDigit);

        if (leftIsNumeric && rightIsNumeric)
        {
            int lengthComparison = left.Length.CompareTo(right.Length);
            return lengthComparison != 0
                ? lengthComparison
                : string.Compare(left, right, StringComparison.Ordinal);
        }

        if (leftIsNumeric != rightIsNumeric)
        {
            return leftIsNumeric ? -1 : 1;
        }

        return string.Compare(left, right, StringComparison.Ordinal);
    }

    private static bool IsInvalidNumericPreReleaseIdentifier(string identifier) =>
        identifier.Length > 1
        && identifier[0] == '0'
        && identifier.All(char.IsDigit);

    [GeneratedRegex(
        "^(0|[1-9][0-9]*)\\.(0|[1-9][0-9]*)\\.(0|[1-9][0-9]*)(?:-([0-9A-Za-z-]+(?:\\.[0-9A-Za-z-]+)*))?(?:\\+([0-9A-Za-z-]+(?:\\.[0-9A-Za-z-]+)*))?$",
        RegexOptions.CultureInvariant)]
    private static partial Regex SemanticVersionPattern();
}