using System.Text.RegularExpressions;

namespace HackerOs.App.Abstractions;

/// <summary>
/// Identifies a UI platform (e.g. desktop, mobile) an app entry point can target. Deliberately not
/// a closed enum — the platform catalog is an open, extensible set (see
/// doc/wasm/dev/old/mobile-interface-platform-plan.md §3); app and shell code should query platform
/// capabilities through a contract rather than switch on this value.
/// </summary>
public readonly partial record struct AppPlatformId
{
    /// <summary>Maximum length of a platform identifier.</summary>
    public const int MaximumLength = 32;

    private AppPlatformId(string value)
    {
        Value = value;
    }

    /// <summary>Gets the normalized platform identifier.</summary>
    public string Value { get; }

    /// <summary>Parses a platform identifier.</summary>
    /// <param name="value">Identifier text.</param>
    /// <returns>The parsed platform identifier.</returns>
    /// <exception cref="FormatException">The value is not a valid platform identifier.</exception>
    public static AppPlatformId Parse(string value)
    {
        if (!TryParse(value, out AppPlatformId platformId))
        {
            throw new FormatException($"'{value}' is not a valid platform identifier.");
        }

        return platformId;
    }

    /// <summary>Attempts to parse a platform identifier.</summary>
    /// <param name="value">Identifier text.</param>
    /// <param name="platformId">Parsed identifier when successful.</param>
    /// <returns><see langword="true"/> when parsing succeeds.</returns>
    public static bool TryParse(string? value, out AppPlatformId platformId)
    {
        platformId = default;
        if (string.IsNullOrEmpty(value) || value.Length > MaximumLength)
        {
            return false;
        }

        if (!PlatformIdPattern().IsMatch(value))
        {
            return false;
        }

        platformId = new AppPlatformId(value);
        return true;
    }

    /// <summary>Returns the normalized platform identifier.</summary>
    /// <returns>The platform identifier text.</returns>
    public override string ToString() => Value;

    // Lowercase ASCII segments separated by single hyphens; no leading/trailing/doubled hyphens.
    [GeneratedRegex("^[a-z0-9]+(-[a-z0-9]+)*$", RegexOptions.CultureInvariant)]
    private static partial Regex PlatformIdPattern();
}

/// <summary>
/// Well-known platform identifiers registered by the platform itself. Not an exhaustive list —
/// future platforms (e.g. a third-party form factor) register their own <see cref="AppPlatformId"/>
/// values without requiring a change here.
/// </summary>
public static class WellKnownAppPlatforms
{
    /// <summary>The desktop platform: floating windows, a taskbar, pointer/keyboard-first input.</summary>
    public static AppPlatformId Desktop { get; } = AppPlatformId.Parse("desktop");

    /// <summary>The mobile platform: a single full-screen surface, system nav bar, touch-first input.</summary>
    public static AppPlatformId Mobile { get; } = AppPlatformId.Parse("mobile");
}
