using Kazo.Os.Theming.Abstractions;

namespace Kazo.Os.Theming.Blazor;

/// <summary>
/// Holds the bounded strings emitted as data attributes by theme-rendering components. Keeping
/// this validation in one place prevents previews and the live shell from drifting apart.
/// </summary>
internal sealed record ThemeRenderState(
    ThemeDefinition Theme,
    string FormFactorId,
    string AccentId,
    string MotionId,
    string ContrastId = "standard",
    string TextScaleId = "100")
{
    /// <summary>The text size factors of ADR 0105 and their attribute value (a percentage).</summary>
    private static readonly (double Scale, string Id)[] TextScales = [(1.0, "100"), (1.25, "125"), (1.5, "150"), (2.0, "200")];

    /// <summary>Validates a theme request and converts it to safe, catalog-backed attribute values.</summary>
    internal static ThemeRenderState Create(
        string? themeId,
        ThemePlatform? requiredPlatform,
        string? accentId,
        bool animationsEnabled,
        bool highContrast = false,
        double textScale = 1.0)
    {
        if (!ThemeCatalog.TryGet(themeId, out ThemeDefinition? theme))
        {
            throw new ArgumentException($"Unknown theme ID '{themeId}'.", nameof(themeId));
        }

        ThemeDefinition resolvedTheme = theme;

        if (requiredPlatform is { } platform && resolvedTheme.Platform != platform)
        {
            throw new ArgumentException(
                $"Theme '{resolvedTheme.Id}' targets {resolvedTheme.Platform}, not the requested {platform} form factor.",
                nameof(themeId));
        }

        if (!ThemeCatalog.IsKnownAccent(accentId))
        {
            throw new ArgumentException($"Unknown accent ID '{accentId}'.", nameof(accentId));
        }

        return new ThemeRenderState(
            resolvedTheme,
            resolvedTheme.Platform == ThemePlatform.Desktop ? "desktop" : "mobile",
            accentId!,
            animationsEnabled ? "enabled" : "reduced",
            highContrast ? "high" : "standard",
            TextScaleIdOf(textScale));
    }

    /// <summary>Maps a factor to its attribute value; anything not in ADR 0105's list renders at 100 %.</summary>
    private static string TextScaleIdOf(double textScale)
    {
        foreach ((double scale, string id) in TextScales)
        {
            if (scale == textScale)
            {
                return id;
            }
        }

        return "100";
    }
}
