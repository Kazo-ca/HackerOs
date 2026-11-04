using System.Text.Json;
using Kazo.Os.Simulation.Abstractions;
using Kazo.Os.Theming.Abstractions;

namespace Kazo.Os.Platform.Core.Appearance;

/// <summary>Validates the human-editable desktop-appearance settings document.</summary>
public sealed class AppearanceSettingsValidator : ISettingsDocumentValidator
{
    private static readonly HashSet<string> VersionOneProperties = new(
        ["schemaVersion", "accent", "animationsEnabled"],
        StringComparer.Ordinal);

    private static readonly HashSet<string> VersionTwoProperties = new(
        ["schemaVersion", "desktopThemeId", "mobileThemeId", "accent", "animationsEnabled"],
        StringComparer.Ordinal);

    /// <inheritdoc />
    public IReadOnlyList<string> Validate(string content)
    {
        List<string> errors = [];

        try
        {
            using JsonDocument document = JsonDocument.Parse(content);
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return ["appearance.root-object-required"];
            }

            int? version = !root.TryGetProperty("schemaVersion", out JsonElement schemaVersion)
                || schemaVersion.ValueKind != JsonValueKind.Number
                || !schemaVersion.TryGetInt32(out int parsedVersion)
                    ? null
                    : parsedVersion;

            if (version is not (1 or 2))
            {
                errors.Add("appearance.schema-version-invalid");
            }

            if (version is 1 or 2)
            {
                ValidatePropertySet(root, version == 1 ? VersionOneProperties : VersionTwoProperties, errors);
            }

            if (version == 2)
            {
                ValidateTheme(root, "desktopThemeId", ThemePlatform.Desktop, "appearance.desktop-theme-invalid", errors);
                ValidateTheme(root, "mobileThemeId", ThemePlatform.Mobile, "appearance.mobile-theme-invalid", errors);
            }

            if (!root.TryGetProperty("accent", out JsonElement accent)
                || accent.ValueKind != JsonValueKind.String
                || accent.GetString() is not string accentValue
                || !ThemeCatalog.IsKnownAccent(accentValue))
            {
                errors.Add("appearance.accent-invalid");
            }

            if (!root.TryGetProperty("animationsEnabled", out JsonElement animationsEnabled)
                || animationsEnabled.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            {
                errors.Add("appearance.animations-enabled-invalid");
            }
        }
        catch (JsonException)
        {
            errors.Add("settings.json-invalid");
        }

        return errors;
    }

    private static void ValidatePropertySet(
        JsonElement root,
        IReadOnlySet<string> allowedProperties,
        List<string> errors)
    {
        HashSet<string> seen = new(StringComparer.Ordinal);
        foreach (JsonProperty property in root.EnumerateObject())
        {
            if (!seen.Add(property.Name))
            {
                errors.Add("appearance.property-duplicate");
            }

            if (!allowedProperties.Contains(property.Name))
            {
                errors.Add("appearance.property-unknown");
            }
        }
    }

    private static void ValidateTheme(
        JsonElement root,
        string propertyName,
        ThemePlatform requiredPlatform,
        string errorCode,
        List<string> errors)
    {
        if (!root.TryGetProperty(propertyName, out JsonElement element)
            || element.ValueKind != JsonValueKind.String
            || !ThemeCatalog.TryGet(element.GetString(), out ThemeDefinition? theme)
            || theme.Platform != requiredPlatform)
        {
            errors.Add(errorCode);
        }
    }
}
