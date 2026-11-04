using System.Text.Json.Serialization;

namespace Kazo.Os.App.Abstractions;

/// <summary>
/// Identifies the logical kind of one declared package asset.
/// </summary>
public enum AssetKind
{
    /// <summary>An image or icon asset.</summary>
    [JsonStringEnumMemberName("image")]
    Image = 1,

    /// <summary>A collocated component stylesheet.</summary>
    [JsonStringEnumMemberName("css")]
    Css = 2,

    /// <summary>A collocated JavaScript interop module.</summary>
    [JsonStringEnumMemberName("javaScriptModule")]
    JavaScriptModule = 3,

    /// <summary>A localization resource file referenced by <see cref="LocalizationManifest"/>.</summary>
    [JsonStringEnumMemberName("localization")]
    Localization = 4,

    /// <summary>An approved static data asset with no other declared kind.</summary>
    [JsonStringEnumMemberName("data")]
    Data = 5
}

/// <summary>
/// Declares one package-relative static asset owned by the application.
/// </summary>
/// <param name="Path">
/// Package-relative path using <c>/</c> separators. Absolute paths, <c>.</c>/<c>..</c> segments,
/// backslashes, query strings, fragments, and external URLs are rejected by
/// <see cref="AppManifestValidator"/>.
/// </param>
/// <param name="Kind">Logical asset kind.</param>
/// <param name="Sha256">Lowercase hexadecimal SHA-256 integrity hash of the asset's bytes.</param>
/// <param name="Role">Optional logical role, e.g. <c>icon-32</c>, used by other manifest sections to reference this asset.</param>
public sealed record AssetManifest(string Path, AssetKind Kind, string Sha256, string? Role = null);
