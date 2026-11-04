using Kazo.Os.App.Abstractions;

namespace Kazo.Os.Simulation.Abstractions.Settings;

/// <summary>
/// Identifies one canonical settings ownership scope defined by ADR 0011.
/// </summary>
public enum SettingsScope
{
    /// <summary>Local-only preferences owned by one app for one user.</summary>
    AppUser = 1,

    /// <summary>Local-only preferences owned by one app for one device installation.</summary>
    AppDevice = 2,

    /// <summary>Sync-eligible preferences owned by one app for one roaming user.</summary>
    AppRoamingUser = 3,

    /// <summary>Protected OS-global/administrator policy.</summary>
    OsAdmin = 4,

    /// <summary>Local-only preferences owned by one app, user, and device installation.</summary>
    AppUserDevice = 5
}

/// <summary>
/// Identifies one canonical settings document independently from its filesystem projection path.
/// </summary>
/// <remarks>
/// This structured key is the repository identity and future sync partition. Filesystem
/// paths are deterministic projections derived from the key and are never the only source
/// of scope or ownership authority.
/// </remarks>
public readonly record struct SettingsDocumentKey
{
    /// <summary>Default document ID used when an app owns exactly one settings document per scope.</summary>
    public const string DefaultDocumentId = "settings";

    private SettingsDocumentKey(
        SettingsScope scope,
        string documentId,
        string? appId,
        string? userId,
        string? installationId)
    {
        Scope = scope;
        DocumentId = documentId;
        AppId = appId;
        UserId = userId;
        InstallationId = installationId;
    }

    /// <summary>Gets the canonical ownership scope.</summary>
    public SettingsScope Scope { get; }

    /// <summary>Gets the stable document ID within its scope and owner partition.</summary>
    public string DocumentId { get; }

    /// <summary>Gets the owning app ID, or <see langword="null"/> for the OS/admin scope.</summary>
    public string? AppId { get; }

    /// <summary>Gets the owning user ID, or <see langword="null"/> when the scope is not user-partitioned.</summary>
    public string? UserId { get; }

    /// <summary>Gets the owning installation ID, or <see langword="null"/> when the scope is not device-partitioned.</summary>
    public string? InstallationId { get; }

    /// <summary>Creates a key for an app/user local preference document.</summary>
    public static SettingsDocumentKey ForAppUser(string appId, string userId, string documentId = DefaultDocumentId) =>
        new(
            SettingsScope.AppUser,
            ValidateIdentifier(documentId, nameof(documentId)),
            ValidateAppId(appId),
            ValidateIdentifier(userId, nameof(userId)),
            null);

    /// <summary>Creates a key for an app/device local preference document.</summary>
    public static SettingsDocumentKey ForAppDevice(string appId, string installationId, string documentId = DefaultDocumentId) =>
        new(
            SettingsScope.AppDevice,
            ValidateIdentifier(documentId, nameof(documentId)),
            ValidateAppId(appId),
            null,
            ValidateIdentifier(installationId, nameof(installationId)));

    /// <summary>Creates a key for an app/user/device local preference document.</summary>
    public static SettingsDocumentKey ForAppUserDevice(
        string appId,
        string userId,
        string installationId,
        string documentId = DefaultDocumentId) =>
        new(
            SettingsScope.AppUserDevice,
            ValidateIdentifier(documentId, nameof(documentId)),
            ValidateAppId(appId),
            ValidateIdentifier(userId, nameof(userId)),
            ValidateIdentifier(installationId, nameof(installationId)));

    /// <summary>Creates a key for a sync-eligible app/roaming-user preference document.</summary>
    public static SettingsDocumentKey ForAppRoamingUser(string appId, string userId, string documentId = DefaultDocumentId) =>
        new(
            SettingsScope.AppRoamingUser,
            ValidateIdentifier(documentId, nameof(documentId)),
            ValidateAppId(appId),
            ValidateIdentifier(userId, nameof(userId)),
            null);

    /// <summary>Creates a key for a protected OS-global/administrator policy document.</summary>
    public static SettingsDocumentKey ForOsAdmin(string documentId) =>
        new(
            SettingsScope.OsAdmin,
            ValidateIdentifier(documentId, nameof(documentId)),
            null,
            null,
            null);

    private static string ValidateAppId(string appId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(appId);
        return appId;
    }

    private static string ValidateIdentifier(string value, string paramName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, paramName);
        if (value is "." or ".."
            || value.Contains('/', StringComparison.Ordinal)
            || value.Contains('\\', StringComparison.Ordinal)
            || value.Any(char.IsControl))
        {
            throw new ArgumentException(
                "Settings identifiers cannot contain path separators, control characters, or be '.' or '..'.",
                paramName);
        }

        return value;
    }
}

/// <summary>
/// Derives deterministic virtual filesystem projection paths for canonical settings keys.
/// </summary>
public static class SettingsDocumentPathFactory
{
    /// <summary>Computes the deterministic projection path for one settings document key.</summary>
    /// <param name="key">Canonical settings document key.</param>
    /// <returns>The virtual filesystem path exposing the document.</returns>
    public static VirtualPath GetProjectionPath(SettingsDocumentKey key) => key.Scope switch
    {
        SettingsScope.AppUser => VirtualPath.Parse(
            $"/home/{key.UserId}/.config/apps/{key.AppId}/{key.DocumentId}.config"),
        SettingsScope.AppDevice => VirtualPath.Parse(
            $"/var/lib/kazoos/devices/{key.InstallationId}/apps/{key.AppId}/{key.DocumentId}.config"),
        SettingsScope.AppUserDevice => VirtualPath.Parse(
            $"/var/lib/kazoos/devices/{key.InstallationId}/users/{key.UserId}/apps/{key.AppId}/{key.DocumentId}.config"),
        SettingsScope.AppRoamingUser => VirtualPath.Parse(
            $"/home/{key.UserId}/.config/apps/{key.AppId}/roaming/{key.DocumentId}.config"),
        SettingsScope.OsAdmin => VirtualPath.Parse($"/etc/kazoos/{key.DocumentId}.config"),
        _ => throw new ArgumentOutOfRangeException(nameof(key), key.Scope, "Unknown settings scope.")
    };
}
