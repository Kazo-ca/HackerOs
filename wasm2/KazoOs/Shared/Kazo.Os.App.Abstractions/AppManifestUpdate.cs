namespace Kazo.Os.App.Abstractions;

/// <summary>
/// Declares data migration identifiers and package upgrade rules applied when this version
/// replaces an older installed version of the same application.
/// </summary>
/// <param name="MigrationIds">
/// Ordered data migration identifiers this version can apply, matching identifiers previously
/// declared by <see cref="AppSettingsSchemaManifest.MigrationIds"/> or filesystem-level migrations.
/// </param>
/// <param name="MinimumUpgradableVersion">
/// Lowest installed package semantic version this version can upgrade in place; an older installed
/// version must be reinstalled instead of upgraded.
/// </param>
public sealed record UpdateManifest(
    IReadOnlyList<string> MigrationIds,
    string? MinimumUpgradableVersion = null);
