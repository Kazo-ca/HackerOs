using System.Text.Json.Serialization;

namespace Kazo.Os.App.Abstractions;

/// <summary>
/// Identifies the typed value kind of one manifest-declared setting field, mirroring
/// <c>SettingValueType</c> in <c>Kazo.Os.Simulation.Abstractions</c>.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<AppSettingValueType>))]
public enum AppSettingValueType
{
    /// <summary>An arbitrary UTF-8 string value.</summary>
    [JsonStringEnumMemberName("string")]
    String = 1,

    /// <summary>A 64-bit signed integer value.</summary>
    [JsonStringEnumMemberName("integer")]
    Integer = 2,

    /// <summary>A boolean <c>true</c>/<c>false</c> value.</summary>
    [JsonStringEnumMemberName("boolean")]
    Boolean = 3,

    /// <summary>A string constrained to one of a declared closed set of values.</summary>
    [JsonStringEnumMemberName("enum")]
    Enum = 4
}

/// <summary>
/// Identifies the sensitivity/redaction class of one manifest-declared setting field, mirroring
/// <c>SettingSensitivity</c> in <c>Kazo.Os.Simulation.Abstractions</c>.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<AppSettingSensitivity>))]
public enum AppSettingSensitivity
{
    /// <summary>Safe in projections, diagnostics, export, and eligible sync.</summary>
    [JsonStringEnumMemberName("public")]
    Public = 1,

    /// <summary>Visible to the owning authorized user/app but redacted from ordinary logs and diagnostics.</summary>
    [JsonStringEnumMemberName("private")]
    Private = 2,

    /// <summary>Projection contains only an opaque reference or fixed redacted marker.</summary>
    [JsonStringEnumMemberName("secretReference")]
    SecretReference = 3,

    /// <summary>Excluded from filesystem projection, export, and sync.</summary>
    [JsonStringEnumMemberName("restricted")]
    Restricted = 4
}

/// <summary>
/// Identifies the canonical settings ownership scope a manifest may declare, mirroring
/// <c>SettingsScope</c> in <c>Kazo.Os.Simulation.Abstractions</c>. Declaring a scope here grants
/// nothing: settings are files since ADR 0044, so access is decided by file rights alone.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<AppSettingScope>))]
public enum AppSettingScope
{
    /// <summary>Local-only preferences owned by one app for one user.</summary>
    [JsonStringEnumMemberName("appUser")]
    AppUser = 1,

    /// <summary>Local-only preferences owned by one app for one device installation.</summary>
    [JsonStringEnumMemberName("appDevice")]
    AppDevice = 2,

    /// <summary>Local-only settings partitioned by app, user, and device installation.</summary>
    [JsonStringEnumMemberName("appUserDevice")]
    AppUserDevice = 5,

    /// <summary>Sync-eligible preferences owned by one app for one roaming user.</summary>
    [JsonStringEnumMemberName("appRoamingUser")]
    AppRoamingUser = 3,

    /// <summary>Protected OS-global/administrator policy.</summary>
    [JsonStringEnumMemberName("osAdmin")]
    OsAdmin = 4
}

/// <summary>
/// Declares one setting field an application persists through the canonical settings service.
/// </summary>
/// <param name="Key">Stable, case-sensitive key, optionally qualified with a <c>Group</c>.</param>
/// <param name="ValueType">Typed value kind enforced during validation.</param>
/// <param name="DefaultValue">Default serialized value used when a document omits the key.</param>
/// <param name="Scope">Requested settings ownership scope.</param>
/// <param name="Sensitivity">Redaction and export/sync sensitivity class.</param>
/// <param name="AllowedValues">Closed set of accepted values; required when <paramref name="ValueType"/> is <see cref="AppSettingValueType.Enum"/>.</param>
/// <param name="Group">Optional `[GroupName]` section the key belongs to, or <see langword="null"/> for the document root.</param>
public sealed record AppSettingFieldManifest(
    string Key,
    AppSettingValueType ValueType,
    string DefaultValue,
    AppSettingScope Scope,
    AppSettingSensitivity Sensitivity,
    IReadOnlyList<string>? AllowedValues = null,
    string? Group = null);

/// <summary>
/// Declares the complete typed settings schema an application persists.
/// </summary>
/// <param name="SchemaVersion">Current migration version written into every document.</param>
/// <param name="Fields">Declared setting fields.</param>
/// <param name="MigrationIds">Ordered migration identifiers accepted from an older schema version.</param>
public sealed record AppSettingsSchemaManifest(
    int SchemaVersion,
    IReadOnlyList<AppSettingFieldManifest> Fields,
    IReadOnlyList<string>? MigrationIds = null);
