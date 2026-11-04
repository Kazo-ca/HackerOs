using System.Collections.ObjectModel;
using System.Diagnostics.CodeAnalysis;

namespace HackerOs.Theming.Abstractions;

/// <summary>Exposes the deterministic, framework-neutral catalog of built-in Kazo OS themes and accents.</summary>
public static class ThemeCatalog
{
    private static readonly ReadOnlyCollection<ThemeDefinition> DesktopThemeItems = Array.AsReadOnly<ThemeDefinition>(
    [
        new(WellKnownThemeIds.HackerOs, "Kazo OS", ThemePlatform.Desktop, supportsAccentColor: true),
        new(WellKnownThemeIds.Windows98, "Windows 98", ThemePlatform.Desktop),
        new(WellKnownThemeIds.WindowsXp, "Windows XP", ThemePlatform.Desktop),
        new(WellKnownThemeIds.Windows7, "Windows 7", ThemePlatform.Desktop),
        new(WellKnownThemeIds.Windows10, "Windows 10", ThemePlatform.Desktop),
        new(WellKnownThemeIds.MacOs, "macOS", ThemePlatform.Desktop),
        new(WellKnownThemeIds.Ubuntu, "Ubuntu", ThemePlatform.Desktop)
    ]);

    private static readonly ReadOnlyCollection<ThemeDefinition> MobileThemeItems = Array.AsReadOnly<ThemeDefinition>(
    [
        new(WellKnownThemeIds.Android, "Android", ThemePlatform.Mobile),
        new(WellKnownThemeIds.Ios, "iOS", ThemePlatform.Mobile)
    ]);

    private static readonly ReadOnlyCollection<ThemeDefinition> AllThemeItems = Array.AsReadOnly(
        DesktopThemeItems.Concat(MobileThemeItems).ToArray());

    private static readonly ReadOnlyCollection<string> AccentItems = Array.AsReadOnly<string>(
    [
        WellKnownAccentIds.Green,
        WellKnownAccentIds.Cyan,
        WellKnownAccentIds.Purple
    ]);

    private static readonly IReadOnlyDictionary<string, ThemeDefinition> ThemesById =
        new ReadOnlyDictionary<string, ThemeDefinition>(AllThemeItems.ToDictionary(theme => theme.Id, StringComparer.Ordinal));

    private static readonly HashSet<string> AccentSet = new(AccentItems, StringComparer.Ordinal);

    /// <summary>Gets the seven built-in desktop themes in stable picker order.</summary>
    public static IReadOnlyList<ThemeDefinition> DesktopThemes => DesktopThemeItems;

    /// <summary>Gets the two built-in mobile themes in stable picker order.</summary>
    public static IReadOnlyList<ThemeDefinition> MobileThemes => MobileThemeItems;

    /// <summary>Gets every built-in theme, with desktop themes followed by mobile themes.</summary>
    public static IReadOnlyList<ThemeDefinition> AllThemes => AllThemeItems;

    /// <summary>Gets the supported accent identifiers in stable picker order.</summary>
    public static IReadOnlyList<string> AccentIds => AccentItems;

    /// <summary>Gets the stable theme list for a shell form factor.</summary>
    /// <param name="platform">Requested shell form factor.</param>
    /// <returns>The immutable ordered list for the requested form factor.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="platform"/> is not a defined value.</exception>
    public static IReadOnlyList<ThemeDefinition> GetThemes(ThemePlatform platform) => platform switch
    {
        ThemePlatform.Desktop => DesktopThemeItems,
        ThemePlatform.Mobile => MobileThemeItems,
        _ => throw new ArgumentOutOfRangeException(nameof(platform))
    };

    /// <summary>Attempts to resolve a built-in theme by its exact ordinal identifier.</summary>
    /// <param name="themeId">Stable theme identifier.</param>
    /// <param name="theme">Resolved definition when found; otherwise <see langword="null"/>.</param>
    /// <returns><see langword="true"/> when the identifier belongs to a built-in theme.</returns>
    public static bool TryGet(string? themeId, [NotNullWhen(true)] out ThemeDefinition? theme)
    {
        if (themeId is null)
        {
            theme = null;
            return false;
        }

        return ThemesById.TryGetValue(themeId, out theme);
    }

    /// <summary>Resolves a built-in theme by its exact ordinal identifier.</summary>
    /// <param name="themeId">Stable theme identifier.</param>
    /// <returns>The matching built-in definition.</returns>
    /// <exception cref="KeyNotFoundException">No built-in theme has the requested identifier.</exception>
    public static ThemeDefinition Get(string themeId) => TryGet(themeId, out ThemeDefinition? theme)
        ? theme
        : throw new KeyNotFoundException($"Unknown theme ID '{themeId}'.");

    /// <summary>Determines whether an exact identifier belongs to the bounded accent set.</summary>
    /// <param name="accentId">Accent identifier to inspect.</param>
    /// <returns><see langword="true"/> for green, cyan, or purple.</returns>
    public static bool IsKnownAccent(string? accentId) => accentId is not null && AccentSet.Contains(accentId);
}
