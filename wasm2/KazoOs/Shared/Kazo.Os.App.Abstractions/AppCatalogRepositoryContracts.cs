namespace Kazo.Os.App.Abstractions;

/// <summary>Represents one durable app manifest snapshot and its local enablement state.</summary>
public sealed record PersistedAppCatalogEntry(AppManifest Manifest, bool IsEnabled);

/// <summary>Persists build-selected app manifests and local enablement state.</summary>
public interface IPersistentAppCatalogRepository
{
    /// <summary>
    /// Reconciles durable records with authoritative build-selected manifests while preserving
    /// their existing enablement and disabling retained records absent from the current build.
    /// </summary>
    ValueTask<IReadOnlyList<PersistedAppCatalogEntry>> ReconcileAsync(
        IEnumerable<AppManifest> selectedManifests,
        CancellationToken cancellationToken = default);

    /// <summary>Changes enablement for one existing durable catalog record.</summary>
    ValueTask<bool> SetEnabledAsync(
        string appId,
        bool enabled,
        CancellationToken cancellationToken = default);

    /// <summary>Loads all durable records in ordinal app-ID order.</summary>
    ValueTask<IReadOnlyList<PersistedAppCatalogEntry>> ReadAllAsync(
        CancellationToken cancellationToken = default);
}