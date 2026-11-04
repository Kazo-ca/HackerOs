using Kazo.Os.App.Abstractions;

namespace Kazo.Os.Simulation.Abstractions.Settings;

/// <summary>
/// Identifies the typed value kind of one declared setting field.
/// </summary>
public enum SettingValueType
{
    /// <summary>An arbitrary UTF-8 string value.</summary>
    String = 1,

    /// <summary>A 64-bit signed integer value.</summary>
    Integer = 2,

    /// <summary>A boolean <c>true</c>/<c>false</c> value.</summary>
    Boolean = 3,

    /// <summary>A string constrained to one of a declared closed set of values.</summary>
    Enum = 4
}

/// <summary>
/// Identifies the sensitivity class of one declared setting field per ADR 0011.
/// </summary>
public enum SettingSensitivity
{
    /// <summary>Safe in projections, diagnostics, export, and eligible sync.</summary>
    Public = 1,

    /// <summary>Visible to the owning authorized user/app but redacted from ordinary logs and diagnostics.</summary>
    Private = 2,

    /// <summary>Projection contains only an opaque reference or fixed redacted marker.</summary>
    SecretReference = 3,

    /// <summary>Excluded from filesystem projection, export, and sync.</summary>
    Restricted = 4
}

/// <summary>
/// Declares one schema-owned setting field: its key, type, default, and sensitivity.
/// </summary>
/// <param name="Key">Stable, case-sensitive key understood by the settings service.</param>
/// <param name="ValueType">Typed value kind enforced during validation.</param>
/// <param name="DefaultValue">Default serialized value used when a document omits the key.</param>
/// <param name="Sensitivity">Redaction and export/sync sensitivity class.</param>
/// <param name="AllowedValues">Closed set of accepted values; required when <paramref name="ValueType"/> is <see cref="SettingValueType.Enum"/>.</param>
/// <param name="Group">Optional `[GroupName]` section the key belongs to, or <see langword="null"/> for the document root.</param>
public sealed record SettingFieldDeclaration(
    string Key,
    SettingValueType ValueType,
    string DefaultValue,
    SettingSensitivity Sensitivity,
    IReadOnlyList<string>? AllowedValues = null,
    string? Group = null)
{
    /// <summary>Gets the fully qualified `[Group]key` identity used for lookup and duplicate detection.</summary>
    public string QualifiedKey => Group is null ? Key : $"{Group}.{Key}";
}

/// <summary>
/// Declares the complete typed schema for one canonical settings document.
/// </summary>
public sealed class SettingsSchema
{
    private readonly Dictionary<string, SettingFieldDeclaration> _fieldsByQualifiedKey;

    /// <summary>Initializes a validated schema from its declared fields.</summary>
    /// <param name="schemaVersion">Current migration version written into every document.</param>
    /// <param name="fields">Declared setting fields; keys must be unique per group.</param>
    /// <param name="migrationIds">Ordered migration identifiers accepted from an older schema version.</param>
    /// <exception cref="ArgumentException">A field is invalid, duplicated, or an enum field omits allowed values.</exception>
    public SettingsSchema(
        int schemaVersion,
        IEnumerable<SettingFieldDeclaration> fields,
        IEnumerable<string>? migrationIds = null)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(schemaVersion, 1);
        ArgumentNullException.ThrowIfNull(fields);

        SettingFieldDeclaration[] copied = fields.ToArray();
        _fieldsByQualifiedKey = new Dictionary<string, SettingFieldDeclaration>(StringComparer.Ordinal);
        foreach (SettingFieldDeclaration field in copied)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(field.Key);
            if (string.Equals(field.Group, LayeredConfigResolver.LockedSectionName, StringComparison.Ordinal))
            {
                throw new ArgumentException(
                    $"Setting '{field.QualifiedKey}' uses the reserved group name '{LayeredConfigResolver.LockedSectionName}' (ADR 0044).",
                    nameof(fields));
            }

            if (field.ValueType == SettingValueType.Enum
                && (field.AllowedValues is null || field.AllowedValues.Count == 0))
            {
                throw new ArgumentException(
                    $"Enum setting '{field.QualifiedKey}' must declare at least one allowed value.",
                    nameof(fields));
            }

            if (!_fieldsByQualifiedKey.TryAdd(field.QualifiedKey, field))
            {
                throw new ArgumentException(
                    $"Setting '{field.QualifiedKey}' is declared more than once.",
                    nameof(fields));
            }
        }

        SchemaVersion = schemaVersion;
        MigrationIds = migrationIds?.ToArray() ?? [];
    }

    /// <summary>
    /// Converts a manifest-declared settings schema (<see cref="AppManifest.Settings"/>) into the
    /// engine's schema. The manifest's per-field scope is not part of the file model of ADR 0044
    /// and is ignored here.
    /// </summary>
    /// <param name="manifest">Validated manifest settings declaration.</param>
    /// <returns>The equivalent schema.</returns>
    public static SettingsSchema FromManifest(AppSettingsSchemaManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        return new SettingsSchema(
            manifest.SchemaVersion,
            manifest.Fields.Select(static field => new SettingFieldDeclaration(
                field.Key,
                (SettingValueType)(int)field.ValueType,
                field.DefaultValue,
                (SettingSensitivity)(int)field.Sensitivity,
                field.AllowedValues,
                field.Group)),
            manifest.MigrationIds);
    }

    /// <summary>Gets the current schema/migration version written into every document.</summary>
    public int SchemaVersion { get; }

    /// <summary>Gets every declared setting field keyed by its qualified `[Group]key` identity.</summary>
    public IReadOnlyDictionary<string, SettingFieldDeclaration> Fields => _fieldsByQualifiedKey;

    /// <summary>Gets ordered migration identifiers accepted from an older schema version.</summary>
    public IReadOnlyList<string> MigrationIds { get; }

    /// <summary>
    /// Validates a parsed document's root and grouped values against every declared field.
    /// </summary>
    /// <param name="values">Parsed key/value pairs keyed by their qualified `[Group]key` identity.</param>
    /// <returns>Every schema violation in deterministic declaration order; empty when the document is valid.</returns>
    public IReadOnlyList<string> Validate(IReadOnlyDictionary<string, string> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        List<string> errors = [];

        foreach (string qualifiedKey in values.Keys)
        {
            if (qualifiedKey == "schemaVersion")
            {
                // Reserved document metadata key written by the serializer; not a data field.
                continue;
            }

            if (!_fieldsByQualifiedKey.ContainsKey(qualifiedKey))
            {
                errors.Add($"settings.unknown-key:{qualifiedKey}");
            }
        }

        foreach (SettingFieldDeclaration field in _fieldsByQualifiedKey.Values)
        {
            string value = values.TryGetValue(field.QualifiedKey, out string? provided)
                ? provided
                : field.DefaultValue;

            switch (field.ValueType)
            {
                case SettingValueType.Integer:
                    if (!long.TryParse(value, out _))
                    {
                        errors.Add($"settings.invalid-integer:{field.QualifiedKey}");
                    }

                    break;
                case SettingValueType.Boolean:
                    if (value is not ("true" or "false"))
                    {
                        errors.Add($"settings.invalid-boolean:{field.QualifiedKey}");
                    }

                    break;
                case SettingValueType.Enum:
                    if (field.AllowedValues is null || !field.AllowedValues.Contains(value, StringComparer.Ordinal))
                    {
                        errors.Add($"settings.invalid-enum-value:{field.QualifiedKey}");
                    }

                    break;
                case SettingValueType.String:
                default:
                    break;
            }
        }

        return errors;
    }
}
