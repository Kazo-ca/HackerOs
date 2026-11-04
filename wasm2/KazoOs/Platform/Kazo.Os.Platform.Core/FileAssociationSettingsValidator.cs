using System.Text.Json;
using Kazo.Os.Simulation.Abstractions;

namespace Kazo.Os.Platform.Core;

/// <summary>
/// Validates the human-editable file-association settings document.
/// </summary>
public sealed class FileAssociationSettingsValidator : ISettingsDocumentValidator
{
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
                return ["file-associations.root-object-required"];
            }

            if (!root.TryGetProperty("schemaVersion", out JsonElement schemaVersion)
                || schemaVersion.ValueKind != JsonValueKind.Number
                || !schemaVersion.TryGetInt32(out int version)
                || version != 1)
            {
                errors.Add("file-associations.schema-version-invalid");
            }

            if (!root.TryGetProperty("associations", out JsonElement associations)
                || associations.ValueKind != JsonValueKind.Array)
            {
                errors.Add("file-associations.array-required");
                return errors;
            }

            foreach (JsonElement association in associations.EnumerateArray())
            {
                ValidateAssociation(association, errors);
            }
        }
        catch (JsonException)
        {
            errors.Add("settings.json-invalid");
        }

        return errors;
    }

    private static void ValidateAssociation(JsonElement association, List<string> errors)
    {
        if (association.ValueKind != JsonValueKind.Object)
        {
            errors.Add("file-associations.entry-object-required");
            return;
        }

        bool hasExtension = association.TryGetProperty("extension", out JsonElement extension)
            && extension.ValueKind == JsonValueKind.String
            && extension.GetString() is string extensionValue
            && extensionValue.StartsWith(".", StringComparison.Ordinal)
            && extensionValue.Length > 1;
        bool hasMediaType = association.TryGetProperty("mediaType", out JsonElement mediaType)
            && mediaType.ValueKind == JsonValueKind.String
            && !string.IsNullOrWhiteSpace(mediaType.GetString());

        if (!hasExtension && !hasMediaType)
        {
            errors.Add("file-associations.type-required");
        }

        if (!association.TryGetProperty("appId", out JsonElement appId)
            || appId.ValueKind != JsonValueKind.String
            || string.IsNullOrWhiteSpace(appId.GetString()))
        {
            errors.Add("file-associations.app-id-required");
        }

        if (!association.TryGetProperty("actions", out JsonElement actions)
            || actions.ValueKind != JsonValueKind.Array
            || actions.GetArrayLength() == 0)
        {
            errors.Add("file-associations.actions-required");
        }
    }
}