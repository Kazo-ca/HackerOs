using System.Text;
using System.Text.Json;
using Kazo.Os.Theming.Abstractions;

namespace Kazo.Os.Platform.Core.Appearance;

/// <summary>Reads legacy and current appearance documents and writes canonical schema-version-2 JSON.</summary>
public static class AppearanceSettingsCodec
{
    /// <summary>Attempts to decode a validated version-1 or version-2 appearance document.</summary>
    /// <param name="content">Complete JSON document content.</param>
    /// <param name="preferences">
    /// Decoded preferences. A version-1 document receives the default desktop and mobile themes
    /// while preserving its accent and animation choices.
    /// </param>
    /// <returns><see langword="true"/> when the document is valid and supported.</returns>
    public static bool TryDecode(string content, out ThemePreferences preferences)
    {
        preferences = ThemePreferences.Default;
        if (new AppearanceSettingsValidator().Validate(content).Count != 0)
        {
            return false;
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(content);
            JsonElement root = document.RootElement;
            int version = root.GetProperty("schemaVersion").GetInt32();
            string accentId = root.GetProperty("accent").GetString()!;
            bool animationsEnabled = root.GetProperty("animationsEnabled").GetBoolean();

            preferences = version == 1
                ? ThemePreferences.Default with
                {
                    AccentId = accentId,
                    AnimationsEnabled = animationsEnabled
                }
                : new ThemePreferences(
                    root.GetProperty("desktopThemeId").GetString()!,
                    root.GetProperty("mobileThemeId").GetString()!,
                    accentId,
                    animationsEnabled);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>Encodes preferences as deterministic schema-version-2 JSON.</summary>
    /// <param name="preferences">Complete preferences to encode.</param>
    /// <returns>Canonical compact JSON suitable for the settings document service.</returns>
    /// <exception cref="ArgumentException">A theme or accent ID is unknown or belongs to the wrong platform.</exception>
    public static string Encode(ThemePreferences preferences)
    {
        ArgumentNullException.ThrowIfNull(preferences);
        ValidateTheme(preferences.DesktopThemeId, ThemePlatform.Desktop, nameof(preferences));
        ValidateTheme(preferences.MobileThemeId, ThemePlatform.Mobile, nameof(preferences));

        if (!ThemeCatalog.IsKnownAccent(preferences.AccentId))
        {
            throw new ArgumentException($"Unknown accent ID '{preferences.AccentId}'.", nameof(preferences));
        }

        using MemoryStream stream = new();
        using (Utf8JsonWriter writer = new(stream))
        {
            writer.WriteStartObject();
            writer.WriteNumber("schemaVersion", 2);
            writer.WriteString("desktopThemeId", preferences.DesktopThemeId);
            writer.WriteString("mobileThemeId", preferences.MobileThemeId);
            writer.WriteString("accent", preferences.AccentId);
            writer.WriteBoolean("animationsEnabled", preferences.AnimationsEnabled);
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static void ValidateTheme(string themeId, ThemePlatform platform, string parameterName)
    {
        if (!ThemeCatalog.TryGet(themeId, out ThemeDefinition? theme) || theme.Platform != platform)
        {
            throw new ArgumentException(
                $"Theme ID '{themeId}' is not a known {platform.ToString().ToLowerInvariant()} theme.",
                parameterName);
        }
    }
}
