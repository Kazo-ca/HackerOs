namespace Kazo.Os.App.Abstractions;

/// <summary>
/// Identifies a <see cref="AppKind.Service"/> app's live, user-toggleable start behavior --
/// analogous to a systemd unit's enabled/disabled state. Distinct from
/// <see cref="AppManifest.AutoStart"/>, which is only the package's shipped preset used to seed
/// this value the first time it is read; the effective mode itself is stored outside the
/// manifest, per session-scoped user, and can change at runtime.
/// </summary>
public enum ServiceStartMode
{
    /// <summary>The service starts automatically every session (`StartAllServicesAsync`).</summary>
    Automatic,

    /// <summary>The service never starts automatically, but can still be started explicitly.</summary>
    Manual,

    /// <summary>The service cannot be started at all, automatically or explicitly, until re-enabled.</summary>
    Disabled
}
