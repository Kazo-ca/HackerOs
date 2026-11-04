using System.Text.Json.Serialization;

namespace Kazo.Os.Server.Contracts.Versioning;

/// <summary>
/// Describes a single supported API version entry.
/// </summary>
/// <param name="Version">Semantic API version string (e.g., "1.0.0").</param>
/// <param name="Status">Lifecycle status: "current", "deprecated", or "sunset".</param>
/// <param name="SunsetUtc">ISO-8601 date after which the version is no longer served. Null when not sunset.</param>
/// <param name="MinPwaSchemaVersion">Minimum IndexedDB schema version that this API version understands.</param>
/// <param name="MaxPwaSchemaVersion">Maximum IndexedDB schema version this API version will accept from clients.</param>
public sealed record ApiVersionEntry(
    string Version,
    string Status,
    DateTimeOffset? SunsetUtc,
    int MinPwaSchemaVersion,
    int MaxPwaSchemaVersion);

/// <summary>
/// Response body for GET /api/version — lists all supported API versions so cached PWAs
/// can determine compatibility before performing sync or proxy operations.
/// </summary>
/// <param name="ServerVersion">The running server software version.</param>
/// <param name="CurrentApiVersion">The preferred stable API version clients should target.</param>
/// <param name="SupportedVersions">Full list of supported, deprecated, and sunset versions.</param>
/// <param name="MinCompatiblePwaSchema">Lowest schema version this server will serve at all.</param>
public sealed record ApiVersionResponse(
    string ServerVersion,
    string CurrentApiVersion,
    IReadOnlyList<ApiVersionEntry> SupportedVersions,
    int MinCompatiblePwaSchema);

/// <summary>
/// Request body for POST /api/version/check — clients submit their schema version to receive
/// a compatibility decision before initiating sync or proxy work.
/// </summary>
/// <param name="ClientPwaSchemaVersion">The IndexedDB schema version of the requesting PWA.</param>
/// <param name="DesiredApiVersion">The API version the client intends to use.</param>
public sealed record CompatibilityCheckRequest(
    int ClientPwaSchemaVersion,
    string DesiredApiVersion);

/// <summary>
/// Result of a compatibility check.
/// </summary>
/// <param name="Compatible">True when the client may proceed with the desired API version.</param>
/// <param name="Reason">Human-readable reason, populated when Compatible is false.</param>
/// <param name="UpgradeRequired">True when the client PWA schema is below the server minimum.</param>
/// <param name="VersionSunset">True when the desired API version has been sunset.</param>
public sealed record CompatibilityCheckResponse(
    bool Compatible,
    string? Reason,
    bool UpgradeRequired,
    bool VersionSunset);

/// <summary>
/// JSON serialization context for all versioning contracts.
/// Source-generated to support trimming and AOT compilation.
/// </summary>
[JsonSerializable(typeof(ApiVersionResponse))]
[JsonSerializable(typeof(ApiVersionEntry))]
[JsonSerializable(typeof(CompatibilityCheckRequest))]
[JsonSerializable(typeof(CompatibilityCheckResponse))]
[JsonSerializable(typeof(IReadOnlyList<ApiVersionEntry>))]
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    WriteIndented = false)]
public sealed partial class VersioningContractsJsonContext : JsonSerializerContext { }
