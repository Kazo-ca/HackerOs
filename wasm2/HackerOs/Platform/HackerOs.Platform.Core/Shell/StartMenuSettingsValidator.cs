using System.Text.Json;
using HackerOs.Simulation.Abstractions;

namespace HackerOs.Platform.Core.Shell;

/// <summary>Strictly validates the aggregated, per-user start-menu preferences document.</summary>
public sealed class StartMenuSettingsValidator : ISettingsDocumentValidator
{
    private static readonly HashSet<string> RootProperties = new(
        ["schemaVersion", "profiles"],
        StringComparer.Ordinal);

    private static readonly HashSet<string> ProfileProperties = new(
        ["pinnedAppIds", StartMenuSettingsDocuments.DefaultPinsAppliedProperty],
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
                return ["start-menu.root-object-required"];
            }

            ValidateExactProperties(root, RootProperties, "start-menu.root-property", errors);

            if (!root.TryGetProperty("schemaVersion", out JsonElement schemaVersion)
                || schemaVersion.ValueKind != JsonValueKind.Number
                || !schemaVersion.TryGetInt32(out int version)
                || version != 1)
            {
                errors.Add("start-menu.schema-version-invalid");
            }

            if (!root.TryGetProperty("profiles", out JsonElement profiles)
                || profiles.ValueKind != JsonValueKind.Object)
            {
                errors.Add("start-menu.profiles-object-required");
                return errors;
            }

            HashSet<string> seenUserIds = new(StringComparer.Ordinal);
            foreach (JsonProperty profile in profiles.EnumerateObject())
            {
                if (!seenUserIds.Add(profile.Name))
                {
                    errors.Add("start-menu.user-id-duplicate");
                }

                if (!IsCanonicalLocalUserId(profile.Name))
                {
                    errors.Add("start-menu.user-id-invalid");
                }

                ValidateProfile(profile.Value, errors);
            }
        }
        catch (JsonException)
        {
            errors.Add("settings.json-invalid");
        }

        return errors;
    }

    private static void ValidateProfile(JsonElement profile, List<string> errors)
    {
        if (profile.ValueKind != JsonValueKind.Object)
        {
            errors.Add("start-menu.profile-object-required");
            return;
        }

        ValidateExactProperties(profile, ProfileProperties, "start-menu.profile-property", errors);
        if (!profile.TryGetProperty("pinnedAppIds", out JsonElement pinnedAppIds)
            || pinnedAppIds.ValueKind != JsonValueKind.Array)
        {
            errors.Add("start-menu.pinned-app-ids-array-required");
            return;
        }

        if (pinnedAppIds.GetArrayLength() > StartMenuSettingsDocuments.MaximumPinnedAppCount)
        {
            errors.Add("start-menu.pinned-app-ids-limit-exceeded");
        }

        // ADR 0212 §6: the default pins already added once; optional, an array of app IDs.
        if (profile.TryGetProperty(StartMenuSettingsDocuments.DefaultPinsAppliedProperty, out JsonElement applied)
            && (applied.ValueKind != JsonValueKind.Array
                || applied.GetArrayLength() > StartMenuSettingsDocuments.MaximumPinnedAppCount
                || applied.EnumerateArray().Any(static item => item.ValueKind != JsonValueKind.String || !StartMenuAppIdSyntax.IsValid(item.GetString()))))
        {
            errors.Add("start-menu.default-pins-applied-invalid");
        }

        HashSet<string> seenAppIds = new(StringComparer.Ordinal);
        foreach (JsonElement appIdElement in pinnedAppIds.EnumerateArray())
        {
            if (appIdElement.ValueKind != JsonValueKind.String
                || !StartMenuAppIdSyntax.IsValid(appIdElement.GetString()))
            {
                errors.Add("start-menu.app-id-invalid");
                continue;
            }

            if (!seenAppIds.Add(appIdElement.GetString()!))
            {
                errors.Add("start-menu.app-id-duplicate");
            }
        }
    }

    private static void ValidateExactProperties(
        JsonElement element,
        IReadOnlySet<string> allowed,
        string errorPrefix,
        List<string> errors)
    {
        HashSet<string> seen = new(StringComparer.Ordinal);
        foreach (JsonProperty property in element.EnumerateObject())
        {
            if (!seen.Add(property.Name))
            {
                errors.Add($"{errorPrefix}-duplicate");
            }

            if (!allowed.Contains(property.Name))
            {
                errors.Add($"{errorPrefix}-unknown");
            }
        }
    }

    private static bool IsCanonicalLocalUserId(string value) =>
        value.Length == 32
        && string.Equals(value, value.ToLowerInvariant(), StringComparison.Ordinal)
        && Guid.TryParseExact(value, "N", out Guid parsed)
        && parsed != Guid.Empty;
}
